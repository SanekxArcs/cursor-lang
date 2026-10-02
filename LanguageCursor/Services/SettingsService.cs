using System.Text.Json;
using System.Text.Json.Serialization;
using LanguageCursor.Models;

namespace LanguageCursor.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _settingsPath;
    public string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanguageCursor");
    public string CursorDirectory { get; }
    public string IndicatorImageDirectory { get; }

    public SettingsService()
    {
        _settingsPath = Path.Combine(DataDirectory, "settings.json");
        CursorDirectory = Path.Combine(DataDirectory, "Cursors");
        IndicatorImageDirectory = Path.Combine(DataDirectory, "IndicatorImages");
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(CursorDirectory);
        Directory.CreateDirectory(IndicatorImageDirectory);
    }

    public AppSettings Load(AppLogger logger)
    {
        var settings = new AppSettings();
        if (File.Exists(_settingsPath))
        {
            try
            {
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath), JsonOptions) ?? new AppSettings();
            }
            catch (Exception ex)
            {
                logger.Error("Settings JSON was invalid; using default settings", ex);
                try { File.Copy(_settingsPath, _settingsPath + ".invalid", overwrite: true); } catch { /* Keep going with defaults. */ }
            }
        }
        settings.Normalize();
        return settings;
    }

    public void Save(AppSettings settings, AppLogger logger)
    {
        try
        {
            var temporaryPath = _settingsPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporaryPath, _settingsPath, true);
        }
        catch (Exception ex) { logger.Error("Failed to save settings", ex); }
    }

    public string ImportCursor(string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not ".cur" and not ".ani") throw new InvalidOperationException("Only .cur and .ani cursor files are supported.");
        return Import(sourcePath, CursorDirectory, extension, "The selected cursor file does not exist.");
    }

    public string ImportTypingIndicatorImage(string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not ".png" and not ".jpg" and not ".jpeg" and not ".bmp" and not ".gif")
            throw new InvalidOperationException("Use a PNG, JPG, BMP, or GIF image for the label.");
        return Import(sourcePath, IndicatorImageDirectory, extension, "The selected image file does not exist.");
    }

    /// <summary>Deletes a file previously copied into the app's data folder. Files elsewhere are never touched.</summary>
    public void DeleteImportedFile(string? path, AppLogger logger)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var full = Path.GetFullPath(path);
            var ownedDirectories = new[] { CursorDirectory, IndicatorImageDirectory };
            if (!ownedDirectories.Any(d => full.StartsWith(Path.GetFullPath(d) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) return;
            if (File.Exists(full)) File.Delete(full);
        }
        catch (Exception ex) { logger.Error("Could not delete an unused imported file", ex); }
    }

    private static string Import(string sourcePath, string directory, string extension, string missingMessage)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException(missingMessage, sourcePath);
        var safeName = Path.GetFileNameWithoutExtension(sourcePath);
        foreach (var invalid in Path.GetInvalidFileNameChars()) safeName = safeName.Replace(invalid, '_');
        var target = Path.Combine(directory, $"{safeName}-{Guid.NewGuid():N}{extension}");
        File.Copy(sourcePath, target);
        return target;
    }
}
