# install-auto.ps1 - manage ZFanAutoMode scheduled task via ScheduledTasks module
param([string]$Action = "enable")  # create|enable|disable|status
$tn = "ZFanAutoMode"
switch($Action){
  "create" {
    # $PSScriptRoot must be expanded HERE: a single-quoted -Argument would store it
    # literally and the task would fail every run with "file does not exist"
    $autoScript = (Resolve-Path (Join-Path $PSScriptRoot '..\auto-mode.ps1')).Path
    $ta = New-ScheduledTaskAction -Execute 'powershell.exe' `
      -Argument ('-NoProfile -ExecutionPolicy Bypass -File "' + $autoScript + '"')
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
    # recreate a task left broken by the old single-quoted $PSScriptRoot argument
    $stale = $false
    if($t){ $stale = @($t.Actions | Where-Object { $_.Arguments -like '*$PSScriptRoot*' }).Count -gt 0 }
    if(-not $t -or $stale){ & $PSCommandPath -Action create | Out-Null }
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
