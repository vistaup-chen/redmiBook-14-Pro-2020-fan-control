# test-e2e.ps1 - end-to-end smoke: launch the real GUI (elevated), wait for the status
# engine to report in logs/app.log, then close it. Catches "app dies on startup" class
# bugs (e.g. WinForms API order violations) that compile-time tests cannot see.
# Result also written to logs/app-e2e.txt.
# Usage: powershell -ExecutionPolicy Bypass -File test-e2e.ps1   (self-elevates silently)
$ErrorActionPreference = 'Stop'
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
if(-not (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
  Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`""); exit
}
$root = Split-Path $PSScriptRoot -Parent
$appLog = Join-Path $root "logs\app.log"
$resultFile = Join-Path $root "logs\app-e2e.txt"
$before = if (Test-Path $appLog) { (Get-Item $appLog).Length } else { 0 }

# single-instance mutex: another GUI instance blocks App.Run with a message box and this
# test would time out. Elevated context -> OpenExisting sees all instances reliably.
$already = $false
try { $null = [System.Threading.Mutex]::OpenExisting('FanManager_TM1901_SingleInstance'); $already = $true } catch { }
if ($already) {
  $msg = "SKIP an instance is already running (its boot path is the one under test's subject anyway)"
  $msg
  Set-Content -Path $resultFile -Value ("{0}  {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg) -Encoding UTF8
  exit 0
}

$proc = Start-Process powershell.exe -WindowStyle Hidden -PassThru -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSScriptRoot\FanManager.ps1`"")
"launched PID $($proc.Id); waiting for status engine..."

# "后台状态引擎已启动" built from code points; log file itself is UTF-8
$engineMark = -join [char[]](0x540E,0x53F0,0x72B6,0x6001,0x5F15,0x64CE,0x5DF2,0x542F,0x52A8)
$ok = $false
for ($i = 0; $i -lt 30; $i++) {
  Start-Sleep -Seconds 2
  if ($proc.HasExited) { $msg = "FAIL process exited early (code $($proc.ExitCode))"; break }
  $new = if (Test-Path $appLog) { (Get-Item $appLog).Length } else { 0 }
  if ($new -gt $before) {
    $tail = Get-Content $appLog -Tail 4 -Encoding UTF8 | Out-String
    if ($tail.Contains($engineMark)) { $ok = $true; $msg = "PASS status engine reported after ~$((($i+1)*2))s"; break }
  }
}
if (-not $ok -and -not $msg) { $msg = "FAIL status engine did not report in 60s" }
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
$msg
Set-Content -Path $resultFile -Value ("{0}  {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg) -Encoding UTF8
if ($ok) { exit 0 } else { exit 1 }
