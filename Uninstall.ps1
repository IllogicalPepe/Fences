#Requires -Version 5.1
$ErrorActionPreference = 'SilentlyContinue'

$InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\FenceDesk'

Get-Process FenceDesk -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

# Autostart
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'FenceDesk' -ErrorAction SilentlyContinue

# Shortcuts
$desk = [Environment]::GetFolderPath('Desktop')
Remove-Item (Join-Path $desk 'FenceDesk.lnk') -Force -ErrorAction SilentlyContinue
$sm = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\FenceDesk'
Remove-Item $sm -Recurse -Force -ErrorAction SilentlyContinue

# Apps & Features unregister entry
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\FenceDesk' -Recurse -Force -ErrorAction SilentlyContinue

if (Test-Path $InstallDir) {
    Remove-Item $InstallDir -Recurse -Force
}

Write-Host 'FenceDesk uninstalled.'
Write-Host 'Layout data under %LOCALAPPDATA%\FenceDesk was kept (delete that folder to wipe settings).'
