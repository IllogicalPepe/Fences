using System.Runtime.InteropServices;

namespace FenceDesk.Native;

/// <summary>
/// Smart z-order: topmost when the desktop/shell is focused (Win+D / after minimizing a game);
/// under the focused app otherwise. Foreground-based — a background fullscreen game must not
/// keep fences stuck at HWND_BOTTOM behind the desktop.
/// </summary>
internal static class DesktopPin
{
    public static string LastDebug { get; private set; } = "";
    public static string LastZOrderDebug { get; private set; } = "";

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
        if (NativeMethods.IsIconic(hwnd) || !NativeMethods.IsWindowVisible(hwnd))
            return true;
        if (NativeMethods.IsCloaked(hwnd))
            return true;
        // Win+D / Show Desktop can strip TOPMOST without minimizing
        return false;
    }

    /// <summary>
    /// True when fences should sit above the desktop (Win+D, minimize game, click desktop).
    /// False when a normal app/game is the foreground window.
    /// </summary>
    public static bool ShouldUseTopmost(int ourPid)
    {
        try
        {
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

            // Minimized / cloaked / hidden foreground → treat as desktop
            if (!NativeMethods.IsWindowVisible(root) ||
                NativeMethods.IsIconic(root) ||
                NativeMethods.IsCloaked(root))
            {
                LastZOrderDebug = "topmost: fg-hidden " + cls;
                return true;
            }

            // Real app or game has focus — stay underneath
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

        if (NativeMethods.IsIconic(hwnd) || NativeMethods.IsCloaked(hwnd))
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        if (!NativeMethods.IsWindowVisible(hwnd))
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);

        var flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER |
                    NativeMethods.SWP_SHOWWINDOW;

        if (useTopmost)
        {
            // NOTOPMOST first clears a stale bottom band, then TOPMOST raises above desktop WorkerW
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, flags);
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

    public static int CurrentProcessId => Environment.ProcessId;
}
