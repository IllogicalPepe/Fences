#Requires -Version 5.1
<#
.SYNOPSIS
  Uninstall FenceDesk.
#>
[CmdletBinding()]
param(
    [switch]$Silent
)

$ErrorActionPreference = 'Continue'
$AppName = 'FenceDesk'
$AppId = 'FenceDesk'

# Stop running instance
Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -and $_.CommandLine -match 'FenceDesk\.ps1' } |
    ForEach-Object {
        try { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue } catch { }
    }

Start-Sleep -Milliseconds 400

$InstallDir = Split-Path -Parent $MyInvocation.MyCommand.Path
# If running from source and also installed, prefer Programs path
$programs = Join-Path $env:LOCALAPPDATA "Programs\$AppId"
if ((Test-Path $programs) -and ($InstallDir -ne $programs)) {
    # uninstall the installed copy when invoked from elsewhere
}

# Remove autostart
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'FenceDesk' -ErrorAction SilentlyContinue

# Shortcuts
$deskLnk = Join-Path ([Environment]::GetFolderPath('Desktop')) "$AppName.lnk"
Remove-Item -LiteralPath $deskLnk -Force -ErrorAction SilentlyContinue

$sm = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\FenceDesk'
if (Test-Path $sm) {
    Remove-Item -LiteralPath $sm -Recurse -Force -ErrorAction SilentlyContinue
}

# Uninstall registry
Remove-Item -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$AppId" -Recurse -Force -ErrorAction SilentlyContinue

# App files (install location)
if (Test-Path -LiteralPath $programs) {
    Remove-Item -LiteralPath $programs -Recurse -Force -ErrorAction SilentlyContinue
}

# Restore any desktop icons we hid
try {
    $hiddenState = Join-Path $env:LOCALAPPDATA 'FenceDesk\hidden-desktop.json'
    if (Test-Path -LiteralPath $hiddenState) {
        $obj = Get-Content -LiteralPath $hiddenState -Raw -Encoding UTF8 | ConvertFrom-Json
        $desktops = @(
            [Environment]::GetFolderPath('Desktop')
            [Environment]::GetFolderPath('CommonDesktopDirectory')
        )
        foreach ($p in @($obj.paths)) {
            if (-not $p) { continue }
            $name = [System.IO.Path]::GetFileName($p)
            foreach ($desk in $desktops) {
                if (-not $desk) { continue }
                $candidate = Join-Path $desk $name
                if (Test-Path -LiteralPath $candidate) {
                    try {
                        $item = Get-Item -LiteralPath $candidate -Force
                        if ($item.Attributes -band [System.IO.FileAttributes]::Hidden) {
                            $item.Attributes = $item.Attributes -band (-bnot [System.IO.FileAttributes]::Hidden)
                        }
                    }
                    catch { }
                }
            }
        }
    }
}
catch { }

# Keep layout/settings by default; remove only if user wants — leave %LOCALAPPDATA%\FenceDesk data
if (-not $Silent) {
    Add-Type -AssemblyName System.Windows.Forms -ErrorAction SilentlyContinue
    $r = [System.Windows.Forms.MessageBox]::Show(
        "FenceDesk uninstalled from Programs.`n`nAlso delete saved fences and settings?",
        $AppName,
        [System.Windows.Forms.MessageBoxButtons]::YesNo,
        [System.Windows.Forms.MessageBoxIcon]::Question
    )
    if ($r -eq [System.Windows.Forms.DialogResult]::Yes) {
        $data = Join-Path $env:LOCALAPPDATA 'FenceDesk'
        if (Test-Path $data) {
            Remove-Item -LiteralPath $data -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

Write-Host 'FenceDesk uninstalled.'
