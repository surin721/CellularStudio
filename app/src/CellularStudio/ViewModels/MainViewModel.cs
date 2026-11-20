using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Cellular.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CellularStudio.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _pollTimer;
    private readonly AppData _data;
    private bool _polling;
    private RegistrationState? _lastReg;

    public MainViewModel()
    {
        _data = AppStorage.Load();
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _pollTimer.Tick += async (_, _) => await RefreshStatusAsync();

        LoadMessages();
        LoadBalance();
        InitUssd();
    }

    public async Task StartAsync()
    {
        Log("App", "Started");
        await RefreshStatusAsync();
        Capabilities = await ModemCapabilities.ReadAsync();
        BuildCapabilityRows();
        _pollTimer.Start();
        await StartSmsAsync();
    }

    // ───────────────────────── Startup / tray ─────────────────────────
    [ObservableProperty] private bool _runAtStartup = Services.StartupRegistration.IsEnabled;

    /// <summary>Raised for each new incoming SMS (sender, body, conversation) so the shell can notify.</summary>
    public event Action<string, string, Conversation>? IncomingMessage;

    partial void OnRunAtStartupChanged(bool value)
    {
        if (value == Services.StartupRegistration.IsEnabled) return;
        try
        {
            Services.StartupRegistration.Set(value);
            Log("App", value ? "Enabled start with Windows" : "Disabled start with Windows");
            ShowToast(ToastKind.Success, value ? "Will start with Windows" : "Won't start with Windows",
                value ? "It starts hidden, with just the tray icon." : null);
        }
        catch (Exception ex)
        {
            ShowToast(ToastKind.Error, "Couldn't change startup setting", ex.Message);
            RunAtStartup = Services.StartupRegistration.IsEnabled;
        }
    }

    // ───────────────────────── Navigation ─────────────────────────
    [ObservableProperty] private string _currentPage = "Overview";

    [RelayCommand]
    private void Navigate(string page) => CurrentPage = page;

    // ───────────────────────── Modem status ─────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CarrierName), nameof(NetworkLabel), nameof(StatusText), nameof(StatusKind),
        nameof(SignalBars), nameof(SignalText), nameof(IsRegistered), nameof(IsOffline), nameof(OfflineHint), nameof(UssdBlocked))]
    private ModemStatus _status = new() { Registration = RegistrationState.Unknown };

    [ObservableProperty] private ModemCapabilities? _capabilities;
    [ObservableProperty] private CarrierBrand _brand = Carriers.Resolve("", "");
    [ObservableProperty] private string? _logoPath;

    public ObservableCollection<DetailRow> ModemRows { get; } = [];
    public ObservableCollection<DetailRow> SimRows { get; } = [];
    public ObservableCollection<DetailRow> NetworkRows { get; } = [];
    public ObservableCollection<CapabilityRow> CapabilityRows { get; } = [];

    public bool IsRegistered => Status.IsRegistered;
    public bool IsOffline => !Status.IsRegistered;

    public string CarrierName => Status.ModemPresent ? Brand.Name : "No modem";

    public string NetworkLabel => Status.Registration switch
    {
        RegistrationState.Home => string.IsNullOrEmpty(Status.NetworkType) ? "Home network" : $"{Status.NetworkType} · Home network",
        RegistrationState.Roaming => $"{Status.NetworkType} · Roaming",
        RegistrationState.Searching => "Searching…",
        RegistrationState.Deregistered => "Not registered",
        RegistrationState.Denied => "Registration denied",
        RegistrationState.Off => "Radio off",
        RegistrationState.NoModem => "Modem not found",
        _ => "Checking…",
    };

    public string StatusText => Status.Registration switch
    {
        RegistrationState.Home or RegistrationState.Roaming => Status.DataConnected ? "Connected" : "Registered",
        RegistrationState.Searching => "Searching",
        RegistrationState.Off => "Radio off",
        RegistrationState.NoModem => "No modem",
        RegistrationState.Unknown => "Checking",
        _ => "No service",
    };

    /// <summary>ok | warn | error — drives the status dot color.</summary>
    public string StatusKind => Status.Registration switch
    {
        RegistrationState.Home or RegistrationState.Roaming => "ok",
        RegistrationState.Searching or RegistrationState.Unknown => "warn",
        _ => "error",
    };

    public int SignalBars => Status.SignalBars;
    public string SignalText => Status.RssiDbm is int d ? $"{d} dBm" : Status.IsRegistered ? $"{Status.SignalPercent}%" : "No signal";

    public string OfflineHint => Status.Registration switch
    {
        RegistrationState.Off => "The cellular radio is off. Turn on Cellular in Windows quick settings.",
        RegistrationState.NoModem => "No mobile broadband modem was found.",
        RegistrationState.Unknown => "Reading modem state…",
        _ => (Status.RegistrationError != 0
                ? $"The modem is not registered on a network (reject cause #{Status.RegistrationError}). "
                : "The modem is not registered on a network. ")
            + "SMS and USSD are unavailable until it reconnects. Reconnect usually fixes this; the app also retries automatically after 2 minutes.",
    };

    [RelayCommand]
    private Task Refresh() => RefreshStatusAsync();

    // ───────────────────────── Registration recovery ─────────────────────────
    // The XMM7360 can get stuck in a Searching ↔ Deregistered loop (e.g. after a USSD attempt);
    // an explicit "register automatically" request (mbnapi) brings it back within seconds.
    private DateTimeOffset? _offlineSince;
    private DateTimeOffset _lastAutoRecover = DateTimeOffset.MinValue;
    private static readonly TimeSpan AutoRecoverAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AutoRecoverCooldown = TimeSpan.FromMinutes(10);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReconnectCommand))]
    private bool _reconnecting;

    private bool CanReconnect() => !Reconnecting && Status.ModemPresent && Status.RadioOn;

    [RelayCommand(CanExecute = nameof(CanReconnect))]
    private Task Reconnect() => RecoverRegistrationAsync(manual: true);

    private async Task RecoverRegistrationAsync(bool manual)
    {
        if (Reconnecting) return;
        Reconnecting = true;
        Log("Network", manual ? "Reconnect requested" : "Modem stuck searching — requesting automatic registration");
        try
        {
            var ok = await MbnRegistration.RecoverAsync(TimeSpan.FromSeconds(120));
            await RefreshStatusAsync();
            if (ok)
            {
                Log("Network", "Registration recovered");
                if (manual) ShowToast(ToastKind.Success, "Reconnected", $"Registered on {Brand.Name}");
            }
            else
            {
                Log("Network", "Registration request sent, modem still not registered");
                if (manual) ShowToast(ToastKind.Warning, "Still searching",
                    "The modem didn't register yet. If this persists, shut down the PC fully (not restart) and try again.");
            }
        }
        catch (Exception ex)
        {
            Log("Error", $"Reconnect failed: {ex.Message}");
            if (manual) ShowToast(ToastKind.Error, "Reconnect failed", ex.Message);
        }
        finally
        {
            Reconnecting = false;
        }
    }

    private void CheckAutoRecover(ModemStatus s)
    {
        bool stuck = s.ModemPresent && s.RadioOn &&
                     s.Registration is RegistrationState.Searching or RegistrationState.Deregistered or RegistrationState.Denied;
        if (!stuck) { _offlineSince = null; return; }
        _offlineSince ??= DateTimeOffset.Now;
        if (DateTimeOffset.Now - _offlineSince >= AutoRecoverAfter &&
            DateTimeOffset.Now - _lastAutoRecover >= AutoRecoverCooldown && !Reconnecting)
        {
            _lastAutoRecover = DateTimeOffset.Now;
            _ = RecoverRegistrationAsync(manual: false);
        }
    }

    private async Task RefreshStatusAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var s = await Task.Run(() => ModemStatusReader.ReadAsync());
            var prev = _lastReg;
            Status = s;
            var brand = Carriers.Resolve(s.BrandingId, s.CarrierDisplayName, _data.CarrierOverrides);
            if (brand != Brand) Brand = brand;
            LogoPath = Carriers.FindLogo(brand);
            OnPropertyChanged(nameof(CarrierName));
            BuildDetailRows();
            UpdateQuickCodes();

            if (prev is not null && prev != s.Registration)
            {
                Log("Network", $"{prev} → {s.Registration}");
                if (s.IsRegistered && prev is not (RegistrationState.Home or RegistrationState.Roaming))
                    ShowToast(ToastKind.Success, "Back on the network", $"Registered on {brand.Name}");
                else if (!s.IsRegistered && prev is RegistrationState.Home or RegistrationState.Roaming)
                    ShowToast(ToastKind.Warning, "Lost network registration", "SMS and USSD are unavailable for now.");
            }
            _lastReg = s.Registration;
            SendMessageCommand.NotifyCanExecuteChanged();
            ReconnectCommand.NotifyCanExecuteChanged();
            CheckBalanceCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(BalanceMethodText));
            CheckAutoRecover(s);
            CheckSimChanged();
            if (_smsStarted) MaybeAutoRefreshBalance();
            MaybePollInbox();
        }
        catch (Exception ex)
        {
            Log("Error", $"Status read failed: {ex.Message}");
        }
        finally { _polling = false; }
    }

    private void BuildDetailRows()
    {
        var s = Status;
        Replace(ModemRows,
        [
            new("Manufacturer", s.Manufacturer),
            new("Model", s.Model),
            new("Firmware", s.Firmware, true),
            new("IMEI", s.Imei, true),
        ]);
        Replace(SimRows,
        [
            new("Operator (SPN)", Or(s.SimSpn)),
            new("Phone number", Or(s.PhoneNumber, _data.MyNumber ?? "Not stored on SIM")),
            new("ICCID", s.Iccid, true),
            new("IMSI", s.Imsi, true),
        ]);
        Replace(NetworkRows,
        [
            new("Registration", NetworkLabel),
            new("Network", Or(s.ProviderName)),
            new("MCC / MNC", s.ProviderId.Length >= 5 ? $"{s.ProviderId[..3]} / {s.ProviderId[3..]}" : "—"),
            new("Technology", Or(s.NetworkType)),
            new("RSSI", s.RssiDbm is int d ? $"{d} dBm · {SignalQuality(d)}" : "—"),
            new("Signal level", s.IsRegistered ? $"{s.SignalPercent}% · {s.SignalBars}/5 bars" : "—"),
            new("Error rate", s.ErrorRate is int er and <= 7 ? $"Class {er} of 7 ({(er <= 1 ? "good" : er <= 4 ? "fair" : "poor")})" : "—"),
            new("RSRP · RSRQ · SINR", "Not reported by this modem's driver"),
            new("Data connection", s.DataConnected ? "Connected" : "Disconnected"),
        ]);
    }

    /// <summary>Rough RSSI rating for LTE (RSSI includes noise/interference, so it is only indicative).</summary>
    private static string SignalQuality(int rssiDbm) => rssiDbm switch
    {
        >= -65 => "excellent",
        >= -75 => "good",
        >= -85 => "fair",
        >= -95 => "weak",
        _ => "very weak",
    };

    private void BuildCapabilityRows()
    {
        var c = Capabilities;
        if (c is null) return;
        Replace(CapabilityRows,
        [
            new("Mobile data", true, c.DataClasses),
            new("Send SMS", c.CanSendSms, c.CanSendSms ? "PDU mode via MBIM SMS service" : "Not supported"),
            new("Receive SMS", c.CanReceiveSms, c.CanReceiveSms ? "PDU mode via MBIM SMS service" : "Not supported"),
            new("USSD", false, c.HasUssd
                ? "Advertised, but fails on LTE and drops the network connection. Balance is read via SMS instead."
                : "Not supported"),
            new("Voice calls", c.HasVoice, c.HasVoice ? c.VoiceClass
                : "Firmware reports \"No voice\", and M.2 modems have no audio path to the laptop's speakers or microphone."),
        ]);
    }

    private static string Or(string v, string fallback = "—") => string.IsNullOrWhiteSpace(v) ? fallback : v;

    private static void Replace<T>(ObservableCollection<T> target, IList<T> items)
    {
        if (target.SequenceEqual(items)) return;
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    [RelayCommand]
    private void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); ShowToast(ToastKind.Info, "Copied to clipboard"); } catch { }
    }

    // ───────────────────────── Toasts & log ─────────────────────────
    public ObservableCollection<Toast> Toasts { get; } = [];
    public ObservableCollection<LogEntry> ActivityLog { get; } = [];

    public void ShowToast(ToastKind kind, string title, string? message = null)
    {
        var t = new Toast { Kind = kind, Title = title, Message = message };
        Toasts.Add(t);
        while (Toasts.Count > 3) Toasts.RemoveAt(0);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(kind == ToastKind.Error ? 7 : 4) };
        timer.Tick += (_, _) => { timer.Stop(); Toasts.Remove(t); };
        timer.Start();
    }

    [RelayCommand]
    private void DismissToast(Toast t) => Toasts.Remove(t);

    public void Log(string category, string text)
    {
        OnUi(() =>
        {
            ActivityLog.Insert(0, new LogEntry(DateTimeOffset.Now, category, text));
            while (ActivityLog.Count > 300) ActivityLog.RemoveAt(ActivityLog.Count - 1);
        });
        // also keep a rolling file for diagnostics (%LOCALAPPDATA%\CellularStudio\activity.log)
        try
        {
            var path = System.IO.Path.Combine(AppStorage.Root, "activity.log");
            var fi = new System.IO.FileInfo(path);
            if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
            System.IO.File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [{category}] {text}{Environment.NewLine}");
        }
        catch { /* best effort */ }
    }

    private void Save()
    {
        try { AppStorage.Save(_data); }
        catch (Exception ex) { Log("Error", $"Saving data failed: {ex.Message}"); }
    }

    private static void OnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a(); else d.BeginInvoke(a);
    }
}
