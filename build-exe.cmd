@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\build-exe.ps1" %*
set "build_exit=%ERRORLEVEL%"
if not "%build_exit%"=="0" echo Build failed. See the error above.
pause
exit /b %build_exit%
