#requires -Version 5.1
<#
.SYNOPSIS
  打包 immurok for Windows 的安装程序（Inno Setup）。

.DESCRIPTION
  流程：编 C++ Credential Provider → 发布三个 .NET 程序（框架依赖）→ 汇总到暂存目录
        →（可选）签名二进制 → ISCC 编译（签名时连同安装包、卸载程序一起签）。

  为什么是框架依赖而非自包含：安装包只有几 MB，代价是目标机需装 .NET 8 Desktop
  Runtime。immurok.iss 的 InitializeSetup 会在安装前检测并给出下载指引，不会让用户
  装完才在启动时撞见运行时缺失。

  为什么用 Inno 而不是 WiX：WiX v6 起适用 Open Source Maintenance Fee，v7 更是不接受
  许可条款就无法执行任何命令。Inno Setup 完全免费且无此类机制。MSIX 则做不了本项目
  ——它禁止把包外进程加载的 in-proc 扩展（Credential Provider 正是被 LogonUI 加载的
  in-proc COM DLL），也禁止写 HKLM。

.NOTES
  依赖：
    · .NET 8 SDK
    · Visual Studio 2022（编 C++ Credential Provider）
    · Inno Setup 6：https://jrsoftware.org/isdl.php

  ⚠️ Credential Provider 进入登录链路，首次务必在带快照的虚拟机中验证。

.PARAMETER Configuration
  Release（默认）或 Debug。

.PARAMETER Arch
  x64 / arm64 / all（默认 all，两个都出）。两个架构必须各出一个安装包，不能合并：
  Credential Provider 是被 LogonUI 在进程内加载的 DLL，Windows 不允许跨架构在同一
  进程内加载，所以 ARM64 机器必须用原生 ARM64 的 CP。
  编 ARM64 需要在 Visual Studio Installer 里勾选 C++ 的 ARM64/ARM64EC 生成工具组件。

.PARAMETER ArtifactSigningEndpoint / ArtifactSigningAccount / ArtifactSigningProfile
  三者都给时用 Azure Artifact Signing（原 Trusted Signing）签名：证书与私钥全由微软托管，
  signtool 通过 dlib 插件调服务签（CI 用这条）。dlib 首次使用时自动从 NuGet 下到
  packaging\.tools；signtool 取本机 Windows SDK 里最新的 x64 版（或用 -SignToolPath 指定）。
  身份走 DefaultAzureCredential：CI 里是 azure/login 的 OIDC 会话，本机是 az login。

.PARAMETER KeyVaultUrl / KeyVaultCert
  两者都给时用 AzureSignTool 签名，证书私钥在 Azure Key Vault 的 HSM 里（自购 OV 证书时用）。
  需要先装 AzureSignTool： dotnet tool install --global AzureSignTool

.PARAMETER SignToolPath / CertSubject
  两者都给时用 signtool 按证书主体签名（证书在本机证书库 / U 盾里）。

  三种方式都不给则跳过签名。签名范围：自家 exe/dll、安装包、卸载程序。

.EXAMPLE
  .\packaging\build-installer.ps1                 # x64 + arm64 两个包
  .\packaging\build-installer.ps1 -Arch x64       # 只出 x64
  .\packaging\build-installer.ps1 -Configuration Debug
  .\packaging\build-installer.ps1 -ArtifactSigningEndpoint https://jpe.codesigning.azure.net `
      -ArtifactSigningAccount immurok -ArtifactSigningProfile immurok-public
#>
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',
    # 目标架构。x64 与 arm64 各出一个独立安装包（不能合并：CP 是进程内 DLL，
    # 必须与 LogonUI 的架构一致）。默认两个都出。
    [ValidateSet('x64','arm64','all')]
    [string]$Arch = 'all',
    [string]$ArtifactSigningEndpoint = '',
    [string]$ArtifactSigningAccount  = '',
    [string]$ArtifactSigningProfile  = '',
    [string]$KeyVaultUrl  = '',
    [string]$KeyVaultCert = '',
    [string]$SignToolPath = '',
    [string]$CertSubject  = '',
    # 必须带时间戳：否则证书一过期，已发布的安装包签名跟着失效（Artifact Signing 的证书
    # 只有 3 天有效期）。留空按签名方式取默认：Artifact Signing 用微软的，其余用 DigiCert 的。
    [string]$TimestampUrl = '',
    # 自动定位失败时手工指定 ISCC.exe 完整路径
    [string]$IsccPath     = '',
    # 版本号。留空则取 Directory.Build.props 的 <Version>；CI 里由 tag 传入，
    # 否则打 v0.2.0 的 tag 会发出一个内部仍写着 0.1.0 的包。
    [string]$Version      = ''
)

$ErrorActionPreference = 'Stop'
$pkgDir = $PSScriptRoot
$root   = Split-Path $pkgDir -Parent
$outDir = Join-Path $pkgDir 'out'

function Info($m) { Write-Host $m -ForegroundColor Cyan }
function Ok($m)   { Write-Host $m -ForegroundColor Green }

# 版本号取自 Directory.Build.props，避免两处各写一份对不上
$propsPath = Join-Path $root 'Directory.Build.props'
# PowerShell 变量名大小写不敏感，$Version 与 $version 是同一个：参数有值就用参数。
if (-not $Version) {
    $Version = ([xml](Get-Content $propsPath)).Project.PropertyGroup.Version |
               Where-Object { $_ } | Select-Object -First 1
    if (-not $Version) { throw "无法从 $propsPath 读取 <Version>，也未通过 -Version 指定。" }
}
# AssemblyVersion / FileVersion 只接受四段纯数字，须剥掉 -beta.1 这类预发布后缀
$numericVersion = ($Version -split '-')[0]
while (($numericVersion -split '\.').Count -lt 4) { $numericVersion += '.0' }
Info "==> 版本 $Version（程序集 $numericVersion，$Configuration）"

# ---- 定位 ISCC（先做，免得编译半天最后卡在找不到编译器）----
# 不写死「Inno Setup 6」这类目录名：winget 可能装到 per-user 目录，
# 版本升级后目录名也会变（Inno Setup 7…）。优先级：显式参数 → PATH → 注册表 → 常见目录。
function Find-Iscc {
    if ($IsccPath) {
        if (Test-Path $IsccPath) { return $IsccPath }
        throw "指定的 ISCC 不存在: $IsccPath"
    }

    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    # Inno Setup 的卸载项记录了 InstallLocation，winget 装的同样会写这里。
    # 键名带版本号（Inno Setup 6_is1 / Inno Setup 7_is1…），故用通配匹配。
    $hives = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
    )
    foreach ($hive in $hives) {
        if (-not (Test-Path $hive)) { continue }
        $keys = @(Get-ChildItem $hive -ErrorAction SilentlyContinue |
                  Where-Object { $_.PSChildName -like 'Inno Setup*' })
        foreach ($k in $keys) {
            $loc = (Get-ItemProperty $k.PSPath -ErrorAction SilentlyContinue).InstallLocation
            if ($loc) {
                $p = Join-Path $loc 'ISCC.exe'
                if (Test-Path $p) { return $p }
            }
        }
    }

    $roots = @("${env:ProgramFiles(x86)}", $env:ProgramFiles, "$env:LOCALAPPDATA\Programs") |
             Where-Object { $_ -and (Test-Path $_) }
    foreach ($r in $roots) {
        $dirs = @(Get-ChildItem -LiteralPath $r -Filter 'Inno Setup*' -Directory -ErrorAction SilentlyContinue)
        foreach ($d in $dirs) {
            $p = Join-Path $d.FullName 'ISCC.exe'
            if (Test-Path $p) { return $p }
        }
    }
    return $null
}

$iscc = Find-Iscc
if (-not $iscc) {
    throw @"
未找到 Inno Setup 编译器 ISCC.exe。
已依次查过：PATH、注册表卸载项（HKLM/HKCU 下 Inno Setup*）、Program Files 与
%LOCALAPPDATA%\Programs 下的 Inno Setup* 目录。

若已安装但仍未找到，先定位它：
    Get-ChildItem C:\ , "$env:LOCALAPPDATA\Programs" -Filter ISCC.exe -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 5 -ExpandProperty FullName
再用参数指定：
    .\packaging\build-installer.ps1 -IsccPath "<上面查到的完整路径>"

未安装则见 https://jrsoftware.org/isdl.php
"@
}
Info "==> 使用 ISCC: $iscc"

# ---- 定位 MSBuild（编 C++ Credential Provider 用）----
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = $null
if (Test-Path $vswhere) {
    $msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe |
               Select-Object -First 1
}
if (-not $msbuild) {
    throw "未找到 MSBuild。需要 Visual Studio 并安装「使用 C++ 的桌面开发」工作负载。"
}

# VS 安装根目录：用于判断该套 VS 支持哪些 C++ 目标平台
$vsRoot = $null
if (Test-Path $vswhere) {
    $vsRoot = & $vswhere -latest -property installationPath | Select-Object -First 1
}

# ARM64 的 C++ 生成工具装没装，看 VC 的 Platforms 目录里有没有 ARM64。
# 缺这个组件时 MSBuild 报的是「没有为项目设置 BaseOutputPath/OutputPath ... 配置和平台
# 组合」，字面上完全看不出是缺组件，所以提前检测、给出能直接照做的提示。
function Test-Arm64Toolset {
    if (-not $vsRoot) { return $false }
    $vcDirs = @(Get-ChildItem (Join-Path $vsRoot 'MSBuild\Microsoft\VC') -Directory -ErrorAction SilentlyContinue)
    foreach ($d in $vcDirs) {
        if (Test-Path (Join-Path $d.FullName 'Platforms\ARM64')) { return $true }
    }
    return $false
}

# ---- 签名方式（先定下来，缺工具就立刻报错，不要编译完才发现）----
# $signExe + $signArgs 后面接文件路径就是完整命令；二进制由 Invoke-Sign 直接调，
# 安装包与卸载程序交给 Inno 的 SignTool 调（见下面 ISCC 一步）。
$signExe  = $null
$signArgs = @()

# Artifact Signing 要求 Windows SDK 10.0.22621 及以上的 x64 signtool（20348 那版与 dlib 不兼容）
function Find-SignTool {
    if ($SignToolPath) {
        if (Test-Path $SignToolPath) { return $SignToolPath }
        throw "signtool 不存在: $SignToolPath"
    }
    $kits = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    $best = Get-ChildItem $kits -Directory -Filter '10.0.*' -ErrorAction SilentlyContinue |
            Where-Object { [version]$_.Name -ge [version]'10.0.22621.0' -and
                           (Test-Path (Join-Path $_.FullName 'x64\signtool.exe')) } |
            Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if (-not $best) { throw "未找到 Windows SDK 10.0.22621 及以上的 signtool.exe（查过 $kits），可用 -SignToolPath 指定。" }
    return (Join-Path $best.FullName 'x64\signtool.exe')
}

# dlib 不随 Windows SDK 提供，从 NuGet 取 Microsoft.ArtifactSigning.Client（.nupkg 就是 zip），
# 解到 packaging\.tools 下按版本缓存。包里自带 VC++ 运行库，另需本机有 .NET 8 运行时。
function Get-ArtifactSigningDlib {
    $toolsDir = Join-Path $pkgDir '.tools'
    $cached = Get-ChildItem $toolsDir -Directory -Filter 'artifact-signing-client-*' -ErrorAction SilentlyContinue |
              Sort-Object Name -Descending | Select-Object -First 1
    if ($cached) {
        $dll = Join-Path $cached.FullName 'bin\x64\Azure.CodeSigning.Dlib.dll'
        if (Test-Path $dll) { return $dll }
    }
    Info "==> 下载 Artifact Signing dlib ..."
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $base = 'https://api.nuget.org/v3-flatcontainer/microsoft.artifactsigning.client'
    $ver  = (Invoke-RestMethod "$base/index.json").versions | Select-Object -Last 1
    $dest = Join-Path $toolsDir "artifact-signing-client-$ver"
    $zip  = Join-Path $toolsDir "artifact-signing-client-$ver.zip"
    New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
    Invoke-WebRequest "$base/$ver/microsoft.artifactsigning.client.$ver.nupkg" -OutFile $zip -UseBasicParsing
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $dest)
    Remove-Item $zip -Force
    $dll = Join-Path $dest 'bin\x64\Azure.CodeSigning.Dlib.dll'
    if (-not (Test-Path $dll)) { throw "dlib 包里没有预期的文件: $dll" }
    return $dll
}

if ($ArtifactSigningEndpoint -or $ArtifactSigningAccount -or $ArtifactSigningProfile) {
    if (-not ($ArtifactSigningEndpoint -and $ArtifactSigningAccount -and $ArtifactSigningProfile)) {
        throw "-ArtifactSigningEndpoint / -ArtifactSigningAccount / -ArtifactSigningProfile 必须同时给出。"
    }
    $signExe = Find-SignTool
    $dlib    = Get-ArtifactSigningDlib
    # 账户与证书配置通过 JSON 传给 dlib。ExcludeCredentials 跳过 DefaultAzureCredential 里用不上的
    # 几种：托管标识在 GitHub runner（本身是 Azure VM）上会去探 IMDS，白等超时甚至拿错身份。
    $meta = Join-Path $pkgDir '.tools\artifact-signing-metadata.json'
    $json = [ordered]@{
        Endpoint               = $ArtifactSigningEndpoint
        CodeSigningAccountName = $ArtifactSigningAccount
        CertificateProfileName = $ArtifactSigningProfile
        ExcludeCredentials     = @('ManagedIdentityCredential', 'WorkloadIdentityCredential',
                                   'SharedTokenCacheCredential', 'VisualStudioCredential',
                                   'VisualStudioCodeCredential', 'InteractiveBrowserCredential')
    } | ConvertTo-Json
    [IO.File]::WriteAllText($meta, $json, (New-Object Text.UTF8Encoding $false))
    if (-not $TimestampUrl) { $TimestampUrl = 'http://timestamp.acs.microsoft.com' }
    $signArgs = @('sign', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256', '/dlib', $dlib, '/dmdf', $meta)
} elseif ($KeyVaultUrl -or $KeyVaultCert) {
    if (-not ($KeyVaultUrl -and $KeyVaultCert)) { throw "-KeyVaultUrl 与 -KeyVaultCert 必须同时给出。" }
    $ast = Get-Command AzureSignTool.exe -ErrorAction SilentlyContinue
    if (-not $ast) { throw "未找到 AzureSignTool。安装： dotnet tool install --global AzureSignTool" }
    if (-not $TimestampUrl) { $TimestampUrl = 'http://timestamp.digicert.com' }
    $signExe  = $ast.Source
    $signArgs = @('sign', '-kvu', $KeyVaultUrl, '-kvc', $KeyVaultCert, '-kvm',
                  '-tr', $TimestampUrl, '-td', 'sha256', '-fd', 'sha256')
} elseif ($SignToolPath -and $CertSubject) {
    if (-not (Test-Path $SignToolPath)) { throw "signtool 不存在: $SignToolPath" }
    if (-not $TimestampUrl) { $TimestampUrl = 'http://timestamp.digicert.com' }
    $signExe  = $SignToolPath
    $signArgs = @('sign', '/n', $CertSubject, '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256')
}
if ($signExe) { Info "==> 签名: $signExe" } else { Write-Warning "未配置签名，产出的是未签名安装包（SmartScreen 会拦）。" }

function Invoke-Sign([string[]]$targets) {
    if (-not $signExe) { return }
    if (-not $targets -or $targets.Count -eq 0) { return }
    Info "    签名 $($targets.Count) 个文件 ..."
    & $signExe @signArgs @targets
    if ($LASTEXITCODE -ne 0) { throw "签名失败（退出码 $LASTEXITCODE）。" }
}

# Inno 的 /S 签名命令：$q 是引号、$f 是待签文件（Inno 自己的占位符，所以用单引号不让
# PowerShell 展开）。带空格的参数（signtool 路径、证书主体）用 $q 包起来。
function Get-InnoSignCommand {
    $parts = (@($signExe) + $signArgs) | ForEach-Object {
        if ($_ -match '\s') { '$q' + $_ + '$q' } else { $_ }
    }
    return ($parts -join ' ') + ' $f'
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$Arch = $Arch.ToLowerInvariant()
$targets = if ($Arch -eq 'all') { @('x64','arm64') } else { @($Arch) }
$skipped = @()

if ($targets -contains 'arm64' -and -not (Test-Arm64Toolset)) {
    $hint = @"
当前 Visual Studio 未安装 ARM64 的 C++ 生成工具（在 $vsRoot 的
MSBuild\Microsoft\VC\*\Platforms 下找不到 ARM64）。
补装方法：Visual Studio Installer → 修改 → 单个组件 → 勾选 C++ 的
「ARM64/ARM64EC 生成工具」（名称含当前 MSVC 版本号）。
"@
    if ($Arch -eq 'arm64') {
        throw $hint
    }
    Write-Warning $hint
    Write-Warning "本次跳过 arm64，只出 x64 包。"
    $targets = @($targets | Where-Object { $_ -ne 'arm64' })
    $skipped += 'arm64'
}

$built = @()

foreach ($arch in $targets) {
    # C++ 项目的平台名是 ARM64/x64；.NET 的 RID 是 win-arm64/win-x64
    # RID 必须是小写（win-arm64 / win-x64），MSBuild 的 Platform 则是 ARM64 / x64
    $arch        = $arch.ToLowerInvariant()
    $cppPlatform = if ($arch -eq 'arm64') { 'ARM64' } else { 'x64' }
    $rid         = "win-$arch"
    $stage       = Join-Path $pkgDir "stage-$arch"

    Write-Host ""
    Info "======== 构建 $arch ========"

    # ---- 1. 清理暂存目录 ----
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    # ---- 2. C++ Credential Provider ----
    # 必须逐架构编译：CP 是被 LogonUI 在进程内加载的 DLL，Windows 不允许跨架构在同一
    # 进程内加载，ARM64 机器上的原生 LogonUI 加载不了 x64 的 CP。
    $cpProj = Join-Path $root 'ImmurokCredentialProvider\ImmurokCredentialProvider.vcxproj'
    $cpDll  = Join-Path $root "ImmurokCredentialProvider\$cppPlatform\$Configuration\ImmurokCredentialProvider.dll"

    Info "==> 编译 Credential Provider ($Configuration|$cppPlatform) ..."
    & $msbuild $cpProj /p:Configuration=$Configuration /p:Platform=$cppPlatform /v:m /nologo
    if ($LASTEXITCODE -ne 0) {
        if ($cppPlatform -eq 'ARM64') {
            throw @"
Credential Provider 的 ARM64 编译失败（退出码 $LASTEXITCODE）。
多半是缺少 ARM64 编译器：请在 Visual Studio Installer 里勾选 C++ 的
「ARM64/ARM64EC 生成工具」组件（名称含当前 MSVC 版本号）后重试。
只想出 x64 包可以用： .\packaging\build-installer.ps1 -Arch x64
"@
        }
        throw "Credential Provider 编译失败（退出码 $LASTEXITCODE）。"
    }
    if (-not (Test-Path $cpDll)) { throw "未找到编译产物: $cpDll" }

    # ---- 3. 发布三个 .NET 程序（框架依赖，同一目录共享依赖 DLL）----
    foreach ($proj in 'ImmurokService','ImmurokClient','ImmurokCli') {
        Info "==> 发布 $proj ($rid) ..."
        dotnet publish (Join-Path $root "$proj\$proj.csproj") `
            -c $Configuration -r $rid --self-contained false `
            -p:Platform=$cppPlatform -p:PublishSingleFile=false `
            -p:Version=$Version -p:AssemblyVersion=$numericVersion -p:FileVersion=$numericVersion `
            -o $stage --nologo
        if ($LASTEXITCODE -ne 0) { throw "$proj ($rid) 发布失败（退出码 $LASTEXITCODE）。" }
    }

    Copy-Item $cpDll $stage -Force
    Copy-Item (Join-Path $root 'ImmurokClient\Assets\immurok.ico') (Join-Path $stage 'immurok.ico') -Force
    Get-ChildItem $stage -Filter *.pdb -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue

    $fileCount = (Get-ChildItem $stage -Recurse -File).Count
    Ok "    暂存目录就绪（$fileCount 个文件）"

    # ---- 4.（可选）签名二进制 ----
    # 只签自家产物（imk.exe 的托管程序集是 imk.dll）；第三方依赖保持各自原有签名
    Invoke-Sign (Get-ChildItem $stage -Include *.exe,*.dll -Recurse |
                 Where-Object { $_.Name -like 'Immurok*' -or $_.BaseName -eq 'imk' } |
                 ForEach-Object { $_.FullName })

    # ---- 5. ISCC 编译 ----
    # 签名时由 Inno 自己调签名命令：卸载程序 unins000.exe 是 ISCC 编译期生成并压进安装包的，
    # 事后只签 setup.exe 签不到它，装完后从「设置 → 应用」卸载时 UAC 仍显示未知发布者。
    Info "==> ISCC 编译 ($arch) ..."
    $isccArgs = @("/DAppVersion=$version", "/DStageDir=$stage", "/DArch=$arch", "/O$outDir")
    if ($signExe) { $isccArgs += @('/DSigned=1', "/Simmurok=$(Get-InnoSignCommand)") }
    & $iscc @isccArgs (Join-Path $pkgDir 'immurok.iss')
    if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败（$arch，退出码 $LASTEXITCODE）。" }

    $setup = Join-Path $outDir "immurok-$version-$arch-setup.exe"
    if (-not (Test-Path $setup)) { throw "ISCC 未产出预期文件: $setup" }

    # ---- 6.（签名时）确认安装包签名有效，免得发出去才发现没签上 ----
    if ($signExe) {
        $sig = Get-AuthenticodeSignature $setup
        if ($sig.Status -ne 'Valid') { throw "安装包签名无效（$($sig.Status)）: $setup" }
        Ok "    已签名: $($sig.SignerCertificate.Subject)"
    }
    $built += $setup
    Ok "    完成: $setup"
}

Write-Host ""
if ($skipped.Count -gt 0) {
    Write-Warning "已跳过的架构：$($skipped -join ', ')（见上方原因）"
    Write-Host ""
}
Ok "==> 完成，产出："
foreach ($b in $built) {
    $mb = [math]::Round((Get-Item $b).Length / 1MB, 1)
    Write-Host "    $b  ($mb MB)" -ForegroundColor Green
}
Write-Host ""
Write-Host "安装：双击运行（会请求管理员权限）；静默安装加 /VERYSILENT /NORESTART" -ForegroundColor Yellow
Write-Host "卸载：设置 → 应用，或安装目录下的 unins000.exe（会询问是否一并删除配对数据）" -ForegroundColor Yellow
Write-Host "架构：两个包互斥 —— x64 包在 ARM64 机器上会拒装并提示改用 arm64 包。" -ForegroundColor Yellow
Write-Host "⚠️ Credential Provider 会进入登录链路，首次务必在带快照的虚拟机中验证。" -ForegroundColor Red
