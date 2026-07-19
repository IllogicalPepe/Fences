using System.Runtime.InteropServices;
using System.Text;

namespace FenceDesk.Native;

/// <summary>
/// Desktop surface detection using ONLY GetClassName / parent walk.
/// Never calls SendMessage into Explorer (that blacks multi-monitor DWM).
/// Icon vs empty-space is decided by DesktopClickPoller via deferred focus check.
/// </summary>
internal static class DesktopHitTest
{
    private const uint GA_ROOT = 2;

    public static string LastDebug { get; private set; } = "";

    /// <summary>
    /// True if the point is on the real desktop host (Progman/WorkerW),
    /// not on FenceDesk, Explorer folders, taskbar, or other apps.
    /// Does NOT distinguish desktop icons from empty wallpaper — that is handled
    /// by a deferred focus check in the poller (no Explorer messages).
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
    /// True if this window looks like a normal app / Explorer folder that would
    /// receive focus after double-clicking a desktop icon. Used to cancel a
    /// pending fence toggle without talking to the desktop list-view.
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

    // Keep old name as alias so any leftover callers compile
    public static bool IsEmptyDesktopAt(int screenX, int screenY) =>
        IsDesktopSurfaceAt(screenX, screenY);

    private static string ClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var sb = new StringBuilder(128);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT pt);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
