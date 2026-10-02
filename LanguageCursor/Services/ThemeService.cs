using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using LanguageCursor.Models;

namespace LanguageCursor.Services;

/// <summary>Applies Light/Dark/System theme to the app and follows Windows when set to System.</summary>
public sealed class ThemeService : IDisposable
{
    private AppTheme _theme = AppTheme.System;
    private bool? _appliedDark;

    public ThemeService() => SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

    public event Action<bool>? EffectiveThemeChanged;
    public bool IsDark => _appliedDark ?? IsSystemDark();

    public void Apply(AppTheme theme)
    {
        _theme = theme;
        Update(force: true);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_theme != AppTheme.System) return;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Update(force: false));
    }

    private void Update(bool force)
    {
        var dark = _theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => IsSystemDark()
        };
        if (!force && dark == _appliedDark) return;
        _appliedDark = dark;
        ApplicationThemeManager.Apply(dark ? ApplicationTheme.Dark : ApplicationTheme.Light, WindowBackdropType.Mica, updateAccent: true);
        EffectiveThemeChanged?.Invoke(dark);
    }

    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
}
