param([int]$Tab = 0)
$ErrorActionPreference = 'Stop'
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
if(-not (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
  Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"",$Tab); exit
}
$cs = [System.IO.File]::ReadAllText("C:\Users\Administrator\Desktop\fan\app\app-cs.txt")
Add-Type -TypeDefinition $cs -ReferencedAssemblies @('System.Windows.Forms','System.Drawing','System.ServiceProcess','System.Core','System.Xml','System')
[FanManager.App]::SetShotTab($Tab)
[FanManager.App]::RunScreenshot()
