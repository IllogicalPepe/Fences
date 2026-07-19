# Control window for FenceDesk (hidden from Alt+Tab / taskbar; open via tray)

function New-TaskbarHostButton {
    param(
        [string]$Text,
        [scriptblock]$OnClick,
        [string]$BgR = '32',
        [string]$BgG = '48',
        [string]$BgB = '72',
        [string]$FgR = '220',
        [string]$FgG = '230',
        [string]$FgB = '245',
        [string]$BdR = '60',
        [string]$BdG = '90',
        [string]$BdB = '130'
    )
    $b = New-Object System.Windows.Controls.Button
    $b.Content = $Text
    $b.Margin = New-Object System.Windows.Thickness 0, 0, 0, 8
    $b.Padding = New-Object System.Windows.Thickness 10, 8, 10, 8
    $b.HorizontalContentAlignment = 'Left'
    $b.Background = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb([byte]$BgR, [byte]$BgG, [byte]$BgB))
    $b.Foreground = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb([byte]$FgR, [byte]$FgG, [byte]$FgB))
    $b.BorderBrush = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb([byte]$BdR, [byte]$BdG, [byte]$BdB))
    $b.BorderThickness = New-Object System.Windows.Thickness 1
    $b.Cursor = [System.Windows.Input.Cursors]::Hand
    $b.Add_Click({ & $OnClick }.GetNewClosure())
    return $b
}

function Initialize-TaskbarHost {
    $script:TaskbarHostUserOpen = $false

    $win = New-Object System.Windows.Window
    # Empty title so Win+D / shell never surfaces a "FenceDesk" search/title chip
    $win.Title = ' '
    $win.Width = 360
    $win.Height = 440
    $win.MinWidth = 300
    $win.MinHeight = 340
    $win.WindowStartupLocation = 'CenterScreen'
    # Do not appear in Alt+Tab or the taskbar — tray owns app lifetime / controls
    $win.ShowInTaskbar = $false
    $win.ShowActivated = $false
    $win.Background = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(18, 28, 44))
    $win.ResizeMode = 'CanMinimize'
    try { Register-WindowExcludeFromAltTab -Window $win } catch { }

    try {
        $img = Get-FenceDeskImageSource
        if ($null -ne $img) { $win.Icon = $img }
    }
    catch { }

    $root = New-Object System.Windows.Controls.DockPanel
    $root.Margin = New-Object System.Windows.Thickness 16

    $header = New-Object System.Windows.Controls.StackPanel
    $header.Margin = New-Object System.Windows.Thickness 0, 0, 0, 12
    [System.Windows.Controls.DockPanel]::SetDock($header, 'Top')

    $title = New-Object System.Windows.Controls.TextBlock
    $title.Text = 'FenceDesk'
    $title.FontSize = 18
    $title.FontWeight = [System.Windows.FontWeights]::SemiBold
    $title.Foreground = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(210, 220, 235))

    $sub = New-Object System.Windows.Controls.TextBlock
    $sub.Text = 'Desktop fence organizer'
    $sub.FontSize = 11
    $sub.Margin = New-Object System.Windows.Thickness 0, 2, 0, 0
    $sub.Foreground = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(140, 155, 175))

    [void]$header.Children.Add($title)
    [void]$header.Children.Add($sub)

    $buttons = New-Object System.Windows.Controls.StackPanel
    $buttons.Orientation = 'Vertical'

    $btnShow = New-Object System.Windows.Controls.Button
    $btnShow.Content = 'Show fences'
    $btnShow.Margin = New-Object System.Windows.Thickness 0, 0, 0, 8
    $btnShow.Padding = New-Object System.Windows.Thickness 10, 8, 10, 8
    $btnShow.HorizontalContentAlignment = 'Left'
    $btnShow.Background = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(32, 48, 72))
    $btnShow.Foreground = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(220, 230, 245))
    $btnShow.BorderBrush = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(60, 90, 130))
    $btnShow.Cursor = [System.Windows.Input.Cursors]::Hand
    $btnShow.Add_Click({ try { Show-AllFences } catch { Write-FenceLog $_.Exception.Message } })

    $btnHide = New-Object System.Windows.Controls.Button
    $btnHide.Content = 'Hide fences'
    $btnHide.Margin = New-Object System.Windows.Thickness 0, 0, 0, 8
    $btnHide.Padding = New-Object System.Windows.Thickness 10, 8, 10, 8
    $btnHide.HorizontalContentAlignment = 'Left'
    $btnHide.Background = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(32, 48, 72))
    $btnHide.Foreground = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(220, 230, 245))
    $btnHide.BorderBrush = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(60, 90, 130))
    $btnHide.Cursor = [System.Windows.Input.Cursors]::Hand
    $btnHide.Add_Click({ try { Hide-AllFences } catch { Write-FenceLog $_.Exception.Message } })

    $btnFront = New-Object System.Windows.Controls.Button
    $btnFront.Content = 'Bring fences to front'
    $btnFront.Margin = New-Object System.Windows.Thickness 0, 0, 0, 8
    $btnFront.Padding = New-Object System.Windows.Thickness 10, 8, 10, 8
    $btnFront.HorizontalContentAlignment = 'Left'
    $btnFront.Background = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(32, 48, 72))
    $btnFront.Foreground = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(220, 230, 245))
    $btnFront.BorderBrush = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(60, 90, 130))
    $btnFront.Cursor = [System.Windows.Input.Cursors]::Hand
    $btnFront.Add_Click({
        try {
            Show-AllFences
            foreach ($id in @($script:FenceWindows.Keys)) {
                $w = $script:FenceWindows[$id].Window
                if ($null -ne $w) {
                    try { $w.Activate() } catch { }
                    # Raise with smart topmost (desktop = topmost for visibility; under focused apps/games)
                    try { Pin-FenceWindowToDesktop -Window $w -Raise | Out-Null } catch { }
                }
            }
        }
        catch { Write-FenceLog $_.Exception.Message }
    })

    $btnNew = New-Object System.Windows.Controls.Button
    $btnNew.Content = 'New fence'
    $btnNew.Margin = New-Object System.Windows.Thickness 0, 0, 0, 8
    $btnNew.Padding = New-Object System.Windows.Thickness 10, 8, 10, 8
    $btnNew.HorizontalContentAlignment = 'Left'
    $btnNew.Background = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(32, 48, 72))
    $btnNew.Foreground = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(220, 230, 245))
    $btnNew.BorderBrush = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(60, 90, 130))
    $btnNew.Cursor = [System.Windows.Input.Cursors]::Hand
    $btnNew.Add_Click({ try { New-FenceFromTray } catch { Write-FenceLog $_.Exception.Message } })

    $btnColorAll = New-Object System.Windows.Controls.Button
    $btnColorAll.Content = 'Background color (all fences)...'
    $btnColorAll.Margin = New-Object System.Windows.Thickness 0, 0, 0, 8
    $btnColorAll.Padding = New-Object System.Windows.Thickness 10, 8, 10, 8
    $btnColorAll.HorizontalContentAlignment = 'Left'
    $btnColorAll.Background = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(32, 48, 72))
    $btnColorAll.Foreground = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(220, 230, 245))
    $btnColorAll.BorderBrush = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(60, 90, 130))
    $btnColorAll.Cursor = [System.Windows.Input.Cursors]::Hand
    $btnColorAll.Add_Click({
        try {
            $firstId = $null
            foreach ($f in @($script:Layout.fences)) {
                if ($f -and $f.id) { $firstId = $f.id; break }
            }
            Show-FenceColorDialog -FenceId $firstId -ApplyToAll
        }
        catch { Write-FenceLog $_.Exception.Message }
    })

    $btnResetColors = New-Object System.Windows.Controls.Button
    $btnResetColors.Content = 'Reset all fence colors'
    $btnResetColors.Margin = New-Object System.Windows.Thickness 0, 0, 0, 8
    $btnResetColors.Padding = New-Object System.Windows.Thickness 10, 8, 10, 8
    $btnResetColors.HorizontalContentAlignment = 'Left'
    $btnResetColors.Background = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(32, 48, 72))
    $btnResetColors.Foreground = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(220, 230, 245))
    $btnResetColors.BorderBrush = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(60, 90, 130))
    $btnResetColors.Cursor = [System.Windows.Input.Cursors]::Hand
    $btnResetColors.Add_Click({ try { Reset-AllFencesBackgroundColor } catch { Write-FenceLog $_.Exception.Message } })

    $btnExit = New-Object System.Windows.Controls.Button
    $btnExit.Content = 'Exit FenceDesk (close completely)'
    $btnExit.Margin = New-Object System.Windows.Thickness 0, 8, 0, 0
    $btnExit.Padding = New-Object System.Windows.Thickness 10, 8, 10, 8
    $btnExit.HorizontalContentAlignment = 'Left'
    $btnExit.Background = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(90, 40, 50))
    $btnExit.Foreground = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(255, 220, 220))
    $btnExit.BorderBrush = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(140, 70, 80))
    $btnExit.Cursor = [System.Windows.Input.Cursors]::Hand
    $btnExit.Add_Click({
        try { Close-FenceDeskApplication } catch { [Environment]::Exit(0) }
    })

    $hint = New-Object System.Windows.Controls.TextBlock
    $hint.Text = "This panel stays out of Alt+Tab and Win+D. Close it to hide (app keeps running) or choose Exit to quit. Left-click the tray icon to reopen."
    $hint.TextWrapping = 'Wrap'
    $hint.FontSize = 10
    $hint.Margin = New-Object System.Windows.Thickness 0, 12, 0, 0
    $hint.Foreground = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(120, 135, 155))
    [System.Windows.Controls.DockPanel]::SetDock($hint, 'Bottom')

    [void]$buttons.Children.Add($btnShow)
    [void]$buttons.Children.Add($btnHide)
    [void]$buttons.Children.Add($btnFront)
    [void]$buttons.Children.Add($btnNew)
    [void]$buttons.Children.Add($btnColorAll)
    [void]$buttons.Children.Add($btnResetColors)
    [void]$buttons.Children.Add($btnExit)

    [void]$root.Children.Add($header)
    [void]$root.Children.Add($hint)
    [void]$root.Children.Add($buttons)
    $win.Content = $root

    $win.Add_Closing({
        param($s, $e)
        # Closing control window exits the whole app
        if ($script:FenceDeskExiting) { return }
        $r = [System.Windows.MessageBox]::Show(
            "Exit FenceDesk and close all fences?`n`nClick No to keep running (panel will hide).",
            'FenceDesk',
            [System.Windows.MessageBoxButton]::YesNo,
            [System.Windows.MessageBoxImage]::Question
        )
        if ($r -eq [System.Windows.MessageBoxResult]::Yes) {
            $script:FenceDeskExiting = $true
            try { Close-FenceDeskApplication -FromWindowClose } catch { }
        }
        else {
            $e.Cancel = $true
            $script:TaskbarHostUserOpen = $false
            try { $s.Hide() } catch { $s.Visibility = 'Hidden' }
        }
    })

    # If shell restores this window (Win+D cycle), hide it unless user opened it
    $win.Add_StateChanged({
        param($s, $e)
        try {
            if ($script:FenceDeskExiting) { return }
            if ($script:TaskbarHostUserOpen) { return }
            if ($s.WindowState -eq [System.Windows.WindowState]::Minimized) { return }
            # Unexpected restore — hide again without focus
            $s.Dispatcher.BeginInvoke([action]{
                try {
                    if (-not $script:TaskbarHostUserOpen -and -not $script:FenceDeskExiting) {
                        $script:TaskbarHost.Hide()
                    }
                }
                catch { }
            }) | Out-Null
        }
        catch { }
    })

    $win.Add_IsVisibleChanged({
        param($s, $e)
        try {
            if ($script:FenceDeskExiting) { return }
            if ($script:TaskbarHostUserOpen) { return }
            if ($s.IsVisible) {
                $s.Dispatcher.BeginInvoke([action]{
                    try {
                        if (-not $script:TaskbarHostUserOpen -and -not $script:FenceDeskExiting) {
                            $script:TaskbarHost.Hide()
                        }
                    }
                    catch { }
                }) | Out-Null
            }
        }
        catch { }
    })

    $script:TaskbarHost = $win
    # Create HWND then hide — do NOT leave minimized (Win+D restores minimized windows)
    $win.Show()
    try { Exclude-WindowFromAltTab -Window $win } catch { }
    $win.Hide()
    $script:TaskbarHostUserOpen = $false
}

function Close-FenceDeskApplication {
    param([switch]$FromWindowClose)
    $script:FenceDeskExiting = $true
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
        if ($script:TaskbarHost -and -not $FromWindowClose) {
            $script:TaskbarHost.Close()
        }
    }
    catch { }
    try {
        if ($script:WpfApp) { $script:WpfApp.Shutdown() }
    }
    catch { }
    try { [System.Windows.Forms.Application]::Exit() } catch { }
}
