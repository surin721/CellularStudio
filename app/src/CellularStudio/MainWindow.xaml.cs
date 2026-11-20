using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CellularStudio.ViewModels;

namespace CellularStudio;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };

    public MainWindow()
    {
        InitializeComponent();
        FitToWorkArea();
        DataContext = _vm;
        _vm.PropertyChanged += OnVmChanged;
        SyncNav();
        UpdateStatusDot();

        SourceInitialized += (_, _) => ApplyRoundedCorners();
        StateChanged += (_, _) => OnStateChanged();
        IsVisibleChanged += (_, e) => Trace($"IsVisibleChanged -> {e.NewValue}");
        _clock.Tick += (_, _) => _vm.Tick();
        _clock.Start();
    }

    public MainViewModel ViewModel => _vm;

    /// <summary>Raised when the close button hid the window instead of exiting.</summary>
    public event Action? HiddenToTray;

    internal static void Trace(string what)
    {
        try
        {
            var dir = Cellular.Core.AppStorage.Root;
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "trace.log"), $"{DateTime.Now:HH:mm:ss.fff} {what}\n{Environment.StackTrace}\n\n");
        }
        catch { }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        Trace($"OnClosing exiting={App.IsExiting}");
        if (!App.IsExiting)
        {
            // Close = minimize to tray. Exit lives in the tray menu.
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke();
            return;
        }
        base.OnClosing(e);
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentPage)) SyncNav();
        if (e.PropertyName == nameof(MainViewModel.StatusKind)) UpdateStatusDot();
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return; // fires during InitializeComponent
        _vm.CurrentPage = sender == NavMessages ? "Messages"
            : sender == NavUssd ? "USSD"
            : sender == NavDevice ? "Device"
            : "Overview";
    }

    private void SyncNav()
    {
        NavOverview.IsChecked = _vm.CurrentPage == "Overview";
        NavMessages.IsChecked = _vm.CurrentPage == "Messages";
        NavUssd.IsChecked = _vm.CurrentPage == "USSD";
        NavDevice.IsChecked = _vm.CurrentPage == "Device";
    }

    private void UpdateStatusDot()
    {
        StatusDot.Fill = (Brush)FindResource(_vm.StatusKind switch
        {
            "ok" => "Success",
            "warn" => "Warning",
            _ => "Danger",
        });
    }

    /// <summary>Keeps the initial window inside the screen on high-DPI / small displays.</summary>
    private void FitToWorkArea()
    {
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(Width, wa.Width - 48);
        Height = Math.Min(Height, wa.Height - 48);
        MinWidth = Math.Min(MinWidth, Width);
        MinHeight = Math.Min(MinHeight, Height);
    }

    // ── custom chrome ──
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnStateChanged()
    {
        // With WindowChrome a maximized window overhangs the screen by the resize border.
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        MaxButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplyRoundedCorners()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
        }
        catch { /* pre-Windows 11 */ }
    }
}
