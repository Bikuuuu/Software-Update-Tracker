@echo off
rem Starts Update Tracker without a console window. Keep this file next to UpdateTracker.ps1.
powershell.exe -NoProfile -Command "Get-ChildItem -LiteralPath '%~dp0' | Unblock-File" >nul 2>&1
start "" /min powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0UpdateTracker.ps1"
