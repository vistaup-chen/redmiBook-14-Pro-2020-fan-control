# emergency-fan-restore.ps1 - force EC back to AUTO fan control (ASCII only)
$ec = "C:\Program Files (x86)\NoteBook FanControl\ec-probe.exe"
$nbfc = "C:\Program Files (x86)\NoteBook FanControl\nbfc.exe"
"== stop NBFC service control =="
cmd /c "`"$nbfc`" stop 2>&1"
"== write EC 0x93=0x04 (auto mode) =="
cmd /c "`"$ec`" write 0x93 0x04 2>&1"
Start-Sleep -Seconds 2
"== verify =="
cmd /c "`"$ec`" read 147 2>&1"
cmd /c "`"$ec`" read 148 2>&1"
cmd /c "`"$ec`" read 149 2>&1"
"if mode(147)=4 (0x04) the EC is back in AUTO. Fan is now firmware-controlled."
