# FanManager.ps1 - GUI app entry. Self-elevates, runtime-compiles app-cs.txt (WinForms).
# ASCII only in this file. The C# lives in app-cs.txt (UTF-8, Chinese UI strings).
param([string]$Mode = "gui")
$ErrorActionPreference = 'Stop'

$logDir = Join-Path (Split-Path $PSScriptRoot -Parent) "logs"

function Write-AppError($err){
  New-Item -ItemType Directory -Force -Path $logDir | Out-Null
  [System.IO.File]::WriteAllText((Join-Path $logDir "app-error.txt"),
    (Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + "`r`n" + $err, [System.Text.Encoding]::UTF8)
}

try {
  # --- self-elevate (no prompt when UAC is set to auto-approve; otherwise a prompt appears) ---
  $id = [Security.Principal.WindowsIdentity]::GetCurrent()
  $princ = New-Object Security.Principal.WindowsPrincipal($id)
  if(-not $princ.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -WindowStyle Hidden `
      -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"",$Mode)
    exit
  }

  $cs = [System.IO.File]::ReadAllText("$PSScriptRoot\app-cs.txt")

  # early feedback while the runtime compile runs (the VBS hides the console, so without
  # this the user double-clicks and sees nothing for a few seconds). GUI mode only.
  $splash = $null
  if ($Mode -eq 'gui' -or $Mode -eq '') {
    try {
      Add-Type -AssemblyName 'System.Windows.Forms'
      Add-Type -AssemblyName 'System.Drawing'
      # application-wide WinForms settings MUST precede any control creation on this thread
      [System.Windows.Forms.Application]::EnableVisualStyles()
      [System.Windows.Forms.Application]::SetCompatibleTextRenderingDefault($false)
      $splash = New-Object System.Windows.Forms.Form
      $splash.FormBorderStyle = 'None'
      $splash.StartPosition = 'CenterScreen'
      $splash.TopMost = $true
      $splash.Size = New-Object System.Drawing.Size(400, 84)
      $splash.BackColor = [System.Drawing.Color]::FromArgb(20, 90, 160)
      $lbl = New-Object System.Windows.Forms.Label
      $lbl.Dock = 'Fill'
      $lbl.TextAlign = 'MiddleCenter'
      $lbl.ForeColor = [System.Drawing.Color]::White
      # the splash text (Chinese) is built from code points below: this file must stay pure ASCII
      $lbl.Text = -join [char[]](0x98CE,0x6247,0x7BA1,0x5BB6,0x542F,0x52A8,0x4E2D,0x2026,0x6B63,0x5728,0x7F16,0x8BD1,0x754C,0x9762,0xFF0C,0x7EA6,0x9700,0x51E0,0x79D2)
      $splash.Controls.Add($lbl)
      $splash.Show()
      [System.Windows.Forms.Application]::DoEvents()
    } catch { $splash = $null }
  }

  $sw = [Diagnostics.Stopwatch]::StartNew()
  Add-Type -TypeDefinition $cs -ReferencedAssemblies @('System.Windows.Forms','System.Drawing','System.ServiceProcess','System.Core','System.Xml','System')
  $sw.Stop()
  if ($splash -ne $null) { try { $splash.Close() } catch { } }
  try { [System.IO.File]::AppendAllText((Join-Path $logDir 'app.log'),
    (Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + "  startup compile: " + $sw.ElapsedMilliseconds + " ms`r`n",
    [System.Text.Encoding]::UTF8) } catch { }

  [FanManager.Config]::Init((Split-Path $PSScriptRoot -Parent))
  switch($Mode){
    'selftest'   { [FanManager.SelfTest]::CliRun() }
    'screenshot' { [FanManager.App]::RunScreenshot() }
    default      { [FanManager.App]::Run() }
  }
} catch {
  Write-AppError $_.ToString()
  throw
}
