using System.Windows;
using System.Windows.Threading;
using FenceDesk.Models;
using FenceDesk.Native;
using FenceDesk.Windows;

namespace FenceDesk.Services;

public sealed class FenceManager
{
    private readonly LayoutStore _layout;
    private readonly IconService _icons;
    private readonly DesktopIconService _desktopIcons;
    private readonly PortalService _portals;
    private readonly Dictionary<string, FenceWindow> _windows = new();
    private DispatcherTimer? _showDesktopTimer;
    private DesktopClickPoller? _clickPoller;
    private int _lastToggleTick;
    private bool _exiting;

    public FenceManager(LayoutStore layout, IconService icons, DesktopIconService desktopIcons, PortalService portals)
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
            try { CreateFenceWindow(model); }
            catch (Exception ex) { AppLog.Write($"Create fence failed ({model.Title}): {ex}"); }
        }

        // Always show on cold start unless user last hid them
        if (_layout.Layout.Settings.ShowFences)
        {
            ShowAll();
            // Ensure every fence is on-screen after first composition pass
            foreach (var w in _windows.Values)
            {
                try
                {
                    w.SetSoftVisible(true);
                    w.ApplyDesktopChrome();
                }
                catch (Exception ex) { AppLog.Write($"Init show: {ex.Message}"); }
            }
        }
        else HideAll();

        // Show-desktop guard can be re-enabled after stability is confirmed
        // StartShowDesktopGuard();

        StartDesktopDoubleClickWatch();
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
        win.Show();
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
        var model = FenceModel.Create("New Fence", "items", null, wa.Left + 100, wa.Top + 100);
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
        foreach (var w in _windows.Values)
        {
            try { w.SetSoftVisible(true); }
            catch (Exception ex) { AppLog.Write($"ShowAll: {ex.Message}"); }
        }
        // In-memory only during interactive toggle — disk write can hitch AV/DWM
        _layout.Layout.Settings.ShowFences = true;
    }

    public void HideAll()
    {
        foreach (var w in _windows.Values)
        {
            try { w.SetSoftVisible(false); }
            catch (Exception ex) { AppLog.Write($"HideAll: {ex.Message}"); }
        }
        _layout.Layout.Settings.ShowFences = false;
    }

    /// <summary>
    /// Toggle all fences (used by desktop empty-space double-click).
    /// </summary>
    public void ToggleAll()
    {
        if (_exiting) return;
        var now = Environment.TickCount;
        if (_lastToggleTick != 0)
        {
            var dt = now - _lastToggleTick;
            if (dt > 0 && dt < 250)
                return;
        }
        _lastToggleTick = now;

        var anyShown = _windows.Values.Any(w => w.IsOnScreen);
        if (anyShown) HideAll();
        else ShowAll();
    }

    public void BringToFront()
    {
        ShowAll();
        foreach (var w in _windows.Values)
            w.ApplyDesktopChrome(raise: true);
    }

    public void StartDesktopDoubleClickWatch()
    {
        try
        {
            StopDesktopDoubleClickWatch();
            var dispatcher = System.Windows.Application.Current?.Dispatcher
                             ?? Dispatcher.CurrentDispatcher;
            // UI-thread poller only — no global mouse hooks (those black multi-monitor DWM)
            _clickPoller = new DesktopClickPoller(dispatcher, () =>
            {
                if (!_exiting) ToggleAll();
            });
            _clickPoller.Start();
            _layout.Layout.Settings.DoubleClickDesktopHide = true;
            AppLog.Write("Desktop double-click hide/show enabled (ui-poller, no global hooks)");
        }
        catch (Exception ex)
        {
            AppLog.Write("StartDesktopDoubleClickWatch: " + ex.Message);
        }
    }

    public void StopDesktopDoubleClickWatch()
    {
        try
        {
            _clickPoller?.Dispose();
            _clickPoller = null;
        }
        catch { /* ignore */ }
    }

    public void SetAllLocked(bool locked)
    {
        _layout.SetAllLocked(locked);
        foreach (var w in _windows.Values)
            w.UpdateLockChrome();
    }

    public IReadOnlyList<FenceModel> GetFencesInGroup(string? groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId)) return Array.Empty<FenceModel>();
        return _layout.Layout.Fences.Where(f => f.GroupId == groupId).ToList();
    }

    public IReadOnlyList<FenceModel> GetLinkedFences(string fenceId)
    {
        var m = _layout.FindFence(fenceId);
        if (m is null) return Array.Empty<FenceModel>();
        if (string.IsNullOrWhiteSpace(m.GroupId))
            return new[] { m };
        return GetFencesInGroup(m.GroupId);
    }

    public void SetFenceLocked(string fenceId, bool locked)
    {
        var linked = GetLinkedFences(fenceId);
        foreach (var f in linked)
        {
            f.Locked = locked;
            _layout.UpdateFence(f);
            if (_windows.TryGetValue(f.Id, out var w))
                w.UpdateLockChrome();
        }
    }

    public void JoinFenceGroup(string fenceId, string targetFenceId)
    {
        var a = _layout.FindFence(fenceId);
        var b = _layout.FindFence(targetFenceId);
        if (a is null || b is null) return;

        var gid = !string.IsNullOrWhiteSpace(b.GroupId) ? b.GroupId
            : !string.IsNullOrWhiteSpace(a.GroupId) ? a.GroupId
            : Guid.NewGuid().ToString();

        var merge = new Dictionary<string, FenceModel>(StringComparer.Ordinal);
        merge[a.Id] = a;
        merge[b.Id] = b;
        if (!string.IsNullOrWhiteSpace(a.GroupId))
            foreach (var f in GetFencesInGroup(a.GroupId)) merge[f.Id] = f;
        if (!string.IsNullOrWhiteSpace(b.GroupId))
            foreach (var f in GetFencesInGroup(b.GroupId)) merge[f.Id] = f;

        var lockGroup = merge.Values.Any(f => f.Locked);

        // Preserve an existing group name when merging
        var groups = _layout.Layout.Groups ??= new Dictionary<string, string>(StringComparer.Ordinal);
        string? existingName = null;
        foreach (var id in new[] { a.GroupId, b.GroupId, gid })
        {
            if (id is null) continue;
            if (groups.TryGetValue(id, out var n) && !string.IsNullOrWhiteSpace(n))
            {
                existingName = n;
                break;
            }
        }

        foreach (var f in merge.Values)
        {
            f.GroupId = gid;
            f.Locked = lockGroup;
            _layout.UpdateFence(f);
            if (_windows.TryGetValue(f.Id, out var w))
                w.UpdateLockChrome();
        }
        if (!string.IsNullOrWhiteSpace(existingName))
            groups[gid!] = existingName!;
        _layout.Save();
        AppLog.Write($"Merged group [{string.Join(", ", merge.Values.Select(f => f.Title))}] locked={lockGroup}");
    }

    public void LeaveFenceGroup(string fenceId)
    {
        var m = _layout.FindFence(fenceId);
        if (m is null) return;
        var gid = m.GroupId;
        m.GroupId = null;
        _layout.UpdateFence(m);
        if (_windows.TryGetValue(fenceId, out var w))
            w.UpdateLockChrome();
        CleanupEmptyGroup(gid);
    }

    public string GetGroupName(string? groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId)) return string.Empty;
        var groups = _layout.Layout.Groups;
        if (groups is not null && groups.TryGetValue(groupId, out var name) && !string.IsNullOrWhiteSpace(name))
            return name.Trim();
        return string.Empty;
    }

    public void SetGroupName(string groupId, string? name)
    {
        if (string.IsNullOrWhiteSpace(groupId)) return;
        var groups = _layout.Layout.Groups ??= new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(name))
            groups.Remove(groupId);
        else
            groups[groupId] = name.Trim();
        _layout.Save();
        foreach (var f in GetFencesInGroup(groupId))
        {
            if (_windows.TryGetValue(f.Id, out var w))
                w.UpdateLockChrome();
        }
    }

    private void CleanupEmptyGroup(string? groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId)) return;
        if (GetFencesInGroup(groupId).Count > 0) return;
        _layout.Layout.Groups?.Remove(groupId);
        _layout.Save();
    }

    public void MatchGroupSize(string fenceId)
    {
        var m = _layout.FindFence(fenceId);
        if (m is null || string.IsNullOrWhiteSpace(m.GroupId)) return;
        var w = Math.Max(140, m.Width);
        var h = Math.Max(80, m.Height);
        foreach (var f in GetFencesInGroup(m.GroupId))
        {
            f.Width = w;
            f.Height = h;
            _layout.UpdateFence(f);
            if (_windows.TryGetValue(f.Id, out var win))
                win.ApplySizeFromModel();
        }
        // After uniform size, push members apart so they no longer stack on top of each other
        ArrangeGroupNoOverlap(m.GroupId, preferAnchorId: fenceId);
    }

    /// <summary>
    /// Repositions group members so none overlap, keeping the anchor fence fixed.
    /// Others slide right, then below, until clear (with a small gap).
    /// </summary>
    public void ArrangeGroupNoOverlap(string groupId, string? preferAnchorId = null, double gap = 14)
    {
        var members = GetFencesInGroup(groupId).ToList();
        if (members.Count <= 1) return;

        // Snapshot current screen positions (window if available)
        var entries = new List<(string Id, double X, double Y, double W, double H, bool Locked)>();
        foreach (var f in members)
        {
            double x = f.X, y = f.Y, ww = f.Width, hh = f.Height;
            if (_windows.TryGetValue(f.Id, out var win) && !win.ToggleHidden)
            {
                x = win.Left;
                y = win.Top;
                ww = win.ActualWidth > 1 ? win.ActualWidth : win.Width;
                hh = win.ActualHeight > 1 ? win.ActualHeight : win.Height;
            }
            entries.Add((f.Id, x, y, Math.Max(140, ww), Math.Max(32, hh), f.Locked));
        }

        // Anchor stays put (the fence that initiated match-size, or top-left-most)
        var anchorId = preferAnchorId is not null && entries.Any(e => e.Id == preferAnchorId)
            ? preferAnchorId
            : entries.OrderBy(e => e.Y).ThenBy(e => e.X).First().Id;

        var ordered = entries
            .OrderBy(e => e.Id == anchorId ? 0 : 1)
            .ThenBy(e => e.Y)
            .ThenBy(e => e.X)
            .ToList();

        var placed = new List<(string Id, double X, double Y, double W, double H)>();
        var minX = entries.Min(e => e.X);
        var maxRight = System.Windows.Forms.SystemInformation.VirtualScreen.Right - 40.0;
        var maxBottom = System.Windows.Forms.SystemInformation.VirtualScreen.Bottom - 40.0;
        var minScreenX = (double)System.Windows.Forms.SystemInformation.VirtualScreen.Left;
        var minScreenY = (double)System.Windows.Forms.SystemInformation.VirtualScreen.Top;

        static bool Overlaps(
            double ax, double ay, double aw, double ah,
            double bx, double by, double bw, double bh, double g)
        {
            return ax < bx + bw + g && ax + aw + g > bx
                   && ay < by + bh + g && ay + ah + g > by;
        }

        foreach (var e in ordered)
        {
            var x = e.X;
            var y = e.Y;
            var isAnchor = e.Id == anchorId;

            if (!isAnchor && !e.Locked)
            {
                // Nudge until free of every already-placed rect
                var guard = 0;
                while (guard++ < 80)
                {
                    var hit = placed.FirstOrDefault(p =>
                        Overlaps(x, y, e.W, e.H, p.X, p.Y, p.W, p.H, gap));
                    if (hit.Id is null) break;

                    // Prefer sliding to the right of the conflict
                    var rightOf = hit.X + hit.W + gap;
                    if (rightOf + e.W <= maxRight)
                    {
                        x = rightOf;
                        continue;
                    }

                    // Wrap: align under the conflict (or under the pack), keep left of group
                    x = Math.Max(minX, minScreenX);
                    y = Math.Max(y, hit.Y + hit.H + gap);
                    if (y + e.H > maxBottom)
                        y = Math.Max(minScreenY, hit.Y); // clamp; still better than infinite loop
                }
            }

            placed.Add((e.Id, x, y, e.W, e.H));

            var model = _layout.FindFence(e.Id);
            if (model is null) continue;
            model.X = x;
            model.Y = y;
            _layout.UpdateFence(model);
            if (_windows.TryGetValue(e.Id, out var win) && !win.ToggleHidden)
            {
                // Only move unlocked non-anchor (anchor already correct; locked stay)
                if (e.Id == anchorId || e.Locked)
                {
                    // Still sync model; window may already be there
                    if (Math.Abs(win.Left - x) > 0.5 || Math.Abs(win.Top - y) > 0.5)
                    {
                        if (!e.Locked)
                        {
                            win.Left = x;
                            win.Top = y;
                        }
                    }
                }
                else
                {
                    win.Left = x;
                    win.Top = y;
                }
            }
        }
        _layout.Save();
    }

    /// <summary>Magnetic snap of Left/Top against other visible fences.</summary>
    public (double Left, double Top) GetSnappedPosition(
        string fenceId, double left, double top, double width, double height, double threshold = 14)
    {
        var bestLeft = left;
        var bestTop = top;
        var bestDx = threshold + 1;
        var bestDy = threshold + 1;
        var right = left + width;
        var bottom = top + height;

        foreach (var (id, win) in _windows)
        {
            if (id == fenceId || win.ToggleHidden) continue;
            if (!win.IsVisible || win.Visibility != Visibility.Visible) continue;
            // Skip group siblings while dragging together (optional: still snap to non-group)
            var other = _layout.FindFence(id);
            var self = _layout.FindFence(fenceId);
            if (self?.GroupId is not null && self.GroupId == other?.GroupId) continue;

            var oL = win.Left;
            var oT = win.Top;
            var oR = win.Left + win.ActualWidth;
            var oB = win.Top + win.ActualHeight;

            foreach (var (v, d) in new[]
                     {
                         (oL, Math.Abs(left - oL)),
                         (oR - width, Math.Abs(right - oR)),
                         (oR, Math.Abs(left - oR)),
                         (oL - width, Math.Abs(right - oL))
                     })
            {
                if (d <= threshold && d < bestDx) { bestDx = d; bestLeft = v; }
            }
            foreach (var (v, d) in new[]
                     {
                         (oT, Math.Abs(top - oT)),
                         (oB - height, Math.Abs(bottom - oB)),
                         (oB, Math.Abs(top - oB)),
                         (oT - height, Math.Abs(bottom - oT))
                     })
            {
                if (d <= threshold && d < bestDy) { bestDy = d; bestTop = v; }
            }
        }
        return (bestLeft, bestTop);
    }

    public void MoveGroupBy(string fenceId, double dx, double dy)
    {
        if (Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01) return;
        foreach (var f in GetLinkedFences(fenceId))
        {
            if (f.Id == fenceId) continue;
            if (f.Locked) continue;
            if (!_windows.TryGetValue(f.Id, out var win)) continue;
            win.ApplyOffset(dx, dy);
        }
    }

    public void SyncGroupGeometryFromLeader(string fenceId)
    {
        foreach (var f in GetLinkedFences(fenceId))
        {
            if (!_windows.TryGetValue(f.Id, out var win)) continue;
            win.PushGeometryToModel();
        }
    }

    public string? PickFenceToGroupWith(string excludeId)
    {
        var options = new List<(string Id, string Label)>();
        var seenGroups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in _layout.Layout.Fences)
        {
            if (f.Id == excludeId) continue;
            if (GetLinkedFences(excludeId).Any(x => x.Id == f.Id)) continue;

            if (!string.IsNullOrWhiteSpace(f.GroupId))
            {
                if (!seenGroups.Add(f.GroupId)) continue;
                var gName = GetGroupName(f.GroupId);
                var titles = GetFencesInGroup(f.GroupId).Select(x => x.Title).Where(t => !string.IsNullOrWhiteSpace(t));
                var label = string.IsNullOrWhiteSpace(gName)
                    ? string.Join(" & ", titles) + " (grouped)"
                    : $"{gName} — {string.Join(" & ", titles)}";
                options.Add((f.Id, label));
            }
            else
            {
                options.Add((f.Id, f.Title));
            }
        }
        if (options.Count == 0)
        {
            MessageBox.Show("No other fences to group with. Create another fence first.", "FenceDesk");
            return null;
        }
        return ShowPickerDialog("Group with…", "Select a fence or group to attach to:", options);
    }

    private static string? ShowPickerDialog(string title, string prompt, List<(string Id, string Label)> options)
    {
        var win = new Window
        {
            Title = title,
            Width = 400,
            Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(30, 36, 48)),
            ShowInTaskbar = false
        };
        var grid = new System.Windows.Controls.Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

        var lbl = new System.Windows.Controls.TextBlock
        {
            Text = prompt,
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(200, 208, 220)),
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap
        };
        System.Windows.Controls.Grid.SetRow(lbl, 0);

        var list = new System.Windows.Controls.ListBox
        {
            DisplayMemberPath = "Label",
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(24, 30, 42)),
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(200, 208, 220))
        };
        foreach (var o in options)
            list.Items.Add(new { o.Id, o.Label });
        if (list.Items.Count > 0) list.SelectedIndex = 0;
        System.Windows.Controls.Grid.SetRow(list, 1);

        string? result = null;
        var sp = new System.Windows.Controls.StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        System.Windows.Controls.Grid.SetRow(sp, 2);
        var ok = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        ok.Click += (_, _) =>
        {
            if (list.SelectedItem is null) return;
            var idProp = list.SelectedItem.GetType().GetProperty("Id");
            result = idProp?.GetValue(list.SelectedItem) as string;
            win.DialogResult = true;
            win.Close();
        };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        cancel.Click += (_, _) => win.Close();
        sp.Children.Add(ok);
        sp.Children.Add(cancel);
        grid.Children.Add(lbl);
        grid.Children.Add(list);
        grid.Children.Add(sp);
        win.Content = grid;
        win.ShowDialog();
        return result;
    }

    public void SetAllBackgroundColor(string hex)
    {
        foreach (var f in _layout.Layout.Fences)
        {
            f.BgColor = hex;
            _layout.UpdateFence(f);
            if (_windows.TryGetValue(f.Id, out var w))
                w.ApplyGlassAppearance();
        }
        _layout.SaveImmediate();
    }

    /// <summary>Reset background + text colors on every fence to defaults.</summary>
    public void ResetAllFenceColors()
    {
        foreach (var f in _layout.Layout.Fences)
        {
            f.BgColor = FenceWindow.DefaultBgColor;
            f.TextColor = FenceWindow.DefaultTextColor;
            _layout.UpdateFence(f);
            if (_windows.TryGetValue(f.Id, out var w))
            {
                w.ApplyGlassAppearance();
                w.RefreshContent();
            }
        }
        _layout.SaveImmediate();
    }

    /// <summary>Legacy name — resets background and text.</summary>
    public void ResetAllBackgroundColors() => ResetAllFenceColors();

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
        try { StopDesktopDoubleClickWatch(); } catch { /* ignore */ }
        try { StopShowDesktopGuard(); } catch { /* ignore */ }
        try { _layout.SaveImmediate(); } catch { /* ignore */ }
        try { CloseAll(); } catch { /* ignore */ }
        Exiting?.Invoke(this, EventArgs.Empty);
    }

    private void StartShowDesktopGuard()
    {
        _showDesktopTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _showDesktopTimer.Tick += (_, _) => RestoreAfterShowDesktop();
        _showDesktopTimer.Start();
        AppLog.Write("Fence Win+D guard started (smart z-order)");
    }

    private void StopShowDesktopGuard()
    {
        _showDesktopTimer?.Stop();
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
                var needs = !w.IsVisible || w.WindowState == WindowState.Minimized;
                if (hwnd != IntPtr.Zero && DesktopPin.NeedsShowDesktopRepair(hwnd)) needs = true;
                if (needs || w.Topmost != wantTop)
                    w.ApplyDesktopChrome(useTopmost: wantTop);
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
                MessageBox.Show("Portal folder is not available.", "FenceDesk");
                return;
            }
            foreach (var p in paths)
            {
                try
                {
                    if (!File.Exists(p) && !Directory.Exists(p)) continue;
                    var dest = Path.Combine(destRoot, Path.GetFileName(p));
                    if (File.Exists(dest) || Directory.Exists(dest)) continue;
                    if (Directory.Exists(p)) CopyDirectory(p, dest);
                    else File.Copy(p, dest, overwrite: false);
                }
                catch (Exception ex) { AppLog.Write($"Portal drop failed: {ex.Message}"); }
            }
            if (_windows.TryGetValue(fenceId, out var win)) win.RefreshContent();
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
            tab.Items.Add(new FenceItem { Path = norm, Label = _icons.GetDisplayLabel(norm) });
            existing.Add(norm);
        }
        _layout.UpdateFence(model);
        if (_windows.TryGetValue(fenceId, out var w)) w.RefreshContent();
        try { _desktopIcons.SyncVisibility(); } catch { /* ignore */ }
    }

    public void RemoveItem(string fenceId, string path) =>
        RemoveItems(fenceId, new[] { path });

    public void RemoveItems(string fenceId, IEnumerable<string> paths)
    {
        var model = _layout.FindFence(fenceId);
        if (model is null || model.IsPortal) return;
        var tab = model.GetActiveTab();
        if (tab is null) return;
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var set = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
        if (set.Count == 0) return;

        // Capture exact stored paths (for restore) before removing
        var removed = tab.Items.Where(i => set.Contains(i.Path)).Select(i => i.Path).ToList();
        foreach (var p in list)
            if (!removed.Contains(p, StringComparer.OrdinalIgnoreCase))
                removed.Add(p);

        tab.Items.RemoveAll(i => set.Contains(i.Path));
        _layout.UpdateFence(model);
        if (_windows.TryGetValue(fenceId, out var w)) w.RefreshContent();

        // Critical: put desktop icons back (unhide / move out of shelve).
        // Without this, drag-out removes the fence tile and leaves the real icon hidden —
        // common on Windows 11 where shelve/hidden restore is easy to miss.
        try { _desktopIcons.RestoreDesktopIcons(removed); }
        catch (Exception ex) { AppLog.Write($"RemoveItems restore: {ex.Message}"); }
    }

    /// <summary>
    /// Deletes files/folders from disk (used by portal fences and optional hard-delete).
    /// Returns count successfully deleted.
    /// </summary>
    public int DeleteFromDisk(IEnumerable<string> paths, bool confirm = true)
    {
        var list = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => _desktopIcons.ResolveItemPath(p))
            .Where(p => !DesktopIconService.IsShellNamespacePath(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (list.Count == 0) return 0;

        if (confirm)
        {
            var preview = list.Count == 1
                ? Path.GetFileName(list[0])
                : $"{list.Count} items";
            var msg = list.Count == 1
                ? $"Permanently delete \"{preview}\"?\n\n{list[0]}"
                : $"Permanently delete {preview}?\n\n" + string.Join("\n", list.Take(8).Select(Path.GetFileName))
                  + (list.Count > 8 ? "\n…" : "");
            if (MessageBox.Show(msg, "FenceDesk", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                != MessageBoxResult.Yes)
                return 0;
        }

        var deleted = 0;
        foreach (var p in list)
        {
            try
            {
                if (File.Exists(p))
                {
                    File.SetAttributes(p, FileAttributes.Normal);
                    File.Delete(p);
                    deleted++;
                }
                else if (Directory.Exists(p))
                {
                    Directory.Delete(p, recursive: true);
                    deleted++;
                }
            }
            catch (Exception ex)
            {
                AppLog.Write($"Delete failed ({p}): {ex.Message}");
                MessageBox.Show($"Could not delete:\n{p}\n\n{ex.Message}", "FenceDesk",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        return deleted;
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
