using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using FenceDesk.Models;
using FenceDesk.Native;
using FenceDesk.Services;

namespace FenceDesk.Windows;

public sealed class FenceWindow : Window
{
    /// <summary>Custom DnD format for items dragged between/out of fences (newline-separated paths).</summary>
    public const string FenceItemFormat = "FenceDesk.ItemPath";

    /// <summary>Set by a fence Drop handler so the drag source knows where it landed.</summary>
    internal static string? LastDropFenceId;

    private readonly FenceManager _manager;
    private FenceModel _model;
    private readonly TextBlock _titleText;
    private readonly StackPanel _tabStrip;
    private readonly Grid _body;
    private readonly WrapPanel _itemsPanel;
    private readonly TextBlock _hint;
    private readonly Border _glass;
    /// <summary>Client-drawn white resize chrome (edges + corner grip). WindowChrome alone is invisible.</summary>
    private readonly FrameworkElement _resizeChrome;
    private readonly Canvas _fxLayer;
    private bool _deleteFxActive;
    private double _expandedHeight;
    private bool _readyToSync;
    private bool _suppressGeometry;
    private int _restX, _restY, _restW, _restH;
    private bool _hasRestRect;
    private Border _titleBar = null!;
    private bool _groupDragging;
    private bool _soloGroupDrag;
    private bool _forceSoloDragNext;
    private System.Windows.Point _groupDragScreenStart;
    private readonly Dictionary<string, (double L, double T)> _groupDragOrigins = new();

    // Multi-select state (paths of selected tiles) — desktop-style marquee on empty area
    private readonly HashSet<string> _selectedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Border> _tileByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _itemOrder = new();
    private bool _marqueePending;
    private bool _marqueeActive;
    private bool _marqueeAdditive;
    private HashSet<string>? _marqueeBaseSelection;
    private System.Windows.Point _marqueeStartBody;
    private Border? _marqueeVisual;
    private string? _selectionAnchor;
    private Border? _insertMarker;

    /// <summary>Fence that started the current item drag (so source fence can reject self-drops).</summary>
    internal static string? DragSourceFenceId;

    public bool ToggleHidden { get; set; }
    public string FenceId => _model.Id;

    /// <summary>True when fence is visually shown (not soft-hidden).</summary>
    public bool IsOnScreen => !ToggleHidden;

    /// <summary>Last Win32 topmost pin state (WPF Topmost stays false to avoid DWM flash).</summary>
    public bool IsPinnedTopmost { get; private set; }

    public FenceWindow(FenceManager manager, FenceModel model)
    {
        _manager = manager;
        _model = model;
        _expandedHeight = Math.Max(80, model.Height);

        Title = " ";
        WindowStyle = WindowStyle.None;
        // True panel transparency (see desktop through glass; icons stay solid) needs
        // per-brush alpha, which requires AllowsTransparency. Hide/show still parks
        // off-screen (no Visibility flicker) to avoid multi-monitor DWM flash.
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        // Always full window opacity — transparency is applied only to the glass fill
        Opacity = 1.0;
        ShowInTaskbar = false;
        ShowActivated = false;
        // Unlocked: CanResize. Locked: NoResize (see UpdateLockChrome).
        ResizeMode = ResizeMode.CanResize;
        Width = Math.Max(160, model.Width);
        Height = Math.Max(80, model.Height);
        Left = model.X;
        Top = model.Y;
        MinWidth = 140;
        MinHeight = 80;
        Topmost = false;

        // Custom WindowChrome: invisible hit-test resize edges when unlocked (toggled in UpdateLockChrome).
        // Visible white chrome is drawn in-client via _resizeChrome (OS grip is suppressed by WindowChrome).
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false
        });

        var shell = new Grid();
        _glass = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Background = new SolidColorBrush(Color.FromRgb(15, 23, 36))
        };
        // ApplyGlassAppearance runs after title/body controls exist (see end of ctor)

        var content = new Border { Background = Brushes.Transparent, Padding = new Thickness(8, 6, 8, 8) };
        var root = new DockPanel { LastChildFill = true };

        // Title (double-click title rolls up; body double-click does not)
        _titleBar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(15, 23, 36)),
            Padding = new Thickness(2, 0, 2, 4),
            Cursor = Cursors.SizeAll
        };
        DockPanel.SetDock(_titleBar, Dock.Top);
        var titleBar = _titleBar;
        _titleText = new TextBlock
        {
            Text = model.Title,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(200, 208, 220)),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Double-click title to roll up · drag on empty area to multi-select"
        };
        titleBar.Child = _titleText;

        _tabStrip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(_tabStrip, Dock.Top);

        _body = new Grid();
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Brushes.Transparent
        };
        _itemsPanel = new WrapPanel { Orientation = Orientation.Horizontal };
        scroll.Content = _itemsPanel;
        _hint = new TextBlock
        {
            Text = "Drop files here\nRight-click for options",
            Foreground = new SolidColorBrush(Color.FromRgb(140, 150, 165)),
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.85
        };
        _body.Children.Add(scroll);
        _body.Children.Add(_hint);

        root.Children.Add(titleBar);
        root.Children.Add(_tabStrip);
        root.Children.Add(_body);
        content.Child = root;
        _resizeChrome = BuildResizeChrome();
        _fxLayer = new Canvas { IsHitTestVisible = false, ClipToBounds = false };
        shell.Children.Add(_glass);
        shell.Children.Add(content);
        shell.Children.Add(_resizeChrome);
        shell.Children.Add(_fxLayer);
        Content = shell;

        // Colors / text / opacity — must run after title bar, hint, panels exist
        ApplyGlassAppearance();

        // Events
        titleBar.PreviewMouseLeftButtonDown += TitleBar_MouseLeftButtonDown;
        titleBar.PreviewMouseMove += TitleBar_MouseMove;
        titleBar.PreviewMouseLeftButtonUp += TitleBar_MouseLeftButtonUp;
        titleBar.LostMouseCapture += (_, _) => EndGroupDrag(snap: true);
        // Empty body: click-drag marquee multi-select (desktop style). Tiles handle their own select/drag.
        _body.PreviewMouseLeftButtonDown += Body_PreviewMouseLeftButtonDown;
        _body.PreviewMouseMove += Body_PreviewMouseMove;
        _body.PreviewMouseLeftButtonUp += Body_PreviewMouseLeftButtonUp;
        _body.LostMouseCapture += (_, _) => EndMarquee(commit: true);
        _hint.MouseLeftButtonDown += EmptyArea_MouseLeftButtonDown;
        content.MouseLeftButtonDown += EmptyArea_MouseLeftButtonDown;
        scroll.MouseLeftButtonDown += EmptyArea_MouseLeftButtonDown;
        PreviewKeyDown += FenceWindow_PreviewKeyDown;
        Focusable = true;
        SourceInitialized += (_, _) => ApplyDesktopChrome();
        Loaded += (_, _) =>
        {
            _readyToSync = true;
            ApplyDesktopChrome();
            ExcludeFromAltTab();
            UpdateLockChrome();
            ApplyGlassAppearance(); // re-apply layered opacity after HWND/styles settle
        };
        LocationChanged += (_, _) =>
        {
            if (_groupDragging) return;
            SyncGeometry();
        };
        SizeChanged += (_, _) =>
        {
            if (_readyToSync && !_suppressGeometry && !_model.RolledUp && ActualHeight > 40)
                _expandedHeight = ActualHeight;
            if (!_groupDragging) SyncGeometry();
        };
        AllowDrop = true;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        Drop += OnDrop;
        GiveFeedback += OnGiveFeedback;

        BuildContextMenu();
        RefreshContent();
        UpdateLockChrome();
        if (model.RolledUp) ApplyRollUp();
    }

    /// <summary>
    /// Visible white edge strips + corner grip. Drawn in client area because
    /// WindowChrome's ResizeBorderThickness is hit-test only (not painted).
    /// Shown only when unlocked and not rolled up.
    /// </summary>
    private static FrameworkElement BuildResizeChrome()
    {
        var white = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255));
        var dim = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255));

        var root = new Grid
        {
            IsHitTestVisible = false, // resize hits go to WindowChrome border, not this overlay
            Visibility = Visibility.Collapsed
        };

        // Thin white strips on the four edges (the "white things")
        root.Children.Add(new Border
        {
            Height = 3,
            VerticalAlignment = VerticalAlignment.Top,
            Background = dim,
            Margin = new Thickness(3, 0, 3, 0)
        });
        root.Children.Add(new Border
        {
            Height = 3,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = dim,
            Margin = new Thickness(3, 0, 16, 0)
        });
        root.Children.Add(new Border
        {
            Width = 3,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = dim,
            Margin = new Thickness(0, 3, 0, 3)
        });
        root.Children.Add(new Border
        {
            Width = 3,
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = dim,
            Margin = new Thickness(0, 3, 0, 16)
        });

        // Bottom-right grip: classic dotted triangle (white)
        var grip = new Canvas
        {
            Width = 14,
            Height = 14,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 2, 2)
        };
        void Dot(double x, double y)
        {
            grip.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 2.2,
                Height = 2.2,
                Fill = white,
                Margin = new Thickness(x, y, 0, 0)
            });
        }
        // 3 diagonal rows of dots (NWSE grip)
        Dot(10, 2); Dot(10, 6); Dot(10, 10);
        Dot(6, 6); Dot(6, 10);
        Dot(2, 10);
        root.Children.Add(grip);

        return root;
    }

    public void ApplyOffset(double dx, double dy)
    {
        if (_model.Locked) return;
        _suppressGeometry = true;
        try
        {
            Left += dx;
            Top += dy;
        }
        finally { _suppressGeometry = false; }
    }

    public void ApplySizeFromModel()
    {
        _model = _manager.LayoutStore.FindFence(_model.Id) ?? _model;
        _suppressGeometry = true;
        try
        {
            Width = Math.Max(140, _model.Width);
            if (!_model.RolledUp)
            {
                Height = Math.Max(80, _model.Height);
                _expandedHeight = Height;
            }
        }
        finally { _suppressGeometry = false; }
    }

    public void PushGeometryToModel()
    {
        if (ToggleHidden || Left < -5000) return;
        _model.X = Left;
        _model.Y = Top;
        _model.Width = Width;
        if (!_model.RolledUp && Height > 40)
            _model.Height = Height;
        _manager.LayoutStore.UpdateFence(_model);
    }

    private void OnDragOver(object sender, System.Windows.DragEventArgs e)
    {
        // FileDrop for normal files; Shell IDList for virtual desktop icons (Recycle Bin, This PC, …)
        if (e.Data.GetDataPresent(DataFormats.FileDrop) ||
            e.Data.GetDataPresent(FenceItemFormat) ||
            e.Data.GetDataPresent(DataFormats.StringFormat) ||
            ShellIdListDrop.IsPresent(e.Data))
        {
            var sameFenceReorder = e.Data.GetDataPresent(FenceItemFormat) &&
                string.Equals(DragSourceFenceId, _model.Id, StringComparison.Ordinal) &&
                !_model.IsPortal;

            if (sameFenceReorder)
            {
                e.Effects = DragDropEffects.Move;
                var paths = ExtractDropPaths(e.Data);
                var dragging = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
                var insert = GetReorderInsertIndex(e.GetPosition(_itemsPanel), dragging);
                ShowInsertMarker(insert, dragging);
            }
            else
            {
                HideInsertMarker();
                e.Effects = e.Data.GetDataPresent(FenceItemFormat)
                    ? DragDropEffects.Move
                    : DragDropEffects.Copy;
            }
        }
        else
        {
            HideInsertMarker();
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnDragLeave(object sender, System.Windows.DragEventArgs e)
    {
        // DragLeave also fires when moving over child elements — only hide when truly leaving
        var p = e.GetPosition(this);
        if (p.X < 0 || p.Y < 0 || p.X > ActualWidth || p.Y > ActualHeight)
            HideInsertMarker();
    }

    private void OnGiveFeedback(object sender, System.Windows.GiveFeedbackEventArgs e)
    {
        // Use the standard OLE cursors — avoids a stuck/custom "second cursor" on layered windows
        e.UseDefaultCursors = true;
        e.Handled = true;
    }

    private void OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        try
        {
            var paths = ExtractDropPaths(e.Data);
            if (paths.Count == 0)
            {
                AppLog.Write("Drop: no paths extracted (formats: " +
                             string.Join(", ", e.Data.GetFormats(true) ?? Array.Empty<string>()) + ")");
                return;
            }

            LastDropFenceId = _model.Id;

            // Same-fence drop → rearrange icons instead of re-adding
            if (e.Data.GetDataPresent(FenceItemFormat) &&
                string.Equals(DragSourceFenceId, _model.Id, StringComparison.Ordinal) &&
                !_model.IsPortal)
            {
                var dragging = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
                var insert = GetReorderInsertIndex(e.GetPosition(_itemsPanel), dragging);
                _manager.ReorderItems(_model.Id, paths, insert);
                e.Effects = DragDropEffects.Move;
                AppLog.Write("Reorder drop: " + string.Join("; ", paths));
                return;
            }

            _manager.AddItems(_model.Id, paths);
            e.Effects = e.Data.GetDataPresent(FenceItemFormat)
                ? DragDropEffects.Move
                : DragDropEffects.Copy;
            AppLog.Write("Drop accepted: " + string.Join("; ", paths));
        }
        catch (Exception ex)
        {
            AppLog.Write($"Drop: {ex.Message}");
        }
        finally
        {
            HideInsertMarker();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Insert index among items that are not being dragged (WrapPanel reading order).
    /// </summary>
    private int GetReorderInsertIndex(System.Windows.Point posInItemsPanel, HashSet<string> dragging)
    {
        var remaining = _itemOrder.Where(p => !dragging.Contains(p)).ToList();
        for (var i = 0; i < remaining.Count; i++)
        {
            if (!_tileByPath.TryGetValue(remaining[i], out var tile)) continue;
            try
            {
                var origin = tile.TranslatePoint(new System.Windows.Point(0, 0), _itemsPanel);
                var cx = origin.X + tile.ActualWidth / 2;
                var top = origin.Y;
                var bottom = origin.Y + tile.ActualHeight;
                var aboveRow = posInItemsPanel.Y < top;
                var sameRow = posInItemsPanel.Y >= top && posInItemsPanel.Y <= bottom;
                if (aboveRow || (sameRow && posInItemsPanel.X < cx))
                    return i;
            }
            catch { /* layout race */ }
        }
        return remaining.Count;
    }

    private void ShowInsertMarker(int insertIndex, HashSet<string> dragging)
    {
        if (_insertMarker is null)
        {
            _insertMarker = new Border
            {
                Width = 2,
                Background = new SolidColorBrush(Color.FromRgb(80, 150, 255)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                CornerRadius = new CornerRadius(1)
            };
            _body.Children.Add(_insertMarker);
            System.Windows.Controls.Panel.SetZIndex(_insertMarker, 100);
        }

        var remaining = _itemOrder.Where(p => !dragging.Contains(p)).ToList();
        double x = 8, y = 8, h = Math.Max(24, _manager.Icons.IconSize + 20);

        try
        {
            if (remaining.Count == 0)
            {
                // Empty (all items dragged) — mark start of panel
                var panelOrigin = _itemsPanel.TranslatePoint(new System.Windows.Point(0, 0), _body);
                x = panelOrigin.X + 4;
                y = panelOrigin.Y + 4;
            }
            else if (insertIndex >= remaining.Count)
            {
                if (_tileByPath.TryGetValue(remaining[^1], out var last))
                {
                    var tl = last.TranslatePoint(new System.Windows.Point(0, 0), _body);
                    x = tl.X + last.ActualWidth - 1;
                    y = tl.Y;
                    h = Math.Max(24, last.ActualHeight);
                }
            }
            else if (_tileByPath.TryGetValue(remaining[insertIndex], out var tile))
            {
                var tl = tile.TranslatePoint(new System.Windows.Point(0, 0), _body);
                x = tl.X;
                y = tl.Y;
                h = Math.Max(24, tile.ActualHeight);
            }
        }
        catch { /* ignore */ }

        _insertMarker.Margin = new Thickness(x, y, 0, 0);
        _insertMarker.Height = h;
        _insertMarker.Visibility = Visibility.Visible;
    }

    private void HideInsertMarker()
    {
        if (_insertMarker is not null)
            _insertMarker.Visibility = Visibility.Collapsed;
    }

    private static List<string> ExtractDropPaths(System.Windows.IDataObject data)
    {
        var paths = new List<string>();
        void AddPath(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            // FenceItemFormat may be multi-line (multi-select drag)
            foreach (var line in p.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = ShellIdListDrop.NormalizeShellPath(line.Trim().Trim('"'));
                if (t.Length == 0) continue;
                if (!paths.Contains(t, StringComparer.OrdinalIgnoreCase))
                    paths.Add(t);
            }
        }
        try
        {
            if (data.GetDataPresent(FenceItemFormat))
                AddPath(data.GetData(FenceItemFormat) as string);

            // Virtual shell icons (Recycle Bin) — must run even when FileDrop is empty
            if (ShellIdListDrop.IsPresent(data))
            {
                foreach (var p in ShellIdListDrop.ExtractPaths(data))
                    AddPath(p);
            }

            if (data.GetDataPresent(DataFormats.FileDrop) &&
                data.GetData(DataFormats.FileDrop) is string[] files)
            {
                foreach (var f in files)
                    AddPath(f);
            }
            if (paths.Count == 0 && data.GetDataPresent(DataFormats.StringFormat))
            {
                var s = data.GetData(DataFormats.StringFormat) as string;
                if (!string.IsNullOrWhiteSpace(s) &&
                    (s.Contains('\\') || s.StartsWith("::", StringComparison.Ordinal) ||
                     s.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ||
                     s.Contains("Recycle", StringComparison.OrdinalIgnoreCase)))
                    AddPath(s);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("ExtractDropPaths: " + ex.Message);
        }
        return paths;
    }

    public IntPtr GetHwnd()
    {
        try { return new WindowInteropHelper(this).Handle; }
        catch { return IntPtr.Zero; }
    }

    public void ApplyDesktopChrome(bool? useTopmost = null, bool raise = false)
    {
        if (ToggleHidden) return;
        try
        {
            // Keep WPF Topmost false — HWND_TOPMOST via DesktopPin survives Win+D without DWM flash
            Topmost = false;
            Opacity = 1.0; // whole-window opacity must stay 1; panel alpha is on brushes only
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            if (!IsVisible) Show();
            if (Visibility != Visibility.Visible)
                Visibility = Visibility.Visible;
            // Pull back on-screen if we were parked or coords are nonsense
            if (Left < -5000 || Top < -5000)
            {
                Left = _model.X;
                Top = _model.Y;
            }
            ExcludeFromAltTab();
            var hwnd = EnsureHwnd();
            if (hwnd != IntPtr.Zero)
            {
                var want = useTopmost ?? DesktopPin.ShouldUseTopmost(DesktopPin.CurrentProcessId);
                // Position without changing z-order, then pin for Win+D / app-focus
                var x = (int)Math.Round(Left);
                var y = (int)Math.Round(Top);
                var w = (int)Math.Round(Math.Max(MinWidth, ActualWidth > 1 ? ActualWidth : Width));
                var h = (int)Math.Round(Math.Max(MinHeight, ActualHeight > 1 ? ActualHeight : Height));
                NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h,
                    NativeMethods.SWP_NOACTIVATE
                    | NativeMethods.SWP_NOZORDER
                    | NativeMethods.SWP_SHOWWINDOW
                    | NativeMethods.SWP_NOSENDCHANGING);
                DesktopPin.PinForShowDesktop(hwnd, want);
                if (raise && want)
                {
                    NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
                }
                IsPinnedTopmost = want;
            }
            UpdateLockChrome();
            ApplyGlassAppearance();
        }
        catch (Exception ex) { AppLog.Write($"ApplyDesktopChrome: {ex.Message}"); }
    }

    /// <summary>
    /// Hide/show without WPF Visibility, Opacity, Topmost, or Explorer messages.
    /// Uses a single async SetWindowPos to park off-screen / restore.
    /// This avoids DWM multi-monitor blackouts from layered/topmost/ShowWindow paths.
    /// </summary>
    public void SetSoftVisible(bool visible)
    {
        var hwnd = EnsureHwnd();
        if (hwnd == IntPtr.Zero)
        {
            // Absolute fallback
            ToggleHidden = !visible;
            Visibility = visible ? Visibility.Visible : Visibility.Hidden;
            return;
        }

        _suppressGeometry = true;
        try
        {
            if (!visible)
            {
                if (!ToggleHidden)
                {
                    // Snapshot current screen rect via Win32 (not WPF — more accurate)
                    if (NativeMethods.GetWindowRect(hwnd, out var rc))
                    {
                        _restX = rc.Left;
                        _restY = rc.Top;
                        _restW = Math.Max(1, rc.Right - rc.Left);
                        _restH = Math.Max(1, rc.Bottom - rc.Top);
                        _hasRestRect = true;
                        // Keep model in sync for next session (no disk write here)
                        _model.X = _restX;
                        _model.Y = _restY;
                        _model.Width = _restW;
                        _model.Height = _restH;
                    }
                }
                ToggleHidden = true;
                IsHitTestVisible = false;
                // CRITICAL: keep WPF Left/Top in sync with Win32. If they diverge,
                // WPF's next layout pass pulls the HWND back on-screen and DWM
                // multi-monitor composition can black out for ~1s while fighting itself.
                Left = -32000;
                Top = -32000;
                NativeMethods.SetWindowPos(
                    hwnd, IntPtr.Zero,
                    -32000, -32000, 0, 0,
                    NativeMethods.SWP_NOSIZE
                    | NativeMethods.SWP_NOZORDER
                    | NativeMethods.SWP_NOACTIVATE
                    | NativeMethods.SWP_NOSENDCHANGING
                    | NativeMethods.SWP_NOCOPYBITS
                    | NativeMethods.SWP_ASYNCWINDOWPOS);
            }
            else
            {
                ToggleHidden = false;
                IsHitTestVisible = true;
                var x = _hasRestRect ? _restX : (int)Math.Round(_model.X);
                var y = _hasRestRect ? _restY : (int)Math.Round(_model.Y);
                var w = _hasRestRect ? _restW : (int)Math.Round(Math.Max(140, _model.Width));
                var h = _hasRestRect ? _restH : (int)Math.Round(Math.Max(80, _model.Height));
                // Update WPF first so layout won't fight SetWindowPos
                Left = x;
                Top = y;
                Width = w;
                Height = h;
                NativeMethods.SetWindowPos(
                    hwnd, NativeMethods.HWND_NOTOPMOST,
                    x, y, w, h,
                    NativeMethods.SWP_NOACTIVATE
                    | NativeMethods.SWP_NOSENDCHANGING
                    | NativeMethods.SWP_ASYNCWINDOWPOS);
                // Layered alpha can be reset by style changes — re-apply
                ApplyWindowOpacity();
            }
        }
        finally
        {
            _suppressGeometry = false;
        }
    }

    private IntPtr EnsureHwnd()
    {
        var hwnd = GetHwnd();
        if (hwnd != IntPtr.Zero) return hwnd;
        try { return new WindowInteropHelper(this).EnsureHandle(); }
        catch { return IntPtr.Zero; }
    }

    private void ExcludeFromAltTab()
    {
        try
        {
            ShowInTaskbar = false;
            var hwnd = GetHwnd();
            if (hwnd != IntPtr.Zero)
                NativeMethods.ExcludeFromAltTab(hwnd);
        }
        catch { /* ignore */ }
    }

    public const string DefaultBgColor = "#0F1724";
    public const string DefaultTextColor = "#C8D0DC";

    public void ApplyGlassAppearance()
    {
        try
        {
            // Always prefer the store model when refs diverge. Bulk updates
            // (SetAllBackgroundColor etc.) write the store first; preserving
            // stale window appearance would clobber those changes.
            // Opacity dialog live-previews write through to the store object.
            var stored = _manager.LayoutStore.FindFence(_model.Id);
            if (stored is not null)
                _model = stored;

            var rgb = FenceManager.ParseHex(
                string.IsNullOrWhiteSpace(_model.BgColor) ? DefaultBgColor : _model.BgColor);
            var textRgb = FenceManager.ParseHex(
                string.IsNullOrWhiteSpace(_model.TextColor) ? DefaultTextColor : _model.TextColor);

            // Panel alpha only — desktop shows through; icons/text stay fully opaque
            var panelAlpha = (byte)Math.Round(Math.Clamp(_model.Opacity, 0.08, 1.0) * 255.0);
            var fill = Color.FromArgb(panelAlpha, rgb.R, rgb.G, rgb.B);
            var text = Color.FromRgb(textRgb.R, textRgb.G, textRgb.B); // solid labels
            var hint = Color.FromArgb(220, textRgb.R, textRgb.G, textRgb.B);
            // Border tracks panel alpha so edges fade with the glass
            var border = Color.FromArgb(
                panelAlpha,
                (byte)Math.Clamp(rgb.R + 40, 0, 255),
                (byte)Math.Clamp(rgb.G + 45, 0, 255),
                (byte)Math.Clamp(rgb.B + 50, 0, 255));

            // Window chrome stays fully transparent — only glass + title paint the fill
            Background = Brushes.Transparent;
            Opacity = 1.0; // never dim the whole window (that blacks out / fades icons)
            if (_glass is not null)
            {
                _glass.Background = new SolidColorBrush(fill);
                _glass.BorderBrush = new SolidColorBrush(border);
            }
            if (_titleBar is not null)
                _titleBar.Background = new SolidColorBrush(fill);

            var textBrush = new SolidColorBrush(text);
            if (_titleText is not null)
                _titleText.Foreground = textBrush;
            if (_hint is not null)
                _hint.Foreground = new SolidColorBrush(hint);

            // Live-update item labels already on screen (keep solid)
            if (_itemsPanel is not null)
            {
                foreach (var child in _itemsPanel.Children)
                {
                    if (child is not Border tile || tile.Child is not StackPanel sp) continue;
                    // Ensure tiles don't paint an opaque backdrop
                    if (tile.Background is not null && tile.Background != Brushes.Transparent
                        && tile.Background != TileSelectedBrush
                        && tile.Background != TileHoverBrush
                        && tile.Background != TileSelectedHoverBrush)
                        tile.Background = Brushes.Transparent;
                    foreach (var el in sp.Children)
                    {
                        if (el is TextBlock tb)
                            tb.Foreground = textBrush;
                        // Images stay fully opaque (default)
                    }
                }
            }
            if (_tabStrip is not null)
            {
                foreach (var child in _tabStrip.Children)
                {
                    if (child is Button btn)
                        btn.Foreground = textBrush;
                }
            }

            // NEVER call SetLayeredWindowAttributes here. WPF AllowsTransparency uses
            // per-pixel alpha; forcing LWA_ALPHA makes the entire window blank/invisible.
        }
        catch (Exception ex)
        {
            AppLog.Write($"ApplyGlassAppearance: {ex.Message}");
        }
    }

    /// <summary>
    /// Push appearance onto this window from the store (after a bulk update).
    /// Optional overrides force values onto the store model before painting.
    /// </summary>
    public void ApplyAppearanceFromStore(string? bgColor, string? textColor, double? opacity, bool persist = true)
    {
        var stored = _manager.LayoutStore.FindFence(_model.Id);
        if (stored is not null)
            _model = stored;

        if (bgColor is not null) _model.BgColor = bgColor;
        if (textColor is not null) _model.TextColor = textColor;
        if (opacity is not null) _model.Opacity = opacity.Value;

        if (persist)
            _manager.LayoutStore.UpdateFence(_model);
        ApplyGlassAppearance();
        if (textColor is not null)
            RefreshContent();
    }

    /// <summary>
    /// Re-apply panel (glass) alpha only — icons remain solid.
    /// Used by the opacity slider for live preview.
    /// </summary>
    private void ApplyWindowOpacity() => ApplyGlassAppearance();

    public void ResetColorsToDefault()
    {
        _model.BgColor = DefaultBgColor;
        _model.TextColor = DefaultTextColor;
        _manager.LayoutStore.UpdateFence(_model);
        ApplyGlassAppearance();
        RefreshContent();
    }

    public void RebuildContextMenu() => BuildContextMenu();

    public void UpdateLockChrome()
    {
        _model = _manager.LayoutStore.FindFence(_model.Id) ?? _model;
        // Only treat as grouped when 2+ fences share the id (orphans don't count)
        var grouped = _manager.IsEffectivelyGrouped(_model.Id);
        var groupName = grouped ? _manager.GetGroupName(_model.GroupId) : string.Empty;
        var prefix = (_model.Locked ? "🔒 " : "") + (grouped ? "⧉ " : "");
        _titleText.Text = string.IsNullOrWhiteSpace(groupName)
            ? prefix + _model.Title
            : prefix + groupName + " · " + _model.Title;
        _titleText.Opacity = _model.Locked ? 0.75 : 1.0;
        if (_titleBar is not null)
        {
            _titleBar.Cursor = _model.Locked ? Cursors.Arrow : Cursors.SizeAll;
            if (_model.Locked)
                _titleBar.ToolTip = "Locked — right-click to unlock";
            else if (_forceSoloDragNext && grouped)
                _titleBar.ToolTip = "Rearrange mode — drag to move this fence only (still grouped)";
            else if (grouped)
                _titleBar.ToolTip = string.IsNullOrWhiteSpace(groupName)
                    ? "Grouped — drag moves all · Alt+drag moves this fence only"
                    : $"Group \"{groupName}\" — drag moves all · Alt+drag moves this fence only";
            else
                _titleBar.ToolTip = "Double-click title to roll up · drag empty area to multi-select";
        }

        // Unlocked: white edge strips + corner grip visible, edges resizable
        // Locked (or rolled up): hide chrome completely, no resize
        if (_model.Locked || _model.RolledUp)
        {
            ResizeMode = ResizeMode.NoResize;
            _resizeChrome.Visibility = Visibility.Collapsed;
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(0),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });
        }
        else
        {
            ResizeMode = ResizeMode.CanResize;
            _resizeChrome.Visibility = Visibility.Visible;
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });
        }

        // Do not call ApplyGlassAppearance here (re-entrancy / style thrash).
        // Callers that change colors/opacity invoke it explicitly.
    }

    public void RefreshContent()
    {
        if (_deleteFxActive) return;
        _model = _manager.LayoutStore.FindFence(_model.Id) ?? _model;
        _model.EnsureDefaults();
        UpdateLockChrome();
        _itemsPanel.Children.Clear();
        _tabStrip.Children.Clear();
        _tileByPath.Clear();
        _itemOrder.Clear();
        // Keep selection only for paths that still exist after refresh
        var prevSel = _selectedPaths.ToList();
        _selectedPaths.Clear();

        List<FenceItem> items;
        if (_model.IsPortal)
            items = PortalService.GetPortalItems(_model.PortalPath).ToList();
        else
            items = _model.GetActiveTab()?.Items.ToList() ?? new List<FenceItem>();

        var showTabs = !_model.IsPortal && _model.Tabs.Count > 1 && !_model.RolledUp;
        _tabStrip.Visibility = showTabs ? Visibility.Visible : Visibility.Collapsed;
        if (showTabs)
        {
            foreach (var t in _model.Tabs)
            {
                var isActive = t.Id == _model.ActiveTabId;
                var tr = FenceManager.ParseHex(_model.TextColor);
                var tabId = t.Id;
                var btn = new Button
                {
                    Content = t.Title,
                    Padding = new Thickness(8, 2, 8, 2),
                    Margin = new Thickness(0, 0, 4, 0),
                    FontSize = 11,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    Tag = tabId,
                    Background = isActive
                        ? new SolidColorBrush(Color.FromArgb(60, 255, 255, 255))
                        : Brushes.Transparent,
                    Foreground = new SolidColorBrush(isActive
                        ? Color.FromRgb(tr.R, tr.G, tr.B)
                        : Color.FromArgb(160, tr.R, tr.G, tr.B)),
                    ToolTip = "Left-click to switch · Right-click to rename/delete"
                };
                btn.Click += (_, _) =>
                {
                    _model.ActiveTabId = tabId;
                    _manager.LayoutStore.UpdateFence(_model);
                    RefreshContent();
                };
                var tabCm = new ContextMenu();
                var renameTab = new MenuItem { Header = "Rename tab…" };
                renameTab.Click += (_, _) =>
                {
                    var name = FenceManager.PromptText("Rename tab", "Tab name:", t.Title);
                    if (string.IsNullOrWhiteSpace(name)) return;
                    var tab = _model.Tabs.FirstOrDefault(x => x.Id == tabId);
                    if (tab is null) return;
                    tab.Title = name.Trim();
                    _manager.LayoutStore.UpdateFence(_model);
                    RefreshContent();
                };
                tabCm.Items.Add(renameTab);
                var delTab = new MenuItem { Header = "Delete tab…" };
                delTab.Click += (_, _) => DeleteTab(tabId);
                tabCm.Items.Add(delTab);
                btn.ContextMenu = tabCm;
                _tabStrip.Children.Add(btn);
            }
        }

        if (items.Count == 0)
        {
            _hint.Visibility = Visibility.Visible;
            _hint.Text = _model.IsPortal
                ? (string.IsNullOrWhiteSpace(_model.PortalPath)
                    ? "No folder selected for portal"
                    : $"Portal is empty\n{_model.PortalPath}")
                : "Drop files here\nRight-click for options\nDrag to multi-select";
        }
        else
        {
            _hint.Visibility = Visibility.Collapsed;
            foreach (var it in items)
            {
                var path = it.Path;
                var label = string.IsNullOrWhiteSpace(it.Label)
                    ? _manager.Icons.GetDisplayLabel(path)
                    : it.Label!;
                _itemsPanel.Children.Add(CreateTile(path, label));
            }
            // Restore selection for surviving paths
            foreach (var p in prevSel)
            {
                if (_tileByPath.ContainsKey(p))
                    _selectedPaths.Add(p);
            }
            ApplySelectionVisuals();
        }

        if (_model.RolledUp) ApplyRollUp();
    }

    /// <summary>Swap recycle-bin tile artwork when the bin becomes empty or full.</summary>
    public void RefreshRecycleBinIcons()
    {
        var size = _manager.Icons.IconSize;
        foreach (var path in _itemOrder)
        {
            if (!DesktopIconService.IsRecycleBinPath(path)) continue;
            if (!_tileByPath.TryGetValue(path, out var border)) continue;
            if (border.Child is not StackPanel stack || stack.Children.Count == 0) continue;
            if (stack.Children[0] is not Image img) continue;
            img.Source = _manager.Icons.GetItemImage(path, size);
        }
    }

    private void DeleteTab(string tabId)
    {
        _model = _manager.LayoutStore.FindFence(_model.Id) ?? _model;
        if (_model.IsPortal)
        {
            MessageBox.Show("Portal fences do not use tabs.", "FenceDesk");
            return;
        }
        if (_model.Tabs.Count <= 1)
        {
            MessageBox.Show("Cannot delete the only tab.", "FenceDesk");
            return;
        }
        var tab = _model.Tabs.FirstOrDefault(t => t.Id == tabId);
        if (tab is null) return;
        var n = tab.Items.Count;
        var msg = n > 0
            ? $"Delete tab \"{tab.Title}\"?\n\n{n} item(s) on this tab will be removed from the fence (not from disk)."
            : $"Delete tab \"{tab.Title}\"?";
        if (MessageBox.Show(msg, "FenceDesk", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes)
            return;

        _model.Tabs.RemoveAll(t => t.Id == tabId);
        if (string.Equals(_model.ActiveTabId, tabId, StringComparison.Ordinal))
            _model.ActiveTabId = _model.Tabs[0].Id;
        _manager.LayoutStore.UpdateFence(_model);
        ClearSelection();
        RefreshContent();
        BuildContextMenu();
    }

    private static readonly System.Windows.Media.Brush TileHoverBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
    private static readonly System.Windows.Media.Brush TileSelectedBrush = new SolidColorBrush(Color.FromArgb(90, 80, 150, 255));
    private static readonly System.Windows.Media.Brush TileSelectedHoverBrush = new SolidColorBrush(Color.FromArgb(120, 80, 150, 255));

    private UIElement CreateTile(string path, string label)
    {
        var m = _manager.Icons;
        // No Margin — gaps are padding so the whole cell is hittable (avoids accidental marquee
        // when starting a drag near an icon).
        var border = new Border
        {
            Width = m.TileWidth + 4,
            Margin = new Thickness(0),
            Padding = new Thickness(6, 6, 6, 6),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Cursor = Cursors.Arrow,
            ToolTip = path,
            Tag = path
        };
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var img = new Image
        {
            Width = m.IconSize,
            Height = m.IconSize,
            Stretch = Stretch.Uniform,
            Source = _manager.Icons.GetItemImage(path, m.IconSize),
            IsHitTestVisible = false
        };
        var textRgb = FenceManager.ParseHex(_model.TextColor);
        var tb = new TextBlock
        {
            Text = label,
            FontSize = m.FontSize,
            Foreground = new SolidColorBrush(Color.FromRgb(textRgb.R, textRgb.G, textRgb.B)),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = m.LabelMaxHeight,
            Margin = new Thickness(0, 4, 0, 0),
            Width = Math.Max(48, m.TileWidth - 8),
            IsHitTestVisible = false
        };
        stack.Children.Add(img);
        stack.Children.Add(tb);
        border.Child = stack;

        _tileByPath[path] = border;
        _itemOrder.Add(path);

        border.MouseEnter += (_, _) =>
        {
            border.Background = _selectedPaths.Contains(path) ? TileSelectedHoverBrush : TileHoverBrush;
        };
        border.MouseLeave += (_, _) =>
        {
            border.Background = _selectedPaths.Contains(path) ? TileSelectedBrush : Brushes.Transparent;
        };

        // Double-click opens; drag moves item(s). Empty-area marquee multi-selects (desktop style).
        System.Windows.Point? dragOrigin = null;
        border.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;

            if (e.ClickCount >= 2)
            {
                dragOrigin = null;
                _manager.DesktopIcons.LaunchItem(path);
                e.Handled = true;
                return;
            }

            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

            if (ctrl)
            {
                // Ctrl+click: toggle this tile in the selection
                if (!_selectedPaths.Remove(path))
                    _selectedPaths.Add(path);
                _selectionAnchor = path;
                ApplySelectionVisuals();
            }
            else if (shift && _selectionAnchor is not null)
            {
                SelectRange(_selectionAnchor, path);
            }
            else
            {
                // Plain click: keep multi-selection if this tile is already selected (for group drag);
                // otherwise select only this tile.
                if (!_selectedPaths.Contains(path) || _selectedPaths.Count <= 1)
                {
                    _selectedPaths.Clear();
                    _selectedPaths.Add(path);
                    ApplySelectionVisuals();
                }
                _selectionAnchor = path;
            }

            dragOrigin = e.GetPosition(border);
            border.Focusable = true;
            border.Focus();
        };
        border.PreviewMouseLeftButtonUp += (_, _) => dragOrigin = null;
        border.PreviewMouseMove += (_, e) =>
        {
            if (dragOrigin is null || e.LeftButton != MouseButtonState.Pressed) return;
            if (_marqueeActive || _marqueePending) return;

            var pos = e.GetPosition(border);
            if (Math.Abs(pos.X - dragOrigin.Value.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - dragOrigin.Value.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            dragOrigin = null;
            // Drag all selected if this tile is selected; otherwise just this one
            var paths = _selectedPaths.Contains(path) && _selectedPaths.Count > 0
                ? _selectedPaths.ToList()
                : new List<string> { path };
            if (!_selectedPaths.Contains(path))
            {
                _selectedPaths.Clear();
                _selectedPaths.Add(path);
                ApplySelectionVisuals();
            }
            CancelMarquee();
            StartItemDrag(border, paths);
            e.Handled = true;
        };

        var cm = new ContextMenu();
        cm.Opened += (_, _) =>
        {
            // Right-click on unselected tile → select only it for context actions
            if (!_selectedPaths.Contains(path))
            {
                _selectedPaths.Clear();
                _selectedPaths.Add(path);
                ApplySelectionVisuals();
            }
        };
        var open = new MenuItem { Header = "Open" };
        open.Click += (_, _) => _manager.DesktopIcons.LaunchItem(path);
        cm.Items.Add(open);
        var explorer = new MenuItem { Header = "Show in Explorer" };
        explorer.Click += (_, _) =>
        {
            try
            {
                var rp = _manager.DesktopIcons.ResolveItemPath(path);
                if (DesktopIconService.IsShellNamespacePath(rp))
                {
                    _manager.DesktopIcons.LaunchItem(rp);
                    return;
                }
                if (File.Exists(rp) || Directory.Exists(rp))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{rp}\"",
                        UseShellExecute = true
                    });
                }
            }
            catch { /* ignore */ }
        };
        cm.Items.Add(explorer);
        cm.Items.Add(new Separator());
        if (_model.IsPortal)
        {
            var del = new MenuItem { Header = "Delete from folder…" };
            del.Click += (_, _) => DeleteSelectedOr(path);
            cm.Items.Add(del);
        }
        else
        {
            var remove = new MenuItem { Header = "Remove from fence" };
            remove.Click += (_, _) =>
            {
                var toRemove = _selectedPaths.Contains(path) && _selectedPaths.Count > 0
                    ? _selectedPaths.ToList()
                    : new List<string> { path };
                RemoveFromFenceWithFx(toRemove);
            };
            cm.Items.Add(remove);
        }
        border.ContextMenu = cm;
        return border;
    }

    private void ApplySelectionVisuals()
    {
        foreach (var (p, border) in _tileByPath)
            border.Background = _selectedPaths.Contains(p) ? TileSelectedBrush : Brushes.Transparent;
    }

    private void ClearSelection()
    {
        if (_selectedPaths.Count == 0) return;
        _selectedPaths.Clear();
        ApplySelectionVisuals();
    }

    private static Border? FindTileFromSource(DependencyObject? src)
    {
        var cur = src;
        while (cur is not null)
        {
            if (cur is Border b && b.Tag is string)
                return b;
            cur = VisualTreeHelper.GetParent(cur);
        }
        return null;
    }

    private void SelectRange(string fromPath, string toPath)
    {
        var i0 = _itemOrder.FindIndex(p => string.Equals(p, fromPath, StringComparison.OrdinalIgnoreCase));
        var i1 = _itemOrder.FindIndex(p => string.Equals(p, toPath, StringComparison.OrdinalIgnoreCase));
        if (i0 < 0 || i1 < 0)
        {
            _selectedPaths.Clear();
            _selectedPaths.Add(toPath);
            ApplySelectionVisuals();
            return;
        }
        if (i1 < i0) (i0, i1) = (i1, i0);
        _selectedPaths.Clear();
        for (var i = i0; i <= i1; i++)
            _selectedPaths.Add(_itemOrder[i]);
        ApplySelectionVisuals();
    }

    private void Body_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        // Clicks on icons select/drag the tile — marquee only from empty space (like desktop)
        if (FindTileFromSource(e.OriginalSource as DependencyObject) is not null)
            return;

        var additive = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        BeginPendingMarquee(e.GetPosition(_body), additive);
        e.Handled = true;
        Focus();
    }

    /// <summary>
    /// Arm marquee on empty-area press, but don't show/clear until the mouse actually moves
    /// past the system drag threshold — so icon drags aren't stolen by a selection box.
    /// </summary>
    private void BeginPendingMarquee(System.Windows.Point start, bool additive)
    {
        CancelMarquee();
        _marqueePending = true;
        _marqueeActive = false;
        _marqueeAdditive = additive;
        _marqueeStartBody = start;
        _marqueeBaseSelection = additive
            ? new HashSet<string>(_selectedPaths, StringComparer.OrdinalIgnoreCase)
            : null;
        _body.CaptureMouse();
    }

    private void ActivateMarquee()
    {
        if (!_marqueePending || _marqueeActive) return;
        _marqueePending = false;
        _marqueeActive = true;

        if (!_marqueeAdditive)
        {
            _selectedPaths.Clear();
            ApplySelectionVisuals();
        }

        if (_marqueeVisual is null)
        {
            _marqueeVisual = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 150, 255)),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromArgb(50, 80, 150, 255)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Width = 0,
                Height = 0
            };
            _body.Children.Add(_marqueeVisual);
        }
        _marqueeVisual.Visibility = Visibility.Visible;
        _marqueeVisual.Margin = new Thickness(_marqueeStartBody.X, _marqueeStartBody.Y, 0, 0);
        _marqueeVisual.Width = 0;
        _marqueeVisual.Height = 0;
    }

    private void Body_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if ((!_marqueePending && !_marqueeActive) || e.LeftButton != MouseButtonState.Pressed)
            return;

        var pos = e.GetPosition(_body);
        if (_marqueePending)
        {
            if (Math.Abs(pos.X - _marqueeStartBody.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _marqueeStartBody.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            ActivateMarquee();
        }

        if (_marqueeActive)
        {
            UpdateMarquee(pos);
            e.Handled = true;
        }
    }

    private void Body_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_marqueePending && !_marqueeActive) return;

        if (_marqueePending)
        {
            // Click empty area without dragging → clear selection (desktop behavior)
            var additive = _marqueeAdditive;
            CancelMarquee();
            if (!additive)
                ClearSelection();
            e.Handled = true;
            return;
        }

        UpdateMarquee(e.GetPosition(_body));
        EndMarquee(commit: true);
        e.Handled = true;
    }

    private void UpdateMarquee(System.Windows.Point current)
    {
        if (_marqueeVisual is null) return;
        var x = Math.Min(_marqueeStartBody.X, current.X);
        var y = Math.Min(_marqueeStartBody.Y, current.Y);
        var w = Math.Abs(current.X - _marqueeStartBody.X);
        var h = Math.Abs(current.Y - _marqueeStartBody.Y);
        _marqueeVisual.Margin = new Thickness(x, y, 0, 0);
        _marqueeVisual.Width = Math.Max(0, w);
        _marqueeVisual.Height = Math.Max(0, h);

        var box = new Rect(x, y, Math.Max(1, w), Math.Max(1, h));
        var hit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, tile) in _tileByPath)
        {
            try
            {
                // Tile lives inside ScrollViewer — TranslatePoint accounts for scroll offset
                var topLeft = tile.TranslatePoint(new System.Windows.Point(0, 0), _body);
                var tileRect = new Rect(topLeft.X, topLeft.Y,
                    Math.Max(1, tile.ActualWidth), Math.Max(1, tile.ActualHeight));
                if (box.IntersectsWith(tileRect))
                    hit.Add(path);
            }
            catch { /* ignore layout race */ }
        }

        _selectedPaths.Clear();
        if (_marqueeAdditive && _marqueeBaseSelection is not null)
        {
            foreach (var p in _marqueeBaseSelection)
                _selectedPaths.Add(p);
        }
        foreach (var p in hit)
            _selectedPaths.Add(p);
        ApplySelectionVisuals();
    }

    private void EndMarquee(bool commit)
    {
        if (!_marqueeActive && !_marqueePending) return;
        _marqueePending = false;
        _marqueeActive = false;
        _marqueeAdditive = false;
        _marqueeBaseSelection = null;
        try { if (_body.IsMouseCaptured) _body.ReleaseMouseCapture(); } catch { /* ignore */ }
        if (_marqueeVisual is not null)
            _marqueeVisual.Visibility = Visibility.Collapsed;
        if (!commit)
            ClearSelection();
    }

    private void CancelMarquee()
    {
        if (!_marqueeActive && !_marqueePending) return;
        EndMarquee(commit: true);
    }

    private void DeleteSelectedOr(string fallbackPath)
    {
        if (_deleteFxActive) return;
        var paths = _selectedPaths.Count > 0 ? _selectedPaths.ToList() : new List<string> { fallbackPath };
        var resolved = _manager.CollectDeletablePaths(paths);
        if (resolved.Count == 0) return;
        if (!_manager.ConfirmPermanentDelete(resolved)) return;

        PlayDeleteFx(paths, () =>
        {
            _manager.DeleteFromDisk(resolved, confirm: false);
            _selectedPaths.Clear();
            RefreshContent();
        });
    }

    private void RemoveFromFenceWithFx(IReadOnlyList<string> paths)
    {
        if (_deleteFxActive || paths.Count == 0) return;
        PlayDeleteFx(paths, () =>
        {
            _manager.RemoveItems(_model.Id, paths);
            _selectedPaths.Clear();
        });
    }

    private void PlayDeleteFx(IReadOnlyList<string> paths, Action commit)
    {
        var tiles = new List<Border>();
        foreach (var p in paths)
        {
            if (_tileByPath.TryGetValue(p, out var tile))
                tiles.Add(tile);
        }

        if (tiles.Count == 0)
        {
            commit();
            return;
        }

        _deleteFxActive = true;
        var remaining = tiles.Count;
        var iconPx = (double)_manager.Icons.IconSize;
        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            var icon = GetTileIcon(tile);
            BlackHoleDeleteFx.Play(_fxLayer, tile, icon, iconPx, TimeSpan.FromMilliseconds(70 * i), () =>
            {
                remaining--;
                if (remaining > 0) return;
                _deleteFxActive = false;
                _fxLayer.Children.Clear();
                commit();
            });
        }
    }

    private static ImageSource? GetTileIcon(Border tile)
    {
        if (tile.Child is StackPanel sp &&
            sp.Children.Count > 0 &&
            sp.Children[0] is Image img)
            return img.Source;
        return null;
    }

    private void StartItemDrag(FrameworkElement source, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        try
        {
            CancelMarquee();
            LastDropFenceId = null;
            DragSourceFenceId = _model.Id;
            AppLog.Write($"StartItemDrag: {paths.Count} item(s) from {_model.Title}");
            var data = new System.Windows.DataObject();
            // Newline-separated so multi-item drops work between fences
            data.SetData(FenceItemFormat, string.Join("\n", paths));
            data.SetData(DataFormats.StringFormat, paths.Count == 1 ? paths[0] : string.Join(Environment.NewLine, paths));

            if (_model.IsPortal)
            {
                // Portal items are real files in a folder — FileDrop lets Explorer move/copy them out.
                var sc = new StringCollection();
                foreach (var path in paths)
                {
                    var resolved = _manager.DesktopIcons.ResolveItemPath(path);
                    if ((File.Exists(resolved) || Directory.Exists(resolved)) &&
                        !DesktopIconService.IsShellNamespacePath(resolved))
                        sc.Add(resolved);
                }
                if (sc.Count > 0)
                    data.SetFileDropList(sc);

                DragDrop.DoDragDrop(source, data, DragDropEffects.Copy | DragDropEffects.Move);
                // Watcher refreshes the portal list; do not strip manually.
                return;
            }

            // Manual fence items are references (often desktop shortcuts that were shelved/hidden).
            // Only offer FileDrop when the real file is OFF the desktop (shelved folder).
            // If we FileDrop a path that still lives on Desktop, Explorer shows:
            //   "The destination folder is the same as the source folder."
            // For still-on-desktop items, fence-only DnD + RemoveItems/RestoreDesktopIcons is correct.
            var fileDrop = new StringCollection();
            foreach (var path in paths)
            {
                if (!_manager.DesktopIcons.IsOffDesktopFile(path)) continue;
                var resolved = _manager.DesktopIcons.ResolveItemPath(path);
                fileDrop.Add(resolved);
            }
            if (fileDrop.Count > 0)
                data.SetFileDropList(fileDrop);

            // Prefer Move only for inter-fence; Copy|Move when we have a real FileDrop so Explorer
            // can place shelved files. Without FileDrop, Move alone is fine (custom format).
            var effects = fileDrop.Count > 0
                ? DragDropEffects.Copy | DragDropEffects.Move
                : DragDropEffects.Move;
            var result = DragDrop.DoDragDrop(source, data, effects);

            var droppedOnSelf = string.Equals(LastDropFenceId, _model.Id, StringComparison.Ordinal);
            if (droppedOnSelf) return;

            // Released outside this fence → leave fence and restore desktop icons.
            // Explorer "None" effect on desktop is normal when we only used FenceItemFormat.
            if (LastDropFenceId is not null || result != DragDropEffects.None || !IsCursorOverThisWindow())
                _manager.RemoveItems(_model.Id, paths);
        }
        catch (Exception ex)
        {
            AppLog.Write($"StartItemDrag: {ex.Message}");
        }
        finally
        {
            LastDropFenceId = null;
            DragSourceFenceId = null;
            HideInsertMarker();
        }
    }

    private bool IsCursorOverThisWindow()
    {
        try
        {
            // MousePosition is physical pixels; WPF Left/Width are DIPs — convert for Win11 DPI
            var screen = System.Windows.Forms.Control.MousePosition;
            var src = PresentationSource.FromVisual(this);
            System.Windows.Point wpfPt;
            if (src?.CompositionTarget is not null)
            {
                var fromDevice = src.CompositionTarget.TransformFromDevice;
                wpfPt = fromDevice.Transform(new System.Windows.Point(screen.X, screen.Y));
            }
            else
            {
                wpfPt = new System.Windows.Point(screen.X, screen.Y);
            }

            return wpfPt.X >= Left && wpfPt.X <= Left + ActualWidth
                   && wpfPt.Y >= Top && wpfPt.Y <= Top + ActualHeight;
        }
        catch { return false; }
    }

    private void BuildContextMenu()
    {
        // Always rebuild from live model so group/ungroup state is current
        _model = _manager.LayoutStore.FindFence(_model.Id) ?? _model;

        // Use default system menu chrome — partial custom colors leave
        // submenu/hover panels white with light text (unreadable).
        var cm = new ContextMenu();

        void Add(string header, Action action, ItemCollection? into = null)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) =>
            {
                try { action(); } catch (Exception ex) { AppLog.Write(ex.Message); }
            };
            (into ?? cm.Items).Add(mi);
        }

        MenuItem Sub(string header)
        {
            var mi = new MenuItem { Header = header };
            cm.Items.Add(mi);
            return mi;
        }

        Add("Rename…", () =>
        {
            var name = FenceManager.PromptText("Rename fence", "Fence name:", _model.Title);
            if (string.IsNullOrWhiteSpace(name)) return;
            _model.Title = name.Trim();
            _manager.LayoutStore.UpdateFence(_model);
            RefreshContent();
        });
        Add(_model.RolledUp ? "Expand" : "Roll up", ToggleRollUp);

        if (!_model.IsPortal)
        {
            var tabs = Sub("Tabs");
            Add("Add tab…", () =>
            {
                var name = FenceManager.PromptText("Add tab", "Tab name:", "New tab");
                if (string.IsNullOrWhiteSpace(name)) return;
                var tid = Guid.NewGuid().ToString();
                _model.Tabs.Add(new FenceTab { Id = tid, Title = name.Trim() });
                _model.ActiveTabId = tid;
                _manager.LayoutStore.UpdateFence(_model);
                RefreshContent();
                BuildContextMenu();
            }, tabs.Items);
            if (_model.Tabs.Count > 1)
            {
                Add("Delete current tab…", () => DeleteTab(_model.ActiveTabId), tabs.Items);
            }
        }

        cm.Items.Add(new Separator());
        Add("Appearance…", () => ShowAppearanceDialog());

        if (_model.IsPortal)
        {
            Add("Convert to items", () =>
            {
                _manager.Portals.Unregister(_model.Id);
                _model.Mode = "items";
                _model.PortalPath = null;
                _manager.LayoutStore.UpdateFence(_model);
                RefreshContent();
                _manager.DesktopIcons.SyncVisibility();
            });
        }
        else
        {
            Add("Convert to portal…", () =>
            {
                var folder = FenceManager.PickFolder("Select folder for portal");
                if (folder is null) return;
                _model.Mode = "portal";
                _model.PortalPath = folder;
                _model.Title = Path.GetFileName(folder);
                if (string.IsNullOrWhiteSpace(_model.Title)) _model.Title = folder;
                _manager.LayoutStore.UpdateFence(_model);
                _manager.Portals.Register(_model.Id, folder, id =>
                {
                    if (_manager.Windows.TryGetValue(id, out var w)) w.RefreshContent();
                });
                RefreshContent();
                _manager.DesktopIcons.SyncVisibility();
            });

            var shellMenu = Sub("Add desktop icon");
            foreach (var kv in DesktopIconService.ShellDesktopIcons.OrderBy(k => k.Value.Name))
            {
                var info = kv.Value;
                var path = info.Path;
                var mi = new MenuItem { Header = info.Name };
                mi.Click += (_, _) =>
                {
                    try { _manager.AddItems(_model.Id, new[] { path }); }
                    catch (Exception ex) { AppLog.Write(ex.Message); }
                };
                shellMenu.Items.Add(mi);
            }
        }

        cm.Items.Add(new Separator());
        Add(_model.Locked ? "Unlock position" : "Lock position", () =>
        {
            _manager.SetFenceLocked(_model.Id, !_model.Locked);
            BuildContextMenu();
        });

        var group = Sub("Group");
        Add("Group with…", () =>
        {
            var target = _manager.PickFenceToGroupWith(_model.Id);
            if (target is null) return;
            _manager.JoinFenceGroup(_model.Id, target);
        }, group.Items);

        if (_manager.IsEffectivelyGrouped(_model.Id))
        {
            Add("Rename group…", () =>
            {
                var current = _manager.GetGroupName(_model.GroupId);
                var name = FenceManager.PromptText(
                    "Rename group",
                    "Group name (leave blank to clear):",
                    current);
                if (name is null) return;
                _manager.SetGroupName(_model.GroupId!, name);
            }, group.Items);
            Add("Rearrange this fence only", () =>
            {
                if (_model.Locked)
                {
                    MessageBox.Show(
                        "Unlock this fence first to rearrange it.",
                        "FenceDesk",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }
                _forceSoloDragNext = true;
                UpdateLockChrome();
            }, group.Items);
            Add("Ungroup this fence", () => _manager.LeaveFenceGroup(_model.Id), group.Items);
            Add("Ungroup all", () => _manager.DissolveFenceGroup(_model.Id), group.Items);
            Add("Match size to this fence", () => _manager.MatchGroupSize(_model.Id), group.Items);
        }
        else if (!string.IsNullOrWhiteSpace(_model.GroupId))
        {
            Add("Clear stuck group link", () => _manager.LeaveFenceGroup(_model.Id), group.Items);
        }

        cm.Items.Add(new Separator());
        Add("New fence", () => _manager.NewFenceFromTray());
        Add("Delete fence", () =>
        {
            if (MessageBox.Show($"Delete fence \"{_model.Title}\"?", "FenceDesk",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                _manager.RemoveFence(_model.Id);
        });

        ContextMenu = cm;
    }

    public void ShowAppearanceDialog(bool applyToAllByDefault = false, bool showFencePicker = false)
    {
        _model = _manager.LayoutStore.FindFence(_model.Id) ?? _model;

        var originals = _manager.LayoutStore.Layout.Fences.ToDictionary(
            f => f.Id,
            f => (
                Bg: string.IsNullOrWhiteSpace(f.BgColor) ? DefaultBgColor : f.BgColor,
                Text: string.IsNullOrWhiteSpace(f.TextColor) ? DefaultTextColor : f.TextColor,
                Opacity: Math.Clamp(f.Opacity, 0.15, 1.0)
            ));

        var targetId = _model.Id;
        string draftBg;
        string draftText;
        double draftOpacity;
        if (originals.TryGetValue(targetId, out var seed))
        {
            draftBg = seed.Bg;
            draftText = seed.Text;
            draftOpacity = seed.Opacity;
        }
        else
        {
            draftBg = DefaultBgColor;
            draftText = DefaultTextColor;
            draftOpacity = 0.72;
        }

        var win = new Window
        {
            Title = "Appearance",
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(22, 30, 44)),
            ShowInTaskbar = false,
            Topmost = true
        };

        var root = new StackPanel { Margin = new Thickness(18) };
        var labelFg = new SolidColorBrush(Color.FromRgb(200, 208, 220));
        var btnBg = new SolidColorBrush(Color.FromRgb(40, 54, 74));
        var btnFg = new SolidColorBrush(Color.FromRgb(220, 230, 245));
        var btnBorder = new SolidColorBrush(Color.FromRgb(70, 96, 130));
        var swatchPaints = new List<Action>();
        var suppressPreview = false;

        var applyAll = new System.Windows.Controls.CheckBox
        {
            Content = "Apply to all fences",
            Foreground = labelFg,
            Margin = new Thickness(0, 0, 0, 14),
            IsChecked = applyToAllByDefault
        };

        var valueLbl = new TextBlock
        {
            Text = $"{(int)Math.Round(draftOpacity * 100)}%",
            Foreground = labelFg,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4)
        };

        var slider = new Slider
        {
            Minimum = 15,
            Maximum = 100,
            TickFrequency = 5,
            IsSnapToTickEnabled = false,
            Value = Math.Clamp(Math.Round(draftOpacity * 100), 15, 100),
            Margin = new Thickness(0, 0, 0, 12)
        };

        void PushPreview()
        {
            if (suppressPreview) return;
            var o = slider.Value / 100.0;
            draftOpacity = o;
            if (applyAll.IsChecked == true)
                _manager.PreviewAppearance(draftBg, draftText, o);
            else
                _manager.PreviewAppearance(draftBg, draftText, o, onlyFenceId: targetId);
        }

        void SyncControlsFromDraft()
        {
            suppressPreview = true;
            slider.Value = Math.Clamp(Math.Round(draftOpacity * 100), 15, 100);
            valueLbl.Text = $"{(int)slider.Value}%";
            suppressPreview = false;
            foreach (var paint in swatchPaints) paint();
        }

        if (showFencePicker)
        {
            root.Children.Add(new TextBlock
            {
                Text = "Fence",
                FontWeight = FontWeights.SemiBold,
                Foreground = labelFg,
                Margin = new Thickness(0, 0, 0, 6)
            });

            var fenceBox = new System.Windows.Controls.ComboBox
            {
                Margin = new Thickness(0, 0, 0, 14),
                Background = btnBg,
                Foreground = btnFg,
                BorderBrush = btnBorder,
                Padding = new Thickness(8, 6, 8, 6)
            };

            string FenceLabel(FenceModel f)
            {
                var title = string.IsNullOrWhiteSpace(f.Title) ? "Untitled" : f.Title;
                if (!string.IsNullOrWhiteSpace(f.GroupId) && _manager.IsEffectivelyGrouped(f.Id))
                {
                    var g = _manager.GetGroupName(f.GroupId);
                    if (!string.IsNullOrWhiteSpace(g))
                        return $"{g} · {title}";
                }
                return title;
            }

            System.Windows.Controls.ComboBoxItem? selectItem = null;
            foreach (var f in _manager.LayoutStore.Layout.Fences.OrderBy(FenceLabel))
            {
                var item = new System.Windows.Controls.ComboBoxItem
                {
                    Content = FenceLabel(f),
                    Tag = f.Id
                };
                fenceBox.Items.Add(item);
                if (f.Id == targetId) selectItem = item;
            }
            fenceBox.SelectedItem = selectItem ?? fenceBox.Items.OfType<System.Windows.Controls.ComboBoxItem>().FirstOrDefault();

            fenceBox.SelectionChanged += (_, _) =>
            {
                if (fenceBox.SelectedItem is not System.Windows.Controls.ComboBoxItem item)
                    return;
                if (item.Tag is not string newId || newId == targetId)
                    return;

                var prevId = targetId;
                targetId = newId;

                if (applyAll.IsChecked == true)
                    return;

                _manager.RestoreAppearanceSnapshot(originals, onlyFenceId: prevId);
                if (originals.TryGetValue(targetId, out var snap))
                {
                    draftBg = snap.Bg;
                    draftText = snap.Text;
                    draftOpacity = snap.Opacity;
                    SyncControlsFromDraft();
                }
                PushPreview();
            };
            root.Children.Add(fenceBox);
        }

        void AddSwatchRow(string label, Func<string> getHex, Action<string> setHex)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            var swatch = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(4),
                BorderBrush = new SolidColorBrush(Color.FromRgb(90, 110, 140)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            void PaintSwatch()
            {
                var rgb = FenceManager.ParseHex(getHex());
                swatch.Background = new SolidColorBrush(Color.FromRgb(rgb.R, rgb.G, rgb.B));
            }
            PaintSwatch();
            swatchPaints.Add(PaintSwatch);
            var btn = new Button
            {
                Content = label,
                Padding = new Thickness(12, 7, 12, 7),
                Background = btnBg,
                Foreground = btnFg,
                BorderBrush = btnBorder,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                MinWidth = 180
            };
            btn.Click += (_, _) =>
            {
                var c = FenceManager.PickColor(getHex());
                if (c is null) return;
                setHex(FenceManager.ToHex(c.Value.R, c.Value.G, c.Value.B));
                PaintSwatch();
            };
            row.Children.Add(swatch);
            row.Children.Add(btn);
            root.Children.Add(row);
        }

        root.Children.Add(new TextBlock
        {
            Text = "Colors",
            FontWeight = FontWeights.SemiBold,
            Foreground = labelFg,
            Margin = new Thickness(0, 0, 0, 10)
        });

        AddSwatchRow("Background color…", () => draftBg, hex =>
        {
            draftBg = hex;
            PushPreview();
        });

        AddSwatchRow("Text color…", () => draftText, hex =>
        {
            draftText = hex;
            PushPreview();
        });

        root.Children.Add(new TextBlock
        {
            Text = "Opacity",
            FontWeight = FontWeights.SemiBold,
            Foreground = labelFg,
            Margin = new Thickness(0, 8, 0, 6)
        });
        root.Children.Add(new TextBlock
        {
            Text = "Panel only — icons stay solid",
            Foreground = new SolidColorBrush(Color.FromRgb(150, 162, 180)),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 6)
        });

        root.Children.Add(valueLbl);
        slider.ValueChanged += (_, _) =>
        {
            valueLbl.Text = $"{(int)slider.Value}%";
            PushPreview();
        };
        root.Children.Add(slider);

        applyAll.Checked += (_, _) => PushPreview();
        applyAll.Unchecked += (_, _) =>
        {
            // Put every other fence back; keep draft on the selected target
            foreach (var id in originals.Keys)
            {
                if (id == targetId) continue;
                _manager.RestoreAppearanceSnapshot(originals, onlyFenceId: id);
            }
            PushPreview();
        };
        root.Children.Add(applyAll);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var reset = new Button
        {
            Content = "Reset",
            Width = 72,
            Margin = new Thickness(0, 0, 8, 0),
            Background = btnBg,
            Foreground = btnFg,
            BorderBrush = btnBorder,
            Padding = new Thickness(0, 6, 0, 6)
        };
        reset.Click += (_, _) =>
        {
            draftBg = DefaultBgColor;
            draftText = DefaultTextColor;
            draftOpacity = 0.72;
            SyncControlsFromDraft();
            PushPreview();
        };
        var ok = new Button
        {
            Content = "OK",
            Width = 72,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true,
            Background = new SolidColorBrush(Color.FromRgb(50, 90, 140)),
            Foreground = btnFg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(80, 130, 190)),
            Padding = new Thickness(0, 6, 0, 6)
        };
        ok.Click += (_, _) =>
        {
            var applyToAll = applyAll.IsChecked == true;
            var o = slider.Value / 100.0;

            if (applyToAll)
            {
                _manager.SetAllAppearance(draftBg, draftText, o);
            }
            else
            {
                // Ensure non-target fences are back to originals, then commit target
                foreach (var id in originals.Keys)
                {
                    if (id == targetId) continue;
                    _manager.RestoreAppearanceSnapshot(originals, onlyFenceId: id);
                }
                var target = _manager.LayoutStore.FindFence(targetId);
                if (target is not null)
                {
                    target.BgColor = draftBg;
                    target.TextColor = draftText;
                    target.Opacity = o;
                    _manager.LayoutStore.UpdateFence(target);
                    if (_manager.Windows.TryGetValue(targetId, out var tw))
                        tw.ApplyAppearanceFromStore(draftBg, draftText, o);
                }
                _manager.LayoutStore.SaveImmediate();
            }

            win.DialogResult = true;
        };
        var cancel = new Button
        {
            Content = "Cancel",
            Width = 72,
            IsCancel = true,
            Background = btnBg,
            Foreground = btnFg,
            BorderBrush = btnBorder,
            Padding = new Thickness(0, 6, 0, 6)
        };
        cancel.Click += (_, _) =>
        {
            _manager.RestoreAppearanceSnapshot(originals);
            _manager.LayoutStore.SaveImmediate();
            win.Close();
        };
        buttons.Children.Add(reset);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        win.Content = root;
        win.ShowDialog();
    }


    private void EmptyArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Ignore if click originated on an item tile
        if (FindTileFromSource(e.OriginalSource as DependencyObject) is not null)
            return;

        // Marquee on body Preview already owns empty-area left-clicks
        if (_marqueeActive || _marqueePending) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            return;

        // Single-click empty area: clear multi-selection (no roll-up on double-click)
        if (e.ClickCount >= 1)
            ClearSelection();
    }

    private void FenceWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ClearSelection();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            _selectedPaths.Clear();
            foreach (var p in _itemOrder) _selectedPaths.Add(p);
            if (_itemOrder.Count > 0)
                _selectionAnchor = _itemOrder[0];
            ApplySelectionVisuals();
            e.Handled = true;
            return;
        }
        if (e.Key is Key.Delete or Key.Back)
        {
            if (_selectedPaths.Count == 0 || _deleteFxActive) return;
            if (_model.IsPortal)
                DeleteSelectedOr(_selectedPaths.First());
            else
                RemoveFromFenceWithFx(_selectedPaths.ToList());
            e.Handled = true;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            ToggleRollUp();
            e.Handled = true;
            return;
        }
        _model = _manager.LayoutStore.FindFence(_model.Id) ?? _model;
        if (_model.Locked)
        {
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;

        // Group-aware drag (moves linked fences together when effectively grouped).
        // Alt+drag or "Rearrange this fence only" moves just this fence while staying grouped.
        _model = _manager.LayoutStore.FindFence(_model.Id) ?? _model;
        _groupDragging = true;
        _soloGroupDrag = _forceSoloDragNext
            || (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
        _forceSoloDragNext = false;
        if (_soloGroupDrag) UpdateLockChrome();
        _groupDragScreenStart = PointToScreen(e.GetPosition(this));
        _groupDragOrigins.Clear();
        var toMove = _soloGroupDrag
            ? new[] { _model }
            : _manager.GetLinkedFences(_model.Id);
        foreach (var f in toMove)
        {
            if (_manager.Windows.TryGetValue(f.Id, out var win))
                _groupDragOrigins[f.Id] = (win.Left, win.Top);
        }
        _titleBar.CaptureMouse();
        e.Handled = true;
    }

    private void TitleBar_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_groupDragging || e.LeftButton != MouseButtonState.Pressed) return;
        var screen = PointToScreen(e.GetPosition(this));
        var dx = screen.X - _groupDragScreenStart.X;
        var dy = screen.Y - _groupDragScreenStart.Y;

        _suppressGeometry = true;
        try
        {
            foreach (var (id, origin) in _groupDragOrigins)
            {
                if (!_manager.Windows.TryGetValue(id, out var win)) continue;
                var m = _manager.LayoutStore.FindFence(id);
                if (m?.Locked == true) continue;
                win.Left = origin.L + dx;
                win.Top = origin.T + dy;
            }
        }
        finally { _suppressGeometry = false; }
    }

    private void TitleBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_groupDragging) return;
        EndGroupDrag(snap: true);
        e.Handled = true;
    }

    private void EndGroupDrag(bool snap)
    {
        if (!_groupDragging) return;
        _groupDragging = false;
        var solo = _soloGroupDrag;
        _soloGroupDrag = false;
        try { _titleBar.ReleaseMouseCapture(); } catch { /* ignore */ }

        if (snap && !_model.Locked)
        {
            var (sl, st) = _manager.GetSnappedPosition(
                _model.Id, Left, Top, ActualWidth, ActualHeight,
                allowGroupSiblingSnap: solo);
            var sdx = sl - Left;
            var sdy = st - Top;
            if (Math.Abs(sdx) > 0.01 || Math.Abs(sdy) > 0.01)
            {
                _suppressGeometry = true;
                try
                {
                    foreach (var id in _groupDragOrigins.Keys)
                    {
                        if (!_manager.Windows.TryGetValue(id, out var win)) continue;
                        var m = _manager.LayoutStore.FindFence(id);
                        if (m?.Locked == true) continue;
                        win.Left += sdx;
                        win.Top += sdy;
                    }
                }
                finally { _suppressGeometry = false; }
            }
        }

        if (solo)
            PushGeometryToModel();
        else
            _manager.SyncGroupGeometryFromLeader(_model.Id);
        _groupDragOrigins.Clear();
        if (solo) UpdateLockChrome();
    }

    private void ToggleRollUp()
    {
        _model.RolledUp = !_model.RolledUp;
        ApplyRollUp();
        _manager.LayoutStore.UpdateFence(_model);
        UpdateLockChrome();
    }

    private void ApplyRollUp()
    {
        _suppressGeometry = true;
        try
        {
            if (_model.RolledUp)
            {
                if (ActualHeight > 40) _expandedHeight = ActualHeight;
                _body.Visibility = Visibility.Collapsed;
                _tabStrip.Visibility = Visibility.Collapsed;
                MinHeight = 28;
                Height = 32;
            }
            else
            {
                var restore = _expandedHeight > 40 ? _expandedHeight : Math.Max(80, _model.Height);
                _body.Visibility = Visibility.Visible;
                MinHeight = 80;
                Height = Math.Max(80, restore);
                _expandedHeight = Height;
                var showTabs = !_model.IsPortal && _model.Tabs.Count > 1;
                _tabStrip.Visibility = showTabs ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        finally
        {
            _suppressGeometry = false;
            UpdateLockChrome();
        }
    }

    private void SyncGeometry()
    {
        if (!_readyToSync || _suppressGeometry || ToggleHidden || _groupDragging) return;
        // Never persist off-screen park coords
        if (Left < -5000 || Top < -5000) return;
        // Ignore resize while locked
        if (_model.Locked)
        {
            // Still allow nothing — position locked
            return;
        }
        _model.X = Left;
        _model.Y = Top;
        _model.Width = Width;
        if (!_model.RolledUp && Height > 40)
        {
            _model.Height = Height;
            _expandedHeight = Height;
        }
        _manager.LayoutStore.UpdateFence(_model);
    }
}
