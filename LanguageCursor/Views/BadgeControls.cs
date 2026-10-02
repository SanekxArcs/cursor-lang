using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using LanguageCursor.Models;
using LanguageCursor.Rendering;

namespace LanguageCursor.Views;

/// <summary>Draws a language label exactly as it appears on screen.</summary>
public class BadgeView : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = Register(nameof(Text), typeof(string), "");
    public static readonly DependencyProperty BadgeColorProperty = Register(nameof(BadgeColor), typeof(Color), Color.FromRgb(0x00, 0x67, 0xC0));
    public static readonly DependencyProperty ImagePathProperty = Register(nameof(ImagePath), typeof(string), null);
    public static readonly DependencyProperty BadgeFontSizeProperty = Register(nameof(BadgeFontSize), typeof(double), 12.0);
    public static readonly DependencyProperty RoundnessProperty = Register(nameof(Roundness), typeof(double), 0.45);
    public static readonly DependencyProperty ShadowProperty = Register(nameof(Shadow), typeof(bool), false);

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Color BadgeColor { get => (Color)GetValue(BadgeColorProperty); set => SetValue(BadgeColorProperty, value); }
    public string? ImagePath { get => (string?)GetValue(ImagePathProperty); set => SetValue(ImagePathProperty, value); }
    public double BadgeFontSize { get => (double)GetValue(BadgeFontSizeProperty); set => SetValue(BadgeFontSizeProperty, value); }
    public double Roundness { get => (double)GetValue(RoundnessProperty); set => SetValue(RoundnessProperty, value); }
    public bool Shadow { get => (bool)GetValue(ShadowProperty); set => SetValue(ShadowProperty, value); }

    protected BadgeStyle LabelStyle => new(Text ?? "", BadgeColor, ImagePath, Math.Max(4, BadgeFontSize), Roundness, Shadow);
    protected double PixelsPerDip => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    protected static DependencyProperty Register(string name, Type type, object? defaultValue) =>
        DependencyProperty.Register(name, type, typeof(BadgeView),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = BadgeRenderer.Measure(LabelStyle, PixelsPerDip);
        var margin = BadgeRenderer.MarginFor(LabelStyle);
        return new Size(size.Width + 2 * margin, size.Height + 2 * margin);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var margin = BadgeRenderer.MarginFor(LabelStyle);
        BadgeRenderer.Draw(dc, LabelStyle, new Point(margin, margin), PixelsPerDip);
    }
}

/// <summary>A mock pointer with the label placed by the same layout rules as the real overlay.</summary>
public sealed class BadgePreview : BadgeView
{
    public static readonly DependencyProperty PositionProperty = RegisterPreview(nameof(Position), typeof(BadgePosition), BadgePosition.BottomRight);
    public static readonly DependencyProperty OffsetXProperty = RegisterPreview(nameof(OffsetX), typeof(double), 14.0);
    public static readonly DependencyProperty OffsetYProperty = RegisterPreview(nameof(OffsetY), typeof(double), 18.0);
    public static readonly DependencyProperty BadgeOpacityProperty = RegisterPreview(nameof(BadgeOpacity), typeof(double), 1.0);
    public static readonly DependencyProperty UseTextPointerProperty = RegisterPreview(nameof(UseTextPointer), typeof(bool), false);

    public BadgePosition Position { get => (BadgePosition)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public double OffsetX { get => (double)GetValue(OffsetXProperty); set => SetValue(OffsetXProperty, value); }
    public double OffsetY { get => (double)GetValue(OffsetYProperty); set => SetValue(OffsetYProperty, value); }
    public double BadgeOpacity { get => (double)GetValue(BadgeOpacityProperty); set => SetValue(BadgeOpacityProperty, value); }
    public bool UseTextPointer { get => (bool)GetValue(UseTextPointerProperty); set => SetValue(UseTextPointerProperty, value); }

    private static readonly Geometry Arrow = Geometry.Parse("M0,0 L0,17.2 L4.2,13.4 L7,19.6 L9.6,18.5 L6.9,12.4 L12.4,12.4 Z");
    private static readonly Geometry IBeam = Geometry.Parse("M-4,-9.5 H-1.2 Q0,-9.5 0,-8.3 Q0,-9.5 1.2,-9.5 H4 M0,-8.3 V8.3 M-4,9.5 H-1.2 Q0,9.5 0,8.3 Q0,9.5 1.2,9.5 H4");
    private static readonly Pen IBeamHalo = CreatePen(Brushes.White, 3.2);
    private static readonly Pen IBeamStroke = CreatePen(Brushes.Black, 1.3);
    private static readonly Pen ArrowOutline = CreatePen(Brushes.Black, 1);

    private static Pen CreatePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }

    private static DependencyProperty RegisterPreview(string name, Type type, object defaultValue) =>
        DependencyProperty.Register(name, type, typeof(BadgePreview), new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var style = LabelStyle;
        var badgeSize = BadgeRenderer.Measure(LabelStyle, PixelsPerDip);
        var badge = BadgeLayout.Place(Position, 0, 0, badgeSize.Width, badgeSize.Height, OffsetX, OffsetY);
        var pointerBounds = UseTextPointer ? new Rect(-4, -10, 8, 20) : new Rect(-1, -1, 14.5, 22);
        var content = Rect.Union(pointerBounds, new Rect(badge, badgeSize));
        content.Inflate(BadgeRenderer.ShadowMargin + 8, BadgeRenderer.ShadowMargin + 8);

        // Centre the pointer + label group, shrinking it only when large offsets would not fit.
        var scale = Math.Min(2.2, Math.Min(width / content.Width, height / content.Height));
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));
        dc.PushTransform(new TranslateTransform((width - content.Width * scale) / 2 - content.Left * scale, (height - content.Height * scale) / 2 - content.Top * scale));
        dc.PushTransform(new ScaleTransform(scale, scale));

        dc.PushOpacity(Math.Clamp(BadgeOpacity, 0.2, 1));
        BadgeRenderer.Draw(dc, LabelStyle, badge, PixelsPerDip * scale);
        dc.Pop();

        if (UseTextPointer)
        {
            dc.DrawGeometry(null, IBeamHalo, IBeam);
            dc.DrawGeometry(null, IBeamStroke, IBeam);
        }
        else
        {
            dc.DrawGeometry(Brushes.White, ArrowOutline, Arrow);
        }

        dc.Pop();
        dc.Pop();
        dc.Pop();
    }
}

public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is string name && string.Equals(value.ToString(), name, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string name ? Enum.Parse(targetType, name) : Binding.DoNothing;
}

public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && ViewModels.LanguageItemViewModel.TryParseColor(hex, out var color))
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
