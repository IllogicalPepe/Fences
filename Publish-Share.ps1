#Requires -Version 5.1
<#
  Builds a friend-ready share package:
    dist\FenceDesk-2.0.0-win-x64\
    dist\FenceDesk-2.0.0-win-x64.zip

  Self-contained — friends do NOT need the .NET SDK or runtime installed.
#>
param(
    [ValidateSet("win-x64", "win-arm64", "win-x86")]
    [string]$Runtime = "win-x64",
    [string]$Version = "2.0.0"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Project = Join-Path $Root "src\FenceDesk.Wpf\FenceDesk.Wpf.csproj"
$PublishOut = Join-Path $Root "dist\publish"
$PackageName = "FenceDesk-$Version-$Runtime"
$PackageDir = Join-Path $Root "dist\$PackageName"
$ZipPath = Join-Path $Root "dist\$PackageName.zip"

if (-not (Test-Path $Project)) { throw "Project not found: $Project" }

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    $candidates = @(
        "$env:ProgramFiles\dotnet\dotnet.exe",
        "${env:ProgramFiles(x86)}\dotnet\dotnet.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { $dotnet = $c; break }
    }
}
if (-not $dotnet) { throw "dotnet SDK not found. Install .NET 8 SDK to publish." }
$dotnetExe = if ($dotnet -is [string]) { $dotnet } else { $dotnet.Source }

Write-Host "==> Publishing self-contained $Runtime ..."
if (Test-Path $PublishOut) { Remove-Item $PublishOut -Recurse -Force }
& $dotnetExe publish $Project `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $PublishOut

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
if (-not (Test-Path (Join-Path $PublishOut "FenceDesk.exe"))) {
    throw "Publish succeeded but FenceDesk.exe missing in $PublishOut"
}

Write-Host "==> Assembling package $PackageName ..."
if (Test-Path $PackageDir) { Remove-Item $PackageDir -Recurse -Force }
New-Item -ItemType Directory -Path $PackageDir -Force | Out-Null

# App files
Copy-Item -Path (Join-Path $PublishOut "*") -Destination $PackageDir -Recurse -Force

# Installer scripts
Copy-Item (Join-Path $Root "Install.ps1") $PackageDir -Force
Copy-Item (Join-Path $Root "Install.bat") $PackageDir -Force
Copy-Item (Join-Path $Root "Uninstall.ps1") $PackageDir -Force
if (Test-Path (Join-Path $Root "Uninstall.bat")) {
    Copy-Item (Join-Path $Root "Uninstall.bat") $PackageDir -Force
}

$howTo = @"
FenceDesk $Version — how to install
===================================

REQUIREMENTS
  - Windows 10 or Windows 11 (64-bit)
  - No .NET install needed (this package is self-contained)

EASY INSTALL (recommended)
  1. Unzip this folder anywhere (Downloads is fine)
  2. Double-click  Install.bat
  3. If Windows SmartScreen appears: More info → Run anyway
  4. Look for the FenceDesk tray icon near the clock
  5. Desktop + Start Menu shortcuts are created automatically

PORTABLE (no install)
  1. Unzip
  2. Double-click  FenceDesk.exe
  (Settings still save under %LOCALAPPDATA%\FenceDesk\)

UNINSTALL
  - Start Menu → FenceDesk → Uninstall FenceDesk
  - Or run Uninstall.bat
  - Your fence layout is kept in %LOCALAPPDATA%\FenceDesk\ unless you delete it

TIPS
  - Right-click a fence for colors, opacity, groups, portals, etc.
  - Double-click empty desktop wallpaper to hide/show fences
  - Left-click the tray icon for the control panel

"@
Set-Content -Path (Join-Path $PackageDir "HOW-TO-INSTALL.txt") -Value $howTo -Encoding UTF8

Write-Host "==> Creating zip ..."
if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
Compress-Archive -Path $PackageDir -DestinationPath $ZipPath -CompressionLevel Optimal

$zipItem = Get-Item $ZipPath
Write-Host ""
Write-Host "Done!"
Write-Host "  Folder : $PackageDir"
Write-Host "  Zip    : $ZipPath  ($([math]::Round($zipItem.Length/1MB, 1)) MB)"
Write-Host ""
Write-Host "Send your friends the ZIP. They unzip and double-click Install.bat"
