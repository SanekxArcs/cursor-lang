using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using LanguageCursor.Services;
using LanguageCursor.Views;

namespace LanguageCursor;

public partial class App : System.Windows.Application
{
    private const string InstanceMutexName = @"Local\LanguageCursor.SingleInstance";
    private const string ActivateEventName = @"Local\LanguageCursor.Activate";
    private AppHost? _host;
    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateWait;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        var background = e.Args.Any(a => string.Equals(a, StartupService.BackgroundArgument, StringComparison.OrdinalIgnoreCase));
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out _ownsInstanceMutex);
        if (!_ownsInstanceMutex)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            if (!background) ActivateRunningInstance();
            Shutdown();
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        _host = new AppHost();
        var window = new MainWindow { DataContext = _host.ViewModel };
        _host.AttachWindow(window);
        _host.Start();

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _activateWait = ThreadPool.RegisterWaitForSingleObject(_activateEvent,
            (_, _) => Dispatcher.BeginInvoke(() => _host?.ViewModel.OpenWindow()), null, Timeout.Infinite, executeOnlyOnce: false);

        if (!background) window.Show();
    }

    private static void ActivateRunningInstance()
    {
        try
        {
            // Let the running instance bring its window to the front.
            AllowSetForegroundWindow(-1);
            using var activate = EventWaitHandle.OpenExisting(ActivateEventName);
            activate.Set();
        }
        catch { /* The other instance is shutting down; nothing to do. */ }
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        _activateWait?.Unregister(null);
        _activateEvent?.Dispose();
        _host?.Dispose();
        if (_ownsInstanceMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e) => _host?.Dispose();

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _host?.Logger.Error("Unhandled UI exception", e.Exception);
        _host?.RestoreCursor();
        System.Windows.MessageBox.Show("Language Cursor encountered an unexpected error. Your Windows cursors were restored.", "Language Cursor", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        _host?.Logger.Error("Unhandled exception", e.ExceptionObject as Exception);
        _host?.RestoreCursor();
    }

    public void RequestExit()
    {
        _host?.Dispose();
        Shutdown();
    }

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
}
