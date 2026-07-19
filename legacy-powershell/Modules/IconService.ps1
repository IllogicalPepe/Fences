# Icon extraction and caching for FenceDesk

$script:IconService_Cache = @{}
$script:IconService_CacheDir = Join-Path $env:LOCALAPPDATA 'FenceDesk\icon-cache'

function Initialize-IconService {
    if (-not (Test-Path -LiteralPath $script:IconService_CacheDir)) {
        New-Item -ItemType Directory -Path $script:IconService_CacheDir -Force | Out-Null
    }
}

function Get-ItemDisplayLabel {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return 'Item' }
    try {
        # Shell namespace (Recycle Bin, This PC, ...)
        if (Get-Command Test-IsShellNamespacePath -ErrorAction SilentlyContinue) {
            if (Test-IsShellNamespacePath -Path $Path) {
                $clsid = Get-ShellClsidFromPath -Path $Path
                if ($clsid -and $script:ShellDesktopIcons -and $script:ShellDesktopIcons.ContainsKey($clsid)) {
                    return $script:ShellDesktopIcons[$clsid].Name
                }
                if ($Path -match 'Recycle' -or $Path -match '645FF040') { return 'Recycle Bin' }
                return 'Shell item'
            }
        }
        $name = [System.IO.Path]::GetFileNameWithoutExtension($Path)
        if ([string]::IsNullOrWhiteSpace($name)) {
            $name = [System.IO.Path]::GetFileName($Path)
        }
        if ([string]::IsNullOrWhiteSpace($name)) {
            $name = $Path
        }
        if ($Path -match '\.lnk$') {
            $dn = [System.IO.Path]::GetFileNameWithoutExtension($Path)
            if (-not [string]::IsNullOrWhiteSpace($dn)) { return $dn }
        }
        return $name
    }
    catch {
        return 'Item'
    }
}

function Get-FenceItemImage {
    param(
        [string]$Path,
        [int]$Size = 32
    )
    Initialize-IconService
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return Get-DefaultFileImageSource -Size $Size
    }

    # Always re-resolve; shelved paths may appear after startup
    $iconPath = $Path
    if (Get-Command Resolve-FenceItemPath -ErrorAction SilentlyContinue) {
        try { $iconPath = Resolve-FenceItemPath -Path $Path } catch { $iconPath = $Path }
    }

    $key = "$Size|$Path|$iconPath"
    if ($script:IconService_Cache.ContainsKey($key)) {
        return $script:IconService_Cache[$key]
    }

    $img = $null
    try {
        # Pass original path so known-app fallbacks still see "Microsoft Edge.lnk"
        $img = Get-ShellFileIcon -Path $Path -Size $Size
        if ($null -eq $img -and $iconPath -ne $Path) {
            $img = Get-ShellFileIcon -Path $iconPath -Size $Size
        }
    }
    catch {
        $img = $null
    }

    if ($null -eq $img) {
        # Last-chance: known app EXE by label/filename
        if (Get-Command Get-KnownAppExePath -ErrorAction SilentlyContinue) {
            $exe = Get-KnownAppExePath -NameOrPath $Path
            if ($exe) {
                try {
                    $ico = [System.Drawing.Icon]::ExtractAssociatedIcon($exe)
                    if ($null -ne $ico) {
                        $img = Convert-DrawingIconToImageSource $ico $Size
                    }
                }
                catch { }
            }
        }
    }

    if ($null -eq $img) {
        $img = Get-DefaultFileImageSource -Size $Size
    }

    $script:IconService_Cache[$key] = $img
    return $img
}

function Clear-IconCache {
    $script:IconService_Cache = @{}
}
