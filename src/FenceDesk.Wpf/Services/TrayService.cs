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
        menu.Items.Add("New portal fence…", null, (_, _) => Safe(() =>
        {
            var folder = FenceManager.PickFolder("Select folder for new portal fence");
            if (folder is not null) _manager.NewPortalFence(folder);
        }));
        menu.Items.Add(new ToolStripSeparator());
        var visibility = new ToolStripMenuItem("Visibility");
        visibility.DropDownItems.Add("Show fences", null, (_, _) => Safe(_manager.BringToFront));
        visibility.DropDownItems.Add("Hide fences", null, (_, _) => Safe(_manager.HideAll));
        menu.Items.Add(visibility);
        menu.Items.Add(new ToolStripSeparator());

        var appearance = new ToolStripMenuItem("Appearance");
        appearance.DropDownItems.Add("Appearance…", null, (_, _) => Safe(() => _manager.ShowAppearanceEditor(applyToAllByDefault: true)));
        appearance.DropDownItems.Add("Reset colors", null, (_, _) => Safe(_manager.ResetAllFenceColors));
        menu.Items.Add(appearance);

        var locking = new ToolStripMenuItem("Locking");
        locking.DropDownItems.Add("Lock all", null, (_, _) => Safe(() => _manager.SetAllLocked(true)));
        locking.DropDownItems.Add("Unlock all", null, (_, _) => Safe(() => _manager.SetAllLocked(false)));
        menu.Items.Add(locking);

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
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Safe(_manager.ExitApplication));

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
