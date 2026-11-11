#requires -Version 5.1
<#
.SYNOPSIS
  开发迭代冒烟：编译 ImmurokService → 后台运行 → 跑非交互管道测试 → 停止 → 汇总日志。
  所有输出写入 tools\last-run.log（Claude 会通过桥接读取该文件，无需手动粘贴）。

  注意：涉及"按设备按键 / 触摸指纹"的步骤（如 PAIR:START）无法脚本化，需人工。
        本脚本只做不依赖硬件交互的验证。
#>
param(
    [int]$WaitSeconds = 8
)

$ErrorActionPreference = 'Continue'
$toolsDir = $PSScriptRoot
$root     = Split-Path $toolsDir -Parent
$log      = Join-Path $toolsDir 'last-run.log'
$svcOut   = Join-Path $toolsDir 'service.out.log'
$svcErr   = Join-Path $toolsDir 'service.err.log'
$tfm      = 'net10.0-windows10.0.22621.0'
$binDir   = Join-Path $root "ImmurokService\bin\x64\Debug\$tfm"
$exe      = Join-Path $binDir 'ImmurokService.exe'

function Log($m) { $m | Tee-Object -FilePath $log -Append }

"===== dev-loop $(Get-Date -Format o) =====" | Out-File $log

# 1) 编译
Log "==> dotnet build ImmurokService (Debug/x64)"
dotnet build "$root\ImmurokService\ImmurokService.csproj" -c Debug -p:Platform=x64 2>&1 | Tee-Object -FilePath $log -Append
if ($LASTEXITCODE -ne 0) { Log "!! 编译失败，终止"; exit 1 }

# 2) 后台启动服务（重定向标准输出/错误到文件）
if (-not (Test-Path $exe)) { Log "!! 未找到 $exe"; exit 1 }
Log "==> 启动服务：$exe"
if (Test-Path $svcOut) { Remove-Item $svcOut -Force }
if (Test-Path $svcErr) { Remove-Item $svcErr -Force }
$proc = Start-Process -FilePath $exe -WorkingDirectory $binDir `
    -RedirectStandardOutput $svcOut -RedirectStandardError $svcErr `
    -PassThru -WindowStyle Hidden
Log "    PID=$($proc.Id)，等待 $WaitSeconds 秒让其连接设备…"
Start-Sleep -Seconds $WaitSeconds

# 3) 管道测试（非交互）
function Send-Immurok($cmd) {
    try {
        $p = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'immurok', 'InOut')
        $p.Connect(3000)
        $msg = [Text.Encoding]::UTF8.GetBytes($cmd)
        $p.Write([BitConverter]::GetBytes([int]$msg.Length),0,4)
        $p.Write($msg,0,$msg.Length); $p.Flush()
        $lenBuf = New-Object byte[] 4; $null = $p.Read($lenBuf,0,4)
        $n = [BitConverter]::ToInt32($lenBuf,0)
        $buf = New-Object byte[] $n; $r=0; while($r -lt $n){ $r += $p.Read($buf,$r,$n-$r) }
        $p.Dispose(); return [Text.Encoding]::UTF8.GetString($buf)
    } catch { return "‹管道错误: $($_.Exception.Message)›" }
}

Log "==> 管道测试"
foreach ($cmd in @('STATUS','PAIR:STATUS','FP:LIST')) {
    Log ("    {0,-12} -> {1}" -f $cmd, (Send-Immurok $cmd))
}

# 4) 停止服务并汇总其日志
Log "==> 停止服务"
try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch {}
Start-Sleep -Milliseconds 500

Log "`n----- 服务 stdout -----"
if (Test-Path $svcOut) { Get-Content $svcOut | Out-File $log -Append }
Log "`n----- 服务 stderr -----"
if (Test-Path $svcErr) { Get-Content $svcErr | Out-File $log -Append }

Log "`n===== 完成，日志见 $log ====="
