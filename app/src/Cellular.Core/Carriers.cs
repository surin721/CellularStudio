namespace Cellular.Core;

/// <summary>
/// Everything the app knows about one mobile operator (brand), merged from the built-in profile and the
/// user's overrides.
/// </summary>
public sealed record CarrierBrand
{
    /// <summary>Stable key used for overrides and logo files ("hutch", "dialog", "mobitel", or the MCC+MNC).</summary>
    public required string Key { get; init; }
    /// <summary>MCC+MNC of the network this brand was resolved from.</summary>
    public string Id { get; init; } = "";
    public required string Name { get; init; }
    public string Monogram { get; init; } = "?";
    /// <summary>Brand colour (hex) for the monogram badge. Approximate; only used for a small status badge.</summary>
    public string BrandColor { get; init; } = "#5B6472";

    /// <summary>USSD codes (USSD fails on LTE with the DW5820e, so these are informational quick codes).</summary>
    public string BalanceCode { get; init; } = "";
    public string MenuCode { get; init; } = "";

    /// <summary>Balance query by SMS: send <see cref="BalanceSmsText"/> to <see cref="BalanceSmsNumber"/>.</summary>
    public string BalanceSmsNumber { get; init; } = "";
    public string BalanceSmsText { get; init; } = "";
    /// <summary>True when the SMS method comes from a verified source (vs. entered by the user).</summary>
    public bool BalanceSmsVerified { get; init; }

    /// <summary>Sender IDs whose messages may contain balance statements (case-insensitive).</summary>
    public IReadOnlyList<string> SenderIds { get; init; } = [];

    public string Helpline { get; init; } = "";
    public string Website { get; init; } = "";
    /// <summary>Short note about how balance can be checked for this carrier.</summary>
    public string BalanceHint { get; init; } = "";

    public bool HasSmsBalance => BalanceSmsNumber.Length > 0 && BalanceSmsText.Length > 0;
    public bool IsKnown { get; init; }
}

/// <summary>User overrides for one carrier, persisted in data.json (null = use the built-in value).</summary>
public sealed class CarrierOverride
{
    public string? BalanceSmsNumber { get; set; }
    public string? BalanceSmsText { get; set; }
    public List<string>? SenderIds { get; set; }
}

public static class Carriers
{
    private sealed record Profile(string Key, string[] MccMnc, CarrierBrand Brand, string[] NameAliases);

    // Sri Lanka (MCC 413). Network codes per the ITU region 4xx list (2026): 01/12 SLTMobitel, 02/11 Dialog,
    // 05 ex-Airtel (acquired by Dialog), 08 Hutch, 03 ex-Etisalat and 09 (Hutch, withdrawn).
    // Only Hutch publishes an SMS balance query (hutch.lk/account-balance: "SMS BAL to 344"); Dialog and SLTMobitel
    // don't document one on their websites (checked 2026-11-21) — users can enter it in the app.
    private static readonly Profile[] Profiles =
    [
        new("hutch", ["41308", "41303", "41309"], new CarrierBrand
        {
            Key = "hutch", Name = "Hutch", Monogram = "H", BrandColor = "#F7931E",
            BalanceCode = "*344#", MenuCode = "*123#",
            BalanceSmsNumber = "344", BalanceSmsText = "BAL", BalanceSmsVerified = true,
            SenderIds = ["Hutch"], Helpline = "1788", Website = "https://hutch.lk/account-balance/",
            BalanceHint = "SMS BAL to 344 (free). Source: hutch.lk/account-balance.",
            IsKnown = true,
        }, ["hutch", "etisalat"]),

        new("dialog", ["41302", "41305", "41311"], new CarrierBrand
        {
            Key = "dialog", Name = "Dialog", Monogram = "D", BrandColor = "#C8102E",
            MenuCode = "#678#",
            SenderIds = ["Dialog", "Dialog Axiata"],
            Helpline = "1777", Website = "https://www.dialog.lk/support/self-help-channel/dial-codes",
            BalanceHint = "Dialog doesn't publish an SMS balance query. Its documented options are #678# (menu) and the 1777 helpline. "
                          + "If you know the keyword and short number, enter them below.",
            IsKnown = true,
        }, ["dialog", "airtel"]),

        new("mobitel", ["41301", "41312"], new CarrierBrand
        {
            Key = "mobitel", Name = "SLTMobitel", Monogram = "M", BrandColor = "#0067B1",
            SenderIds = ["Mobitel", "SLTMobitel", "SLT-Mobitel"],   // sender matching is case-insensitive
            Website = "https://www.sltmobitel.lk/",
            BalanceHint = "SLTMobitel's balance-by-SMS details couldn't be confirmed from its website. "
                          + "If you know the keyword and short number, enter them below.",
            IsKnown = true,
        }, ["mobitel", "slt"]),
    ];

    /// <summary>Resolves the brand for a network (MCC+MNC), falling back to name matching, then applies overrides.</summary>
    public static CarrierBrand Resolve(string mccMnc, string fallbackName, IReadOnlyDictionary<string, CarrierOverride>? overrides = null)
    {
        var name = (fallbackName ?? "").Trim();
        // name matching only for Sri Lankan (or unknown) networks — e.g. "Airtel" in India is not Dialog
        bool nameMatchAllowed = mccMnc.Length < 3 || mccMnc.StartsWith("413", StringComparison.Ordinal);
        var p = Profiles.FirstOrDefault(x => x.MccMnc.Contains(mccMnc))
                ?? (nameMatchAllowed
                    ? Profiles.FirstOrDefault(x => name.Length > 0 && x.NameAliases.Any(a => name.Contains(a, StringComparison.OrdinalIgnoreCase)))
                    : null);

        CarrierBrand brand;
        if (p is not null)
            brand = p.Brand with { Id = mccMnc };
        else
        {
            var display = name.Length == 0 ? "Unknown" : name;
            var mono = new string(display.Where(char.IsLetter).Take(1).ToArray()).ToUpperInvariant();
            brand = new CarrierBrand
            {
                Key = mccMnc.Length > 0 ? mccMnc : display.ToLowerInvariant(),
                Id = mccMnc, Name = display, Monogram = mono.Length > 0 ? mono : "?",
                SenderIds = name.Length > 0 ? [name] : [],
                BalanceHint = "No built-in balance method for this carrier. Enter the operator's balance SMS keyword and number below.",
            };
        }
        return ApplyOverride(brand, overrides);
    }

    public static CarrierBrand ApplyOverride(CarrierBrand brand, IReadOnlyDictionary<string, CarrierOverride>? overrides)
    {
        if (overrides is null || !overrides.TryGetValue(brand.Key, out var o)) return brand;
        var number = o.BalanceSmsNumber ?? brand.BalanceSmsNumber;
        var text = o.BalanceSmsText ?? brand.BalanceSmsText;
        bool changed = number != brand.BalanceSmsNumber || text != brand.BalanceSmsText;
        return brand with
        {
            BalanceSmsNumber = number.Trim(),
            BalanceSmsText = text.Trim(),
            BalanceSmsVerified = brand.BalanceSmsVerified && !changed,
            SenderIds = o.SenderIds is { Count: > 0 } ids ? ids : brand.SenderIds,
        };
    }

    /// <summary>Built-in profile (no overrides) for a key, used for "reset to default".</summary>
    public static CarrierBrand? Default(string key) => Profiles.FirstOrDefault(p => p.Key == key)?.Brand;

    /// <summary>Folder for user-chosen logos (%LOCALAPPDATA%\CellularStudio\logos).</summary>
    public static string LogoFolder => Path.Combine(AppStorage.Root, "logos");

    /// <summary>
    /// Optional logo image: a PNG/JPG named after the carrier key (hutch.png, dialog.png, mobitel.png) or the
    /// MCC+MNC (41302.png), in %LOCALAPPDATA%\CellularStudio\logos or the app's Assets\Carriers folder.
    /// </summary>
    public static string? FindLogo(CarrierBrand brand)
    {
        var dirs = new[] { LogoFolder, Path.Combine(AppContext.BaseDirectory, "Assets", "Carriers") };
        var names = new[] { brand.Key, brand.Id, brand.Name.ToLowerInvariant() }.Where(n => n.Length > 0).Distinct();
        foreach (var d in dirs)
            foreach (var n in names)
                foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".ico" })
                {
                    var p = Path.Combine(d, n + ext);
                    if (File.Exists(p)) return p;
                }
        return null;
    }

    /// <summary>Copies an image into the logo folder as &lt;key&gt;.&lt;ext&gt;, replacing any previous logo for that key.</summary>
    public static string SetLogo(CarrierBrand brand, string sourceFile)
    {
        Directory.CreateDirectory(LogoFolder);
        RemoveLogo(brand);
        var dest = Path.Combine(LogoFolder, brand.Key + Path.GetExtension(sourceFile).ToLowerInvariant());
        File.Copy(sourceFile, dest, overwrite: true);
        return dest;
    }

    public static void RemoveLogo(CarrierBrand brand)
    {
        if (!Directory.Exists(LogoFolder)) return;
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".ico" })
        {
            var p = Path.Combine(LogoFolder, brand.Key + ext);
            if (File.Exists(p)) File.Delete(p);
        }
    }
}
