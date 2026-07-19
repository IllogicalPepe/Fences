@echo off
setlocal
cd /d "%~dp0src\FenceDesk.Wpf"
dotnet build -c Release
if errorlevel 1 (
  echo Build failed.
  pause
  exit /b 1
)
echo.
echo Built: bin\Release\net8.0-windows\FenceDesk.exe
endlocal
