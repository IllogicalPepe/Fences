using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FenceDesk.Native;

namespace FenceDesk.Services;

public sealed class IconService
{
    private readonly Dictionary<string, ImageSource> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly DesktopIconService _desktopIcons;
    private int _iconSize = 48;

    public IconService(DesktopIconService desktopIcons)
    {
        _desktopIcons = desktopIcons;
        Directory.CreateDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FenceDesk", "icon-cache"));
        RefreshMetrics();
    }

    public int IconSize => _iconSize;
    public double FontSize { get; private set; } = 12;
    public int TileWidth { get; private set; } = 76;
    public int LabelMaxHeight { get; private set; } = 32;

    public void RefreshMetrics()
    {
        _iconSize = 48;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Bags\1\Desktop");
            if (key?.GetValue("IconSize") is int i && i is >= 16 and <= 256)
                _iconSize = i;
        }
        catch { /* ignore */ }

        FontSize = 12;
        try
        {
            using var font = SystemFonts.IconTitleFont;
            if (font.Size > 0) FontSize = font.Size;
        }
        catch { /* ignore */ }

        if (_iconSize <= 32 && FontSize < 10) FontSize = 11;
        else if (_iconSize <= 48 && FontSize < 11) FontSize = 12;
        else if (FontSize < 12) FontSize = 13;

        TileWidth = Math.Max(72, _iconSize + 28);
        LabelMaxHeight = (int)Math.Ceiling(FontSize * 2.4) + 4;
    }

    public void ClearCache() => _cache.Clear();

    public string GetDisplayLabel(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Item";
        if (DesktopIconService.IsShellNamespacePath(path))
        {
            var clsid = DesktopIconService.GetShellClsid(path);
            if (clsid is not null && DesktopIconService.ShellDesktopIcons.TryGetValue(clsid, out var info))
                return info.Name;
            if (path.Contains("Recycle", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("645FF040", StringComparison.OrdinalIgnoreCase))
                return "Recycle Bin";
            return "Shell item";
        }
        try
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(name) ? path : name;
        }
        catch { return "Item"; }
    }

    public ImageSource GetItemImage(string path, int? size = null)
    {
        var sz = size ?? _iconSize;
        var iconPath = _desktopIcons.ResolveItemPath(path);
        var key = $"{sz}|{path}|{iconPath}";
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        ImageSource? img = null;
        try
        {
            img = GetShellFileIcon(path, sz);
            if (img is null && iconPath != path)
                img = GetShellFileIcon(iconPath, sz);
        }
        catch { /* ignore */ }

        img ??= CreateDefaultImage(sz);
        _cache[key] = img;
        return img;
    }

    private ImageSource? GetShellFileIcon(string path, int size)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var fromLnk = GetShortcutIcon(path, size);
            if (fromLnk is not null) return fromLnk;
        }

        if (DesktopIconService.IsShellNamespacePath(path) ||
            path.Contains("645FF040", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("RecycleBin", StringComparison.OrdinalIgnoreCase))
        {
            var rb = GetRecycleBinIcon(size);
            if (rb is not null) return rb;
        }

        try
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                using var ico = Icon.ExtractAssociatedIcon(path);
                if (ico is not null) return ConvertIcon(ico, size);
            }
        }
        catch { /* ignore */ }

        return GetShellCore(path, size);
    }

    private ImageSource? GetShortcutIcon(string lnkPath, int size)
    {
        try
        {
            if (!File.Exists(lnkPath)) return null;
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t is not null)
            {
                dynamic sh = Activator.CreateInstance(t)!;
                dynamic sc = sh.CreateShortcut(lnkPath);
                string iconLoc = (string?)sc.IconLocation ?? "";
                if (!string.IsNullOrWhiteSpace(iconLoc))
                {
                    var parts = iconLoc.Split(',');
                    var iconFile = parts[0].Trim().Trim('"');
                    var iconIdx = 0;
                    if (parts.Length > 1) int.TryParse(parts[1].Trim(), out iconIdx);
                    if (File.Exists(iconFile))
                    {
                        var img = GetFromFileIndex(iconFile, iconIdx, size);
                        if (img is not null) return img;
                    }
                }
                string target = (string?)sc.TargetPath ?? "";
                if (!string.IsNullOrWhiteSpace(target) && (File.Exists(target) || Directory.Exists(target)))
                {
                    var img = GetFromFileIndex(target, 0, size) ?? GetShellCore(target, size);
                    if (img is not null) return img;
                }
            }
            using var ico = Icon.ExtractAssociatedIcon(lnkPath);
            if (ico is not null) return ConvertIcon(ico, size);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Shortcut icon failed ({lnkPath}): {ex.Message}");
        }
        return GetShellCore(lnkPath, size);
    }

    private ImageSource? GetFromFileIndex(string file, int index, int size)
    {
        try
        {
            if (index != 0 || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                var large = new IntPtr[1];
                var small = new IntPtr[1];
                var n = NativeMethods.ExtractIconEx(file, index, large, small, 1);
                if (n > 0)
                {
                    var h = size <= 16 && small[0] != IntPtr.Zero ? small[0] : large[0];
                    if (h == IntPtr.Zero) h = small[0] != IntPtr.Zero ? small[0] : large[0];
                    if (h != IntPtr.Zero)
                    {
                        try
                        {
                            using var ico = (Icon)Icon.FromHandle(h).Clone();
                            return ConvertIcon(ico, size);
                        }
                        finally
                        {
                            if (large[0] != IntPtr.Zero) NativeMethods.DestroyIcon(large[0]);
                            if (small[0] != IntPtr.Zero) NativeMethods.DestroyIcon(small[0]);
                        }
                    }
                }
            }
            if (!File.Exists(file)) return null;
            using var ico2 = Icon.ExtractAssociatedIcon(file);
            if (ico2 is not null) return ConvertIcon(ico2, size);
        }
        catch { /* ignore */ }
        return null;
    }

    private ImageSource? GetRecycleBinIcon(int size)
    {
        // 1) Official stock icon (correct full/empty system artwork)
        try
        {
            var info = new NativeMethods.SHSTOCKICONINFO
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.SHSTOCKICONINFO>()
            };
            var flags = NativeMethods.SHGSI_ICON |
                        (size <= 16 ? NativeMethods.SHGSI_SMALLICON : NativeMethods.SHGSI_LARGEICON);
            // Prefer empty bin; full bin as fallback
            foreach (var id in new[] { NativeMethods.SIID_RECYCLER, NativeMethods.SIID_RECYCLERFULL })
            {
                if (NativeMethods.SHGetStockIconInfo(id, flags, ref info) == 0 && info.hIcon != IntPtr.Zero)
                {
                    try
                    {
                        using var ico = (Icon)Icon.FromHandle(info.hIcon).Clone();
                        return ConvertIcon(ico, size);
                    }
                    finally { NativeMethods.DestroyIcon(info.hIcon); }
                }
            }
        }
        catch { /* ignore */ }

        // 2) imageres.dll / shell32.dll known indices
        try
        {
            var imageres = Path.Combine(Environment.SystemDirectory, "imageres.dll");
            var shell32 = Path.Combine(Environment.SystemDirectory, "shell32.dll");
            foreach (var (file, idx) in new[]
                     {
                         (imageres, 50), (imageres, 49), (imageres, 51),
                         (shell32, 31), (shell32, 32)
                     })
            {
                if (!File.Exists(file)) continue;
                var img = GetFromFileIndex(file, idx, size);
                if (img is not null) return img;
            }
        }
        catch { /* ignore */ }

        // 3) Shell path
        return GetShellCore("::{645FF040-5081-101B-9F08-00AA002F954E}", size)
               ?? GetShellCore("shell:RecycleBinFolder", size);
    }

    private ImageSource? GetShellCore(string path, int size)
    {
        try
        {
            var flags = NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON;
            var attr = NativeMethods.FILE_ATTRIBUTE_NORMAL;
            var exists = File.Exists(path) || Directory.Exists(path);
            if (exists && Directory.Exists(path))
                attr = NativeMethods.FILE_ATTRIBUTE_DIRECTORY;
            else if (!exists && !DesktopIconService.IsShellNamespacePath(path) &&
                     !path.StartsWith("::", StringComparison.Ordinal) &&
                     !path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                return null;

            var r = NativeMethods.SHGetFileInfo(path, attr, out var fi,
                (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(), flags);
            if (r == IntPtr.Zero || fi.hIcon == IntPtr.Zero) return null;
            try
            {
                using var ico = (Icon)Icon.FromHandle(fi.hIcon).Clone();
                return ConvertIcon(ico, size);
            }
            finally { NativeMethods.DestroyIcon(fi.hIcon); }
        }
        catch { return null; }
    }

    private static ImageSource ConvertIcon(Icon icon, int size)
    {
        using var bmp = new Bitmap(icon.ToBitmap(), new System.Drawing.Size(size, size));
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    private static ImageSource CreateDefaultImage(int size)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.FromArgb(40, 80, 120, 180));
            using var pen = new Pen(System.Drawing.Color.FromArgb(200, 200, 220, 255), 2);
            g.DrawRectangle(pen, 4, 4, size - 9, size - 9);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }
}
