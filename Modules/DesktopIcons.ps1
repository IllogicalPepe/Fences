# Hide / restore desktop icons that live inside fences
# - User Desktop: Hidden attribute
# - Public Desktop (Edge/Brave/etc.): move to shelved folder when attrib fails
# - Shell icons (Recycle Bin, This PC, ...): HideDesktopIcons registry

$script:DesktopHiddenStatePath = Join-Path $env:LOCALAPPDATA 'FenceDesk\hidden-desktop.json'
$script:DesktopShelveDir = Join-Path $env:LOCALAPPDATA 'FenceDesk\desktop-shelved'
$script:DesktopHiddenPaths = @{}   # path(lower) -> $true  (attrib-hidden)
$script:DesktopShelvedMap = @{}    # originalPath(lower) -> shelvedFullPath
$script:DesktopHiddenShell = @{}   # clsid -> $true

# Well-known desktop shell folders
$script:ShellDesktopIcons = @{
    '{645FF040-5081-101B-9F08-00AA002F954E}' = @{
        Name   = 'Recycle Bin'
        Launch = 'shell:RecycleBinFolder'
        Path   = '::{645FF040-5081-101B-9F08-00AA002F954E}'
    }
    '{20D04FE0-3AEA-1069-A2D8-08002B30309D}' = @{
        Name   = 'This PC'
        Launch = 'shell:MyComputerFolder'
        Path   = '::{20D04FE0-3AEA-1069-A2D8-08002B30309D}'
    }
    '{59031A47-3F72-44A7-89C5-5595FE6B30EE}' = @{
        Name   = 'User Files'
        Launch = 'shell:UsersFilesFolder'
        Path   = '::{59031A47-3F72-44A7-89C5-5595FE6B30EE}'
    }
    '{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}' = @{
        Name   = 'Network'
        Launch = 'shell:NetworkPlacesFolder'
        Path   = '::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}'
    }
    '{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}' = @{
        Name   = 'Control Panel'
        Launch = 'shell:ControlPanelFolder'
        Path   = '::{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}'
    }
}

function Get-RecycleBinClsid {
    return '{645FF040-5081-101B-9F08-00AA002F954E}'
}

function Get-RecycleBinPath {
    return '::{645FF040-5081-101B-9F08-00AA002F954E}'
}

function Test-IsShellNamespacePath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    if ($Path -match '^::\{[0-9A-Fa-f\-]+\}$') { return $true }
    if ($Path -match '^shell:') { return $true }
    return $false
}

function Get-ShellClsidFromPath {
    param([string]$Path)
    if ($Path -match '::\{([0-9A-Fa-f\-]+)\}') {
        return '{' + $Matches[1].ToUpper() + '}'
    }
    # Match known launch strings
    foreach ($clsid in $script:ShellDesktopIcons.Keys) {
        $info = $script:ShellDesktopIcons[$clsid]
        if ($Path -eq $info.Path -or $Path -eq $info.Launch) {
            return $clsid
        }
    }
    return $null
}

function Get-DesktopFolderPaths {
    $paths = @()
    try {
        $paths += [Environment]::GetFolderPath('Desktop')
        $paths += [Environment]::GetFolderPath('CommonDesktopDirectory')
    }
    catch { }
    return @($paths | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | ForEach-Object {
        [System.IO.Path]::GetFullPath($_).TrimEnd('\')
    } | Select-Object -Unique)
}

function Test-PathIsOnDesktop {
    param(
        [string]$Path,
        [switch]$UserDesktopOnly
    )
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    if (Test-IsShellNamespacePath -Path $Path) { return $false }
    try {
        if (-not (Test-Path -LiteralPath $Path)) { return $false }
        $full = [System.IO.Path]::GetFullPath($Path)
        $parent = [System.IO.Path]::GetDirectoryName($full)
        if ([string]::IsNullOrWhiteSpace($parent)) { return $false }
        $parent = $parent.TrimEnd('\')
        $desks = if ($UserDesktopOnly) {
            @([Environment]::GetFolderPath('Desktop'))
        } else {
            Get-DesktopFolderPaths
        }
        foreach ($desk in $desks) {
            if (-not $desk) { continue }
            $d = [System.IO.Path]::GetFullPath($desk).TrimEnd('\')
            if ($parent.Equals($d, [StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }
    }
    catch { }
    return $false
}

function Test-PathIsOnPublicDesktop {
    param([string]$Path)
    try {
        $pub = [Environment]::GetFolderPath('CommonDesktopDirectory')
        if (-not $pub -or -not (Test-Path -LiteralPath $Path)) { return $false }
        $full = [System.IO.Path]::GetFullPath($Path)
        $parent = [System.IO.Path]::GetDirectoryName($full).TrimEnd('\')
        $d = [System.IO.Path]::GetFullPath($pub).TrimEnd('\')
        return $parent.Equals($d, [StringComparison]::OrdinalIgnoreCase)
    }
    catch { return $false }
}

function Read-DesktopHiddenState {
    $script:DesktopHiddenPaths = @{}
    $script:DesktopShelvedMap = @{}
    $script:DesktopHiddenShell = @{}
    if (-not (Test-Path -LiteralPath $script:DesktopHiddenStatePath)) { return }
    try {
        $raw = Get-Content -LiteralPath $script:DesktopHiddenStatePath -Raw -Encoding UTF8
        $obj = $raw | ConvertFrom-Json
        foreach ($p in @($obj.paths)) {
            if ($p) { $script:DesktopHiddenPaths[$p.ToString().ToLowerInvariant()] = $true }
        }
        if ($obj.shelved) {
            $obj.shelved.PSObject.Properties | ForEach-Object {
                $script:DesktopShelvedMap[$_.Name.ToLowerInvariant()] = [string]$_.Value
            }
        }
        foreach ($c in @($obj.shellIcons)) {
            if ($c) { $script:DesktopHiddenShell[$c.ToString().ToUpper()] = $true }
        }
    }
    catch {
        Write-FenceLog "Read hidden-desktop state failed: $($_.Exception.Message)"
    }
}

function Write-DesktopHiddenState {
    try {
        $dir = Split-Path -Parent $script:DesktopHiddenStatePath
        if (-not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
        $shelvedObj = @{}
        foreach ($k in $script:DesktopShelvedMap.Keys) {
            $shelvedObj[$k] = $script:DesktopShelvedMap[$k]
        }
        $obj = @{
            version    = 2
            paths      = @($script:DesktopHiddenPaths.Keys)
            shelved    = $shelvedObj
            shellIcons = @($script:DesktopHiddenShell.Keys)
        }
        $json = $obj | ConvertTo-Json -Depth 6
        $tmp = "$($script:DesktopHiddenStatePath).tmp"
        [System.IO.File]::WriteAllText($tmp, $json, [System.Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $tmp -Destination $script:DesktopHiddenStatePath -Force
    }
    catch {
        Write-FenceLog "Write hidden-desktop state failed: $($_.Exception.Message)"
    }
}

function Get-AllFencedItemPaths {
    $set = @{}
    if ($null -eq $script:Layout -or $null -eq $script:Layout.fences) {
        return $set
    }
    foreach ($f in @($script:Layout.fences)) {
        if ($f.mode -eq 'portal') { continue }
        foreach ($t in @($f.tabs)) {
            foreach ($it in @($t.items)) {
                if ($it.path) {
                    $p = [string]$it.path
                    if (Test-IsShellNamespacePath -Path $p) {
                        $set[$p.ToLowerInvariant()] = $p
                    }
                    else {
                        try {
                            $key = [System.IO.Path]::GetFullPath($p).ToLowerInvariant()
                            $set[$key] = $p
                        }
                        catch {
                            $set[$p.ToLowerInvariant()] = $p
                        }
                    }
                }
            }
        }
    }
    return $set
}

function Hide-DesktopFile {
    param([string]$Path)
    try {
        if (-not (Test-Path -LiteralPath $Path)) { return $false }
        $attrs = [System.IO.File]::GetAttributes($Path)
        if ($attrs -band [System.IO.FileAttributes]::Hidden) {
            return $true
        }
        [System.IO.File]::SetAttributes($Path, $attrs -bor [System.IO.FileAttributes]::Hidden)
        return $true
    }
    catch {
        Write-FenceLog "Hide desktop file (attrib) failed ($Path): $($_.Exception.Message)"
        return $false
    }
}

function Show-DesktopFile {
    param([string]$Path)
    try {
        if (-not (Test-Path -LiteralPath $Path)) { return $false }
        $attrs = [System.IO.File]::GetAttributes($Path)
        if ($attrs -band [System.IO.FileAttributes]::Hidden) {
            [System.IO.File]::SetAttributes($Path, $attrs -band (-bnot [System.IO.FileAttributes]::Hidden))
        }
        return $true
    }
    catch {
        Write-FenceLog "Show desktop file failed ($Path): $($_.Exception.Message)"
        return $false
    }
}

function Hide-DesktopFileByShelve {
    <#
      For Public Desktop shortcuts (Edge/Brave) where attrib is denied:
      move the .lnk into FenceDesk\desktop-shelved so it vanishes from the desktop.
    #>
    param([string]$Path)
    try {
        if (-not (Test-Path -LiteralPath $Path)) { return $false }
        $key = [System.IO.Path]::GetFullPath($Path).ToLowerInvariant()
        if ($script:DesktopShelvedMap.ContainsKey($key)) {
            # Already shelved
            return $true
        }
        if (-not (Test-Path -LiteralPath $script:DesktopShelveDir)) {
            New-Item -ItemType Directory -Path $script:DesktopShelveDir -Force | Out-Null
        }
        $name = [System.IO.Path]::GetFileName($Path)
        $dest = Join-Path $script:DesktopShelveDir $name
        $i = 1
        while (Test-Path -LiteralPath $dest) {
            $base = [System.IO.Path]::GetFileNameWithoutExtension($name)
            $ext = [System.IO.Path]::GetExtension($name)
            $dest = Join-Path $script:DesktopShelveDir ("{0}_{1}{2}" -f $base, $i, $ext)
            $i++
        }
        Move-Item -LiteralPath $Path -Destination $dest -Force -ErrorAction Stop
        $script:DesktopShelvedMap[$key] = $dest
        Write-FenceLog "Shelved desktop icon: $Path -> $dest"
        return $true
    }
    catch {
        Write-FenceLog "Shelve desktop file failed ($Path): $($_.Exception.Message)"
        return $false
    }
}

function Show-DesktopFileFromShelve {
    param([string]$OriginalKey)
    try {
        if (-not $script:DesktopShelvedMap.ContainsKey($OriginalKey)) { return $false }
        $shelved = [string]$script:DesktopShelvedMap[$OriginalKey]
        if ([string]::IsNullOrWhiteSpace($shelved) -or -not (Test-Path -LiteralPath $shelved)) {
            $script:DesktopShelvedMap.Remove($OriginalKey)
            return $true
        }

        $userDesk = [Environment]::GetFolderPath('Desktop')
        $name = [System.IO.Path]::GetFileName($shelved)
        if ([string]::IsNullOrWhiteSpace($name)) { $name = [System.IO.Path]::GetFileName($OriginalKey) }

        # Prefer original full path when key is a real path
        $dest = $null
        if ($OriginalKey -match '[\\/]' -and $OriginalKey.Length -gt 3) {
            $dest = $OriginalKey
            $parent = [System.IO.Path]::GetDirectoryName($dest)
            if ([string]::IsNullOrWhiteSpace($parent) -or -not (Test-Path -LiteralPath $parent)) {
                $dest = $null
            }
        }
        if (-not $dest) {
            $dest = Join-Path $userDesk $name
        }

        if (Test-Path -LiteralPath $dest) {
            $base = [System.IO.Path]::GetFileNameWithoutExtension($name)
            $ext = [System.IO.Path]::GetExtension($name)
            $i = 1
            do {
                $dest = Join-Path $userDesk ("{0}_{1}{2}" -f $base, $i, $ext)
                $i++
            } while (Test-Path -LiteralPath $dest)
        }

        Move-Item -LiteralPath $shelved -Destination $dest -Force -ErrorAction Stop
        # Drop all map entries that pointed at this shelved file
        foreach ($k in @($script:DesktopShelvedMap.Keys)) {
            if ($script:DesktopShelvedMap[$k] -eq $shelved) {
                $script:DesktopShelvedMap.Remove($k)
            }
        }
        Write-FenceLog "Restored shelved icon: $shelved -> $dest"
        return $true
    }
    catch {
        Write-FenceLog "Restore shelved failed ($OriginalKey): $($_.Exception.Message)"
        return $false
    }
}

function Set-ShellDesktopIconHidden {
    param(
        [string]$Clsid,
        [bool]$Hidden
    )
    # CLSID must be {GUID} form
    if ($Clsid -notmatch '^\{[0-9A-Fa-f\-]+\}$') { return $false }
    $clsid = $Clsid.ToUpper()
    $value = if ($Hidden) { 1 } else { 0 }
    $keys = @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel'
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\ClassicStartMenu'
    )
    $ok = $false
    foreach ($reg in $keys) {
        try {
            if (-not (Test-Path -LiteralPath $reg)) {
                New-Item -Path $reg -Force | Out-Null
            }
            Set-ItemProperty -Path $reg -Name $clsid -Value $value -Type DWord -Force
            $ok = $true
        }
        catch {
            Write-FenceLog "Registry hide $clsid failed ($reg): $($_.Exception.Message)"
        }
    }
    return $ok
}

function Hide-OrShelveDesktopPath {
    param([string]$Path)
    if (Test-IsShellNamespacePath -Path $Path) {
        $clsid = Get-ShellClsidFromPath -Path $Path
        if (-not $clsid) { return $false }
        if (Set-ShellDesktopIconHidden -Clsid $clsid -Hidden $true) {
            $script:DesktopHiddenShell[$clsid] = $true
            return $true
        }
        return $false
    }

    if (-not (Test-PathIsOnDesktop -Path $Path)) { return $false }

    # Try attribute hide first
    if (Hide-DesktopFile -Path $Path) {
        $key = [System.IO.Path]::GetFullPath($Path).ToLowerInvariant()
        $script:DesktopHiddenPaths[$key] = $true
        return $true
    }

    # Public Desktop / ACL: move off desktop
    if (Hide-DesktopFileByShelve -Path $Path) {
        return $true
    }
    return $false
}

function Invoke-DesktopRefresh {
    try {
        if ('FenceDeskNativeV6' -as [type]) {
            [FenceDeskNativeV6]::RefreshDesktop()
        }
    }
    catch { }
    # Stronger refresh for shell icon registry changes
    try {
        $sig = @'
[DllImport("shell32.dll")] public static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
'@
        if (-not ('FenceDeskShellNotify' -as [type])) {
            Add-Type -MemberDefinition $sig -Name FenceDeskShellNotify -Namespace Native -ErrorAction SilentlyContinue
        }
        if ('Native.FenceDeskShellNotify' -as [type]) {
            # SHCNE_ASSOCCHANGED | SHCNF_IDLIST
            [Native.FenceDeskShellNotify]::SHChangeNotify(0x08000000, 0x0000, [IntPtr]::Zero, [IntPtr]::Zero)
        }
    }
    catch { }
    # Restart explorer desktop icons without full explorer restart when possible:
    try {
        # F5-style: notify item attributes
        $progman = Get-Process -Name explorer -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($progman) {
            # no-op; SHChangeNotify above is primary
        }
    }
    catch { }
}

function Test-FencedDesktopPath {
    # True if path is/was a desktop icon we manage (even if already shelved/missing)
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    if (Test-IsShellNamespacePath -Path $Path) { return $false }
    if (Test-PathIsOnDesktop -Path $Path) { return $true }
    try {
        $full = $Path
        try { $full = [System.IO.Path]::GetFullPath($Path) } catch { }
        $parent = [System.IO.Path]::GetDirectoryName($full)
        if ($parent) {
            $parent = $parent.TrimEnd('\')
            foreach ($desk in (Get-DesktopFolderPaths)) {
                $d = [System.IO.Path]::GetFullPath($desk).TrimEnd('\')
                if ($parent.Equals($d, [StringComparison]::OrdinalIgnoreCase)) { return $true }
            }
        }
    }
    catch { }
    $key = $Path.ToLowerInvariant()
    try { $key = [System.IO.Path]::GetFullPath($Path).ToLowerInvariant() } catch { }
    if ($script:DesktopShelvedMap -and $script:DesktopShelvedMap.ContainsKey($key)) { return $true }
    $name = [System.IO.Path]::GetFileName($Path)
    if ($script:DesktopShelveDir -and $name) {
        $cand = Join-Path $script:DesktopShelveDir $name
        if (Test-Path -LiteralPath $cand) { return $true }
    }
    return $false
}

function Sync-DesktopIconVisibility {
    try {
        if ($null -eq $script:DesktopHiddenPaths) { $script:DesktopHiddenPaths = @{} }
        if ($null -eq $script:DesktopShelvedMap) { $script:DesktopShelvedMap = @{} }
        if ($null -eq $script:DesktopHiddenShell) { $script:DesktopHiddenShell = @{} }

        $fenced = Get-AllFencedItemPaths
        $shouldHideFiles = @{}   # lower path -> path
        $shouldHideShell = @{}   # clsid -> $true
        $fencedFileNames = @{}   # lowercase filename still in a fence

        foreach ($key in $fenced.Keys) {
            $path = $fenced[$key]
            if (Test-IsShellNamespacePath -Path $path) {
                $clsid = Get-ShellClsidFromPath -Path $path
                if ($clsid) { $shouldHideShell[$clsid] = $true }
                continue
            }
            $fn = [System.IO.Path]::GetFileName($path)
            if ($fn) { $fencedFileNames[$fn.ToLowerInvariant()] = $true }

            if (Test-FencedDesktopPath -Path $path) {
                try {
                    $fullKey = [System.IO.Path]::GetFullPath($path).ToLowerInvariant()
                }
                catch { $fullKey = $key }
                $shouldHideFiles[$fullKey] = $path
            }
        }

        $changed = $false

        # Hide / shelve files still present on a desktop folder
        foreach ($key in @($shouldHideFiles.Keys)) {
            $path = $shouldHideFiles[$key]
            # Already shelved under this or any key with same filename — keep shelved
            $alreadyShelved = $false
            if ($script:DesktopShelvedMap.ContainsKey($key)) { $alreadyShelved = $true }
            $fn = [System.IO.Path]::GetFileName($path)
            if ($fn) {
                foreach ($sk in @($script:DesktopShelvedMap.Keys)) {
                    if ([System.IO.Path]::GetFileName($sk) -eq $fn.ToLowerInvariant() -or
                        [System.IO.Path]::GetFileName($script:DesktopShelvedMap[$sk]) -eq $fn) {
                        $alreadyShelved = $true
                        break
                    }
                }
                $cand = Join-Path $script:DesktopShelveDir $fn
                if (Test-Path -LiteralPath $cand) { $alreadyShelved = $true }
            }
            if ($alreadyShelved) {
                if (Test-Path -LiteralPath $path) {
                    # Still on desktop somehow — shelve/hide again
                    if (Hide-OrShelveDesktopPath -Path $path) { $changed = $true }
                }
                continue
            }
            if ($script:DesktopHiddenPaths.ContainsKey($key)) {
                if (Test-Path -LiteralPath $path) { Hide-DesktopFile -Path $path | Out-Null }
                continue
            }
            if (Test-Path -LiteralPath $path) {
                if (Hide-OrShelveDesktopPath -Path $path) { $changed = $true }
            }
        }

        # Shell icons (Recycle Bin, etc.)
        foreach ($clsid in @($shouldHideShell.Keys)) {
            if (-not $script:DesktopHiddenShell.ContainsKey($clsid)) {
                if (Set-ShellDesktopIconHidden -Clsid $clsid -Hidden $true) {
                    $script:DesktopHiddenShell[$clsid] = $true
                    $changed = $true
                }
            }
            else {
                Set-ShellDesktopIconHidden -Clsid $clsid -Hidden $true | Out-Null
            }
        }

        # Restore attrib-hidden files no longer fenced
        foreach ($key in @($script:DesktopHiddenPaths.Keys)) {
            if ($shouldHideFiles.ContainsKey($key)) { continue }
            $fn = [System.IO.Path]::GetFileName($key)
            if ($fn -and $fencedFileNames.ContainsKey($fn.ToLowerInvariant())) { continue }
            $path = $key
            try {
                if (Test-Path -LiteralPath $key) {
                    $path = (Get-Item -LiteralPath $key -Force).FullName
                }
                else {
                    $name = [System.IO.Path]::GetFileName($key)
                    foreach ($desk in (Get-DesktopFolderPaths)) {
                        $candidate = Join-Path $desk $name
                        if (Test-Path -LiteralPath $candidate) { $path = $candidate; break }
                    }
                }
            }
            catch { }
            if (Show-DesktopFile -Path $path) {
                $script:DesktopHiddenPaths.Remove($key)
                $changed = $true
            }
            else {
                $script:DesktopHiddenPaths.Remove($key)
                $changed = $true
            }
        }

        # Restore shelved only when that filename is no longer in any fence
        foreach ($key in @($script:DesktopShelvedMap.Keys)) {
            if ($shouldHideFiles.ContainsKey($key)) { continue }
            $fn = [System.IO.Path]::GetFileName($key)
            if (-not $fn) { $fn = [System.IO.Path]::GetFileName($script:DesktopShelvedMap[$key]) }
            if ($fn -and $fencedFileNames.ContainsKey($fn.ToLowerInvariant())) { continue }
            # Skip bare filename helper keys (handled with full path keys)
            if ($key -notmatch '[\\/]') { continue }
            if (Show-DesktopFileFromShelve -OriginalKey $key) {
                $changed = $true
            }
        }

        foreach ($clsid in @($script:DesktopHiddenShell.Keys)) {
            if ($shouldHideShell.ContainsKey($clsid)) { continue }
            if (Set-ShellDesktopIconHidden -Clsid $clsid -Hidden $false) {
                $script:DesktopHiddenShell.Remove($clsid)
                $changed = $true
            }
        }

        if ($changed) {
            Write-DesktopHiddenState
            Invoke-DesktopRefresh
            Write-FenceLog ("Desktop icon sync: hidden={0} shelved={1} shell={2}" -f `
                $script:DesktopHiddenPaths.Count, $script:DesktopShelvedMap.Count, $script:DesktopHiddenShell.Count)
        }
    }
    catch {
        Write-FenceLog "Sync-DesktopIconVisibility: $($_.Exception.Message)"
    }
}

function Restore-AllDesktopIconsHiddenByFenceDesk {
    Read-DesktopHiddenState
    foreach ($key in @($script:DesktopHiddenPaths.Keys)) {
        $name = [System.IO.Path]::GetFileName($key)
        $path = $key
        foreach ($desk in (Get-DesktopFolderPaths)) {
            $candidate = Join-Path $desk $name
            if (Test-Path -LiteralPath $candidate) { $path = $candidate; break }
        }
        Show-DesktopFile -Path $path | Out-Null
    }
    foreach ($key in @($script:DesktopShelvedMap.Keys)) {
        Show-DesktopFileFromShelve -OriginalKey $key | Out-Null
    }
    foreach ($clsid in @($script:DesktopHiddenShell.Keys)) {
        Set-ShellDesktopIconHidden -Clsid $clsid -Hidden $false | Out-Null
    }
    $script:DesktopHiddenPaths = @{}
    $script:DesktopShelvedMap = @{}
    $script:DesktopHiddenShell = @{}
    Write-DesktopHiddenState
    Invoke-DesktopRefresh
}

function Initialize-DesktopIconHider {
    if (-not (Test-Path -LiteralPath $script:DesktopShelveDir)) {
        New-Item -ItemType Directory -Path $script:DesktopShelveDir -Force | Out-Null
    }
    Read-DesktopHiddenState
    Repair-DesktopShelvedMap
    Sync-DesktopIconVisibility
}

function Invoke-FenceItemLaunch {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    try {
        if (Test-IsShellNamespacePath -Path $Path) {
            $clsid = Get-ShellClsidFromPath -Path $Path
            $launch = $Path
            if ($clsid -and $script:ShellDesktopIcons.ContainsKey($clsid)) {
                $launch = $script:ShellDesktopIcons[$clsid].Launch
            }
            elseif ($Path -match '^::\{') {
                $launch = 'shell:' + $Path  # may not work; use explorer
                Start-Process -FilePath 'explorer.exe' -ArgumentList $Path
                return
            }
            Start-Process -FilePath 'explorer.exe' -ArgumentList $launch
            return
        }
        # Shelved public shortcuts: resolve to shelved file if original path missing
        if (-not (Test-Path -LiteralPath $Path)) {
            $key = $Path.ToLowerInvariant()
            try { $key = [System.IO.Path]::GetFullPath($Path).ToLowerInvariant() } catch { }
            if ($script:DesktopShelvedMap -and $script:DesktopShelvedMap.ContainsKey($key)) {
                $Path = $script:DesktopShelvedMap[$key]
            }
        }
        Start-Process -FilePath $Path -ErrorAction Stop
    }
    catch {
        try {
            $psi = New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName = $Path
            $psi.UseShellExecute = $true
            [void][System.Diagnostics.Process]::Start($psi)
        }
        catch {
            [System.Windows.MessageBox]::Show("Could not open:`n$Path", 'FenceDesk') | Out-Null
        }
    }
}

function Repair-DesktopShelvedMap {
    <#
      Rebuild originalPath -> shelvedPath map from files on disk.
      Layout still stores Public Desktop paths after shelve; map can be lost if state write failed.
    #>
    try {
        if ($null -eq $script:DesktopShelvedMap) { $script:DesktopShelvedMap = @{} }
        if (-not (Test-Path -LiteralPath $script:DesktopShelveDir)) { return }

        $desks = @(Get-DesktopFolderPaths)
        $changed = $false

        Get-ChildItem -LiteralPath $script:DesktopShelveDir -Force -ErrorAction SilentlyContinue |
            Where-Object { -not $_.PSIsContainer } |
            ForEach-Object {
                $shelvedPath = $_.FullName
                $name = $_.Name
                # Map every desktop folder + this filename as a possible original key
                foreach ($desk in $desks) {
                    $orig = Join-Path $desk $name
                    $key = $orig.ToLowerInvariant()
                    if (-not $script:DesktopShelvedMap.ContainsKey($key)) {
                        $script:DesktopShelvedMap[$key] = $shelvedPath
                        $changed = $true
                    }
                    elseif (-not (Test-Path -LiteralPath $script:DesktopShelvedMap[$key])) {
                        $script:DesktopShelvedMap[$key] = $shelvedPath
                        $changed = $true
                    }
                }
                # Also map bare filename key for loose matching
                $nameKey = $name.ToLowerInvariant()
                if (-not $script:DesktopShelvedMap.ContainsKey($nameKey)) {
                    $script:DesktopShelvedMap[$nameKey] = $shelvedPath
                    $changed = $true
                }
            }

        # Map any fenced missing paths by filename
        foreach ($key in @(Get-AllFencedItemPaths).Keys) {
            $path = (Get-AllFencedItemPaths)[$key]
            if (Test-IsShellNamespacePath -Path $path) { continue }
            if (Test-Path -LiteralPath $path) { continue }
            $name = [System.IO.Path]::GetFileName($path)
            $candidate = Join-Path $script:DesktopShelveDir $name
            if (Test-Path -LiteralPath $candidate) {
                $k = $path.ToLowerInvariant()
                try { $k = [System.IO.Path]::GetFullPath($path).ToLowerInvariant() } catch { }
                if (-not $script:DesktopShelvedMap.ContainsKey($k)) {
                    $script:DesktopShelvedMap[$k] = $candidate
                    $changed = $true
                }
            }
        }

        if ($changed) {
            Write-DesktopHiddenState
            Write-FenceLog ("Repaired shelved map: {0} entr(y/ies)" -f $script:DesktopShelvedMap.Count)
        }
    }
    catch {
        Write-FenceLog "Repair-DesktopShelvedMap: $($_.Exception.Message)"
    }
}

function Resolve-FenceItemPath {
    <# Keep fence item usable after public-desktop shelve. #>
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $Path }
    if (Test-IsShellNamespacePath -Path $Path) { return $Path }
    if (Test-Path -LiteralPath $Path) { return $Path }

    if ($null -eq $script:DesktopShelvedMap) { $script:DesktopShelvedMap = @{} }

    $key = $Path.ToLowerInvariant()
    try { $key = [System.IO.Path]::GetFullPath($Path).ToLowerInvariant() } catch { }

    if ($script:DesktopShelvedMap.ContainsKey($key)) {
        $s = $script:DesktopShelvedMap[$key]
        if (Test-Path -LiteralPath $s) { return $s }
    }

    $name = [System.IO.Path]::GetFileName($Path)
    $nameKey = $name.ToLowerInvariant()
    if ($script:DesktopShelvedMap.ContainsKey($nameKey)) {
        $s = $script:DesktopShelvedMap[$nameKey]
        if (Test-Path -LiteralPath $s) { return $s }
    }

    foreach ($k in @($script:DesktopShelvedMap.Keys)) {
        $fn = [System.IO.Path]::GetFileName($k)
        if ($fn -and ($fn.Equals($name, [StringComparison]::OrdinalIgnoreCase) -or
                      $fn.Equals($nameKey, [StringComparison]::OrdinalIgnoreCase))) {
            $s = $script:DesktopShelvedMap[$k]
            if (Test-Path -LiteralPath $s) { return $s }
        }
    }

    # Direct look in shelved folder by filename
    if ($script:DesktopShelveDir -and (Test-Path -LiteralPath $script:DesktopShelveDir)) {
        $candidate = Join-Path $script:DesktopShelveDir $name
        if (Test-Path -LiteralPath $candidate) { return $candidate }
        # fuzzy: Brave_1.lnk etc.
        $base = [System.IO.Path]::GetFileNameWithoutExtension($name)
        $hit = Get-ChildItem -LiteralPath $script:DesktopShelveDir -Filter ($base + '*') -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }

    return $Path
}

function Get-KnownAppExePath {
    <# Fallback icon sources when .lnk is missing. #>
    param([string]$NameOrPath)
    $n = [System.IO.Path]::GetFileNameWithoutExtension($NameOrPath)
    if ([string]::IsNullOrWhiteSpace($n)) { $n = $NameOrPath }
    $n = $n.ToLowerInvariant()

    $candidates = @()
    if ($n -match 'edge' -or $n -eq 'microsoft edge') {
        $candidates += @(
            "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
            "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
        )
    }
    if ($n -match 'brave') {
        $candidates += @(
            "$env:ProgramFiles\BraveSoftware\Brave-Browser\Application\brave.exe"
            "${env:ProgramFiles(x86)}\BraveSoftware\Brave-Browser\Application\brave.exe"
            "$env:LOCALAPPDATA\BraveSoftware\Brave-Browser\Application\brave.exe"
        )
    }
    if ($n -match 'steam') {
        $candidates += @(
            "${env:ProgramFiles(x86)}\Steam\Steam.exe"
            "$env:ProgramFiles\Steam\Steam.exe"
        )
    }
    foreach ($c in $candidates) {
        if ($c -and (Test-Path -LiteralPath $c)) { return $c }
    }
    return $null
}
