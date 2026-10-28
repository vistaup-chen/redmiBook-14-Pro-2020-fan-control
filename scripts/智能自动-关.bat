@echo off
chcp 65001 >nul
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0libun-elevated.ps1" -Script "%~dp0lib\install-auto.ps1" -ScriptArgs "disable" -OutFile "%~dp0..\logsuto-last.txt" -TimeoutSec 60
type "%~dp0..\logsuto-last.txt"
pause
