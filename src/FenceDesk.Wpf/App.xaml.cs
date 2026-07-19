using System.Threading;
using System.Windows;
using FenceDesk.Services;

namespace FenceDesk;

public partial class App : Application
{
    private Mutex? _mutex;
    private MainWindow? _controlPanel;
    private FenceManager? _manager;
    private TrayService? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            AppLog.Write($"AppDomain unhandled: {args.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Write($"Task unhandled: {args.Exception}");
            args.SetObserved();
        };
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Write($"Dispatcher unhandled: {args.Exception}");
            args.Handled = true;
        };

        _mutex = new Mutex(true, @"Local\FenceDesk_SingleInstance", out var created);
        if (!created)
        {
            AppLog.Write("Another instance is already running.");
            MessageBox.Show("FenceDesk is already running.\nCheck the tray icon near the clock.", "FenceDesk");
            Shutdown();
            return;
        }

        try
        {
            // Dev/self-test: flash-free toggle path under load
            if (e.Args.Any(a => string.Equals(a, "--stress-toggle", StringComparison.OrdinalIgnoreCase)))
            {
                RunStressToggle();
                Shutdown();
                return;
            }

            AppLog.Write("WPF OnStartup begin");
            var layout = new LayoutStore();
            layout.Load();
            AppLog.Write($"Layout loaded: {layout.Layout.Fences.Count} fences");

            var desktopIcons = new DesktopIconService(layout);
            var icons = new IconService(desktopIcons);
            var portals = new PortalService();
            portals.SetDispatcher(Dispatcher);

            _manager = new FenceManager(layout, icons, desktopIcons, portals);

            _controlPanel = new MainWindow(_manager);
            // Create HWND then hide — tray only on launch (no options window)
            _controlPanel.Show();
            _controlPanel.HidePanel();
            AppLog.Write("Control panel ready (hidden; tray only)");

            _tray = new TrayService(_manager, () => _controlPanel?.ShowPanel());
            _tray.Initialize();
            AppLog.Write("Tray ready");

            _manager.Exiting += (_, _) =>
            {
                try { _tray?.Dispose(); } catch { /* ignore */ }
                try { _controlPanel?.ForceClose(); } catch { /* ignore */ }
                try { Shutdown(); } catch { Environment.Exit(0); }
            };

            _manager.InitializeAll();
            AppLog.Write("Fences initialized");
            AppLog.Write($"FenceDesk WPF starting — {_manager.LayoutStore.Layout.Fences.Count} fence(s)");
        }
        catch (Exception ex)
        {
            AppLog.Write($"Startup failed: {ex}");
            MessageBox.Show($"FenceDesk failed to start:\n{ex.Message}", "FenceDesk");
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _tray?.Dispose(); } catch { /* ignore */ }
        try { _mutex?.ReleaseMutex(); _mutex?.Dispose(); } catch { /* ignore */ }
        AppLog.Write("FenceDesk exited");
        base.OnExit(e);
    }

    /// <summary>
    /// Self-test: rapid Hide/Show without WH_MOUSE_LL or layered windows.
    /// Exit code 0 = survived; logs timing.
    /// </summary>
    private void RunStressToggle()
    {
        AppLog.Write("STRESS-TOGGLE begin");
        var layout = new LayoutStore();
        layout.Load();
        var desktopIcons = new DesktopIconService(layout);
        var icons = new IconService(desktopIcons);
        var portals = new PortalService();
        portals.SetDispatcher(Dispatcher);
        var manager = new FenceManager(layout, icons, desktopIcons, portals);
        // No desktop click watcher — only visual toggle path
        foreach (var model in layout.Layout.Fences.ToList())
        {
            model.EnsureDefaults();
            manager.CreateFenceWindow(model);
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 40; i++)
        {
            manager.HideAll();
            DoEvents();
            manager.ShowAll();
            DoEvents();
        }
        sw.Stop();
        AppLog.Write($"STRESS-TOGGLE ok 40 cycles in {sw.ElapsedMilliseconds}ms");
        manager.ExitApplication();
    }

    private void DoEvents()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
}
