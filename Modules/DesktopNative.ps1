# Win32 helpers for FenceDesk (icons, z-order, desktop double-click hook)
# Type name versioned so updated C# is recompiled after code changes.

$DesktopNativeCode = @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class FenceDeskNativeV6 {
    public const int SWP_NOSIZE = 0x0001;
    public const int SWP_NOMOVE = 0x0002;
    public const int SWP_NOACTIVATE = 0x0010;
    public const int SWP_SHOWWINDOW = 0x0040;
    public static readonly IntPtr HWND_TOP = new IntPtr(0);
    public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

    public const int WH_MOUSE_LL = 14;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONDBLCLK = 0x0203;
    public const int LVM_HITTEST = 0x1000 + 18;
    public const int LVHT_ONITEMICON = 0x0002;
    public const int LVHT_ONITEMLABEL = 0x0004;
    public const int LVHT_ONITEMSTATEICON = 0x0008;
    public const int LVHT_ONITEM = (LVHT_ONITEMICON | LVHT_ONITEMLABEL | LVHT_ONITEMSTATEICON);

    public const int SHCNE_ASSOCCHANGED = 0x08000000;
    public const uint SHCNF_IDLIST = 0x0000;
    public const uint SHCNF_FLUSH = 0x1000;

    // Set by hook thread; polled by PowerShell UI timer (more reliable than Action from hook)
    public static int DesktopDoubleClickFlag = 0;
    public static string LastHookDebug = "";

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    public const int SW_HIDE = 0;
    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_SHOWNA = 8;

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT pt);

    [DllImport("user32.dll")]
    public static extern IntPtr ChildWindowFromPointEx(IntPtr hwndParent, POINT pt, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, ref LVHITTESTINFO lParam);

    [DllImport("user32.dll")]
    public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHGetFileInfo(string pszPath, uint dwFileAttributes,
        out SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string className, string windowTitle);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LVHITTESTINFO {
        public POINT pt;
        public uint flags;
        public int iItem;
        public int iSubItem;
        public int iGroup;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    public const uint SHGFI_ICON = 0x000000100;
    public const uint SHGFI_LARGEICON = 0x000000000;
    public const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    private static IntPtr _hook = IntPtr.Zero;
    // MUST keep delegate alive for hook lifetime
    private static LowLevelMouseProc _proc;
    private static int _ourPid;
    // WH_MOUSE_LL does NOT receive WM_LBUTTONDBLCLK — only DOWN/UP/MOVE/WHEEL.
    // Reconstruct double-click from two downs within GetDoubleClickTime().
    private static int _lastClickTick;
    private static int _lastClickX;
    private static int _lastClickY;
    private static int _clickCount;

    public static void RefreshDesktop() {
        try {
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST | SHCNF_FLUSH, IntPtr.Zero, IntPtr.Zero);
        } catch { }
    }

    public static bool StartDesktopDoubleClickHook() {
        if (_hook != IntPtr.Zero) return true;
        _ourPid = Process.GetCurrentProcess().Id;
        _proc = new LowLevelMouseProc(HookCallback);
        // For WH_MOUSE_LL, hMod can be the exe module; IntPtr.Zero also works on modern Windows
        IntPtr hMod = GetModuleHandle(null);
        if (hMod == IntPtr.Zero) {
            try {
                hMod = GetModuleHandle(Process.GetCurrentProcess().MainModule.ModuleName);
            } catch { }
        }
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, hMod, 0);
        if (_hook == IntPtr.Zero) {
            int err = Marshal.GetLastWin32Error();
            LastHookDebug = "SetWindowsHookEx failed error=" + err;
            // retry with zero module
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, IntPtr.Zero, 0);
        }
        if (_hook != IntPtr.Zero) {
            LastHookDebug = "hook ok handle=" + _hook.ToString();
        } else {
            LastHookDebug = "hook FAILED lastError=" + Marshal.GetLastWin32Error();
        }
        return _hook != IntPtr.Zero;
    }

    public static void StopDesktopDoubleClickHook() {
        if (_hook != IntPtr.Zero) {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        _proc = null;
    }

    public static bool ConsumeDesktopDoubleClick() {
        return Interlocked.Exchange(ref DesktopDoubleClickFlag, 0) != 0;
    }

    private static string GetWindowClass(IntPtr hwnd) {
        if (hwnd == IntPtr.Zero) return string.Empty;
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static bool IsOurProcessWindow(IntPtr hwnd) {
        try {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            return pid == (uint)_ourPid;
        } catch { return false; }
    }

    private static bool IsShellDesktopClass(string cls) {
        if (string.IsNullOrEmpty(cls)) return false;
        return cls == "Progman"
            || cls == "WorkerW"
            || cls == "SHELLDLL_DefView"
            || cls == "SysListView32"
            // Win11 variants
            || cls == "Windows.UI.Core.CoreWindow"
            || cls.IndexOf("Desktop", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsTaskbarRelated(IntPtr hwnd) {
        IntPtr walk = hwnd;
        for (int i = 0; i < 6 && walk != IntPtr.Zero; i++) {
            string c = GetWindowClass(walk);
            if (c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd" ||
                c == "TrayNotifyWnd" || c == "NotifyIconOverflowWindow" ||
                c == "Windows.UI.Composition.DesktopWindowContentBridge" && GetWindowClass(GetParent(walk)) == "Shell_TrayWnd") {
                return true;
            }
            // Start menu / search flyouts
            if (c == "Windows.UI.Core.CoreWindow") {
                // could be start menu; treat as non-desktop if not under Progman
            }
            walk = GetParent(walk);
        }
        string top = GetWindowClass(GetAncestor(hwnd, 2 /*GA_ROOT*/));
        return top == "Shell_TrayWnd" || top == "Shell_SecondaryTrayWnd";
    }

    private static bool IsExplorerProcess(IntPtr hwnd) {
        try {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            using (var p = Process.GetProcessById((int)pid)) {
                return string.Equals(p.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase);
            }
        } catch { return false; }
    }

    private static bool IsDesktopSurface(IntPtr hwnd, POINT screenPt) {
        if (hwnd == IntPtr.Zero) return false;

        // Never toggle when clicking FenceDesk windows
        if (IsOurProcessWindow(hwnd)) {
            LastHookDebug = "skip our window class=" + GetWindowClass(hwnd);
            return false;
        }

        if (IsTaskbarRelated(hwnd)) {
            LastHookDebug = "skip taskbar";
            return false;
        }

        // Walk up a few parents; desktop is Progman / WorkerW (+ DefView / ListView)
        IntPtr walk = hwnd;
        bool sawDefView = false;
        bool sawListView = false;
        IntPtr listView = IntPtr.Zero;
        string chain = "";
        bool foundDesktopHost = false;

        for (int i = 0; i < 10 && walk != IntPtr.Zero; i++) {
            string c = GetWindowClass(walk);
            if (chain.Length > 0) chain += ">";
            chain += c;

            if (c == "SysListView32") {
                sawListView = true;
                listView = walk;
            }
            if (c == "SHELLDLL_DefView") sawDefView = true;

            if (c == "Progman" || c == "WorkerW") {
                foundDesktopHost = true;
                break;
            }

            walk = GetParent(walk);
        }

        // Fallback: explorer process + GA_ROOT is Progman/WorkerW
        if (!foundDesktopHost) {
            IntPtr root = GetAncestor(hwnd, 2 /*GA_ROOT*/);
            string rootCls = GetWindowClass(root);
            chain += "|root=" + rootCls;
            if (rootCls == "Progman" || rootCls == "WorkerW") {
                foundDesktopHost = true;
            } else if (IsExplorerProcess(hwnd) && (sawDefView || sawListView || rootCls == "Progman" || rootCls == "WorkerW")) {
                foundDesktopHost = true;
            } else if (IsExplorerProcess(hwnd) && !IsTaskbarRelated(hwnd)) {
                // Last-resort Win11: explorer window that is not taskbar — treat empty clicks as desktop
                // Still skip if listview reports an icon under the cursor
                if (sawListView && listView != IntPtr.Zero && IsListViewItemAtPoint(listView, screenPt)) {
                    LastHookDebug = "skip explorer icon " + chain;
                    return false;
                }
                // Only accept if class chain looks shell-desktop-ish
                if (chain.IndexOf("WorkerW", StringComparison.Ordinal) >= 0 ||
                    chain.IndexOf("Progman", StringComparison.Ordinal) >= 0 ||
                    chain.IndexOf("SHELLDLL_DefView", StringComparison.Ordinal) >= 0 ||
                    chain.IndexOf("SysListView32", StringComparison.Ordinal) >= 0) {
                    foundDesktopHost = true;
                }
            }
        }

        if (!foundDesktopHost) {
            LastHookDebug = "not desktop " + chain;
            return false;
        }

        if (sawListView && listView != IntPtr.Zero) {
            if (IsListViewItemAtPoint(listView, screenPt)) {
                LastHookDebug = "skip desktop icon " + chain;
                return false;
            }
        }

        LastHookDebug = "desktop HIT " + chain;
        return true;
    }

    private static bool IsListViewItemAtPoint(IntPtr listView, POINT screenPt) {
        try {
            POINT pt = screenPt;
            if (!ScreenToClient(listView, ref pt)) return false;
            LVHITTESTINFO hit = new LVHITTESTINFO();
            hit.pt = pt;
            hit.iItem = -1;
            SendMessage(listView, LVM_HITTEST, IntPtr.Zero, ref hit);
            if (hit.iItem >= 0) {
                // On item (icon or label)
                if ((hit.flags & LVHT_ONITEM) != 0) return true;
                // Some builds leave flags empty but set index
                if (hit.flags == 0 && hit.iItem >= 0) return true;
                // LVHT_NOWHERE = 1
                if ((hit.flags & 0x0001) != 0) return false;
                return true;
            }
        } catch { }
        return false;
    }

    private static int _lastToggleTick;

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam) {
        // Never swallow messages (return 1) — that caused full-desktop blank flashes.
        // WH_MOUSE_LL never gets WM_LBUTTONDBLCLK; reconstruct from two downs.
        try {
            if (nCode >= 0 && wParam == (IntPtr)WM_LBUTTONDOWN) {
                MSLLHOOKSTRUCT hs = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                int now = Environment.TickCount;
                int dbl = (int)GetDoubleClickTime();
                if (dbl < 200) dbl = 500;
                int slop = GetSystemMetrics(36); // SM_CXDOUBLECLK
                if (slop < 4) slop = 8;

                int dx = Math.Abs(hs.pt.X - _lastClickX);
                int dy = Math.Abs(hs.pt.Y - _lastClickY);
                int dt = now - _lastClickTick;
                if (dt < 0) dt = dbl + 1;

                bool isSecond = (_clickCount > 0 && dt <= dbl && dx <= slop && dy <= slop);
                if (isSecond) {
                    _clickCount = 0;
                    _lastClickTick = 0;
                    int sinceToggle = now - _lastToggleTick;
                    if (sinceToggle < 0 || sinceToggle > 550) {
                        IntPtr hwnd = WindowFromPoint(hs.pt);
                        if (IsDesktopSurface(hwnd, hs.pt)) {
                            _lastToggleTick = now;
                            Interlocked.Exchange(ref DesktopDoubleClickFlag, 1);
                            LastHookDebug = "2down " + LastHookDebug;
                        } else {
                            LastHookDebug = "2down not-desktop " + LastHookDebug;
                        }
                    }
                } else {
                    _clickCount = 1;
                    _lastClickTick = now;
                    _lastClickX = hs.pt.X;
                    _lastClickY = hs.pt.Y;
                }
            }
        } catch (Exception ex) {
            LastHookDebug = "hook ex: " + ex.Message;
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
'@

try {
    if (-not ('FenceDeskNativeV6' -as [type])) {
        Add-Type -TypeDefinition $DesktopNativeCode -ErrorAction Stop
    }
}
catch {
    try {
        Write-FenceLog "FenceDeskNativeV6 compile: $($_.Exception.Message)"
    }
    catch { }
}

# Separate type so we can add Alt+Tab exclusion without recompiling V6 if already loaded
$FenceDeskWinStyleCode = @'
using System;
using System.Runtime.InteropServices;

public static class FenceDeskWinStyle {
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_APPWINDOW = 0x00040000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    public static int GetExStyle(IntPtr hWnd) {
        if (IntPtr.Size == 8) {
            return unchecked((int)GetWindowLongPtr64(hWnd, GWL_EXSTYLE).ToInt64());
        }
        return GetWindowLong32(hWnd, GWL_EXSTYLE);
    }

    public static void SetExStyle(IntPtr hWnd, int exStyle) {
        if (IntPtr.Size == 8) {
            SetWindowLongPtr64(hWnd, GWL_EXSTYLE, new IntPtr(exStyle));
        } else {
            SetWindowLong32(hWnd, GWL_EXSTYLE, exStyle);
        }
        // Force non-client frame refresh so Alt+Tab picks up the new style
        SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    /// <summary>
    /// Hide window from Alt+Tab / Task View (and taskbar when combined with ShowInTaskbar=false).
    /// </summary>
    public static void ExcludeFromAltTab(IntPtr hWnd) {
        if (hWnd == IntPtr.Zero) return;
        int ex = GetExStyle(hWnd);
        ex |= WS_EX_TOOLWINDOW;
        ex &= ~WS_EX_APPWINDOW;
        SetExStyle(hWnd, ex);
    }
}
'@

try {
    if (-not ('FenceDeskWinStyle' -as [type])) {
        Add-Type -TypeDefinition $FenceDeskWinStyleCode -ErrorAction Stop
    }
}
catch {
    try {
        Write-FenceLog "FenceDeskWinStyle compile: $($_.Exception.Message)"
    }
    catch { }
}

function Exclude-WindowFromAltTab {
    param(
        [System.Windows.Window]$Window,
        [IntPtr]$Handle = [IntPtr]::Zero
    )
    try {
        if (-not ('FenceDeskWinStyle' -as [type])) { return }
        $hwnd = $Handle
        if ($hwnd -eq [IntPtr]::Zero -and $null -ne $Window) {
            $helper = New-Object System.Windows.Interop.WindowInteropHelper($Window)
            $hwnd = $helper.Handle
            if ($hwnd -eq [IntPtr]::Zero) {
                try { $null = $helper.EnsureHandle() } catch { }
                $hwnd = $helper.Handle
            }
        }
        if ($hwnd -eq [IntPtr]::Zero) { return }
        # WPF ShowInTaskbar=false alone is not enough for Alt+Tab on modern Windows
        try {
            if ($null -ne $Window) { $Window.ShowInTaskbar = $false }
        }
        catch { }
        [FenceDeskWinStyle]::ExcludeFromAltTab($hwnd)
    }
    catch {
        try { Write-FenceLog "Exclude-WindowFromAltTab: $($_.Exception.Message)" } catch { }
    }
}

function Register-WindowExcludeFromAltTab {
    param([System.Windows.Window]$Window)
    if ($null -eq $Window) { return }
    try {
        $Window.ShowInTaskbar = $false
    }
    catch { }

    # Apply as soon as HWND exists
    $Window.Add_SourceInitialized({
        param($s, $e)
        try { Exclude-WindowFromAltTab -Window $s } catch { }
    })

    # Re-apply after Shown — WPF can reset styles around first Show()
    $Window.Add_Loaded({
        param($s, $e)
        try { Exclude-WindowFromAltTab -Window $s } catch { }
    })
}

# Win+D + glass opacity together:
# - Glass opacity needs AllowsTransparency (layered WPF).
# - SetParent(desktop) + layered = invisible.
# - HWND_TOPMOST survives Win+D, but must not stay on while games are visible.
# - NOTOPMOST alone is NOT enough: it leaves the window at the top of the normal band
#   (still over games). Demote with HWND_BOTTOM when apps/games should cover fences.
# - Also: never topmost if ANY visible fullscreen app exists (FG can stay WorkerW).
$FenceDeskDesktopPinCode = @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class FenceDeskDesktopPinV11 {
    public const int GWL_EXSTYLE = -20;
    public const int GWL_STYLE = -16;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_APPWINDOW = 0x00040000;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_CHILD = 0x40000000;
    public const int WS_DISABLED = 0x08000000;

    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_RESTORE = 9;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOOWNERZORDER = 0x0200;

    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint GA_ROOT = 2;

    public static readonly IntPtr HWND_TOP = new IntPtr(0);
    public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

    public static string LastDebug = "";
    public static string LastZOrderDebug = "";

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct MONITORINFO {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private const uint GW_OWNER = 4;

    private static int GetStyle(IntPtr hWnd) {
        if (IntPtr.Size == 8) return unchecked((int)GetWindowLongPtr64(hWnd, GWL_STYLE).ToInt64());
        return GetWindowLong32(hWnd, GWL_STYLE);
    }

    private static int GetExStyle(IntPtr hWnd) {
        if (IntPtr.Size == 8) return unchecked((int)GetWindowLongPtr64(hWnd, GWL_EXSTYLE).ToInt64());
        return GetWindowLong32(hWnd, GWL_EXSTYLE);
    }

    private static void SetExStyle(IntPtr hWnd, int ex) {
        if (IntPtr.Size == 8) SetWindowLongPtr64(hWnd, GWL_EXSTYLE, new IntPtr(ex));
        else SetWindowLong32(hWnd, GWL_EXSTYLE, ex);
    }

    private static string ClassName(IntPtr hWnd) {
        var sb = new StringBuilder(256);
        GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static void EnsureToolWindowStyles(IntPtr hwnd) {
        if (hwnd == IntPtr.Zero) return;
        int ex = GetExStyle(hwnd);
        int next = ex | WS_EX_TOOLWINDOW;
        next &= ~WS_EX_APPWINDOW;
        if (next != ex) {
            SetExStyle(hwnd, next);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
    }

    public static void EnsureTopLevel(IntPtr hwnd) {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
        IntPtr parent = GetParent(hwnd);
        if (parent == IntPtr.Zero) return;
        string cls = ClassName(parent);
        if (cls == "Progman" || cls == "WorkerW") {
            SetParent(hwnd, IntPtr.Zero);
        }
    }

    public static bool NeedsShowDesktopRepair(IntPtr hwnd) {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        return IsIconic(hwnd) || !IsWindowVisible(hwnd);
    }

    private static bool IsShellOrDesktopClass(string cls) {
        if (string.IsNullOrEmpty(cls)) return false;
        return cls == "Progman"
            || cls == "WorkerW"
            || cls == "SHELLDLL_DefView"
            || cls == "Shell_TrayWnd"
            || cls == "Shell_SecondaryTrayWnd"
            || cls == "NotifyIconOverflowWindow"
            || cls == "DV2ControlHost"
            || cls == "ForegroundStaging"
            || cls == "MultitaskingViewFrame"
            || cls == "XamlExplorerHostIslandWindow"
            || cls == "Windows.UI.Core.CoreWindow"
            || cls == "ImmersiveLauncher"
            || cls == "SearchPane"
            || cls == "Windows.Internal.Shell.TabProxyWindow"
            || cls == "NativeHWNDHost"
            || cls == "ApplicationManager_ImmersiveShellWindow";
    }

    private static bool IsKnownGameClass(string cls) {
        if (string.IsNullOrEmpty(cls)) return false;
        // Common engine / overlay hosts (fullscreen detection may miss some modes)
        if (cls == "UnrealWindow") return true;
        if (cls == "UnityWndClass") return true;
        if (cls == "Chrome_WidgetWin_1") return false; // not a game by class alone
        if (cls.IndexOf("RenderWindow", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (cls.IndexOf("Valve001", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (cls == "SDL_app" || cls == "SDL_Window") return true;
        if (cls.StartsWith("CryENGINE", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsFullscreenLike(IntPtr hwnd) {
        try {
            RECT wr;
            if (!GetWindowRect(hwnd, out wr)) return false;
            int ww = wr.Right - wr.Left;
            int wh = wr.Bottom - wr.Top;
            if (ww < 200 || wh < 200) return false;

            IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero) return false;
            MONITORINFO mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (!GetMonitorInfo(mon, ref mi)) return false;

            int mw = mi.rcMonitor.Right - mi.rcMonitor.Left;
            int mh = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
            if (mw < 1 || mh < 1) return false;

            // Borderless / exclusive-ish: covers nearly the whole monitor
            int slop = 16;
            bool covers =
                wr.Left <= mi.rcMonitor.Left + slop &&
                wr.Top <= mi.rcMonitor.Top + slop &&
                wr.Right >= mi.rcMonitor.Right - slop &&
                wr.Bottom >= mi.rcMonitor.Bottom - slop;
            if (covers) return true;

            // Large window covering most of the monitor (borderless windowed with slight inset)
            double areaRatio = ((double)ww * wh) / ((double)mw * mh);
            if (areaRatio >= 0.90 && ww >= mw - 40 && wh >= mh - 80) return true;

            // Maximized covering work area
            if (IsZoomed(hwnd)) {
                int workW = mi.rcWork.Right - mi.rcWork.Left;
                int workH = mi.rcWork.Bottom - mi.rcWork.Top;
                if (ww >= workW - slop && wh >= workH - slop) return true;
            }
            return false;
        } catch { return false; }
    }

    private static bool IsCandidateAppWindow(IntPtr hwnd, int ourPid) {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;

        // Owned popups / tooltips — skip
        if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return false;

        int style = GetStyle(hwnd);
        if ((style & WS_CHILD) != 0) return false;
        if ((style & WS_DISABLED) != 0) return false;

        uint pid = 0;
        GetWindowThreadProcessId(hwnd, out pid);
        if ((int)pid == ourPid) return false;

        string cls = ClassName(hwnd);
        if (IsShellOrDesktopClass(cls)) return false;

        // Tiny windows aren't games/apps we care about
        RECT wr;
        if (!GetWindowRect(hwnd, out wr)) return false;
        if ((wr.Right - wr.Left) < 100 || (wr.Bottom - wr.Top) < 100) return false;

        return true;
    }

    private static int _scanOurPid;
    private static bool _scanFoundFullscreen;
    private static string _scanFoundClass;

    private static bool EnumFullscreenCallback(IntPtr hWnd, IntPtr lParam) {
        try {
            if (!IsCandidateAppWindow(hWnd, _scanOurPid)) return true;
            string cls = ClassName(hWnd);
            if (IsFullscreenLike(hWnd) || IsKnownGameClass(cls)) {
                // Known game classes only force demote when reasonably large
                if (IsKnownGameClass(cls) && !IsFullscreenLike(hWnd)) {
                    RECT wr;
                    if (GetWindowRect(hWnd, out wr)) {
                        int ww = wr.Right - wr.Left;
                        int wh = wr.Bottom - wr.Top;
                        if (ww < 640 || wh < 480) return true;
                    }
                }
                _scanFoundFullscreen = true;
                _scanFoundClass = cls;
                return false; // stop
            }
        } catch { }
        return true;
    }

    /// <summary>
    /// True if any visible non-minimized fullscreen/game window exists (any monitor).
    /// FG alone is unreliable — games often leave WorkerW as foreground.
    /// </summary>
    public static bool HasVisibleFullscreenApp(int ourPid) {
        _scanOurPid = ourPid;
        _scanFoundFullscreen = false;
        _scanFoundClass = "";
        try {
            EnumWindows(EnumFullscreenCallback, IntPtr.Zero);
        } catch { }
        return _scanFoundFullscreen;
    }

    /// <summary>
    /// Topmost only when desktop is effectively showing AND no fullscreen/game is visible.
    /// </summary>
    public static bool ShouldUseTopmost(int ourPid) {
        try {
            // Hard rule: any visible fullscreen/game → never topmost (covers Unreal etc.)
            if (HasVisibleFullscreenApp(ourPid)) {
                LastZOrderDebug = "notopmost: fullscreen-present " + _scanFoundClass;
                return false;
            }

            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) {
                LastZOrderDebug = "topmost: no-fg";
                return true;
            }

            // Walk to root in case FG is a child
            IntPtr root = GetAncestor(fg, GA_ROOT);
            if (root == IntPtr.Zero) root = fg;

            uint pid = 0;
            GetWindowThreadProcessId(root, out pid);
            if ((int)pid == ourPid) {
                LastZOrderDebug = "topmost: our-process";
                return true;
            }

            string cls = ClassName(root);
            if (IsShellOrDesktopClass(cls)) {
                LastZOrderDebug = "topmost: shell " + cls;
                return true;
            }

            if (!IsWindowVisible(root) || IsIconic(root)) {
                LastZOrderDebug = "topmost: fg-hidden " + cls;
                return true;
            }

            // Any real focused app (windowed browser, etc.) — stay under it
            LastZOrderDebug = "notopmost: app-focus " + cls;
            return false;
        } catch (Exception ex) {
            LastZOrderDebug = "topmost: ex " + ex.Message;
            return true;
        }
    }

    /// <summary>
    /// Restore after Win+D and apply smart z-order.
    /// useTopmost=true  → HWND_TOPMOST (Win+D / pure desktop)
    /// useTopmost=false → clear topmost AND HWND_BOTTOM so games/apps cover us
    /// </summary>
    public static bool PinForShowDesktop(IntPtr hwnd, bool useTopmost) {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) {
            LastDebug = "pin: bad hwnd";
            return false;
        }

        EnsureTopLevel(hwnd);
        EnsureToolWindowStyles(hwnd);

        if (IsIconic(hwnd)) {
            ShowWindow(hwnd, SW_RESTORE);
        }
        if (!IsWindowVisible(hwnd)) {
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        }

        uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER;

        if (useTopmost) {
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, flags | SWP_SHOWWINDOW);
            LastDebug = "pin: topmost vis=" + IsWindowVisible(hwnd) + " iconic=" + IsIconic(hwnd)
                + " | " + LastZOrderDebug;
        } else {
            // Critical: NOTOPMOST alone leaves us at the top of the normal z-band (still over games).
            // Drop out of topmost band, then send to bottom so apps/games paint above.
            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, flags);
            SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, flags);
            LastDebug = "pin: bottom vis=" + IsWindowVisible(hwnd) + " iconic=" + IsIconic(hwnd)
                + " | " + LastZOrderDebug;
        }

        return IsWindowVisible(hwnd) && !IsIconic(hwnd);
    }
}
'@

try {
    if (-not ('FenceDeskDesktopPinV11' -as [type])) {
        Add-Type -TypeDefinition $FenceDeskDesktopPinCode -ErrorAction Stop
    }
}
catch {
    try { Write-FenceLog "FenceDeskDesktopPinV11 compile: $($_.Exception.Message)" } catch { }
}

function Get-WindowHwnd {
    param([System.Windows.Window]$Window)
    if ($null -eq $Window) { return [IntPtr]::Zero }
    try {
        $helper = New-Object System.Windows.Interop.WindowInteropHelper($Window)
        $hwnd = $helper.Handle
        if ($hwnd -eq [IntPtr]::Zero) {
            try { $null = $helper.EnsureHandle() } catch { }
            $hwnd = $helper.Handle
        }
        return $hwnd
    }
    catch { return [IntPtr]::Zero }
}

function Get-FenceWantTopmost {
    # Topmost only on pure desktop / Win+D; never while a fullscreen game is visible
    try {
        if (-not ('FenceDeskDesktopPinV11' -as [type])) { return $true }
        return [bool][FenceDeskDesktopPinV11]::ShouldUseTopmost([int]$PID)
    }
    catch { return $true }
}

function Pin-FenceWindowToDesktop {
    param(
        [System.Windows.Window]$Window,
        [switch]$Force,
        [switch]$Raise,
        [object]$UseTopmost = $null
    )
    if ($null -eq $Window) { return $false }
    try {
        if (-not ('FenceDeskDesktopPinV11' -as [type])) { return $false }
        $hwnd = Get-WindowHwnd -Window $Window
        if ($hwnd -eq [IntPtr]::Zero) { return $false }

        # Always re-evaluate unless caller forces a bool (never trust stale topmost during games)
        $wantTop = if ($null -ne $UseTopmost) { [bool]$UseTopmost } else { Get-FenceWantTopmost }

        try {
            if ($Window.WindowState -eq [System.Windows.WindowState]::Minimized) {
                $Window.WindowState = [System.Windows.WindowState]::Normal
            }
            if (-not $Window.IsVisible) { $Window.Show() }
            $Window.Visibility = [System.Windows.Visibility]::Visible
            $Window.Opacity = 1.0
            $Window.Topmost = $wantTop
        }
        catch { }

        try { Exclude-WindowFromAltTab -Window $Window -Handle $hwnd } catch { }
        $ok = [FenceDeskDesktopPinV11]::PinForShowDesktop($hwnd, $wantTop)
        return [bool]$ok
    }
    catch {
        try { Write-FenceLog "Pin-FenceWindowToDesktop: $($_.Exception.Message)" } catch { }
        return $false
    }
}

function Register-FenceDesktopPin {
    param([System.Windows.Window]$Window)
    if ($null -eq $Window) { return }
    try { $Window.Topmost = (Get-FenceWantTopmost) } catch { $Window.Topmost = $true }
    $Window.Add_StateChanged({
        param($s, $e)
        try {
            if ($null -eq $s) { return }
            # Win+D minimizes windows — restore visibility; z-order follows smart rules
            # (do NOT force topmost — that re-covers games if one is still visible)
            if ($s.WindowState -eq [System.Windows.WindowState]::Minimized) {
                $fid = [string]$s.Tag
                if ($fid -and $script:FenceWindows -and $script:FenceWindows.ContainsKey($fid)) {
                    if ($script:FenceWindows[$fid].ToggleHidden) { return }
                }
                if ($script:Layout -and $script:Layout.settings -and $script:Layout.settings.showFences -eq $false) { return }
                $s.WindowState = [System.Windows.WindowState]::Normal
                Pin-FenceWindowToDesktop -Window $s | Out-Null
            }
        }
        catch { }
    })
}

function Restore-FenceWindowsAfterShowDesktop {
    # Keep fences visible after Win+D (topmost on desktop) and under focused apps/games
    if ($script:FenceDeskExiting) { return }
    if (-not $script:FenceWindows) { return }

    $userHidden = $false
    try {
        if ($script:Layout -and $script:Layout.settings -and $script:Layout.settings.showFences -eq $false) {
            $userHidden = $true
        }
    }
    catch { }
    if ($userHidden) {
        try { Hide-FenceDeskControlPanelIfIdle } catch { }
        return
    }

    $wantTop = Get-FenceWantTopmost

    foreach ($id in @($script:FenceWindows.Keys)) {
        try {
            $entry = $script:FenceWindows[$id]
            if ($null -eq $entry -or $entry.ToggleHidden) { continue }
            $w = $entry.Window
            if ($null -eq $w) { continue }

            $needs = $false
            if ($w.WindowState -eq [System.Windows.WindowState]::Minimized) { $needs = $true }
            elseif (-not $w.IsVisible) { $needs = $true }
            else {
                $hwnd = Get-WindowHwnd -Window $w
                if ($hwnd -ne [IntPtr]::Zero -and ('FenceDeskDesktopPinV11' -as [type])) {
                    if ([FenceDeskDesktopPinV11]::NeedsShowDesktopRepair($hwnd)) { $needs = $true }
                }
            }
            # Sync topmost with desktop-vs-app focus (every tick only when wrong)
            if ([bool]$w.Topmost -ne [bool]$wantTop) { $needs = $true }
            if (-not $needs) { continue }

            Pin-FenceWindowToDesktop -Window $w -UseTopmost $wantTop | Out-Null
        }
        catch { }
    }

    try { Hide-FenceDeskControlPanelIfIdle } catch { }
}

function Start-FenceShowDesktopGuard {
    try {
        if ($null -ne $script:FenceShowDesktopTimer) { return }
        $script:FenceShowDesktopTimer = New-Object System.Windows.Threading.DispatcherTimer
        # A bit snappier so Win+D restore and app-focus demote feel immediate
        $script:FenceShowDesktopTimer.Interval = [TimeSpan]::FromMilliseconds(250)
        $script:FenceShowDesktopTimer.Add_Tick({
            try { Restore-FenceWindowsAfterShowDesktop } catch { }
        })
        $script:FenceShowDesktopTimer.Start()
        Write-FenceLog 'Fence Win+D guard started (smart z-order: topmost on desktop, bottom under games)'
    }
    catch {
        try { Write-FenceLog "Start-FenceShowDesktopGuard: $($_.Exception.Message)" } catch { }
    }
}

function Stop-FenceShowDesktopGuard {
    try {
        if ($script:FenceShowDesktopTimer) {
            $script:FenceShowDesktopTimer.Stop()
            $script:FenceShowDesktopTimer = $null
        }
    }
    catch { }
}

function Hide-FenceDeskControlPanelIfIdle {
    if ($script:FenceDeskExiting) { return }
    if ($script:TaskbarHostUserOpen) { return }
    $w = $script:TaskbarHost
    if ($null -eq $w) { return }
    try {
        if ($w.Visibility -ne [System.Windows.Visibility]::Visible) { return }
        # User did not request it — hide without activating anything
        $w.ShowInTaskbar = $false
        $w.WindowState = [System.Windows.WindowState]::Normal
        $w.Hide()
    }
    catch {
        try {
            $w.Visibility = [System.Windows.Visibility]::Hidden
        }
        catch { }
    }
}

function Show-FenceDeskControlPanel {
    $w = $script:TaskbarHost
    if ($null -eq $w) { return }
    try {
        $script:TaskbarHostUserOpen = $true
        $w.ShowInTaskbar = $false
        $w.WindowState = [System.Windows.WindowState]::Normal
        $w.Show()
        $w.Visibility = [System.Windows.Visibility]::Visible
        $w.Activate()
    }
    catch {
        try { Write-FenceLog "Show-FenceDeskControlPanel: $($_.Exception.Message)" } catch { }
    }
}

function Request-HideFenceDeskControlPanel {
    $script:TaskbarHostUserOpen = $false
    try { Hide-FenceDeskControlPanelIfIdle } catch { }
}

function Set-FenceWindowZOrder {
    param(
        [System.Windows.Window]$Window,
        [ValidateSet('Top', 'Bottom', 'TopMost', 'NoTopMost')]
        [string]$Position = 'Top'
    )
    try {
        if (-not ('FenceDeskNativeV6' -as [type])) { return }
        $helper = New-Object System.Windows.Interop.WindowInteropHelper($Window)
        $hwnd = $helper.Handle
        if ($hwnd -eq [IntPtr]::Zero) { return }
        $after = switch ($Position) {
            'Top'       { [FenceDeskNativeV6]::HWND_TOP }
            'Bottom'    { [FenceDeskNativeV6]::HWND_BOTTOM }
            'TopMost'   { [FenceDeskNativeV6]::HWND_TOPMOST }
            'NoTopMost' { [FenceDeskNativeV6]::HWND_NOTOPMOST }
        }
        $flags = [uint32]([FenceDeskNativeV6]::SWP_NOMOVE -bor [FenceDeskNativeV6]::SWP_NOSIZE -bor [FenceDeskNativeV6]::SWP_NOACTIVATE)
        [void][FenceDeskNativeV6]::SetWindowPos($hwnd, $after, 0, 0, 0, 0, $flags)
    }
    catch { }
}

function Get-IconFromFileIndex {
    param(
        [string]$File,
        [int]$Index = 0,
        [int]$Size = 32
    )
    try {
        if ([string]::IsNullOrWhiteSpace($File) -or -not (Test-Path -LiteralPath $File)) { return $null }
        if ($Index -ne 0 -or $File -match '\.dll$') {
            $img = Get-IconFromDllIndex -Dll $File -Index $Index -Size $Size
            if ($null -ne $img) { return $img }
        }
        $ico = [System.Drawing.Icon]::ExtractAssociatedIcon($File)
        if ($null -ne $ico) {
            return Convert-DrawingIconToImageSource $ico $Size
        }
    }
    catch { }
    return $null
}

function Get-ShortcutIconImage {
    param(
        [string]$LnkPath,
        [int]$Size = 32
    )
    try {
        if (-not (Test-Path -LiteralPath $LnkPath)) { return $null }
        $sh = New-Object -ComObject WScript.Shell
        $sc = $sh.CreateShortcut($LnkPath)

        # 1) Explicit icon location on the shortcut
        $iconLoc = [string]$sc.IconLocation
        if (-not [string]::IsNullOrWhiteSpace($iconLoc)) {
            $parts = $iconLoc -split ','
            $iconFile = $parts[0].Trim().Trim('"')
            $iconIdx = 0
            if ($parts.Count -gt 1) { [void][int]::TryParse($parts[1].Trim(), [ref]$iconIdx) }
            if ($iconFile -and (Test-Path -LiteralPath $iconFile)) {
                $img = Get-IconFromFileIndex -File $iconFile -Index $iconIdx -Size $Size
                if ($null -ne $img) { return $img }
            }
        }

        # 2) Target executable / file
        $target = [string]$sc.TargetPath
        if (-not [string]::IsNullOrWhiteSpace($target) -and (Test-Path -LiteralPath $target)) {
            $img = Get-IconFromFileIndex -File $target -Index 0 -Size $Size
            if ($null -ne $img) { return $img }
            # SHGetFileInfo on target
            $img = Get-ShellFileIconCore -Path $target -Size $Size
            if ($null -ne $img) { return $img }
        }

        # 3) The .lnk itself via ExtractAssociatedIcon
        $ico = [System.Drawing.Icon]::ExtractAssociatedIcon($LnkPath)
        if ($null -ne $ico) {
            return Convert-DrawingIconToImageSource $ico $Size
        }
    }
    catch {
        Write-FenceLog "Shortcut icon failed ($LnkPath): $($_.Exception.Message)"
    }
    return $null
}

function Get-ShellFileIconCore {
    param(
        [string]$Path,
        [int]$Size = 32
    )
    if (-not ('FenceDeskNativeV6' -as [type])) { return $null }
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }

    $flags = [FenceDeskNativeV6]::SHGFI_ICON -bor [FenceDeskNativeV6]::SHGFI_LARGEICON
    $fi = New-Object FenceDeskNativeV6+SHFILEINFO
    $attr = [FenceDeskNativeV6]::FILE_ATTRIBUTE_NORMAL
    $usePath = $Path

    $exists = $false
    try { $exists = Test-Path -LiteralPath $Path } catch { }

    if ($exists) {
        $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
        if ($item -and $item.PSIsContainer) {
            $attr = [FenceDeskNativeV6]::FILE_ATTRIBUTE_DIRECTORY
        }
    }
    elseif ($Path -match '^::\{' -or $Path -match '^shell:') {
        # Shell namespace — do NOT use USEFILEATTRIBUTES (that yields a blank generic icon)
        $usePath = $Path
    }
    else {
        return $null
    }

    $r = [FenceDeskNativeV6]::SHGetFileInfo($usePath, $attr, [ref]$fi, [uint32][System.Runtime.InteropServices.Marshal]::SizeOf($fi), [uint32]$flags)
    if ($r -eq 0 -or $fi.hIcon -eq [IntPtr]::Zero) {
        return $null
    }
    try {
        $ico = [System.Drawing.Icon]::FromHandle($fi.hIcon).Clone()
        [void][FenceDeskNativeV6]::DestroyIcon($fi.hIcon)
        $fi.hIcon = [IntPtr]::Zero
        return Convert-DrawingIconToImageSource $ico $Size
    }
    finally {
        if ($fi.hIcon -ne [IntPtr]::Zero) {
            try { [void][FenceDeskNativeV6]::DestroyIcon($fi.hIcon) } catch { }
        }
    }
}

function Get-IconFromDllIndex {
    param(
        [string]$Dll,
        [int]$Index = 0,
        [int]$Size = 32
    )
    try {
        if (-not (Test-Path -LiteralPath $Dll)) { return $null }
        $code = @'
using System;
using System.Runtime.InteropServices;
public static class FenceDeskExtractIcon {
  [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
  public static extern uint ExtractIconEx(string lpszFile, int nIconIndex,
    IntPtr[] phiconLarge, IntPtr[] phiconSmall, uint nIcons);
  [DllImport("user32.dll", SetLastError = true)]
  public static extern bool DestroyIcon(IntPtr hIcon);
}
'@
        if (-not ('FenceDeskExtractIcon' -as [type])) {
            Add-Type -TypeDefinition $code -ErrorAction Stop
        }
        $large = New-Object IntPtr[] 1
        $small = New-Object IntPtr[] 1
        $n = [FenceDeskExtractIcon]::ExtractIconEx($Dll, $Index, $large, $small, 1)
        $h = [IntPtr]::Zero
        if ($Size -le 16 -and $small[0] -ne [IntPtr]::Zero) {
            $h = $small[0]
            if ($large[0] -ne [IntPtr]::Zero -and $large[0] -ne $h) {
                [void][FenceDeskExtractIcon]::DestroyIcon($large[0])
            }
        }
        else {
            $h = $large[0]
            if ($small[0] -ne [IntPtr]::Zero -and $small[0] -ne $h) {
                [void][FenceDeskExtractIcon]::DestroyIcon($small[0])
            }
        }
        if ($n -gt 0 -and $h -ne [IntPtr]::Zero) {
            try {
                $ico = [System.Drawing.Icon]::FromHandle($h).Clone()
                [void][FenceDeskExtractIcon]::DestroyIcon($h)
                return Convert-DrawingIconToImageSource $ico $Size
            }
            catch {
                try { [void][FenceDeskExtractIcon]::DestroyIcon($h) } catch { }
            }
        }
    }
    catch {
        Write-FenceLog "ExtractIconEx failed ($Dll,$Index): $($_.Exception.Message)"
    }
    return $null
}

function Get-RecycleBinIconImage {
    param([int]$Size = 32)
    $img = Get-ShellFileIconCore -Path 'shell:RecycleBinFolder' -Size $Size
    if ($null -ne $img) { return $img }
    $img = Get-ShellFileIconCore -Path '::{645FF040-5081-101B-9F08-00AA002F954E}' -Size $Size
    if ($null -ne $img) { return $img }
    # imageres.dll: empty bin ~50, full ~49 (varies by Windows build)
    $imageres = Join-Path $env:SystemRoot 'System32\imageres.dll'
    foreach ($idx in @(50, 49, 51, 32)) {
        $img = Get-IconFromDllIndex -Dll $imageres -Index $idx -Size $Size
        if ($null -ne $img) { return $img }
    }
    $shell32 = Join-Path $env:SystemRoot 'System32\shell32.dll'
    foreach ($idx in @(31, 32, 0)) {
        $img = Get-IconFromDllIndex -Dll $shell32 -Index $idx -Size $Size
        if ($null -ne $img) { return $img }
    }
    return $null
}

function Get-ShellFileIcon {
    param(
        [string]$Path,
        [int]$Size = 32
    )
    try {
        if ([string]::IsNullOrWhiteSpace($Path)) { return $null }

        # Shell namespace paths (Recycle Bin, etc.)
        if ($Path -match '^::\{' -or $Path -match '^shell:' -or $Path -match '645FF040') {
            $img = Get-RecycleBinIconImage -Size $Size
            if ($null -ne $img) { return $img }
            return Get-ShellFileIconCore -Path $Path -Size $Size
        }

        # Resolve shelved / moved shortcuts (Public Desktop Edge/Brave/Steam)
        $resolved = $Path
        if (Get-Command Resolve-FenceItemPath -ErrorAction SilentlyContinue) {
            try { $resolved = Resolve-FenceItemPath -Path $Path } catch { $resolved = $Path }
        }

        # Shortcuts: resolve icon from target / IconLocation
        if ($resolved -match '\.lnk$' -or $Path -match '\.lnk$') {
            foreach ($tryPath in @($resolved, $Path) | Select-Object -Unique) {
                if (-not $tryPath) { continue }
                $img = Get-ShortcutIconImage -LnkPath $tryPath -Size $Size
                if ($null -ne $img) { return $img }
            }
        }

        # Existing file / folder
        foreach ($tryPath in @($resolved, $Path) | Select-Object -Unique) {
            if (-not $tryPath) { continue }
            if (Test-Path -LiteralPath $tryPath) {
                $img = Get-ShellFileIconCore -Path $tryPath -Size $Size
                if ($null -ne $img) { return $img }
                try {
                    $ico = [System.Drawing.Icon]::ExtractAssociatedIcon($tryPath)
                    if ($null -ne $ico) {
                        return Convert-DrawingIconToImageSource $ico $Size
                    }
                }
                catch { }
            }
        }

        # Known apps by name (when .lnk is gone / unreadable)
        if (Get-Command Get-KnownAppExePath -ErrorAction SilentlyContinue) {
            $exe = Get-KnownAppExePath -NameOrPath $Path
            if (-not $exe) { $exe = Get-KnownAppExePath -NameOrPath $resolved }
            if ($exe) {
                $img = Get-IconFromFileIndex -File $exe -Index 0 -Size $Size
                if ($null -ne $img) { return $img }
                $img = Get-ShellFileIconCore -Path $exe -Size $Size
                if ($null -ne $img) { return $img }
            }
        }

        return $null
    }
    catch {
        return $null
    }
}

function Convert-DrawingIconToImageSource {
    param(
        [System.Drawing.Icon]$Icon,
        [int]$Size = 32
    )
    if ($null -eq $Icon) { return $null }
    $bmp = $null
    try {
        $bmp = $Icon.ToBitmap()
        if ($Size -ne $bmp.Width) {
            $resized = New-Object System.Drawing.Bitmap $Size, $Size
            $g = [System.Drawing.Graphics]::FromImage($resized)
            try {
                $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $g.DrawImage($bmp, 0, 0, $Size, $Size)
            }
            finally { $g.Dispose() }
            $bmp.Dispose()
            $bmp = $resized
        }
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $ms.Position = 0
        $bi = New-Object System.Windows.Media.Imaging.BitmapImage
        $bi.BeginInit()
        $bi.CacheOption = [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
        $bi.StreamSource = $ms
        $bi.EndInit()
        $bi.Freeze()
        $ms.Dispose()
        return $bi
    }
    catch {
        return $null
    }
    finally {
        if ($null -ne $bmp) { try { $bmp.Dispose() } catch { } }
        if ($null -ne $Icon) { try { $Icon.Dispose() } catch { } }
    }
}

function Get-DefaultFileImageSource {
    param([int]$Size = 32)
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $bg = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(90, 120, 180))
    $bg.Freeze()
    $pen = New-Object System.Windows.Media.Pen ([System.Windows.Media.Brushes]::White, 1.5)
    $rect = New-Object System.Windows.Rect 4, 2, ($Size - 10), ($Size - 6)
    $dc.DrawRectangle($bg, $pen, $rect)
    $dc.Close()
    $rb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $Size, $Size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $rb.Render($dv)
    $rb.Freeze()
    return $rb
}

function Start-DesktopDoubleClickWatch {
    # Disabled: desktop double-click hide/show was unreliable (false positives on
    # Explorer windows). Use taskbar / tray Show fences and Hide fences instead.
    try {
        if ($script:Layout -and $script:Layout.settings) {
            $script:Layout.settings.doubleClickDesktopHide = $false
        }
        Write-FenceLog 'Desktop double-click hide/show is disabled (use taskbar/tray Show/Hide)'
    }
    catch { }
}

function Stop-DesktopDoubleClickWatch {
    try {
        if ($script:DesktopDblClickTimer) {
            $script:DesktopDblClickTimer.Stop()
            $script:DesktopDblClickTimer = $null
        }
        if ('FenceDeskNativeV6' -as [type]) {
            [FenceDeskNativeV6]::StopDesktopDoubleClickHook()
        }
    }
    catch { }
}
