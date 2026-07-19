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
    private double _expandedHeight;
    private bool _readyToSync;
    private bool _suppressGeometry;
    private int _restX, _restY, _restW, _restH;
    private bool _hasRestRect;
    private Border _titleBar = null!;
    private bool _groupDragging;
    private System.Windows.Point _groupDragScreenStart;
    private readonly Dictionary<string, (double L, double T)> _groupDragOrigins = new();

    // Multi-select state (paths of selected tiles) — marquee via Ctrl+drag
    private readonly HashSet<string> _selectedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Border> _tileByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _itemOrder = new();
    private bool _marqueeActive;
    private System.Windows.Point _marqueeStartBody;
    private Border? _marqueeVisual;

    public bool ToggleHidden { get; set; }
    public string FenceId => _model.Id;

    /// <summary>True when fence is visually shown (not soft-hidden).</summary>
    public bool IsOnScreen => !ToggleHidden;

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
            ToolTip = "Double-click title to roll up · Ctrl+drag to multi-select"
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
        shell.Children.Add(_glass);
        shell.Children.Add(content);
        shell.Children.Add(_resizeChrome);
        Content = shell;

        // Colors / text / opacity — must run after title bar, hint, panels exist
        ApplyGlassAppearance();

        // Events
        titleBar.PreviewMouseLeftButtonDown += TitleBar_MouseLeftButtonDown;
        titleBar.PreviewMouseMove += TitleBar_MouseMove;
        titleBar.PreviewMouseLeftButtonUp += TitleBar_MouseLeftButtonUp;
        titleBar.LostMouseCapture += (_, _) => EndGroupDrag(snap: true);
        // Empty body: clear selection. Ctrl+drag: marquee multi-select (no roll-up on double-click).
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
        Drop += OnDrop;

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
        if (e.Data.GetDataPresent(DataFormats.FileDrop) ||
            e.Data.GetDataPresent(FenceItemFormat) ||
            e.Data.GetDataPresent(DataFormats.StringFormat))
        {
            // Prefer Move when reordering/transferring fence items; Copy for OS file drops
            e.Effects = e.Data.GetDataPresent(FenceItemFormat)
                ? DragDropEffects.Move
                : DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        try
        {
            var paths = ExtractDropPaths(e.Data);
            if (paths.Count == 0) return;

            LastDropFenceId = _model.Id;
            _manager.AddItems(_model.Id, paths);
            e.Effects = e.Data.GetDataPresent(FenceItemFormat)
                ? DragDropEffects.Move
                : DragDropEffects.Copy;
        }
        catch (Exception ex)
        {
            AppLog.Write($"Drop: {ex.Message}");
        }
        e.Handled = true;
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
                var t = line.Trim().Trim('"');
                if (t.Length == 0) continue;
                if (!paths.Contains(t, StringComparer.OrdinalIgnoreCase))
                    paths.Add(t);
            }
        }
        try
        {
            if (data.GetDataPresent(FenceItemFormat))
                AddPath(data.GetData(FenceItemFormat) as string);
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
                    (s.Contains('\\') || s.StartsWith("::", StringComparison.Ordinal) || s.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)))
                    AddPath(s);
            }
        }
        catch { /* ignore */ }
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
                DesktopPin.EnsureToolWindowStyles(hwnd);
                // Do NOT SetLayeredWindowAttributes — blanks AllowsTransparency windows
                var x = (int)Math.Round(Left);
                var y = (int)Math.Round(Top);
                var w = (int)Math.Round(Math.Max(MinWidth, ActualWidth > 1 ? ActualWidth : Width));
                var h = (int)Math.Round(Math.Max(MinHeight, ActualHeight > 1 ? ActualHeight : Height));
                NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_NOTOPMOST, x, y, w, h,
                    NativeMethods.SWP_NOACTIVATE
                    | NativeMethods.SWP_SHOWWINDOW
                    | NativeMethods.SWP_NOSENDCHANGING);
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
            // Prefer live _model fields (opacity dialog previews before save).
            // Only pull missing/stale non-appearance state from the store.
            var stored = _manager.LayoutStore.FindFence(_model.Id);
            if (stored is not null && !ReferenceEquals(stored, _model))
            {
                // Keep appearance values currently on _model (may be mid-edit)
                var op = _model.Opacity;
                var bg = _model.BgColor;
                var tc = _model.TextColor;
                _model = stored;
                _model.Opacity = op;
                _model.BgColor = bg;
                _model.TextColor = tc;
            }

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

    public void UpdateLockChrome()
    {
        _model = _manager.LayoutStore.FindFence(_model.Id) ?? _model;
        var grouped = !string.IsNullOrWhiteSpace(_model.GroupId);
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
            else if (grouped)
                _titleBar.ToolTip = string.IsNullOrWhiteSpace(groupName)
                    ? "Grouped — drag moves all linked fences"
                    : $"Group \"{groupName}\" — drag moves all linked fences";
            else
                _titleBar.ToolTip = "Double-click title to roll up · Ctrl+drag icons to multi-select";
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
                : "Drop files here\nRight-click for options\nCtrl+drag to multi-select";
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
        var border = new Border
        {
            Width = m.TileWidth,
            Margin = new Thickness(2),
            Padding = new Thickness(4, 6, 4, 6),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
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

        // Double-click opens; drag moves item(s). Multi-select = Ctrl+drag marquee on body.
        System.Windows.Point? dragOrigin = null;
        border.PreviewMouseLeftButtonDown += (_, e) =>
        {
            // Ctrl+drag marquee is handled on the body (Preview); don't start item drag under Ctrl
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                return;

            if (e.ClickCount >= 2)
            {
                dragOrigin = null;
                _manager.DesktopIcons.LaunchItem(path);
                e.Handled = true;
                return;
            }

            // Plain click: keep multi-selection if this tile is already selected (for group drag);
            // otherwise select only this tile.
            if (!_selectedPaths.Contains(path) || _selectedPaths.Count <= 1)
            {
                _selectedPaths.Clear();
                _selectedPaths.Add(path);
                ApplySelectionVisuals();
            }

            dragOrigin = e.GetPosition(border);
            border.Focusable = true;
            border.Focus();
        };
        border.PreviewMouseLeftButtonUp += (_, _) => dragOrigin = null;
        border.PreviewMouseMove += (_, e) =>
        {
            if (dragOrigin is null || e.LeftButton != MouseButtonState.Pressed) return;
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control) return;
            if (_marqueeActive) return;

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
                _manager.RemoveItems(_model.Id, toRemove);
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

    private void Body_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Ctrl + click/drag inside fence body → marquee multi-select
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
            return;
        if (e.ChangedButton != MouseButton.Left) return;

        _marqueeActive = true;
        _marqueeStartBody = e.GetPosition(_body);
        _selectedPaths.Clear();
        ApplySelectionVisuals();

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

        _body.CaptureMouse();
        e.Handled = true;
        Focus();
    }

    private void Body_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_marqueeActive || e.LeftButton != MouseButtonState.Pressed) return;
        UpdateMarquee(e.GetPosition(_body));
        e.Handled = true;
    }

    private void Body_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_marqueeActive) return;
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
        _selectedPaths.Clear();
        foreach (var (path, tile) in _tileByPath)
        {
            try
            {
                // Tile lives inside ScrollViewer — TranslatePoint accounts for scroll offset
                var topLeft = tile.TranslatePoint(new System.Windows.Point(0, 0), _body);
                var tileRect = new Rect(topLeft.X, topLeft.Y,
                    Math.Max(1, tile.ActualWidth), Math.Max(1, tile.ActualHeight));
                if (box.IntersectsWith(tileRect))
                    _selectedPaths.Add(path);
            }
            catch { /* ignore layout race */ }
        }
        ApplySelectionVisuals();
    }

    private void EndMarquee(bool commit)
    {
        if (!_marqueeActive) return;
        _marqueeActive = false;
        try { _body.ReleaseMouseCapture(); } catch { /* ignore */ }
        if (_marqueeVisual is not null)
            _marqueeVisual.Visibility = Visibility.Collapsed;
        if (!commit)
            ClearSelection();
        // Tiny click with Ctrl and no tiles hit: leave selection empty
    }

    private void DeleteSelectedOr(string fallbackPath)
    {
        var paths = _selectedPaths.Count > 0 ? _selectedPaths.ToList() : new List<string> { fallbackPath };
        if (_manager.DeleteFromDisk(paths) > 0)
        {
            _selectedPaths.Clear();
            RefreshContent();
        }
    }

    private void StartItemDrag(FrameworkElement source, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        try
        {
            LastDropFenceId = null;
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
        var cm = new ContextMenu();
        void Add(string header, Action action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) =>
            {
                try { action(); } catch (Exception ex) { AppLog.Write(ex.Message); }
            };
            cm.Items.Add(mi);
        }

        Add("Rename fence...", () =>
        {
            var name = FenceManager.PromptText("Rename fence", "Fence name:", _model.Title);
            if (string.IsNullOrWhiteSpace(name)) return;
            _model.Title = name.Trim();
            _manager.LayoutStore.UpdateFence(_model);
            RefreshContent();
        });
        Add("Roll up / expand", ToggleRollUp);
        Add("Add tab...", () =>
        {
            if (_model.IsPortal)
            {
                MessageBox.Show("Tabs are not available on portal fences.", "FenceDesk");
                return;
            }
            var name = FenceManager.PromptText("Add tab", "Tab name:", "New tab");
            if (string.IsNullOrWhiteSpace(name)) return;
            var tid = Guid.NewGuid().ToString();
            _model.Tabs.Add(new FenceTab { Id = tid, Title = name.Trim() });
            _model.ActiveTabId = tid;
            _manager.LayoutStore.UpdateFence(_model);
            RefreshContent();
            BuildContextMenu();
        });
        if (!_model.IsPortal && _model.Tabs.Count > 1)
        {
            Add("Delete current tab…", () => DeleteTab(_model.ActiveTabId));
        }
        cm.Items.Add(new Separator());
        Add("New fence", () => _manager.NewFenceFromTray());
        Add("Convert to portal (folder view)...", () =>
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
        Add("Convert to items (manual list)", () =>
        {
            _manager.Portals.Unregister(_model.Id);
            _model.Mode = "items";
            _model.PortalPath = null;
            _manager.LayoutStore.UpdateFence(_model);
            RefreshContent();
            _manager.DesktopIcons.SyncVisibility();
        });
        cm.Items.Add(new Separator());
        Add("Background color...", () =>
        {
            var c = FenceManager.PickColor(_model.BgColor);
            if (c is null) return;
            _model.BgColor = FenceManager.ToHex(c.Value.R, c.Value.G, c.Value.B);
            _manager.LayoutStore.UpdateFence(_model);
            ApplyGlassAppearance();
        });
        Add("Text color...", () =>
        {
            var c = FenceManager.PickColor(
                string.IsNullOrWhiteSpace(_model.TextColor) ? DefaultTextColor : _model.TextColor);
            if (c is null) return;
            _model.TextColor = FenceManager.ToHex(c.Value.R, c.Value.G, c.Value.B);
            _manager.LayoutStore.UpdateFence(_model);
            ApplyGlassAppearance();
            RefreshContent(); // rebuild tiles with new label color
        });
        Add("Reset colors (background + text)", ResetColorsToDefault);
        Add("Opacity...", ShowOpacityDialog);
        cm.Items.Add(new Separator());
        Add(_model.Locked ? "Unlock position" : "Lock position", () =>
        {
            _manager.SetFenceLocked(_model.Id, !_model.Locked);
            BuildContextMenu();
        });
        Add("Group with…", () =>
        {
            var target = _manager.PickFenceToGroupWith(_model.Id);
            if (target is null) return;
            _manager.JoinFenceGroup(_model.Id, target);
            BuildContextMenu();
        });
        if (!string.IsNullOrWhiteSpace(_model.GroupId))
        {
            Add("Rename group…", () =>
            {
                var current = _manager.GetGroupName(_model.GroupId);
                var name = FenceManager.PromptText(
                    "Rename group",
                    "Group name (leave blank to clear):",
                    current);
                if (name is null) return; // cancelled
                _manager.SetGroupName(_model.GroupId!, name);
                BuildContextMenu();
            });
            Add("Ungroup this fence", () =>
            {
                _manager.LeaveFenceGroup(_model.Id);
                BuildContextMenu();
            });
            Add("Match size to this fence (group)", () => _manager.MatchGroupSize(_model.Id));
        }
        cm.Items.Add(new Separator());
        Add("Delete fence", () =>
        {
            if (MessageBox.Show($"Delete fence \"{_model.Title}\"?", "FenceDesk",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                _manager.RemoveFence(_model.Id);
        });

        ContextMenu = cm;
    }

    private void ShowOpacityDialog()
    {
        // Reload latest from store first so we don't start from a stale value
        _model = _manager.LayoutStore.FindFence(_model.Id) ?? _model;
        var original = Math.Clamp(_model.Opacity, 0.15, 1.0);
        var win = new Window
        {
            Title = "Fence opacity",
            Width = 400,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(30, 36, 48)),
            ShowInTaskbar = false,
            Topmost = true
        };
        var grid = new Grid { Margin = new Thickness(16) };
        for (var i = 0; i < 4; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var lbl = new TextBlock
        {
            Text = "Panel transparency (icons stay solid)\nLower = more see-through to desktop",
            Foreground = new SolidColorBrush(Color.FromRgb(200, 208, 220)),
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(lbl, 0);
        var valueLbl = new TextBlock
        {
            Text = $"{(int)Math.Round(original * 100)}%",
            Foreground = new SolidColorBrush(Color.FromRgb(200, 208, 220)),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(valueLbl, 1);
        var slider = new Slider
        {
            Minimum = 15, // avoid fully invisible fences
            Maximum = 100,
            TickFrequency = 5,
            IsSnapToTickEnabled = false,
            Value = Math.Clamp(Math.Round(original * 100), 15, 100),
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(slider, 2);
        slider.ValueChanged += (_, _) =>
        {
            var o = slider.Value / 100.0;
            valueLbl.Text = $"{(int)slider.Value}%";
            _model.Opacity = o;
            // Write through to store object so nothing reloads an old value mid-drag
            var stored = _manager.LayoutStore.FindFence(_model.Id);
            if (stored is not null) stored.Opacity = o;
            ApplyWindowOpacity();
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(sp, 3);
        var ok = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        ok.Click += (_, _) =>
        {
            _model.Opacity = slider.Value / 100.0;
            _manager.LayoutStore.UpdateFence(_model);
            ApplyWindowOpacity();
            win.DialogResult = true;
            win.Close();
        };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        cancel.Click += (_, _) =>
        {
            _model.Opacity = original;
            var stored = _manager.LayoutStore.FindFence(_model.Id);
            if (stored is not null) stored.Opacity = original;
            ApplyWindowOpacity();
            win.Close();
        };
        sp.Children.Add(ok);
        sp.Children.Add(cancel);
        grid.Children.Add(lbl);
        grid.Children.Add(valueLbl);
        grid.Children.Add(slider);
        grid.Children.Add(sp);
        win.Content = grid;
        // Apply current value once when opening
        ApplyWindowOpacity();
        win.ShowDialog();
    }

    private void EmptyArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Ignore if click originated on an item tile
        if (e.OriginalSource is DependencyObject d)
        {
            var cur = d;
            while (cur is not null && cur != this)
            {
                if (cur is Border b && b.Tag is string)
                    return; // item tile
                cur = VisualTreeHelper.GetParent(cur);
            }
        }

        // Ctrl is handled by marquee on body — don't clear
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
            ApplySelectionVisuals();
            e.Handled = true;
            return;
        }
        if (e.Key is Key.Delete or Key.Back)
        {
            if (_selectedPaths.Count == 0) return;
            if (_model.IsPortal)
            {
                DeleteSelectedOr(_selectedPaths.First());
            }
            else
            {
                _manager.RemoveItems(_model.Id, _selectedPaths.ToList());
                _selectedPaths.Clear();
            }
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

        // Group-aware drag (moves linked fences together)
        _groupDragging = true;
        _groupDragScreenStart = PointToScreen(e.GetPosition(this));
        _groupDragOrigins.Clear();
        foreach (var f in _manager.GetLinkedFences(_model.Id))
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
        try { _titleBar.ReleaseMouseCapture(); } catch { /* ignore */ }

        if (snap && !_model.Locked)
        {
            var (sl, st) = _manager.GetSnappedPosition(
                _model.Id, Left, Top, ActualWidth, ActualHeight);
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

        _manager.SyncGroupGeometryFromLeader(_model.Id);
        _groupDragOrigins.Clear();
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
