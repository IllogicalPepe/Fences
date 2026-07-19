using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FenceDesk.Native;

/// <summary>
/// Smart z-order: topmost on pure desktop / Win+D; bottom under apps and games.
/// Ported from FenceDeskDesktopPinV11.
/// </summary>
internal static class DesktopPin
{
    public static string LastDebug { get; private set; } = "";
    public static string LastZOrderDebug { get; private set; } = "";

    private static int _scanOurPid;
    private static bool _scanFoundFullscreen;
    private static string _scanFoundClass = "";

    public static void EnsureToolWindowStyles(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        var next = ex | NativeMethods.WS_EX_TOOLWINDOW;
        next &= ~NativeMethods.WS_EX_APPWINDOW;
        if (next != ex)
        {
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, next);
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER |
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
        }
    }

    public static void EnsureTopLevel(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return;
        var parent = NativeMethods.GetParent(hwnd);
        if (parent == IntPtr.Zero) return;
        var cls = NativeMethods.GetClassName(parent);
        if (cls is "Progman" or "WorkerW")
            NativeMethods.SetParent(hwnd, IntPtr.Zero);
    }

    public static bool NeedsShowDesktopRepair(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return false;
        return NativeMethods.IsIconic(hwnd) || !NativeMethods.IsWindowVisible(hwnd);
    }

    public static bool ShouldUseTopmost(int ourPid)
    {
        try
        {
            if (HasVisibleFullscreenApp(ourPid))
            {
                LastZOrderDebug = "notopmost: fullscreen-present " + _scanFoundClass;
                return false;
            }

            var fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero)
            {
                LastZOrderDebug = "topmost: no-fg";
                return true;
            }

            var root = NativeMethods.GetAncestor(fg, NativeMethods.GA_ROOT);
            if (root == IntPtr.Zero) root = fg;

            NativeMethods.GetWindowThreadProcessId(root, out var pid);
            if ((int)pid == ourPid)
            {
                LastZOrderDebug = "topmost: our-process";
                return true;
            }

            var cls = NativeMethods.GetClassName(root);
            if (IsShellOrDesktopClass(cls))
            {
                LastZOrderDebug = "topmost: shell " + cls;
                return true;
            }

            if (!NativeMethods.IsWindowVisible(root) || NativeMethods.IsIconic(root))
            {
                LastZOrderDebug = "topmost: fg-hidden " + cls;
                return true;
            }

            LastZOrderDebug = "notopmost: app-focus " + cls;
            return false;
        }
        catch (Exception ex)
        {
            LastZOrderDebug = "topmost: ex " + ex.Message;
            return true;
        }
    }

    public static bool PinForShowDesktop(IntPtr hwnd, bool useTopmost)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            LastDebug = "pin: bad hwnd";
            return false;
        }

        EnsureTopLevel(hwnd);
        EnsureToolWindowStyles(hwnd);

        if (NativeMethods.IsIconic(hwnd))
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        if (!NativeMethods.IsWindowVisible(hwnd))
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);

        var flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER;

        if (useTopmost)
        {
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                flags | NativeMethods.SWP_SHOWWINDOW);
            LastDebug = "pin: topmost | " + LastZOrderDebug;
        }
        else
        {
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0, flags);
            LastDebug = "pin: bottom | " + LastZOrderDebug;
        }

        return NativeMethods.IsWindowVisible(hwnd) && !NativeMethods.IsIconic(hwnd);
    }

    public static bool HasVisibleFullscreenApp(int ourPid)
    {
        _scanOurPid = ourPid;
        _scanFoundFullscreen = false;
        _scanFoundClass = "";
        try
        {
            NativeMethods.EnumWindows(EnumFullscreenCallback, IntPtr.Zero);
        }
        catch { /* ignore */ }
        return _scanFoundFullscreen;
    }

    private static bool EnumFullscreenCallback(IntPtr hWnd, IntPtr lParam)
    {
        try
        {
            if (!IsCandidateAppWindow(hWnd, _scanOurPid)) return true;
            var cls = NativeMethods.GetClassName(hWnd);
            if (IsFullscreenLike(hWnd) || IsKnownGameClass(cls))
            {
                if (IsKnownGameClass(cls) && !IsFullscreenLike(hWnd))
                {
                    if (NativeMethods.GetWindowRect(hWnd, out var wr))
                    {
                        var ww = wr.Right - wr.Left;
                        var wh = wr.Bottom - wr.Top;
                        if (ww < 640 || wh < 480) return true;
                    }
                }
                _scanFoundFullscreen = true;
                _scanFoundClass = cls;
                return false;
            }
        }
        catch { /* ignore */ }
        return true;
    }

    private static bool IsCandidateAppWindow(IntPtr hwnd, int ourPid)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return false;
        if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)) return false;
        if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero) return false;

        var style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_STYLE);
        if ((style & NativeMethods.WS_CHILD) != 0) return false;
        if ((style & NativeMethods.WS_DISABLED) != 0) return false;

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if ((int)pid == ourPid) return false;

        var cls = NativeMethods.GetClassName(hwnd);
        if (IsShellOrDesktopClass(cls)) return false;

        if (!NativeMethods.GetWindowRect(hwnd, out var wr)) return false;
        if ((wr.Right - wr.Left) < 100 || (wr.Bottom - wr.Top) < 100) return false;
        return true;
    }

    private static bool IsFullscreenLike(IntPtr hwnd)
    {
        try
        {
            if (!NativeMethods.GetWindowRect(hwnd, out var wr)) return false;
            var ww = wr.Right - wr.Left;
            var wh = wr.Bottom - wr.Top;
            if (ww < 200 || wh < 200) return false;

            var mon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero) return false;
            var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (!NativeMethods.GetMonitorInfo(mon, ref mi)) return false;

            var mw = mi.rcMonitor.Right - mi.rcMonitor.Left;
            var mh = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
            if (mw < 1 || mh < 1) return false;

            const int slop = 16;
            var covers =
                wr.Left <= mi.rcMonitor.Left + slop &&
                wr.Top <= mi.rcMonitor.Top + slop &&
                wr.Right >= mi.rcMonitor.Right - slop &&
                wr.Bottom >= mi.rcMonitor.Bottom - slop;
            if (covers) return true;

            var areaRatio = ((double)ww * wh) / ((double)mw * mh);
            if (areaRatio >= 0.90 && ww >= mw - 40 && wh >= mh - 80) return true;

            if (NativeMethods.IsZoomed(hwnd))
            {
                var workW = mi.rcWork.Right - mi.rcWork.Left;
                var workH = mi.rcWork.Bottom - mi.rcWork.Top;
                if (ww >= workW - slop && wh >= workH - slop) return true;
            }
            return false;
        }
        catch { return false; }
    }

    private static bool IsShellOrDesktopClass(string cls)
    {
        if (string.IsNullOrEmpty(cls)) return false;
        return cls is "Progman" or "WorkerW" or "SHELLDLL_DefView" or "Shell_TrayWnd"
            or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "DV2ControlHost"
            or "ForegroundStaging" or "MultitaskingViewFrame" or "XamlExplorerHostIslandWindow"
            or "Windows.UI.Core.CoreWindow" or "ImmersiveLauncher" or "SearchPane"
            or "Windows.Internal.Shell.TabProxyWindow" or "NativeHWNDHost"
            or "ApplicationManager_ImmersiveShellWindow";
    }

    private static bool IsKnownGameClass(string cls)
    {
        if (string.IsNullOrEmpty(cls)) return false;
        if (cls is "UnrealWindow" or "UnityWndClass" or "SDL_app" or "SDL_Window") return true;
        if (cls.Contains("RenderWindow", StringComparison.OrdinalIgnoreCase)) return true;
        if (cls.Contains("Valve001", StringComparison.OrdinalIgnoreCase)) return true;
        if (cls.StartsWith("CryENGINE", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static int CurrentProcessId => Environment.ProcessId;
}
