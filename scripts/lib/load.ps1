# load.ps1 —— 受控 CPU 轻载发生器（用于温度/风扇标定）
# 用法: load.ps1 -Threads 4 -Seconds 90 -DutyPercent 50
# 原理: 每线程按占空比做 busy/sleep 交替，占用 ≈ DutyPercent%
param(
  [int]$Threads = 4,
  [int]$Seconds = 60,
  [int]$DutyPercent = 50
)
$ErrorActionPreference = 'Stop'
$scriptBlock = {
  param($duty, $until)
  $busyMs = [int]([math]::Max(1, $duty))         # busy 毫秒
  $sleepMs = [int]([math]::Max(1, 100 - $duty))  # sleep 毫秒
  while((Get-Date) -lt $until){
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while($sw.ElapsedMilliseconds -lt $busyMs){ }  # busy spin
    Start-Sleep -Milliseconds $sleepMs
  }
}
$until = (Get-Date).AddSeconds($Seconds)
$jobs = @()
for($i=0; $i -lt $Threads; $i++){
  $jobs += Start-Job -ScriptBlock $scriptBlock -ArgumentList $DutyPercent, $until
}
Write-Output ("load: threads={0} duty={1}% seconds={2}" -f $Threads,$DutyPercent,$Seconds)
try {
  while((Get-Date) -lt $until){ Start-Sleep -Seconds 2 }
} finally {
  $jobs | Stop-Job -ErrorAction SilentlyContinue
  $jobs | Remove-Job -Force -ErrorAction SilentlyContinue
}
Write-Output "load: done"
