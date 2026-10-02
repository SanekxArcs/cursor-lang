using Microsoft.Win32;

namespace LanguageCursor.Services;

public sealed class StartupService
{
    /// <summary>Passed by the Run key so the app starts quietly in the tray.</summary>
    public const string BackgroundArgument = "--background";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LanguageCursor";

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? throw new InvalidOperationException("Could not open the per-user startup registry key.");
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Start with Windows is available when running the published application executable.");
        key.SetValue(ValueName, $"\"{executable}\" {BackgroundArgument}", RegistryValueKind.String);
    }
}
