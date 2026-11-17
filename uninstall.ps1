#requires -Version 5.1 -RunAsAdministrator
<#
.SYNOPSIS
  卸载 immurok：反注册 Credential Provider、删除服务、清理文件与数据。
.NOTES 必须以管理员运行。
#>
param(
    [string]$InstallDir = "$env:ProgramFiles\immurok"
)

$ErrorActionPreference = 'Continue'
$root = $PSScriptRoot

Write-Host "==> 反注册 Credential Provider ..." -ForegroundColor Cyan
reg.exe import "$root\ImmurokCredentialProvider\Unregister.reg"

Write-Host "==> 停止服务 ..." -ForegroundColor Cyan
sc.exe stop ImmurokService | Out-Null

# 登录密码以 CRED_PERSIST_LOCAL_MACHINE 存在服务账户（LocalSystem）名下，只有 SYSTEM 删得掉；
# 这里是管理员，cmdkey /delete 删的是管理员自己的凭据集，什么也删不到。
# 做法：把服务的 binPath 临时改成带 --clear-credential，让 SCM 以服务账户拉起一次。
# 进程删完直接退出、不连 SCM，所以 sc start 报 1053 是预期结果。
Write-Host "==> 清理凭据管理器里的登录密码 ..." -ForegroundColor Cyan
$svcExe = Join-Path $InstallDir 'ImmurokService.exe'
$rc = -1
if (Test-Path $svcExe) {
    # 用 CIM 改 binPath 而不是 sc.exe config：带引号的参数在 PowerShell 5.1 和 7 下传给原生程序的转义规则不同
    $changed = Get-CimInstance Win32_Service -Filter "Name='ImmurokService'" |
        Invoke-CimMethod -MethodName Change -Arguments @{ PathName = "`"$svcExe`" --clear-credential" }
    if ($changed -and $changed.ReturnValue -eq 0) {
        # sc stop 是异步的：还在 STOP_PENDING 时 start 回 1056，等它停稳，最多 20 秒
        for ($i = 0; $i -lt 40; $i++) {
            sc.exe start ImmurokService | Out-Null
            $rc = $LASTEXITCODE
            if ($rc -ne 1056) { break }
            Start-Sleep -Milliseconds 500
        }
    }
}
if ($rc -ne 1053) {
    Write-Warning "未能确认登录密码已清除（sc 返回 $rc）。它仍可能留在 SYSTEM 账户的凭据管理器里（目标名 immurok/login）。"
}

Write-Host "==> 删除服务 ..." -ForegroundColor Cyan
sc.exe delete ImmurokService | Out-Null

Write-Host "==> 删除文件与数据 ..." -ForegroundColor Cyan
Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "$env:ProgramData\immurok" -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "==> 卸载完成。" -ForegroundColor Green
