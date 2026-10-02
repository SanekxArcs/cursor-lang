using System.Text;

namespace LanguageCursor.Services;

public sealed class AppLogger : IDisposable
{
    private const int RetentionDays = 14;
    private readonly object _gate = new();
    private readonly string _filePath;

    public AppLogger()
    {
        Directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanguageCursor", "Logs");
        System.IO.Directory.CreateDirectory(Directory);
        _filePath = Path.Combine(Directory, $"language-cursor-{DateTime.Now:yyyyMMdd}.log");
        DeleteOldLogs();
    }

    public string Directory { get; }

    public void Info(string message) => Write("INFO", message, null);
    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (_gate)
            {
                File.AppendAllText(_filePath, $"{DateTimeOffset.Now:O} [{level}] {message}{(exception is null ? string.Empty : Environment.NewLine + exception)}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { /* Logging must never interrupt cursor restoration. */ }
    }

    private void DeleteOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "language-cursor-*.log"))
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
        }
        catch { /* Old logs are harmless. */ }
    }

    public void Dispose() { }
}
