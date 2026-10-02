namespace LanguageCursor.Models;

public sealed class InputLanguageInfo
{
    public required string Id { get; init; }
    public required string CultureName { get; init; }
    public required string DisplayName { get; init; }
    public required string KeyboardLayoutId { get; init; }
    /// <summary>Keyboard layout name such as "US" or "Ukrainian (Enhanced)", when Windows provides one.</summary>
    public string? LayoutName { get; init; }
    public required string DefaultLabel { get; init; }

    public string Description => string.IsNullOrWhiteSpace(LayoutName) ? CultureName : $"{LayoutName} keyboard · {CultureName}";
}
