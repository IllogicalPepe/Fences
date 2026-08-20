using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace FenceDesk.Native;

/// <summary>
/// Desktop surface + icon vs empty-wallpaper hit-testing.
/// Primary: cross-process LVM_HITTEST (once per double-click, SendMessageTimeout).
/// Fallback: MSAA. Never runs from a mouse hook.
/// </summary>
internal static class DesktopHitTest
{
    private const uint GA_ROOT = 2;
    private const uint OBJID_CLIENT = 0xFFFFFFFC;
    private const int CHILDID_SELF = 0;
    private const int IDC_WAIT = 32514;
    private const int IDC_APPSTARTING = 32650;

    private const int ROLE_SYSTEM_CLIENT = 0x0A;
    private const int ROLE_SYSTEM_WINDOW = 0x09;
    private const int ROLE_SYSTEM_PANE = 0x10;
    private const int ROLE_SYSTEM_DOCUMENT = 0x0F;
    private const int ROLE_SYSTEM_LIST = 0x21;
    private const int ROLE_SYSTEM_LISTITEM = 0x22;
    private const int ROLE_SYSTEM_OUTLINEITEM = 0x24;
    private const int ROLE_SYSTEM_GRAPHIC = 0x28;
    private const int ROLE_SYSTEM_STATICTEXT = 0x29;
    private const int ROLE_SYSTEM_TEXT = 0x2A;
    private const int ROLE_SYSTEM_PUSHBUTTON = 0x2B;

    private static readonly Guid IidIAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");

    private const int LvmHitTest = 0x1000 + 18;
    private const uint LvhtOnItem = 0x000E;
    private const uint ProcessVmAccess = 0x0008 | 0x0010 | 0x0020 | 0x0400; // VM_OP|READ|WRITE|QUERY
    private const uint MemCommit = 0x1000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const uint SmtoNormal = 0;
    private const uint SmtoAbortIfHung = 0x0002;

    public static string LastDebug { get; private set; } = "";

    /// <summary>
    /// True if the point is on the real desktop host (Progman/WorkerW),
    /// not on FenceDesk, Explorer folders, taskbar, or other apps.
    /// </summary>
    public static bool IsDesktopSurfaceAt(int screenX, int screenY)
    {
        var pt = new POINT { X = screenX, Y = screenY };
        var hwnd = WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero)
        {
            LastDebug = "no-hwnd";
            return false;
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        if ((int)pid == Environment.ProcessId)
        {
            LastDebug = "self";
            return false;
        }

        var walk = hwnd;
        var chain = "";
        for (var i = 0; i < 12 && walk != IntPtr.Zero; i++)
        {
            var c = ClassName(walk);
            if (chain.Length > 0) chain += ">";
            chain += c;

            if (c is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "TrayNotifyWnd"
                or "NotifyIconOverflowWindow" or "Windows.UI.Core.CoreWindow"
                or "XamlExplorerHostIslandWindow" or "MultitaskingViewFrame"
                or "ImmersiveLauncher" or "SearchPane" or "Windows.UI.Input.InputSite.WindowClass")
            {
                LastDebug = "shell-ui " + c;
                return false;
            }

            // Explorer folder windows / other apps
            if (c is "CabinetWClass" or "ExploreWClass" or "#32770"
                or "Chrome_WidgetWin_1" or "MozillaWindowClass"
                or "ApplicationFrameWindow" or "WinUIDesktopWin32WindowClass")
            {
                LastDebug = "app-window " + c;
                return false;
            }

            walk = GetParent(walk);
        }

        var root = GetAncestor(hwnd, GA_ROOT);
        var rootClass = ClassName(root);
        if (rootClass is not ("Progman" or "WorkerW"))
        {
            LastDebug = "app " + chain + "|r=" + rootClass;
            return false;
        }

        LastDebug = "desktop-surface " + chain + "|r=" + rootClass;
        return true;
    }

    /// <summary>
    /// true = point is on a desktop icon/file, false = empty wallpaper,
    /// null = could not tell (caller should use a conservative fallback).
    /// </summary>
    public static bool? IsDesktopIconAt(int screenX, int screenY)
    {
        try
        {
            var lv = DesktopListViewAt(screenX, screenY);
            if (lv != IntPtr.Zero)
            {
                var lvm = ListViewHitTestRemote(lv, screenX, screenY);
                if (lvm.HasValue)
                {
                    LastDebug = lvm.Value ? "lvm-icon" : "lvm-empty";
                    return lvm.Value;
                }

                var hit = ListViewItemAt(lv, screenX, screenY);
                if (hit.HasValue)
                {
                    LastDebug = hit.Value ? "desktop-icon" : "desktop-empty";
                    return hit.Value;
                }
            }

            var viaPoint = ObjectFromPointIsItem(screenX, screenY);
            if (viaPoint.HasValue)
            {
                LastDebug = viaPoint.Value ? "desktop-icon-pt" : "desktop-empty-pt";
                return viaPoint.Value;
            }

            LastDebug = "icon-unknown";
            return null;
        }
        catch (Exception ex)
        {
            LastDebug = "icon-ex " + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// True when the desktop list-view has a selected icon. After the first click
    /// of a double-click on a file, Explorer has already selected that item.
    /// </summary>
    public static bool? HasDesktopIconSelection(int screenX, int screenY)
    {
        try
        {
            var lv = DesktopListViewAt(screenX, screenY);
            if (lv == IntPtr.Zero) return null;

            var iid = IidIAccessible;
            var hr = AccessibleObjectFromWindow(lv, OBJID_CLIENT, ref iid, out var obj);
            if (hr != 0 || obj is not IAccessible acc)
                return null;

            try
            {
                object? sel;
                try { sel = acc.accSelection; }
                catch { return null; }

                if (sel is null || sel is DBNull)
                    return false;
                if (sel is int id)
                    return id != CHILDID_SELF;
                if (sel is short s)
                    return s != 0;
                if (sel is IAccessible child)
                {
                    Marshal.ReleaseComObject(child);
                    return true;
                }

                return true;
            }
            finally
            {
                Marshal.ReleaseComObject(acc);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// True if the system cursor is the launch/wait pointer (icon open in progress).
    /// </summary>
    public static bool IsLaunchCursor()
    {
        try
        {
            var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(ref info) || info.hCursor == IntPtr.Zero)
                return false;
            var wait = LoadCursor(IntPtr.Zero, (IntPtr)IDC_WAIT);
            var app = LoadCursor(IntPtr.Zero, (IntPtr)IDC_APPSTARTING);
            return info.hCursor == wait || info.hCursor == app;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True if this window looks like a normal app / Explorer folder that would
    /// receive focus after double-clicking a desktop icon.
    /// </summary>
    public static bool IsLaunchedAppWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        GetWindowThreadProcessId(hwnd, out var pid);
        if ((int)pid == Environment.ProcessId)
            return false; // our fences / UI

        var root = GetAncestor(hwnd, GA_ROOT);
        if (root == IntPtr.Zero) root = hwnd;
        var rootClass = ClassName(root);
        var cls = ClassName(hwnd);

        // Still the desktop shell itself
        if (rootClass is "Progman" or "WorkerW")
            return false;
        if (cls is "Progman" or "WorkerW" or "SHELLDLL_DefView" or "SysListView32")
            return false;

        // Taskbar / shell chrome — not an "opened icon"
        if (rootClass is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return false;

        // Anything else with a visible top-level window = treat as launched/focused app
        if (!IsWindowVisible(hwnd))
            return false;

        return true;
    }

    /// <summary>True only when the point is empty wallpaper (not an icon).</summary>
    public static bool IsEmptyDesktopAt(int screenX, int screenY) =>
        IsDesktopSurfaceAt(screenX, screenY) && IsDesktopIconAt(screenX, screenY) == false;

    /// <summary>
    /// Cross-process LVM_HITTEST via Explorer's own memory. Matches what
    /// Explorer uses to open a file vs treat the click as empty wallpaper.
    /// SendMessageTimeout + abort-if-hung; never from a mouse hook.
    /// </summary>
    private static bool? ListViewHitTestRemote(IntPtr listView, int screenX, int screenY)
    {
        var pt = new POINT { X = screenX, Y = screenY };
        if (!ScreenToClient(listView, ref pt))
            return null;

        GetWindowThreadProcessId(listView, out var pid);
        if (pid == 0)
            return null;

        var hProc = OpenProcess(ProcessVmAccess, false, pid);
        if (hProc == IntPtr.Zero)
            return null;

        var size = Marshal.SizeOf<LVHITTESTINFO>();
        var remote = IntPtr.Zero;
        var local = IntPtr.Zero;
        try
        {
            remote = VirtualAllocEx(hProc, IntPtr.Zero, (UIntPtr)size, MemCommit, PageReadWrite);
            if (remote == IntPtr.Zero)
                return null;

            local = Marshal.AllocHGlobal(size);
            var info = new LVHITTESTINFO { X = pt.X, Y = pt.Y };
            Marshal.StructureToPtr(info, local, false);
            if (!WriteProcessMemory(hProc, remote, local, (UIntPtr)size, out _))
                return null;

            if (SendMessageTimeout(listView, LvmHitTest, IntPtr.Zero, remote,
                    SmtoAbortIfHung | SmtoNormal, 80, out _) == IntPtr.Zero)
                return null;

            if (!ReadProcessMemory(hProc, remote, local, (UIntPtr)size, out _))
                return null;

            info = Marshal.PtrToStructure<LVHITTESTINFO>(local);
            return info.iItem >= 0 && (info.flags & LvhtOnItem) != 0;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (local != IntPtr.Zero)
                Marshal.FreeHGlobal(local);
            if (remote != IntPtr.Zero)
                VirtualFreeEx(hProc, remote, UIntPtr.Zero, MemRelease);
            CloseHandle(hProc);
        }
    }

    private static bool? ListViewItemAt(IntPtr listView, int screenX, int screenY)
    {
        var iid = IidIAccessible;
        var hr = AccessibleObjectFromWindow(listView, OBJID_CLIENT, ref iid, out var obj);
        if (hr != 0 || obj is not IAccessible acc)
            return null;

        try
        {
            object? hit;
            try { hit = acc.accHitTest(screenX, screenY); }
            catch { return null; }

            if (hit is null || hit is DBNull)
                return false;

            if (hit is int id)
                return id != CHILDID_SELF;
            if (hit is short s)
                return s != 0;

            if (hit is IAccessible child)
            {
                try
                {
                    return IsItemRole(RoleOf(child, CHILDID_SELF));
                }
                finally
                {
                    if (!ReferenceEquals(child, acc))
                        Marshal.ReleaseComObject(child);
                }
            }

            try
            {
                var n = Convert.ToInt32(hit, CultureInfo.InvariantCulture);
                return n != CHILDID_SELF;
            }
            catch
            {
                return null;
            }
        }
        finally
        {
            Marshal.ReleaseComObject(acc);
        }
    }

    private static bool? ObjectFromPointIsItem(int x, int y)
    {
        var hr = AccessibleObjectFromPoint(new POINT { X = x, Y = y }, out var acc, out var child);
        if (hr != 0 || acc is null)
            return null;

        try
        {
            var role = RoleOf(acc, child ?? CHILDID_SELF);
            if (IsItemRole(role))
                return true;
            if (role is ROLE_SYSTEM_LIST or ROLE_SYSTEM_WINDOW or ROLE_SYSTEM_PANE
                or ROLE_SYSTEM_CLIENT or ROLE_SYSTEM_DOCUMENT)
                return false;
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(acc);
        }
    }

    private static int RoleOf(IAccessible acc, object child)
    {
        try
        {
            var r = acc.accRole(child);
            if (r is null || r is DBNull)
                return 0;
            return Convert.ToInt32(r, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsItemRole(int role) =>
        role is ROLE_SYSTEM_LISTITEM or ROLE_SYSTEM_OUTLINEITEM or ROLE_SYSTEM_GRAPHIC
            or ROLE_SYSTEM_STATICTEXT or ROLE_SYSTEM_TEXT or ROLE_SYSTEM_PUSHBUTTON;

    private static IntPtr DesktopListViewAt(int screenX, int screenY)
    {
        var hwnd = WindowFromPoint(new POINT { X = screenX, Y = screenY });
        for (var walk = hwnd; walk != IntPtr.Zero; walk = GetParent(walk))
        {
            if (ClassName(walk) == "SysListView32")
                return walk;
        }

        for (var walk = hwnd; walk != IntPtr.Zero; walk = GetParent(walk))
        {
            var c = ClassName(walk);
            if (c is "SHELLDLL_DefView" or "WorkerW" or "Progman")
            {
                var lv = FindListViewDescendant(walk);
                if (lv != IntPtr.Zero)
                    return lv;
            }
        }

        return FindDesktopListView();
    }

    private static IntPtr FindDesktopListView()
    {
        var progman = FindWindow("Progman", "Program Manager");
        if (progman == IntPtr.Zero)
            progman = FindWindow("Progman", null);

        var defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero)
        {
            var worker = IntPtr.Zero;
            for (var i = 0; i < 64 && defView == IntPtr.Zero; i++)
            {
                worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null);
                if (worker == IntPtr.Zero)
                    break;
                defView = FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
            }
        }

        if (defView == IntPtr.Zero)
            return IntPtr.Zero;

        var lv = FindWindowEx(defView, IntPtr.Zero, "SysListView32", "FolderView");
        if (lv != IntPtr.Zero)
            return lv;
        return FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
    }

    private static IntPtr FindListViewDescendant(IntPtr parent)
    {
        var direct = FindWindowEx(parent, IntPtr.Zero, "SysListView32", null);
        if (direct != IntPtr.Zero)
            return direct;

        var child = IntPtr.Zero;
        for (var i = 0; i < 24; i++)
        {
            child = FindWindowEx(parent, child, null, null);
            if (child == IntPtr.Zero)
                break;
            var found = FindListViewDescendant(child);
            if (found != IntPtr.Zero)
                return found;
        }

        return IntPtr.Zero;
    }

    private static string ClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var sb = new StringBuilder(128);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    [ComImport]
    [Guid("618736E0-3C3D-11CF-810C-00AA00389B71")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IAccessible
    {
        [DispId(-5006)]
        object accRole([Optional] object varChild);

        [DispId(-5012)]
        object accSelection { get; }

        [DispId(-5017)]
        object accHitTest(int xLeft, int yTop);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LVHITTESTINFO
    {
        public int X;
        public int Y;
        public uint flags;
        public int iItem;
        public int iSubItem;
        public int iGroup;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT pt);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, uint dwFreeType);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, IntPtr lpBuffer, UIntPtr nSize, out UIntPtr lpNumberOfBytesWritten);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, IntPtr lpBuffer, UIntPtr nSize, out UIntPtr lpNumberOfBytesRead);

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(
        IntPtr hwnd, uint dwId, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppvObject);

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromPoint(
        POINT pt,
        [MarshalAs(UnmanagedType.Interface)] out IAccessible acc,
        [MarshalAs(UnmanagedType.Struct)] out object child);
}
