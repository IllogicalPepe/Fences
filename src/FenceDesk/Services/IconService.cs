using System.Drawing;
using System.Runtime.InteropServices;
using FenceDesk.Native;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace FenceDesk.Services;

public sealed class IconService
{
    private readonly Dictionary<string, BitmapImage> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly DesktopIconService _desktopIcons;
    private int _iconSize = 48;

    public IconService(DesktopIconService desktopIcons)
    {
        _desktopIcons = desktopIcons;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FenceDesk", "icon-cache");
        Directory.CreateDirectory(dir);
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
            var v = key?.GetValue("IconSize");
            if (v is int i && i is >= 16 and <= 256)
                _iconSize = i;
        }
        catch { /* ignore */ }

        FontSize = 12;
        try
        {
            using var font = System.Drawing.SystemFonts.IconTitleFont;
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
            if (string.IsNullOrWhiteSpace(name))
                name = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(name))
                name = path;
            return name;
        }
        catch
        {
            return "Item";
        }
    }

    public BitmapImage? GetItemImage(string path, int? size = null)
    {
        var sz = size ?? _iconSize;
        var iconPath = _desktopIcons.ResolveItemPath(path);
        var key = $"{sz}|{path}|{iconPath}";
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        BitmapImage? img = null;
        try
        {
            img = GetShellFileIcon(path, sz) ?? (iconPath != path ? GetShellFileIcon(iconPath, sz) : null);
        }
        catch { /* ignore */ }

        if (img is null)
        {
            var known = GetKnownAppExePath(path);
            if (known is not null)
            {
                try
                {
                    using var ico = Icon.ExtractAssociatedIcon(known);
                    if (ico is not null)
                        img = ConvertIconToBitmapImage(ico, sz);
                }
                catch { /* ignore */ }
            }
        }

        img ??= CreateDefaultImage(sz);
        _cache[key] = img;
        return img;
    }

    private BitmapImage? GetShellFileIcon(string path, int size)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var fromLnk = GetShortcutIcon(path, size);
            if (fromLnk is not null) return fromLnk;
        }

        if (DesktopIconService.IsShellNamespacePath(path) ||
            path.Contains("645FF040", StringComparison.OrdinalIgnoreCase))
        {
            var rb = GetRecycleBinIcon(size);
            if (rb is not null) return rb;
        }

        try
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                using var ico = Icon.ExtractAssociatedIcon(path);
                if (ico is not null)
                    return ConvertIconToBitmapImage(ico, size);
            }
        }
        catch { /* ignore */ }

        return GetShellFileIconCore(path, size);
    }

    private BitmapImage? GetShortcutIcon(string lnkPath, int size)
    {
        try
        {
            if (!File.Exists(lnkPath)) return null;

            // Resolve via WScript.Shell COM
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
                    if (!string.IsNullOrEmpty(iconFile) && File.Exists(iconFile))
                    {
                        var img = GetIconFromFileIndex(iconFile, iconIdx, size);
                        if (img is not null) return img;
                    }
                }

                string target = (string?)sc.TargetPath ?? "";
                if (!string.IsNullOrWhiteSpace(target) && (File.Exists(target) || Directory.Exists(target)))
                {
                    var img = GetIconFromFileIndex(target, 0, size) ?? GetShellFileIconCore(target, size);
                    if (img is not null) return img;
                }
            }

            using var ico = Icon.ExtractAssociatedIcon(lnkPath);
            if (ico is not null) return ConvertIconToBitmapImage(ico, size);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Shortcut icon failed ({lnkPath}): {ex.Message}");
        }

        return GetShellFileIconCore(lnkPath, size);
    }

    private BitmapImage? GetIconFromFileIndex(string file, int index, int size)
    {
        try
        {
            if (index != 0 || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                var fromDll = GetIconFromDllIndex(file, index, size);
                if (fromDll is not null) return fromDll;
            }
            if (!File.Exists(file)) return null;
            using var ico = Icon.ExtractAssociatedIcon(file);
            if (ico is not null) return ConvertIconToBitmapImage(ico, size);
        }
        catch { /* ignore */ }
        return null;
    }

    private BitmapImage? GetIconFromDllIndex(string dll, int index, int size)
    {
        try
        {
            if (!File.Exists(dll)) return null;
            var large = new IntPtr[1];
            var small = new IntPtr[1];
            var n = NativeMethods.ExtractIconEx(dll, index, large, small, 1);
            if (n == 0) return null;
            var h = size <= 16 && small[0] != IntPtr.Zero ? small[0] : large[0];
            if (h == IntPtr.Zero) h = small[0] != IntPtr.Zero ? small[0] : large[0];
            if (h == IntPtr.Zero) return null;
            try
            {
                using var ico = (Icon)Icon.FromHandle(h).Clone();
                return ConvertIconToBitmapImage(ico, size);
            }
            finally
            {
                if (large[0] != IntPtr.Zero) NativeMethods.DestroyIcon(large[0]);
                if (small[0] != IntPtr.Zero) NativeMethods.DestroyIcon(small[0]);
            }
        }
        catch { return null; }
    }

    private BitmapImage? GetRecycleBinIcon(int size)
    {
        try
        {
            var img = GetShellFileIconCore("::{645FF040-5081-101B-9F08-00AA002F954E}", size);
            if (img is not null) return img;
            var imageres = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "imageres.dll");
            return GetIconFromDllIndex(imageres, 50, size);
        }
        catch { return null; }
    }

    private BitmapImage? GetShellFileIconCore(string path, int size)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var flags = NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON;
            var attr = NativeMethods.FILE_ATTRIBUTE_NORMAL;
            var exists = File.Exists(path) || Directory.Exists(path);
            if (exists && Directory.Exists(path))
                attr = NativeMethods.FILE_ATTRIBUTE_DIRECTORY;
            else if (!exists && !DesktopIconService.IsShellNamespacePath(path) && !path.StartsWith("::", StringComparison.Ordinal))
                return null;

            var r = NativeMethods.SHGetFileInfo(path, attr, out var fi,
                (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(), flags);
            if (r == IntPtr.Zero || fi.hIcon == IntPtr.Zero) return null;
            try
            {
                using var ico = (Icon)Icon.FromHandle(fi.hIcon).Clone();
                return ConvertIconToBitmapImage(ico, size);
            }
            finally
            {
                NativeMethods.DestroyIcon(fi.hIcon);
            }
        }
        catch { return null; }
    }

    private static BitmapImage ConvertIconToBitmapImage(Icon icon, int size)
    {
        using var bmp = new Bitmap(icon.ToBitmap(), new Size(size, size));
        return BitmapFromPng(bmp);
    }

    private static BitmapImage CreateDefaultImage(int size)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(40, 80, 120, 180));
            using var pen = new Pen(Color.FromArgb(200, 200, 220, 255), 2);
            g.DrawRectangle(pen, 4, 4, size - 9, size - 9);
        }
        return BitmapFromPng(bmp);
    }

    private static BitmapImage BitmapFromPng(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        var bytes = ms.ToArray();

        var ras = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(ras.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            writer.StoreAsync().AsTask().GetAwaiter().GetResult();
            writer.FlushAsync().AsTask().GetAwaiter().GetResult();
        }
        ras.Seek(0);

        var image = new BitmapImage();
        image.SetSource(ras);
        return image;
    }

    private static string? GetKnownAppExePath(string nameOrPath)
    {
        var n = Path.GetFileNameWithoutExtension(nameOrPath)?.ToLowerInvariant() ?? "";
        var candidates = new List<string>();
        if (n.Contains("edge") || n == "microsoft edge")
        {
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"Microsoft\Edge\Application\msedge.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"Microsoft\Edge\Application\msedge.exe"));
        }
        else if (n.Contains("chrome"))
        {
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"Google\Chrome\Application\chrome.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"Google\Chrome\Application\chrome.exe"));
        }
        else if (n.Contains("brave"))
        {
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"BraveSoftware\Brave-Browser\Application\brave.exe"));
        }
        else if (n.Contains("firefox"))
        {
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"Mozilla Firefox\firefox.exe"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }
}


