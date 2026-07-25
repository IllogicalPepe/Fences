@echo off
setlocal
cd /d "%~dp0"

set "INSTALL=%LOCALAPPDATA%\Programs\FenceDesk"
set "PUBLISH=%~dp0dist\dev-publish"
set "PROJECT=%~dp0src\FenceDesk.Wpf\FenceDesk.Wpf.csproj"

echo ==> Building FenceDesk (Release, win-x64)...
dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained true -o "%PUBLISH%" --nologo -v q
if errorlevel 1 (
  echo Build failed.
  exit /b 1
)

echo ==> Stopping running FenceDesk...
taskkill /IM FenceDesk.exe /F >nul 2>&1
ping -n 2 127.0.0.1 >nul

if not exist "%INSTALL%" mkdir "%INSTALL%"

echo ==> Updating install:
echo     %INSTALL%
robocopy "%PUBLISH%" "%INSTALL%" /E /NFL /NDL /NJH /NJS /nc /ns /np /XF unins000.exe unins000.dat Uninstall.bat Uninstall.ps1
if errorlevel 8 (
  echo Deploy failed.
  exit /b 1
)

echo ==> Starting FenceDesk...
start "" "%INSTALL%\FenceDesk.exe"
echo Done.
endlocal
