# FenceDesk application icon generation and loading

function Get-FenceDeskIconPath {
    $assets = Join-Path $script:AppDir 'Assets'
    if (-not (Test-Path -LiteralPath $assets)) {
        New-Item -ItemType Directory -Path $assets -Force | Out-Null
    }
    return (Join-Path $assets 'FenceDesk.ico')
}

function New-FenceDeskBitmap {
    param([int]$Size = 64)
    $bmp = New-Object System.Drawing.Bitmap $Size, $Size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)

        $scale = $Size / 64.0
        # Background rounded-ish panel
        $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 18, 32, 56))
        $accent = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 70, 140, 220))
        $panel = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(230, 28, 48, 78))
        $light = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 180, 210, 240))
        $border = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(180, 120, 170, 230), [Math]::Max(1, [int](1.5 * $scale)))

        $pad = [int](4 * $scale)
        $g.FillRectangle($bg, 0, 0, $Size, $Size)

        # Outer frame
        $g.FillRectangle($panel, $pad, $pad, $Size - 2 * $pad, $Size - 2 * $pad)
        $g.DrawRectangle($border, $pad, $pad, $Size - 2 * $pad - 1, $Size - 2 * $pad - 1)

        # Three "fence" zones
        $m = [int](8 * $scale)
        $gap = [int](3 * $scale)
        $inner = $Size - 2 * $m
        $colW = [int](($inner - $gap) / 2)
        $rowH = [int](($inner - $gap) / 2)

        $g.FillRectangle($accent, $m, $m, $colW, $rowH)
        $g.FillRectangle($light, $m + $colW + $gap, $m, $colW, [int]($rowH * 0.7))
        $g.FillRectangle($light, $m, $m + $rowH + $gap, $inner, $rowH)

        # Tiny "icon dots" inside fences
        $dot = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(220, 255, 255, 255))
        $ds = [Math]::Max(2, [int](3 * $scale))
        $g.FillEllipse($dot, $m + [int](3*$scale), $m + [int](3*$scale), $ds, $ds)
        $g.FillEllipse($dot, $m + [int](10*$scale), $m + [int](3*$scale), $ds, $ds)
        $g.FillEllipse($dot, $m + [int](3*$scale), $m + $rowH + $gap + [int](4*$scale), $ds, $ds)
        $g.FillEllipse($dot, $m + [int](12*$scale), $m + $rowH + $gap + [int](4*$scale), $ds, $ds)
        $g.FillEllipse($dot, $m + [int](21*$scale), $m + $rowH + $gap + [int](4*$scale), $ds, $ds)

        $bg.Dispose(); $accent.Dispose(); $panel.Dispose(); $light.Dispose(); $border.Dispose(); $dot.Dispose()
    }
    finally { $g.Dispose() }
    return $bmp
}

function Save-FenceDeskIconFile {
    param([string]$Path)
    # Build multi-size ICO (16, 32, 48, 256)
    $sizes = @(16, 32, 48, 256)
    $images = @()
    foreach ($s in $sizes) {
        $images += ,(New-FenceDeskBitmap -Size $s)
    }
    try {
        $ms = New-Object System.IO.MemoryStream
        $bw = New-Object System.IO.BinaryWriter $ms

        # ICONDIR
        $bw.Write([uint16]0)           # reserved
        $bw.Write([uint16]1)           # type icon
        $bw.Write([uint16]$images.Count)

        $offset = 6 + (16 * $images.Count)
        $pngBlobs = @()

        for ($i = 0; $i -lt $images.Count; $i++) {
            $bmp = $images[$i]
            $pngMs = New-Object System.IO.MemoryStream
            $bmp.Save($pngMs, [System.Drawing.Imaging.ImageFormat]::Png)
            $bytes = $pngMs.ToArray()
            $pngMs.Dispose()
            $pngBlobs += ,$bytes

            $w = $bmp.Width
            $h = $bmp.Height
            if ($w -ge 256) { $w = 0 }
            if ($h -ge 256) { $h = 0 }
            $bw.Write([byte]$w)
            $bw.Write([byte]$h)
            $bw.Write([byte]0)         # color count
            $bw.Write([byte]0)         # reserved
            $bw.Write([uint16]1)       # planes
            $bw.Write([uint16]32)      # bit count
            $bw.Write([uint32]$bytes.Length)
            $bw.Write([uint32]$offset)
            $offset += $bytes.Length
        }

        foreach ($blob in $pngBlobs) {
            $bw.Write($blob)
        }
        $bw.Flush()
        [System.IO.File]::WriteAllBytes($Path, $ms.ToArray())
        $bw.Dispose()
        $ms.Dispose()
    }
    finally {
        foreach ($b in $images) { try { $b.Dispose() } catch { } }
    }
}

function Get-FenceDeskIcon {
    param([switch]$ForceRegenerate)
    $path = Get-FenceDeskIconPath
    if ($ForceRegenerate -or -not (Test-Path -LiteralPath $path)) {
        try {
            Save-FenceDeskIconFile -Path $path
            Write-FenceLog "Wrote app icon: $path"
        }
        catch {
            Write-FenceLog "Icon generate failed: $($_.Exception.Message)"
        }
    }
    if (Test-Path -LiteralPath $path) {
        try {
            return New-Object System.Drawing.Icon $path
        }
        catch {
            Write-FenceLog "Icon load failed: $($_.Exception.Message)"
        }
    }
    # Fallback in-memory
    $bmp = New-FenceDeskBitmap -Size 32
    try {
        return [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
    }
    finally {
        # don't dispose bmp while icon uses handle — clone
    }
}

function Get-FenceDeskImageSource {
    $path = Get-FenceDeskIconPath
    if (-not (Test-Path -LiteralPath $path)) {
        try { Save-FenceDeskIconFile -Path $path } catch { }
    }
    if (Test-Path -LiteralPath $path) {
        try {
            $bi = New-Object System.Windows.Media.Imaging.BitmapImage
            $bi.BeginInit()
            $bi.CacheOption = [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
            $bi.UriSource = [Uri]::new($path)
            $bi.EndInit()
            $bi.Freeze()
            return $bi
        }
        catch { }
    }
    return $null
}
