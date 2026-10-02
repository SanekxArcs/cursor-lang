using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LanguageCursor.Models;

namespace LanguageCursor.Rendering;

/// <summary>Everything needed to draw a language label. Sizes are in device-independent pixels.</summary>
public sealed record BadgeStyle(string Text, Color Background, string? ImagePath, double FontSize, double Roundness, bool Shadow)
{
    public Color Foreground => BadgeRenderer.ForegroundFor(Background);
}

/// <summary>A badge rasterised to premultiplied BGRA, top-down rows.</summary>
public sealed class RenderedBadge
{
    public required byte[] Pixels { get; init; }
    public required int PixelWidth { get; init; }
    public required int PixelHeight { get; init; }
    /// <summary>Offset and size of the badge itself inside the bitmap (the rest is shadow margin).</summary>
    public required int ContentX { get; init; }
    public required int ContentY { get; init; }
    public required int ContentWidth { get; init; }
    public required int ContentHeight { get; init; }
}

/// <summary>
/// Draws language labels. The same code paints the settings previews, the floating pointer label and the
/// text-cursor label, so what you see in the window is what appears on screen.
/// </summary>
public static class BadgeRenderer
{
    public const double ShadowMargin = 5;
    private static readonly Typeface LabelTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Color DarkText = Color.FromRgb(0x1B, 0x1B, 0x1F);

    public static double MarginFor(BadgeStyle style) => style.Shadow ? ShadowMargin : 1;

    public static Color ForegroundFor(Color background)
    {
        var luminance = RelativeLuminance(background);
        var contrastWithWhite = 1.05 / (luminance + 0.05);
        var contrastWithDark = (luminance + 0.05) / (RelativeLuminance(DarkText) + 0.05);
        return contrastWithWhite >= contrastWithDark ? Colors.White : DarkText;
    }

    public static Size Measure(BadgeStyle style, double pixelsPerDip)
    {
        var height = Math.Round(style.FontSize * 1.6);
        var image = BadgeImageCache.Get(style.ImagePath);
        if (image is not null)
        {
            var aspect = Math.Clamp(image.PixelWidth / (double)Math.Max(1, image.PixelHeight), 0.5, 3);
            return new Size(Math.Round(height * aspect), height);
        }
        var text = CreateText(style, pixelsPerDip, Brushes.Black);
        var width = Math.Max(height, Math.Ceiling(text.WidthIncludingTrailingWhitespace + style.FontSize * 1.1));
        return new Size(width, height);
    }

    /// <summary>Draws the badge with its top-left corner (excluding shadow) at <paramref name="origin"/>.</summary>
    public static void Draw(DrawingContext dc, BadgeStyle style, Point origin, double pixelsPerDip)
    {
        var size = Measure(style, pixelsPerDip);
        var rect = new Rect(origin, size);
        var radius = Math.Clamp(style.Roundness, 0, 1) * size.Height / 2;
        if (style.Shadow) DrawShadow(dc, rect, radius);

        var outline = new Pen(new SolidColorBrush(RelativeLuminance(style.Background) > 0.5
            ? Color.FromArgb(48, 0, 0, 0)
            : Color.FromArgb(56, 255, 255, 255)), 1);
        outline.Freeze();
        var inner = Rect.Inflate(rect, -0.5, -0.5);
        var innerRadius = Math.Max(0, radius - 0.5);

        var image = BadgeImageCache.Get(style.ImagePath);
        if (image is not null)
        {
            dc.PushClip(new RectangleGeometry(rect, radius, radius));
            dc.DrawImage(image, rect);
            dc.Pop();
            dc.DrawRoundedRectangle(null, outline, inner, innerRadius, innerRadius);
            return;
        }

        var background = new SolidColorBrush(style.Background);
        background.Freeze();
        dc.DrawRoundedRectangle(background, outline, inner, innerRadius, innerRadius);

        var foreground = new SolidColorBrush(style.Foreground);
        foreground.Freeze();
        var text = CreateText(style, pixelsPerDip, foreground);
        // Centre on cap height so upper-case codes such as "EN" sit optically in the middle.
        var capHeight = LabelTypeface.CapsHeight * style.FontSize;
        var baseline = rect.Top + rect.Height / 2 + capHeight / 2;
        dc.DrawText(text, new Point(rect.Left + (rect.Width - text.WidthIncludingTrailingWhitespace) / 2, baseline - text.Baseline));
    }

    public static RenderedBadge Render(BadgeStyle style, double scale)
    {
        var size = Measure(style, scale);
        var margin = MarginFor(style);
        var pixelWidth = Math.Max(1, (int)Math.Ceiling((size.Width + 2 * margin) * scale));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling((size.Height + 2 * margin) * scale));

        var visual = new DrawingVisual();
        TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Grayscale);
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            Draw(dc, style, new Point(margin, margin), scale);
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[pixelWidth * pixelHeight * 4];
        bitmap.CopyPixels(pixels, pixelWidth * 4, 0);
        return new RenderedBadge
        {
            Pixels = pixels,
            PixelWidth = pixelWidth,
            PixelHeight = pixelHeight,
            ContentX = (int)Math.Round(margin * scale),
            ContentY = (int)Math.Round(margin * scale),
            ContentWidth = (int)Math.Round(size.Width * scale),
            ContentHeight = (int)Math.Round(size.Height * scale)
        };
    }

    private static FormattedText CreateText(BadgeStyle style, double pixelsPerDip, Brush brush) =>
        new(string.IsNullOrEmpty(style.Text) ? "?" : style.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            LabelTypeface, style.FontSize, brush, pixelsPerDip);

    private static void DrawShadow(DrawingContext dc, Rect rect, double radius)
    {
        // A few stacked translucent layers approximate a soft drop shadow without bitmap effects.
        for (var layer = 4; layer >= 1; layer--)
        {
            var spread = layer * 0.85;
            var brush = new SolidColorBrush(Color.FromArgb((byte)(22 - layer * 3), 0, 0, 0));
            brush.Freeze();
            var shadowRect = Rect.Inflate(rect, spread, spread);
            shadowRect.Offset(0, 1.2);
            dc.DrawRoundedRectangle(brush, null, shadowRect, radius + spread, radius + spread);
        }
    }

    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }
}

/// <summary>Positions a badge relative to the pointer hotspot.</summary>
public static class BadgeLayout
{
    public static bool UsesHorizontalOffset(BadgePosition position) => position is not (BadgePosition.Top or BadgePosition.Bottom);
    public static bool UsesVerticalOffset(BadgePosition position) => position is not (BadgePosition.Left or BadgePosition.Right);
    public static bool IsLeft(BadgePosition p) => p is BadgePosition.TopLeft or BadgePosition.Left or BadgePosition.BottomLeft;
    public static bool IsRight(BadgePosition p) => p is BadgePosition.TopRight or BadgePosition.Right or BadgePosition.BottomRight;
    public static bool IsTop(BadgePosition p) => p is BadgePosition.TopLeft or BadgePosition.Top or BadgePosition.TopRight;
    public static bool IsBottom(BadgePosition p) => p is BadgePosition.BottomLeft or BadgePosition.Bottom or BadgePosition.BottomRight;

    /// <summary>Top-left corner of a <paramref name="width"/>×<paramref name="height"/> badge for a pointer at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public static Point Place(BadgePosition position, double x, double y, double width, double height, double offsetX, double offsetY)
    {
        var left = IsLeft(position) ? x - offsetX - width : IsRight(position) ? x + offsetX : x - width / 2;
        var top = IsTop(position) ? y - offsetY - height : IsBottom(position) ? y + offsetY : y - height / 2;
        return new Point(left, top);
    }
}

/// <summary>Frozen, thread-safe image cache shared by the UI and overlay threads.</summary>
public static class BadgeImageCache
{
    private static readonly ConcurrentDictionary<string, BitmapSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static BitmapSource? Get(string? path) => string.IsNullOrWhiteSpace(path) ? null : Cache.GetOrAdd(path, Load);

    private static BitmapSource? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelHeight = 160;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}
