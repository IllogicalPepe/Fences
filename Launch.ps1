$ErrorActionPreference = 'Stop'
$root = 'C:\Users\Jasper\apps\Fences'
$project = Join-Path $root 'src\FenceDesk.Wpf\FenceDesk.Wpf.csproj'
$publish = Join-Path $root 'dist\dev-publish'
$install = Join-Path $env:LOCALAPPDATA 'Programs\FenceDesk'

Write-Host '==> Building FenceDesk (Release, win-x64)...'
& dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

Write-Host '==> Stopping FenceDesk...'
Get-Process -Name FenceDesk -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

if (-not (Test-Path $install)) {
    New-Item -ItemType Directory -Path $install -Force | Out-Null
}

Write-Host "==> Updating installed copy at $install ..."
$exclude = @('unins000.exe', 'unins000.dat', 'Uninstall.bat', 'Uninstall.ps1')
Get-ChildItem $publish -Recurse -File | ForEach-Object {
    if ($exclude -contains $_.Name) { return }
    $rel = $_.FullName.Substring($publish.Length).TrimStart('\')
    $dest = Join-Path $install $rel
    $destDir = Split-Path $dest -Parent
    if (-not (Test-Path $destDir)) {
        New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    }
    Copy-Item $_.FullName $dest -Force
}

Write-Host '==> Starting FenceDesk...'
Start-Process (Join-Path $install 'FenceDesk.exe')
Start-Sleep -Seconds 2
$p = Get-Process -Name FenceDesk -ErrorAction SilentlyContinue
if ($p) {
    Write-Host "Running PID $($p.Id)"
} else {
    Write-Host 'WARNING: process not found after start'
}
