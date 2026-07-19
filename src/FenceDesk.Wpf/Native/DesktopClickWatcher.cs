using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using FenceDesk.Services;

namespace FenceDesk.Native;

/// <summary>
/// Desktop empty-space double-click via Raw Input (RIDEV_INPUTSINK).
/// Unlike WH_MOUSE_LL, this does NOT sit in the critical mouse path — so it
/// does not freeze/black-flash DWM when the desktop is double-clicked.
/// </summary>
internal sealed class DesktopClickWatcher : IDisposable
{
    private const int WM_INPUT = 0x00FF;
    private const int RIDEV_INPUTSINK = 0x00000100;
    private const int RID_INPUT = 0x10000003;
    private const int RIM_TYPEMOUSE = 0;
    private const int RI_MOUSE_LEFT_BUTTON_DOWN = 0x0001;

    private readonly Action _onDesktopDoubleClick;
    private readonly Dispatcher _dispatcher;
    private HwndSource? _source;
    private bool _registered;

    private int _lastClickTick;
    private int _lastClickX;
    private int _lastClickY;
    private int _clickCount;
    private int _lastToggleTick;

    public string LastDebug { get; private set; } = "";

    public DesktopClickWatcher(Dispatcher dispatcher, Action onDesktopDoubleClick)
    {
        _dispatcher = dispatcher;
        _onDesktopDoubleClick = onDesktopDoubleClick;
    }

    public bool Start()
    {
        if (_source is not null) return true;

        try
        {
            var p = new HwndSourceParameters("FenceDesk.RawInput")
            {
                Width = 1,
                Height = 1,
                PositionX = -10000,
                PositionY = -10000,
                WindowStyle = unchecked((int)0x80000000), // WS_POPUP
                ExtendedWindowStyle = 0x08000080 // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            };
            _source = new HwndSource(p);
            _source.AddHook(WndProc);

            var rid = new RAWINPUTDEVICE
            {
                usUsagePage = 0x01,
                usUsage = 0x02, // mouse
                dwFlags = RIDEV_INPUTSINK,
                hwndTarget = _source.Handle
            };
            if (!RegisterRawInputDevices(new[] { rid }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            {
                LastDebug = "RegisterRawInputDevices failed err=" + Marshal.GetLastWin32Error();
                Dispose();
                return false;
            }

            _registered = true;
            LastDebug = "raw input ok";
            return true;
        }
        catch (Exception ex)
        {
            LastDebug = "start ex: " + ex.Message;
            Dispose();
            return false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_INPUT) return IntPtr.Zero;

        try
        {
            uint dwSize = 0;
            GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref dwSize, (uint)Marshal.SizeOf<RAWINPUTHEADER>());
            if (dwSize == 0 || dwSize > 1024) return IntPtr.Zero;

            var buffer = Marshal.AllocHGlobal((int)dwSize);
            try
            {
                if (GetRawInputData(lParam, RID_INPUT, buffer, ref dwSize, (uint)Marshal.SizeOf<RAWINPUTHEADER>()) != dwSize)
                    return IntPtr.Zero;

                var raw = Marshal.PtrToStructure<RAWINPUT>(buffer);
                if (raw.header.dwType != RIM_TYPEMOUSE) return IntPtr.Zero;
                if ((raw.mouse.usButtonFlags & RI_MOUSE_LEFT_BUTTON_DOWN) == 0) return IntPtr.Zero;

                // Screen position from cursor (raw doesn't always include abs coords)
                if (!GetCursorPos(out var pt)) return IntPtr.Zero;

                var now = Environment.TickCount;
                var dbl = (int)GetDoubleClickTime();
                if (dbl < 200) dbl = 500;
                var slop = GetSystemMetrics(36);
                if (slop < 4) slop = 8;

                var dx = Math.Abs(pt.X - _lastClickX);
                var dy = Math.Abs(pt.Y - _lastClickY);
                var dt = now - _lastClickTick;
                if (dt < 0) dt = dbl + 1;

                var isSecond = _clickCount > 0 && dt <= dbl && dx <= slop && dy <= slop;
                if (isSecond)
                {
                    _clickCount = 0;
                    _lastClickTick = 0;
                    var since = now - _lastToggleTick;
                    if (since < 0 || since > 350)
                    {
                        _lastToggleTick = now;
                        var x = pt.X;
                        var y = pt.Y;
                        // Marshal to UI thread — never do heavy work here
                        _dispatcher.BeginInvoke(new Action(() => HandleCandidate(x, y)),
                            DispatcherPriority.Input);
                    }
                }
                else
                {
                    _clickCount = 1;
                    _lastClickTick = now;
                    _lastClickX = pt.X;
                    _lastClickY = pt.Y;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex)
        {
            LastDebug = "wndproc: " + ex.Message;
        }

        return IntPtr.Zero;
    }

    private void HandleCandidate(int x, int y)
    {
        try
        {
            if (!DesktopHitTest.IsEmptyDesktopAt(x, y))
            {
                LastDebug = "skip " + DesktopHitTest.LastDebug;
                return;
            }
            LastDebug = "toggle " + DesktopHitTest.LastDebug;
            _onDesktopDoubleClick();
        }
        catch (Exception ex)
        {
            AppLog.Write("DesktopClickWatcher: " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (_registered && _source is not null)
        {
            try
            {
                // Unregister by registering with RIDEV_REMOVE
                var rid = new RAWINPUTDEVICE
                {
                    usUsagePage = 0x01,
                    usUsage = 0x02,
                    dwFlags = 0x00000001, // RIDEV_REMOVE
                    hwndTarget = IntPtr.Zero
                };
                RegisterRawInputDevices(new[] { rid }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            }
            catch { /* ignore */ }
            _registered = false;
        }
        try { _source?.Dispose(); } catch { /* ignore */ }
        _source = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public int dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public int dwType;
        public int dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    // Matches Win32 RAWMOUSE (union layout) on x86/x64
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct RAWMOUSE
    {
        [FieldOffset(0)] public ushort usFlags;
        [FieldOffset(4)] public ushort usButtonFlags;
        [FieldOffset(6)] public ushort usButtonData;
        [FieldOffset(8)] public uint ulRawButtons;
        [FieldOffset(12)] public int lLastX;
        [FieldOffset(16)] public int lLastY;
        [FieldOffset(20)] public uint ulExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUT
    {
        public RAWINPUTHEADER header;
        public RAWMOUSE mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        [In] RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(
        IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
