using System.Windows;
using System.Windows.Threading;
using CellularStudio.Services;
using CellularStudio.ViewModels;

namespace CellularStudio;

public partial class App : Application
{
    private const string InstanceName = "CellularStudio.SingleInstance.v1";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateSignal;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private bool _hideHintShown;

    /// <summary>True once the user chose Exit; window close then really closes.</summary>
    public static bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single instance: a second launch just brings the running one to the front.
        _instanceMutex = new Mutex(true, InstanceName, out bool isFirst);
        if (!isFirst)
        {
            try { EventWaitHandle.OpenExisting(InstanceName + ".Activate").Set(); } catch { }
            Shutdown();
            return;
        }
        _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Activate");
        new Thread(() =>
        {
            while (_activateSignal.WaitOne())
            {
                if (IsExiting) return;
                Dispatcher.BeginInvoke(ShowMainWindow);
            }
        }) { IsBackground = true, Name = "ActivateListener" }.Start();

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnUnhandled;
        StartupRegistration.Repair();

        _window = new MainWindow();
        MainWindow = _window;
        var vm = _window.ViewModel;
        _tray = new TrayIcon(vm, ShowMainWindow, ExitApp);
        vm.IncomingMessage += OnIncomingMessage;
        _window.HiddenToTray += OnHiddenToTray;

        bool startHidden = e.Args.Any(a => a.Equals(StartupRegistration.TrayArg, StringComparison.OrdinalIgnoreCase));
        if (!startHidden) _window.Show();
        else _hideHintShown = true; // launched at sign-in: no "still running" hint

        _ = vm.StartAsync();
    }

    private void ShowMainWindow()
    {
        if (_window is null) return;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;  // reliably bring to front
        _window.Topmost = false;
        _window.Focus();
    }

    private void OnHiddenToTray()
    {
        if (_hideHintShown) return;
        _hideHintShown = true;
        _tray?.Notify("Cellular Studio is still running",
            "It keeps receiving messages in the background. Use the tray icon to open it again or to exit.");
    }

    private void OnIncomingMessage(string from, string body, Conversation c)
    {
        if (_window is { IsVisible: true, IsActive: true }) return; // in-app toast is enough
        _tray?.Notify($"New message from {PhoneFormat.Pretty(from)}", body.Length > 200 ? body[..200] + "…" : body,
            onClick: () =>
            {
                ShowMainWindow();
                if (_window?.ViewModel is { } vm) { vm.CurrentPage = "Messages"; vm.SelectedConversation = c; }
            });
    }

    private void ExitApp()
    {
        IsExiting = true;
        _activateSignal?.Set(); // release listener thread
        _tray?.Dispose();
        _tray = null;
        _window?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        try { _instanceMutex?.ReleaseMutex(); } catch { }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the app alive on unexpected WinRT/driver errors; surface them in the activity log.
        if (_window?.ViewModel is { } vm)
        {
            vm.Log("Error", e.Exception.Message);
            vm.ShowToast(ToastKind.Error, "Something went wrong", e.Exception.Message);
            e.Handled = true;
        }
    }
}
