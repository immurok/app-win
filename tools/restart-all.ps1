#requires -Version 5.1
<#
.SYNOPSIS
  一键重建并重启 immurok。可整体，也可单独只处理 server 或 client。
  流程：关闭目标进程 → 重新编译目标 → 重启 server →（若同时含 client）等待 N 秒 → 启动 client。

.DESCRIPTION
  两种运行模式：
    · 默认（开发进程模式）：直接以进程方式跑 ImmurokService.exe / ImmurokClient.exe，免管理员、迭代快。
      此模式下 Service 在用户会话（Session 1），锁屏解锁的 Credential Provider 走不通，
      但 BLE / 配对 / 指纹 / SSH agent 等功能都可测。
    · -Service（已安装服务模式）：sc 停服务 → 部署编译产物到安装目录 → sc 启服务，需管理员。

  执行顺序固定为「先停后编译」：正在运行的 exe 会锁定文件，不先关掉会导致 build 失败。

.PARAMETER Target
  All（默认）：server + client 都重编重启。
  Server：只重编并重启 server（不动 client）。
  Client：只重编并重启 client（不动 server）。
  说明：C++ Credential Provider 仅在 Target=All 时随 build.ps1 一起编。只改了 CP 源码时请用 All。

.PARAMETER Configuration
  Debug（默认，迭代快）或 Release。

.PARAMETER Service
  以「已安装 Windows 服务」方式停/启 server（需管理员）。省略则用开发进程模式。

.PARAMETER InstallDir
  服务模式下的安装目录，默认 %ProgramFiles%\immurok。

.PARAMETER ClientDelaySeconds
  server 起来后、启动 client 前的等待秒数（默认 5）。仅在同时启动 server 与 client 时生效。

.EXAMPLE
  .\tools\restart-all.ps1                # 全部（等价 -t all，并把 imk 加入用户 PATH）
  .\tools\restart-all.ps1 -t server      # 只重编重启 server
  .\tools\restart-all.ps1 -t client      # 只重编重启 client
  .\tools\restart-all.ps1 -t imk         # 只编译 imk，并加入用户 PATH（不涉及停/启进程）
  .\tools\restart-all.ps1 -t server -Service   # 以管理员运行，重启已安装服务
  .\tools\restart-all.ps1 -t all -Configuration Release -Service
                                         # 覆盖安装（含 Credential Provider）并重启服务。
                                         # 注意 -Configuration 默认是 Debug，而发行安装的是
                                         # Release；部署到安装目录时务必显式指定，别把 Debug
                                         # 产物盖上去。
#>
param(
    [Alias('t')]
    [ValidateSet('All','Server','Client','Imk')]
    [string]$Target = 'All',
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Debug',
    [switch]$Service,
    [string]$InstallDir = "$env:ProgramFiles\immurok",
    [int]$ClientDelaySeconds = 5
)

$ErrorActionPreference = 'Stop'
$toolsDir  = $PSScriptRoot
$root      = Split-Path $toolsDir -Parent
$tfm       = 'net10.0-windows10.0.22621.0'
$svcBin    = Join-Path $root "ImmurokService\bin\x64\$Configuration\$tfm"
$cliBin    = Join-Path $root "ImmurokClient\bin\x64\$Configuration\$tfm"
$imkBin    = Join-Path $root "ImmurokCli\bin\x64\$Configuration\$tfm"
$svcExeBin = Join-Path $svcBin 'ImmurokService.exe'
$cliExeBin = Join-Path $cliBin 'ImmurokClient.exe'

$doServer = $Target -in @('All','Server')
$doClient = $Target -in @('All','Client')

function Info($m) { Write-Host $m -ForegroundColor Cyan }
function Ok($m)   { Write-Host $m -ForegroundColor Green }
function Warn($m) { Write-Host $m -ForegroundColor Yellow }

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltinRole]::Administrator)
}

function Stop-Proc($name) {
    $ps = Get-Process -Name $name -ErrorAction SilentlyContinue
    if (-not $ps) { return }

    Info "    停止进程 $name (PID: $($ps.Id -join ', '))"
    $failed = @()
    foreach ($p in $ps) {
        try { Stop-Process -Id $p.Id -Force -ErrorAction Stop }
        catch { $failed += $p.Id }
    }
    # 这里过去是 -ErrorAction SilentlyContinue：杀不掉也当成功。
    # 结果是旧实例还活着、脚本又起了一个新的，两个 Service 抢同一个管道和同一台 BLE 设备。
    if ($failed.Count -gt 0) {
        throw "无法结束 $name（PID: $($failed -join ', ')）：权限不足或进程受保护。请以管理员运行，或先手动停掉该进程。"
    }
}

# 开发进程模式的前置检查：已安装服务在跑就不许再起一个。
# 两个 Service 并存时，指纹信号可能置在 A 进程、客户端的长轮询却挂在 B 进程上，
# 注入与解锁静默失效；而且两者 shared:true 写同一个日志文件，从日志上根本看不出是两个实例。
function Assert-NoInstalledServiceRunning {
    $svc = Get-CimInstance Win32_Service -Filter "Name='ImmurokService'" -ErrorAction SilentlyContinue
    if ($svc -and $svc.State -eq 'Running') {
        throw @"
已安装的 Windows 服务 ImmurokService 正在运行（PID $($svc.ProcessId)），开发进程模式会再起一个实例。
两个 Service 会抢同一个命名管道和同一台 BLE 设备，表现为指纹信号丢失、注入与解锁静默失效。

请二选一：
  · 已安装服务模式（管理员）: .\tools\restart-all.ps1 -t $Target -Configuration $Configuration -Service
  · 或先停掉服务（管理员）:   sc.exe stop ImmurokService
"@
    }
}

# 把开发态的 imk.exe 目录加入用户 PATH（免管理员，新开终端即可 imk），并让当前会话立即可用。
function Ensure-ImkPath {
    if (-not (Test-Path (Join-Path $imkBin 'imk.exe'))) {
        Warn "    未找到 $imkBin\imk.exe，跳过 PATH 设置（确认已编译）。"
        return
    }
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ([string]::IsNullOrEmpty($userPath) -or ($userPath -notlike "*$imkBin*")) {
        $newPath = if ([string]::IsNullOrEmpty($userPath)) { $imkBin } else { "$userPath;$imkBin" }
        [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
        Ok  "    imk 已加入用户 PATH：$imkBin（新开终端生效）。"
    } else {
        Info "    imk 目录已在用户 PATH。"
    }
    # 当前 PowerShell 会话也立即可用。
    if ($env:Path -notlike "*$imkBin*") { $env:Path = "$env:Path;$imkBin" }
}

# SCM 报告 STOPPED 时进程可能还在退出，文件句柄尚未释放；不等它退干净就复制会撞上
# 「正由另一进程使用」。今天就踩过这个。
function Wait-ProcExit($name, $timeoutSec = 30) {
    for ($i = 0; $i -lt $timeoutSec; $i++) {
        if (-not (Get-Process -Name $name -ErrorAction SilentlyContinue)) { return }
        Start-Sleep -Seconds 1
    }
    throw "$name 进程在 $timeoutSec 秒内没有退出，无法安全部署。"
}

function Deploy-InstallDir([bool]$server, [bool]$client) {
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    if ($server) {
        Info "    部署 server → $InstallDir"
        Copy-Item "$svcBin\*" $InstallDir -Recurse -Force
        $cpDll = Join-Path $root "ImmurokCredentialProvider\x64\$Configuration\ImmurokCredentialProvider.dll"
        if (Test-Path $cpDll) { Copy-Item $cpDll $InstallDir -Force }
    }
    if ($client) {
        Info "    部署 client → $InstallDir"
        Copy-Item "$cliBin\*" $InstallDir -Recurse -Force
    }
}

function Start-Server {
    if ($Service) {
        Info "    sc start ImmurokService"
        sc.exe start ImmurokService | Out-Null
        Ok  "    服务已启动（日志见 C:\ProgramData\immurok\logs\）。"
    }
    else {
        if (-not (Test-Path $svcExeBin)) { throw "未找到 $svcExeBin，请确认编译成功。" }
        $svcOut = Join-Path $toolsDir 'service.console.out.log'
        $svcErr = Join-Path $toolsDir 'service.console.err.log'
        Remove-Item $svcOut, $svcErr -Force -ErrorAction SilentlyContinue
        $p = Start-Process -FilePath $svcExeBin -WorkingDirectory $svcBin `
            -RedirectStandardOutput $svcOut -RedirectStandardError $svcErr `
            -PassThru -WindowStyle Hidden
        Ok  "    server 已启动 (PID=$($p.Id))，日志见 C:\ProgramData\immurok\logs\ 及 $svcOut"
    }
}

function Start-Client {
    if ($Service) {
        $cli = Join-Path $InstallDir 'ImmurokClient.exe'
    }
    else {
        $cli = $cliExeBin
    }
    if (-not (Test-Path $cli)) { throw "未找到 client：$cli" }

    if (Test-Admin) {
        # 经 explorer.exe 中转，让 client 落回普通完整性级别。
        # 直接 Start-Process 会继承当前的管理员令牌，client 便以管理员身份常驻——
        # 既没必要，也和它平时由 HKCU\Run 自启时的身份不一致（注入要的就是普通用户会话身份）。
        Info "    经 explorer 以普通权限启动 client ..."
        Start-Process -FilePath "$env:WINDIR\explorer.exe" -ArgumentList "`"$cli`"" | Out-Null
    }
    else {
        Start-Process -FilePath $cli -WorkingDirectory (Split-Path $cli -Parent) | Out-Null
    }
    Ok  "    client 已启动。"
}

# imk 是命令行工具，不涉及停/启进程：只重编 + 加入用户 PATH。
if ($Target -eq 'Imk') {
    Info "==> 编译 imk ($Configuration) ..."
    dotnet build "$root\ImmurokCli\ImmurokCli.csproj" -c $Configuration -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw "编译 imk 失败（退出码 $LASTEXITCODE）。" }
    Ensure-ImkPath
    Ok "==> 完成：imk 已编译并加入用户 PATH（新开终端可直接 imk）。"
    return
}

if ($Service -and -not (Test-Admin)) {
    throw "服务模式需要管理员权限。请以管理员身份重新运行 PowerShell 后再执行本脚本。"
}

Info "==> 目标: $Target  配置: $Configuration  模式: $(if($Service){'已安装服务'}else{'开发进程'})"

# ============ 1) 先关闭目标（释放文件占用，才能重编译） ============
Info "==> [1/3] 关闭目标进程 ..."
if ($doServer -and -not $Service) { Assert-NoInstalledServiceRunning }
if ($Service) {
    # 服务模式下无论目标是 server 还是 client 都要停服务：两者共用 ImmurokCommon.dll，
    # 服务在跑时部署 client 必然撞上「文件正由另一进程使用」。
    Info "    sc stop ImmurokService"
    sc.exe stop ImmurokService | Out-Null
    for ($i = 0; $i -lt 20; $i++) {
        $st = (sc.exe query ImmurokService) -join "`n"
        if ($st -match 'STOPPED' -or $st -notmatch 'ImmurokService') { break }
        Start-Sleep -Milliseconds 300
    }
    Wait-ProcExit 'ImmurokService'
}
elseif ($doServer) {
    Stop-Proc 'ImmurokService'
}
if ($doClient) { Stop-Proc 'ImmurokClient' }
Start-Sleep -Milliseconds 500

# ============ 2) 重新编译目标 ============
Info "==> [2/3] 编译 ($Configuration) ..."
if ($Target -eq 'All') {
    # 整体编译（含 C++ Credential Provider）
    & (Join-Path $root 'build.ps1') -Configuration $Configuration
}
else {
    if ($doServer) {
        dotnet build "$root\ImmurokService\ImmurokService.csproj" -c $Configuration -p:Platform=x64
        if ($LASTEXITCODE -ne 0) { throw "dotnet 编译 ImmurokService 失败（退出码 $LASTEXITCODE）。" }
    }
    if ($doClient) {
        dotnet build "$root\ImmurokClient\ImmurokClient.csproj" -c $Configuration -p:Platform=x64
        if ($LASTEXITCODE -ne 0) { throw "dotnet 编译 ImmurokClient 失败（退出码 $LASTEXITCODE）。" }
    }
}
Ok "    编译完成。"

# ============ 3) 部署并启动 ============
Info "==> [3/3] 部署并启动 ..."
# 服务模式：所有文件必须在服务仍停着的时候一次性部署完，再启动。
# 原来是「部署 server → 启动服务 → 部署 client」，服务一起来就把共用的
# ImmurokCommon.dll 加载了，紧接着部署 client 必定失败。
if ($Service) { Deploy-InstallDir -server $doServer -client $doClient }

# 服务模式下上面为了部署把服务停了，即便本次目标只是 client 也要把它拉回来。
if ($doServer -or $Service) { Start-Server }

if ($doClient) {
    if ($doServer -or $Service) {
        Info "    等待 $ClientDelaySeconds 秒后启动 client ..."
        Start-Sleep -Seconds $ClientDelaySeconds
    }
    Start-Client
}
# All 模式也编译了 imk（build.ps1），顺带确保它在用户 PATH。
if ($Target -eq 'All') { Ensure-ImkPath }
Ok "==> 完成。"
