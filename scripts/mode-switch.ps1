# mode-switch.ps1 - one script to rule all modes. Usage (elevated):
#   mode-switch.ps1 quiet|balanced|performance|dnd|restore
# ASCII only. All exes called via PS & operator (no cmd quoting issues).
param([string]$Mode = "quiet")

$nbfc = "C:\Program Files (x86)\NoteBook FanControl\nbfc.exe"
$ec   = "C:\Program Files (x86)\NoteBook FanControl\ec-probe.exe"
$backupDir = Join-Path (Split-Path $PSScriptRoot -Parent) "data"   # repo-root\data, shared with the UI
$msrBackup = Join-Path $backupDir "msr-610-original.txt"
# msr-lib loads NBFC's OpenHardwareMonitorLib at dot-source time; restore must still work when NBFC is gone
if($Mode -ne "restore" -or (Test-Path $msrBackup)){ . "$PSScriptRoot\lib\msr-lib.ps1" }

$plans = @{
  quiet       = "9a10e4a1-1111-4b7a-8c0d-000000000001"
  balanced    = "9a10e4a1-1111-4b7a-8c0d-000000000002"
  performance = "9a10e4a1-1111-4b7a-8c0d-000000000003"
  dnd         = "9a10e4a1-1111-4b7a-8c0d-000000000004"
  restore     = "a1841308-3541-4fab-bc81-f71556f20b4a"
}
$configs = @{
  quiet       = "RedmiBook 14 2020 Silent"
  balanced    = "RedmiBook 14 2020 Balanced"
  performance = "RedmiBook 14 2020 Performance"
  dnd         = "RedmiBook 14 2020 DoNotDisturb"
}

  if($Mode -eq "restore"){
    "== RESTORE: stop NBFC control (EC reverts to auto) =="
    & $nbfc stop 2>&1
    "== power plan -> system default =="
    powercfg /setactive $plans[$Mode]
    $orig = $msrBackup
    if(Test-Path $orig){
      $line = (Get-Content $orig | Select-Object -First 1).Trim()
      $m = [regex]::Match($line, '0x([0-9A-Fa-f]{16})')
      if($m.Success){
        "== restore MSR 0x610 to original =="
        Set-Msr 0x610 ([Convert]::ToUInt64($m.Groups[1].Value, 16))
        "0x{0:X16}" -f (Get-Msr 0x610)
      }
    }
  } else {
    $cfg = $configs[$Mode]
    if(-not $cfg){ "unknown mode: $Mode"; exit 2 }
    "== power plan =="
    powercfg /setactive $plans[$Mode]
    "== NBFC config -> $cfg =="
    & $nbfc config -s $cfg 2>&1
    Start-Sleep -Seconds 2
    & $nbfc start 2>&1
    if($Mode -eq "performance"){
      "== unlock PL1 28W (MSR 0x610) =="
      $val = Get-Msr 0x610
      New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
      $origPath = $msrBackup
      if(-not (Test-Path $origPath)){ ("0x{0:X16}" -f $val) | Out-File $origPath -Encoding ascii }
      # clear bits 0-14, set PL1=28W (224 x 1/8W), keep enable(bit15)/clamp(bit16)
      $b = [BitConverter]::GetBytes([UInt64]$val)
      $low = [BitConverter]::ToUInt32($b, 0)
      $high = [BitConverter]::ToUInt32($b, 4)
      $low = ($low -band [UInt32]4294934528) -bor [UInt32]224 -bor [UInt32]32768
      $new = ([UInt64]$low) + ([UInt64]$high) * 4294967296
      "orig=0x{0:X16}  new=0x{1:X16}" -f $val, $new
      Set-Msr 0x610 $new
      Start-Sleep -Milliseconds 500
      "readback=0x{0:X16}" -f (Get-Msr 0x610)
    } else {
      # leaving performance mode: restore original PL if an override is active
      New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
      $origPath = $msrBackup
      if(Test-Path $origPath){
        $line = (Get-Content $origPath | Select-Object -First 1).Trim()
        $m = [regex]::Match($line, '0x([0-9A-Fa-f]{16})')
        if($m.Success){
          $orig = [Convert]::ToUInt64($m.Groups[1].Value, 16)
          $cur = Get-Msr 0x610
          if($cur -ne $orig){
            "== restore PL1 to original 25W =="
            Set-Msr 0x610 $orig
            "readback=0x{0:X16}" -f (Get-Msr 0x610)
          }
        }
      }
    }
  }

"== final status =="
powercfg /getactivescheme
& $nbfc status 2>&1
"EC mode(147) / duty(148) / feedback(149):"
& $ec read 147 2>&1
& $ec read 148 2>&1
& $ec read 149 2>&1
"== done =="
