using System.Windows.Threading;
using LanguageCursor.Models;

namespace LanguageCursor.Services;

public sealed class InputLanguageMonitor : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly InputLanguageService _service;
    private IntPtr _previousLayout;
    private bool _disposed;

    public InputLanguageMonitor(InputLanguageService service)
    {
        _service = service;
        _timer.Tick += OnTick;
    }

    public event EventHandler<InputLanguageInfo>? LanguageChanged;
    public InputLanguageInfo? Current { get; private set; }

    public void Start()
    {
        if (!_timer.IsEnabled) _timer.Start();
        CheckNow();
    }

    public void Stop() => _timer.Stop();
    public void CheckNow() => OnTick(this, EventArgs.Empty);

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        try
        {
            var layout = _service.GetForegroundLayout();
            // Zero means the foreground thread has no keyboard layout (e.g. during a window switch); keep the last one.
            if (layout == IntPtr.Zero || layout == _previousLayout) return;
            _previousLayout = layout;
            Current = _service.GetInfo(layout);
            LanguageChanged?.Invoke(this, Current);
        }
        catch { /* A transient foreground-window change should not terminate monitoring. */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
