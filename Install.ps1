#Requires -Version 5.1
<#
  FenceDesk installer (per-user, no admin required).

  Works in two modes:
  1) Packaged share (recommended): Install.ps1 sits next to FenceDesk.exe
  2) Dev tree: builds from source if needed, then copies Release output
#>
param(
    [switch]$Silent,
    [switch]$StartWithWindows
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$InstallDir = Join-Path $env:LOCALAPPDATA "Programs\FenceDesk"

function Write-Info([string]$msg) {
    if (-not $Silent) { Write-Host $msg }
}

# --- Resolve source folder that contains FenceDesk.exe ---
$SourceDir = $null
$packagedExe = Join-Path $Root "FenceDesk.exe"
$devExe = Join-Path $Root "src\FenceDesk.Wpf\bin\Release\net8.0-windows\FenceDesk.exe"
$devDir = Join-Path $Root "src\FenceDesk.Wpf\bin\Release\net8.0-windows"
$publishDir = Join-Path $Root "dist\publish"

if (Test-Path -LiteralPath $packagedExe) {
    $SourceDir = $Root
    Write-Info "Installing from package folder: $SourceDir"
}
elseif (Test-Path -LiteralPath (Join-Path $publishDir "FenceDesk.exe")) {
    $SourceDir = $publishDir
    Write-Info "Installing from publish output: $SourceDir"
}
else {
    if (-not (Test-Path -LiteralPath $devExe)) {
        Write-Info "Building FenceDesk (Release)..."
        $project = Join-Path $Root "src\FenceDesk.Wpf\FenceDesk.Wpf.csproj"
        if (-not (Test-Path $project)) {
            throw "FenceDesk.exe not found next to Install.ps1, and project not found at $project"
        }
        & dotnet build $project -c Release
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }
    }
    if (-not (Test-Path -LiteralPath $devExe)) {
        throw "Executable not found: $devExe"
    }
    $SourceDir = $devDir
    Write-Info "Installing from build output: $SourceDir"
}

$SourceExe = Join-Path $SourceDir "FenceDesk.exe"
if (-not (Test-Path -LiteralPath $SourceExe)) {
    throw "FenceDesk.exe missing in $SourceDir"
}

# Stop running instance so files can be overwritten
Get-Process -Name "FenceDesk" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400

Write-Info "Installing to $InstallDir"
if (Test-Path -LiteralPath $InstallDir) {
    Remove-Item -LiteralPath $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

# Copy app files (skip installer scripts if packaging from same folder)
Get-ChildItem -LiteralPath $SourceDir -Force | ForEach-Object {
    $name = $_.Name
    if ($name -in @('Install.ps1', 'Install.bat', 'Uninstall.ps1', 'Uninstall.bat', 'README-SHARE.txt', 'HOW-TO-INSTALL.txt')) {
        return
    }
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $InstallDir $name) -Recurse -Force
}

# Always drop uninstall helpers into install dir
$uninstallPs1 = Join-Path $Root "Uninstall.ps1"
if (Test-Path $uninstallPs1) {
    Copy-Item $uninstallPs1 (Join-Path $InstallDir "Uninstall.ps1") -Force
}
$uninstallBat = @"
@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall.ps1"
pause
"@
Set-Content -Path (Join-Path $InstallDir "Uninstall.bat") -Value $uninstallBat -Encoding ASCII

$exe = Join-Path $InstallDir "FenceDesk.exe"
if (-not (Test-Path $exe)) { throw "Install failed — FenceDesk.exe not copied." }

$Wsh = New-Object -ComObject WScript.Shell
$ico = Join-Path $InstallDir "Assets\FenceDesk.ico"
if (-not (Test-Path $ico)) { $ico = $exe }

# Start Menu
$startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\FenceDesk"
New-Item -ItemType Directory -Path $startMenu -Force | Out-Null
$lnk = $Wsh.CreateShortcut((Join-Path $startMenu "FenceDesk.lnk"))
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $InstallDir
$lnk.Description = "FenceDesk — desktop fence organizer"
$lnk.IconLocation = $ico
$lnk.Save()

$ulnk = $Wsh.CreateShortcut((Join-Path $startMenu "Uninstall FenceDesk.lnk"))
$ulnk.TargetPath = "powershell.exe"
$ulnk.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $InstallDir 'Uninstall.ps1')`""
$ulnk.WorkingDirectory = $InstallDir
$ulnk.Description = "Uninstall FenceDesk"
$ulnk.Save()

# Desktop shortcut
$desk = [Environment]::GetFolderPath("Desktop")
$dlnk = $Wsh.CreateShortcut((Join-Path $desk "FenceDesk.lnk"))
$dlnk.TargetPath = $exe
$dlnk.WorkingDirectory = $InstallDir
$dlnk.Description = "FenceDesk"
$dlnk.IconLocation = $ico
$dlnk.Save()

# Optional autostart
if ($StartWithWindows) {
    $key = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
    Set-ItemProperty -Path $key -Name "FenceDesk" -Value "`"$exe`"" -Type String -Force
    Write-Info "Start with Windows: enabled"
}

# Per-user Apps & Features style unregister entry (no admin)
try {
    $unreg = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\FenceDesk"
    New-Item -Path $unreg -Force | Out-Null
    Set-ItemProperty -Path $unreg -Name "DisplayName" -Value "FenceDesk"
    Set-ItemProperty -Path $unreg -Name "DisplayVersion" -Value "2.0.0"
    Set-ItemProperty -Path $unreg -Name "Publisher" -Value "FenceDesk"
    Set-ItemProperty -Path $unreg -Name "InstallLocation" -Value $InstallDir
    Set-ItemProperty -Path $unreg -Name "UninstallString" -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $InstallDir 'Uninstall.ps1')`""
    Set-ItemProperty -Path $unreg -Name "DisplayIcon" -Value $exe
    Set-ItemProperty -Path $unreg -Name "NoModify" -Value 1 -Type DWord
    Set-ItemProperty -Path $unreg -Name "NoRepair" -Value 1 -Type DWord
}
catch { /* ignore */ }

Write-Info ""
Write-Info "Installed to: $InstallDir"
Write-Info "Shortcuts: Desktop + Start Menu"
Write-Info "Layout data: %LOCALAPPDATA%\FenceDesk\"
Write-Info ""
Write-Info "Done. Look for the FenceDesk tray icon near the clock."

if (-not $Silent) {
    Start-Process -FilePath $exe
}
