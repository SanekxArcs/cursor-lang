using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using LanguageCursor.Native;
using LanguageCursor.Rendering;

namespace LanguageCursor.Services;

/// <summary>Request to replace the text-select cursor with one carrying the language label.</summary>
public sealed record TextCursorRequest(BadgeStyle Badge, double Scale);

public sealed class CursorService
{
    private const int GeneratorVersion = 2;
    private readonly AppLogger _logger;
    private readonly string _generatedCursorDirectory;
    private readonly string _appliedMarkerPath;
    private string? _appliedArrowPath;
    private string? _appliedTextCursorKey;

    public CursorService(AppLogger logger)
    {
        _logger = logger;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanguageCursor");
        _generatedCursorDirectory = Path.Combine(root, "GeneratedCursors");
        _appliedMarkerPath = Path.Combine(root, "cursors-applied.flag");
        Directory.CreateDirectory(_generatedCursorDirectory);
        PurgeGeneratedCursors();
    }

    public bool HasAppliedCursors => _appliedArrowPath is not null || _appliedTextCursorKey is not null;

    public static bool CanLoad(string cursorPath)
    {
        var handle = NativeMethods.LoadCursorFromFile(cursorPath);
        if (handle == IntPtr.Zero) return false;
        NativeMethods.DestroyCursor(handle);
        return true;
    }

    /// <summary>
    /// Brings system cursors to the requested state. Null means "Windows default" for that cursor. The Windows
    /// scheme is reloaded only when a previously applied cursor has to be removed, which avoids flicker.
    /// </summary>
    public void Apply(string? arrowPath, TextCursorRequest? textCursor)
    {
        var textCursorKey = textCursor is null ? null : CreateKey(textCursor);
        if ((arrowPath is null && _appliedArrowPath is not null) || (textCursorKey is null && _appliedTextCursorKey is not null))
            RestoreWindowsCursor();

        if (arrowPath is not null && !string.Equals(_appliedArrowPath, arrowPath, StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(arrowPath)) throw new FileNotFoundException("The configured cursor file no longer exists.", arrowPath);
            MarkApplied();
            SetSystemCursorFromFile(arrowPath, NativeMethods.OCR_NORMAL);
            _appliedArrowPath = arrowPath;
            _logger.Info($"Applied pointer: {Path.GetFileName(arrowPath)}");
        }

        if (textCursor is not null && !string.Equals(_appliedTextCursorKey, textCursorKey, StringComparison.Ordinal))
        {
            var generatedPath = Path.Combine(_generatedCursorDirectory, $"text-{textCursorKey}.cur");
            if (!File.Exists(generatedPath)) TextCursorWriter.Write(generatedPath, textCursor, GetCursorPixelScale());
            MarkApplied();
            SetSystemCursorFromFile(generatedPath, NativeMethods.OCR_IBEAM);
            _appliedTextCursorKey = textCursorKey;
            _logger.Info($"Applied text cursor label: {textCursor.Badge.Text}");
        }
    }

    /// <summary>Restores cursors left changed by a previous session that did not exit cleanly.</summary>
    public void RestoreIfPreviousSessionCrashed()
    {
        if (!File.Exists(_appliedMarkerPath)) return;
        _logger.Info("Previous session did not restore its cursors; restoring now");
        RestoreWindowsCursor();
    }

    public void RestoreWindowsCursor()
    {
        if (NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETCURSORS, 0, IntPtr.Zero, 0))
            _logger.Info("Restored Windows cursor scheme");
        else
            _logger.Error($"Failed to restore Windows cursor scheme (Win32 error {Marshal.GetLastWin32Error()})");
        _appliedArrowPath = null;
        _appliedTextCursorKey = null;
        try { File.Delete(_appliedMarkerPath); } catch { /* Best effort. */ }
    }

    private void MarkApplied()
    {
        try { if (!File.Exists(_appliedMarkerPath)) File.WriteAllText(_appliedMarkerPath, DateTimeOffset.Now.ToString("O")); }
        catch { /* Best effort. */ }
    }

    private static string CreateKey(TextCursorRequest request)
    {
        var badge = request.Badge;
        var raw = $"{GeneratorVersion}|{badge.Text}|{badge.Background}|{badge.ImagePath}|{badge.Roundness:0.00}|{request.Scale:0.00}|{GetCursorPixelScale()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16];
    }

    /// <summary>Integer scale so the text cursor matches Windows' cursor size at the current display scaling.</summary>
    private static int GetCursorPixelScale()
    {
        var baseSize = 32;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Cursors");
            if (key?.GetValue("CursorBaseSize") is int size && size >= 32) baseSize = size;
        }
        catch { /* Use the default size. */ }
        var dpiScale = NativeMethods.GetDpiForSystem() / 96.0;
        return Math.Clamp((int)Math.Round(dpiScale * baseSize / 32.0), 1, 4);
    }

    private void PurgeGeneratedCursors()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_generatedCursorDirectory, "*.cur")) File.Delete(file);
        }
        catch (Exception ex) { _logger.Error("Could not clean generated cursors", ex); }
    }

    private static void SetSystemCursorFromFile(string cursorPath, uint cursorRole)
    {
        var loadedCursor = NativeMethods.LoadCursorFromFile(cursorPath);
        if (loadedCursor == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not load this cursor file.");
        try
        {
            // SetSystemCursor consumes/destroys the passed copy. The original LoadCursorFromFile handle remains ours.
            var transferableCopy = NativeMethods.CopyIcon(loadedCursor);
            if (transferableCopy == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not copy this cursor.");
            if (!NativeMethods.SetSystemCursor(transferableCopy, cursorRole))
            {
                NativeMethods.DestroyCursor(transferableCopy);
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not set the cursor.");
            }
        }
        finally
        {
            NativeMethods.DestroyCursor(loadedCursor);
        }
    }
}

/// <summary>Writes a 32-bit .cur with an I-beam followed by the language label.</summary>
internal static class TextCursorWriter
{
    private const uint Black = 0xFF000000;
    private const uint White = 0xFFFFFFFF;

    public static void Write(string path, TextCursorRequest request, int k)
    {
        var beamHeight = 32 * k;
        var labelStyle = request.Badge with { FontSize = 8 * Math.Clamp(request.Scale, 1, 3) * k, Shadow = false };
        var label = BadgeRenderer.Render(labelStyle, 1);
        var labelLeft = 14 * k;
        var width = Math.Min(256, labelLeft + label.PixelWidth - label.ContentX);
        var height = Math.Min(256, Math.Max(beamHeight, label.PixelHeight));
        var pixels = new uint[width * height];
        var beamTop = (height - beamHeight) / 2;
        DrawIBeam(pixels, width, height, k, beamTop);

        // Copy the label, converting premultiplied alpha to the straight alpha used by cursor files.
        var labelTop = (height - label.PixelHeight) / 2;
        for (var y = 0; y < label.PixelHeight; y++)
        {
            for (var x = 0; x < label.PixelWidth; x++)
            {
                var offset = (y * label.PixelWidth + x) * 4;
                uint alpha = label.Pixels[offset + 3];
                if (alpha == 0) continue;
                uint blue = Unpremultiply(label.Pixels[offset], alpha);
                uint green = Unpremultiply(label.Pixels[offset + 1], alpha);
                uint red = Unpremultiply(label.Pixels[offset + 2], alpha);
                SetPixel(pixels, width, height, labelLeft - label.ContentX + x, labelTop + y, (alpha << 24) | (red << 16) | (green << 8) | blue);
            }
        }

        var hotspotX = (int)(6.5 * k - 0.5);
        var hotspotY = beamTop + 16 * k;
        WriteCursorFile(path, pixels, width, height, hotspotX, hotspotY);
    }

    private static uint Unpremultiply(byte value, uint alpha) => Math.Min(255u, (uint)(value * 255 / alpha));

    private static void DrawIBeam(uint[] pixels, int width, int height, int k, int top)
    {
        void Fill(int x, int y, int w, int h, uint color)
        {
            for (var row = y * k; row < (y + h) * k; row++)
                for (var column = x * k; column < (x + w) * k; column++)
                    SetPixel(pixels, width, height, column, top + row, color);
        }
        Fill(2, 3, 10, 2, Black);
        Fill(2, 27, 10, 2, Black);
        Fill(5, 3, 3, 26, Black);
        Fill(6, 5, 1, 22, White);
        Fill(3, 4, 8, 1, White);
        Fill(3, 27, 8, 1, White);
    }

    private static void SetPixel(uint[] pixels, int width, int height, int x, int y, uint color)
    {
        if (x >= 0 && x < width && y >= 0 && y < height) pixels[y * width + x] = color;
    }

    private static void WriteCursorFile(string path, uint[] pixels, int width, int height, int hotspotX, int hotspotY)
    {
        var temporaryPath = path + ".tmp";
        using (var stream = File.Create(temporaryPath))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
        {
            const int bitmapHeaderSize = 40;
            var xorSize = width * height * 4;
            var andRowSize = ((width + 31) / 32) * 4;
            var andSize = andRowSize * height;
            var imageSize = bitmapHeaderSize + xorSize + andSize;

            writer.Write((ushort)0);                       // ICONDIR.reserved
            writer.Write((ushort)2);                       // cursor resource
            writer.Write((ushort)1);
            writer.Write((byte)(width >= 256 ? 0 : width));
            writer.Write((byte)(height >= 256 ? 0 : height));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)hotspotX);
            writer.Write((ushort)hotspotY);
            writer.Write(imageSize);
            writer.Write(22);                              // first image offset

            writer.Write(bitmapHeaderSize);
            writer.Write(width);
            writer.Write(height * 2);                      // XOR + AND masks
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(0);
            writer.Write(xorSize);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);

            // DIB rows are bottom-up. Transparent pixels have alpha 0; the AND mask stays zero.
            for (var y = height - 1; y >= 0; y--)
                for (var x = 0; x < width; x++) writer.Write(pixels[y * width + x]);
            writer.Write(new byte[andSize]);
        }
        File.Move(temporaryPath, path, overwrite: true);
    }
}
