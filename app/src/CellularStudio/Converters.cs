using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace CellularStudio;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool b = value switch
        {
            bool x => x,
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i != 0,
            ICollection col => col.Count > 0,
            _ => true,
        };
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : true;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : false;
}

public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value as string ?? "#5B6472")); }
        catch { return Brushes.Gray; }
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Human friendly relative time ("just now", "5 min ago", "Yesterday 14:02").</summary>
public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        DateTimeOffset? ts = value switch
        {
            DateTimeOffset d => d,
            DateTime d => new DateTimeOffset(d),
            _ => null,
        };
        if (ts is null) return "";
        return Format(ts.Value, p as string == "short");
    }

    public static string Format(DateTimeOffset ts, bool shortForm = false)
    {
        var local = ts.ToLocalTime();
        var diff = DateTimeOffset.Now - local;
        if (diff.TotalSeconds < 45) return "just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} min ago";
        if (local.Date == DateTime.Today) return local.ToString("HH:mm");
        if (local.Date == DateTime.Today.AddDays(-1)) return shortForm ? "Yesterday" : $"Yesterday {local:HH:mm}";
        if (diff.TotalDays < 7) return shortForm ? local.ToString("ddd") : local.ToString("ddd HH:mm");
        return shortForm ? local.ToString("d MMM") : local.ToString("d MMM yyyy, HH:mm");
    }

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Fraction (0–1) → star GridLength; parameter "rest" gives the remainder (1 − fraction).</summary>
public sealed class StarLengthConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        double f = value is double d ? Math.Clamp(d, 0, 1) : 0;
        if (p as string == "rest") f = 1 - f;
        return new GridLength(Math.Max(f, 0.0001), GridUnitType.Star);
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class EqualityToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        Equals(value?.ToString(), p?.ToString()) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
