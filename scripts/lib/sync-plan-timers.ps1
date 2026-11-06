# sync-plan-timers.ps1 - entry point used by the GUI and by manual troubleshooting.
# Mirrors the user's own sleep/hibernate/display timers into one Z-fan power plan so a
# mode switch never changes them. Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File sync-plan-timers.ps1 -Target <guid> [-DataDir <dir>]
#   powershell ... -PrintBaseline          # prints "baseline=<guid>" and exits
param(
  [string]$Target = "",
  [string]$DataDir = "",
  [switch]$PrintBaseline
)
. "$PSScriptRoot\power-settings.ps1"
if(-not $DataDir){ $DataDir = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) "data") }
Sync-PlanUserTimers -Target $Target -DataDir $DataDir -PrintBaseline:$PrintBaseline
