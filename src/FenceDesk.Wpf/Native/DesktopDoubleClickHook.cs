using System.Runtime.InteropServices;
using System.Threading;

namespace FenceDesk.Native;

/// <summary>
/// Minimal WH_MOUSE_LL hook: only reconstructs double-clicks and stashes screen coords.
/// Desktop hit-testing runs on the UI thread (never block the input queue).
/// </summary>
internal static class DesktopDoubleClickHook
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int SM_CXDOUBLECLK = 36;

    public static string LastDebug { get; private set; } = "";

    private static int _pending; // 1 = double-click candidate ready
    private static int _pendingX;
    private static int _pendingY;

    private static IntPtr _hook;
    private static LowLevelMouseProc? _proc; // keep alive
    private static int _lastClickTick;
    private static int _lastClickX;
    private static int _lastClickY;
    private static int _clickCount;
    private static int _lastFlagTick;

    public static bool IsRunning => _hook != IntPtr.Zero;

    public static bool Start()
    {
        if (_hook != IntPtr.Zero) return true;
        _proc = HookCallback;
        // hMod = 0 is fine for WH_MOUSE_LL on modern Windows
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, IntPtr.Zero, 0);
        LastDebug = _hook != IntPtr.Zero
            ? "hook ok"
            : "hook FAILED err=" + Marshal.GetLastWin32Error();
        return _hook != IntPtr.Zero;
    }

    public static void Stop()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        _proc = null;
        Interlocked.Exchange(ref _pending, 0);
    }

    /// <summary>
    /// If a double-click was detected, returns true and the screen point.
    /// Caller must verify the point is empty desktop before toggling.
    /// </summary>
    public static bool TryConsume(out int screenX, out int screenY)
    {
        if (Interlocked.Exchange(ref _pending, 0) == 0)
        {
            screenX = screenY = 0;
            return false;
        }
        screenX = Volatile.Read(ref _pendingX);
        screenY = Volatile.Read(ref _pendingY);
        return true;
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // MUST stay fast — never hit-test desktop / call Process / SendMessage here
        try
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_LBUTTONDOWN)
            {
                var hs = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var now = Environment.TickCount;
                var dbl = (int)GetDoubleClickTime();
                if (dbl < 200) dbl = 500;
                var slop = GetSystemMetrics(SM_CXDOUBLECLK);
                if (slop < 4) slop = 8;

                var dx = Math.Abs(hs.pt.X - _lastClickX);
                var dy = Math.Abs(hs.pt.Y - _lastClickY);
                var dt = now - _lastClickTick;
                if (dt < 0) dt = dbl + 1;

                var isSecond = _clickCount > 0 && dt <= dbl && dx <= slop && dy <= slop;
                if (isSecond)
                {
                    _clickCount = 0;
                    _lastClickTick = 0;
                    var since = now - _lastFlagTick;
                    if (since < 0 || since > 400)
                    {
                        _lastFlagTick = now;
                        Volatile.Write(ref _pendingX, hs.pt.X);
                        Volatile.Write(ref _pendingY, hs.pt.Y);
                        Interlocked.Exchange(ref _pending, 1);
                    }
                }
                else
                {
                    _clickCount = 1;
                    _lastClickTick = now;
                    _lastClickX = hs.pt.X;
                    _lastClickY = hs.pt.Y;
                }
            }
        }
        catch
        {
            // never throw from hook
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
