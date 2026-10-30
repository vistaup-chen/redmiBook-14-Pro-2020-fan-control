@echo off
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0lib\run-elevated.ps1" -Script "%~dp0emergency-fan-restore.ps1" -OutFile "%~dp0..\logs\emergency-last.txt" -TimeoutSec 60
type "%~dp0..\logs\emergency-last.txt"
pause
