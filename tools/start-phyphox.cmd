@echo off
setlocal
cd /d "%~dp0"
start "" "%~dp0phyphox.exe" --data-dir "%~dp0data" %*
if errorlevel 1 (
  echo.
  echo Service failed. Keep the full portable folder and check permissions.
  pause
)
