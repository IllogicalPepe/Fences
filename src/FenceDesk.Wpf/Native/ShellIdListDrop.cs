using System.Runtime.InteropServices;
using System.Text;
using FenceDesk.Services;

namespace FenceDesk.Native;

/// <summary>
/// Extracts paths from Explorer drag-drop "Shell IDList Array" data.
/// Needed for virtual desktop icons (Recycle Bin, This PC, …) which do not
/// appear in <see cref="System.Windows.DataFormats.FileDrop"/>.
/// </summary>
internal static class ShellIdListDrop
{
    public const string FormatName = "Shell IDList Array";

    // SIGDN values (shobjidl_core.h)
    private const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;
    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const uint SIGDN_NORMALDISPLAY = 0x00000000;
    private const uint SIGDN_PARENTRELATIVEPARSING = 0x80018001;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetNameFromIDList(IntPtr pidl, uint sigdnName, out IntPtr ppszName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDList(IntPtr pidl, StringBuilder pszPath);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    public static bool IsPresent(System.Windows.IDataObject data)
    {
        try
        {
            return data.GetDataPresent(FormatName) ||
                   data.GetDataPresent(FormatName, true);
        }
        catch { return false; }
    }

    public static List<string> ExtractPaths(System.Windows.IDataObject data)
    {
        var results = new List<string>();
        try
        {
            object? raw = null;
            try { raw = data.GetData(FormatName); } catch { /* ignore */ }
            if (raw is null)
            {
                try { raw = data.GetData(FormatName, true); } catch { /* ignore */ }
            }
            if (raw is null) return results;

            var bytes = ToBytes(raw);
            if (bytes is null || bytes.Length < 8) return results;

            // CIDA: UINT cidl; UINT aoffset[cidl+1];
            var cidl = BitConverter.ToUInt32(bytes, 0);
            // offsets: index 0 = parent folder, 1..cidl = relative children
            // Absolute PIDLs for items: parent + child (ILCombine) OR for desktop items
            // the offset often points at absolute PIDLs when parent is empty desktop.

            for (uint i = 0; i <= cidl && i < 64; i++)
            {
                var offIndex = 4 + (int)(i * 4);
                if (offIndex + 4 > bytes.Length) break;
                var offset = BitConverter.ToUInt32(bytes, offIndex);
                if (offset == 0 || offset >= bytes.Length) continue;

                // For i==0 this is the parent; for children we need combine.
                // Many desktop virtual icons: parent = desktop absolute, child = relative.
            }

            // Walk items 1..cidl (skip parent at 0 when cidl >= 1)
            if (cidl == 0)
            {
                // Single absolute PIDL at offset[0]
                var off0 = BitConverter.ToUInt32(bytes, 4);
                TryAddPidl(bytes, off0, results);
            }
            else
            {
                var parentOff = BitConverter.ToUInt32(bytes, 4);
                for (uint i = 1; i <= cidl; i++)
                {
                    var offIndex = 4 + (int)(i * 4);
                    if (offIndex + 4 > bytes.Length) break;
                    var childOff = BitConverter.ToUInt32(bytes, offIndex);
                    if (childOff == 0 || childOff >= bytes.Length) continue;

                    // Prefer absolute interpretation of child offset (Explorer often stores
                    // full PIDLs). Fall back to parent+child combine.
                    if (!TryAddPidl(bytes, childOff, results))
                        TryAddCombined(bytes, parentOff, childOff, results);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("ShellIdListDrop: " + ex.Message);
        }

        return results;
    }

    private static byte[]? ToBytes(object raw)
    {
        switch (raw)
        {
            case MemoryStream ms:
                return ms.ToArray();
            case byte[] b:
                return b;
            case System.IO.Stream stream:
            {
                using var m = new MemoryStream();
                stream.CopyTo(m);
                return m.ToArray();
            }
            default:
                // Some hosts hand back a raw HGLOBAL pointer via COM — rare in WPF
                return null;
        }
    }

    private static bool TryAddPidl(byte[] bytes, uint offset, List<string> results)
    {
        if (offset >= bytes.Length) return false;
        var handle = AllocPidl(bytes, (int)offset);
        if (handle == IntPtr.Zero) return false;
        try
        {
            var path = NameFromPidl(handle);
            if (string.IsNullOrWhiteSpace(path)) return false;
            path = NormalizeShellPath(path);
            if (!results.Contains(path, StringComparer.OrdinalIgnoreCase))
                results.Add(path);
            return true;
        }
        finally
        {
            Marshal.FreeCoTaskMem(handle);
        }
    }

    private static void TryAddCombined(byte[] bytes, uint parentOff, uint childOff, List<string> results)
    {
        var parent = AllocPidl(bytes, (int)parentOff);
        var child = AllocPidl(bytes, (int)childOff);
        if (parent == IntPtr.Zero || child == IntPtr.Zero)
        {
            if (parent != IntPtr.Zero) Marshal.FreeCoTaskMem(parent);
            if (child != IntPtr.Zero) Marshal.FreeCoTaskMem(child);
            return;
        }
        try
        {
            var combined = ILCombine(parent, child);
            if (combined == IntPtr.Zero) return;
            try
            {
                var path = NameFromPidl(combined);
                if (string.IsNullOrWhiteSpace(path)) return;
                path = NormalizeShellPath(path);
                if (!results.Contains(path, StringComparer.OrdinalIgnoreCase))
                    results.Add(path);
            }
            finally
            {
                Marshal.FreeCoTaskMem(combined);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(parent);
            Marshal.FreeCoTaskMem(child);
        }
    }

    private static IntPtr AllocPidl(byte[] bytes, int offset)
    {
        if (offset < 0 || offset + 2 > bytes.Length) return IntPtr.Zero;
        // ITEMIDLIST is a chain of SHITEMID until two zero bytes (cb == 0)
        var end = offset;
        while (end + 2 <= bytes.Length)
        {
            var cb = BitConverter.ToUInt16(bytes, end);
            if (cb == 0)
            {
                end += 2; // include terminator
                break;
            }
            if (end + cb > bytes.Length) return IntPtr.Zero;
            end += cb;
        }
        var len = end - offset;
        if (len < 2) return IntPtr.Zero;
        var ptr = Marshal.AllocCoTaskMem(len);
        Marshal.Copy(bytes, offset, ptr, len);
        return ptr;
    }

    [DllImport("shell32.dll")]
    private static extern IntPtr ILCombine(IntPtr pidl1, IntPtr pidl2);

    private static string? NameFromPidl(IntPtr pidl)
    {
        // Filesystem path first
        try
        {
            var sb = new StringBuilder(520);
            if (SHGetPathFromIDList(pidl, sb) && sb.Length > 0)
                return sb.ToString();
        }
        catch { /* ignore */ }

        // Virtual shell items (Recycle Bin etc.)
        foreach (var sigdn in new[]
                 {
                     SIGDN_DESKTOPABSOLUTEPARSING,
                     SIGDN_PARENTRELATIVEPARSING,
                     SIGDN_NORMALDISPLAY
                 })
        {
            try
            {
                if (SHGetNameFromIDList(pidl, sigdn, out var pName) == 0 && pName != IntPtr.Zero)
                {
                    try
                    {
                        var s = Marshal.PtrToStringUni(pName);
                        if (!string.IsNullOrWhiteSpace(s)) return s;
                    }
                    finally { CoTaskMemFree(pName); }
                }
            }
            catch { /* ignore */ }
        }
        return null;
    }

    /// <summary>
    /// Map display names / partial shell strings to stable ::{CLSID} form.
    /// </summary>
    public static string NormalizeShellPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        path = path.Trim();

        // Already a parsing name
        if (path.StartsWith("::{", StringComparison.Ordinal) ||
            path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            var clsid = DesktopIconService.GetShellClsid(path);
            if (clsid is not null &&
                DesktopIconService.ShellDesktopIcons.TryGetValue(clsid, out var info))
                return info.Path; // ::{...}
            return path;
        }

        // "Recycle Bin" display name or known labels
        foreach (var kv in DesktopIconService.ShellDesktopIcons)
        {
            if (path.Equals(kv.Value.Name, StringComparison.OrdinalIgnoreCase) ||
                path.Equals(kv.Value.Launch, StringComparison.OrdinalIgnoreCase) ||
                path.Equals(kv.Value.Path, StringComparison.OrdinalIgnoreCase) ||
                path.Contains(kv.Key.Trim('{', '}'), StringComparison.OrdinalIgnoreCase))
                return kv.Value.Path;
        }

        if (path.Contains("Recycle", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("645FF040", StringComparison.OrdinalIgnoreCase))
            return DesktopIconService.RecycleBinPath;

        return path;
    }
}
