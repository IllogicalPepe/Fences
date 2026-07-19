# WPF fence window factory for FenceDesk

$script:FenceWindows = @{}  # id -> @{ Window; Model; Elements... }
$script:DesktopIconMetrics = $null
$script:DefaultFenceBgColor = '#0F1724'

function Get-DesktopIconMetrics {
    # Match Windows desktop icon size + label font (medium icons default 48px)
    if ($null -ne $script:DesktopIconMetrics) {
        return $script:DesktopIconMetrics
    }

    $iconSize = 48
    try {
        $bag = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\Shell\Bags\1\Desktop' -ErrorAction SilentlyContinue
        if ($null -ne $bag -and $null -ne $bag.IconSize) {
            $v = [int]$bag.IconSize
            if ($v -ge 16 -and $v -le 256) { $iconSize = $v }
        }
    }
    catch { }

    if ($iconSize -eq 48) {
        try {
            $sysW = [int][System.Windows.SystemParameters]::IconWidth
            if ($sysW -ge 32 -and $sysW -le 256 -and $sysW -ne 32) {
                # SystemParameters often returns 32 even for medium; only trust larger values
                $iconSize = $sysW
            }
        }
        catch { }
    }

    $fontSize = 12.0
    try {
        $iconFont = [System.Drawing.SystemFonts]::IconTitleFont
        if ($null -ne $iconFont -and $iconFont.Size -gt 0) {
            $fontSize = [double]$iconFont.Size
        }
    }
    catch {
        try {
            $fontSize = [double][System.Windows.SystemFonts]::MessageFontSize
        }
        catch { $fontSize = 12.0 }
    }

    # Keep label readable relative to icon size (desktop-like)
    if ($iconSize -le 32) {
        if ($fontSize -lt 10) { $fontSize = 11 }
    }
    elseif ($iconSize -le 48) {
        if ($fontSize -lt 11) { $fontSize = 12 }
    }
    else {
        if ($fontSize -lt 12) { $fontSize = 13 }
    }

    # Cell width/height roughly match desktop icon grid
    $tileWidth = [Math]::Max(72, $iconSize + 28)
    $labelMaxHeight = [Math]::Ceiling($fontSize * 2.4) + 4
    $tilePadV = 6
    $tilePadH = 4

    $script:DesktopIconMetrics = @{
        IconSize       = $iconSize
        FontSize       = $fontSize
        TileWidth      = $tileWidth
        LabelMaxHeight = $labelMaxHeight
        TilePadV       = $tilePadV
        TilePadH       = $tilePadH
        LabelMarginTop = 4
    }
    try {
        Write-FenceLog ("Desktop icon metrics: size={0} font={1:N1} tileW={2}" -f $iconSize, $fontSize, $tileWidth)
    }
    catch { }
    return $script:DesktopIconMetrics
}

function ConvertFrom-HexColor {
    param([string]$Hex, [byte]$DefaultR = 15, [byte]$DefaultG = 23, [byte]$DefaultB = 36)
    $r = $DefaultR; $g = $DefaultG; $b = $DefaultB
    if ([string]::IsNullOrWhiteSpace($Hex)) {
        return @{ R = $r; G = $g; B = $b }
    }
    $h = $Hex.Trim()
    if ($h.StartsWith('#')) { $h = $h.Substring(1) }
    if ($h.Length -eq 8) { $h = $h.Substring(2) } # strip alpha if ARGB
    if ($h -match '^[0-9A-Fa-f]{6}$') {
        try {
            $r = [Convert]::ToByte($h.Substring(0, 2), 16)
            $g = [Convert]::ToByte($h.Substring(2, 2), 16)
            $b = [Convert]::ToByte($h.Substring(4, 2), 16)
        }
        catch { }
    }
    return @{ R = $r; G = $g; B = $b }
}

function ConvertTo-HexColor {
    param([byte]$R, [byte]$G, [byte]$B)
    return ('#{0:X2}{1:X2}{2:X2}' -f $R, $G, $B)
}

function Get-FenceBrush {
    param(
        [double]$Opacity = 0.72,
        [string]$HexColor = $null
    )
    $a = [byte][Math]::Max(0, [Math]::Min(255, [int](255 * $Opacity)))
    $rgb = ConvertFrom-HexColor -Hex $HexColor
    $c = [System.Windows.Media.Color]::FromArgb($a, $rgb.R, $rgb.G, $rgb.B)
    $b = New-Object System.Windows.Media.SolidColorBrush $c
    $b.Freeze()
    return $b
}

function Get-DefaultFenceBgColor {
    if ($script:DefaultFenceBgColor) { return [string]$script:DefaultFenceBgColor }
    return '#0F1724'
}

function Get-FenceBgColorHex {
    param($FenceModel)
    if ($null -eq $FenceModel) { return (Get-DefaultFenceBgColor) }
    try {
        Ensure-FenceModelFields $FenceModel | Out-Null
        if ($FenceModel.bgColor) { return [string]$FenceModel.bgColor }
    }
    catch { }
    return (Get-DefaultFenceBgColor)
}

function Get-FenceTitleBrush {
    $b = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(200, 208, 220))
    $b.Freeze()
    return $b
}

function Get-FenceMutedBrush {
    $b = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(140, 150, 165))
    $b.Freeze()
    return $b
}

function Get-FenceBorderBrush {
    $b = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromArgb(40, 255, 255, 255))
    $b.Freeze()
    return $b
}

function Get-FenceHoverBrush {
    $b = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromArgb(40, 255, 255, 255))
    $b.Freeze()
    return $b
}

function New-FenceItemTile {
    param(
        [string]$Path,
        [string]$Label,
        [string]$FenceId,
        [scriptblock]$OnRemove
    )
    $metrics = Get-DesktopIconMetrics
    $iconSize = [int]$metrics.IconSize
    $fontSize = [double]$metrics.FontSize
    $tileW = [int]$metrics.TileWidth
    $labelH = [int]$metrics.LabelMaxHeight
    $padH = [int]$metrics.TilePadH
    $padV = [int]$metrics.TilePadV
    $labelTop = [int]$metrics.LabelMarginTop

    $border = New-Object System.Windows.Controls.Border
    $border.Width = $tileW
    $border.Margin = New-Object System.Windows.Thickness 2
    $border.Padding = New-Object System.Windows.Thickness $padH, $padV, $padH, $padV
    $border.CornerRadius = New-Object System.Windows.CornerRadius 4
    $border.Background = [System.Windows.Media.Brushes]::Transparent
    $border.Cursor = [System.Windows.Input.Cursors]::Hand
    $border.ToolTip = $Path

    $stack = New-Object System.Windows.Controls.StackPanel
    $stack.HorizontalAlignment = 'Center'

    $img = New-Object System.Windows.Controls.Image
    $img.Width = $iconSize
    $img.Height = $iconSize
    $img.Stretch = 'Uniform'
    $img.HorizontalAlignment = 'Center'
    try {
        $img.Source = Get-FenceItemImage -Path $Path -Size $iconSize
    }
    catch {
        $img.Source = Get-DefaultFileImageSource -Size $iconSize
    }

    $tb = New-Object System.Windows.Controls.TextBlock
    $tb.Text = $Label
    $tb.FontSize = $fontSize
    try {
        $tb.FontFamily = [System.Windows.SystemFonts]::MessageFontFamily
    }
    catch { }
    $tb.Foreground = Get-FenceTitleBrush
    $tb.TextAlignment = 'Center'
    $tb.TextWrapping = 'Wrap'
    $tb.TextTrimming = 'CharacterEllipsis'
    $tb.MaxHeight = $labelH
    $tb.Margin = New-Object System.Windows.Thickness 0, $labelTop, 0, 0
    $tb.HorizontalAlignment = 'Center'
    $tb.Width = [Math]::Max(48, $tileW - ($padH * 2))

    [void]$stack.Children.Add($img)
    [void]$stack.Children.Add($tb)
    $border.Child = $stack

    $border.Add_MouseEnter({
        $this.Background = Get-FenceHoverBrush
    }.GetNewClosure())
    $border.Add_MouseLeave({
        $this.Background = [System.Windows.Media.Brushes]::Transparent
    }.GetNewClosure())

    $border.Add_MouseLeftButtonDown({
        param($s, $e)
        if ($e.ClickCount -ge 2) {
            Invoke-FenceItemLaunch -Path $Path
            $e.Handled = $true
        }
    }.GetNewClosure())

    $cm = New-Object System.Windows.Controls.ContextMenu
    $miOpen = New-Object System.Windows.Controls.MenuItem
    $miOpen.Header = 'Open'
    $miOpen.Add_Click({
        Invoke-FenceItemLaunch -Path $Path
    }.GetNewClosure())
    $miExplorer = New-Object System.Windows.Controls.MenuItem
    $miExplorer.Header = 'Show in Explorer'
    $miExplorer.Add_Click({
        try {
            $rp = $Path
            if (Get-Command Resolve-FenceItemPath -ErrorAction SilentlyContinue) {
                $rp = Resolve-FenceItemPath -Path $Path
            }
            if (Test-IsShellNamespacePath -Path $rp) {
                Invoke-FenceItemLaunch -Path $rp
                return
            }
            if (Test-Path -LiteralPath $rp) {
                Start-Process -FilePath 'explorer.exe' -ArgumentList "/select,`"$rp`""
            }
        }
        catch { }
    }.GetNewClosure())
    $miRemove = New-Object System.Windows.Controls.MenuItem
    $miRemove.Header = 'Remove from fence'
    $miRemove.Add_Click({
        if ($OnRemove) { & $OnRemove $Path }
    }.GetNewClosure())
    [void]$cm.Items.Add($miOpen)
    [void]$cm.Items.Add($miExplorer)
    [void]$cm.Items.Add((New-Object System.Windows.Controls.Separator))
    [void]$cm.Items.Add($miRemove)
    $border.ContextMenu = $cm

    return $border
}

function Update-FenceContent {
    param([string]$FenceId)

    if (-not $script:FenceWindows.ContainsKey($FenceId)) { return }
    $entry = $script:FenceWindows[$FenceId]
    $model = Find-FenceModel -Id $FenceId
    if ($null -eq $model) { return }
    $entry.Model = $model

    $panel = $entry.ItemsPanel
    $panel.Children.Clear()

    $hint = $entry.HintText
    $items = @()

    if ($model.mode -eq 'portal') {
        $items = @(Get-PortalItems -FolderPath $model.portalPath)
        $entry.TitleText.Text = if ($model.title) { $model.title } else { 'Portal' }
        Update-FenceLockChrome -FenceId $FenceId
    }
    else {
        $tab = $null
        foreach ($t in @($model.tabs)) {
            if ($t.id -eq $model.activeTabId) { $tab = $t; break }
        }
        if ($null -eq $tab -and @($model.tabs).Count -gt 0) {
            $tab = @($model.tabs)[0]
            $model.activeTabId = $tab.id
        }
        if ($null -ne $tab) {
            $items = @($tab.items)
        }
        $entry.TitleText.Text = $model.title
        Update-FenceLockChrome -FenceId $FenceId
    }

    # Tabs strip
    $tabStrip = $entry.TabStrip
    $tabStrip.Children.Clear()
    # Tab strip only when 2+ tabs (never stick a single uncloseable tab under the title)
    $tabCount = @($model.tabs).Count
    if ($tabCount -le 1 -and $entry.ForceShowTabs) {
        $entry.ForceShowTabs = $false
    }
    $showTabs = ($model.mode -ne 'portal') -and ($tabCount -gt 1)
    $tabStrip.Visibility = if ($showTabs) { 'Visible' } else { 'Collapsed' }

    if ($showTabs) {
        foreach ($t in @($model.tabs)) {
            $btn = New-Object System.Windows.Controls.Button
            $btn.Content = $t.title
            $btn.Padding = New-Object System.Windows.Thickness 8, 2, 8, 2
            $btn.Margin = New-Object System.Windows.Thickness 0, 0, 4, 0
            $btn.FontSize = 11
            $btn.Cursor = [System.Windows.Input.Cursors]::Hand
            $btn.BorderThickness = New-Object System.Windows.Thickness 0
            $isActive = ($t.id -eq $model.activeTabId)
            if ($isActive) {
                $btn.Background = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromArgb(60, 255, 255, 255))
                $btn.Foreground = Get-FenceTitleBrush
            }
            else {
                $btn.Background = [System.Windows.Media.Brushes]::Transparent
                $btn.Foreground = Get-FenceMutedBrush
            }
            $tid = $t.id
            $btn.Add_Click({
                $m = Find-FenceModel -Id $FenceId
                if ($null -eq $m) { return }
                $m.activeTabId = $tid
                Update-FenceModelInLayout -FenceModel $m
                Update-FenceContent -FenceId $FenceId
            }.GetNewClosure())
            # Right-click rename tab
            $tcm = New-Object System.Windows.Controls.ContextMenu
            $miRen = New-Object System.Windows.Controls.MenuItem
            $miRen.Header = 'Rename tab'
            $miRen.Add_Click({
                $m = Find-FenceModel -Id $FenceId
                if ($null -eq $m) { return }
                $name = Show-FenceInputDialog -Title 'Rename tab' -Prompt 'Tab name:' -DefaultText $t.title
                if ($null -eq $name -or $name.Trim() -eq '') { return }
                foreach ($tt in @($m.tabs)) {
                    if ($tt.id -eq $tid) { $tt.title = $name.Trim(); break }
                }
                Update-FenceModelInLayout -FenceModel $m
                Update-FenceContent -FenceId $FenceId
            }.GetNewClosure())
            $miClose = New-Object System.Windows.Controls.MenuItem
            $miClose.Header = 'Close tab'
            $miClose.Add_Click({
                $m = Find-FenceModel -Id $FenceId
                if ($null -eq $m) { return }
                $remaining = @($m.tabs | Where-Object { $_.id -ne $tid })
                if ($remaining.Count -eq 0) {
                    # Should not happen; keep one data tab, hide strip
                    if ($script:FenceWindows.ContainsKey($FenceId)) {
                        $script:FenceWindows[$FenceId].ForceShowTabs = $false
                    }
                    Update-FenceContent -FenceId $FenceId
                    return
                }
                $m.tabs = $remaining
                if ($m.activeTabId -eq $tid) {
                    $m.activeTabId = $remaining[0].id
                }
                # Hide strip when only one tab left
                if ($remaining.Count -le 1 -and $script:FenceWindows.ContainsKey($FenceId)) {
                    $script:FenceWindows[$FenceId].ForceShowTabs = $false
                }
                Update-FenceModelInLayout -FenceModel $m
                Update-FenceContent -FenceId $FenceId
            }.GetNewClosure())
            [void]$tcm.Items.Add($miRen)
            [void]$tcm.Items.Add($miClose)
            $btn.ContextMenu = $tcm
            [void]$tabStrip.Children.Add($btn)
        }
    }

    if (@($items).Count -eq 0) {
        $hint.Visibility = 'Visible'
        if ($model.mode -eq 'portal') {
            $hint.Text = if ($model.portalPath) { "Portal is empty`n$($model.portalPath)" } else { 'No folder selected for portal' }
        }
        else {
            $hint.Text = "Drop files here`nRight-click for options"
        }
    }
    else {
        $hint.Visibility = 'Collapsed'
        foreach ($it in $items) {
            $path = $it.path
            $label = if ($it.label) { $it.label } else { Get-ItemDisplayLabel -Path $path }
            $onRemove = {
                param($p)
                Remove-ItemFromFence -FenceId $FenceId -Path $p
            }.GetNewClosure()
            $tile = New-FenceItemTile -Path $path -Label $label -FenceId $FenceId -OnRemove $onRemove
            # In portal mode, hide "Remove from fence" meaning
            if ($model.mode -eq 'portal') {
                $tile.ContextMenu.Items.Clear()
                $miOpen = New-Object System.Windows.Controls.MenuItem
                $miOpen.Header = 'Open'
                $miOpen.Add_Click({ Invoke-FenceItemLaunch -Path $path }.GetNewClosure())
                $miEx = New-Object System.Windows.Controls.MenuItem
                $miEx.Header = 'Show in Explorer'
                $miEx.Add_Click({
                    try { Start-Process -FilePath 'explorer.exe' -ArgumentList "/select,`"$path`"" } catch { }
                }.GetNewClosure())
                [void]$tile.ContextMenu.Items.Add($miOpen)
                [void]$tile.ContextMenu.Items.Add($miEx)
            }
            [void]$panel.Children.Add($tile)
        }
    }

    # Roll-up
    Apply-FenceRollUp -FenceId $FenceId
}

function Apply-FenceRollUp {
    param([string]$FenceId)
    if (-not $script:FenceWindows.ContainsKey($FenceId)) { return }
    $entry = $script:FenceWindows[$FenceId]
    $model = Find-FenceModel -Id $FenceId
    if ($null -eq $model) { return }
    $win = $entry.Window
    if ($null -eq $win) { return }

    # Suppress SizeChanged/Sync while we change Height — otherwise expand
    # overwrites ExpandedHeight with the collapsed (or MinHeight-clamped) size.
    $entry.SuppressGeometrySync = $true
    try {
        if ($model.rolledUp) {
            # Capture expanded size before collapsing (prefer live window height)
            $curH = 0.0
            try {
                if ($win.ActualHeight -gt 40) { $curH = [double]$win.ActualHeight }
                elseif ($win.Height -gt 40) { $curH = [double]$win.Height }
            }
            catch { }
            if ($curH -gt 40) {
                $entry.ExpandedHeight = $curH
            }
            elseif (-not $entry.ExpandedHeight -or $entry.ExpandedHeight -lt 40) {
                $mh = 0
                try { $mh = [double]$model.height } catch { }
                $entry.ExpandedHeight = [Math]::Max(120, $mh)
            }
            try { $model.height = [int][Math]::Round([double]$entry.ExpandedHeight) } catch { }

            $entry.Body.Visibility = 'Collapsed'
            $entry.TabStrip.Visibility = 'Collapsed'
            $win.MinHeight = 28
            $win.Height = 32
            $win.ResizeMode = 'NoResize'
            try {
                if ($entry.RollButton) { $entry.RollButton.Content = [char]0x25BC }  # down triangle
            }
            catch { }
        }
        else {
            # Snapshot restore height BEFORE any MinHeight/Height changes trigger SizeChanged
            $restoreH = 0.0
            try {
                if ($entry.ExpandedHeight -and [double]$entry.ExpandedHeight -gt 40) {
                    $restoreH = [double]$entry.ExpandedHeight
                }
            }
            catch { }
            if ($restoreH -lt 40) {
                try {
                    if ($model.height -gt 40) { $restoreH = [double]$model.height }
                }
                catch { }
            }
            if ($restoreH -lt 40) { $restoreH = 200.0 }

            $entry.Body.Visibility = 'Visible'
            $win.MinHeight = 80
            $win.ResizeMode = 'NoResize'
            $win.Height = [Math]::Max(80.0, $restoreH)
            $entry.ExpandedHeight = [double]$win.Height
            try { $model.height = [int][Math]::Round([double]$win.Height) } catch { }

            # Re-show tabs only when multi-tab
            $showTabs = ($model.mode -ne 'portal') -and (@($model.tabs).Count -gt 1)
            $entry.TabStrip.Visibility = if ($showTabs) { 'Visible' } else { 'Collapsed' }
            try {
                if ($entry.RollButton) { $entry.RollButton.Content = [char]0x25B2 }  # up triangle
            }
            catch { }
        }
    }
    finally {
        $entry.SuppressGeometrySync = $false
        # Re-store entry in case caller held a copy (hashtable is by ref, but be explicit)
        $script:FenceWindows[$FenceId] = $entry
    }
}

function Add-ItemToFence {
    param(
        [string]$FenceId,
        [string[]]$Paths
    )
    $model = Find-FenceModel -Id $FenceId
    if ($null -eq $model) { return }

    if ($model.mode -eq 'portal') {
        # Copy/move into portal folder on drop
        $destRoot = $model.portalPath
        if (-not $destRoot -or -not (Test-Path -LiteralPath $destRoot)) {
            [System.Windows.MessageBox]::Show('Portal folder is not available.', 'FenceDesk') | Out-Null
            return
        }
        foreach ($p in $Paths) {
            try {
                if (-not (Test-Path -LiteralPath $p)) { continue }
                $name = [System.IO.Path]::GetFileName($p)
                $dest = Join-Path $destRoot $name
                if ((Test-Path -LiteralPath $dest)) { continue }
                $item = Get-Item -LiteralPath $p -Force
                if ($item.PSIsContainer) {
                    Copy-Item -LiteralPath $p -Destination $dest -Recurse -Force -ErrorAction SilentlyContinue
                }
                else {
                    Copy-Item -LiteralPath $p -Destination $dest -Force -ErrorAction SilentlyContinue
                }
            }
            catch {
                Write-FenceLog "Portal drop failed: $($_.Exception.Message)"
            }
        }
        Update-FenceContent -FenceId $FenceId
        return
    }

    $tab = $null
    foreach ($t in @($model.tabs)) {
        if ($t.id -eq $model.activeTabId) { $tab = $t; break }
    }
    if ($null -eq $tab) {
        if (@($model.tabs).Count -eq 0) {
            $tid = [guid]::NewGuid().ToString()
            $tab = [ordered]@{ id = $tid; title = 'Items'; items = @() }
            $model.tabs = @($tab)
            $model.activeTabId = $tid
        }
        else {
            $tab = @($model.tabs)[0]
            $model.activeTabId = $tab.id
        }
    }

    $existing = @{}
    foreach ($it in @($tab.items)) {
        $existing[$it.path.ToLowerInvariant()] = $true
    }
    $list = [System.Collections.ArrayList]@($tab.items)
    foreach ($p in $Paths) {
        if ([string]::IsNullOrWhiteSpace($p)) { continue }
        # Normalize recycle bin / shell drops if Windows provides a weird path
        $norm = $p
        if ($p -match 'Recycle' -or $p -match '645FF040') {
            $norm = Get-RecycleBinPath
        }
        $key = $norm.ToLowerInvariant()
        if ($existing.ContainsKey($key)) { continue }
        [void]$list.Add([ordered]@{
            path  = $norm
            label = Get-ItemDisplayLabel -Path $norm
        })
        $existing[$key] = $true
    }
    $tab.items = @($list.ToArray())

    # write back tab into model
    $newTabs = @()
    foreach ($t in @($model.tabs)) {
        if ($t.id -eq $tab.id) { $newTabs += $tab } else { $newTabs += $t }
    }
    $model.tabs = $newTabs
    Update-FenceModelInLayout -FenceModel $model
    Update-FenceContent -FenceId $FenceId
    try { Sync-DesktopIconVisibility } catch { }
}

function Remove-ItemFromFence {
    param(
        [string]$FenceId,
        [string]$Path
    )
    $model = Find-FenceModel -Id $FenceId
    if ($null -eq $model -or $model.mode -eq 'portal') { return }
    $newTabs = @()
    foreach ($t in @($model.tabs)) {
        if ($t.id -eq $model.activeTabId) {
            $t.items = @($t.items | Where-Object { $_.path -ne $Path })
        }
        $newTabs += $t
    }
    $model.tabs = $newTabs
    Update-FenceModelInLayout -FenceModel $model
    Update-FenceContent -FenceId $FenceId
    try { Sync-DesktopIconVisibility } catch { }
}

function Show-FenceInputDialog {
    param(
        [string]$Title = 'FenceDesk',
        [string]$Prompt = 'Value:',
        [string]$DefaultText = ''
    )
    $win = New-Object System.Windows.Window
    $win.Title = $Title
    $win.Width = 360
    $win.Height = 150
    $win.WindowStartupLocation = 'CenterScreen'
    $win.ResizeMode = 'NoResize'
    $win.ShowInTaskbar = $false
    $win.Background = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(30, 36, 48))
    try { Register-WindowExcludeFromAltTab -Window $win } catch { }

    $grid = New-Object System.Windows.Controls.Grid
    $grid.Margin = New-Object System.Windows.Thickness 16
    $r0 = New-Object System.Windows.Controls.RowDefinition
    $r0.Height = [System.Windows.GridLength]::Auto
    $r1 = New-Object System.Windows.Controls.RowDefinition
    $r1.Height = [System.Windows.GridLength]::Auto
    $r2 = New-Object System.Windows.Controls.RowDefinition
    $r2.Height = [System.Windows.GridLength]::Auto
    [void]$grid.RowDefinitions.Add($r0)
    [void]$grid.RowDefinitions.Add($r1)
    [void]$grid.RowDefinitions.Add($r2)

    $lbl = New-Object System.Windows.Controls.TextBlock
    $lbl.Text = $Prompt
    $lbl.Foreground = Get-FenceTitleBrush
    $lbl.Margin = New-Object System.Windows.Thickness 0, 0, 0, 8
    [System.Windows.Controls.Grid]::SetRow($lbl, 0)

    $tb = New-Object System.Windows.Controls.TextBox
    $tb.Text = $DefaultText
    $tb.Padding = New-Object System.Windows.Thickness 6
    [System.Windows.Controls.Grid]::SetRow($tb, 1)

    $sp = New-Object System.Windows.Controls.StackPanel
    $sp.Orientation = 'Horizontal'
    $sp.HorizontalAlignment = 'Right'
    $sp.Margin = New-Object System.Windows.Thickness 0, 12, 0, 0
    [System.Windows.Controls.Grid]::SetRow($sp, 2)

    $result = @{ value = $null }
    $ok = New-Object System.Windows.Controls.Button
    $ok.Content = 'OK'
    $ok.Width = 80
    $ok.Margin = New-Object System.Windows.Thickness 0, 0, 8, 0
    $ok.IsDefault = $true
    $ok.Add_Click({
        $result.value = $tb.Text
        $win.DialogResult = $true
        $win.Close()
    }.GetNewClosure())
    $cancel = New-Object System.Windows.Controls.Button
    $cancel.Content = 'Cancel'
    $cancel.Width = 80
    $cancel.IsCancel = $true
    $cancel.Add_Click({ $win.Close() }.GetNewClosure())
    [void]$sp.Children.Add($ok)
    [void]$sp.Children.Add($cancel)
    [void]$grid.Children.Add($lbl)
    [void]$grid.Children.Add($tb)
    [void]$grid.Children.Add($sp)
    $win.Content = $grid
    $tb.Focus() | Out-Null
    $tb.SelectAll()
    $null = $win.ShowDialog()
    return $result.value
}

function Show-FolderPicker {
    param([string]$Description = 'Select folder for portal')
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = $Description
    $dlg.ShowNewFolderButton = $true
    if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        return $dlg.SelectedPath
    }
    return $null
}

function Get-FenceGroupPickerOptions {
    <#
      One list entry per ungrouped fence, and one entry per existing group
      (merged title like "Files & Downloads (grouped)").
      Returns TargetFenceId = any member id to join that group / fence.
    #>
    param([string]$ExcludeId = $null)

    $excludeIds = @()
    if ($ExcludeId) {
        $excludeIds += $ExcludeId
        $self = Find-FenceModel -Id $ExcludeId
        if ($null -ne $self) {
            Ensure-FenceModelFields $self | Out-Null
            if ($self.groupId) {
                foreach ($f in (Get-FencesInGroup -GroupId $self.groupId)) {
                    if ($f.id -and ($excludeIds -notcontains $f.id)) {
                        $excludeIds += $f.id
                    }
                }
            }
        }
    }

    $options = @()
    $seenGroupIds = @{}

    foreach ($f in @($script:Layout.fences)) {
        if ($null -eq $f -or -not $f.id) { continue }
        if ($excludeIds -contains $f.id) { continue }
        Ensure-FenceModelFields $f | Out-Null

        if ($f.groupId) {
            if ($seenGroupIds.ContainsKey([string]$f.groupId)) { continue }
            $seenGroupIds[[string]$f.groupId] = $true

            $members = @(Get-FencesInGroup -GroupId $f.groupId | Sort-Object { $_.title })
            if ($members.Count -eq 0) { continue }
            $titles = @($members | ForEach-Object { $_.title } | Where-Object { $_ })
            if ($titles.Count -eq 0) { $titles = @('Group') }
            $label = ($titles -join ' & ') + ' (grouped)'
            $options += [pscustomobject]@{
                Id    = $members[0].id   # any member — Join merges into that group
                Label = $label
                Kind  = 'group'
            }
        }
        else {
            $options += [pscustomobject]@{
                Id    = $f.id
                Label = $f.title
                Kind  = 'fence'
            }
        }
    }

    return $options
}

function Show-FencePickerDialog {
    param(
        [string]$Title = 'Choose a fence',
        [string]$Prompt = 'Select a fence or group:',
        [string]$ExcludeId = $null
    )
    $options = @(Get-FenceGroupPickerOptions -ExcludeId $ExcludeId)
    if ($options.Count -eq 0) {
        [System.Windows.MessageBox]::Show('No other fences or groups to attach to. Create another fence first.', 'FenceDesk') | Out-Null
        return $null
    }

    $win = New-Object System.Windows.Window
    $win.Title = $Title
    $win.Width = 400
    $win.Height = 300
    $win.WindowStartupLocation = 'CenterScreen'
    $win.ResizeMode = 'NoResize'
    $win.Background = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(30, 36, 48))
    $win.ShowInTaskbar = $false
    try { Register-WindowExcludeFromAltTab -Window $win } catch { }

    $grid = New-Object System.Windows.Controls.Grid
    $grid.Margin = New-Object System.Windows.Thickness 16
    $r0 = New-Object System.Windows.Controls.RowDefinition
    $r0.Height = [System.Windows.GridLength]::Auto
    $r1 = New-Object System.Windows.Controls.RowDefinition
    $r1.Height = New-Object System.Windows.GridLength 1, 'Star'
    $r2 = New-Object System.Windows.Controls.RowDefinition
    $r2.Height = [System.Windows.GridLength]::Auto
    [void]$grid.RowDefinitions.Add($r0)
    [void]$grid.RowDefinitions.Add($r1)
    [void]$grid.RowDefinitions.Add($r2)

    $lbl = New-Object System.Windows.Controls.TextBlock
    $lbl.Text = $Prompt
    $lbl.Foreground = Get-FenceTitleBrush
    $lbl.Margin = New-Object System.Windows.Thickness 0, 0, 0, 8
    $lbl.TextWrapping = 'Wrap'
    [System.Windows.Controls.Grid]::SetRow($lbl, 0)

    $list = New-Object System.Windows.Controls.ListBox
    $list.DisplayMemberPath = 'Label'
    $list.Background = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(24, 30, 42))
    $list.Foreground = Get-FenceTitleBrush
    $list.BorderThickness = New-Object System.Windows.Thickness 1
    $list.BorderBrush = Get-FenceBorderBrush
    foreach ($o in $options) { [void]$list.Items.Add($o) }
    if ($list.Items.Count -gt 0) { $list.SelectedIndex = 0 }
    [System.Windows.Controls.Grid]::SetRow($list, 1)

    $sp = New-Object System.Windows.Controls.StackPanel
    $sp.Orientation = 'Horizontal'
    $sp.HorizontalAlignment = 'Right'
    $sp.Margin = New-Object System.Windows.Thickness 0, 12, 0, 0
    [System.Windows.Controls.Grid]::SetRow($sp, 2)

    $result = @{ id = $null }
    $ok = New-Object System.Windows.Controls.Button
    $ok.Content = 'OK'
    $ok.Width = 80
    $ok.Margin = New-Object System.Windows.Thickness 0, 0, 8, 0
    $ok.IsDefault = $true
    $ok.Add_Click({
        if ($list.SelectedItem) {
            $result.id = $list.SelectedItem.Id
            $win.DialogResult = $true
            $win.Close()
        }
    }.GetNewClosure())
    $cancel = New-Object System.Windows.Controls.Button
    $cancel.Content = 'Cancel'
    $cancel.Width = 80
    $cancel.IsCancel = $true
    $cancel.Add_Click({ $win.Close() }.GetNewClosure())
    [void]$sp.Children.Add($ok)
    [void]$sp.Children.Add($cancel)
    [void]$grid.Children.Add($lbl)
    [void]$grid.Children.Add($list)
    [void]$grid.Children.Add($sp)
    $win.Content = $grid
    $null = $win.ShowDialog()
    return $result.id
}

function Join-FenceGroup {
    param(
        [string]$FenceId,
        [string]$TargetFenceId
    )
    $a = Find-FenceModel -Id $FenceId
    $b = Find-FenceModel -Id $TargetFenceId
    if ($null -eq $a -or $null -eq $b) { return }
    Ensure-FenceModelFields $a | Out-Null
    Ensure-FenceModelFields $b | Out-Null

    # Prefer target's group id so attaching to "Files & Downloads" keeps that group
    $gid = $null
    if ($b.groupId) { $gid = [string]$b.groupId }
    elseif ($a.groupId) { $gid = [string]$a.groupId }
    else { $gid = [guid]::NewGuid().ToString() }

    # Collect every fence that must end up in this group (merge both sides fully)
    $toMerge = @{}
    $toMerge[$a.id] = $a
    $toMerge[$b.id] = $b
    if ($a.groupId) {
        foreach ($f in (Get-FencesInGroup -GroupId $a.groupId)) { $toMerge[$f.id] = $f }
    }
    if ($b.groupId) {
        foreach ($f in (Get-FencesInGroup -GroupId $b.groupId)) { $toMerge[$f.id] = $f }
    }

    $lockGroup = $false
    foreach ($f in $toMerge.Values) {
        Ensure-FenceModelFields $f | Out-Null
        try { if ([bool]$f.locked) { $lockGroup = $true } } catch { }
    }

    $names = @()
    foreach ($f in $toMerge.Values) {
        Ensure-FenceModelFields $f | Out-Null
        $f.groupId = $gid
        $f.locked = $lockGroup
        Update-FenceModelInLayout -FenceModel $f
        if ($script:FenceWindows.ContainsKey($f.id)) {
            try { Update-FenceLockChrome -FenceId $f.id } catch { }
        }
        if ($f.title) { $names += $f.title }
    }
    Write-FenceLog ("Merged group [{0}] groupId=$gid locked=$lockGroup" -f ($names -join ', '))
}

function Leave-FenceGroup {
    param([string]$FenceId)
    $m = Find-FenceModel -Id $FenceId
    if ($null -eq $m) { return }
    Ensure-FenceModelFields $m | Out-Null
    $m.groupId = $null
    Update-FenceModelInLayout -FenceModel $m
}

function Get-SnappedFencePosition {
    <#
      Magnetic snap: when a fence is close to another, align edges / butt against them.
    #>
    param(
        [string]$FenceId,
        [double]$Left,
        [double]$Top,
        [double]$Width,
        [double]$Height,
        [double]$Threshold = 14
    )
    $bestLeft = $Left
    $bestTop = $Top
    $bestDx = $Threshold + 1
    $bestDy = $Threshold + 1
    $right = $Left + $Width
    $bottom = $Top + $Height
    $gap = 0.0  # flush snap; change to 4 for a small gutter

    foreach ($id in @($script:FenceWindows.Keys)) {
        if ($id -eq $FenceId) { continue }
        $entry = $script:FenceWindows[$id]
        if ($entry.ToggleHidden) { continue }
        $o = $entry.Window
        if ($null -eq $o) { continue }
        if ($o.Visibility -ne 'Visible') { continue }

        $oL = $o.Left
        $oT = $o.Top
        $oR = $o.Left + $o.ActualWidth
        $oB = $o.Top + $o.ActualHeight

        # --- Horizontal candidates ---
        $candidatesX = @(
            @{ v = $oL;               d = [Math]::Abs($Left - $oL) }                 # left align
            @{ v = $oR - $Width;      d = [Math]::Abs($right - $oR) }                # right align
            @{ v = $oR + $gap;        d = [Math]::Abs($Left - ($oR + $gap)) }        # stick to right of other
            @{ v = $oL - $Width - $gap; d = [Math]::Abs($right - ($oL - $gap)) }     # stick to left of other
        )
        foreach ($c in $candidatesX) {
            if ($c.d -le $Threshold -and $c.d -lt $bestDx) {
                $bestDx = $c.d
                $bestLeft = $c.v
            }
        }

        # --- Vertical candidates ---
        $candidatesY = @(
            @{ v = $oT;                d = [Math]::Abs($Top - $oT) }                  # top align
            @{ v = $oB - $Height;      d = [Math]::Abs($bottom - $oB) }               # bottom align
            @{ v = $oB + $gap;         d = [Math]::Abs($Top - ($oB + $gap)) }         # stick below
            @{ v = $oT - $Height - $gap; d = [Math]::Abs($bottom - ($oT - $gap)) }    # stick above
        )
        foreach ($c in $candidatesY) {
            if ($c.d -le $Threshold -and $c.d -lt $bestDy) {
                $bestDy = $c.d
                $bestTop = $c.v
            }
        }
    }

    # Optional: light snap to work-area edges
    try {
        $wa = [System.Windows.SystemParameters]::WorkArea
        $edgeCandidatesX = @(
            @{ v = $wa.Left; d = [Math]::Abs($Left - $wa.Left) }
            @{ v = $wa.Right - $Width; d = [Math]::Abs(($Left + $Width) - $wa.Right) }
        )
        foreach ($c in $edgeCandidatesX) {
            if ($c.d -le $Threshold -and $c.d -lt $bestDx) {
                $bestDx = $c.d
                $bestLeft = $c.v
            }
        }
        $edgeCandidatesY = @(
            @{ v = $wa.Top; d = [Math]::Abs($Top - $wa.Top) }
            @{ v = $wa.Bottom - $Height; d = [Math]::Abs(($Top + $Height) - $wa.Bottom) }
        )
        foreach ($c in $edgeCandidatesY) {
            if ($c.d -le $Threshold -and $c.d -lt $bestDy) {
                $bestDy = $c.d
                $bestTop = $c.v
            }
        }
    }
    catch { }

    return @{
        Left = $bestLeft
        Top  = $bestTop
        SnappedX = ($bestDx -le $Threshold)
        SnappedY = ($bestDy -le $Threshold)
    }
}

function Invoke-FenceSnap {
    param(
        [string]$FenceId,
        [System.Windows.Window]$Window
    )
    if ($null -eq $Window) { return }
    $w = $Window.ActualWidth
    if ($w -lt 1) { $w = $Window.Width }
    $h = $Window.ActualHeight
    if ($h -lt 1) { $h = $Window.Height }
    $snap = Get-SnappedFencePosition -FenceId $FenceId -Left $Window.Left -Top $Window.Top -Width $w -Height $h
    if ($snap.SnappedX -or $snap.SnappedY) {
        $Window.Left = $snap.Left
        $Window.Top = $snap.Top
    }
}

function Start-FenceGroupDrag {
    param(
        [string]$LeaderId,
        [System.Windows.Window]$LeaderWindow
    )
    $script:GroupDrag = $null
    $m = Find-FenceModel -Id $LeaderId
    if ($null -eq $m) { return }
    Ensure-FenceModelFields $m | Out-Null
    if (-not $m.groupId) { return }

    $members = @{}
    foreach ($f in (Get-FencesInGroup -GroupId $m.groupId)) {
        if (-not $script:FenceWindows.ContainsKey($f.id)) { continue }
        $w = $script:FenceWindows[$f.id].Window
        if ($null -eq $w) { continue }
        $members[$f.id] = @{
            Left = $w.Left
            Top  = $w.Top
        }
    }
    if ($members.Count -lt 2) { return }

    $script:GroupDrag = @{
        LeaderId     = $LeaderId
        OriginLeft   = $LeaderWindow.Left
        OriginTop    = $LeaderWindow.Top
        Members      = $members
        Applying     = $false
    }
}

function Update-FenceGroupDrag {
    param(
        [string]$LeaderId,
        [System.Windows.Window]$LeaderWindow
    )
    if ($null -eq $script:GroupDrag) { return }
    if ($script:GroupDrag.LeaderId -ne $LeaderId) { return }
    if ($script:GroupDrag.Applying) { return }

    $dx = $LeaderWindow.Left - $script:GroupDrag.OriginLeft
    $dy = $LeaderWindow.Top - $script:GroupDrag.OriginTop
    if ([Math]::Abs($dx) -lt 0.1 -and [Math]::Abs($dy) -lt 0.1) { return }

    $script:GroupDrag.Applying = $true
    try {
        foreach ($mid in @($script:GroupDrag.Members.Keys)) {
            if ($mid -eq $LeaderId) { continue }
            if (-not $script:FenceWindows.ContainsKey($mid)) { continue }
            $w = $script:FenceWindows[$mid].Window
            if ($null -eq $w) { continue }
            $start = $script:GroupDrag.Members[$mid]
            $w.Left = $start.Left + $dx
            $w.Top = $start.Top + $dy
        }
    }
    finally {
        $script:GroupDrag.Applying = $false
    }
}

function Stop-FenceGroupDrag {
    param([string]$LeaderId)
    if ($null -eq $script:GroupDrag) { return }
    if ($script:GroupDrag.LeaderId -ne $LeaderId) { return }
    try {
        foreach ($mid in @($script:GroupDrag.Members.Keys)) {
            Sync-FenceGeometry -FenceId $mid
        }
    }
    catch { }
    $script:GroupDrag = $null
}

function Get-FencePanelAlpha {
    param([double]$Opacity = 0.72)
    # Map slider 0..1 to panel glass alpha (keep a tiny floor so fully-clear still hits-tests)
    $op = [Math]::Max(0.0, [Math]::Min(1.0, [double]$Opacity))
    if ($op -le 0) { return 0.0 }
    # Slight curve so mid values look natural for a desktop panel
    return $op
}

function Update-FenceGlassAppearance {
    param(
        [string]$FenceId,
        $FenceModel = $null
    )
    if (-not $script:FenceWindows.ContainsKey($FenceId)) { return }
    $entry = $script:FenceWindows[$FenceId]
    $m = $FenceModel
    if ($null -eq $m) { $m = Find-FenceModel -Id $FenceId }
    if ($null -eq $m) { return }

    $op = 0.72
    try { if ($null -ne $m.opacity) { $op = [double]$m.opacity } } catch { }
    $op = [Math]::Max(0.0, [Math]::Min(1.0, $op))
    $hex = Get-FenceBgColorHex -FenceModel $m
    $alpha = Get-FencePanelAlpha -Opacity $op

    # Window stays fully opaque compositing; only the glass layer fades (icons stay crisp)
    try {
        if ($entry.Window) {
            $entry.Window.Opacity = 1.0
            $entry.Window.Background = [System.Windows.Media.Brushes]::Transparent
        }
    }
    catch { }

    $glass = $null
    if ($entry.Glass) { $glass = $entry.Glass }
    elseif ($entry.Outer) { $glass = $entry.Outer }

    if ($glass) {
        $glass.Background = Get-FenceBrush -Opacity $alpha -HexColor $hex
        try {
            # Soften border with panel so edges fade too
            $ba = [byte][Math]::Max(0, [Math]::Min(255, [int](40 * $alpha)))
            $bb = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromArgb($ba, 255, 255, 255))
            $bb.Freeze()
            $glass.BorderBrush = $bb
        }
        catch { }
    }
}

function Set-FenceOpacity {
    param(
        [string]$FenceId,
        [double]$Opacity
    )
    try {
        # 0 = clear glass, 1 = solid panel — icons/text are NOT faded
        $op = [Math]::Max(0.0, [Math]::Min(1.0, [double]$Opacity))
        $m = Find-FenceModel -Id $FenceId
        if ($null -eq $m) { return }
        $m.opacity = $op
        Update-FenceGlassAppearance -FenceId $FenceId -FenceModel $m
        Update-FenceModelInLayout -FenceModel $m
    }
    catch {
        Write-FenceLog "Set-FenceOpacity: $($_.Exception.Message)"
    }
}

function Set-FenceBackgroundColor {
    param(
        [string]$FenceId,
        [string]$HexColor,
        [switch]$SkipSave
    )
    try {
        $m = Find-FenceModel -Id $FenceId
        if ($null -eq $m) { return }
        Ensure-FenceModelFields $m | Out-Null
        $rgb = ConvertFrom-HexColor -Hex $HexColor
        $m.bgColor = ConvertTo-HexColor -R $rgb.R -G $rgb.G -B $rgb.B
        Update-FenceGlassAppearance -FenceId $FenceId -FenceModel $m
        if (-not $SkipSave) {
            Update-FenceModelInLayout -FenceModel $m
        }
        else {
            try {
                $fences = @($script:Layout.fences)
                for ($i = 0; $i -lt $fences.Count; $i++) {
                    if ($null -ne $fences[$i] -and $fences[$i].id -eq $m.id) {
                        $fences[$i] = $m
                        break
                    }
                }
                $script:Layout.fences = $fences
            }
            catch { }
        }
    }
    catch {
        Write-FenceLog "Set-FenceBackgroundColor: $($_.Exception.Message)"
    }
}

function Set-AllFencesBackgroundColor {
    param([string]$HexColor)
    try {
        $rgb = ConvertFrom-HexColor -Hex $HexColor
        $hex = ConvertTo-HexColor -R $rgb.R -G $rgb.G -B $rgb.B
        foreach ($f in @($script:Layout.fences)) {
            if ($null -eq $f -or -not $f.id) { continue }
            Set-FenceBackgroundColor -FenceId $f.id -HexColor $hex -SkipSave
        }
        Save-FenceLayout -Layout $script:Layout -Immediate
        Write-FenceLog ("Set background color $hex on all fences")
    }
    catch {
        Write-FenceLog "Set-AllFencesBackgroundColor: $($_.Exception.Message)"
    }
}

function Reset-FenceBackgroundColor {
    param([string]$FenceId)
    Set-FenceBackgroundColor -FenceId $FenceId -HexColor (Get-DefaultFenceBgColor)
}

function Reset-AllFencesBackgroundColor {
    Set-AllFencesBackgroundColor -HexColor (Get-DefaultFenceBgColor)
}

function Show-FenceColorDialog {
    param(
        [string]$FenceId,
        [switch]$ApplyToAll
    )
    $seedHex = Get-DefaultFenceBgColor
    if (-not $ApplyToAll) {
        $m = Find-FenceModel -Id $FenceId
        if ($null -eq $m) { return }
        Ensure-FenceModelFields $m | Out-Null
        $seedHex = Get-FenceBgColorHex -FenceModel $m
    }
    else {
        # Prefer the active fence color as the seed when painting all
        if ($FenceId) {
            $m = Find-FenceModel -Id $FenceId
            if ($null -ne $m) {
                Ensure-FenceModelFields $m | Out-Null
                $seedHex = Get-FenceBgColorHex -FenceModel $m
            }
        }
    }
    $rgb = ConvertFrom-HexColor -Hex $seedHex

    try {
        $dlg = New-Object System.Windows.Forms.ColorDialog
        $dlg.FullOpen = $true
        $dlg.AnyColor = $true
        $dlg.SolidColorOnly = $false
        $dlg.Color = [System.Drawing.Color]::FromArgb(255, $rgb.R, $rgb.G, $rgb.B)
        $dlg.AllowFullOpen = $true
        # COLORREF custom colors: 0x00BBGGRR (first slot = default)
        $dlg.CustomColors = @(
            [int](0x0024170F),  # default dark blue-gray
            [int](0x00462814),
            [int](0x00321428),
            [int](0x00283214),
            [int](0x00141E3C),
            [int](0x001E1E1E),
            [int](0x005A3C0A),
            [int](0x001E1432)
        )

        $result = $dlg.ShowDialog()
        if ($result -eq [System.Windows.Forms.DialogResult]::OK) {
            $c = $dlg.Color
            $newHex = ConvertTo-HexColor -R $c.R -G $c.G -B $c.B
            if ($ApplyToAll) {
                Set-AllFencesBackgroundColor -HexColor $newHex
            }
            else {
                Set-FenceBackgroundColor -FenceId $FenceId -HexColor $newHex
            }
        }
    }
    catch {
        Write-FenceLog "Show-FenceColorDialog: $($_.Exception.Message)"
        try {
            [System.Windows.MessageBox]::Show(
                "Could not open color picker:`n$($_.Exception.Message)",
                'FenceDesk'
            ) | Out-Null
        }
        catch { }
    }
}

function Show-OpacityDialog {
    param(
        [string]$FenceId,
        [double]$Current = 0.72
    )
    $original = $Current
    $win = New-Object System.Windows.Window
    $win.Title = 'Fence opacity'
    $win.Width = 380
    $win.Height = 170
    $win.WindowStartupLocation = 'CenterScreen'
    $win.ResizeMode = 'NoResize'
    $win.Background = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(30, 36, 48))
    $win.ShowInTaskbar = $false
    try { Register-WindowExcludeFromAltTab -Window $win } catch { }

    $grid = New-Object System.Windows.Controls.Grid
    $grid.Margin = New-Object System.Windows.Thickness 16
    foreach ($i in 0..3) {
        $rd = New-Object System.Windows.Controls.RowDefinition
        $rd.Height = [System.Windows.GridLength]::Auto
        [void]$grid.RowDefinitions.Add($rd)
    }

    $lbl = New-Object System.Windows.Controls.TextBlock
    $lbl.Text = 'Panel opacity (icons stay solid)'
    $lbl.Foreground = Get-FenceTitleBrush
    $lbl.Margin = New-Object System.Windows.Thickness 0, 0, 0, 8
    [System.Windows.Controls.Grid]::SetRow($lbl, 0)

    $valueLbl = New-Object System.Windows.Controls.TextBlock
    $valueLbl.Text = ('{0}%' -f [int][Math]::Round($Current * 100))
    $valueLbl.Foreground = Get-FenceTitleBrush
    $valueLbl.FontSize = 16
    $valueLbl.FontWeight = [System.Windows.FontWeights]::SemiBold
    $valueLbl.HorizontalAlignment = 'Center'
    $valueLbl.Margin = New-Object System.Windows.Thickness 0, 0, 0, 8
    [System.Windows.Controls.Grid]::SetRow($valueLbl, 1)

    $slider = New-Object System.Windows.Controls.Slider
    $slider.Minimum = 0
    $slider.Maximum = 100
    $slider.TickFrequency = 1
    $slider.IsSnapToTickEnabled = $true
    $slider.Value = [Math]::Max(0, [Math]::Min(100, [int][Math]::Round($Current * 100)))
    $slider.Margin = New-Object System.Windows.Thickness 0, 0, 0, 12
    [System.Windows.Controls.Grid]::SetRow($slider, 2)

    $sp = New-Object System.Windows.Controls.StackPanel
    $sp.Orientation = 'Horizontal'
    $sp.HorizontalAlignment = 'Right'
    [System.Windows.Controls.Grid]::SetRow($sp, 3)

    $result = @{ ok = $false; value = $Current }
    $win.Tag = @{
        FenceId  = $FenceId
        Original = $original
        ValueLbl = $valueLbl
        Result   = $result
    }
    $slider.Tag = $win

    $slider.Add_ValueChanged({
        param($s, $e)
        try {
            $hostWin = $s.Tag
            if ($null -eq $hostWin -or $null -eq $hostWin.Tag) { return }
            $state = $hostWin.Tag
            $pct = [int][Math]::Round($s.Value)
            if ($state.ValueLbl) { $state.ValueLbl.Text = ('{0}%' -f $pct) }
            Set-FenceOpacity -FenceId $state.FenceId -Opacity ($pct / 100.0)
        }
        catch {
            Write-FenceLog "Opacity slider: $($_.Exception.Message)"
        }
    })

    $ok = New-Object System.Windows.Controls.Button
    $ok.Content = 'OK'
    $ok.Width = 80
    $ok.Margin = New-Object System.Windows.Thickness 0, 0, 8, 0
    $ok.IsDefault = $true
    $ok.Tag = $win
    $ok.Add_Click({
        param($s, $e)
        try {
            $hostWin = $s.Tag
            $state = $hostWin.Tag
            $sl = $null
            foreach ($child in @($hostWin.Content.Children)) {
                if ($child -is [System.Windows.Controls.Slider]) { $sl = $child; break }
            }
            if ($null -ne $sl) {
                $state.Result.ok = $true
                $state.Result.value = $sl.Value / 100.0
            }
            $hostWin.DialogResult = $true
            $hostWin.Close()
        }
        catch {
            Write-FenceLog "Opacity OK: $($_.Exception.Message)"
        }
    })

    $cancel = New-Object System.Windows.Controls.Button
    $cancel.Content = 'Cancel'
    $cancel.Width = 80
    $cancel.IsCancel = $true
    $cancel.Tag = $win
    $cancel.Add_Click({
        param($s, $e)
        try {
            $hostWin = $s.Tag
            $state = $hostWin.Tag
            Set-FenceOpacity -FenceId $state.FenceId -Opacity $state.Original
            $hostWin.Close()
        }
        catch {
            Write-FenceLog "Opacity Cancel: $($_.Exception.Message)"
        }
    })

    [void]$sp.Children.Add($ok)
    [void]$sp.Children.Add($cancel)
    [void]$grid.Children.Add($lbl)
    [void]$grid.Children.Add($valueLbl)
    [void]$grid.Children.Add($slider)
    [void]$grid.Children.Add($sp)
    $win.Content = $grid

    try {
        $null = $win.ShowDialog()
    }
    catch {
        Write-FenceLog "Opacity dialog: $($_.Exception.Message)"
        Set-FenceOpacity -FenceId $FenceId -Opacity $original
    }
}

function Get-FenceResizeEdge {
    param(
        [double]$X,
        [double]$Y,
        [double]$Width,
        [double]$Height,
        [double]$Edge = 8
    )
    $left = $X -le $Edge
    $right = $X -ge ($Width - $Edge)
    $top = $Y -le $Edge
    $bottom = $Y -ge ($Height - $Edge)
    if ($top -and $left) { return 'TopLeft' }
    if ($top -and $right) { return 'TopRight' }
    if ($bottom -and $left) { return 'BottomLeft' }
    if ($bottom -and $right) { return 'BottomRight' }
    if ($left) { return 'Left' }
    if ($right) { return 'Right' }
    if ($top) { return 'Top' }
    if ($bottom) { return 'Bottom' }
    return $null
}

function Get-CursorForResizeEdge {
    param([string]$Edge)
    switch ($Edge) {
        'Left'        { return [System.Windows.Input.Cursors]::SizeWE }
        'Right'       { return [System.Windows.Input.Cursors]::SizeWE }
        'Top'         { return [System.Windows.Input.Cursors]::SizeNS }
        'Bottom'      { return [System.Windows.Input.Cursors]::SizeNS }
        'TopLeft'     { return [System.Windows.Input.Cursors]::SizeNWSE }
        'BottomRight' { return [System.Windows.Input.Cursors]::SizeNWSE }
        'TopRight'    { return [System.Windows.Input.Cursors]::SizeNESW }
        'BottomLeft'  { return [System.Windows.Input.Cursors]::SizeNESW }
        default       { return [System.Windows.Input.Cursors]::Arrow }
    }
}

function Initialize-FenceResize {
    param(
        [System.Windows.Window]$Window,
        [string]$FenceId
    )
    # Transparent borderless windows need manual edge-drag resize
    $Window.ResizeMode = 'NoResize'
    $state = @{
        Active    = $false
        Edge      = $null
        StartMX   = 0.0
        StartMY   = 0.0
        StartLeft = 0.0
        StartTop  = 0.0
        StartW    = 0.0
        StartH    = 0.0
    }
    $Window.Tag = $FenceId  # keep fence id on window (also used by drop handlers)

    $Window.Add_PreviewMouseMove({
        param($s, $e)
        try {
            $w = $s
            if ($state.Active) {
                $cur = [System.Windows.Forms.Control]::MousePosition
                # Convert screen pixels -> WPF DIPs (correct on scaled displays)
                $dx = [double]$cur.X - $state.StartMX
                $dy = [double]$cur.Y - $state.StartMY
                try {
                    $src = [System.Windows.PresentationSource]::FromVisual($w)
                    if ($null -ne $src) {
                        $m = $src.CompositionTarget.TransformFromDevice
                        $a = $m.Transform((New-Object System.Windows.Point $state.StartMX, $state.StartMY))
                        $b = $m.Transform((New-Object System.Windows.Point $cur.X, $cur.Y))
                        $dx = $b.X - $a.X
                        $dy = $b.Y - $a.Y
                    }
                }
                catch { }
                $minW = [Math]::Max(140.0, $w.MinWidth)
                $minH = [Math]::Max(48.0, $w.MinHeight)

                $left = $state.StartLeft
                $top = $state.StartTop
                $width = $state.StartW
                $height = $state.StartH
                $edge = $state.Edge

                switch ($edge) {
                    'Right' {
                        $width = [Math]::Max($minW, $state.StartW + $dx)
                    }
                    'Bottom' {
                        $height = [Math]::Max($minH, $state.StartH + $dy)
                    }
                    'Left' {
                        $width = [Math]::Max($minW, $state.StartW - $dx)
                        if ($width -gt $minW -or $dx -lt ($state.StartW - $minW)) {
                            $left = $state.StartLeft + ($state.StartW - $width)
                        }
                    }
                    'Top' {
                        $height = [Math]::Max($minH, $state.StartH - $dy)
                        if ($height -gt $minH -or $dy -lt ($state.StartH - $minH)) {
                            $top = $state.StartTop + ($state.StartH - $height)
                        }
                    }
                    'BottomRight' {
                        $width = [Math]::Max($minW, $state.StartW + $dx)
                        $height = [Math]::Max($minH, $state.StartH + $dy)
                    }
                    'BottomLeft' {
                        $width = [Math]::Max($minW, $state.StartW - $dx)
                        $height = [Math]::Max($minH, $state.StartH + $dy)
                        $left = $state.StartLeft + ($state.StartW - $width)
                    }
                    'TopRight' {
                        $width = [Math]::Max($minW, $state.StartW + $dx)
                        $height = [Math]::Max($minH, $state.StartH - $dy)
                        $top = $state.StartTop + ($state.StartH - $height)
                    }
                    'TopLeft' {
                        $width = [Math]::Max($minW, $state.StartW - $dx)
                        $height = [Math]::Max($minH, $state.StartH - $dy)
                        $left = $state.StartLeft + ($state.StartW - $width)
                        $top = $state.StartTop + ($state.StartH - $height)
                    }
                }

                # Snap free edges against nearby fences while resizing
                try {
                    $thr = 12.0
                    foreach ($oid in @($script:FenceWindows.Keys)) {
                        if ($oid -eq $w.Tag) { continue }
                        $ow = $script:FenceWindows[$oid].Window
                        if ($null -eq $ow -or $script:FenceWindows[$oid].ToggleHidden) { continue }
                        $oL = $ow.Left; $oT = $ow.Top
                        $oR = $ow.Left + $ow.ActualWidth; $oB = $ow.Top + $ow.ActualHeight
                        $myR = $left + $width; $myB = $top + $height
                        if ($edge -match 'Right') {
                            if ([Math]::Abs($myR - $oL) -le $thr) { $width = $oL - $left }
                            elseif ([Math]::Abs($myR - $oR) -le $thr) { $width = $oR - $left }
                        }
                        if ($edge -match 'Left') {
                            if ([Math]::Abs($left - $oR) -le $thr) { $nl = $oR; $width = $myR - $nl; $left = $nl }
                            elseif ([Math]::Abs($left - $oL) -le $thr) { $nl = $oL; $width = $myR - $nl; $left = $nl }
                        }
                        if ($edge -match 'Bottom') {
                            if ([Math]::Abs($myB - $oT) -le $thr) { $height = $oT - $top }
                            elseif ([Math]::Abs($myB - $oB) -le $thr) { $height = $oB - $top }
                        }
                        if ($edge -match 'Top') {
                            if ([Math]::Abs($top - $oB) -le $thr) { $nt = $oB; $height = $myB - $nt; $top = $nt }
                            elseif ([Math]::Abs($top - $oT) -le $thr) { $nt = $oT; $height = $myB - $nt; $top = $nt }
                        }
                    }
                }
                catch { }
                $width = [Math]::Max($minW, $width)
                $height = [Math]::Max($minH, $height)
                $w.Left = $left
                $w.Top = $top
                $w.Width = $width
                $w.Height = $height
                $e.Handled = $true
                return
            }

            # Hover: update cursor near edges (skip when rolled up or locked)
            if ($w.Height -le 40) {
                $w.Cursor = [System.Windows.Input.Cursors]::Arrow
                return
            }
            $fidHover = $w.Tag
            if ($fidHover -and (Test-FenceIsLocked -FenceId $fidHover)) {
                $w.Cursor = [System.Windows.Input.Cursors]::Arrow
                return
            }
            $p = $e.GetPosition($w)
            $edgeName = Get-FenceResizeEdge -X $p.X -Y $p.Y -Width $w.ActualWidth -Height $w.ActualHeight -Edge 8
            if ($edgeName) {
                $w.Cursor = Get-CursorForResizeEdge -Edge $edgeName
            }
            else {
                $w.Cursor = [System.Windows.Input.Cursors]::Arrow
            }
        }
        catch { }
    }.GetNewClosure())

    $Window.Add_PreviewMouseLeftButtonDown({
        param($s, $e)
        try {
            $w = $s
            if ($w.Height -le 40) { return }
            $fid = $w.Tag
            if ($fid -and (Test-FenceIsLocked -FenceId $fid)) { return }
            $p = $e.GetPosition($w)
            $edgeName = Get-FenceResizeEdge -X $p.X -Y $p.Y -Width $w.ActualWidth -Height $w.ActualHeight -Edge 8
            if (-not $edgeName) { return }

            $mp = [System.Windows.Forms.Control]::MousePosition
            $state.Active = $true
            $state.Edge = $edgeName
            $state.StartMX = [double]$mp.X
            $state.StartMY = [double]$mp.Y
            $state.StartLeft = $w.Left
            $state.StartTop = $w.Top
            $state.StartW = $w.Width
            $state.StartH = $w.Height
            [void]$w.CaptureMouse()
            $w.Cursor = Get-CursorForResizeEdge -Edge $edgeName
            $e.Handled = $true
        }
        catch { }
    }.GetNewClosure())

    $endResize = {
        param($s, $e)
        try {
            if (-not $state.Active) { return }
            $state.Active = $false
            $state.Edge = $null
            $s.ReleaseMouseCapture()
            $fid = $s.Tag
            if ($fid) { Sync-FenceGeometry -FenceId $fid }
        }
        catch { }
    }.GetNewClosure()

    $Window.Add_PreviewMouseLeftButtonUp($endResize)
    $Window.Add_LostMouseCapture({
        param($s, $e)
        try {
            if ($state.Active) {
                $state.Active = $false
                $state.Edge = $null
                $fid = $s.Tag
                if ($fid) { Sync-FenceGeometry -FenceId $fid }
            }
        }
        catch { }
    }.GetNewClosure())
}

function New-FenceWindow {
    param($FenceModel)

    $id = $FenceModel.id
    if ($script:FenceWindows.ContainsKey($id)) {
        try { $script:FenceWindows[$id].Window.Close() } catch { }
        $script:FenceWindows.Remove($id)
    }

    $win = New-Object System.Windows.Window
    # Blank title avoids shell/search chips; tool-window style keeps fences out of Alt+Tab
    $win.Title = ' '
    $win.WindowStyle = 'None'
    # True glass needs AllowsTransparency so panel alpha composites over the desktop.
    # Win+D is handled by placing windows above the desktop shell (not SetParent + layered).
    $win.AllowsTransparency = $true
    $win.Background = [System.Windows.Media.Brushes]::Transparent
    $win.Opacity = 1.0
    $win.ShowInTaskbar = $false
    $win.ShowActivated = $false
    $win.ResizeMode = 'NoResize'
    try { Register-WindowExcludeFromAltTab -Window $win } catch { }
    try { Register-FenceDesktopPin -Window $win } catch { }
    $win.Width = [Math]::Max(160, [double]$FenceModel.width)
    $win.Height = [Math]::Max(80, [double]$FenceModel.height)
    $win.Left = [double]$FenceModel.x
    $win.Top = [double]$FenceModel.y
    $win.MinWidth = 140
    $win.MinHeight = 80
    # Smart z-order: topmost on desktop/Win+D; bottom under games (see DesktopNative pin V11)
    try { $win.Topmost = (Get-FenceWantTopmost) } catch { $win.Topmost = $true }

    Ensure-FenceModelFields $FenceModel | Out-Null

    $opacity = 0.72
    if ($null -ne $FenceModel.opacity) {
        try { $opacity = [double]$FenceModel.opacity } catch { $opacity = 0.72 }
    }
    $opacity = [Math]::Max(0.0, [Math]::Min(1.0, $opacity))
    $bgHex = Get-FenceBgColorHex -FenceModel $FenceModel
    $panelAlpha = Get-FencePanelAlpha -Opacity $opacity

    # Root: glass (fades) behind content (icons/title stay full strength)
    $shell = New-Object System.Windows.Controls.Grid

    $glass = New-Object System.Windows.Controls.Border
    $glass.Background = Get-FenceBrush -Opacity $panelAlpha -HexColor $bgHex
    $glass.BorderBrush = Get-FenceBorderBrush
    $glass.BorderThickness = New-Object System.Windows.Thickness 1
    $glass.CornerRadius = New-Object System.Windows.CornerRadius 8
    $glass.IsHitTestVisible = $true

    $contentPad = New-Object System.Windows.Controls.Border
    $contentPad.Background = [System.Windows.Media.Brushes]::Transparent
    $contentPad.Padding = New-Object System.Windows.Thickness 8, 6, 8, 8
    $contentPad.IsHitTestVisible = $true

    $root = New-Object System.Windows.Controls.DockPanel
    $root.LastChildFill = $true
    $root.Background = [System.Windows.Media.Brushes]::Transparent

    # Title bar
    $titleBar = New-Object System.Windows.Controls.Border
    $titleBar.Background = [System.Windows.Media.Brushes]::Transparent
    $titleBar.Padding = New-Object System.Windows.Thickness 2, 0, 2, 4
    $titleBar.Cursor = [System.Windows.Input.Cursors]::SizeAll
    [System.Windows.Controls.DockPanel]::SetDock($titleBar, 'Top')

    $titleRow = New-Object System.Windows.Controls.DockPanel
    $titleText = New-Object System.Windows.Controls.TextBlock
    $titleText.Text = $FenceModel.title
    $titleText.FontSize = 12
    $titleText.FontWeight = 'SemiBold'
    $titleText.Foreground = Get-FenceTitleBrush
    $titleText.VerticalAlignment = 'Center'

    $btnRoll = New-Object System.Windows.Controls.Button
    $btnRoll.Content = [char]0x25B2  # up triangle
    $btnRoll.Width = 22
    $btnRoll.Height = 20
    $btnRoll.FontSize = 9
    $btnRoll.Padding = New-Object System.Windows.Thickness 0
    $btnRoll.Margin = New-Object System.Windows.Thickness 4, 0, 0, 0
    $btnRoll.Background = [System.Windows.Media.Brushes]::Transparent
    $btnRoll.Foreground = Get-FenceMutedBrush
    $btnRoll.BorderThickness = New-Object System.Windows.Thickness 0
    $btnRoll.Cursor = [System.Windows.Input.Cursors]::Hand
    $btnRoll.ToolTip = 'Roll up / expand'
    [System.Windows.Controls.DockPanel]::SetDock($btnRoll, 'Right')

    [void]$titleRow.Children.Add($btnRoll)
    [void]$titleRow.Children.Add($titleText)
    $titleBar.Child = $titleRow

    # Tab strip
    $tabStrip = New-Object System.Windows.Controls.StackPanel
    $tabStrip.Orientation = 'Horizontal'
    $tabStrip.Margin = New-Object System.Windows.Thickness 0, 0, 0, 4
    [System.Windows.Controls.DockPanel]::SetDock($tabStrip, 'Top')

    # Body
    $body = New-Object System.Windows.Controls.Grid
    $scroll = New-Object System.Windows.Controls.ScrollViewer
    $scroll.VerticalScrollBarVisibility = 'Auto'
    $scroll.HorizontalScrollBarVisibility = 'Disabled'
    $scroll.Focusable = $false
    $scroll.Background = [System.Windows.Media.Brushes]::Transparent

    $itemsPanel = New-Object System.Windows.Controls.WrapPanel
    $itemsPanel.Orientation = 'Horizontal'
    $scroll.Content = $itemsPanel

    $hint = New-Object System.Windows.Controls.TextBlock
    $hint.Text = "Drop files here`nRight-click for options"
    $hint.Foreground = Get-FenceMutedBrush
    $hint.FontSize = 11
    $hint.TextAlignment = 'Center'
    $hint.HorizontalAlignment = 'Center'
    $hint.VerticalAlignment = 'Center'
    $hint.Opacity = 0.85

    [void]$body.Children.Add($scroll)
    [void]$body.Children.Add($hint)

    [void]$root.Children.Add($titleBar)
    [void]$root.Children.Add($tabStrip)
    [void]$root.Children.Add($body)
    $contentPad.Child = $root

    [void]$shell.Children.Add($glass)
    [void]$shell.Children.Add($contentPad)
    $win.Content = $shell

    # Outer alias = glass (context menu / legacy callers)
    $outer = $glass

    $entry = @{
        Window               = $win
        Model                = $FenceModel
        TitleText            = $titleText
        TitleBar             = $titleBar
        TabStrip             = $tabStrip
        Body                 = $body
        ItemsPanel           = $itemsPanel
        HintText             = $hint
        ExpandedHeight       = [Math]::Max(80.0, [double]$FenceModel.height)
        ForceShowTabs        = $false
        Outer                = $outer
        Glass                = $glass
        ContentRoot          = $contentPad
        ReadyToSync          = $false
        ToggleHidden         = $false
        SuppressGeometrySync = $false
        RollButton           = $btnRoll
    }
    $script:FenceWindows[$id] = $entry

    Initialize-FenceResize -Window $win -FenceId $id
    $win.Tag = $id
    $titleBar.Tag = $id
    $btnRoll.Tag = $id

    # Drag move (grouped fences move together); blocked when locked
    $titleBar.Add_MouseLeftButtonDown({
        param($s, $e)
        try {
            $fid = $s.Tag
            if ($e.ClickCount -ge 2) {
                Toggle-FenceRollUp -FenceId $fid
                $e.Handled = $true
                return
            }
            if (Test-FenceIsLocked -FenceId $fid) {
                $e.Handled = $true
                return
            }
            $w = $s
            while ($null -ne $w -and -not ($w -is [System.Windows.Window])) {
                $w = [System.Windows.Media.VisualTreeHelper]::GetParent($w)
            }
            if ($null -ne $w) {
                Start-FenceGroupDrag -LeaderId $fid -LeaderWindow $w
                $w.DragMove()
                # Magnetic snap after drop (leader), then re-apply group offsets
                $beforeL = $w.Left
                $beforeT = $w.Top
                Invoke-FenceSnap -FenceId $fid -Window $w
                $dxSnap = $w.Left - $beforeL
                $dySnap = $w.Top - $beforeT
                if ($script:GroupDrag -and $script:GroupDrag.Members -and ($dxSnap -ne 0 -or $dySnap -ne 0)) {
                    foreach ($mid in @($script:GroupDrag.Members.Keys)) {
                        if ($mid -eq $fid) { continue }
                        if (Test-FenceIsLocked -FenceId $mid) { continue }
                        if (-not $script:FenceWindows.ContainsKey($mid)) { continue }
                        $mw = $script:FenceWindows[$mid].Window
                        if ($null -eq $mw) { continue }
                        $mw.Left += $dxSnap
                        $mw.Top += $dySnap
                    }
                }
                Stop-FenceGroupDrag -LeaderId $fid
                Sync-FenceGeometry -FenceId $fid
            }
        }
        catch {
            Write-FenceLog "Title drag error: $($_.Exception.Message)"
            try { $script:GroupDrag = $null } catch { }
        }
    })

    $btnRoll.Add_Click({
        param($s, $e)
        try { Toggle-FenceRollUp -FenceId $s.Tag } catch { }
    })

    # Resize / move persistence (skip until after first show)
    $win.Add_LocationChanged({
        param($s, $e)
        try {
            $fid = $s.Tag
            if ([string]::IsNullOrEmpty($fid)) { return }
            if (-not $script:FenceWindows.ContainsKey($fid)) { return }
            $ent = $script:FenceWindows[$fid]
            if (-not $ent.ReadyToSync) { return }
            if ($ent.SuppressGeometrySync) { return }
            # While group-dragging, move siblings with the leader
            if ($script:GroupDrag -and $script:GroupDrag.LeaderId -eq $fid -and -not $script:GroupDrag.Applying) {
                Update-FenceGroupDrag -LeaderId $fid -LeaderWindow $s
            }
            if ($script:GroupDrag -and $script:GroupDrag.Applying) { return }
            Sync-FenceGeometry -FenceId $fid
        }
        catch { }
    })
    $win.Add_SizeChanged({
        param($s, $e)
        try {
            $fid = $s.Tag
            if ([string]::IsNullOrEmpty($fid)) { return }
            if (-not $script:FenceWindows.ContainsKey($fid)) { return }
            $ent = $script:FenceWindows[$fid]
            if (-not $ent.ReadyToSync) { return }
            if ($ent.SuppressGeometrySync) { return }
            $m = Find-FenceModel -Id $fid
            if ($null -ne $m -and -not $m.rolledUp -and $null -ne $ent.Window) {
                $h = [double]$ent.Window.Height
                if ($h -gt 40) {
                    $ent.ExpandedHeight = $h
                }
            }
            Sync-FenceGeometry -FenceId $fid
        }
        catch {
            Write-FenceLog "SizeChanged error: $($_.Exception.Message)"
        }
    })

    # Drag-drop
    $win.AllowDrop = $true
    $win.Add_DragOver({
        param($s, $e)
        if ($e.Data.GetDataPresent([System.Windows.DataFormats]::FileDrop)) {
            $e.Effects = [System.Windows.DragDropEffects]::Copy
        }
        else {
            $e.Effects = [System.Windows.DragDropEffects]::None
        }
        $e.Handled = $true
    })
    $win.Add_Drop({
        param($s, $e)
        try {
            $fid = $s.Tag
            if ($e.Data.GetDataPresent([System.Windows.DataFormats]::FileDrop)) {
                $files = @($e.Data.GetData([System.Windows.DataFormats]::FileDrop))
                Add-ItemToFence -FenceId $fid -Paths $files
            }
            $e.Handled = $true
        }
        catch {
            Write-FenceLog "Drop error: $($_.Exception.Message)"
        }
    })

    # Context menu on fence (ASCII "..." only - PS 5.1 misreads Unicode ellipsis)
    $cm = New-Object System.Windows.Controls.ContextMenu
    $cm.Tag = $id

    $miRename = New-Object System.Windows.Controls.MenuItem
    $miRename.Header = 'Rename fence...'
    $miRename.Tag = $id
    $miRename.Add_Click({
        param($s, $e)
        try {
            $fid = $s.Tag
            $m = Find-FenceModel -Id $fid
            if ($null -eq $m) { return }
            $name = Show-FenceInputDialog -Title 'Rename fence' -Prompt 'Fence name:' -DefaultText $m.title
            if ($null -eq $name -or $name.Trim() -eq '') { return }
            $m.title = $name.Trim()
            Update-FenceModelInLayout -FenceModel $m
            Update-FenceContent -FenceId $fid
        }
        catch { Write-FenceLog "Rename menu: $($_.Exception.Message)" }
    })

    $miRoll = New-Object System.Windows.Controls.MenuItem
    $miRoll.Header = 'Roll up / expand'
    $miRoll.Tag = $id
    $miRoll.Add_Click({
        param($s, $e)
        try { Toggle-FenceRollUp -FenceId $s.Tag } catch { }
    })

    $miAddTab = New-Object System.Windows.Controls.MenuItem
    $miAddTab.Header = 'Add tab...'
    $miAddTab.Tag = $id
    $miAddTab.Add_Click({
        param($s, $e)
        try {
            $fid = $s.Tag
            $m = Find-FenceModel -Id $fid
            if ($null -eq $m) { return }
            if ($m.mode -eq 'portal') {
                [System.Windows.MessageBox]::Show('Tabs are not available on portal fences. Convert to items first.', 'FenceDesk') | Out-Null
                return
            }
            $name = Show-FenceInputDialog -Title 'Add tab' -Prompt 'Tab name:' -DefaultText 'New tab'
            if ($null -eq $name -or $name.Trim() -eq '') { return }
            $tid = [guid]::NewGuid().ToString()
            $m.tabs = @($m.tabs) + @([ordered]@{ id = $tid; title = $name.Trim(); items = @() })
            $m.activeTabId = $tid
            if ($script:FenceWindows.ContainsKey($fid)) {
                $script:FenceWindows[$fid].ForceShowTabs = $true
            }
            Update-FenceModelInLayout -FenceModel $m
            Update-FenceContent -FenceId $fid
        }
        catch { Write-FenceLog "Add tab menu: $($_.Exception.Message)" }
    })

    $miRecycle = New-Object System.Windows.Controls.MenuItem
    $miRecycle.Header = 'Add Recycle Bin'
    $miRecycle.ToolTip = 'Add the Recycle Bin icon (cannot be dragged from the desktop)'
    $miRecycle.Tag = $id
    $miRecycle.Add_Click({
        param($s, $e)
        try {
            $fid = $s.Tag
            $m = Find-FenceModel -Id $fid
            if ($null -eq $m) { return }
            if ($m.mode -eq 'portal') {
                [System.Windows.MessageBox]::Show('Switch to items mode first to add Recycle Bin.', 'FenceDesk') | Out-Null
                return
            }
            $rb = Get-RecycleBinPath
            Add-ItemToFence -FenceId $fid -Paths @($rb)
        }
        catch { Write-FenceLog "Recycle Bin menu: $($_.Exception.Message)" }
    })

    $miNewFence = New-Object System.Windows.Controls.MenuItem
    $miNewFence.Header = 'New fence'
    $miNewFence.Tag = $id
    $miNewFence.Add_Click({
        param($s, $e)
        try { New-FenceFromTray } catch { Write-FenceLog "New fence menu: $($_.Exception.Message)" }
    })

    $miPortal = New-Object System.Windows.Controls.MenuItem
    $miPortal.Header = 'Convert to portal (folder view)...'
    $miPortal.ToolTip = 'Show live contents of a real folder. Dropping files copies them into that folder.'
    $miPortal.Tag = $id
    $miPortal.Add_Click({
        param($s, $e)
        try {
            $fid = $s.Tag
            $m = Find-FenceModel -Id $fid
            if ($null -eq $m) { return }
            $folder = Show-FolderPicker -Description 'Select folder to show as a portal fence'
            if (-not $folder) { return }
            $m.mode = 'portal'
            $m.portalPath = $folder
            $m.title = [System.IO.Path]::GetFileName($folder)
            if ([string]::IsNullOrWhiteSpace($m.title)) { $m.title = $folder }
            Update-FenceModelInLayout -FenceModel $m
            Register-PortalWatcher -FenceId $fid -FolderPath $folder -OnChanged {
                param($watchFid)
                $w = $null
                if ($script:FenceWindows.ContainsKey($watchFid)) {
                    $w = $script:FenceWindows[$watchFid].Window
                }
                if ($null -ne $w) {
                    $w.Dispatcher.Invoke({ Update-FenceContent -FenceId $watchFid })
                }
            }
            Update-FenceContent -FenceId $fid
            try { Sync-DesktopIconVisibility } catch { }
        }
        catch { Write-FenceLog "Portal menu: $($_.Exception.Message)" }
    })

    $miItems = New-Object System.Windows.Controls.MenuItem
    $miItems.Header = 'Convert to items (manual list)'
    $miItems.ToolTip = 'Stop mirroring a folder. Keep a manual list of shortcuts/files you drop in.'
    $miItems.Tag = $id
    $miItems.Add_Click({
        param($s, $e)
        try {
            $fid = $s.Tag
            $m = Find-FenceModel -Id $fid
            if ($null -eq $m) { return }
            Unregister-PortalWatcher -FenceId $fid
            $m.mode = 'items'
            $m.portalPath = $null
            Update-FenceModelInLayout -FenceModel $m
            Update-FenceContent -FenceId $fid
            try { Sync-DesktopIconVisibility } catch { }
        }
        catch { Write-FenceLog "Items menu: $($_.Exception.Message)" }
    })

    $miOpenPortal = New-Object System.Windows.Controls.MenuItem
    $miOpenPortal.Header = 'Open portal folder'
    $miOpenPortal.Tag = $id
    $miOpenPortal.Add_Click({
        param($s, $e)
        try {
            $m = Find-FenceModel -Id $s.Tag
            if ($null -eq $m -or -not $m.portalPath) { return }
            Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$($m.portalPath)`""
        }
        catch { }
    })

    $miAttach = New-Object System.Windows.Controls.MenuItem
    $miAttach.Header = 'Attach to fence (move together)...'
    $miAttach.ToolTip = 'Link this fence to another so dragging either moves the whole group.'
    $miAttach.Tag = $id
    $miAttach.Add_Click({
        param($s, $e)
        try {
            $fid = $s.Tag
            $target = Show-FencePickerDialog -Title 'Attach fences' -Prompt 'Attach to which fence or group? They will move together.' -ExcludeId $fid
            if (-not $target) { return }
            Join-FenceGroup -FenceId $fid -TargetFenceId $target
            [System.Windows.MessageBox]::Show(
                'Attached. Groups appear as one entry (e.g. Files & Downloads). Drag any title bar to move the group. Use Detach from group to unlink this fence only.',
                'FenceDesk'
            ) | Out-Null
        }
        catch { Write-FenceLog "Attach menu: $($_.Exception.Message)" }
    })

    $miDetach = New-Object System.Windows.Controls.MenuItem
    $miDetach.Header = 'Detach from group'
    $miDetach.Tag = $id
    $miDetach.Add_Click({
        param($s, $e)
        try {
            Leave-FenceGroup -FenceId $s.Tag
        }
        catch { Write-FenceLog "Detach menu: $($_.Exception.Message)" }
    })

    $miLock = New-Object System.Windows.Controls.MenuItem
    $miLock.Header = 'Lock position'
    $miLock.ToolTip = 'Prevent move/resize. Linked fences lock/unlock together.'
    $miLock.Tag = $id
    $miLock.Add_Click({
        param($s, $e)
        try {
            $fid = $s.Tag
            $locked = Test-FenceIsLocked -FenceId $fid
            Set-FenceLocked -FenceId $fid -Locked (-not $locked)
        }
        catch { Write-FenceLog "Lock menu: $($_.Exception.Message)" }
    })

    $miOpacity = New-Object System.Windows.Controls.MenuItem
    $miOpacity.Header = 'Opacity...'
    $miOpacity.Tag = $id
    $miOpacity.Add_Click({
        param($s, $e)
        try {
            $fid = $s.Tag
            $m = Find-FenceModel -Id $fid
            $cur = 0.72
            if ($null -ne $m -and $null -ne $m.opacity) { $cur = [double]$m.opacity }
            Show-OpacityDialog -FenceId $fid -Current $cur
        }
        catch {
            Write-FenceLog "Opacity menu: $($_.Exception.Message)"
        }
    })

    $miColor = New-Object System.Windows.Controls.MenuItem
    $miColor.Header = 'Background color'
    $miColor.Tag = $id

    $miColorThis = New-Object System.Windows.Controls.MenuItem
    $miColorThis.Header = 'This fence...'
    $miColorThis.ToolTip = 'Pick a background color for this fence only'
    $miColorThis.Tag = $id
    $miColorThis.Add_Click({
        param($s, $e)
        try { Show-FenceColorDialog -FenceId $s.Tag } catch { Write-FenceLog "Color menu: $($_.Exception.Message)" }
    })

    $miColorAll = New-Object System.Windows.Controls.MenuItem
    $miColorAll.Header = 'All fences...'
    $miColorAll.ToolTip = 'Pick one background color and apply it to every fence'
    $miColorAll.Tag = $id
    $miColorAll.Add_Click({
        param($s, $e)
        try { Show-FenceColorDialog -FenceId $s.Tag -ApplyToAll } catch { Write-FenceLog "Color all menu: $($_.Exception.Message)" }
    })

    $miColorReset = New-Object System.Windows.Controls.MenuItem
    $miColorReset.Header = 'Reset this fence'
    $miColorReset.ToolTip = 'Restore the default fence background color'
    $miColorReset.Tag = $id
    $miColorReset.Add_Click({
        param($s, $e)
        try { Reset-FenceBackgroundColor -FenceId $s.Tag } catch { Write-FenceLog "Color reset: $($_.Exception.Message)" }
    })

    $miColorResetAll = New-Object System.Windows.Controls.MenuItem
    $miColorResetAll.Header = 'Reset all fences'
    $miColorResetAll.ToolTip = 'Restore the default background color on every fence'
    $miColorResetAll.Tag = $id
    $miColorResetAll.Add_Click({
        param($s, $e)
        try { Reset-AllFencesBackgroundColor } catch { Write-FenceLog "Color reset all: $($_.Exception.Message)" }
    })

    [void]$miColor.Items.Add($miColorThis)
    [void]$miColor.Items.Add($miColorAll)
    [void]$miColor.Items.Add((New-Object System.Windows.Controls.Separator))
    [void]$miColor.Items.Add($miColorReset)
    [void]$miColor.Items.Add($miColorResetAll)

    $miDelete = New-Object System.Windows.Controls.MenuItem
    $miDelete.Header = 'Delete fence'
    $miDelete.Tag = $id
    $miDelete.Add_Click({
        param($s, $e)
        try {
            $fid = $s.Tag
            $r = [System.Windows.MessageBox]::Show(
                'Delete this fence? Items are not deleted from disk.',
                'FenceDesk',
                [System.Windows.MessageBoxButton]::YesNo,
                [System.Windows.MessageBoxImage]::Question
            )
            if ($r -eq [System.Windows.MessageBoxResult]::Yes) {
                Leave-FenceGroup -FenceId $fid
                Remove-FenceWindow -FenceId $fid
            }
        }
        catch { Write-FenceLog "Delete menu: $($_.Exception.Message)" }
    })

    # Refresh lock menu label when menu opens
    $cm.Add_Opened({
        param($s, $e)
        try {
            $fid = $s.Tag
            $locked = Test-FenceIsLocked -FenceId $fid
            foreach ($item in $s.Items) {
                if ($item -is [System.Windows.Controls.MenuItem] -and ($item.Header -like 'Lock position*' -or $item.Header -like 'Unlock position*')) {
                    $item.Header = if ($locked) { 'Unlock position' } else { 'Lock position' }
                }
            }
        }
        catch { }
    })

    [void]$cm.Items.Add($miNewFence)
    [void]$cm.Items.Add((New-Object System.Windows.Controls.Separator))
    [void]$cm.Items.Add($miRename)
    [void]$cm.Items.Add($miRoll)
    [void]$cm.Items.Add($miLock)
    [void]$cm.Items.Add((New-Object System.Windows.Controls.Separator))
    [void]$cm.Items.Add($miAddTab)
    [void]$cm.Items.Add($miRecycle)
    [void]$cm.Items.Add($miPortal)
    [void]$cm.Items.Add($miItems)
    [void]$cm.Items.Add($miOpenPortal)
    [void]$cm.Items.Add((New-Object System.Windows.Controls.Separator))
    [void]$cm.Items.Add($miAttach)
    [void]$cm.Items.Add($miDetach)
    [void]$cm.Items.Add((New-Object System.Windows.Controls.Separator))
    [void]$cm.Items.Add($miOpacity)
    [void]$cm.Items.Add($miColor)
    [void]$cm.Items.Add((New-Object System.Windows.Controls.Separator))
    [void]$cm.Items.Add($miDelete)
    $outer.ContextMenu = $cm
    $titleBar.ContextMenu = $cm
    try { if ($contentPad) { $contentPad.ContextMenu = $cm } } catch { }
    try { if ($shell) { $shell.ContextMenu = $cm } } catch { }

    $win.Add_Closed({
        # if closed externally, keep layout unless deleted
    }.GetNewClosure())

    $win.Show()
    # Glass alpha only — never Window.Opacity (that blacked out icons too)
    try {
        $win.Opacity = 1.0
        Update-FenceGlassAppearance -FenceId $id -FenceModel $FenceModel
    }
    catch { }
    # Final pass so fences never appear in Alt+Tab / Task View
    try { Exclude-WindowFromAltTab -Window $win } catch { }
    # Smart pin: topmost on desktop (Win+D safe), notopmost under focused apps/games
    try {
        $ok = Pin-FenceWindowToDesktop -Window $win -Force
        $dbg = if ('FenceDeskDesktopPinV11' -as [type]) { [FenceDeskDesktopPinV11]::LastDebug } else { 'type missing' }
        Write-FenceLog ("Fence desktop pin ({0}): ok={1} {2}" -f $FenceModel.title, $ok, $dbg)
    }
    catch {
        Write-FenceLog "Fence desktop place error: $($_.Exception.Message)"
    }
    $entry.ReadyToSync = $true
    $script:FenceWindows[$id] = $entry

    try {
        Update-FenceContent -FenceId $id
    }
    catch {
        Write-FenceLog "Update-FenceContent failed for $id : $($_.Exception.Message)"
    }

    if ($FenceModel.mode -eq 'portal' -and $FenceModel.portalPath) {
        Register-PortalWatcher -FenceId $id -FolderPath $FenceModel.portalPath -OnChanged {
            param($fid)
            try {
                $w = $script:FenceWindows[$fid].Window
                if ($null -ne $w) {
                    $w.Dispatcher.BeginInvoke([action]{ Update-FenceContent -FenceId $fid }) | Out-Null
                }
            }
            catch {
                Update-FenceContent -FenceId $fid
            }
        }
    }

    if ($FenceModel.rolledUp) {
        Apply-FenceRollUp -FenceId $id
    }

    try { Update-FenceLockChrome -FenceId $id } catch { }

    return $win
}

function Toggle-FenceRollUp {
    param([string]$FenceId)
    $m = Find-FenceModel -Id $FenceId
    if ($null -eq $m) { return }
    if (-not $script:FenceWindows.ContainsKey($FenceId)) { return }
    $entry = $script:FenceWindows[$FenceId]

    if (-not $m.rolledUp) {
        # Save full size before collapse (use ActualHeight when available)
        if ($entry -and $entry.Window) {
            $h = 0.0
            try {
                if ($entry.Window.ActualHeight -gt 40) { $h = [double]$entry.Window.ActualHeight }
                else { $h = [double]$entry.Window.Height }
            }
            catch { $h = [double]$entry.Window.Height }
            if ($h -gt 40) {
                $entry.ExpandedHeight = $h
                $m.height = [int][Math]::Round($h)
            }
        }
    }
    $m.rolledUp = -not [bool]$m.rolledUp
    Update-FenceModelInLayout -FenceModel $m
    Apply-FenceRollUp -FenceId $FenceId
    # Persist restored height after expand
    if (-not $m.rolledUp -and $entry -and $entry.ExpandedHeight -gt 40) {
        $m.height = [int][Math]::Round([double]$entry.ExpandedHeight)
        Update-FenceModelInLayout -FenceModel $m
    }
}

function Sync-FenceGeometry {
    param([string]$FenceId)
    $m = Find-FenceModel -Id $FenceId
    if ($null -eq $m) { return }
    if (-not $script:FenceWindows.ContainsKey($FenceId)) { return }
    $entry = $script:FenceWindows[$FenceId]
    if ($entry.ToggleHidden) { return }  # never persist off-screen park coords
    if ($entry.SuppressGeometrySync) { return }
    $win = $entry.Window
    if ($null -eq $win) { return }
    if ($win.Left -lt -5000 -or $win.Top -lt -5000) { return }
    $m.x = [int][Math]::Round($win.Left)
    $m.y = [int][Math]::Round($win.Top)
    if (-not $m.rolledUp) {
        $m.width = [int][Math]::Round($win.Width)
        $h = [int][Math]::Round($win.Height)
        if ($h -gt 40) {
            $m.height = $h
            $entry.ExpandedHeight = [double]$h
        }
    }
    else {
        $m.width = [int][Math]::Round($win.Width)
        # Keep model.height as expanded size while rolled up
        if ($entry.ExpandedHeight -and $entry.ExpandedHeight -gt 40) {
            $m.height = [int][Math]::Round([double]$entry.ExpandedHeight)
        }
    }
    Update-FenceModelInLayout -FenceModel $m
}

function Remove-FenceWindow {
    param([string]$FenceId)
    Unregister-PortalWatcher -FenceId $FenceId
    if ($script:FenceWindows.ContainsKey($FenceId)) {
        try {
            $script:FenceWindows[$FenceId].Window.Close()
        }
        catch { }
        $script:FenceWindows.Remove($FenceId)
    }
    Remove-FenceModelFromLayout -Id $FenceId
    try { Sync-DesktopIconVisibility } catch { }
}

function Get-FenceHwnd {
    param([System.Windows.Window]$Window)
    try {
        $helper = New-Object System.Windows.Interop.WindowInteropHelper($Window)
        if ($helper.Handle -eq [IntPtr]::Zero) {
            $null = $helper.EnsureHandle()
        }
        return $helper.Handle
    }
    catch { return [IntPtr]::Zero }
}

function Show-AllFences {
    # Restore from off-screen park — never ShowWindow/SW_HIDE (DWM full-desktop flash)
    $i = 0
    foreach ($id in @($script:FenceWindows.Keys)) {
        try {
            $entry = $script:FenceWindows[$id]
            $w = $entry.Window
            if ($null -eq $w) { continue }
            $entry.ToggleHidden = $false
            $m = Find-FenceModel -Id $id
            $x = if ($null -ne $entry.ParkedLeft) { $entry.ParkedLeft } elseif ($m) { [double]$m.x } else { $w.Left }
            $y = if ($null -ne $entry.ParkedTop) { $entry.ParkedTop } elseif ($m) { [double]$m.y } else { $w.Top }
            # Guard: if parked coords look real, use them; if model has valid on-screen pos prefer model when parked was offscreen
            if ($x -lt -5000 -and $m) { $x = [double]$m.x }
            if ($y -lt -5000 -and $m) { $y = [double]$m.y }
            $w.Left = $x
            $w.Top = $y
            $w.IsHitTestVisible = $true
            if ($w.WindowState -eq [System.Windows.WindowState]::Minimized) {
                $w.WindowState = [System.Windows.WindowState]::Normal
            }
            try { Pin-FenceWindowToDesktop -Window $w -Raise | Out-Null } catch { }
            $i++
        }
        catch { }
    }
    if ($script:Layout.settings) {
        $script:Layout.settings.showFences = $true
        Save-FenceLayout -Layout $script:Layout
    }
}

function Hide-AllFences {
    # Park off-screen instead of hide/opacity — avoids full-desktop DWM blank flash
    $i = 0
    foreach ($id in @($script:FenceWindows.Keys)) {
        try {
            $entry = $script:FenceWindows[$id]
            $w = $entry.Window
            if ($null -eq $w) { continue }
            if (-not $entry.ToggleHidden) {
                # Only snapshot if currently on-screen
                if ($w.Left -gt -5000) {
                    $entry.ParkedLeft = $w.Left
                    $entry.ParkedTop = $w.Top
                    # Keep model coords as the real position
                    $m = Find-FenceModel -Id $id
                    if ($null -ne $m) {
                        $m.x = [int][Math]::Round($w.Left)
                        $m.y = [int][Math]::Round($w.Top)
                    }
                }
            }
            $entry.ToggleHidden = $true
            $w.IsHitTestVisible = $false
            $w.Left = -20000 - ($i * 80)
            $w.Top = -20000
            $i++
        }
        catch { }
    }
    if ($script:Layout.settings) {
        $script:Layout.settings.showFences = $false
        Save-FenceLayout -Layout $script:Layout
    }
}

function Toggle-AllFences {
    $now = [Environment]::TickCount
    if ($script:LastFenceToggleTick -and (($now - $script:LastFenceToggleTick) -gt 0) -and (($now - $script:LastFenceToggleTick) -lt 400)) {
        Write-FenceLog 'Toggle-AllFences debounced'
        return
    }
    $script:LastFenceToggleTick = $now

    $anyShown = $false
    foreach ($id in @($script:FenceWindows.Keys)) {
        $entry = $script:FenceWindows[$id]
        if ($entry.ToggleHidden) { continue }
        $w = $entry.Window
        if ($null -ne $w -and $w.Left -gt -5000) {
            $anyShown = $true
            break
        }
    }
    Write-FenceLog ("Toggle-AllFences -> {0}" -f $(if ($anyShown) { 'Hide' } else { 'Show' }))
    if ($anyShown) { Hide-AllFences } else { Show-AllFences }
}

function Update-FenceLockChrome {
    param([string]$FenceId)
    if (-not $script:FenceWindows.ContainsKey($FenceId)) { return }
    $entry = $script:FenceWindows[$FenceId]
    $m = Find-FenceModel -Id $FenceId
    if ($null -eq $m -or $null -eq $entry.TitleText) { return }
    Ensure-FenceModelFields $m | Out-Null
    $locked = $false
    try { $locked = [bool]$m.locked } catch { }
    $base = $m.title
    if ($locked) {
        $entry.TitleText.Text = '[L] ' + $base
        $entry.TitleBar.Cursor = [System.Windows.Input.Cursors]::Arrow
        $entry.TitleBar.ToolTip = 'Locked - right-click Unlock position'
    }
    else {
        $entry.TitleText.Text = $base
        $entry.TitleBar.Cursor = [System.Windows.Input.Cursors]::SizeAll
        $entry.TitleBar.ToolTip = $null
    }
}

function Close-AllFenceWindows {
    Unregister-AllPortalWatchers
    foreach ($id in @($script:FenceWindows.Keys)) {
        try { $script:FenceWindows[$id].Window.Close() } catch { }
    }
    $script:FenceWindows = @{}
}

function New-FenceFromTray {
    $wa = [System.Windows.SystemParameters]::WorkArea
    $x = [int]($wa.Left + 80 + (Get-Random -Maximum 120))
    $y = [int]($wa.Top + 80 + (Get-Random -Maximum 120))
    $model = New-FenceModel -Title 'New Fence' -X $x -Y $y
    Add-FenceModelToLayout -FenceModel $model
    New-FenceWindow -FenceModel $model | Out-Null
}

function Import-DesktopShortcutsToFence {
    param([string]$FenceId, [int]$MaxItems = 12)
    $model = Find-FenceModel -Id $FenceId
    if ($null -eq $model -or $model.mode -eq 'portal') { return }
    $tab = @($model.tabs) | Where-Object { $_.id -eq $model.activeTabId } | Select-Object -First 1
    if ($null -eq $tab) { return }
    if (@($tab.items).Count -gt 0) { return }

    $desktop = [Environment]::GetFolderPath('Desktop')
    $publicDesktop = [Environment]::GetFolderPath('CommonDesktopDirectory')
    $paths = @()
    foreach ($dir in @($desktop, $publicDesktop)) {
        if (-not (Test-Path -LiteralPath $dir)) { continue }
        Get-ChildItem -LiteralPath $dir -Filter '*.lnk' -ErrorAction SilentlyContinue |
            Select-Object -First $MaxItems |
            ForEach-Object { $paths += $_.FullName }
    }
    if ($paths.Count -eq 0) { return }
    Add-ItemToFence -FenceId $FenceId -Paths $paths
}

function Initialize-AllFences {
    # First-run: seed Apps fence from desktop shortcuts when empty
    try {
        $apps = @($script:Layout.fences) | Where-Object { $_.title -eq 'Apps' -and $_.mode -eq 'items' } | Select-Object -First 1
        if ($null -ne $apps) {
            $tab = @($apps.tabs) | Select-Object -First 1
            if ($null -ne $tab -and @($tab.items).Count -eq 0) {
                # Will import after windows exist so UI refreshes; do path inject now
                $desktop = [Environment]::GetFolderPath('Desktop')
                $publicDesktop = [Environment]::GetFolderPath('CommonDesktopDirectory')
                $items = @()
                foreach ($dir in @($desktop, $publicDesktop)) {
                    if (-not (Test-Path -LiteralPath $dir)) { continue }
                    Get-ChildItem -LiteralPath $dir -Filter '*.lnk' -ErrorAction SilentlyContinue | ForEach-Object {
                        $items += [ordered]@{
                            path  = $_.FullName
                            label = [System.IO.Path]::GetFileNameWithoutExtension($_.Name)
                        }
                    }
                }
                if ($items.Count -gt 0) {
                    $tab.items = @($items | Select-Object -First 16)
                    Update-FenceModelInLayout -FenceModel $apps
                    Write-FenceLog "Seeded Apps fence with $(@($tab.items).Count) desktop shortcut(s)"
                }
            }
        }
    }
    catch {
        Write-FenceLog "Seed shortcuts failed: $($_.Exception.Message)"
    }

    foreach ($f in @($script:Layout.fences)) {
        try {
            New-FenceWindow -FenceModel $f | Out-Null
        }
        catch {
            Write-FenceLog "Failed to create fence $($f.title): $($_.Exception.Message)"
        }
    }
    if ($script:Layout.settings -and $script:Layout.settings.showFences -eq $false) {
        Hide-AllFences
    }
    try { Sync-DesktopIconVisibility } catch { }
    try { Start-FenceShowDesktopGuard } catch { }
}
