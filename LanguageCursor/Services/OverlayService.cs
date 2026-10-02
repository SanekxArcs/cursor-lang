using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using LanguageCursor.Models;
using LanguageCursor.Native;
using LanguageCursor.Rendering;
using Forms = System.Windows.Forms;

namespace LanguageCursor.Services;

public sealed record OverlayOptions(
    BadgePosition Position,
    double OffsetX,
    double OffsetY,
    double Opacity,
    BadgeVisibility Visibility,
    double AfterSwitchSeconds,
    bool HideWhenPointerHidden,
    bool HideInFullScreen)
{
    public static OverlayOptions From(OverlaySettings s) =>
        new(s.Position, s.OffsetX, s.OffsetY, s.Opacity, s.Visibility, s.AfterSwitchSeconds, s.HideWhenPointerHidden, s.HideInFullScreen);
}

/// <summary>
/// Shows the language label next to the pointer in a click-through layered window. It runs on its own thread, so
/// the label keeps up with the pointer even while the settings window is busy, and it never changes Windows cursors.
/// </summary>
public sealed class OverlayService : IDisposable
{
    private const byte FadeInStep = 48;
    private const byte FadeOutStep = 32;

    private readonly AppLogger _logger;
    private readonly Thread _thread;
    private Dispatcher? _dispatcher;
    private volatile bool _disposed;

    // Everything below is touched only on the overlay thread.
    private OverlayWindow? _window;
    private DispatcherTimer? _pollTimer;
    private DispatcherTimer? _fadeTimer;
    private NativeMethods.LowLevelMouseProc? _hookProc;
    private Action? _processMove;
    private IntPtr _hook;
    private bool _active;
    private BadgeStyle? _badge;
    private OverlayOptions _options = OverlayOptions.From(new OverlaySettings());
    private RenderedBadge? _rendered;
    private double _renderedScale;
    private bool _bitmapDirty;
    private NativeMethods.POINT _pointer;
    private bool _hasPointer;
    private bool _pointerShowing = true;
    private bool _pointerOverText;
    private IntPtr _monitor;
    private double _monitorScale = 1;
    private NativeMethods.RECT _monitorBounds;
    private bool _fullScreen;
    private int _tick;
    private DateTime _transientUntil = DateTime.MinValue;
    private byte _alpha;
    private byte _targetAlpha;
    private bool _shown;
    private int _windowX = int.MinValue, _windowY = int.MinValue;
    private bool _movePending;
    private int _pendingX, _pendingY;

    public OverlayService(AppLogger logger)
    {
        _logger = logger;
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "Pointer label" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        ready.Dispose();
    }

    /// <summary>Updates what is shown. <paramref name="active"/> false hides the label and removes the mouse hook.</summary>
    public void Update(bool active, BadgeStyle? badge, OverlayOptions options) => Post(() => ApplyState(active, badge, options));

    /// <summary>Starts the "briefly after switching" window.</summary>
    public void NotifyLanguageSwitched() => Post(() =>
    {
        _transientUntil = DateTime.UtcNow.AddSeconds(_options.AfterSwitchSeconds);
        Refresh();
    });

    private void Post(Action action)
    {
        if (_disposed || _dispatcher is null) return;
        _dispatcher.BeginInvoke(action);
    }

    private void Run(ManualResetEventSlim ready)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _dispatcher.UnhandledException += (_, e) =>
        {
            _logger.Error("Pointer label error", e.Exception);
            e.Handled = true;
        };
        try
        {
            _window = new OverlayWindow();
            _pollTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = TimeSpan.FromMilliseconds(50) };
            _pollTimer.Tick += (_, _) => OnPoll();
            _fadeTimer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher) { Interval = TimeSpan.FromMilliseconds(15) };
            _fadeTimer.Tick += (_, _) => OnFade();
            _hookProc = HookCallback;
            _processMove = ProcessMove;
        }
        catch (Exception ex)
        {
            _logger.Error("Could not create the pointer label window", ex);
            _window = null;
        }
        finally
        {
            ready.Set();
        }
        Dispatcher.Run();
    }

    private void ApplyState(bool active, BadgeStyle? badge, OverlayOptions options)
    {
        if (_window is null) return;
        _options = options;
        if (badge != _badge)
        {
            _badge = badge;
            _rendered = null;
        }
        _active = active && badge is not null;
        if (_active)
        {
            InstallHook();
            _pollTimer!.Start();
            SyncPointer(includePosition: true);
            _fullScreen = QueryFullScreen();
        }
        else
        {
            RemoveHook();
            _pollTimer!.Stop();
        }
        Refresh();
    }

    private void InstallHook()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _hookProc!, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            // Without the hook the label still follows the pointer through polling, just less smoothly.
            _logger.Error($"Could not install the pointer-move hook (Win32 error {Marshal.GetLastWin32Error()}); using polling");
            _pollTimer!.Interval = TimeSpan.FromMilliseconds(16);
        }
        else
        {
            _pollTimer!.Interval = TimeSpan.FromMilliseconds(50);
        }
    }

    private void RemoveHook()
    {
        if (_hook == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Keep this minimal: a slow low-level hook delays every mouse event in the system.
        if (nCode >= 0 && wParam == NativeMethods.WM_MOUSEMOVE)
        {
            _pendingX = Marshal.ReadInt32(lParam);
            _pendingY = Marshal.ReadInt32(lParam, 4);
            if (!_movePending)
            {
                _movePending = true;
                _dispatcher!.BeginInvoke(DispatcherPriority.Input, _processMove!);
            }
        }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void ProcessMove()
    {
        _movePending = false;
        if (!_active) return;
        _pointer = new NativeMethods.POINT(_pendingX, _pendingY);
        _hasPointer = true;
        SyncPointer(includePosition: false);
        Refresh();
    }

    private void OnPoll()
    {
        if (!_active) return;
        if (++_tick % 10 == 0)
        {
            _fullScreen = QueryFullScreen();
            _monitor = IntPtr.Zero; // re-read monitor bounds/DPI in case display settings changed
            if (_shown) _window!.BringToTop();
        }
        SyncPointer(includePosition: true);
        Refresh();
    }

    private void SyncPointer(bool includePosition)
    {
        var info = new NativeMethods.CURSORINFO { cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>() };
        if (!NativeMethods.GetCursorInfo(ref info)) return;
        _pointerShowing = (info.flags & NativeMethods.CURSOR_SHOWING) != 0;
        _pointerOverText = info.hCursor != IntPtr.Zero && info.hCursor == NativeMethods.LoadCursor(IntPtr.Zero, NativeMethods.IDC_IBEAM);
        if (includePosition)
        {
            _pointer = info.ptScreenPos;
            _hasPointer = true;
        }
    }

    private static bool QueryFullScreen()
    {
        // QUNS_BUSY (2), QUNS_RUNNING_D3D_FULL_SCREEN (3) and QUNS_PRESENTATION_MODE (4) all mean a full-screen app or presentation.
        return NativeMethods.SHQueryUserNotificationState(out var state) == 0 && state is 2 or 3 or 4;
    }

    private bool ShouldShow()
    {
        if (!_active || !_hasPointer || _badge is null) return false;
        if (_options.HideInFullScreen && _fullScreen) return false;
        if (_options.HideWhenPointerHidden && !_pointerShowing) return false;
        return _options.Visibility switch
        {
            BadgeVisibility.OverText => _pointerOverText,
            BadgeVisibility.AfterSwitch => DateTime.UtcNow < _transientUntil,
            _ => true
        };
    }

    private void Refresh()
    {
        if (_window is null) return;
        var visible = ShouldShow();
        if (visible) Reposition();
        FadeTo(visible ? (byte)Math.Round(Math.Clamp(_options.Opacity, 0.2, 1) * 255) : (byte)0);
    }

    private void Reposition()
    {
        UpdateMonitor();
        var scale = _monitorScale;
        if (_rendered is null || Math.Abs(_renderedScale - scale) > 0.001)
        {
            _rendered = BadgeRenderer.Render(_badge!, scale);
            _renderedScale = scale;
            _bitmapDirty = true;
        }

        var badge = _rendered;
        var position = _options.Position;
        double width = badge.ContentWidth, height = badge.ContentHeight;
        double offsetX = _options.OffsetX * scale, offsetY = _options.OffsetY * scale;
        var placed = BadgeLayout.Place(position, _pointer.X, _pointer.Y, width, height, offsetX, offsetY);
        double x = placed.X, y = placed.Y;
        var bounds = _monitorBounds;

        // Flip to the other side of the pointer near screen edges, then keep the label fully on screen.
        if (x + width > bounds.Right && BadgeLayout.IsRight(position)) x = _pointer.X - offsetX - width;
        else if (x < bounds.Left && BadgeLayout.IsLeft(position)) x = _pointer.X + offsetX;
        if (y + height > bounds.Bottom && BadgeLayout.IsBottom(position)) y = _pointer.Y - offsetY - height;
        else if (y < bounds.Top && BadgeLayout.IsTop(position)) y = _pointer.Y + offsetY;
        x = Math.Clamp(x, bounds.Left, Math.Max(bounds.Left, bounds.Right - width));
        y = Math.Clamp(y, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - height));

        var windowX = (int)Math.Round(x) - badge.ContentX;
        var windowY = (int)Math.Round(y) - badge.ContentY;
        if (_bitmapDirty)
        {
            _window!.SetBitmap(badge, windowX, windowY, _alpha);
            _bitmapDirty = false;
        }
        else if (windowX != _windowX || windowY != _windowY)
        {
            _window!.Move(windowX, windowY);
        }
        _windowX = windowX;
        _windowY = windowY;
    }

    private void UpdateMonitor()
    {
        var monitor = NativeMethods.MonitorFromPoint(_pointer, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == _monitor && monitor != IntPtr.Zero) return;
        _monitor = monitor;
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (NativeMethods.GetMonitorInfo(monitor, ref info)) _monitorBounds = info.rcMonitor;
        _monitorScale = NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : 1;
    }

    private void FadeTo(byte target)
    {
        _targetAlpha = target;
        if (target > 0 && !_shown)
        {
            _window!.Show();
            _shown = true;
        }
        if (_alpha != target && !_fadeTimer!.IsEnabled) _fadeTimer.Start();
    }

    private void OnFade()
    {
        if (_alpha < _targetAlpha) _alpha = (byte)Math.Min(_targetAlpha, _alpha + FadeInStep);
        else if (_alpha > _targetAlpha) _alpha = (byte)Math.Max(_targetAlpha, _alpha - FadeOutStep);
        _window!.SetAlpha(_alpha);
        if (_alpha != _targetAlpha) return;
        _fadeTimer!.Stop();
        if (_alpha == 0 && _shown)
        {
            _window.Hide();
            _shown = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var dispatcher = _dispatcher;
        if (dispatcher is null) return;
        try
        {
            dispatcher.Invoke(() =>
            {
                RemoveHook();
                _pollTimer?.Stop();
                _fadeTimer?.Stop();
                _window?.Dispose();
                _window = null;
            }, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            _logger.Error("Pointer label did not shut down cleanly", ex);
        }
        dispatcher.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    /// <summary>Topmost, click-through, never-activated popup painted with UpdateLayeredWindow.</summary>
    private sealed class OverlayWindow : Forms.NativeWindow, IDisposable
    {
        public OverlayWindow()
        {
            CreateHandle(new Forms.CreateParams
            {
                Caption = "Language Cursor label",
                Style = NativeMethods.WS_POPUP,
                ExStyle = NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW
                          | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST,
                Width = 1,
                Height = 1
            });
        }

        protected override void WndProc(ref Forms.Message m)
        {
            switch (m.Msg)
            {
                case NativeMethods.WM_NCHITTEST:
                    m.Result = NativeMethods.HTTRANSPARENT;
                    return;
                case NativeMethods.WM_MOUSEACTIVATE:
                    m.Result = NativeMethods.MA_NOACTIVATE;
                    return;
                default:
                    base.WndProc(ref m);
                    return;
            }
        }

        public void SetBitmap(RenderedBadge badge, int x, int y, byte alpha)
        {
            var screen = NativeMethods.GetDC(IntPtr.Zero);
            var memory = NativeMethods.CreateCompatibleDC(screen);
            var header = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = badge.PixelWidth,
                biHeight = -badge.PixelHeight, // top-down rows, matching RenderTargetBitmap
                biPlanes = 1,
                biBitCount = 32
            };
            var bitmap = NativeMethods.CreateDIBSection(screen, ref header, 0, out var bits, IntPtr.Zero, 0);
            try
            {
                if (bitmap == IntPtr.Zero) return;
                Marshal.Copy(badge.Pixels, 0, bits, badge.Pixels.Length);
                var previous = NativeMethods.SelectObject(memory, bitmap);
                var position = new NativeMethods.POINT(x, y);
                var size = new NativeMethods.SIZE(badge.PixelWidth, badge.PixelHeight);
                var source = new NativeMethods.POINT(0, 0);
                var blend = Blend(alpha);
                NativeMethods.UpdateLayeredWindow(Handle, screen, ref position, ref size, memory, ref source, 0, ref blend, NativeMethods.ULW_ALPHA);
                NativeMethods.SelectObject(memory, previous);
            }
            finally
            {
                if (bitmap != IntPtr.Zero) NativeMethods.DeleteObject(bitmap);
                NativeMethods.DeleteDC(memory);
                NativeMethods.ReleaseDC(IntPtr.Zero, screen);
            }
        }

        public void SetAlpha(byte alpha)
        {
            var blend = Blend(alpha);
            NativeMethods.UpdateLayeredWindowAlpha(Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref blend, NativeMethods.ULW_ALPHA);
        }

        public void Move(int x, int y) =>
            NativeMethods.SetWindowPos(Handle, IntPtr.Zero, x, y, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);

        public void BringToTop() =>
            NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);

        public void Show()
        {
            NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);
            BringToTop();
        }

        public void Hide() => NativeMethods.ShowWindow(Handle, NativeMethods.SW_HIDE);

        public void Dispose() => DestroyHandle();

        private static NativeMethods.BLENDFUNCTION Blend(byte alpha) => new()
        {
            BlendOp = NativeMethods.AC_SRC_OVER,
            SourceConstantAlpha = alpha,
            AlphaFormat = NativeMethods.AC_SRC_ALPHA
        };
    }
}
