# Layout persistence for FenceDesk
# Stores state under %LOCALAPPDATA%\FenceDesk\layout.json

$script:LayoutStore_Path = Join-Path $env:LOCALAPPDATA 'FenceDesk\layout.json'
$script:LayoutStore_Dir  = Join-Path $env:LOCALAPPDATA 'FenceDesk'
$script:LayoutStore_SavePending = $false
$script:LayoutStore_Timer = $null

function Get-FenceDeskDataDir {
    if (-not (Test-Path -LiteralPath $script:LayoutStore_Dir)) {
        New-Item -ItemType Directory -Path $script:LayoutStore_Dir -Force | Out-Null
    }
    return $script:LayoutStore_Dir
}

function Get-DefaultLayout {
    $desktop = [Environment]::GetFolderPath('Desktop')
    $downloads = Join-Path $env:USERPROFILE 'Downloads'
    $wa = [System.Windows.SystemParameters]::WorkArea

    $right = [int]($wa.Right - 480)
    if ($right -lt 40) { $right = 40 }
    $top = [int]($wa.Top + 40)

    return [ordered]@{
        version  = 1
        settings = [ordered]@{
            startWithWindows       = $false
            doubleClickDesktopHide = $false
            defaultOpacity         = 0.72
            accentColor            = '#0F1724'
            showFences             = $true
        }
        fences = @(
            [ordered]@{
                id          = [guid]::NewGuid().ToString()
                title       = 'Apps'
                x           = $right
                y           = $top
                width       = 420
                height      = 180
                rolledUp    = $false
                opacity     = 0.72
                bgColor     = '#0F1724'
                mode        = 'items'
                activeTabId = 'tab-apps'
                portalPath  = $null
                tabs        = @(
                    [ordered]@{
                        id    = 'tab-apps'
                        title = 'Apps'
                        items = @()
                    }
                )
            },
            [ordered]@{
                id          = [guid]::NewGuid().ToString()
                title       = 'Files'
                x           = $right
                y           = $top + 200
                width       = 420
                height      = 160
                rolledUp    = $false
                opacity     = 0.72
                bgColor     = '#0F1724'
                mode        = 'items'
                activeTabId = 'tab-files'
                portalPath  = $null
                tabs        = @(
                    [ordered]@{
                        id    = 'tab-files'
                        title = 'Files'
                        items = @()
                    }
                )
            },
            [ordered]@{
                id          = [guid]::NewGuid().ToString()
                title       = 'Downloads'
                x           = $right
                y           = $top + 380
                width       = 420
                height      = 200
                rolledUp    = $false
                opacity     = 0.72
                bgColor     = '#0F1724'
                mode        = 'portal'
                activeTabId = 'tab-portal'
                portalPath  = $(if (Test-Path $downloads) { $downloads } else { $desktop })
                tabs        = @(
                    [ordered]@{
                        id    = 'tab-portal'
                        title = 'Downloads'
                        items = @()
                    }
                )
            }
        )
    }
}

function ConvertTo-HashtableDeep {
    param($InputObject)
    if ($null -eq $InputObject) { return $null }
    if ($InputObject -is [System.Collections.IDictionary]) {
        $h = [ordered]@{}
        foreach ($k in $InputObject.Keys) {
            $h[$k] = ConvertTo-HashtableDeep $InputObject[$k]
        }
        return $h
    }
    if ($InputObject -is [System.Collections.IEnumerable] -and -not ($InputObject -is [string])) {
        $list = @()
        foreach ($item in $InputObject) {
            $list += ,(ConvertTo-HashtableDeep $item)
        }
        return $list
    }
    if ($InputObject -is [pscustomobject]) {
        $h = [ordered]@{}
        foreach ($p in $InputObject.PSObject.Properties) {
            $h[$p.Name] = ConvertTo-HashtableDeep $p.Value
        }
        return $h
    }
    return $InputObject
}

function Read-FenceLayout {
    $null = Get-FenceDeskDataDir
    if (-not (Test-Path -LiteralPath $script:LayoutStore_Path)) {
        $layout = Get-DefaultLayout
        Save-FenceLayout -Layout $layout -Immediate
        return $layout
    }
    try {
        $raw = Get-Content -LiteralPath $script:LayoutStore_Path -Raw -Encoding UTF8
        if ([string]::IsNullOrWhiteSpace($raw)) {
            return Get-DefaultLayout
        }
        $obj = $raw | ConvertFrom-Json
        $layout = ConvertTo-HashtableDeep $obj
        if (-not $layout.fences) { $layout.fences = @() }
        if (-not $layout.settings) {
            $layout.settings = (Get-DefaultLayout).settings
        }
        # Desktop double-click hide is disabled; keep setting consistent for existing layouts
        try { $layout.settings.doubleClickDesktopHide = $false } catch { }
        return $layout
    }
    catch {
        Write-FenceLog "Failed to read layout: $($_.Exception.Message)"
        $backup = "$($script:LayoutStore_Path).bad"
        try { Copy-Item -LiteralPath $script:LayoutStore_Path -Destination $backup -Force } catch { }
        return Get-DefaultLayout
    }
}

function Save-FenceLayout {
    param(
        $Layout,
        [switch]$Immediate
    )
    if ($null -ne $Layout) { $script:Layout = $Layout }
    if ($Immediate) {
        Write-FenceLayoutFile
        return
    }
    $script:LayoutStore_SavePending = $true
    try {
        if ($null -eq $script:LayoutStore_Timer) {
            $script:LayoutStore_Timer = New-Object System.Windows.Threading.DispatcherTimer
            $script:LayoutStore_Timer.Interval = [TimeSpan]::FromMilliseconds(500)
            $script:LayoutStore_Timer.Add_Tick({
                try {
                    $script:LayoutStore_Timer.Stop()
                    if ($script:LayoutStore_SavePending) {
                        $script:LayoutStore_SavePending = $false
                        Write-FenceLayoutFile
                    }
                }
                catch { }
            })
        }
        $script:LayoutStore_Timer.Stop()
        $script:LayoutStore_Timer.Start()
    }
    catch {
        # Fallback: save immediately if timer cannot start (no dispatcher yet)
        Write-FenceLayoutFile
    }
}

function Write-FenceLayoutFile {
    try {
        $null = Get-FenceDeskDataDir
        $json = $script:Layout | ConvertTo-Json -Depth 12
        $tmp = "$($script:LayoutStore_Path).tmp"
        [System.IO.File]::WriteAllText($tmp, $json, [System.Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $script:LayoutStore_Path) {
            $bak = "$($script:LayoutStore_Path).bak"
            Copy-Item -LiteralPath $script:LayoutStore_Path -Destination $bak -Force -ErrorAction SilentlyContinue
        }
        Move-Item -LiteralPath $tmp -Destination $script:LayoutStore_Path -Force
    }
    catch {
        Write-FenceLog "Failed to save layout: $($_.Exception.Message)"
    }
}

function New-FenceModel {
    param(
        [string]$Title = 'New Fence',
        [string]$Mode = 'items',
        [string]$PortalPath = $null,
        [int]$X = 100,
        [int]$Y = 100,
        [int]$Width = 360,
        [int]$Height = 200
    )
    $tabId = [guid]::NewGuid().ToString()
    return [ordered]@{
        id          = [guid]::NewGuid().ToString()
        title       = $Title
        x           = $X
        y           = $Y
        width       = $Width
        height      = $Height
        rolledUp    = $false
        opacity     = 0.72
        bgColor     = '#0F1724'
        mode        = $Mode
        activeTabId = $tabId
        portalPath  = $PortalPath
        groupId     = $null
        locked      = $false
        tabs        = @(
            [ordered]@{
                id    = $tabId
                title = $Title
                items = @()
            }
        )
    }
}

function Ensure-FenceModelFields {
    param($FenceModel)
    if ($null -eq $FenceModel) { return $FenceModel }
    try {
        if ($FenceModel -is [System.Collections.IDictionary]) {
            if (-not ($FenceModel.Contains('groupId') -or $FenceModel.ContainsKey('groupId'))) {
                $FenceModel['groupId'] = $null
            }
            if (-not ($FenceModel.Contains('locked') -or $FenceModel.ContainsKey('locked'))) {
                $FenceModel['locked'] = $false
            }
            if (-not ($FenceModel.Contains('bgColor') -or $FenceModel.ContainsKey('bgColor'))) {
                $FenceModel['bgColor'] = '#0F1724'
            }
            return $FenceModel
        }
    }
    catch { }
    try {
        $names = @($FenceModel.PSObject.Properties.Name)
        if ($names -notcontains 'groupId') {
            $FenceModel | Add-Member -NotePropertyName groupId -NotePropertyValue $null -Force
        }
        if ($names -notcontains 'locked') {
            $FenceModel | Add-Member -NotePropertyName locked -NotePropertyValue $false -Force
        }
        if ($names -notcontains 'bgColor') {
            $FenceModel | Add-Member -NotePropertyName bgColor -NotePropertyValue '#0F1724' -Force
        }
    }
    catch { }
    return $FenceModel
}

function Test-FenceIsLocked {
    param([string]$FenceId)
    $m = Find-FenceModel -Id $FenceId
    if ($null -eq $m) { return $false }
    Ensure-FenceModelFields $m | Out-Null
    try { return [bool]$m.locked } catch { return $false }
}

function Set-FenceLocked {
    param(
        [string]$FenceId,
        [bool]$Locked
    )
    $m = Find-FenceModel -Id $FenceId
    if ($null -eq $m) { return }
    Ensure-FenceModelFields $m | Out-Null

    # Linked fences lock/unlock together
    $ids = @($FenceId)
    if ($m.groupId) {
        foreach ($f in (Get-FencesInGroup -GroupId $m.groupId)) {
            if ($f.id -and ($ids -notcontains $f.id)) {
                $ids += $f.id
            }
        }
    }

    foreach ($id in $ids) {
        $fm = Find-FenceModel -Id $id
        if ($null -eq $fm) { continue }
        Ensure-FenceModelFields $fm | Out-Null
        $fm.locked = $Locked
        Update-FenceModelInLayout -FenceModel $fm
        if ($script:FenceWindows.ContainsKey($id)) {
            try { Update-FenceLockChrome -FenceId $id } catch { }
        }
    }
    Write-FenceLog ("Set lock=$Locked on {0} fence(s)" -f $ids.Count)
}

function Set-AllFencesLocked {
    param([bool]$Locked)
    foreach ($f in @($script:Layout.fences)) {
        Ensure-FenceModelFields $f | Out-Null
        $f.locked = $Locked
        Update-FenceModelInLayout -FenceModel $f
        if ($script:FenceWindows.ContainsKey($f.id)) {
            Update-FenceLockChrome -FenceId $f.id
        }
    }
}

function Get-FencesInGroup {
    param([string]$GroupId)
    if ([string]::IsNullOrWhiteSpace($GroupId)) { return @() }
    return @($script:Layout.fences | Where-Object {
        $g = $null
        try { $g = $_.groupId } catch { }
        $g -and ($g -eq $GroupId)
    })
}

function Find-FenceModel {
    param([string]$Id)
    foreach ($f in @($script:Layout.fences)) {
        if ($f.id -eq $Id) { return $f }
    }
    return $null
}

function Update-FenceModelInLayout {
    param($FenceModel)
    if ($null -eq $script:Layout -or $null -eq $FenceModel) { return }
    $fences = @($script:Layout.fences)
    if ($fences.Count -eq 0) { return }
    $updated = $false
    for ($i = 0; $i -lt $fences.Count; $i++) {
        if ($null -ne $fences[$i] -and $fences[$i].id -eq $FenceModel.id) {
            $fences[$i] = $FenceModel
            $updated = $true
            break
        }
    }
    if (-not $updated) { return }
    $script:Layout.fences = $fences
    Save-FenceLayout -Layout $script:Layout
}

function Remove-FenceModelFromLayout {
    param([string]$Id)
    $script:Layout.fences = @($script:Layout.fences | Where-Object { $_.id -ne $Id })
    Save-FenceLayout -Layout $script:Layout -Immediate
}

function Add-FenceModelToLayout {
    param($FenceModel)
    $script:Layout.fences = @($script:Layout.fences) + @($FenceModel)
    Save-FenceLayout -Layout $script:Layout -Immediate
}
