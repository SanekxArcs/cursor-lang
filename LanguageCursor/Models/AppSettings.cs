using System.Text.Json.Serialization;

namespace LanguageCursor.Models;

public enum AppTheme { System, Light, Dark }

/// <summary>How the active language is shown.</summary>
public enum IndicatorMode
{
    /// <summary>A floating label follows the pointer; Windows cursors are left untouched.</summary>
    FollowPointer,
    /// <summary>The system text cursor (I-beam) is replaced by one with a built-in label.</summary>
    TextCursor,
    Off
}

public enum BadgePosition { TopLeft, Top, TopRight, Left, Right, BottomLeft, Bottom, BottomRight }

public enum BadgeVisibility { Always, OverText, AfterSwitch }

public sealed class AppSettings
{
    public bool SwitchingEnabled { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.System;
    public IndicatorMode IndicatorMode { get; set; } = IndicatorMode.FollowPointer;
    public OverlaySettings Overlay { get; set; } = new();
    /// <summary>Label size multiplier for <see cref="IndicatorMode.TextCursor"/>.</summary>
    public double TypingIndicatorScale { get; set; } = 1;
    public List<LanguageCursorMapping> CursorMappings { get; set; } = [];

    public void Normalize()
    {
        Overlay ??= new OverlaySettings();
        Overlay.Normalize();
        CursorMappings ??= [];
        CursorMappings.RemoveAll(m => m is null || string.IsNullOrWhiteSpace(m.InputLanguageId));
        TypingIndicatorScale = Math.Clamp(TypingIndicatorScale, 1, 3);
        if (!Enum.IsDefined(Theme)) Theme = AppTheme.System;
        if (!Enum.IsDefined(IndicatorMode)) IndicatorMode = IndicatorMode.FollowPointer;
    }
}

public sealed class OverlaySettings
{
    public const double MinOffset = -40, MaxOffset = 120, MinSize = 8, MaxSize = 40;

    public BadgePosition Position { get; set; } = BadgePosition.BottomRight;
    public double OffsetX { get; set; } = 14;
    public double OffsetY { get; set; } = 18;
    /// <summary>Label font size in device-independent pixels.</summary>
    public double Size { get; set; } = 12;
    public double Opacity { get; set; } = 0.95;
    /// <summary>0 = square corners, 1 = pill.</summary>
    public double Roundness { get; set; } = 0.45;
    public bool Shadow { get; set; } = true;
    public BadgeVisibility Visibility { get; set; } = BadgeVisibility.Always;
    public double AfterSwitchSeconds { get; set; } = 1.5;
    public bool HideWhenPointerHidden { get; set; } = true;
    public bool HideInFullScreen { get; set; } = true;

    public void Normalize()
    {
        OffsetX = Math.Clamp(OffsetX, MinOffset, MaxOffset);
        OffsetY = Math.Clamp(OffsetY, MinOffset, MaxOffset);
        Size = Math.Clamp(Size, MinSize, MaxSize);
        Opacity = Math.Clamp(Opacity, 0.2, 1);
        Roundness = Math.Clamp(Roundness, 0, 1);
        AfterSwitchSeconds = Math.Clamp(AfterSwitchSeconds, 0.5, 5);
        if (!Enum.IsDefined(Position)) Position = BadgePosition.BottomRight;
        if (!Enum.IsDefined(Visibility)) Visibility = BadgeVisibility.Always;
    }

    public void ResetAppearance()
    {
        var defaults = new OverlaySettings();
        Position = defaults.Position;
        OffsetX = defaults.OffsetX;
        OffsetY = defaults.OffsetY;
        Size = defaults.Size;
        Opacity = defaults.Opacity;
        Roundness = defaults.Roundness;
        Shadow = defaults.Shadow;
    }
}

public sealed class LanguageCursorMapping
{
    public required string InputLanguageId { get; set; }
    public required string CultureName { get; set; }
    /// <summary>Optional .cur/.ani pointer for this layout (empty = Windows pointer).</summary>
    public string? CursorPath { get; set; }
    public string? TypingIndicatorImagePath { get; set; }
    /// <summary>Custom label text; null uses the language code.</summary>
    public string? Label { get; set; }
    /// <summary>Label background as #RRGGBB; null uses an automatic colour.</summary>
    public string? Color { get; set; }

    [JsonIgnore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(CursorPath) && string.IsNullOrWhiteSpace(TypingIndicatorImagePath)
        && string.IsNullOrWhiteSpace(Label) && string.IsNullOrWhiteSpace(Color);
}
