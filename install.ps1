#requires -Version 5.1 -RunAsAdministrator
<#
.SYNOPSIS
  安装 immurok：部署文件、注册 Windows 服务、注册 Credential Provider。
.NOTES
  ⚠️ 请先在带快照的虚拟机中验证 Credential Provider，避免锁死登录。
  必须以管理员运行。
#>
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',
    [string]$InstallDir = "$env:ProgramFiles\immurok"
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$tfm  = 'net10.0-windows10.0.22621.0'

Write-Host "==> 部署到 $InstallDir ..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null

Copy-Item "$root\ImmurokService\bin\$Configuration\$tfm\*" $InstallDir -Recurse -Force
Copy-Item "$root\ImmurokClient\bin\$Configuration\$tfm\*"  $InstallDir -Recurse -Force
Copy-Item "$root\ImmurokCli\bin\$Configuration\$tfm\*"      $InstallDir -Recurse -Force
Copy-Item "$root\ImmurokCredentialProvider\x64\$Configuration\ImmurokCredentialProvider.dll" $InstallDir -Force

# 把安装目录加入系统 PATH，让终端能直接调用 imk
$machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
if ($machinePath -notlike "*$InstallDir*") {
    Write-Host "==> 将 $InstallDir 加入系统 PATH（imk 命令）..." -ForegroundColor Cyan
    [Environment]::SetEnvironmentVariable('Path', "$machinePath;$InstallDir", 'Machine')
    Write-Host "    新开的终端里即可使用 imk（当前终端需重开或刷新环境变量）。" -ForegroundColor Yellow
}

Write-Host "==> 注册 Windows 服务 ImmurokService ..." -ForegroundColor Cyan
$svcExe = Join-Path $InstallDir 'ImmurokService.exe'
sc.exe create ImmurokService binPath= "`"$svcExe`"" start= auto obj= LocalSystem DisplayName= "immurok 指纹认证服务" | Out-Null
sc.exe description ImmurokService "immurok 蓝牙指纹认证：维持 BLE 连接、锁屏指纹解锁。" | Out-Null
sc.exe start ImmurokService | Out-Null

Write-Host "==> 注册 Credential Provider ..." -ForegroundColor Cyan
reg.exe import "$root\ImmurokCredentialProvider\Register.reg"

Write-Host "==> 完成。请打开 immurok 客户端完成配对、录入指纹、配置登录密码。" -ForegroundColor Green
Write-Host "    锁屏后触摸设备验证解锁（务必保留密码登录后备）。" -ForegroundColor Yellow
