using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using LanguageCursor.Native;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace LanguageCursor.Services;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly TrayMenuRenderer _renderer = new();
    private readonly Forms.ToolStripMenuItem _currentItem;
    private readonly Forms.ToolStripMenuItem _pauseItem;
    private readonly Drawing.Icon _activeIcon;
    private readonly Drawing.Icon _pausedIcon;

    public TrayService(Action open, Action toggleEnabled, Action restore, Action exit)
    {
        var font = new Drawing.Font("Segoe UI", 9f);
        _menu = new Forms.ContextMenuStrip
        {
            Renderer = _renderer,
            ShowImageMargin = false,
            ShowCheckMargin = false,
            Font = font,
            Padding = new Forms.Padding(0, 4, 0, 4),
            DropShadowEnabled = true
        };
        var header = MenuItem("Language Cursor", null);
        header.Enabled = false;
        header.Font = new Drawing.Font(font, Drawing.FontStyle.Bold);
        _currentItem = MenuItem("Current: —", null);
        _currentItem.Enabled = false;
        _pauseItem = MenuItem("Pause", toggleEnabled);
        _menu.Items.AddRange([
            header, _currentItem, new Forms.ToolStripSeparator(),
            MenuItem("Open settings", open),
            _pauseItem,
            MenuItem("Restore Windows cursors", restore),
            new Forms.ToolStripSeparator(),
            MenuItem("Exit", exit)
        ]);
        _menu.Opening += (_, _) => RoundCorners(_menu.Handle);

        _activeIcon = LoadAppIcon();
        _pausedIcon = CreateDimmedIcon(_activeIcon);
        _icon = new Forms.NotifyIcon
        {
            Icon = _activeIcon,
            Text = "Language Cursor",
            ContextMenuStrip = _menu,
            Visible = false
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) open(); };
    }

    public void Show() => _icon.Visible = true;

    public void SetDarkTheme(bool dark)
    {
        _renderer.Dark = dark;
        _menu.BackColor = _renderer.Background;
        _menu.Invalidate();
    }

    public void Update(string currentLanguage, bool enabled)
    {
        _currentItem.Text = $"Current: {currentLanguage}";
        _pauseItem.Text = enabled ? "Pause" : "Resume";
        _icon.Icon = enabled ? _activeIcon : _pausedIcon;
        var tooltip = enabled ? $"Language Cursor — {currentLanguage}" : "Language Cursor — paused";
        _icon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
    }

    private static Forms.ToolStripMenuItem MenuItem(string text, Action? onClick)
    {
        var item = new Forms.ToolStripMenuItem(text) { Padding = new Forms.Padding(12, 5, 24, 5) };
        if (onClick is not null) item.Click += (_, _) => onClick();
        return item;
    }

    private static void RoundCorners(IntPtr handle)
    {
        var preference = NativeMethods.DWMWCP_ROUND;
        try { NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int)); }
        catch { /* Rounded corners are Windows 11 only. */ }
    }

    private static Drawing.Icon LoadAppIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
            }
        }
        catch { /* Fall back below. */ }
        return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
    }

    private static Drawing.Icon CreateDimmedIcon(Drawing.Icon source)
    {
        using var original = source.ToBitmap();
        using var dimmed = new Drawing.Bitmap(original.Width, original.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Drawing.Graphics.FromImage(dimmed))
        {
            var matrix = new ColorMatrix(
            [
                [0.30f, 0.30f, 0.30f, 0, 0],
                [0.59f, 0.59f, 0.59f, 0, 0],
                [0.11f, 0.11f, 0.11f, 0, 0],
                [0, 0, 0, 0.75f, 0],
                [0, 0, 0, 0, 1]
            ]);
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(matrix);
            graphics.DrawImage(original, new Drawing.Rectangle(0, 0, original.Width, original.Height), 0, 0, original.Width, original.Height, Drawing.GraphicsUnit.Pixel, attributes);
        }
        var handle = dimmed.GetHicon();
        try { return (Drawing.Icon)Drawing.Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _activeIcon.Dispose();
        _pausedIcon.Dispose();
    }

    /// <summary>Windows 11-style flat menu that follows the app theme.</summary>
    private sealed class TrayMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        public TrayMenuRenderer() => RoundedEdges = false;

        public bool Dark { get; set; }
        public Drawing.Color Background => Dark ? Drawing.Color.FromArgb(44, 44, 44) : Drawing.Color.FromArgb(249, 249, 249);
        private Drawing.Color Border => Dark ? Drawing.Color.FromArgb(70, 70, 70) : Drawing.Color.FromArgb(222, 222, 222);
        private Drawing.Color Hover => Dark ? Drawing.Color.FromArgb(61, 61, 61) : Drawing.Color.FromArgb(234, 234, 234);
        private Drawing.Color Text => Dark ? Drawing.Color.FromArgb(255, 255, 255) : Drawing.Color.FromArgb(26, 26, 26);
        private Drawing.Color DisabledText => Dark ? Drawing.Color.FromArgb(165, 165, 165) : Drawing.Color.FromArgb(110, 110, 110);
        private Drawing.Color Separator => Dark ? Drawing.Color.FromArgb(68, 68, 68) : Drawing.Color.FromArgb(226, 226, 226);

        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e) => e.Graphics.Clear(Background);

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            using var pen = new Drawing.Pen(Border);
            e.Graphics.DrawRectangle(pen, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
        }

        protected override void OnRenderImageMargin(Forms.ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var bounds = new Drawing.Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
            using var path = RoundedRectangle(bounds, 4);
            using var brush = new Drawing.SolidBrush(Hover);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(brush, path);
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Drawing.Pen(Separator);
            var y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : DisabledText;
            base.OnRenderItemText(e);
        }

        private static GraphicsPath RoundedRectangle(Drawing.Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var diameter = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
