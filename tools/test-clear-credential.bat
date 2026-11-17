@echo off
rem Launcher for test-clear-credential.ps1 (the script elevates itself via UAC).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0test-clear-credential.ps1" %*
