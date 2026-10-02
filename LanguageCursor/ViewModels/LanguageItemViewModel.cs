using System.Globalization;
using System.Windows.Media;
using LanguageCursor.Models;
using LanguageCursor.Rendering;

namespace LanguageCursor.ViewModels;

public sealed class LanguageItemViewModel : ObservableObject
{
    public const int MaxLabelLength = 6;
    private readonly Action<LanguageItemViewModel> _changed;
    private readonly Color _autoColor;
    private string? _customLabel;
    private string? _customColor;
    private string? _colorText;
    private string? _imagePath;
    private string? _cursorPath;
    private bool _isActive;

    public LanguageItemViewModel(InputLanguageInfo info, LanguageCursorMapping? mapping, Color autoColor, Action<LanguageItemViewModel> changed)
    {
        Info = info;
        _autoColor = autoColor;
        _changed = changed;
        if (mapping is null) return;
        _customLabel = CleanLabel(mapping.Label);
        _customColor = TryParseColor(mapping.Color, out _) ? NormalizeHex(mapping.Color!) : null;
        _imagePath = ExistingFile(mapping.TypingIndicatorImagePath);
        _cursorPath = ExistingFile(mapping.CursorPath);
    }

    public InputLanguageInfo Info { get; }
    public string Id => Info.Id;
    public string DisplayName => Info.DisplayName;
    public string Description => Info.Description;
    public string DefaultLabel => Info.DefaultLabel;

    public bool IsActive { get => _isActive; set => SetProperty(ref _isActive, value); }

    /// <summary>Custom label text; empty means the language code is used.</summary>
    public string LabelText
    {
        get => _customLabel ?? string.Empty;
        set
        {
            // The TextBox is not refreshed here so trimming doesn't fight the caret while typing.
            var cleaned = CleanLabel(value);
            if (cleaned == _customLabel) return;
            _customLabel = cleaned;
            OnPropertyChanged(nameof(EffectiveLabel));
            OnPropertyChanged(nameof(HasCustomLabel));
            _changed(this);
        }
    }

    public void ResetLabel()
    {
        if (_customLabel is null) return;
        _customLabel = null;
        OnPropertyChanged(nameof(LabelText));
        OnPropertyChanged(nameof(EffectiveLabel));
        OnPropertyChanged(nameof(HasCustomLabel));
        _changed(this);
    }

    public string EffectiveLabel => _customLabel ?? DefaultLabel;
    public bool HasCustomLabel => _customLabel is not null;

    public Color Color => _customColor is not null && TryParseColor(_customColor, out var color) ? color : _autoColor;
    public bool HasCustomColor => _customColor is not null;

    /// <summary>Selected palette swatch (#RRGGBB). Values outside the palette leave no swatch selected.</summary>
    public string ColorHex
    {
        get => ToHex(Color);
        set
        {
            // Selector writes null when the current colour is not in the palette; ignore that.
            if (value is null || !TryParseColor(value, out _)) return;
            SetCustomColor(NormalizeHex(value), updateText: true);
        }
    }

    /// <summary>Free-form colour entry; applied as soon as it parses.</summary>
    public string ColorText
    {
        get => _colorText ?? ColorHex;
        set
        {
            _colorText = value;
            OnPropertyChanged();
            if (TryParseColor(value, out _)) SetCustomColor(NormalizeHex(value), updateText: false);
        }
    }

    public string? ImagePath
    {
        get => _imagePath;
        set
        {
            if (!SetProperty(ref _imagePath, value)) return;
            OnPropertyChanged(nameof(HasImage));
            OnPropertyChanged(nameof(ImageFileName));
            _changed(this);
        }
    }

    public bool HasImage => _imagePath is not null;
    public string ImageFileName => _imagePath is null ? "None — the label shows text" : DisplayFileName(_imagePath);

    public string? CursorPath
    {
        get => _cursorPath;
        set
        {
            if (!SetProperty(ref _cursorPath, value)) return;
            OnPropertyChanged(nameof(HasCursor));
            OnPropertyChanged(nameof(CursorFileName));
            _changed(this);
        }
    }

    public bool HasCursor => _cursorPath is not null;
    public string CursorFileName => _cursorPath is null ? "Windows pointer" : DisplayFileName(_cursorPath);

    public void ResetColor()
    {
        if (_customColor is null) return;
        _customColor = null;
        _colorText = null;
        RaiseColorChanged();
        _changed(this);
    }

    public BadgeStyle CreateBadge(double fontSize, double roundness, bool shadow) => new(EffectiveLabel, Color, ImagePath, fontSize, roundness, shadow);

    public LanguageCursorMapping ToMapping() => new()
    {
        InputLanguageId = Info.Id,
        CultureName = Info.CultureName,
        CursorPath = _cursorPath,
        TypingIndicatorImagePath = _imagePath,
        Label = _customLabel,
        Color = _customColor
    };

    private void SetCustomColor(string hex, bool updateText)
    {
        if (string.Equals(hex, _customColor, StringComparison.OrdinalIgnoreCase)) return;
        _customColor = hex;
        if (updateText) _colorText = null;
        RaiseColorChanged();
        _changed(this);
    }

    private void RaiseColorChanged()
    {
        OnPropertyChanged(nameof(Color));
        OnPropertyChanged(nameof(ColorHex));
        OnPropertyChanged(nameof(ColorText));
        OnPropertyChanged(nameof(HasCustomColor));
    }

    private static string? CleanLabel(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        var info = new StringInfo(trimmed);
        return info.LengthInTextElements > MaxLabelLength ? info.SubstringByTextElements(0, MaxLabelLength) : trimmed;
    }

    private static string? ExistingFile(string? path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;

    /// <summary>Imported files get a "-{guid}" suffix; hide it in the UI.</summary>
    private static string DisplayFileName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var dash = name.LastIndexOf('-');
        if (dash > 0 && name.Length - dash - 1 == 32) name = name[..dash];
        return name + Path.GetExtension(path);
    }

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static string NormalizeHex(string value) => TryParseColor(value, out var color) ? ToHex(color) : value;

    public static bool TryParseColor(string? value, out Color color)
    {
        color = default;
        var text = value?.Trim().TrimStart('#');
        if (text is null || (text.Length != 6 && text.Length != 3)) return false;
        if (text.Length == 3) text = string.Concat(text.Select(c => $"{c}{c}"));
        if (!int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return false;
        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }
}
