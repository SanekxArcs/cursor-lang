using System.Globalization;
using System.Text;
using Microsoft.Win32;
using LanguageCursor.Models;
using LanguageCursor.Native;

namespace LanguageCursor.Services;

public sealed class InputLanguageService
{
    private readonly Dictionary<IntPtr, InputLanguageInfo> _cache = [];

    public IReadOnlyList<InputLanguageInfo> GetInstalledLanguages(AppLogger logger)
    {
        try
        {
            var count = NativeMethods.GetKeyboardLayoutList(0, null);
            if (count <= 0) return [];
            var layouts = new IntPtr[count];
            var returned = NativeMethods.GetKeyboardLayoutList(layouts.Length, layouts);
            return layouts.Take(returned).Select(GetInfo).OrderBy(x => x.DisplayName).ThenBy(x => x.LayoutName).ToList();
        }
        catch (Exception ex)
        {
            logger.Error("Could not enumerate installed keyboard layouts", ex);
            return [];
        }
    }

    /// <summary>Keyboard layout of the window the user is typing into, or zero when it cannot be determined.</summary>
    public IntPtr GetForegroundLayout()
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero) return NativeMethods.GetKeyboardLayout(0);
        var threadId = NativeMethods.GetWindowThreadProcessId(window, out _);
        // Store (UWP) apps are hosted in a frame window whose thread does not own the keyboard input.
        if (NativeMethods.GetClassName(window) == "ApplicationFrameWindow")
        {
            var core = NativeMethods.FindWindowEx(window, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
            if (core != IntPtr.Zero) threadId = NativeMethods.GetWindowThreadProcessId(core, out _);
        }
        return NativeMethods.GetKeyboardLayout(threadId);
    }

    public InputLanguageInfo GetInfo(IntPtr hkl)
    {
        if (_cache.TryGetValue(hkl, out var cached)) return cached;
        var info = CreateInfo(hkl);
        _cache[hkl] = info;
        return info;
    }

    private static InputLanguageInfo CreateInfo(IntPtr hkl)
    {
        var raw = unchecked((ulong)hkl.ToInt64());
        var langId = (int)(raw & 0xffff);
        CultureInfo culture;
        try { culture = CultureInfo.GetCultureInfo(langId); }
        catch (CultureNotFoundException) { culture = CultureInfo.InvariantCulture; }
        var cultureName = string.IsNullOrWhiteSpace(culture.Name) ? $"0x{langId:X4}" : culture.Name;
        var display = string.IsNullOrWhiteSpace(culture.Name) ? "Unknown input language" : culture.EnglishName;
        return new InputLanguageInfo
        {
            Id = raw.ToString("X16"),
            CultureName = cultureName,
            DisplayName = display,
            KeyboardLayoutId = (raw & 0xffffffff).ToString("X8"),
            LayoutName = GetLayoutName(raw),
            DefaultLabel = GetDefaultLabel(culture)
        };
    }

    public static string GetDefaultLabel(CultureInfo culture)
    {
        // UA is clearer for the Ukrainian layout than the ISO language code "UK".
        if (culture.Name.StartsWith("uk", StringComparison.OrdinalIgnoreCase)) return "UA";
        var code = culture.TwoLetterISOLanguageName;
        if (string.IsNullOrWhiteSpace(culture.Name) || code.Length is < 2 or > 3 || code == "iv") return "??";
        return code.ToUpperInvariant();
    }

    private static string? GetLayoutName(ulong hkl)
    {
        try
        {
            using var layouts = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Keyboard Layouts");
            if (layouts is null) return null;
            var device = (int)((hkl >> 16) & 0xffff);
            string? klid = null;
            if ((device & 0xf000) == 0xf000)
            {
                // Substitute layouts (Dvorak, Enhanced…) store an index that maps to a "Layout Id" value.
                var layoutId = (device & 0x0fff).ToString("X4");
                foreach (var name in layouts.GetSubKeyNames())
                {
                    using var candidate = layouts.OpenSubKey(name);
                    if (string.Equals(candidate?.GetValue("Layout Id") as string, layoutId, StringComparison.OrdinalIgnoreCase)) { klid = name; break; }
                }
            }
            else if ((device & 0xf000) == 0xe000) klid = (hkl & 0xffffffff).ToString("X8"); // IME
            else klid = device.ToString("X8");
            if (klid is null) return null;

            using var key = layouts.OpenSubKey(klid);
            if (key is null) return null;
            if (key.GetValue("Layout Display Name") is string indirect && indirect.StartsWith('@'))
            {
                var buffer = new StringBuilder(260);
                if (NativeMethods.SHLoadIndirectString(indirect, buffer, buffer.Capacity, IntPtr.Zero) == 0 && buffer.Length > 0)
                    return buffer.ToString();
            }
            return key.GetValue("Layout Text") as string;
        }
        catch
        {
            return null;
        }
    }
}
