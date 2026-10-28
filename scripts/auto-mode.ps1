# auto-mode.ps1 - thermal-follow mode watcher (run by scheduled task ZFanAutoMode, as SYSTEM)
# Silent <-> Balanced only. Never touches Performance/DoNotDisturb (user intent).
#   escalate: temp >= 62C on 2 consecutive runs  -> Balanced
#   return:   temp <= 54C on 5 consecutive runs  -> Silent
#   anti-flap: no decision counted within 3 min after a switch
# DryRun switch: print decision without acting. ASCII only.
param([switch]$DryRun)

$ec        = "C:\Program Files (x86)\NoteBook FanControl\ec-probe.exe"
$root      = Split-Path $PSScriptRoot -Parent
$switch    = Join-Path $PSScriptRoot "mode-switch.ps1"
$stateFile = Join-Path $root "logs\auto-mode-state.txt"
$logFile   = Join-Path $root "logs\auto-mode.log"
$guidSilent   = "9a10e4a1-1111-4b7a-8c0d-000000000001"
$guidBalanced = "9a10e4a1-1111-4b7a-8c0d-000000000002"

function Log($msg){
  $line = "{0} {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg
  if($DryRun){ $line = "[DRYRUN] " + $line }
  Add-Content -Path $logFile -Value $line -Encoding UTF8
  if((Get-Item $logFile -ErrorAction SilentlyContinue).Length -gt 200KB){
    Get-Content $logFile -Tail 500 | Set-Content $logFile -Encoding UTF8
  }
}

# --- read EC temperature (0xA8) ---
$raw = (& $ec read 168 2>&1) -join ' '
if($raw -match '\((0x[0-9A-Fa-f]+)\)'){
  $temp = [Convert]::ToInt32($matches[1], 16)
} else { Log "temp read failed: $raw"; exit 1 }

# --- read active scheme ---
$sch = (powercfg /getactivescheme 2>&1) -join ' '
if($sch -match '([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})'){
  $scheme = $matches[1].ToLower()
} else { Log "scheme read failed: $sch"; exit 1 }

# --- load state ---
$state = @{ hot=0; cool=0; lastSwitch='' }
if(Test-Path $stateFile){
  Get-Content $stateFile | ForEach-Object {
    if($_ -match '^hot=(\d+)'){ $state.hot = [int]$matches[1] }
    if($_ -match '^cool=(\d+)'){ $state.cool = [int]$matches[1] }
    if($_ -match '^lastSwitch=(.+)$'){ $state.lastSwitch = $matches[1] }
  }
}
$coolSinceSwitch = $true
if($state.lastSwitch){
  $ts = [datetime]::Parse($state.lastSwitch)
  if(((Get-Date) - $ts).TotalMinutes -lt 3){ $coolSinceSwitch = $false }
}

$action = "none"
if($scheme -eq $guidSilent){
  if($temp -ge 62){ $state.hot++ } else { $state.hot = 0 }
  if($state.hot -ge 2 -and $coolSinceSwitch){ $action = "escalate" }
} elseif($scheme -eq $guidBalanced){
  if($temp -le 54){ $state.cool++ } else { $state.cool = 0 }
  if($state.cool -ge 5 -and $coolSinceSwitch){ $action = "return" }
} else {
  Log ("user mode " + $scheme + " - watcher stays out")
  $state.hot = 0; $state.cool = 0
}

if($action -eq "escalate" -and -not $DryRun){
  & powershell -NoProfile -ExecutionPolicy Bypass -File $switch balanced 2>&1 | Out-Null
  $state.lastSwitch = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
  $state.hot = 0; $state.cool = 0
  Log ("escalated to Balanced, temp=" + $temp)
} elseif($action -eq "return" -and -not $DryRun){
  & powershell -NoProfile -ExecutionPolicy Bypass -File $switch quiet 2>&1 | Out-Null
  $state.lastSwitch = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
  $state.hot = 0; $state.cool = 0
  Log ("returned to Silent, temp=" + $temp)
} else {
  Log ("temp=" + $temp + " scheme=" + $scheme.Substring(30) + " hot=" + $state.hot + " cool=" + $state.cool + " -> " + $action)
}

# --- save state ---
$lines = @(
  "hot=$($state.hot)",
  "cool=$($state.cool)",
  "lastSwitch=$($state.lastSwitch)"
)
Set-Content -Path $stateFile -Value $lines -Encoding ASCII
