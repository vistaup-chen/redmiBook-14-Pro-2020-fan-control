# run-elevated.ps1 —— 以管理员权限静默运行一个 .ps1（本机 UAC 配置为静默提权，无弹窗）
# 用法: powershell -NoProfile -File run-elevated.ps1 -Script <target.ps1> -OutFile <output.txt> [-ScriptArgs a,b] [-TimeoutSec 300]
param(
  [Parameter(Mandatory=$true)][string]$Script,
  [Parameter(Mandatory=$true)][string]$OutFile,
  [string[]]$ScriptArgs = @(),
  [int]$TimeoutSec = 300
)
$ErrorActionPreference = 'Stop'
$Script = (Resolve-Path $Script).Path
$OutFile = [System.IO.Path]::GetFullPath($OutFile)
New-Item -ItemType Directory -Force -Path (Split-Path $OutFile -Parent) | Out-Null

function Q($s){ return "'" + ($s -replace "'","''") + "'" }
$argLine = ''
foreach($a in $ScriptArgs){ $argLine += ' ' + (Q $a) }
$inner = "& " + (Q $Script) + $argLine + " *>&1 | Out-File -FilePath " + (Q $OutFile) + " -Encoding utf8; exit `$LASTEXITCODE"
$bytes  = [System.Text.Encoding]::Unicode.GetBytes($inner)
$b64    = [Convert]::ToBase64String($bytes)

$p = Start-Process -FilePath 'powershell.exe' `
  -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-EncodedCommand',$b64) `
  -Verb RunAs -WindowStyle Hidden -PassThru
if(-not $p.WaitForExit($TimeoutSec * 1000)){
  try { $p.Kill() } catch {}
  Write-Error "Elevated run timed out after ${TimeoutSec}s: $Script"
  exit 124
}
exit $p.ExitCode
