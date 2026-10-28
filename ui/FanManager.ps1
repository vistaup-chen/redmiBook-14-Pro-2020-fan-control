# FanManager.ps1 - GUI app entry. Self-elevates, runtime-compiles app-cs.txt (WinForms).
# ASCII only in this file. The C# lives in app-cs.txt (UTF-8, Chinese UI strings).
param([string]$Mode = "gui")
$ErrorActionPreference = 'Stop'

# --- self-elevate (silent, ConsentPromptBehaviorAdmin=0 on this machine) ---
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$princ = New-Object Security.Principal.WindowsPrincipal($id)
if(-not $princ.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
  Start-Process -FilePath 'powershell.exe' -Verb RunAs -WindowStyle Hidden `
    -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"",$Mode)
  exit
}

try {
  $cs = [System.IO.File]::ReadAllText("$PSScriptRoot\app-cs.txt")
  Add-Type -TypeDefinition $cs -ReferencedAssemblies @('System.Windows.Forms','System.Drawing','System.ServiceProcess','System.Core','System.Xml','System')
} catch {
  $logDir = Join-Path (Split-Path $PSScriptRoot -Parent) "logs"; New-Item -ItemType Directory -Force -Path $logDir | Out-Null
  [System.IO.File]::WriteAllText((Join-Path $logDir "app-error.txt"),
    (Get-Date) + "`r`n" + $_.ToString(), [System.Text.Encoding]::UTF8)
  throw
}

[FanManager.Config]::Init((Split-Path $PSScriptRoot -Parent))
switch($Mode){
  'selftest'   { [FanManager.SelfTest]::CliRun() }
  'screenshot' { [FanManager.App]::RunScreenshot() }
  default      { [FanManager.App]::Run() }
}
