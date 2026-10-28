# install-auto.ps1 - manage ZFanAutoMode scheduled task via ScheduledTasks module
param([string]$Action = "enable")  # create|enable|disable|status
$tn = "ZFanAutoMode"
switch($Action){
  "create" {
    $ta = New-ScheduledTaskAction -Execute 'powershell.exe' `
      -Argument '-NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot\..\auto-mode.ps1"'
    $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) `
      -RepetitionInterval (New-TimeSpan -Minutes 1) -RepetitionDuration (New-TimeSpan -Days 3650)
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -DontStopIfGoingOnBatteries -AllowStartIfOnBatteries
    Register-ScheduledTask -TaskName $tn -Action $ta -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
    Disable-ScheduledTask -TaskName $tn | Out-Null
    "task created (DISABLED by default)"
  }
  "enable" {
    $t = Get-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue
    if(-not $t){ & $PSCommandPath -Action create | Out-Null }
    Enable-ScheduledTask -TaskName $tn | Out-Null
    "auto mode ENABLED (watcher runs every minute)"
  }
  "disable" {
    Disable-ScheduledTask -TaskName $tn | Out-Null
    "auto mode DISABLED"
  }
  "status" {
    $t = Get-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue
    if($t){ "state: " + $t.State } else { "task not created" }
  }
}
