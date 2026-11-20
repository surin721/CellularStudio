using System.Windows;
using System.Windows.Media;

namespace CellularStudio.Controls;

/// <summary>Classic 5-step cellular signal indicator.</summary>
public sealed class SignalBars : FrameworkElement
{
    public static readonly DependencyProperty BarsProperty = DependencyProperty.Register(
        nameof(Bars), typeof(int), typeof(SignalBars),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ActiveBrushProperty = DependencyProperty.Register(
        nameof(ActiveBrush), typeof(Brush), typeof(SignalBars),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty InactiveBrushProperty = DependencyProperty.Register(
        nameof(InactiveBrush), typeof(Brush), typeof(SignalBars),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0xD5, 0xD9, 0xE0)), FrameworkPropertyMetadataOptions.AffectsRender));

    public int Bars { get => (int)GetValue(BarsProperty); set => SetValue(BarsProperty, value); }
    public Brush ActiveBrush { get => (Brush)GetValue(ActiveBrushProperty); set => SetValue(ActiveBrushProperty, value); }
    public Brush InactiveBrush { get => (Brush)GetValue(InactiveBrushProperty); set => SetValue(InactiveBrushProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsNaN(Width) ? 22 : Width, double.IsNaN(Height) ? 14 : Height);

    protected override void OnRender(DrawingContext dc)
    {
        const int count = 5;
        double w = ActualWidth, h = ActualHeight;
        double gap = Math.Max(1.5, w / 14);
        double barW = (w - gap * (count - 1)) / count;
        for (int i = 0; i < count; i++)
        {
            double bh = h * (i + 1) / count;
            var rect = new Rect(i * (barW + gap), h - bh, barW, bh);
            dc.DrawRoundedRectangle(i < Bars ? ActiveBrush : InactiveBrush, null, rect, 1, 1);
        }
    }
}
