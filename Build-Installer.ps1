#Requires -Version 5.1
<#
  Build a Windows installer (Inno Setup):
    dist\FenceDesk-Setup-2.1.2.exe

  Steps:
    1. Publish self-contained win-x64 app to dist\publish
    2. Compile installer\FenceDesk.iss with ISCC

  Requires:
    - .NET 8 SDK
    - Inno Setup 6 (https://jrsoftware.org/isinfo.php)
#>
param(
    [ValidateSet("win-x64", "win-arm64", "win-x86")]
    [string]$Runtime = "win-x64",
    [string]$Version = "2.1.2",
    [switch]$SkipPublish,
    [string]$IsccPath = ""
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Project = Join-Path $Root "src\FenceDesk.Wpf\FenceDesk.Wpf.csproj"
$PublishOut = Join-Path $Root "dist\publish"
$Iss = Join-Path $Root "installer\FenceDesk.iss"
$OutDir = Join-Path $Root "dist"
$SetupExe = Join-Path $OutDir "FenceDesk-Setup-$Version.exe"

function Find-ISCC([string]$Hint) {
    if ($Hint -and (Test-Path -LiteralPath $Hint)) { return (Resolve-Path $Hint).Path }

    $cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $candidates = @(
        Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"
        Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 7\ISCC.exe"
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
        "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe"
        "$env:ProgramFiles\Inno Setup 7\ISCC.exe"
        "${env:ProgramFiles(x86)}\Inno Setup 5\ISCC.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c) { return $c }
    }
    return $null
}

if (-not (Test-Path -LiteralPath $Project)) { throw "Project not found: $Project" }
if (-not (Test-Path -LiteralPath $Iss)) { throw "Inno script not found: $Iss" }

$iscc = Find-ISCC $IsccPath
if (-not $iscc) {
    throw @"
Inno Setup compiler (ISCC.exe) not found.
Install Inno Setup 6 from https://jrsoftware.org/isinfo.php
Or pass -IsccPath 'C:\Path\To\ISCC.exe'
"@
}

if (-not $SkipPublish) {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        foreach ($c in @("$env:ProgramFiles\dotnet\dotnet.exe", "${env:ProgramFiles(x86)}\dotnet\dotnet.exe")) {
            if (Test-Path $c) { $dotnet = $c; break }
        }
    }
    if (-not $dotnet) { throw "dotnet SDK not found. Install .NET 8 SDK." }
    $dotnetExe = if ($dotnet -is [string]) { $dotnet } else { $dotnet.Source }

    Write-Host "==> Publishing self-contained $Runtime (v$Version) ..."
    if (Test-Path $PublishOut) { Remove-Item $PublishOut -Recurse -Force }
    New-Item -ItemType Directory -Path $PublishOut -Force | Out-Null

    & $dotnetExe publish $Project `
        -c Release `
        -r $Runtime `
        --self-contained true `
        -p:Version=$Version `
        -p:PublishSingleFile=false `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $PublishOut

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
}
else {
    Write-Host "==> Skipping publish (using existing dist\publish)"
}

$exe = Join-Path $PublishOut "FenceDesk.exe"
if (-not (Test-Path -LiteralPath $exe)) {
    throw "FenceDesk.exe missing in $PublishOut - publish first or omit -SkipPublish"
}

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

Write-Host "==> Compiling Inno Setup installer ..."
Write-Host "    ISCC: $iscc"
& $iscc `
    "/DMyAppVersion=$Version" `
    "/DSourceDir=$PublishOut" `
    "/DOutputDir=$OutDir" `
    $Iss

if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

# Inno names output from OutputBaseFilename; ensure expected path exists
if (-not (Test-Path -LiteralPath $SetupExe)) {
    $found = Get-ChildItem -Path $OutDir -Filter "FenceDesk-Setup-*.exe" -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($found) { $SetupExe = $found.FullName }
}

if (-not (Test-Path -LiteralPath $SetupExe)) {
    throw "Installer exe not found under $OutDir"
}

$len = (Get-Item -LiteralPath $SetupExe).Length
Write-Host ""
Write-Host "Done."
Write-Host "Installer: $SetupExe"
Write-Host ("Size:      {0:N1} MB" -f ($len / 1MB))
Write-Host ""
Write-Host "Share that .exe with users - double-click to install (no admin needed)."
