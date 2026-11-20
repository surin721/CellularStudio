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
    /// <summary>Generic: latest date found when no structured expiry was recognised.</summary>
    public DateTime? ValidUntil { get; init; }
    public string RawText { get; init; } = "";
    public DateTimeOffset RetrievedAt { get; init; } = DateTimeOffset.Now;

    public double TotalDataMb => Data.Sum(d => d.Megabytes);
    public DateTime? Validity => CreditExpires ?? ValidUntil;
    public bool HasAnything => Credit is not null || Data.Count > 0 || Validity is not null;
}

/// <summary>
/// Parses operator balance replies (SMS or USSD), carrier-agnostic.
///
/// Verified format (Hutch, "BAL" to 344, 2026-11-21):
///   "Bal 1 Rs 50.08 Ex 20/05/2027, Cheer Points  1031.90 Ex 31/01/2027, Hotspot Anytime Data 50.00 GB Ex 21/12/2026,
///    Bonus Anytime Data 12582912.00 Byte Ex 21/11/2026, Status : Active"
///   "Reloaded Rs.50.00. AC Bal 50.08 Dial *344# for all bal"
/// Also understands common phrasings used by other operators, e.g.
///   "Your balance is Rs. 123.45, valid till 31/12/2026", "Main Bal: LKR 99.50 Exp 2027-01-31",
///   "Anytime Data 1.5GB valid until 21-Dec-2026".
/// </summary>
public static partial class BalanceParser
{
    private const string Date =
        @"\d{1,2}[/\-.]\d{1,2}[/\-.]\d{2,4}" +
        @"|\d{4}-\d{2}-\d{2}" +
        @"|\d{1,2}(?:st|nd|rd|th)?[\s\-]?(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*[\s\-,]*\d{2,4}";

    private const string Expiry =
        @"(?:Ex|Exp(?:ires?|iry)?(?:\s*on)?|valid\s*(?:till|until|upto|up\s*to|to|thru)|till|until)\s*[:\-]?\s*";

    // credit: "Bal 1 Rs 50.08 Ex …", "AC Bal 50.08", "Balance is Rs. 123.45", "Main Bal: LKR 99.50", "Credit: Rs 10"
    [GeneratedRegex(@"\b(?:Bal(?:ance)?|Credit)(?:\s*\d(?=\s))?(?:\s+(?:is|of))?\s*[:=]?\s*(?:Rs\.?|LKR|රු\.?)?\s*(?<amt>-?[\d,]+(?:\.\d{1,2})?)(?![\d.]*\s*(?:GB|MB|KB|Bytes?)\b)(?:[,.;]?\s*" + Expiry + @"(?<ex>" + Date + @"))?",
        RegexOptions.IgnoreCase)]
    private static partial Regex CreditRx();

    // data bucket: "<Name> 6950912.00 Byte Ex 21/11/2026", "Anytime Data 1.5GB valid until 21-Dec-2026"
    [GeneratedRegex(@"(?<label>[A-Za-z][A-Za-z0-9 &+\-]{1,40}?)\s*[:\-]?\s*(?<num>\d+(?:\.\d+)?)\s*(?<unit>Bytes?|KB|MB|GB)\b(?:[,.;]?\s*(?:" + Expiry + @")(?<ex>" + Date + @"))?",
        RegexOptions.IgnoreCase)]
    private static partial Regex BucketRx();

    [GeneratedRegex(@"(?:Cheer\s*|Loyalty\s*|Star\s*)?Points\s*:?\s*(?<n>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PointsRx();

    [GeneratedRegex(@"(?:Rs\.?|LKR|රු\.?)\s*(-?[\d,]+(?:\.\d{1,2})?)", RegexOptions.IgnoreCase)]
    private static partial Regex GenericCreditRx();

    [GeneratedRegex(Date, RegexOptions.IgnoreCase)]
    private static partial Regex DateRx();

    // "Reloaded Rs.50.00", "Recharge of Rs.100 successful", "You have received Rs.20" — never the balance itself.
    [GeneratedRegex(@"(?:Reload(?:ed)?|Recharge[ds]?(?:\s*of)?|Top[\s\-]?up(?:\s*of)?|received)\s*(?:Rs\.?|LKR)\s*[\d,.]+", RegexOptions.IgnoreCase)]
    private static partial Regex ReloadRx();

    public static BalanceInfo Parse(string text)
    {
        var t = Regex.Replace(text, @"\s+", " ");
        var noReload = ReloadRx().Replace(t, " ");

        decimal? credit = null; DateTime? creditEx = null;
        var cm = CreditRx().Match(noReload);
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
        var bucketSpans = new List<(int Start, int End)>();
        foreach (Match m in BucketRx().Matches(t))
        {
            bucketSpans.Add((m.Index, m.Index + m.Length));
            if (!double.TryParse(m.Groups["num"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) continue;
            double mb = m.Groups["unit"].Value.ToUpperInvariant() switch
            {
                "GB" => n * 1024,
                "KB" => n / 1024,
                "BYTE" or "BYTES" => n / (1024 * 1024),
                _ => n,
            };
            DateTime? ex = m.Groups["ex"].Success ? ParseDate(m.Groups["ex"].Value) : null;
            data.Add(new DataBucket(CleanLabel(m.Groups["label"].Value), mb, ex));
        }

        int? points = null;
        var pm = PointsRx().Match(t);
        if (pm.Success && int.TryParse(pm.Groups["n"].Value, out var pts)) points = pts;

        DateTime? latest = null;
        if (creditEx is null)
        {
            // a date right after a "valid till"-style keyword anywhere in the text is the account validity
            var vm = Regex.Match(t, @"(?:account|main|validity|valid)[^.]{0,30}?" + Expiry + "(?<d>" + Date + ")", RegexOptions.IgnoreCase);
            if (vm.Success) creditEx = ParseDate(vm.Groups["d"].Value);
            else
                foreach (Match m in DateRx().Matches(t))
                {
                    if (bucketSpans.Any(s => m.Index >= s.Start && m.Index < s.End)) continue;   // a data pack's own expiry
                    if (ParseDate(m.Value) is DateTime d && (latest is null || d > latest)) latest = d;
                }
        }

        return new BalanceInfo
        {
            Credit = credit, CreditExpires = creditEx, Data = data, Points = points,
            ValidUntil = latest, RawText = text,
        };
    }

    /// <summary>True when the text looks like a balance statement (not just any message mentioning Rs.).</summary>
    public static bool LooksLikeBalance(string text)
    {
        var t = Regex.Replace(text, @"\s+", " ");
        return CreditRx().IsMatch(ReloadRx().Replace(t, " ")) ||
               BucketRx().Matches(t).Any(m => m.Groups["ex"].Success);   // "… 25MB …" in a promo has no expiry
    }

    private static string CleanLabel(string raw)
    {
        // drop separators / fragments of the previous item ("…/2026, Bonus Anytime Data")
        var l = Regex.Replace(raw, @"^[\s,;:\-]+|[\s,;:\-]+$", "");
        l = Regex.Replace(l, @"^(?:Ex\s+)?\d+[/\d]*\s*,?\s*", "");
        l = Regex.Replace(l, @"^(?:and|&|your|you have|remaining|balance of)\s+", "", RegexOptions.IgnoreCase);
        if (string.IsNullOrWhiteSpace(l)) l = "Data";
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(l.ToLowerInvariant());
    }

    private static bool TryAmount(string s, out decimal v) =>
        decimal.TryParse(s.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out v);

    private static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    public static DateTime? ParseDate(string s)
    {
        s = s.Trim();
        try
        {
            var iso = Regex.Match(s, @"^(\d{4})-(\d{2})-(\d{2})$");
            if (iso.Success) return new DateTime(int.Parse(iso.Groups[1].Value), int.Parse(iso.Groups[2].Value), int.Parse(iso.Groups[3].Value));

            var named = Regex.Match(s, @"^(\d{1,2})(?:st|nd|rd|th)?[\s\-]?([A-Za-z]{3})[a-z]*[\s\-,]*(\d{2,4})$");
            if (named.Success)
            {
                int mon = Array.IndexOf(Months, named.Groups[2].Value.ToLowerInvariant()) + 1;
                int yr = int.Parse(named.Groups[3].Value); if (yr < 100) yr += 2000;
                return mon > 0 ? new DateTime(yr, mon, int.Parse(named.Groups[1].Value)) : null;
            }

            var num = Regex.Match(s, @"^(\d{1,2})[/\-.](\d{1,2})[/\-.](\d{2,4})$");
            if (num.Success)
            {
                int day = int.Parse(num.Groups[1].Value), mon = int.Parse(num.Groups[2].Value), yr = int.Parse(num.Groups[3].Value);
                if (yr < 100) yr += 2000;
                if (mon > 12 && day <= 12) (day, mon) = (mon, day);   // Sri Lankan operators use dd/mm; tolerate mm/dd
                return new DateTime(yr, mon, day);
            }
        }
        catch { /* invalid date */ }
        return null;
    }

    public static string FormatData(double mb) =>
        mb >= 1024 ? $"{mb / 1024:0.##} GB" : mb >= 1 ? $"{mb:0.#} MB" : $"{mb * 1024:0} KB";
}
