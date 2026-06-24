@echo off
REM Double-click this to build mojoCAD and install it into AutoCAD.
REM It runs the PowerShell installer with the execution policy bypassed for this
REM process only (it does not change any machine setting).
echo.
echo  mojoCAD installer
echo  =================
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
echo.
pause
