# status.ps1 - show current mode/fan/temp state (ASCII only)
$nbfc = "C:\Program Files (x86)\NoteBook FanControl\nbfc.exe"
$ec   = "C:\Program Files (x86)\NoteBook FanControl\ec-probe.exe"
$modeNames = @{
  "9a10e4a1-1111-4b7a-8c0d-000000000001" = "Silent (quiet mode)"
  "9a10e4a1-1111-4b7a-8c0d-000000000002" = "Balanced"
  "9a10e4a1-1111-4b7a-8c0d-000000000003" = "Performance"
  "9a10e4a1-1111-4b7a-8c0d-000000000004" = "DoNotDisturb"
  "a1841308-3541-4fab-bc81-f71556f20b4a" = "system default (power saver)"
}
"== power plan =="
$sch = (powercfg /getactivescheme 2>&1) -join ' '
if($sch -match '([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})'){
  $g = $matches[1].ToLower()
  $name = $modeNames[$g]
  if(-not $name){ $name = "other/custom" }
  "mode: $name"
}
powercfg /getactivescheme
"== NBFC =="
& $nbfc status 2>&1
"== EC registers =="
"mode(147) duty(148) feedback(149) cpuTemp(168):"
& $ec read 147 2>&1
& $ec read 148 2>&1
& $ec read 149 2>&1
& $ec read 168 2>&1
"== auto mode watcher =="
$t = Get-ScheduledTask -TaskName "ZFanAutoMode" -ErrorAction SilentlyContinue
if($t){
  if($t.State -eq "Running" -or $t.State -eq "Ready"){ "ON  (task state: " + $t.State + ")" } else { "OFF (task state: " + $t.State + ")" }
} else { "OFF (not created)" }
Get-Content (Join-Path (Split-Path $PSScriptRoot -Parent) "logs\auto-mode.log") -Tail 1 -ErrorAction SilentlyContinue
"== service =="
$svc = Get-Service NbfcService -ErrorAction SilentlyContinue
if($svc){ $svc.Status } else { "not installed" }
