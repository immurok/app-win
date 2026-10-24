#requires -Version 5.1
<#
.SYNOPSIS
  编译 immurok Windows 版全部组件（Service / Client / Common / ConsolePrompt + C++ Credential Provider）。
.NOTES
  需已安装 Visual Studio 2022（含"使用 C++ 的桌面开发"与 .NET 10 SDK）。

  为什么不对整个 .sln 跑 `dotnet build`：
    解决方案里含 C++ 的 .vcxproj，dotnet CLI 无法导入 Microsoft.Cpp.Default.props（报 MSB4278），
    会导致整条命令失败。因此这里用 dotnet 只编 .NET 组件，C++ 项目单独用 VS 的 MSBuild 编。
#>
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# ---- .NET 组件：只编 Service 与 Client 两个入口项目 ----
# （二者经 ProjectReference 会自动带上 ImmurokCommon 与 ImmurokConsolePrompt，无需单独编）
Write-Host "==> 还原并编译 .NET 组件 ($Configuration/x64)..." -ForegroundColor Cyan
dotnet build "$root\ImmurokService\ImmurokService.csproj" -c $Configuration -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw "dotnet 编译 ImmurokService 失败（退出码 $LASTEXITCODE）。" }
dotnet build "$root\ImmurokClient\ImmurokClient.csproj" -c $Configuration -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw "dotnet 编译 ImmurokClient 失败（退出码 $LASTEXITCODE）。" }
dotnet build "$root\ImmurokCli\ImmurokCli.csproj" -c $Configuration -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw "dotnet 编译 imk (ImmurokCli) 失败（退出码 $LASTEXITCODE）。" }

# ---- C++ Credential Provider：用 VS2022 的 MSBuild ----
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" `
    -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe |
    Select-Object -First 1

if ($msbuild) {
    Write-Host "==> 编译 Credential Provider ($Configuration|x64)..." -ForegroundColor Cyan
    & $msbuild "$root\ImmurokCredentialProvider\ImmurokCredentialProvider.vcxproj" `
        /p:Configuration=$Configuration /p:Platform=x64 /v:m /nologo
    if ($LASTEXITCODE -ne 0) { throw "MSBuild 编译 Credential Provider 失败（退出码 $LASTEXITCODE）。" }
} else {
    Write-Warning "未找到 MSBuild，跳过 C++ CP 编译。请在 Visual Studio 中手动生成该项目。"
}

Write-Host "==> 完成。" -ForegroundColor Green
