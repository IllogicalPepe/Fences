@echo off
setlocal
cd /d "%~dp0"
echo Building share package (self-contained win-x64)...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Publish-Share.ps1" %*
if errorlevel 1 (
  echo.
  echo Publish failed.
  pause
  exit /b 1
)
echo.
echo Open the dist folder?
explorer "%~dp0dist"
endlocal
