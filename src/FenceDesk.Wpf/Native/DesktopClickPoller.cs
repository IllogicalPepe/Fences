using System.Runtime.InteropServices;
using System.Windows.Threading;
using FenceDesk.Services;

namespace FenceDesk.Native;

/// <summary>
/// Empty-desktop double-click via UI-thread polling only.
/// No mouse hooks, no Raw Input, no list-view SendMessage.
///
/// Icon vs empty wallpaper is decided by DesktopHitTest (MSAA). A deferred
/// focus/cursor check is only a fallback when accessibility cannot tell.
/// </summary>
internal sealed class DesktopClickPoller : IDisposable
{
    private const int VK_LBUTTON = 0x01;
    /// <summary>Fallback wait when we could not hit-test the icon.</summary>
    private const int DeferMs = 750;

    private readonly DispatcherTimer _timer;
    private readonly Action _onDesktopDoubleClick;

    private bool _prevDown;
    private int _lastClickTick;
    private int _lastClickX;
    private int _lastClickY;
    private int _clickCount;
    private int _lastFireTick;

    // Pending deferred toggle (unknown hit-test only)
    private bool _pending;
    private int _pendingDueTick;
    private int _pendingX;
    private int _pendingY;
    private IntPtr _fgAtPending;

    public string LastDebug { get; private set; } = "poller";

    public DesktopClickPoller(Dispatcher dispatcher, Action onDesktopDoubleClick)
    {
        _onDesktopDoubleClick = onDesktopDoubleClick;
        _timer = new DispatcherTimer(DispatcherPriority.Input, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _timer.Tick += OnTick;
    }

    public void Start() => _timer.Start();

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
        _pending = false;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        try
        {
            if (_pending)
            {
                if (LaunchLooksStarted())
                {
                    _pending = false;
                    LastDebug = "cancel-icon-launch";
                }
                else
                {
                    var wait = Environment.TickCount - _pendingDueTick;
                    if (wait >= 0)
                    {
                        _pending = false;
                        TryFireDeferred();
                    }
                }
            }

            var down = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
            if (down && !_prevDown)
                OnLeftDownEdge();
            _prevDown = down;
        }
        catch (Exception ex)
        {
            LastDebug = "tick: " + ex.Message;
        }
    }

    private void OnLeftDownEdge()
    {
        if (!GetCursorPos(out var pt)) return;

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
        if (!isSecond)
        {
            _pending = false;
            _clickCount = 1;
            _lastClickTick = now;
            _lastClickX = pt.X;
            _lastClickY = pt.Y;
            return;
        }

        var firstX = _lastClickX;
        var firstY = _lastClickY;
        _clickCount = 0;
        _lastClickTick = 0;

        var since = now - _lastFireTick;
        if (since >= 0 && since < 350) return;

        if (!DesktopHitTest.IsDesktopSurfaceAt(pt.X, pt.Y))
        {
            LastDebug = "skip " + DesktopHitTest.LastDebug;
            _pending = false;
            return;
        }

        var icon2 = DesktopHitTest.IsDesktopIconAt(pt.X, pt.Y);
        var icon1 = DesktopHitTest.IsDesktopIconAt(firstX, firstY);
        var selected = DesktopHitTest.HasDesktopIconSelection(pt.X, pt.Y);

        // Any icon hit or a selected desktop item → this is opening/clicking a file, not empty space.
        if (icon1 == true || icon2 == true || selected == true)
        {
            LastDebug = "skip-icon " + DesktopHitTest.LastDebug;
            AppLog.Write($"Desktop dblclick SKIP icon1={icon1} icon2={icon2} sel={selected} {DesktopHitTest.LastDebug}");
            _pending = false;
            return;
        }

        if (icon1 == false && icon2 == false)
        {
            if (DesktopHitTest.IsLaunchCursor())
            {
                LastDebug = "skip-launch-cursor";
                AppLog.Write("Desktop dblclick SKIP launch-cursor");
                _pending = false;
                return;
            }

            _lastFireTick = now;
            LastDebug = "fire-empty " + DesktopHitTest.LastDebug;
            AppLog.Write($"Desktop dblclick FIRE empty {DesktopHitTest.LastDebug}");
            _onDesktopDoubleClick();
            return;
        }

        _pending = true;
        _pendingX = pt.X;
        _pendingY = pt.Y;
        _pendingDueTick = now + DeferMs;
        _fgAtPending = GetForegroundWindow();
        LastDebug = "pending-empty-check " + DesktopHitTest.LastDebug;
        AppLog.Write($"Desktop dblclick PENDING icon1={icon1} icon2={icon2} sel={selected} {DesktopHitTest.LastDebug}");
    }

    private bool LaunchLooksStarted()
    {
        if (DesktopHitTest.IsLaunchCursor())
            return true;

        var fg = GetForegroundWindow();
        return fg != IntPtr.Zero && fg != _fgAtPending && DesktopHitTest.IsLaunchedAppWindow(fg);
    }

    private void TryFireDeferred()
    {
        try
        {
            if (LaunchLooksStarted())
            {
                LastDebug = "cancel-icon-launch focus-stole";
                AppLog.Write("Desktop dblclick CANCEL launch/focus");
                return;
            }

            if (DesktopHitTest.IsDesktopIconAt(_pendingX, _pendingY) == true ||
                DesktopHitTest.HasDesktopIconSelection(_pendingX, _pendingY) == true)
            {
                LastDebug = "cancel-icon " + DesktopHitTest.LastDebug;
                AppLog.Write("Desktop dblclick CANCEL icon " + DesktopHitTest.LastDebug);
                return;
            }

            var onDesktop = DesktopHitTest.IsDesktopSurfaceAt(_pendingX, _pendingY);
            if (!onDesktop && GetCursorPos(out var cur))
                onDesktop = DesktopHitTest.IsDesktopSurfaceAt(cur.X, cur.Y);
            if (!onDesktop)
            {
                LastDebug = "cancel-left-desktop " + DesktopHitTest.LastDebug;
                return;
            }

            var now = Environment.TickCount;
            _lastFireTick = now;
            LastDebug = "fire-empty-deferred " + DesktopHitTest.LastDebug;
            AppLog.Write("Desktop dblclick FIRE deferred " + DesktopHitTest.LastDebug);
            _onDesktopDoubleClick();
        }
        catch (Exception ex)
        {
            LastDebug = "defer: " + ex.Message;
            AppLog.Write("DesktopClickPoller defer: " + ex.Message);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
