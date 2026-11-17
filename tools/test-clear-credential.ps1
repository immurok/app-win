#requires -Version 5.1
<#
.SYNOPSIS
  验证卸载时清登录密码的机制：把服务 binPath 临时改成带 --clear-credential，让 SCM 以
  LocalSystem 拉起一次，然后还原。对应 packaging/immurok.iss 的 ClearStoredCredential。
.NOTES
  会删掉本机存在凭据管理器里的登录密码（目标名 immurok/login），测完要在客户端里重新设。
  不是管理员时会自己请求提权。
#>
param(
    [string]$InstallDir = "$env:ProgramFiles\immurok"
)

$ErrorActionPreference = 'Continue'
$ServiceName = 'ImmurokService'

# ---- 自提权 ----
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "需要管理员权限，正在请求提权 ..." -ForegroundColor Yellow
    Start-Process powershell.exe -Verb RunAs -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-InstallDir', "`"$InstallDir`"")
    return
}

function Send-Immurok($cmd) {
    try {
        $p = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'immurok', 'InOut')
        $p.Connect(3000)
        $msg = [Text.Encoding]::UTF8.GetBytes($cmd)
        $p.Write([BitConverter]::GetBytes([int]$msg.Length), 0, 4)
        $p.Write($msg, 0, $msg.Length); $p.Flush()
        $lenBuf = New-Object byte[] 4; $null = $p.Read($lenBuf, 0, 4)
        $n = [BitConverter]::ToInt32($lenBuf, 0)
        $buf = New-Object byte[] $n; $r = 0; while ($r -lt $n) { $r += $p.Read($buf, $r, $n - $r) }
        $p.Dispose(); return [Text.Encoding]::UTF8.GetString($buf)
    } catch { return "<管道错误: $($_.Exception.Message)>" }
}

function Wait-PassStatus {
    # 服务刚起来时管道还没建好，最多等 20 秒
    for ($i = 0; $i -lt 40; $i++) {
        $r = Send-Immurok 'PASS:STATUS'
        if ($r -like 'OK:*') { return $r }
        Start-Sleep -Milliseconds 500
    }
    return $r
}

function Set-BinPath($path) {
    $r = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" |
        Invoke-CimMethod -MethodName Change -Arguments @{ PathName = $path }
    return $r.ReturnValue
}

$svc = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
if (-not $svc) { Write-Host "找不到服务 $ServiceName，immurok 没装？" -ForegroundColor Red; Read-Host '按回车退出'; return }
$originalPath = $svc.PathName
$exe = Join-Path $InstallDir 'ImmurokService.exe'
if (-not (Test-Path $exe)) { Write-Host "找不到 $exe" -ForegroundColor Red; Read-Host '按回车退出'; return }

Write-Host "==> 原 binPath: $originalPath"
$before = Send-Immurok 'PASS:STATUS'
Write-Host "==> 清理前 PASS:STATUS = $before" -ForegroundColor Cyan
if ($before -ne 'OK:CONFIGURED') {
    Write-Host "    当前没有存密码（或服务没在跑），这次测试看不出删除效果。先在客户端里设好登录密码再跑。" -ForegroundColor Yellow
}

$rc = -1
$elapsed = $null
try {
    Write-Host "==> 停止服务 ..."
    sc.exe stop $ServiceName | Out-Null
    for ($i = 0; $i -lt 40; $i++) {
        if ((Get-Service $ServiceName).Status -eq 'Stopped') { break }
        Start-Sleep -Milliseconds 500
    }
    Write-Host "    服务状态: $((Get-Service $ServiceName).Status)"

    $cfg = Set-BinPath "`"$exe`" --clear-credential"
    Write-Host "==> 改 binPath 带 --clear-credential，返回 $cfg（0 才算成功）"
    if ($cfg -eq 0) {
        Write-Host "==> 让 SCM 以 LocalSystem 拉起一次 ..."
        $sw = [Diagnostics.Stopwatch]::StartNew()
        sc.exe start $ServiceName | Out-Null
        $rc = $LASTEXITCODE
        $sw.Stop()
        $elapsed = $sw.Elapsed.TotalSeconds
        Write-Host ("    sc start 返回 {0}，耗时 {1:N1} 秒" -f $rc, $elapsed)
    }
}
finally {
    $restore = Set-BinPath $originalPath
    Write-Host "==> 还原 binPath，返回 $restore（0 才算成功）"
    if ($restore -ne 0) {
        Write-Host "    还原失败！请手工执行: sc.exe config $ServiceName binPath= <原 binPath>，原值见上面第一行" -ForegroundColor Red
    }
    sc.exe start $ServiceName | Out-Null
}

$after = Wait-PassStatus
Write-Host "==> 清理后 PASS:STATUS = $after" -ForegroundColor Cyan

Write-Host ""
Write-Host "---------------- 结果 ----------------"
Write-Host ("清理前: {0}" -f $before)
Write-Host ("清理后: {0}" -f $after)
Write-Host ("sc start 返回: {0}（预期 1053）" -f $rc)
if ($null -ne $elapsed) { Write-Host ("sc start 耗时: {0:N1} 秒（接近 30 秒说明 SCM 是等超时才返回）" -f $elapsed) }
if ($before -eq 'OK:CONFIGURED' -and $after -eq 'OK:NOTSET') {
    Write-Host "通过：登录密码已被清除。记得在客户端里重新设密码。" -ForegroundColor Green
} elseif ($after -eq 'OK:CONFIGURED') {
    Write-Host "未通过：密码还在。" -ForegroundColor Red
} else {
    Write-Host "无法判定，见上面的输出。" -ForegroundColor Yellow
}
Read-Host '按回车退出'
