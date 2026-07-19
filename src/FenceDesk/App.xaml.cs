using System.Threading;
using FenceDesk.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace FenceDesk;

public partial class App : Application
{
    private MainWindow? _controlPanel;
    private FenceManager? _manager;
    private TrayService? _tray;
    private Mutex? _mutex;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            AppLog.Write($"Unhandled: {e.Message}");
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Single instance
        _mutex = new Mutex(true, @"Local\FenceDesk_SingleInstance", out var created);
        if (!created)
        {
            AppLog.Write("Another instance is already running.");
            Exit();
            return;
        }

        try
        {
            var layout = new LayoutStore();
            layout.Load();

            var desktopIcons = new DesktopIconService(layout);
            var icons = new IconService(desktopIcons);
            var portals = new PortalService();
            portals.SetDispatcher(DispatcherQueue.GetForCurrentThread());

            _manager = new FenceManager(layout, icons, desktopIcons, portals);

            _controlPanel = new MainWindow(_manager);
            // Keep a real window HWND alive for the process; show then hide control panel
            _controlPanel.Activate();

            _tray = new TrayService(_manager, () => _controlPanel?.ShowPanel());
            _tray.Initialize();

            _manager.Exiting += (_, _) =>
            {
                try { _tray?.Dispose(); } catch { /* ignore */ }
                try { _controlPanel?.ForceClose(); } catch { /* ignore */ }
                try { Exit(); } catch { Environment.Exit(0); }
            };

            // Fences first so layout is stable before desktop hide/shelve sync
            _manager.InitializeAll();
            try { desktopIcons.Initialize(); }
            catch (Exception ex) { AppLog.Write($"Desktop icon init: {ex.Message}"); }

            // Hide control panel after fences are up (tray owns day-to-day UI)
            _controlPanel.HidePanel();

            AppLog.Write($"FenceDesk WinUI starting — {_manager.LayoutStore.Layout.Fences.Count} fence(s)");
        }
        catch (Exception ex)
        {
            AppLog.Write($"Startup failed: {ex}");
            System.Windows.Forms.MessageBox.Show(
                $"FenceDesk failed to start:\n{ex.Message}",
                "FenceDesk");
            Exit();
        }
    }
}
