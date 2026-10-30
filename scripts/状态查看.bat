@echo off
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0lib\run-elevated.ps1" -Script "%~dp0status.ps1" -OutFile "%~dp0..\logs\status-last.txt" -TimeoutSec 60
type "%~dp0..\logs\status-last.txt"
pause
