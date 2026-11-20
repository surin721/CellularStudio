namespace Cellular.Core;

/// <summary>Carrier branding used for the small badge in the UI.</summary>
/// <param name="Id">MCC+MNC.</param>
/// <param name="Name">Display name.</param>
/// <param name="Monogram">1–2 letters drawn when no logo image is available.</param>
/// <param name="BrandColor">Approximate brand color (hex) for the monogram badge.</param>
public sealed record CarrierBrand(string Id, string Name, string Monogram, string BrandColor, string BalanceCode = "", string MenuCode = "",
    string BalanceSmsNumber = "", string BalanceSmsText = "")
{
    public bool HasSmsBalance => BalanceSmsNumber.Length > 0;
}

public static class Carriers
{
    // Sri Lanka (MCC 413). Colors are approximations used only for a small status badge.
    private static readonly CarrierBrand[] Known =
    [
        // Only Hutch's codes are confirmed (by the SIM owner); others fall back to manual entry.
        new("41301", "Mobitel", "M", "#00A651"),
        new("41302", "Dialog", "D", "#D7182A"),
        new("41303", "Etisalat", "E", "#719E19"),
        new("41305", "Airtel", "A", "#E40000"),
        // Hutch: SMS "BAL" to 344 (https://hutch.lk/account-balance/), free for subscribers.
        new("41308", "Hutch", "H", "#F7931E", "*344#", "*123#", "344", "BAL"),
        new("41309", "Hutch", "H", "#F7931E", "*344#", "*123#", "344", "BAL"),
    ];

    public static CarrierBrand Resolve(string mccMnc, string fallbackName)
    {
        var hit = Known.FirstOrDefault(k => k.Id == mccMnc);
        if (hit is not null) return hit;
        var name = string.IsNullOrWhiteSpace(fallbackName) ? "Unknown" : fallbackName.Trim();
        var mono = new string(name.Where(char.IsLetter).Take(1).ToArray()).ToUpperInvariant();
        return new CarrierBrand(mccMnc, name, mono.Length > 0 ? mono : "?", "#5B6472");
    }

    /// <summary>
    /// Optional logo image. Drop a PNG named after the MCC+MNC (e.g. 41308.png) or the carrier name
    /// (hutch.png) into %LOCALAPPDATA%\CellularStudio\logos or the app's Assets\Carriers folder.
    /// </summary>
    public static string? FindLogo(CarrierBrand brand)
    {
        var dirs = new[]
        {
            Path.Combine(AppStorage.Root, "logos"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "Carriers"),
        };
        var names = new[] { brand.Id, brand.Name.ToLowerInvariant() };
        foreach (var d in dirs)
            foreach (var n in names)
                foreach (var ext in new[] { ".png", ".jpg", ".ico" })
                {
                    var p = Path.Combine(d, n + ext);
                    if (File.Exists(p)) return p;
                }
        return null;
    }
}
