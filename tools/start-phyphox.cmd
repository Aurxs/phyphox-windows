@echo off
setlocal
cd /d "%~dp0"
"%~dp0Phyphox.Server.exe" --data-dir "%~dp0data" %*
if errorlevel 1 (
  echo.
  echo Service failed. Keep the full portable folder and check permissions.
  pause
)
