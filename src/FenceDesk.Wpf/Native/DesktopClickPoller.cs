using System.Runtime.InteropServices;
using System.Windows.Threading;
using FenceDesk.Services;

namespace FenceDesk.Native;

/// <summary>
/// Empty-desktop double-click via UI-thread polling only.
/// No mouse hooks, no Raw Input, no SendMessage into Explorer.
///
/// Distinguishing icon vs empty space without LVM_HITTEST:
/// after a desktop-surface double-click we WAIT briefly; if focus moved to a
/// normal app / Explorer folder, we cancel (user opened an icon). If focus is
/// still the desktop shell, we toggle fences.
/// </summary>
internal sealed class DesktopClickPoller : IDisposable
{
    private const int VK_LBUTTON = 0x01;
    /// <summary>How long to wait to see if an icon launch stole focus.</summary>
    private const int DeferMs = 220;

    private readonly DispatcherTimer _timer;
    private readonly Action _onDesktopDoubleClick;

    private bool _prevDown;
    private int _lastClickTick;
    private int _lastClickX;
    private int _lastClickY;
    private int _clickCount;
    private int _lastFireTick;

    // Pending deferred toggle
    private bool _pending;
    private int _pendingDueTick;
    private int _pendingX;
    private int _pendingY;
    private IntPtr _fgAtPending; // foreground at schedule time — cancel only if focus *moves* to an app

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
            // Resolve deferred toggle first
            if (_pending)
            {
                var now = Environment.TickCount;
                var wait = now - _pendingDueTick;
                if (wait >= 0)
                {
                    _pending = false;
                    TryFireDeferred();
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
            _clickCount = 1;
            _lastClickTick = now;
            _lastClickX = pt.X;
            _lastClickY = pt.Y;
            return;
        }

        _clickCount = 0;
        _lastClickTick = 0;

        var since = now - _lastFireTick;
        if (since >= 0 && since < 350) return; // hard debounce

        // Class-name only — never touch Explorer list-view
        if (!DesktopHitTest.IsDesktopSurfaceAt(pt.X, pt.Y))
        {
            LastDebug = "skip " + DesktopHitTest.LastDebug;
            _pending = false;
            return;
        }

        // Defer: icon double-clicks steal focus to the opened app; empty ones don't
        _pending = true;
        _pendingX = pt.X;
        _pendingY = pt.Y;
        _pendingDueTick = now + DeferMs;
        _fgAtPending = GetForegroundWindow();
        LastDebug = "pending-empty-check " + DesktopHitTest.LastDebug;
    }

    private void TryFireDeferred()
    {
        try
        {
            var fg = GetForegroundWindow();
            // Only cancel when focus *moved* to a different top-level app/folder.
            // (If Chrome was already focused and user double-clicks empty desktop,
            //  FG may stay Chrome — still allow toggle.)
            if (fg != IntPtr.Zero && fg != _fgAtPending && DesktopHitTest.IsLaunchedAppWindow(fg))
            {
                LastDebug = "cancel-icon-launch focus-stole";
                return;
            }

            // Point of the double-click (or current cursor) must still be desktop surface
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
            LastDebug = "fire-empty " + DesktopHitTest.LastDebug;
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
