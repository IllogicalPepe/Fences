using System.Drawing;
using System.Windows.Forms;

namespace FenceDesk.Services;

public sealed class TrayService : IDisposable
{
    private readonly FenceManager _manager;
    private readonly Action _showControlPanel;
    private NotifyIcon? _notify;

    public TrayService(FenceManager manager, Action showControlPanel)
    {
        _manager = manager;
        _showControlPanel = showControlPanel;
    }

    public void Initialize()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("New fence", null, (_, _) => Safe(() => _manager.NewFenceFromTray()));
        menu.Items.Add("New portal fence...", null, (_, _) => Safe(() =>
        {
            var folder = FenceManager.PickFolder("Select folder for new portal fence");
            if (folder is not null) _manager.NewPortalFence(folder);
        }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Show fences", null, (_, _) => Safe(_manager.ShowAll));
        menu.Items.Add("Hide fences", null, (_, _) => Safe(_manager.HideAll));
        menu.Items.Add("Bring fences to front", null, (_, _) => Safe(_manager.BringToFront));
        menu.Items.Add("Lock all fences", null, (_, _) => Safe(() => _manager.SetAllLocked(true)));
        menu.Items.Add("Unlock all fences", null, (_, _) => Safe(() => _manager.SetAllLocked(false)));
        menu.Items.Add("Background color (all fences)...", null, (_, _) => Safe(() =>
        {
            var seed = _manager.LayoutStore.Layout.Fences.FirstOrDefault()?.BgColor ?? "#0F1724";
            var c = FenceManager.PickColor(seed);
            if (c is not null)
                _manager.SetAllBackgroundColor(FenceManager.ToHex(c.Value.R, c.Value.G, c.Value.B));
        }));
        menu.Items.Add("Reset all fence colors (bg + text)", null, (_, _) => Safe(_manager.ResetAllFenceColors));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open config folder", null, (_, _) => Safe(() =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_manager.LayoutStore.DataDir}\"",
                UseShellExecute = true
            });
        }));

        var autostart = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
        autostart.Checked = AutostartService.IsEnabled();
        autostart.CheckedChanged += (_, _) =>
        {
            try
            {
                AutostartService.SetEnabled(autostart.Checked);
                _manager.LayoutStore.Layout.Settings.StartWithWindows = autostart.Checked;
                _manager.LayoutStore.SaveImmediate();
            }
            catch { /* ignore */ }
        };
        menu.Items.Add(autostart);
        menu.Items.Add("About FenceDesk", null, (_, _) =>
        {
            System.Windows.Forms.MessageBox.Show(
                "FenceDesk — desktop fence organizer\nC# + WPF port.\n\n" +
                "Right-click a fence for options.\nDrop files onto fences.\n" +
                "Left-click this tray icon for the control panel.",
                "FenceDesk", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit (close completely)", null, (_, _) => Safe(_manager.ExitApplication));

        _notify = new NotifyIcon
        {
            Text = "FenceDesk",
            Visible = true,
            ContextMenuStrip = menu,
            Icon = LoadIcon()
        };
        _notify.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                Safe(_showControlPanel);
        };

    }

    private static Icon LoadIcon()
    {
        try
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Assets", "FenceDesk.ico"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Assets", "FenceDesk.ico")
            };
            foreach (var p in candidates)
            {
                var full = Path.GetFullPath(p);
                if (File.Exists(full)) return new Icon(full);
            }
        }
        catch { /* ignore */ }

        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.FromArgb(255, 30, 48, 80));
            using var brush = new SolidBrush(System.Drawing.Color.FromArgb(255, 160, 190, 230));
            g.FillRectangle(brush, 3, 3, 5, 4);
            g.FillRectangle(brush, 9, 3, 4, 4);
            g.FillRectangle(brush, 3, 9, 10, 4);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex) { AppLog.Write(ex.Message); }
    }

    public void Dispose()
    {
        if (_notify is null) return;
        _notify.Visible = false;
        _notify.Dispose();
        _notify = null;
    }
}
