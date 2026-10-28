' FanManager launcher - double click; locates itself (ASCII only)
' NOTE: build the quoted path via Chr(34) because VBS uses "" to escape quotes.
Set fso = CreateObject("Scripting.FileSystemObject")
Set sh = CreateObject("Shell.Application")
dir = fso.GetParentFolderName(WScript.ScriptFullName)
q = Chr(34)
args = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File " & q & dir & "\FanManager.ps1" & q
sh.ShellExecute "powershell.exe", args, "", "runas", 0
