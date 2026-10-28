@echo off
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0libun-elevated.ps1" -Script "%~dp0mode-switch.ps1" -ScriptArgs "dnd" -OutFile "%~dp0..\logs\mode-last.txt" -TimeoutSec 120
type "%~dp0..\logs\mode-last.txt"
pause
