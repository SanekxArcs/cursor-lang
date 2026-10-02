using LanguageCursor.Services;
using LanguageCursor.ViewModels;
using LanguageCursor.Views;

namespace LanguageCursor;

internal sealed class AppHost : IDisposable
{
    private readonly SettingsService _settings = new();
    private readonly InputLanguageService _languages = new();
    private readonly CursorService _cursor;
    private readonly OverlayService _overlay;
    private readonly InputLanguageMonitor _monitor;
    private readonly StartupService _startup = new();
    private readonly ThemeService _theme = new();
    private readonly TrayService _tray;
    private bool _disposed;

    public AppHost()
    {
        Logger = new AppLogger();
        _cursor = new CursorService(Logger);
        _overlay = new OverlayService(Logger);
        _monitor = new InputLanguageMonitor(_languages);
        ViewModel = new MainViewModel(_settings, _languages, _cursor, _overlay, _monitor, _startup, _theme, Logger);
        _tray = new TrayService(
            () => ViewModel.OpenWindow(),
            () => ViewModel.ToggleEnabled(),
            () => ViewModel.RestoreWindowsCursor(),
            () => ((App)System.Windows.Application.Current).RequestExit());
        ViewModel.StateChanged += UpdateTray;
        _theme.EffectiveThemeChanged += _tray.SetDarkTheme;
    }

    public AppLogger Logger { get; }
    public MainViewModel ViewModel { get; }

    public void Start()
    {
        Logger.Info("Application started");
        ViewModel.Initialize();
        _tray.SetDarkTheme(_theme.IsDark);
        _tray.Show();
        UpdateTray();
    }

    public void AttachWindow(MainWindow window)
    {
        ViewModel.SetWindow(window);
        window.Closing += (_, e) =>
        {
            if (_disposed) return;
            e.Cancel = true;
            window.Hide();
        };
    }

    public void RestoreCursor()
    {
        try { _cursor.RestoreWindowsCursor(); }
        catch { /* Called from crash handlers; never throw. */ }
    }

    private void UpdateTray() => _tray.Update(ViewModel.TrayLanguageText, ViewModel.IsEnabled);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ViewModel.StateChanged -= UpdateTray;
        ViewModel.FlushSave();
        _monitor.Dispose();
        _overlay.Dispose();
        if (_cursor.HasAppliedCursors) _cursor.RestoreWindowsCursor();
        _tray.Dispose();
        _theme.Dispose();
        Logger.Info("Application exited");
        Logger.Dispose();
    }
}
