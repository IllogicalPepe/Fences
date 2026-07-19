# System tray UI for FenceDesk

function New-FenceDeskTrayIcon {
    try {
        if (Get-Command Get-FenceDeskIcon -ErrorAction SilentlyContinue) {
            $ico = Get-FenceDeskIcon
            if ($null -ne $ico) { return $ico }
        }
    }
    catch { }
    $size = 16
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.Clear([System.Drawing.Color]::Transparent)
        $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 30, 48, 80))
        $fg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 160, 190, 230))
        $g.FillRectangle($bg, 1, 1, 14, 14)
        $g.FillRectangle($fg, 3, 3, 5, 4)
        $g.FillRectangle($fg, 9, 3, 4, 4)
        $g.FillRectangle($fg, 3, 9, 10, 4)
        $bg.Dispose()
        $fg.Dispose()
    }
    finally { $g.Dispose() }
    return [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
}

function Get-StartWithWindowsEnabled {
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $val = (Get-ItemProperty -Path $key -Name 'FenceDesk' -ErrorAction SilentlyContinue).FenceDesk
    return -not [string]::IsNullOrWhiteSpace($val)
}

function Set-StartWithWindows {
    param([bool]$Enabled)
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $launcher = Join-Path $script:AppDir 'Start.vbs'
    if (-not (Test-Path -LiteralPath $launcher)) {
        $launcher = Join-Path $script:AppDir 'Start.bat'
    }
    if ($Enabled) {
        $cmd = "wscript.exe `"$launcher`""
        Set-ItemProperty -Path $key -Name 'FenceDesk' -Value $cmd -Type String -Force
    }
    else {
        Remove-ItemProperty -Path $key -Name 'FenceDesk' -ErrorAction SilentlyContinue
    }
    if ($script:Layout.settings) {
        $script:Layout.settings.startWithWindows = $Enabled
        Save-FenceLayout -Layout $script:Layout -Immediate
    }
}

function Initialize-TrayApp {
    $script:NotifyIcon = New-Object System.Windows.Forms.NotifyIcon
    $script:NotifyIcon.Text = 'FenceDesk'
    $script:NotifyIcon.Icon = New-FenceDeskTrayIcon
    $script:NotifyIcon.Visible = $true

    $menu = New-Object System.Windows.Forms.ContextMenuStrip

    $miNew = New-Object System.Windows.Forms.ToolStripMenuItem 'New fence'
    $miNew.Add_Click({
        try { New-FenceFromTray } catch { Write-FenceLog $_.Exception.Message }
    })

    $miNewPortal = New-Object System.Windows.Forms.ToolStripMenuItem 'New portal fence...'
    $miNewPortal.Add_Click({
        try {
            $folder = Show-FolderPicker -Description 'Select folder for new portal fence'
            if (-not $folder) { return }
            $wa = [System.Windows.SystemParameters]::WorkArea
            $x = [int]($wa.Left + 100)
            $y = [int]($wa.Top + 100)
            $title = [System.IO.Path]::GetFileName($folder)
            if ([string]::IsNullOrWhiteSpace($title)) { $title = 'Portal' }
            $model = New-FenceModel -Title $title -Mode 'portal' -PortalPath $folder -X $x -Y $y
            Add-FenceModelToLayout -FenceModel $model
            New-FenceWindow -FenceModel $model | Out-Null
        }
        catch { Write-FenceLog $_.Exception.Message }
    })

    $miShow = New-Object System.Windows.Forms.ToolStripMenuItem 'Show fences'
    $miShow.Add_Click({
        try { Show-AllFences } catch { Write-FenceLog $_.Exception.Message }
    })

    $miHide = New-Object System.Windows.Forms.ToolStripMenuItem 'Hide fences'
    $miHide.Add_Click({
        try { Hide-AllFences } catch { Write-FenceLog $_.Exception.Message }
    })

    $miFront = New-Object System.Windows.Forms.ToolStripMenuItem 'Bring fences to front'
    $miFront.Add_Click({
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

    $miLockAll = New-Object System.Windows.Forms.ToolStripMenuItem 'Lock all fences'
    $miLockAll.Add_Click({
        try { Set-AllFencesLocked -Locked $true } catch { Write-FenceLog $_.Exception.Message }
    })

    $miUnlockAll = New-Object System.Windows.Forms.ToolStripMenuItem 'Unlock all fences'
    $miUnlockAll.Add_Click({
        try { Set-AllFencesLocked -Locked $false } catch { Write-FenceLog $_.Exception.Message }
    })

    $miColorAll = New-Object System.Windows.Forms.ToolStripMenuItem 'Background color (all fences)...'
    $miColorAll.Add_Click({
        try {
            $firstId = $null
            foreach ($f in @($script:Layout.fences)) {
                if ($f -and $f.id) { $firstId = $f.id; break }
            }
            Show-FenceColorDialog -FenceId $firstId -ApplyToAll
        }
        catch { Write-FenceLog $_.Exception.Message }
    })

    $miColorResetAll = New-Object System.Windows.Forms.ToolStripMenuItem 'Reset all fence colors'
    $miColorResetAll.Add_Click({
        try { Reset-AllFencesBackgroundColor } catch { Write-FenceLog $_.Exception.Message }
    })

    $miConfig = New-Object System.Windows.Forms.ToolStripMenuItem 'Open config folder'
    $miConfig.Add_Click({
        try {
            $dir = Get-FenceDeskDataDir
            Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$dir`""
        }
        catch { }
    })

    $miAutostart = New-Object System.Windows.Forms.ToolStripMenuItem 'Start with Windows'
    $miAutostart.CheckOnClick = $true
    $miAutostart.Checked = Get-StartWithWindowsEnabled
    $miAutostart.Add_CheckedChanged({
        try { Set-StartWithWindows -Enabled $miAutostart.Checked } catch { }
    }.GetNewClosure())

    $miAbout = New-Object System.Windows.Forms.ToolStripMenuItem 'About FenceDesk'
    $miAbout.Add_Click({
        [System.Windows.Forms.MessageBox]::Show(
            "FenceDesk - desktop fence organizer`nInspired by Stardock Fences (independent clone).`n`nRight-click a fence for options (color, opacity, tabs).`nDrop files onto fences to organize.`nUse the taskbar window or this tray menu to Show, Hide, create fences, or Exit.",
            'FenceDesk',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Information
        ) | Out-Null
    })

    $miExit = New-Object System.Windows.Forms.ToolStripMenuItem 'Exit (close completely)'
    $miExit.Add_Click({
        try { Close-FenceDeskApplication } catch { [Environment]::Exit(0) }
    })

    [void]$menu.Items.Add($miNew)
    [void]$menu.Items.Add($miNewPortal)
    [void]$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
    [void]$menu.Items.Add($miShow)
    [void]$menu.Items.Add($miHide)
    [void]$menu.Items.Add($miFront)
    [void]$menu.Items.Add($miLockAll)
    [void]$menu.Items.Add($miUnlockAll)
    [void]$menu.Items.Add($miColorAll)
    [void]$menu.Items.Add($miColorResetAll)
    [void]$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
    [void]$menu.Items.Add($miConfig)
    [void]$menu.Items.Add($miAutostart)
    [void]$menu.Items.Add($miAbout)
    [void]$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
    [void]$menu.Items.Add($miExit)

    $script:NotifyIcon.ContextMenuStrip = $menu
    # Left-click tray icon: open the control panel (not Alt+Tab / not Win+D)
    $script:NotifyIcon.Add_Click({
        param($s, $e)
        try {
            if ($e.Button -eq [System.Windows.Forms.MouseButtons]::Left) {
                if (Get-Command Show-FenceDeskControlPanel -ErrorAction SilentlyContinue) {
                    Show-FenceDeskControlPanel
                }
                elseif ($script:TaskbarHost) {
                    $script:TaskbarHostUserOpen = $true
                    $script:TaskbarHost.Show()
                    $script:TaskbarHost.WindowState = 'Normal'
                    $script:TaskbarHost.Activate()
                }
            }
        }
        catch { }
    })
}
