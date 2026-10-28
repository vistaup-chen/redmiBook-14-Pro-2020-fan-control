# make-shortcut.ps1 - create a desktop shortcut pointing at the portable VBS launcher
$desktop = [Environment]::GetFolderPath("Desktop")
$vbs = (Resolve-Path "$PSScriptRoot\启动风扇管家.vbs").Path
$sh = New-Object -ComObject WScript.Shell
$lnk = $sh.CreateShortcut("$desktop\风扇管家.lnk")
$lnk.TargetPath = "$env:SystemRoot\System32\wscript.exe"
$lnk.Arguments = '"' + $vbs + '"'
$lnk.WorkingDirectory = $PSScriptRoot
$lnk.IconLocation = "$env:SystemRoot\System32\imageres.dll,109"
$lnk.Description = "RedmiBook FanManager"
$lnk.Save()
"shortcut exists: " + (Test-Path "$desktop\风扇管家.lnk")
