# make-shortcut.ps1 - create a desktop shortcut pointing at the portable VBS launcher
# ASCII only: PS 5.1 reads BOM-less files as ANSI, so Chinese literals here become mojibake paths
$name = -join ([char]0x98CE, [char]0x6247, [char]0x7BA1, [char]0x5BB6)   # shortcut name: feng shan guan jia
$vbsName = -join ([char]0x542F, [char]0x52A8, [char]0x98CE, [char]0x6247, [char]0x7BA1, [char]0x5BB6)   # launcher: qi dong feng shan guan jia
$desktop = [Environment]::GetFolderPath("Desktop")
$vbs = (Resolve-Path (Join-Path $PSScriptRoot ($vbsName + ".vbs"))).Path
$sh = New-Object -ComObject WScript.Shell
$lnkPath = Join-Path $desktop ($name + ".lnk")
$lnk = $sh.CreateShortcut($lnkPath)
$lnk.TargetPath = "$env:SystemRoot\System32\wscript.exe"
$lnk.Arguments = '"' + $vbs + '"'
$lnk.WorkingDirectory = $PSScriptRoot
$lnk.IconLocation = "$PSScriptRoot\app.ico,0"
$lnk.Description = "RedmiBook FanManager"
$lnk.Save()
"shortcut exists: " + (Test-Path $lnkPath)
