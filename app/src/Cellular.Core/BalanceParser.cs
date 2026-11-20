using System.Globalization;
using System.Text.RegularExpressions;

namespace Cellular.Core;

public sealed record DataBucket(string Label, double Megabytes, DateTime? Expires = null);

public sealed record BalanceInfo
{
    public decimal? Credit { get; init; }
    public string Currency { get; init; } = "Rs.";
    /// <summary>Expiry of the main (credit) balance.</summary>
    public DateTime? CreditExpires { get; init; }
    public IReadOnlyList<DataBucket> Data { get; init; } = [];
    public int? Points { get; init; }
    /// <summary>Legacy/generic: latest date found when no structured expiry was recognised.</summary>
    public DateTime? ValidUntil { get; init; }
    public string RawText { get; init; } = "";
    public DateTimeOffset RetrievedAt { get; init; } = DateTimeOffset.Now;

    public double TotalDataMb => Data.Sum(d => d.Megabytes);
    public DateTime? Validity => CreditExpires ?? ValidUntil;
    public bool HasAnything => Credit is not null || Data.Count > 0 || Validity is not null;
}

/// <summary>
/// Parses operator balance replies.
/// Hutch (SMS "BAL" to 344), observed 2026-11-21, a 2-part SMS:
///   "Bal 1 Rs 50.08 Ex 20/05/2027, Cheer Points  1031 Ex dd/11/2026, Bonus Anytime Data 6950912.00 Byte Ex 21/11/2026, Status : Active"
/// and reload notices: "Reloaded Rs.50.00. AC Bal 50.08 Dial *344# for all bal".
/// Falls back to generic "Rs. x", "n MB/GB", dd/mm/yyyy heuristics for other formats.
/// </summary>
public static partial class BalanceParser
{
    // "Bal 1 Rs 50.08 Ex 20/05/2027"  |  "AC Bal 50.08"  |  "Main Bal Rs.12.50"
    [GeneratedRegex(@"\bBal(?:ance)?(?:\s*\d(?=\s))?\s*(?:Rs\.?|LKR)?\s*:?\s*(?<amt>-?[\d,]+(?:\.\d{1,2})?)(?:\s*Ex\s*(?<ex>\d{1,2}/\d{1,2}/\d{2,4}))?", RegexOptions.IgnoreCase)]
    private static partial Regex HutchCreditRx();

    // "<Name> 6950912.00 Byte Ex 21/11/2026"
    [GeneratedRegex(@"(?<label>[A-Za-z][A-Za-z0-9 &+\-]{1,40}?)\s+(?<num>\d+(?:\.\d+)?)\s*(?<unit>Bytes?|KB|MB|GB)\b(?:\s*Ex\s*(?<ex>\d{1,2}/\d{1,2}/\d{2,4}))?", RegexOptions.IgnoreCase)]
    private static partial Regex BucketRx();

    [GeneratedRegex(@"(?:Cheer\s*)?Points\s*:?\s*(?<n>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PointsRx();

    [GeneratedRegex(@"(?:Rs\.?|LKR|රු\.?)\s*(-?[\d,]+(?:\.\d{1,2})?)", RegexOptions.IgnoreCase)]
    private static partial Regex GenericCreditRx();

    [GeneratedRegex(@"\b(\d{1,2})[/\-.](\d{1,2})[/\-.](\d{2,4})\b")]
    private static partial Regex DateRx();

    // Reload/top-up notices mention the amount reloaded first ("Reloaded Rs.50.00"); never treat that as the balance.
    [GeneratedRegex(@"Reload(?:ed)?\s*Rs\.?\s*[\d,.]+", RegexOptions.IgnoreCase)]
    private static partial Regex ReloadRx();

    public static BalanceInfo Parse(string text)
    {
        var t = Regex.Replace(text, @"\s+", " ");
        var noReload = ReloadRx().Replace(t, " ");

        decimal? credit = null; DateTime? creditEx = null;
        var cm = HutchCreditRx().Match(noReload);
        if (cm.Success && TryAmount(cm.Groups["amt"].Value, out var c))
        {
            credit = c;
            if (cm.Groups["ex"].Success) creditEx = ParseDate(cm.Groups["ex"].Value);
        }
        else
        {
            var g = GenericCreditRx().Match(noReload);
            if (g.Success && TryAmount(g.Groups[1].Value, out var c2)) credit = c2;
        }

        var data = new List<DataBucket>();
        foreach (Match m in BucketRx().Matches(t))
        {
            if (!double.TryParse(m.Groups["num"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) continue;
            double mb = m.Groups["unit"].Value.ToUpperInvariant() switch
            {
                "GB" => n * 1024,
                "KB" => n / 1024,
                "BYTE" or "BYTES" => n / (1024 * 1024),
                _ => n,
            };
            var label = CleanLabel(m.Groups["label"].Value);
            DateTime? ex = m.Groups["ex"].Success ? ParseDate(m.Groups["ex"].Value) : null;
            data.Add(new DataBucket(label, mb, ex));
        }

        int? points = null;
        var pm = PointsRx().Match(t);
        if (pm.Success && int.TryParse(pm.Groups["n"].Value, out var pts)) points = pts;

        DateTime? latest = null;
        if (creditEx is null)
            foreach (Match m in DateRx().Matches(t))
                if (ParseDate(m.Value) is DateTime d && (latest is null || d > latest)) latest = d;

        return new BalanceInfo
        {
            Credit = credit, CreditExpires = creditEx, Data = data, Points = points,
            ValidUntil = latest, RawText = text,
        };
    }

    /// <summary>True when the text looks like a balance statement (not just any message mentioning Rs.).</summary>
    public static bool LooksLikeBalance(string text) =>
        HutchCreditRx().IsMatch(ReloadRx().Replace(text, " ")) ||
        BucketRx().Matches(text).Any(m => m.Groups["ex"].Success);   // "… 25MB …" in a promo has no expiry

    private static string CleanLabel(string raw)
    {
        // drop leading separators / words that belong to a previous item ("…/2026, Bonus Anytime Data")
        var l = Regex.Replace(raw, @"^[\s,;:\-]+|[\s,;:\-]+$", "");
        l = Regex.Replace(l, @"^(?:Ex\s+)?\d+[/\d]*\s*,?\s*", "");
        if (string.IsNullOrWhiteSpace(l)) l = "Data";
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(l.ToLowerInvariant());
    }

    private static bool TryAmount(string s, out decimal v) =>
        decimal.TryParse(s.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out v);

    private static DateTime? ParseDate(string s)
    {
        var m = DateRx().Match(s);
        if (!m.Success) return null;
        int day = int.Parse(m.Groups[1].Value), mon = int.Parse(m.Groups[2].Value), yr = int.Parse(m.Groups[3].Value);
        if (yr < 100) yr += 2000;
        if (mon > 12 && day <= 12) (day, mon) = (mon, day);
        try { return new DateTime(yr, mon, day); } catch { return null; }
    }

    public static string FormatData(double mb) =>
        mb >= 1024 ? $"{mb / 1024:0.##} GB" : mb >= 1 ? $"{mb:0.#} MB" : $"{mb * 1024:0} KB";
}
