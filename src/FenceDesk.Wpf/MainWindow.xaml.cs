using System.Windows;
using System.Windows.Interop;
using FenceDesk.Native;
using FenceDesk.Services;

namespace FenceDesk;

public partial class MainWindow : Window
{
    private readonly FenceManager _manager;
    private bool _forceClose;
    private bool _userOpen;

    public MainWindow(FenceManager manager)
    {
        _manager = manager;
        InitializeComponent();
        SourceInitialized += (_, _) => ExcludeAltTab();
        Loaded += (_, _) => ExcludeAltTab();
        Closing += MainWindow_Closing;
    }

    public void ShowPanel()
    {
        _userOpen = true;
        ShowInTaskbar = false;
        WindowState = WindowState.Normal;
        Show();
        Activate();
        ExcludeAltTab();
    }

    public void HidePanel()
    {
        _userOpen = false;
        Hide();
    }

    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    private void ExcludeAltTab()
    {
        try
        {
            ShowInTaskbar = false;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) hwnd = new WindowInteropHelper(this).EnsureHandle();
            NativeMethods.ExcludeFromAltTab(hwnd);
        }
        catch { /* ignore */ }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_forceClose || _manager.IsExiting) return;
        var r = MessageBox.Show(
            "Exit FenceDesk and close all fences?\n\nClick No to keep running (panel will hide).",
            "FenceDesk",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes)
        {
            _manager.ExitApplication();
        }
        else
        {
            e.Cancel = true;
            HidePanel();
        }
    }

    private void BtnHide_Click(object sender, RoutedEventArgs e) => _manager.HideAll();
    private void BtnFront_Click(object sender, RoutedEventArgs e) => _manager.BringToFront();
    private void BtnNew_Click(object sender, RoutedEventArgs e) => _manager.NewFenceFromTray();
    private void BtnColorAll_Click(object sender, RoutedEventArgs e) =>
        _manager.ShowAppearanceEditor(applyToAllByDefault: true);
    private void BtnReset_Click(object sender, RoutedEventArgs e) => _manager.ResetAllBackgroundColors();
    private void BtnExit_Click(object sender, RoutedEventArgs e)
    {
        _forceClose = true;
        _manager.ExitApplication();
    }
}
