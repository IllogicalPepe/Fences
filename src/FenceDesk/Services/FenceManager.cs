using FenceDesk.Models;
using FenceDesk.Native;
using FenceDesk.Windows;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace FenceDesk.Services;

public sealed class FenceManager
{
    private readonly LayoutStore _layout;
    private readonly IconService _icons;
    private readonly DesktopIconService _desktopIcons;
    private readonly PortalService _portals;
    private readonly Dictionary<string, FenceWindow> _windows = new();
    private DispatcherQueueTimer? _showDesktopTimer;
    private bool _exiting;

    public FenceManager(
        LayoutStore layout,
        IconService icons,
        DesktopIconService desktopIcons,
        PortalService portals)
    {
        _layout = layout;
        _icons = icons;
        _desktopIcons = desktopIcons;
        _portals = portals;
    }

    public LayoutStore LayoutStore => _layout;
    public IconService Icons => _icons;
    public DesktopIconService DesktopIcons => _desktopIcons;
    public PortalService Portals => _portals;
    public bool IsExiting => _exiting;
    public IReadOnlyDictionary<string, FenceWindow> Windows => _windows;

    public event EventHandler? Exiting;

    public void InitializeAll()
    {
        foreach (var model in _layout.Layout.Fences.ToList())
        {
            model.EnsureDefaults();
            CreateFenceWindow(model);
        }

        if (_layout.Layout.Settings.ShowFences)
            ShowAll();
        else
            HideAll();

        StartShowDesktopGuard();
    }

    public FenceWindow CreateFenceWindow(FenceModel model)
    {
        model.EnsureDefaults();
        if (_windows.TryGetValue(model.Id, out var existing))
        {
            try { existing.Close(); } catch { /* ignore */ }
            _windows.Remove(model.Id);
        }

        var win = new FenceWindow(this, model);
        _windows[model.Id] = win;
        win.Activate();
        win.ApplyDesktopChrome();

        if (model.IsPortal && !string.IsNullOrWhiteSpace(model.PortalPath))
        {
            _portals.Register(model.Id, model.PortalPath, id =>
            {
                if (_windows.TryGetValue(id, out var w))
                    w.RefreshContent();
            });
        }

        return win;
    }

    public void NewFenceFromTray()
    {
        var wa = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea
                 ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        var model = FenceModel.Create(
            "New Fence",
            "items",
            null,
            wa.Left + 100,
            wa.Top + 100,
            360,
            200);
        _layout.AddFence(model);
        CreateFenceWindow(model);
    }

    public void NewPortalFence(string folder)
    {
        var wa = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea
                 ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        var title = Path.GetFileName(folder);
        if (string.IsNullOrWhiteSpace(title)) title = "Portal";
        var model = FenceModel.Create(title, "portal", folder, wa.Left + 100, wa.Top + 100);
        _layout.AddFence(model);
        CreateFenceWindow(model);
    }

    public void RemoveFence(string id)
    {
        _portals.Unregister(id);
        if (_windows.TryGetValue(id, out var win))
        {
            try { win.Close(); } catch { /* ignore */ }
            _windows.Remove(id);
        }
        _layout.RemoveFence(id);
        try { _desktopIcons.SyncVisibility(); } catch { /* ignore */ }
    }

    public void ShowAll()
    {
        _layout.Layout.Settings.ShowFences = true;
        _layout.Save();
        foreach (var w in _windows.Values)
        {
            w.ToggleHidden = false;
            w.ShowFence();
            w.PinToDesktop();
        }
    }

    public void HideAll()
    {
        _layout.Layout.Settings.ShowFences = false;
        _layout.Save();
        foreach (var w in _windows.Values)
        {
            w.ToggleHidden = true;
            w.HideFence();
        }
    }

    public void BringToFront()
    {
        ShowAll();
        foreach (var w in _windows.Values)
            w.PinToDesktop(raise: true);
    }

    public void SetAllLocked(bool locked)
    {
        _layout.SetAllLocked(locked);
        foreach (var w in _windows.Values)
            w.UpdateLockChrome();
    }

    public void SetAllBackgroundColor(string hex)
    {
        foreach (var f in _layout.Layout.Fences)
        {
            f.BgColor = hex;
            if (_windows.TryGetValue(f.Id, out var w))
                w.ApplyGlassAppearance();
        }
        _layout.SaveImmediate();
    }

    public void ResetAllBackgroundColors() => SetAllBackgroundColor("#0F1724");

    public void CloseAll()
    {
        foreach (var id in _windows.Keys.ToList())
        {
            try { _windows[id].Close(); } catch { /* ignore */ }
        }
        _windows.Clear();
        _portals.UnregisterAll();
    }

    public void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        try { StopShowDesktopGuard(); } catch { /* ignore */ }
        try { _layout.SaveImmediate(); } catch { /* ignore */ }
        try { CloseAll(); } catch { /* ignore */ }
        Exiting?.Invoke(this, EventArgs.Empty);
    }

    private void StartShowDesktopGuard()
    {
        var dq = DispatcherQueue.GetForCurrentThread();
        if (dq is null) return;
        _showDesktopTimer = dq.CreateTimer();
        _showDesktopTimer.Interval = TimeSpan.FromMilliseconds(250);
        _showDesktopTimer.IsRepeating = true;
        _showDesktopTimer.Tick += (_, _) => RestoreAfterShowDesktop();
        _showDesktopTimer.Start();
        AppLog.Write("Fence Win+D guard started (smart z-order)");
    }

    private void StopShowDesktopGuard()
    {
        if (_showDesktopTimer is null) return;
        _showDesktopTimer.Stop();
        _showDesktopTimer = null;
    }

    private void RestoreAfterShowDesktop()
    {
        if (_exiting) return;
        if (!_layout.Layout.Settings.ShowFences) return;

        var wantTop = DesktopPin.ShouldUseTopmost(DesktopPin.CurrentProcessId);
        foreach (var w in _windows.Values)
        {
            if (w.ToggleHidden) continue;
            try
            {
                var hwnd = w.GetHwnd();
                var needs = false;
                if (!w.IsFenceVisible) needs = true;
                else if (hwnd != IntPtr.Zero && DesktopPin.NeedsShowDesktopRepair(hwnd)) needs = true;
                if (needs || w.IsTopmost != wantTop)
                    w.PinToDesktop(useTopmost: wantTop);
            }
            catch { /* ignore */ }
        }
    }

    public void AddItems(string fenceId, IEnumerable<string> paths)
    {
        var model = _layout.FindFence(fenceId);
        if (model is null) return;

        if (model.IsPortal)
        {
            var destRoot = model.PortalPath;
            if (string.IsNullOrWhiteSpace(destRoot) || !Directory.Exists(destRoot))
            {
                System.Windows.Forms.MessageBox.Show("Portal folder is not available.", "FenceDesk");
                return;
            }
            foreach (var p in paths)
            {
                try
                {
                    if (!File.Exists(p) && !Directory.Exists(p)) continue;
                    var name = Path.GetFileName(p);
                    var dest = Path.Combine(destRoot, name);
                    if (File.Exists(dest) || Directory.Exists(dest)) continue;
                    if (Directory.Exists(p))
                        CopyDirectory(p, dest);
                    else
                        File.Copy(p, dest, overwrite: false);
                }
                catch (Exception ex)
                {
                    AppLog.Write($"Portal drop failed: {ex.Message}");
                }
            }
            if (_windows.TryGetValue(fenceId, out var win))
                win.RefreshContent();
            return;
        }

        var tab = model.GetActiveTab();
        if (tab is null) return;
        var existing = new HashSet<string>(tab.Items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            var norm = p;
            if (p.Contains("Recycle", StringComparison.OrdinalIgnoreCase) ||
                p.Contains("645FF040", StringComparison.OrdinalIgnoreCase))
                norm = DesktopIconService.RecycleBinPath;
            if (existing.Contains(norm)) continue;
            tab.Items.Add(new FenceItem
            {
                Path = norm,
                Label = _icons.GetDisplayLabel(norm)
            });
            existing.Add(norm);
        }
        _layout.UpdateFence(model);
        if (_windows.TryGetValue(fenceId, out var w))
            w.RefreshContent();
        try { _desktopIcons.SyncVisibility(); } catch { /* ignore */ }
    }

    public void RemoveItem(string fenceId, string path)
    {
        var model = _layout.FindFence(fenceId);
        if (model is null || model.IsPortal) return;
        var tab = model.GetActiveTab();
        if (tab is null) return;
        tab.Items.RemoveAll(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
        _layout.UpdateFence(model);
        if (_windows.TryGetValue(fenceId, out var w))
            w.RefreshContent();
        try { _desktopIcons.SyncVisibility(); } catch { /* ignore */ }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    public static string? PickFolder(string description = "Select folder")
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dlg.SelectedPath : null;
    }

    public static string? PromptText(string title, string prompt, string defaultText = "")
    {
        using var form = new System.Windows.Forms.Form
        {
            Text = title,
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog,
            StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen,
            ClientSize = new System.Drawing.Size(360, 120),
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false
        };
        var lbl = new System.Windows.Forms.Label { Left = 12, Top = 12, Width = 330, Text = prompt };
        var tb = new System.Windows.Forms.TextBox { Left = 12, Top = 36, Width = 330, Text = defaultText };
        var ok = new System.Windows.Forms.Button { Text = "OK", Left = 180, Width = 75, Top = 72, DialogResult = System.Windows.Forms.DialogResult.OK };
        var cancel = new System.Windows.Forms.Button { Text = "Cancel", Left = 267, Width = 75, Top = 72, DialogResult = System.Windows.Forms.DialogResult.Cancel };
        form.Controls.AddRange(new System.Windows.Forms.Control[] { lbl, tb, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog() == System.Windows.Forms.DialogResult.OK ? tb.Text : null;
    }

    public static (byte R, byte G, byte B)? PickColor(string seedHex)
    {
        var rgb = ParseHex(seedHex);
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            SolidColorOnly = false,
            Color = System.Drawing.Color.FromArgb(255, rgb.R, rgb.G, rgb.B)
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return null;
        return (dlg.Color.R, dlg.Color.G, dlg.Color.B);
    }

    public static (byte R, byte G, byte B) ParseHex(string? hex)
    {
        byte r = 15, g = 23, b = 36;
        if (string.IsNullOrWhiteSpace(hex)) return (r, g, b);
        var h = hex.Trim();
        if (h.StartsWith('#')) h = h[1..];
        if (h.Length == 8) h = h[2..];
        if (h.Length == 6)
        {
            try
            {
                r = Convert.ToByte(h[..2], 16);
                g = Convert.ToByte(h[2..4], 16);
                b = Convert.ToByte(h[4..6], 16);
            }
            catch { /* ignore */ }
        }
        return (r, g, b);
    }

    public static string ToHex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";
}
