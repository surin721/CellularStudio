using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CellularStudio.Controls;

/// <summary>
/// Small carrier logo. Shows a logo image when one is available, otherwise a rounded
/// monogram tile in the carrier's brand color.
/// </summary>
public sealed class CarrierBadge : FrameworkElement
{
    public static readonly DependencyProperty LogoPathProperty = DependencyProperty.Register(
        nameof(LogoPath), typeof(string), typeof(CarrierBadge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((CarrierBadge)d)._image = null));

    public static readonly DependencyProperty MonogramProperty = DependencyProperty.Register(
        nameof(Monogram), typeof(string), typeof(CarrierBadge),
        new FrameworkPropertyMetadata("?", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BrandColorProperty = DependencyProperty.Register(
        nameof(BrandColor), typeof(string), typeof(CarrierBadge),
        new FrameworkPropertyMetadata("#5B6472", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsDimmedProperty = DependencyProperty.Register(
        nameof(IsDimmed), typeof(bool), typeof(CarrierBadge),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public string? LogoPath { get => (string?)GetValue(LogoPathProperty); set => SetValue(LogoPathProperty, value); }
    public string Monogram { get => (string)GetValue(MonogramProperty); set => SetValue(MonogramProperty, value); }
    public string BrandColor { get => (string)GetValue(BrandColorProperty); set => SetValue(BrandColorProperty, value); }
    public bool IsDimmed { get => (bool)GetValue(IsDimmedProperty); set => SetValue(IsDimmedProperty, value); }

    private BitmapImage? _image;

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsNaN(Width) ? 32 : Width, double.IsNaN(Height) ? 32 : Height);

    protected override void OnRender(DrawingContext dc)
    {
        double s = Math.Min(ActualWidth, ActualHeight);
        var rect = new Rect(0, 0, s, s);
        double radius = s * 0.28;
        if (IsDimmed) dc.PushOpacity(0.45);

        var img = LoadImage();
        if (img is not null)
        {
            dc.DrawRoundedRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(0xE3, 0xE6, 0xEC)), 1), rect, radius, radius);
            dc.PushClip(new RectangleGeometry(rect, radius, radius));
            double pad = s * 0.1;
            dc.DrawImage(img, new Rect(pad, pad, s - pad * 2, s - pad * 2));
            dc.Pop();
        }
        else
        {
            Color c;
            try { c = (Color)ColorConverter.ConvertFromString(BrandColor); }
            catch { c = Color.FromRgb(0x5B, 0x64, 0x72); }
            var light = Color.FromArgb(255, (byte)Math.Min(255, c.R + 28), (byte)Math.Min(255, c.G + 28), (byte)Math.Min(255, c.B + 28));
            var brush = new LinearGradientBrush(light, c, 45);
            dc.DrawRoundedRectangle(brush, null, rect, radius, radius);

            var tf = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var ft = new FormattedText(Monogram ?? "?", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf,
                s * 0.5, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point((s - ft.Width) / 2, (s - ft.Height) / 2));
        }

        if (IsDimmed) dc.Pop();
    }

    private BitmapImage? LoadImage()
    {
        if (_image is not null || string.IsNullOrEmpty(LogoPath)) return _image;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.UriSource = new Uri(LogoPath, UriKind.Absolute);
            bi.EndInit();
            bi.Freeze();
            _image = bi;
        }
        catch { _image = null; }
        return _image;
    }
}
