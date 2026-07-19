#Requires -Version 5.1
<#
.SYNOPSIS
  Installer for FenceDesk (desktop fence organizer).
#>
[CmdletBinding()]
param(
    [switch]$Silent,
    [switch]$NoDesktop,
    [switch]$NoStartMenu,
    [switch]$StartWithWindows,
    [switch]$NoLaunch,
    [string]$InstallDir = ""
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms -ErrorAction SilentlyContinue

$AppName   = 'FenceDesk'
$AppId     = 'FenceDesk'
$SourceDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $InstallDir) {
    $InstallDir = Join-Path $env:LOCALAPPDATA "Programs\$AppId"
}

$AppFiles = @(
    'FenceDesk.ps1'
    'Start.vbs'
    'Start.bat'
    'README.md'
    'Uninstall.ps1'
)
# Assets (icon) copied separately

function Write-InstallLog {
    param([string]$Message)
    Write-Host ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $Message)
}

function New-Shortcut {
    param(
        [string]$Path,
        [string]$TargetPath,
        [string]$Arguments = '',
        [string]$WorkingDirectory = '',
        [string]$Description = '',
        [string]$IconLocation = ''
    )
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    $wsh = New-Object -ComObject WScript.Shell
    $lnk = $wsh.CreateShortcut($Path)
    $lnk.TargetPath = $TargetPath
    if ($Arguments) { $lnk.Arguments = $Arguments }
    if ($WorkingDirectory) { $lnk.WorkingDirectory = $WorkingDirectory }
    if ($Description) { $lnk.Description = $Description }
    if ($IconLocation) { $lnk.IconLocation = $IconLocation }
    $lnk.WindowStyle = 7
    $lnk.Save()
}

Write-InstallLog "Installing $AppName to $InstallDir"

if (-not (Test-Path -LiteralPath $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}
$modDest = Join-Path $InstallDir 'Modules'
if (-not (Test-Path -LiteralPath $modDest)) {
    New-Item -ItemType Directory -Path $modDest -Force | Out-Null
}

foreach ($f in $AppFiles) {
    $src = Join-Path $SourceDir $f
    if (Test-Path -LiteralPath $src) {
        Copy-Item -LiteralPath $src -Destination (Join-Path $InstallDir $f) -Force
        Write-InstallLog "Copied $f"
    }
}

Get-ChildItem -LiteralPath (Join-Path $SourceDir 'Modules') -Filter '*.ps1' -ErrorAction SilentlyContinue | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $modDest $_.Name) -Force
    Write-InstallLog "Copied Modules\$($_.Name)"
}
# Ensure new modules (e.g. DesktopIcons.ps1) are always included when present

if (Test-Path -LiteralPath (Join-Path $SourceDir 'Assets')) {
    $assetsDest = Join-Path $InstallDir 'Assets'
    if (-not (Test-Path $assetsDest)) { New-Item -ItemType Directory -Path $assetsDest -Force | Out-Null }
    Copy-Item -Path (Join-Path $SourceDir 'Assets\*') -Destination $assetsDest -Recurse -Force -ErrorAction SilentlyContinue
}

$vbs = Join-Path $InstallDir 'Start.vbs'
$wscript = Join-Path $env:SystemRoot 'System32\wscript.exe'
$ico = Join-Path $InstallDir 'Assets\FenceDesk.ico'
# Generate icon if missing (runs AppIcon helpers lightly)
if (-not (Test-Path -LiteralPath $ico)) {
    try {
        $gen = Join-Path $InstallDir 'Modules\AppIcon.ps1'
        if (Test-Path $gen) {
            $script:AppDir = $InstallDir
            function Write-FenceLog { param($m) Write-InstallLog $m }
            . $gen
            $null = Get-FenceDeskIcon -ForceRegenerate
        }
    }
    catch { Write-InstallLog "Icon gen skipped: $($_.Exception.Message)" }
}
$iconLoc = if (Test-Path -LiteralPath $ico) { "$ico,0" } else { '' }

if (-not $NoDesktop) {
    $desk = [Environment]::GetFolderPath('Desktop')
    $lnk = Join-Path $desk "$AppName.lnk"
    New-Shortcut -Path $lnk -TargetPath $wscript -Arguments "`"$vbs`"" -WorkingDirectory $InstallDir -Description $AppName -IconLocation $iconLoc
    Write-InstallLog "Desktop shortcut: $lnk"
}

if (-not $NoStartMenu) {
    $sm = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\FenceDesk'
    if (-not (Test-Path $sm)) { New-Item -ItemType Directory -Path $sm -Force | Out-Null }
    New-Shortcut -Path (Join-Path $sm "$AppName.lnk") -TargetPath $wscript -Arguments "`"$vbs`"" -WorkingDirectory $InstallDir -Description $AppName -IconLocation $iconLoc
    New-Shortcut -Path (Join-Path $sm "Uninstall $AppName.lnk") -TargetPath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -Arguments "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $InstallDir 'Uninstall.ps1')`"" -WorkingDirectory $InstallDir -Description "Uninstall $AppName"
    Write-InstallLog "Start Menu shortcuts created"
}

if ($StartWithWindows) {
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    Set-ItemProperty -Path $runKey -Name 'FenceDesk' -Value "wscript.exe `"$vbs`"" -Type String -Force
    Write-InstallLog 'Start with Windows enabled'
}

# Uninstall registry (per-user)
$unreg = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$AppId"
if (-not (Test-Path $unreg)) { New-Item -Path $unreg -Force | Out-Null }
Set-ItemProperty -Path $unreg -Name 'DisplayName' -Value $AppName
Set-ItemProperty -Path $unreg -Name 'Publisher' -Value 'FenceDesk'
Set-ItemProperty -Path $unreg -Name 'InstallLocation' -Value $InstallDir
Set-ItemProperty -Path $unreg -Name 'UninstallString' -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $InstallDir 'Uninstall.ps1')`""
Set-ItemProperty -Path $unreg -Name 'NoModify' -Value 1 -Type DWord
Set-ItemProperty -Path $unreg -Name 'NoRepair' -Value 1 -Type DWord
Set-ItemProperty -Path $unreg -Name 'DisplayVersion' -Value '1.0.0'

Write-InstallLog 'Install complete.'

if (-not $NoLaunch) {
    Start-Process -FilePath $wscript -ArgumentList "`"$vbs`""
    Write-InstallLog 'Launched FenceDesk.'
}

if (-not $Silent) {
    [System.Windows.Forms.MessageBox]::Show(
        "FenceDesk installed.`n`nLook for the tray icon near the clock.`nRight-click a fence for options; drop files to organize.",
        $AppName,
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Information
    ) | Out-Null
}
