using Cellular.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CellularStudio.ViewModels;

/// <summary>
/// Balance for the Overview tiles. USSD doesn't work on LTE with this modem, so for carriers with an SMS
/// balance query (Hutch: "BAL" to 344, free) the app sends that SMS and parses the operator's reply.
/// Any operator message that looks like a balance statement (e.g. reload notices) also updates the tiles.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly TimeSpan BalanceReplyTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan AutoBalanceAge = TimeSpan.FromHours(6);
    private DateTimeOffset? _balanceRequestedAt;
    private DateTimeOffset _lastAutoBalance = DateTimeOffset.MinValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CreditText), nameof(CreditDetail), nameof(DataText), nameof(DataDetail), nameof(ValidityText),
        nameof(ValidityDetail), nameof(BalanceUpdatedText), nameof(HasBalance), nameof(BalanceRaw),
        nameof(DataPackages), nameof(HasDataPackages))]
    private BalanceInfo? _balance;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BalanceUpdatedText))]
    [NotifyCanExecuteChangedFor(nameof(CheckBalanceCommand))]
    private bool _balanceBusy;

    public bool HasBalance => Balance is not null;
    public string BalanceRaw => Balance?.RawText ?? "";

    /// <summary>How the balance is obtained, shown under the quick action.</summary>
    public string BalanceMethodText => UseSmsForBalance
        ? $"SMS {Brand.BalanceSmsText} to {Brand.BalanceSmsNumber} · free"
        : string.IsNullOrEmpty(Brand.BalanceCode) ? "Not available for this carrier" : $"Runs {Brand.BalanceCode}";

    // USSD is unreliable on this modem (drops LTE registration), so prefer the SMS query whenever the carrier has one.
    private bool UseSmsForBalance => Brand.HasSmsBalance;

    public string CreditText => Balance?.Credit is decimal c ? $"Rs. {c:N2}" : "—";

    public string CreditDetail => Balance?.Points is int p
        ? $"Prepaid main balance · {p:N0} points"
        : "Prepaid main balance";

    public string DataText => Balance is { Data.Count: > 0 } b ? BalanceParser.FormatData(b.TotalDataMb) : "—";

    public string DataDetail
    {
        get
        {
            if (Balance is not { Data.Count: > 0 } b) return Balance is null ? "Check balance to see data" : "No data packages in the reply";
            if (b.Data.Count == 1)
                return b.Data[0].Expires is DateTime e1 ? $"{b.Data[0].Label}, {ExpiryPhrase(e1)}" : b.Data[0].Label;
            var next = b.Data.Where(d => d.Expires is not null).OrderBy(d => d.Expires).FirstOrDefault();
            return next?.Expires is DateTime e ? $"{b.Data.Count} packages · next {ExpiryPhrase(e)}" : $"{b.Data.Count} packages";
        }
    }

    /// <summary>Rows for the "Data packages" breakdown, largest first.</summary>
    public IReadOnlyList<DataPackageRow> DataPackages => Balance is { Data.Count: > 0 } b
        ? b.Data.OrderByDescending(d => d.Megabytes).Select(d => new DataPackageRow(
            d.Label, BalanceParser.FormatData(d.Megabytes),
            d.Expires is DateTime ex ? $"Expires {ex:d MMM yyyy}" : "",
            d.Expires is DateTime ex2 ? ExpiryPhrase(ex2) : "",
            d.Expires is DateTime ex3 && (ex3.Date - DateTime.Today).TotalDays <= 1,
            b.TotalDataMb > 0 ? d.Megabytes / b.TotalDataMb : 0)).ToList()
        : [];

    public bool HasDataPackages => DataPackages.Count > 0;

    public string ValidityText => Balance?.Validity is DateTime d ? d.ToString("d MMM yyyy") : "—";

    public string ValidityDetail => Balance?.Validity is DateTime d
        ? $"Main balance {ExpiryPhrase(d)}"
        : "No date in the balance reply";

    private static string ExpiryPhrase(DateTime d) => (d.Date - DateTime.Today).TotalDays switch
    {
        < 0 => "expired",
        0 => "expires today",
        1 => "expires tomorrow",
        var n and < 60 => $"{n:0} days left",
        _ => $"until {d:d MMM yyyy}",
    };

    public string BalanceUpdatedText => BalanceBusy
        ? "Waiting for the operator's reply…"
        : Balance is null ? "Not checked yet" : $"Updated {RelativeTimeConverter.Format(Balance.RetrievedAt)}";

    private void LoadBalance()
    {
        if (_data.LastBalance is not null) Balance = _data.LastBalance;
    }

    // ───────────── Check balance (SMS first, USSD where it works) ─────────────

    private bool CanCheckBalance() => !BalanceBusy && !UssdBusy && (Brand.HasSmsBalance || !string.IsNullOrEmpty(Brand.BalanceCode));

    [RelayCommand(CanExecute = nameof(CanCheckBalance))]
    private Task CheckBalance() => UseSmsForBalance
        ? RequestBalanceBySmsAsync(auto: false)
        : RunUssdAsync(Brand.BalanceCode, newSession: true);

    private async Task RequestBalanceBySmsAsync(bool auto)
    {
        if (BalanceBusy) return;
        if (!Status.IsRegistered)
        {
            if (!auto) ShowToast(ToastKind.Error, "Can't check balance", "The modem isn't registered on a network.");
            return;
        }

        BalanceBusy = true;
        var requestedAt = DateTimeOffset.Now;
        _balanceRequestedAt = requestedAt;
        Log("Balance", $"{(auto ? "Auto" : "Manual")} request: SMS {Brand.BalanceSmsText} → {Brand.BalanceSmsNumber}");
        try
        {
            var r = await _sms.SendAsync(Brand.BalanceSmsNumber, Brand.BalanceSmsText);
            if (!r.Success)
            {
                Log("Balance", $"Request failed: {r.Error}");
                if (!auto) ShowToast(ToastKind.Error, "Balance request not sent", r.Error);
                return;
            }

            // Wait for the reply; AddIncoming → TryCaptureBalanceFromSms clears _balanceRequestedAt.
            // The receive event is unreliable in this process, so poll the modem store as well.
            var until = DateTimeOffset.Now + BalanceReplyTimeout;
            var nextPoll = DateTimeOffset.Now.AddSeconds(4);
            while (DateTimeOffset.Now < until && _balanceRequestedAt == requestedAt)
            {
                await Task.Delay(500);
                if (DateTimeOffset.Now >= nextPoll && _balanceRequestedAt == requestedAt)
                {
                    await SyncStoreAsync(notify: false, announceNew: true, quiet: true);
                    nextPoll = DateTimeOffset.Now.AddSeconds(5);
                }
            }

            if (_balanceRequestedAt == requestedAt)
            {
                Log("Balance", "No reply from the operator within 90 s");
                if (!auto) ShowToast(ToastKind.Warning, "No reply yet",
                    $"{Brand.Name} hasn't answered. The reply will still update the balance when it arrives.");
            }
        }
        catch (Exception ex)
        {
            Log("Balance", $"Error: {ex.Message}");
            if (!auto) ShowToast(ToastKind.Error, "Balance request failed", ex.Message);
        }
        finally
        {
            BalanceBusy = false;
        }
    }

    /// <summary>Refreshes the balance in the background when it's stale (SMS query is free).</summary>
    private void MaybeAutoRefreshBalance()
    {
        if (!Status.IsRegistered || !UseSmsForBalance || BalanceBusy) return;
        if (Balance is not null && DateTimeOffset.Now - Balance.RetrievedAt < AutoBalanceAge) return;
        if (DateTimeOffset.Now - _lastAutoBalance < TimeSpan.FromMinutes(30)) return;
        _lastAutoBalance = DateTimeOffset.Now;
        _ = RequestBalanceBySmsAsync(auto: true);
    }

    // ───────────── Capturing replies ─────────────

    private bool IsOperatorSender(string from)
    {
        var f = from.Trim();
        return f.Equals(Brand.Name, StringComparison.OrdinalIgnoreCase)
               || (Brand.HasSmsBalance && PhoneFormat.Key(f) == PhoneFormat.Key(Brand.BalanceSmsNumber))
               || (Status.SimSpn.Length > 0 && f.Equals(Status.SimSpn, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Updates the tiles from an operator SMS. Returns true if it was a balance statement.</summary>
    /// <param name="allowAsReply">Only genuinely new messages may count as the reply to a pending request.</param>
    private bool TryCaptureBalanceFromSms(string from, string body, DateTimeOffset received, bool notify, bool allowAsReply)
    {
        if (!IsOperatorSender(from) || !BalanceParser.LooksLikeBalance(body)) return false;
        bool answersRequest = allowAsReply && _balanceRequestedAt is not null;
        // ignore stale store copies, but always accept the reply to a request we just made
        if (!answersRequest && Balance is not null && received <= Balance.RetrievedAt) return false;
        if (answersRequest) received = DateTimeOffset.Now;

        var parsed = BalanceParser.Parse(body) with { RetrievedAt = received };
        if (!parsed.HasAnything) return false;
        ApplyBalance(parsed, isFullStatement: parsed.Data.Count > 0 || parsed.CreditExpires is not null);
        _balanceRequestedAt = null;
        Log("Balance", $"Updated from {from}: credit {CreditText}, data {DataText}");
        if (notify) ShowToast(ToastKind.Success, "Balance updated", $"Credit {CreditText} · Data {DataText}");
        return true;
    }

    private void TryCaptureBalance(string code, string reply)
    {
        bool isBalanceCode = code == Brand.BalanceCode || code == _data.DataBalanceCode;
        if (!isBalanceCode) return; // only trust replies to known balance codes
        var parsed = BalanceParser.Parse(reply);
        if (!parsed.HasAnything) return;
        ApplyBalance(parsed, isFullStatement: true);
        ShowToast(ToastKind.Success, "Balance updated", CreditText != "—" ? $"Credit {CreditText}" : null);
    }

    /// <summary>
    /// A full statement replaces everything; a partial one (e.g. "Reloaded … AC Bal 50.08") only updates
    /// the fields it contains and keeps the rest of the last known balance.
    /// </summary>
    private void ApplyBalance(BalanceInfo parsed, bool isFullStatement)
    {
        var merged = parsed;
        if (Balance is not null && !isFullStatement)
        {
            merged = parsed with
            {
                Credit = parsed.Credit ?? Balance.Credit,
                CreditExpires = parsed.CreditExpires ?? Balance.CreditExpires,
                Data = parsed.Data.Count > 0 ? parsed.Data : Balance.Data,
                Points = parsed.Points ?? Balance.Points,
                ValidUntil = parsed.ValidUntil ?? Balance.ValidUntil,
            };
        }
        Balance = merged;
        _data.LastBalance = merged;
        _data.LastBalanceRaw = parsed.RawText;
        _data.LastBalanceAt = merged.RetrievedAt;
        Save();
    }

    /// <summary>Called from the UI clock so the "Updated x min ago" label stays fresh.</summary>
    public void Tick() => OnPropertyChanged(nameof(BalanceUpdatedText));
}
