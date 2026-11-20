using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Cellular.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CellularStudio.ViewModels;

public sealed partial class MainViewModel
{
    private readonly UssdService _ussd = new();
    private string? _sessionStartCode;

    public ObservableCollection<UssdBubble> UssdTranscript { get; } = [];
    public ObservableCollection<MenuOption> MenuOptions { get; } = [];
    public ObservableCollection<QuickCode> QuickCodes { get; } = [];
    public ObservableCollection<UssdLogEntry> UssdHistory { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendUssdCommand))]
    private string _ussdInput = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendUssdCommand), nameof(RunCodeCommand), nameof(CheckBalanceCommand))]
    [NotifyPropertyChangedFor(nameof(UssdPlaceholder))]
    private bool _ussdBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UssdPlaceholder), nameof(UssdSendLabel))]
    private bool _ussdAwaitingReply;

    /// <summary>True when the modem is on LTE and the user hasn't opted in to USSD there.</summary>
    public bool UssdBlocked => Status.IsRegistered && Status.NetworkType == "LTE" && !AllowUssdOnLte;

    public bool AllowUssdOnLte
    {
        get => _data.AllowUssdOnLte;
        set
        {
            if (_data.AllowUssdOnLte == value) return;
            _data.AllowUssdOnLte = value;
            Save();
            Log("USSD", value ? "USSD on LTE enabled by user" : "USSD on LTE disabled");
            OnPropertyChanged();
            OnPropertyChanged(nameof(UssdBlocked));
        }
    }

    public string UssdPlaceholder => UssdAwaitingReply ? "Type your reply (e.g. 1) and press Enter" : "Enter a code such as *344#";
    public string UssdSendLabel => UssdAwaitingReply ? "Reply" : "Send";

    private void InitUssd()
    {
        foreach (var h in _data.UssdHistory.OrderByDescending(h => h.Timestamp).Take(50)) UssdHistory.Add(h);
        UpdateQuickCodes();
    }

    private void UpdateQuickCodes()
    {
        var list = new List<QuickCode>();
        if (!string.IsNullOrEmpty(Brand.BalanceCode)) list.Add(new(Brand.BalanceCode, "Balance", ""));
        if (!string.IsNullOrEmpty(Brand.MenuCode)) list.Add(new(Brand.MenuCode, "Services menu", ""));
        if (!string.IsNullOrEmpty(_data.DataBalanceCode)) list.Add(new(_data.DataBalanceCode!, "Data balance", ""));
        if (QuickCodes.Select(q => q.Code).SequenceEqual(list.Select(q => q.Code))) return;
        QuickCodes.Clear();
        foreach (var q in list) QuickCodes.Add(q);
        CheckBalanceCommand.NotifyCanExecuteChanged();
        OpenServicesMenuCommand.NotifyCanExecuteChanged();
    }

    private bool CanSendUssd() => !UssdBusy && !string.IsNullOrWhiteSpace(UssdInput);

    [RelayCommand(CanExecute = nameof(CanSendUssd))]
    private Task SendUssd() => RunUssdAsync(UssdInput.Trim());

    private bool CanRunCode(string? _) => !UssdBusy;

    [RelayCommand(CanExecute = nameof(CanRunCode))]
    private Task RunCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return Task.CompletedTask;
        CurrentPage = "USSD";
        return RunUssdAsync(code, newSession: true);
    }

    [RelayCommand]
    private Task ChooseOption(MenuOption opt) => RunUssdAsync(opt.Key);

    private bool CanOpenServicesMenu() => !string.IsNullOrEmpty(Brand.MenuCode);

    [RelayCommand(CanExecute = nameof(CanOpenServicesMenu))]
    private Task OpenServicesMenu()
    {
        CurrentPage = "USSD";
        return RunUssdAsync(Brand.MenuCode, newSession: true);
    }

    [RelayCommand]
    private async Task EndUssd()
    {
        await _ussd.EndAsync();
        if (UssdAwaitingReply)
            UssdTranscript.Add(new UssdBubble("Session ended", false, DateTimeOffset.Now, IsSystem: true));
        UssdAwaitingReply = false;
        MenuOptions.Clear();
    }

    [RelayCommand]
    private void ClearTranscript()
    {
        UssdTranscript.Clear();
        MenuOptions.Clear();
    }

    private async Task RunUssdAsync(string text, bool newSession = false)
    {
        if (UssdBusy || string.IsNullOrWhiteSpace(text)) return;

        if (!Status.IsRegistered)
        {
            ShowToast(ToastKind.Error, "USSD unavailable", "The modem isn't registered on a network.");
            UssdTranscript.Add(new UssdBubble("Not sent — the modem isn't registered on a network.", false, DateTimeOffset.Now, IsError: true, IsSystem: true));
            return;
        }

        if (UssdBlocked)
        {
            CurrentPage = "USSD";
            ShowToast(ToastKind.Warning, "USSD isn't available on LTE",
                "With this modem it fails and drops the connection. See the note on the USSD page.");
            UssdTranscript.Add(new UssdBubble($"{text} not sent — USSD doesn't work on LTE with this modem.",
                false, DateTimeOffset.Now, IsError: true, IsSystem: true));
            return;
        }

        if (newSession && UssdAwaitingReply) await EndUssd();
        bool isStart = !UssdAwaitingReply;
        if (isStart)
        {
            _sessionStartCode = text;
            if (UssdTranscript.Count > 0)
                UssdTranscript.Add(new UssdBubble("New session", false, DateTimeOffset.Now, IsSystem: true));
        }

        UssdTranscript.Add(new UssdBubble(text, true, DateTimeOffset.Now));
        UssdInput = "";
        MenuOptions.Clear();
        UssdBusy = true;
        Log("USSD", $"→ {text}");
        try
        {
            var r = await _ussd.SendAsync(text);
            var body = r.Text.Trim();
            if (r.State == UssdState.Error)
            {
                var msg = string.IsNullOrEmpty(body) ? r.Error ?? "Request failed" : $"{body}\n\n{r.Error}";
                UssdTranscript.Add(new UssdBubble(msg, false, DateTimeOffset.Now, IsError: true));
                UssdAwaitingReply = false;
                Log("USSD", $"✕ {r.Error}");
                Record(text, r.Error ?? "Error", failed: true);
                ShowToast(ToastKind.Error, "USSD failed", r.Error);
                _ = CheckRegistrationAfterUssdAsync();
                return;
            }

            UssdTranscript.Add(new UssdBubble(string.IsNullOrEmpty(body) ? "(empty reply)" : body, false, DateTimeOffset.Now));
            UssdAwaitingReply = r.State == UssdState.AwaitingReply;
            Log("USSD", $"← {(body.Length > 80 ? body[..80] + "…" : body).ReplaceLineEndings(" ")}");
            Record(isStart ? text : $"{_sessionStartCode} › {text}", body, failed: false);

            if (UssdAwaitingReply)
                foreach (var o in ParseMenu(body)) MenuOptions.Add(o);

            TryCaptureBalance(_sessionStartCode ?? text, body);
        }
        finally
        {
            UssdBusy = false;
        }
    }

    /// <summary>
    /// A failed USSD on this modem has dropped LTE registration before (2026-11-20). If that happens,
    /// re-register right away instead of waiting for the periodic auto-recovery.
    /// </summary>
    private async Task CheckRegistrationAfterUssdAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(6));
        await RefreshStatusAsync();
        if (Status.IsRegistered) return;
        Log("Network", "Registration lost after USSD — reconnecting");
        UssdTranscript.Add(new UssdBubble("The modem dropped off the network after this request. Reconnecting…",
            false, DateTimeOffset.Now, IsSystem: true));
        _lastAutoRecover = DateTimeOffset.Now;
        await RecoverRegistrationAsync(manual: false);
        UssdTranscript.Add(new UssdBubble(Status.IsRegistered ? "Back on the network" : "Still not registered — use Reconnect",
            false, DateTimeOffset.Now, IsError: !Status.IsRegistered, IsSystem: true));
    }

    private void Record(string request, string response, bool failed)
    {
        var e = new UssdLogEntry { Request = request, Response = response, Failed = failed, Timestamp = DateTimeOffset.Now };
        _data.UssdHistory.Add(e);
        if (_data.UssdHistory.Count > 200) _data.UssdHistory.RemoveRange(0, _data.UssdHistory.Count - 200);
        UssdHistory.Insert(0, e);
        while (UssdHistory.Count > 50) UssdHistory.RemoveAt(UssdHistory.Count - 1);
        Save();
    }

    private static readonly Regex MenuLine = new(@"^\s*(\d{1,3}|#|\*)\s*[\.\):\-]\s*(.+?)\s*$", RegexOptions.Multiline);

    public static IEnumerable<MenuOption> ParseMenu(string text) =>
        MenuLine.Matches(text).Select(m => new MenuOption(m.Groups[1].Value, m.Groups[2].Value)).Take(12);
}
