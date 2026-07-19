using FenceDesk.Native;
using FenceDesk.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace FenceDesk;

public sealed partial class MainWindow : Window
{
    private readonly FenceManager _manager;
    private AppWindow? _appWindow;
    private bool _userOpen;
    private bool _forceClose;

    public MainWindow(FenceManager manager)
    {
        _manager = manager;
        InitializeComponent();

        _appWindow = GetAppWindow();
        if (_appWindow is not null)
        {
            _appWindow.IsShownInSwitchers = false;
            _appWindow.Resize(new SizeInt32(360, 440));
            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(true, true);
                presenter.IsMaximizable = false;
            }
        }

        Title = " ";
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
                ApplyChrome();
        };

        Closed += MainWindow_Closed;
    }

    public void ShowPanel()
    {
        _userOpen = true;
        _appWindow?.Show();
        ApplyChrome();
        Activate();
    }

    public void HidePanel()
    {
        _userOpen = false;
        _appWindow?.Hide();
    }

    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    private void ApplyChrome()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        NativeMethods.ExcludeFromAltTab(hwnd);
        if (_appWindow is not null)
            _appWindow.IsShownInSwitchers = false;
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

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_forceClose || _manager.IsExiting) return;

        var r = System.Windows.Forms.MessageBox.Show(
            "Exit FenceDesk and close all fences?\n\nClick No to keep running (panel will hide).",
            "FenceDesk",
            System.Windows.Forms.MessageBoxButtons.YesNo,
            System.Windows.Forms.MessageBoxIcon.Question);

        if (r == System.Windows.Forms.DialogResult.Yes)
        {
            args.Handled = false;
            _manager.ExitApplication();
        }
        else
        {
            args.Handled = true;
            HidePanel();
        }
    }

    private void BtnShow_Click(object sender, RoutedEventArgs e) => _manager.ShowAll();
    private void BtnHide_Click(object sender, RoutedEventArgs e) => _manager.HideAll();
    private void BtnFront_Click(object sender, RoutedEventArgs e) => _manager.BringToFront();
    private void BtnNew_Click(object sender, RoutedEventArgs e) => _manager.NewFenceFromTray();

    private void BtnColorAll_Click(object sender, RoutedEventArgs e)
    {
        var seed = _manager.LayoutStore.Layout.Fences.FirstOrDefault()?.BgColor ?? "#0F1724";
        var c = FenceManager.PickColor(seed);
        if (c is null) return;
        _manager.SetAllBackgroundColor(FenceManager.ToHex(c.Value.R, c.Value.G, c.Value.B));
    }

    private void BtnResetColors_Click(object sender, RoutedEventArgs e) =>
        _manager.ResetAllBackgroundColors();

    private void BtnExit_Click(object sender, RoutedEventArgs e)
    {
        _forceClose = true;
        _manager.ExitApplication();
    }
}
