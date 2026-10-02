using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LanguageCursor.Models;
using LanguageCursor.Rendering;
using LanguageCursor.Services;

namespace LanguageCursor.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    public static IReadOnlyList<Option<AppTheme>> ThemeOptions { get; } =
    [
        new(AppTheme.System, "Use system setting"),
        new(AppTheme.Light, "Light"),
        new(AppTheme.Dark, "Dark")
    ];

    public static IReadOnlyList<Option<IndicatorMode>> ModeOptions { get; } =
    [
        new(IndicatorMode.FollowPointer, "Label next to the pointer"),
        new(IndicatorMode.TextCursor, "Label inside the text cursor"),
        new(IndicatorMode.Off, "Off")
    ];

    public static IReadOnlyList<Option<BadgeVisibility>> VisibilityOptions { get; } =
    [
        new(BadgeVisibility.Always, "Always"),
        new(BadgeVisibility.OverText, "Only over text fields"),
        new(BadgeVisibility.AfterSwitch, "Briefly after switching")
    ];

    /// <summary>Label colours. The first eight are used automatically, in order, for installed languages.</summary>
    public static IReadOnlyList<string> Palette { get; } =
    [
        "#0067C0", "#C42B1C", "#0F7B0F", "#8764B8", "#CA5010", "#038387", "#E3008C", "#4A5459",
        "#FFB900", "#00B7C3", "#1B1B1F", "#F3F3F3"
    ];

    private readonly SettingsService _settingsService;
    private readonly InputLanguageService _languageService;
    private readonly CursorService _cursorService;
    private readonly OverlayService _overlay;
    private readonly InputLanguageMonitor _monitor;
    private readonly StartupService _startupService;
    private readonly ThemeService _themeService;
    private readonly AppLogger _logger;
    private readonly DispatcherTimer _saveTimer;
    private readonly HashSet<string> _refreshedForUnknownLayout = [];
    private HashSet<string> _installedIds = [];
    private AppSettings _settings = new();
    private Window? _window;
    private LanguageItemViewModel? _active;
    private string? _errorMessage;
    private bool _errorFromCursor;

    public MainViewModel(SettingsService settingsService, InputLanguageService languageService, CursorService cursorService, OverlayService overlay,
        InputLanguageMonitor monitor, StartupService startupService, ThemeService themeService, AppLogger logger)
    {
        _settingsService = settingsService;
        _languageService = languageService;
        _cursorService = cursorService;
        _overlay = overlay;
        _monitor = monitor;
        _startupService = startupService;
        _themeService = themeService;
        _logger = logger;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) => FlushSave();
        _monitor.LanguageChanged += OnLanguageChanged;

        RefreshLanguagesCommand = new RelayCommand(RefreshLanguages);
        ChooseCursorCommand = new RelayCommand<LanguageItemViewModel>(ChooseCursor);
        RemoveCursorCommand = new RelayCommand<LanguageItemViewModel>(RemoveCursor);
        ChooseTypingImageCommand = new RelayCommand<LanguageItemViewModel>(ChooseTypingImage);
        RemoveTypingImageCommand = new RelayCommand<LanguageItemViewModel>(RemoveTypingImage);
        ResetLabelCommand = new RelayCommand<LanguageItemViewModel>(item => item.ResetLabel());
        ResetColorCommand = new RelayCommand<LanguageItemViewModel>(item => item.ResetColor());
        RestoreCursorCommand = new RelayCommand(RestoreWindowsCursor);
        ResetAppearanceCommand = new RelayCommand(ResetAppearance);
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);
    }

    public event Action? StateChanged;
    public ObservableCollection<LanguageItemViewModel> Languages { get; } = [];
    public ICommand RefreshLanguagesCommand { get; }
    public ICommand ChooseCursorCommand { get; }
    public ICommand RemoveCursorCommand { get; }
    public ICommand ChooseTypingImageCommand { get; }
    public ICommand RemoveTypingImageCommand { get; }
    public ICommand ResetLabelCommand { get; }
    public ICommand ResetColorCommand { get; }
    public ICommand RestoreCursorCommand { get; }
    public ICommand ResetAppearanceCommand { get; }
    public ICommand OpenDataFolderCommand { get; }

    public string AppVersion { get; } = $"Version {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";

    #region Status

    public string CurrentLanguageDisplay => _active?.DisplayName ?? "Detecting…";
    public string CurrentLanguageDescription => _active?.Description ?? string.Empty;
    public string CurrentLabel => _active?.EffectiveLabel ?? "…";
    public Color CurrentColor => _active?.Color ?? Color.FromRgb(0x4A, 0x54, 0x59);
    public string? CurrentImagePath => _active?.ImagePath;
    public string TrayLanguageText => _active is null ? "—" : $"{_active.DisplayName} ({_active.EffectiveLabel})";

    public string StatusText => !IsEnabled
        ? "Paused. Windows cursors are unchanged."
        : Mode switch
        {
            IndicatorMode.FollowPointer => "The label follows your pointer.",
            IndicatorMode.TextCursor => "The label is shown in the text cursor.",
            _ => "Language indicator is off."
        };

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (!SetProperty(ref _errorMessage, value)) return;
            OnPropertyChanged(nameof(HasError));
        }
    }

    /// <summary>Bound to the InfoBar; closing it clears the message.</summary>
    public bool HasError
    {
        get => _errorMessage is not null;
        set { if (!value) ErrorMessage = null; }
    }

    public bool IsEnabled
    {
        get => _settings.SwitchingEnabled;
        set
        {
            if (_settings.SwitchingEnabled == value) return;
            _settings.SwitchingEnabled = value;
            ScheduleSave();
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            ApplyIndicator(switched: value);
            StateChanged?.Invoke();
        }
    }

    #endregion

    #region General

    public AppTheme Theme
    {
        get => _settings.Theme;
        set
        {
            if (_settings.Theme == value) return;
            _settings.Theme = value;
            _themeService.Apply(value);
            ScheduleSave();
            OnPropertyChanged();
        }
    }

    public bool StartWithWindows
    {
        get => _settings.StartWithWindows;
        set
        {
            if (_settings.StartWithWindows == value) return;
            try
            {
                _startupService.SetEnabled(value);
                _settings.StartWithWindows = value;
                FlushSave(force: true);
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
                _logger.Error("Could not update startup setting", ex);
            }
            OnPropertyChanged();
        }
    }

    #endregion

    #region Indicator

    public IndicatorMode Mode
    {
        get => _settings.IndicatorMode;
        set
        {
            if (_settings.IndicatorMode == value) return;
            _settings.IndicatorMode = value;
            ScheduleSave();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFollowPointerMode));
            OnPropertyChanged(nameof(IsTextCursorMode));
            OnPropertyChanged(nameof(StatusText));
            ApplyIndicator(switched: true);
        }
    }

    public bool IsFollowPointerMode => Mode == IndicatorMode.FollowPointer;
    public bool IsTextCursorMode => Mode == IndicatorMode.TextCursor;

    public BadgePosition Position
    {
        get => _settings.Overlay.Position;
        set
        {
            if (_settings.Overlay.Position == value) return;
            UpdateOverlay(o => o.Position = value);
            OnPropertyChanged(nameof(UsesHorizontalOffset));
            OnPropertyChanged(nameof(UsesVerticalOffset));
        }
    }

    public bool UsesHorizontalOffset => BadgeLayout.UsesHorizontalOffset(Position);
    public bool UsesVerticalOffset => BadgeLayout.UsesVerticalOffset(Position);

    public double OffsetX
    {
        get => _settings.Overlay.OffsetX;
        set
        {
            var normalized = Math.Round(Math.Clamp(value, OverlaySettings.MinOffset, OverlaySettings.MaxOffset));
            if (normalized != _settings.Overlay.OffsetX) UpdateOverlay(o => o.OffsetX = normalized);
        }
    }

    public double OffsetY
    {
        get => _settings.Overlay.OffsetY;
        set
        {
            var normalized = Math.Round(Math.Clamp(value, OverlaySettings.MinOffset, OverlaySettings.MaxOffset));
            if (normalized != _settings.Overlay.OffsetY) UpdateOverlay(o => o.OffsetY = normalized);
        }
    }

    public double BadgeSize
    {
        get => _settings.Overlay.Size;
        set
        {
            var normalized = Math.Round(Math.Clamp(value, OverlaySettings.MinSize, OverlaySettings.MaxSize));
            if (normalized != _settings.Overlay.Size) UpdateOverlay(o => o.Size = normalized);
        }
    }

    public double BadgeOpacity
    {
        get => _settings.Overlay.Opacity;
        set
        {
            var normalized = Math.Round(Math.Clamp(value, 0.2, 1), 2);
            if (normalized != _settings.Overlay.Opacity) UpdateOverlay(o => o.Opacity = normalized);
        }
    }

    public double Roundness
    {
        get => _settings.Overlay.Roundness;
        set
        {
            var normalized = Math.Round(Math.Clamp(value, 0, 1), 2);
            if (normalized != _settings.Overlay.Roundness) UpdateOverlay(o => o.Roundness = normalized);
        }
    }

    public bool Shadow
    {
        get => _settings.Overlay.Shadow;
        set { if (value != _settings.Overlay.Shadow) UpdateOverlay(o => o.Shadow = value); }
    }

    public BadgeVisibility Visibility
    {
        get => _settings.Overlay.Visibility;
        set
        {
            if (value == _settings.Overlay.Visibility) return;
            UpdateOverlay(o => o.Visibility = value);
            OnPropertyChanged(nameof(IsAfterSwitchVisibility));
            OnPropertyChanged(nameof(PreviewOverText));
            if (value == BadgeVisibility.AfterSwitch) _overlay.NotifyLanguageSwitched();
        }
    }

    public bool IsAfterSwitchVisibility => Visibility == BadgeVisibility.AfterSwitch;
    public bool PreviewOverText => Visibility == BadgeVisibility.OverText;

    public double AfterSwitchSeconds
    {
        get => _settings.Overlay.AfterSwitchSeconds;
        set
        {
            var normalized = Math.Round(Math.Clamp(value, 0.5, 5) * 4) / 4;
            if (normalized == _settings.Overlay.AfterSwitchSeconds) return;
            UpdateOverlay(o => o.AfterSwitchSeconds = normalized);
            _overlay.NotifyLanguageSwitched(); // demonstrate the new duration
        }
    }

    public bool HideWhenPointerHidden
    {
        get => _settings.Overlay.HideWhenPointerHidden;
        set { if (value != _settings.Overlay.HideWhenPointerHidden) UpdateOverlay(o => o.HideWhenPointerHidden = value); }
    }

    public bool HideInFullScreen
    {
        get => _settings.Overlay.HideInFullScreen;
        set { if (value != _settings.Overlay.HideInFullScreen) UpdateOverlay(o => o.HideInFullScreen = value); }
    }

    public double TextCursorScale
    {
        get => _settings.TypingIndicatorScale;
        set
        {
            var normalized = Math.Round(Math.Clamp(value, 1, 3) * 4) / 4;
            if (normalized == _settings.TypingIndicatorScale) return;
            _settings.TypingIndicatorScale = normalized;
            ScheduleSave();
            OnPropertyChanged();
            ApplyIndicator();
        }
    }

    private void UpdateOverlay(Action<OverlaySettings> change, [CallerMemberName] string? name = null)
    {
        change(_settings.Overlay);
        OnPropertyChanged(name);
        ScheduleSave();
        ApplyIndicator();
    }

    private void ResetAppearance()
    {
        _settings.Overlay.ResetAppearance();
        foreach (var property in new[] { nameof(Position), nameof(UsesHorizontalOffset), nameof(UsesVerticalOffset), nameof(OffsetX), nameof(OffsetY),
                     nameof(BadgeSize), nameof(BadgeOpacity), nameof(Roundness), nameof(Shadow) })
            OnPropertyChanged(property);
        ScheduleSave();
        ApplyIndicator();
    }

    #endregion

    public void Initialize()
    {
        _settings = _settingsService.Load(_logger);
        _themeService.Apply(_settings.Theme);
        _cursorService.RestoreIfPreviousSessionCrashed();
        if (_settings.StartWithWindows)
        {
            try
            {
                // A newly published build may live at a different path than the previous version.
                _startupService.SetEnabled(true);
            }
            catch (Exception ex)
            {
                ShowError($"Could not update Start with Windows: {ex.Message}");
                _logger.Error("Could not refresh startup registration", ex);
            }
        }
        RefreshLanguages();
        OnPropertyChanged(string.Empty);
        _monitor.Start();
    }

    public void SetWindow(Window window) => _window = window;

    public void OpenWindow()
    {
        if (_window is null) return;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void ToggleEnabled() => IsEnabled = !IsEnabled;

    /// <summary>Panic button: reloads the Windows cursor scheme and pauses so nothing is re-applied.</summary>
    public void RestoreWindowsCursor()
    {
        IsEnabled = false;
        _cursorService.RestoreWindowsCursor();
        if (_errorFromCursor) ErrorMessage = null;
    }

    public void FlushSave(bool force = false)
    {
        if (!_saveTimer.IsEnabled && !force) return;
        _saveTimer.Stop();
        _settingsService.Save(_settings, _logger);
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void RefreshLanguages()
    {
        var installed = _languageService.GetInstalledLanguages(_logger);
        _installedIds = installed.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Languages.Clear();
        for (var index = 0; index < installed.Count; index++)
        {
            var info = installed[index];
            Languages.Add(new LanguageItemViewModel(info, FindMapping(info), AutoColor(index), OnLanguageItemChanged));
        }
        _logger.Info($"Detected languages: {string.Join(", ", Languages.Select(x => x.Info.CultureName))}");
        if (_monitor.Current is { } current)
        {
            SetActive(Resolve(current));
            ApplyIndicator();
        }
    }

    private LanguageCursorMapping? FindMapping(InputLanguageInfo info) =>
        _settings.CursorMappings.FirstOrDefault(m => string.Equals(m.InputLanguageId, info.Id, StringComparison.OrdinalIgnoreCase))
        // A layout's HKL can change after Windows settings changes; adopt a mapping whose layout is no longer installed.
        ?? _settings.CursorMappings.FirstOrDefault(m => m.CultureName == info.CultureName && !_installedIds.Contains(m.InputLanguageId));

    private static Color AutoColor(int index)
    {
        LanguageItemViewModel.TryParseColor(Palette[index % 8], out var color);
        return color;
    }

    private LanguageItemViewModel Resolve(InputLanguageInfo info)
    {
        var item = Languages.FirstOrDefault(x => x.Id == info.Id);
        if (item is not null) return item;
        // Not in the list: a layout was probably added in Windows settings. Refresh once per layout.
        if (_refreshedForUnknownLayout.Add(info.Id))
        {
            RefreshLanguages();
            item = Languages.FirstOrDefault(x => x.Id == info.Id);
            if (item is not null) return item;
        }
        var colorIndex = Math.Abs(info.CultureName.GetHashCode()) % 8;
        return new LanguageItemViewModel(info, FindMapping(info), AutoColor(colorIndex), OnLanguageItemChanged);
    }

    private void OnLanguageChanged(object? sender, InputLanguageInfo info)
    {
        SetActive(Resolve(info));
        ApplyIndicator(switched: true);
        _logger.Info($"Language changed to {info.CultureName}");
    }

    private void SetActive(LanguageItemViewModel item)
    {
        _active = item;
        foreach (var language in Languages) language.IsActive = ReferenceEquals(language, item);
        RaiseCurrent();
    }

    private void RaiseCurrent()
    {
        OnPropertyChanged(nameof(CurrentLanguageDisplay));
        OnPropertyChanged(nameof(CurrentLanguageDescription));
        OnPropertyChanged(nameof(CurrentLabel));
        OnPropertyChanged(nameof(CurrentColor));
        OnPropertyChanged(nameof(CurrentImagePath));
        OnPropertyChanged(nameof(TrayLanguageText));
        StateChanged?.Invoke();
    }

    private void OnLanguageItemChanged(LanguageItemViewModel item)
    {
        SaveMapping(item);
        if (!ReferenceEquals(item, _active)) return;
        RaiseCurrent();
        ApplyIndicator();
    }

    /// <summary>Brings the overlay and system cursors in line with the current language and settings.</summary>
    private void ApplyIndicator(bool switched = false)
    {
        var item = _active;
        var options = OverlayOptions.From(_settings.Overlay);
        if (!IsEnabled || item is null)
        {
            _overlay.Update(false, null, options);
            TryApplyCursors(null, null);
            return;
        }

        var arrowPath = item.CursorPath is { } path && File.Exists(path) ? path : null;
        var textCursor = Mode == IndicatorMode.TextCursor ? new TextCursorRequest(item.CreateBadge(8, Roundness, false), TextCursorScale) : null;
        TryApplyCursors(arrowPath, textCursor);
        _overlay.Update(Mode == IndicatorMode.FollowPointer, item.CreateBadge(BadgeSize, Roundness, Shadow), options);
        if (switched) _overlay.NotifyLanguageSwitched();
    }

    private void TryApplyCursors(string? arrowPath, TextCursorRequest? textCursor)
    {
        try
        {
            _cursorService.Apply(arrowPath, textCursor);
            if (_errorFromCursor) ErrorMessage = null;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to apply cursor", ex);
            ShowError($"Could not apply the cursor: {ex.Message}", fromCursor: true);
            try { _cursorService.Apply(null, null); } catch { /* Already logged. */ }
        }
    }

    private void SaveMapping(LanguageItemViewModel item)
    {
        _settings.CursorMappings.RemoveAll(m =>
            string.Equals(m.InputLanguageId, item.Id, StringComparison.OrdinalIgnoreCase)
            || (m.CultureName == item.Info.CultureName && !_installedIds.Contains(m.InputLanguageId)));
        var mapping = item.ToMapping();
        if (!mapping.IsEmpty) _settings.CursorMappings.Add(mapping);
        ScheduleSave();
    }

    private void ChooseCursor(LanguageItemViewModel item)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Cursor files (*.cur;*.ani)|*.cur;*.ani", Title = $"Choose a pointer for {item.DisplayName}" };
        if (dialog.ShowDialog(_window) != true) return;
        try
        {
            if (!CursorService.CanLoad(dialog.FileName)) throw new InvalidOperationException("Windows could not read this cursor file.");
            var previous = item.CursorPath;
            item.CursorPath = _settingsService.ImportCursor(dialog.FileName);
            DeleteIfUnused(previous);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            _logger.Error("Failed to import cursor", ex);
        }
    }

    private void RemoveCursor(LanguageItemViewModel item)
    {
        var previous = item.CursorPath;
        item.CursorPath = null;
        DeleteIfUnused(previous);
    }

    private void ChooseTypingImage(LanguageItemViewModel item)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Image files (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
            Title = $"Choose a label image for {item.DisplayName}"
        };
        if (dialog.ShowDialog(_window) != true) return;
        try
        {
            if (!CanLoadImage(dialog.FileName)) throw new InvalidOperationException("This image could not be opened.");
            var previous = item.ImagePath;
            item.ImagePath = _settingsService.ImportTypingIndicatorImage(dialog.FileName);
            DeleteIfUnused(previous);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            _logger.Error("Failed to import label image", ex);
        }
    }

    private void RemoveTypingImage(LanguageItemViewModel item)
    {
        var previous = item.ImagePath;
        item.ImagePath = null;
        DeleteIfUnused(previous);
    }

    private static bool CanLoadImage(string path)
    {
        try
        {
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(new Uri(path), System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            return decoder.Frames.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private void DeleteIfUnused(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var inUse = _settings.CursorMappings.Any(m =>
            string.Equals(m.CursorPath, path, StringComparison.OrdinalIgnoreCase)
            || string.Equals(m.TypingIndicatorImagePath, path, StringComparison.OrdinalIgnoreCase));
        if (!inUse) _settingsService.DeleteImportedFile(path, _logger);
    }

    private void OpenDataFolder()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_settingsService.DataDirectory}\"") { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void ShowError(string message, bool fromCursor = false)
    {
        _errorFromCursor = fromCursor;
        ErrorMessage = message;
    }
}
