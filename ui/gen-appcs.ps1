# gen-appcs.ps1 - regenerate app-cs.txt from Program.cs (strip the Main-entry class,
# the runtime-compiled variant must not contain static Program with [STAThread]).
# Usage: powershell -ExecutionPolicy Bypass -File gen-appcs.ps1
$ErrorActionPreference = 'Stop'
$src = [IO.File]::ReadAllText("$PSScriptRoot\Program.cs")
$gen = $src.Substring(0, $src.IndexOf("static class Program")) + $src.Substring($src.IndexOf("public static class Config"))
[IO.File]::WriteAllText("$PSScriptRoot\app-cs.txt", $gen)
Write-Output ("app-cs.txt regenerated: {0} bytes" -f (Get-Item "$PSScriptRoot\app-cs.txt").Length)
