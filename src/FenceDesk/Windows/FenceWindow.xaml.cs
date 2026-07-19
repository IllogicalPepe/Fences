using FenceDesk.Models;
using FenceDesk.Native;
using FenceDesk.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using WinRT.Interop;

namespace FenceDesk.Windows;

public sealed partial class FenceWindow : Window
{
    private readonly FenceManager _manager;
    private FenceModel _model;
    private double _expandedHeight;
    private bool _readyToSync;
    private bool _suppressGeometry;
    private bool _isTopmost = true;
    private AppWindow? _appWindow;
    private OverlappedPresenter? _presenter;

    // Manual resize
    private bool _resizeActive;
    private string? _resizeEdge;
    private double _startMX, _startMY, _startLeft, _startTop, _startW, _startH;

    public bool ToggleHidden { get; set; }
    public bool IsTopmost => _isTopmost;
    public bool IsFenceVisible => _appWindow?.IsVisible ?? false;

    public FenceWindow(FenceManager manager, FenceModel model)
    {
        _manager = manager;
        _model = model;
        _expandedHeight = Math.Max(80, model.Height);
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);

        _appWindow = GetAppWindow();
        if (_appWindow is not null)
        {
            _appWindow.IsShownInSwitchers = false;
            _presenter = _appWindow.Presenter as OverlappedPresenter;
            if (_presenter is not null)
            {
                _presenter.IsResizable = true;
                _presenter.IsMaximizable = false;
                _presenter.IsMinimizable = false;
                _presenter.SetBorderAndTitleBar(false, false);
            }

            _appWindow.Move(new PointInt32((int)model.X, (int)model.Y));
            _appWindow.Resize(new SizeInt32(
                Math.Max(160, (int)model.Width),
                Math.Max(80, (int)model.Height)));
        }

        Title = " ";
        TitleText.Text = model.Title;
        ApplyGlassAppearance();
        BuildContextMenu();
        RefreshContent();

        if (model.RolledUp)
            ApplyRollUp(force: true);

        RootGrid.PointerMoved += RootGrid_PointerMoved;
        RootGrid.PointerReleased += RootGrid_PointerReleased;
        RootGrid.PointerCaptureLost += (_, _) => EndResize();

        Activated += (_, _) =>
        {
            if (!_readyToSync)
            {
                _readyToSync = true;
                ApplyDesktopChrome();
            }
        };

        Closed += (_, _) =>
        {
            // window closed externally
        };
    }

    public IntPtr GetHwnd()
    {
        try { return WindowNative.GetWindowHandle(this); }
        catch { return IntPtr.Zero; }
    }

    private AppWindow? GetAppWindow()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            return AppWindow.GetFromWindowId(id);
        }
        catch { return null; }
    }

    public void ApplyDesktopChrome()
    {
        var hwnd = GetHwnd();
        if (hwnd == IntPtr.Zero) return;
        NativeMethods.ExcludeFromAltTab(hwnd);
        DesktopPin.EnsureToolWindowStyles(hwnd);
        PinToDesktop();
    }

    public void PinToDesktop(bool? useTopmost = null, bool raise = false)
    {
        var hwnd = GetHwnd();
        if (hwnd == IntPtr.Zero) return;
        var want = useTopmost ?? DesktopPin.ShouldUseTopmost(DesktopPin.CurrentProcessId);
        _isTopmost = want;
        if (_appWindow is not null && !_appWindow.IsVisible && !ToggleHidden)
            _appWindow.Show(false);
        DesktopPin.PinForShowDesktop(hwnd, want);
        if (raise && want)
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    public void ShowFence()
    {
        _appWindow?.Show(false);
        ApplyDesktopChrome();
    }

    public void HideFence()
    {
        _appWindow?.Hide();
    }

    public void ApplyGlassAppearance()
    {
        _model = _manager.LayoutStore.FindFence(_model.Id) ?? _model;
        var op = Math.Clamp(_model.Opacity, 0, 1);
        // Map opacity: keep some minimum glass visibility for hit-testing
        var alpha = (byte)Math.Clamp((int)(255 * Math.Max(0.15, op * 0.85 + 0.1)), 0, 255);
        var rgb = FenceManager.ParseHex(_model.BgColor);
        GlassBrush.Color = global::Windows.UI.Color.FromArgb(alpha, rgb.R, rgb.G, rgb.B);
        var ba = (byte)Math.Clamp((int)(40 * op), 0, 255);
        Glass.BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(ba, 255, 255, 255));
    }

    public void UpdateLockChrome()
    {
        _model = _manager.LayoutStore.FindFence(_model.Id) ?? _model;
        TitleText.Opacity = _model.Locked ? 0.7 : 1.0;
        TitleText.Text = _model.Locked ? $"🔒 {_model.Title}" : _model.Title;
    }

    public void RefreshContent()
    {
        _model = _manager.LayoutStore.FindFence(_model.Id) ?? _model;
        _model.EnsureDefaults();
        UpdateLockChrome();
        ItemsHost.Items.Clear();
        TabStrip.Children.Clear();

        List<FenceItem> items;
        if (_model.IsPortal)
        {
            items = PortalService.GetPortalItems(_model.PortalPath).ToList();
            TitleText.Text = string.IsNullOrWhiteSpace(_model.Title) ? "Portal" : _model.Title;
        }
        else
        {
            var tab = _model.GetActiveTab();
            items = tab?.Items.ToList() ?? new List<FenceItem>();
        }

        var showTabs = !_model.IsPortal && _model.Tabs.Count > 1 && !_model.RolledUp;
        TabStrip.Visibility = showTabs ? Visibility.Visible : Visibility.Collapsed;
        if (showTabs)
        {
            foreach (var t in _model.Tabs)
            {
                var btn = new Button
                {
                    Content = t.Title,
                    Padding = new Thickness(8, 2, 8, 2),
                    Margin = new Thickness(0, 0, 4, 0),
                    FontSize = 11,
                    BorderThickness = new Thickness(0),
                    Tag = t.Id
                };
                var isActive = t.Id == _model.ActiveTabId;
                btn.Background = isActive
                    ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(60, 255, 255, 255))
                    : new SolidColorBrush(Colors.Transparent);
                btn.Foreground = new SolidColorBrush(isActive
                    ? global::Windows.UI.Color.FromArgb(255, 200, 208, 220)
                    : global::Windows.UI.Color.FromArgb(255, 140, 150, 165));
                btn.Click += TabButton_Click;
                btn.ContextFlyout = BuildTabFlyout(t);
                TabStrip.Children.Add(btn);
            }
        }

        if (items.Count == 0)
        {
            HintText.Visibility = Visibility.Visible;
            HintText.Text = _model.IsPortal
                ? (string.IsNullOrWhiteSpace(_model.PortalPath)
                    ? "No folder selected for portal"
                    : $"Portal is empty\n{_model.PortalPath}")
                : "Drop files here\nRight-click for options";
        }
        else
        {
            HintText.Visibility = Visibility.Collapsed;
            var metrics = _manager.Icons;
            foreach (var it in items)
            {
                var path = it.Path;
                var label = string.IsNullOrWhiteSpace(it.Label)
                    ? _manager.Icons.GetDisplayLabel(path)
                    : it.Label!;
                ItemsHost.Items.Add(CreateTile(path, label, metrics.IconSize, metrics.FontSize, metrics.TileWidth));
            }
        }

        ApplyRollUp(force: false);
    }

    private UIElement CreateTile(string path, string label, int iconSize, double fontSize, int tileW)
    {
        var border = new Border
        {
            Width = tileW,
            Margin = new Thickness(2),
            Padding = new Thickness(4, 6, 4, 6),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Colors.Transparent),
            Tag = path
        };

        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var img = new Image
        {
            Width = iconSize,
            Height = iconSize,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            Source = _manager.Icons.GetItemImage(path, iconSize)
        };
        var tb = new TextBlock
        {
            Text = label,
            FontSize = fontSize,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 200, 208, 220)),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = _manager.Icons.LabelMaxHeight,
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Width = Math.Max(48, tileW - 8)
        };
        stack.Children.Add(img);
        stack.Children.Add(tb);
        border.Child = stack;

        border.PointerEntered += (_, _) =>
            border.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(40, 255, 255, 255));
        border.PointerExited += (_, _) =>
            border.Background = new SolidColorBrush(Colors.Transparent);

        border.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(border).Properties.IsLeftButtonPressed &&
                e.GetCurrentPoint(border).Properties.PointerUpdateKind ==
                Microsoft.UI.Input.PointerUpdateKind.LeftButtonPressed)
            {
                // double-click detection via timestamp is handled below
            }
        };
        border.DoubleTapped += (_, e) =>
        {
            _manager.DesktopIcons.LaunchItem(path);
            e.Handled = true;
        };

        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "Open" };
        open.Click += (_, _) => _manager.DesktopIcons.LaunchItem(path);
        menu.Items.Add(open);

        var explorer = new MenuFlyoutItem { Text = "Show in Explorer" };
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
        menu.Items.Add(explorer);

        if (!_model.IsPortal)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var remove = new MenuFlyoutItem { Text = "Remove from fence" };
            remove.Click += (_, _) => _manager.RemoveItem(_model.Id, path);
            menu.Items.Add(remove);
        }

        border.ContextFlyout = menu;
        return border;
    }

    private MenuFlyout BuildTabFlyout(FenceTab tab)
    {
        var fly = new MenuFlyout();
        var ren = new MenuFlyoutItem { Text = "Rename tab" };
        ren.Click += (_, _) =>
        {
            var name = FenceManager.PromptText("Rename tab", "Tab name:", tab.Title);
            if (string.IsNullOrWhiteSpace(name)) return;
            tab.Title = name.Trim();
            _manager.LayoutStore.UpdateFence(_model);
            RefreshContent();
        };
        fly.Items.Add(ren);

        var close = new MenuFlyoutItem { Text = "Close tab" };
        close.Click += (_, _) =>
        {
            if (_model.Tabs.Count <= 1) return;
            _model.Tabs.RemoveAll(t => t.Id == tab.Id);
            if (_model.ActiveTabId == tab.Id)
                _model.ActiveTabId = _model.Tabs[0].Id;
            _manager.LayoutStore.UpdateFence(_model);
            RefreshContent();
        };
        fly.Items.Add(close);
        return fly;
    }

    private void TabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string tid)
        {
            _model.ActiveTabId = tid;
            _manager.LayoutStore.UpdateFence(_model);
            RefreshContent();
        }
    }

    private void BuildContextMenu()
    {
        var menu = new MenuFlyout();

        void Add(string text, Action action)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) =>
            {
                try { action(); } catch (Exception ex) { AppLog.Write(ex.Message); }
            };
            menu.Items.Add(item);
        }

        Add("Rename fence...", () =>
        {
            var name = FenceManager.PromptText("Rename fence", "Fence name:", _model.Title);
            if (string.IsNullOrWhiteSpace(name)) return;
            _model.Title = name.Trim();
            _manager.LayoutStore.UpdateFence(_model);
            RefreshContent();
        });
        Add("Roll up / expand", () => ToggleRollUp());
        Add("Add tab...", () =>
        {
            if (_model.IsPortal)
            {
                System.Windows.Forms.MessageBox.Show("Tabs are not available on portal fences.", "FenceDesk");
                return;
            }
            var name = FenceManager.PromptText("Add tab", "Tab name:", "New tab");
            if (string.IsNullOrWhiteSpace(name)) return;
            var tid = Guid.NewGuid().ToString();
            _model.Tabs.Add(new FenceTab { Id = tid, Title = name.Trim() });
            _model.ActiveTabId = tid;
            _manager.LayoutStore.UpdateFence(_model);
            RefreshContent();
        });
        Add("Add Recycle Bin", () =>
        {
            if (_model.IsPortal)
            {
                System.Windows.Forms.MessageBox.Show("Switch to items mode first.", "FenceDesk");
                return;
            }
            _manager.AddItems(_model.Id, new[] { DesktopIconService.RecycleBinPath });
        });
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("New fence", () => _manager.NewFenceFromTray());
        Add("Convert to portal (folder view)...", () =>
        {
            var folder = FenceManager.PickFolder("Select folder to show as a portal fence");
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
        Add("Open portal folder", () =>
        {
            if (!string.IsNullOrWhiteSpace(_model.PortalPath) && Directory.Exists(_model.PortalPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{_model.PortalPath}\"",
                    UseShellExecute = true
                });
            }
        });
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Background color...", () =>
        {
            var c = FenceManager.PickColor(_model.BgColor);
            if (c is null) return;
            _model.BgColor = FenceManager.ToHex(c.Value.R, c.Value.G, c.Value.B);
            _manager.LayoutStore.UpdateFence(_model);
            ApplyGlassAppearance();
        });
        Add("Opacity...", () => ShowOpacityDialog());
        Add(_model.Locked ? "Unlock fence" : "Lock fence", () =>
        {
            _model.Locked = !_model.Locked;
            _manager.LayoutStore.UpdateFence(_model);
            UpdateLockChrome();
        });
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Delete fence", () =>
        {
            var r = System.Windows.Forms.MessageBox.Show(
                $"Delete fence \"{_model.Title}\"?", "FenceDesk",
                System.Windows.Forms.MessageBoxButtons.YesNo,
                System.Windows.Forms.MessageBoxIcon.Question);
            if (r == System.Windows.Forms.DialogResult.Yes)
                _manager.RemoveFence(_model.Id);
        });

        RootGrid.ContextFlyout = menu;
    }

    private void ShowOpacityDialog()
    {
        var original = _model.Opacity;
        using var form = new System.Windows.Forms.Form
        {
            Text = "Fence opacity",
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog,
            StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen,
            ClientSize = new System.Drawing.Size(360, 130),
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false
        };
        var lbl = new System.Windows.Forms.Label
        {
            Left = 12, Top = 12, Width = 330,
            Text = "Panel opacity (icons stay solid)"
        };
        var value = new System.Windows.Forms.Label
        {
            Left = 12, Top = 36, Width = 330,
            Text = $"{(int)Math.Round(original * 100)}%",
            TextAlign = System.Drawing.ContentAlignment.MiddleCenter
        };
        var slider = new System.Windows.Forms.TrackBar
        {
            Left = 12, Top = 56, Width = 330,
            Minimum = 0, Maximum = 100,
            Value = (int)Math.Clamp(Math.Round(original * 100), 0, 100),
            TickFrequency = 5
        };
        slider.ValueChanged += (_, _) =>
        {
            value.Text = $"{slider.Value}%";
            _model.Opacity = slider.Value / 100.0;
            ApplyGlassAppearance();
        };
        var ok = new System.Windows.Forms.Button
        {
            Text = "OK", Left = 180, Top = 96, Width = 75,
            DialogResult = System.Windows.Forms.DialogResult.OK
        };
        var cancel = new System.Windows.Forms.Button
        {
            Text = "Cancel", Left = 267, Top = 96, Width = 75,
            DialogResult = System.Windows.Forms.DialogResult.Cancel
        };
        form.Controls.AddRange(new System.Windows.Forms.Control[] { lbl, value, slider, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        if (form.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _model.Opacity = slider.Value / 100.0;
            _manager.LayoutStore.UpdateFence(_model);
        }
        else
        {
            _model.Opacity = original;
            ApplyGlassAppearance();
        }
    }

    private void ToggleRollUp()
    {
        _model.RolledUp = !_model.RolledUp;
        ApplyRollUp(force: true);
        _manager.LayoutStore.UpdateFence(_model);
    }

    private void ApplyRollUp(bool force)
    {
        if (_appWindow is null) return;
        _suppressGeometry = true;
        try
        {
            if (_model.RolledUp)
            {
                var cur = _appWindow.Size.Height;
                if (cur > 40) _expandedHeight = cur;
                Body.Visibility = Visibility.Collapsed;
                TabStrip.Visibility = Visibility.Collapsed;
                RollButton.Content = "▼";
                _appWindow.Resize(new SizeInt32(_appWindow.Size.Width, 36));
            }
            else if (force || Body.Visibility != Visibility.Visible)
            {
                Body.Visibility = Visibility.Visible;
                RollButton.Content = "▲";
                var h = (int)Math.Max(80, _expandedHeight);
                _appWindow.Resize(new SizeInt32(_appWindow.Size.Width, h));
                var showTabs = !_model.IsPortal && _model.Tabs.Count > 1;
                TabStrip.Visibility = showTabs ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        finally
        {
            _suppressGeometry = false;
        }
    }

    private void TitleBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_model.Locked)
        {
            e.Handled = true;
            return;
        }

        var pt = e.GetCurrentPoint(TitleBar);
        if (pt.Properties.IsLeftButtonPressed)
        {
            // double-tap roll handled separately
            try
            {
                var hwnd = GetHwnd();
                // WinUI: Start dragging via NCLBUTTONDOWN
                const int WM_NCLBUTTONDOWN = 0x00A1;
                const int HTCAPTION = 0x2;
                PostMessage(hwnd, WM_NCLBUTTONDOWN, HTCAPTION, 0);
                SyncGeometry();
            }
            catch (Exception ex)
            {
                AppLog.Write($"Title drag: {ex.Message}");
            }
        }
    }

    private void TitleBar_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        ToggleRollUp();
        e.Handled = true;
    }

    private void RollButton_Click(object sender, RoutedEventArgs e) => ToggleRollUp();

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.Handled = true;
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
                if (paths.Count > 0)
                    _manager.AddItems(_model.Id, paths!);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"Drop error: {ex.Message}");
        }
    }

    private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizeActive || _appWindow is null || _model.Locked || _model.RolledUp) return;
        var screen = System.Windows.Forms.Control.MousePosition;
        var dx = screen.X - _startMX;
        var dy = screen.Y - _startMY;
        var left = _startLeft;
        var top = _startTop;
        var width = _startW;
        var height = _startH;
        const double minW = 140, minH = 48;

        switch (_resizeEdge)
        {
            case "Right": width = Math.Max(minW, _startW + dx); break;
            case "Bottom": height = Math.Max(minH, _startH + dy); break;
            case "Left":
                width = Math.Max(minW, _startW - dx);
                left = _startLeft + (_startW - width);
                break;
            case "Top":
                height = Math.Max(minH, _startH - dy);
                top = _startTop + (_startH - height);
                break;
            case "BottomRight":
                width = Math.Max(minW, _startW + dx);
                height = Math.Max(minH, _startH + dy);
                break;
            case "BottomLeft":
                width = Math.Max(minW, _startW - dx);
                height = Math.Max(minH, _startH + dy);
                left = _startLeft + (_startW - width);
                break;
            case "TopRight":
                width = Math.Max(minW, _startW + dx);
                height = Math.Max(minH, _startH - dy);
                top = _startTop + (_startH - height);
                break;
            case "TopLeft":
                width = Math.Max(minW, _startW - dx);
                height = Math.Max(minH, _startH - dy);
                left = _startLeft + (_startW - width);
                top = _startTop + (_startH - height);
                break;
        }

        _appWindow.MoveAndResize(new RectInt32((int)left, (int)top, (int)width, (int)height));
        e.Handled = true;
    }

    private void RootGrid_PointerReleased(object sender, PointerRoutedEventArgs e) => EndResize();

    private void EndResize()
    {
        if (!_resizeActive) return;
        _resizeActive = false;
        _resizeEdge = null;
        SyncGeometry();
    }

    public void SyncGeometry()
    {
        if (!_readyToSync || _suppressGeometry || _appWindow is null) return;
        var pos = _appWindow.Position;
        var size = _appWindow.Size;
        _model.X = pos.X;
        _model.Y = pos.Y;
        _model.Width = size.Width;
        if (!_model.RolledUp && size.Height > 40)
        {
            _model.Height = size.Height;
            _expandedHeight = size.Height;
        }
        _manager.LayoutStore.UpdateFence(_model);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
}
