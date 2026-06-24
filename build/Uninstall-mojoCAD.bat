@echo off
REM Double-click this to remove the installed mojoCAD bundle from AutoCAD.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -Uninstall
echo.
pause
