using Cellular.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CellularStudio.ViewModels;

/// <summary>
/// "Carrier &amp; balance" settings: per-carrier balance SMS method, accepted reply senders and logo.
/// Built-in profiles cover Hutch, Dialog and SLTMobitel; only Hutch has a verified SMS method, so users of other
/// carriers enter theirs here. Values are saved as overrides in data.json, keyed by the carrier key.
/// </summary>
public sealed partial class MainViewModel
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCarrierSettingsCommand))]
    private string _carrierSmsNumber = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCarrierSettingsCommand))]
    private string _carrierSmsText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCarrierSettingsCommand))]
    private string _carrierSenderIds = "";

    private string? _loadedCarrierKey;

    public string CarrierHint => Brand.BalanceHint;
    public bool CarrierHasHelpline => Brand.Helpline.Length > 0;
    public string CarrierHelpline => Brand.Helpline.Length > 0 ? $"Helpline {Brand.Helpline}" : "";
    public string CarrierMenuCodes => string.Join(" · ", new[] { Brand.BalanceCode, Brand.MenuCode }.Where(c => c.Length > 0));
    public bool HasLogo => !string.IsNullOrEmpty(LogoPath);

    public string CarrierMethodStatus => Brand.HasSmsBalance
        ? Brand.BalanceSmsVerified
            ? $"Verified: SMS “{Brand.BalanceSmsText}” to {Brand.BalanceSmsNumber}"
            : $"Custom: SMS “{Brand.BalanceSmsText}” to {Brand.BalanceSmsNumber}"
        : "Not set up";

    /// <summary>"ok" (verified), "custom" (user-entered) or "none" — drives the status pill colour.</summary>
    public string CarrierMethodKind => !Brand.HasSmsBalance ? "none" : Brand.BalanceSmsVerified ? "ok" : "custom";

    public bool CarrierHasOverride => _data.CarrierOverrides.ContainsKey(Brand.Key);

    partial void OnBrandChanged(CarrierBrand value)
    {
        OnPropertyChanged(nameof(CarrierHint));
        OnPropertyChanged(nameof(CarrierHasHelpline));
        OnPropertyChanged(nameof(CarrierHelpline));
        OnPropertyChanged(nameof(CarrierMenuCodes));
        OnPropertyChanged(nameof(CarrierMethodStatus));
        OnPropertyChanged(nameof(CarrierMethodKind));
        OnPropertyChanged(nameof(CarrierHasOverride));
        OnPropertyChanged(nameof(BalanceMethodText));
        CheckBalanceCommand.NotifyCanExecuteChanged();
        // only reload the editable fields when the carrier itself changes, so typing isn't overwritten by polling
        if (_loadedCarrierKey != value.Key) LoadCarrierFields();
    }

    partial void OnLogoPathChanged(string? value) => OnPropertyChanged(nameof(HasLogo));

    private void LoadCarrierFields()
    {
        _loadedCarrierKey = Brand.Key;
        CarrierSmsNumber = Brand.BalanceSmsNumber;
        CarrierSmsText = Brand.BalanceSmsText;
        CarrierSenderIds = string.Join(", ", Brand.SenderIds);
    }

    private bool CanSaveCarrierSettings()
    {
        var num = CarrierSmsNumber.Trim();
        var text = CarrierSmsText.Trim();
        bool bothOrNeither = (num.Length == 0) == (text.Length == 0);
        bool numberOk = num.Length == 0 || (num.Length <= 15 && num.All(c => char.IsDigit(c) || c == '+'));
        bool changed = num != Brand.BalanceSmsNumber || text != Brand.BalanceSmsText
                       || NormalizeSenders(CarrierSenderIds) != NormalizeSenders(string.Join(", ", Brand.SenderIds));
        return bothOrNeither && numberOk && text.Length <= 40 && changed;
    }

    private static string NormalizeSenders(string s) =>
        string.Join(", ", s.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase));

    [RelayCommand(CanExecute = nameof(CanSaveCarrierSettings))]
    private void SaveCarrierSettings()
    {
        var senders = CarrierSenderIds.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var def = Carriers.Default(Brand.Key);
        _data.CarrierOverrides[Brand.Key] = new CarrierOverride
        {
            BalanceSmsNumber = CarrierSmsNumber.Trim(),
            BalanceSmsText = CarrierSmsText.Trim(),
            SenderIds = senders.Count > 0 && (def is null || !senders.SequenceEqual(def.SenderIds, StringComparer.OrdinalIgnoreCase)) ? senders : null,
        };
        Save();
        Log("Carrier", $"{Brand.Name}: balance SMS set to “{CarrierSmsText.Trim()}” → {CarrierSmsNumber.Trim()}");
        ReapplyBrand();
        ShowToast(ToastKind.Success, "Balance check saved",
            Brand.HasSmsBalance ? $"Check balance will send “{Brand.BalanceSmsText}” to {Brand.BalanceSmsNumber}." : "SMS balance check turned off.");
    }

    [RelayCommand]
    private void ResetCarrierSettings()
    {
        if (_data.CarrierOverrides.Remove(Brand.Key)) Save();
        ReapplyBrand();
        LoadCarrierFields();
        ShowToast(ToastKind.Info, "Restored defaults", $"Using the built-in settings for {Brand.Name}.");
    }

    [RelayCommand]
    private void ChooseLogo()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Choose a logo for {Brand.Name}",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.ico)|*.png;*.jpg;*.jpeg;*.ico",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            Carriers.SetLogo(Brand, dlg.FileName);
            LogoPath = null;                       // force the badge to reload the image
            LogoPath = Carriers.FindLogo(Brand);
            ShowToast(ToastKind.Success, "Logo updated", $"Shown for {Brand.Name}.");
        }
        catch (Exception ex) { ShowToast(ToastKind.Error, "Couldn't use that image", ex.Message); }
    }

    [RelayCommand]
    private void RemoveLogo()
    {
        try { Carriers.RemoveLogo(Brand); } catch { }
        LogoPath = Carriers.FindLogo(Brand);
        ShowToast(ToastKind.Info, "Logo removed", "Showing the monogram badge.");
    }

    [RelayCommand]
    private void OpenCarrierWebsite()
    {
        if (Brand.Website.Length == 0) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Brand.Website) { UseShellExecute = true }); }
        catch { }
    }

    /// <summary>Re-resolves the current carrier with the latest overrides.</summary>
    private void ReapplyBrand()
    {
        Brand = Carriers.Resolve(Status.BrandingId, Status.CarrierDisplayName, _data.CarrierOverrides);
        LogoPath = Carriers.FindLogo(Brand);
        OnPropertyChanged(nameof(CarrierMethodStatus));
        OnPropertyChanged(nameof(CarrierMethodKind));
        OnPropertyChanged(nameof(CarrierHasOverride));
        OnPropertyChanged(nameof(BalanceMethodText));
        UpdateQuickCodes();
        SaveCarrierSettingsCommand.NotifyCanExecuteChanged();
    }
}
