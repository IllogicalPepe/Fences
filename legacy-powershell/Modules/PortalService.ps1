# Folder portal watchers for FenceDesk

$script:PortalWatchers = @{}

function Get-PortalItems {
    param(
        [string]$FolderPath,
        [int]$MaxItems = 200
    )
    $items = @()
    if ([string]::IsNullOrWhiteSpace($FolderPath) -or -not (Test-Path -LiteralPath $FolderPath)) {
        return $items
    }
    try {
        $entries = Get-ChildItem -LiteralPath $FolderPath -Force -ErrorAction SilentlyContinue |
            Where-Object { -not $_.Attributes.ToString().Contains('Hidden') -or $_.Name -notmatch '^\.' } |
            Sort-Object { -not $_.PSIsContainer }, Name

        $count = 0
        foreach ($e in $entries) {
            if ($count -ge $MaxItems) { break }
            # skip desktop.ini and similar
            if ($e.Name -in @('desktop.ini', 'Thumbs.db', '$RECYCLE.BIN')) { continue }
            $items += [ordered]@{
                path  = $e.FullName
                label = $e.Name
            }
            $count++
        }
    }
    catch {
        Write-FenceLog "Portal list failed for $FolderPath : $($_.Exception.Message)"
    }
    return $items
}

function Register-PortalWatcher {
    param(
        [string]$FenceId,
        [string]$FolderPath,
        [scriptblock]$OnChanged
    )
    Unregister-PortalWatcher -FenceId $FenceId
    if ([string]::IsNullOrWhiteSpace($FolderPath) -or -not (Test-Path -LiteralPath $FolderPath)) {
        return
    }
    try {
        $fsw = New-Object System.IO.FileSystemWatcher
        $fsw.Path = $FolderPath
        $fsw.IncludeSubdirectories = $false
        $fsw.NotifyFilter = [System.IO.NotifyFilters]::FileName -bor
            [System.IO.NotifyFilters]::DirectoryName -bor
            [System.IO.NotifyFilters]::LastWrite
        $fsw.EnableRaisingEvents = $true

        $debounce = @{ action = $OnChanged; fenceId = $FenceId }
        $handler = {
            $state = $Event.MessageData
            $fid = $state.fenceId
            $act = $state.action
            try {
                $disp = $null
                if ($script:WpfApp -and $script:WpfApp.Dispatcher) {
                    $disp = $script:WpfApp.Dispatcher
                }
                elseif ($script:FenceWindows -and $script:FenceWindows.ContainsKey($fid)) {
                    $disp = $script:FenceWindows[$fid].Window.Dispatcher
                }
                if ($null -ne $disp) {
                    $disp.BeginInvoke([action]{
                        try { & $act $fid } catch { }
                    }) | Out-Null
                }
                else {
                    & $act $fid
                }
            }
            catch { }
        }.GetNewClosure()

        # Register-ObjectEvent; callback marshals to WPF dispatcher
        $created = Register-ObjectEvent -InputObject $fsw -EventName Created -MessageData $debounce -Action $handler
        $deleted = Register-ObjectEvent -InputObject $fsw -EventName Deleted -MessageData $debounce -Action $handler
        $renamed = Register-ObjectEvent -InputObject $fsw -EventName Renamed -MessageData $debounce -Action $handler
        $changed = Register-ObjectEvent -InputObject $fsw -EventName Changed -MessageData $debounce -Action $handler

        $script:PortalWatchers[$FenceId] = @{
            Watcher = $fsw
            Jobs    = @($created, $deleted, $renamed, $changed)
            Path    = $FolderPath
        }
    }
    catch {
        Write-FenceLog "Portal watcher failed: $($_.Exception.Message)"
    }
}

function Unregister-PortalWatcher {
    param([string]$FenceId)
    if (-not $script:PortalWatchers.ContainsKey($FenceId)) { return }
    $entry = $script:PortalWatchers[$FenceId]
    try {
        if ($entry.Watcher) {
            $entry.Watcher.EnableRaisingEvents = $false
            $entry.Watcher.Dispose()
        }
    }
    catch { }
    foreach ($j in @($entry.Jobs)) {
        try { Unregister-Event -SourceIdentifier $j.Name -ErrorAction SilentlyContinue } catch { }
        try { Remove-Job -Id $j.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    $script:PortalWatchers.Remove($FenceId)
}

function Unregister-AllPortalWatchers {
    foreach ($id in @($script:PortalWatchers.Keys)) {
        Unregister-PortalWatcher -FenceId $id
    }
}
