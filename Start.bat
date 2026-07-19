@echo off
setlocal
REM Prefer C# WPF build (stable). Fall back to WinUI path if present.
set "EXE=%~dp0src\FenceDesk.Wpf\bin\Release\net8.0-windows\FenceDesk.exe"
if not exist "%EXE%" set "EXE=%~dp0src\FenceDesk.Wpf\bin\Debug\net8.0-windows\FenceDesk.exe"
if not exist "%EXE%" (
  echo Building FenceDesk (C# WPF Release)...
  pushd "%~dp0src\FenceDesk.Wpf"
  call dotnet build -c Release
  popd
  set "EXE=%~dp0src\FenceDesk.Wpf\bin\Release\net8.0-windows\FenceDesk.exe"
)
if not exist "%EXE%" (
  echo Build failed or executable missing.
  pause
  exit /b 1
)
start "" "%EXE%"
endlocal
