#Requires -Version 5.1
<#
.SYNOPSIS
  FenceDesk — desktop fence organizer for Windows (Fences-inspired).

.DESCRIPTION
  Creates translucent "fence" groups on your desktop to organize shortcuts,
  files, and folders. Supports tabs and folder portals. PowerShell + WPF only.
#>
[CmdletBinding()]
param()

# WinForms + WPF need STA
if ([System.Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    $psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    Start-Process -FilePath $psExe -ArgumentList @(
        '-NoProfile', '-STA', '-WindowStyle', 'Hidden',
        '-ExecutionPolicy', 'Bypass',
        '-File', $PSCommandPath
    ) | Out-Null
    exit 0
}

$ErrorActionPreference = 'Continue'
$script:AppDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$script:LogPath = Join-Path $env:LOCALAPPDATA 'FenceDesk\fencedesk.log'

function Write-FenceLog {
    param([string]$Message)
    try {
        $dir = Split-Path -Parent $script:LogPath
        if (-not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
        $line = "[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
        Add-Content -LiteralPath $script:LogPath -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue
    }
    catch { }
}

# Single-instance mutex
$script:Mutex = $null
try {
    $created = $false
    $script:Mutex = New-Object System.Threading.Mutex($true, 'Local\FenceDesk_SingleInstance', [ref]$created)
    if (-not $created) {
        Write-FenceLog 'Another instance is already running.'
        exit 0
    }
}
catch {
    Write-FenceLog "Mutex warning: $($_.Exception.Message)"
}

try {
    Add-Type -AssemblyName PresentationFramework -ErrorAction Stop
    Add-Type -AssemblyName PresentationCore -ErrorAction Stop
    Add-Type -AssemblyName WindowsBase -ErrorAction Stop
    Add-Type -AssemblyName System.Xaml -ErrorAction Stop
    Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
    Add-Type -AssemblyName System.Drawing -ErrorAction Stop
}
catch {
    Write-FenceLog "Failed to load assemblies: $($_.Exception.Message)"
    [System.Windows.Forms.MessageBox]::Show(
        "FenceDesk could not load WPF/WinForms assemblies.`n$($_.Exception.Message)",
        'FenceDesk'
    ) | Out-Null
    exit 1
}

# Load modules
$moduleFiles = @(
    'LayoutStore.ps1'
    'DesktopNative.ps1'
    'AppIcon.ps1'
    'IconService.ps1'
    'DesktopIcons.ps1'
    'PortalService.ps1'
    'FenceWindow.ps1'
    'TaskbarHost.ps1'
    'TrayApp.ps1'
)
foreach ($mf in $moduleFiles) {
    $path = Join-Path $script:AppDir "Modules\$mf"
    if (-not (Test-Path -LiteralPath $path)) {
        Write-FenceLog "Missing module: $path"
        throw "Missing module: $mf"
    }
    . $path
}

Write-FenceLog 'FenceDesk starting'

# WPF application (explicit shutdown — tray owns lifetime)
$script:WpfApp = New-Object System.Windows.Application
$script:WpfApp.ShutdownMode = [System.Windows.ShutdownMode]::OnExplicitShutdown

# Never let a UI event kill the whole process
$script:WpfApp.Add_DispatcherUnhandledException({
    param($s, $e)
    try {
        Write-FenceLog ("Unhandled UI error: {0}" -f $e.Exception.Message)
    }
    catch { }
    $e.Handled = $true
})

# Layout + windows
$script:Layout = Read-FenceLayout
$script:FenceDeskExiting = $false
try { $null = Get-FenceDeskIcon -ForceRegenerate } catch { }  # ensure Assets\FenceDesk.ico exists
Initialize-IconService
Clear-IconCache
Initialize-TrayApp
Initialize-TaskbarHost
# Repair shelved shortcut map BEFORE creating tiles (icons need resolved paths)
Initialize-DesktopIconHider
Clear-IconCache
Initialize-AllFences
# Desktop double-click hide/show removed — use taskbar/tray Show/Hide instead

Write-FenceLog ("Loaded {0} fence(s)" -f @($script:Layout.fences).Count)

# Pump WPF dispatcher (blocks until Shutdown)
try {
    [void]$script:WpfApp.Run()
}
catch {
    Write-FenceLog "Run failed: $($_.Exception.Message)"
}
finally {
    try { Stop-FenceShowDesktopGuard } catch { }
    try { Stop-DesktopDoubleClickWatch } catch { }
    try { Save-FenceLayout -Layout $script:Layout -Immediate } catch { }
    try { Close-AllFenceWindows } catch { }
    try {
        if ($script:NotifyIcon) {
            $script:NotifyIcon.Visible = $false
            $script:NotifyIcon.Dispose()
        }
    }
    catch { }
    try {
        if ($script:Mutex) { $script:Mutex.ReleaseMutex() | Out-Null; $script:Mutex.Dispose() }
    }
    catch { }
    Write-FenceLog 'FenceDesk exited'
}
