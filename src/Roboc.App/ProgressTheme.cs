using System.Runtime.InteropServices;

namespace Roboc.App;

/// <summary>
/// Win11 视觉风格的绘制层：字体、配色、圆角、进度条与速度曲线。
///
/// <b>为什么自绘。</b>STATIC 控件只能显示文本，而原生进度窗的信息密度来自它的
/// 图形部分——总进度条和速度曲线。这两样都必须自己画。
///
/// <b>为什么按主题取色而不是写死。</b>浅色与深色主题下的可读性要求不同；写死一套
/// 颜色会在另一种主题下变成低对比度的灰字。系统主题与强调色都能从注册表读到，
/// 代价是启动时几次读取。
///
/// <b>为什么速度曲线存采样而不是直接画瞬时值。</b>瞬时速率抖动剧烈，逐帧直画会是
/// 一条噪声线。环形缓冲保留最近若干秒的采样，画出来才是原生那种趋势曲线。
/// </summary>
internal static class ProgressTheme
{
    // ---------------------------------------------------------------- 调色板

    internal readonly record struct Palette(
        uint Window,
        uint Text,
        uint SecondaryText,
        uint Track,
        uint Accent,
        uint GraphFill,
        uint GraphLine,
        uint GraphGrid,
        uint Border);

    private static Palette? _cached;
    private static bool _dark;

    /// <summary>按当前系统主题取一套颜色。</summary>
    internal static Palette Current()
    {
        if (_cached is { } cached)
        {
            return cached;
        }

        _dark = ReadDword(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1) == 0;

        uint accent = ReadDword(@"Software\Microsoft\Windows\DWM", "AccentColor", 0);

        // AccentColor is stored as ABGR; without one, Win11's default blue is used.
        uint rgb = accent == 0 ? 0x00C06700u : accent & 0x00FFFFFFu;

        Palette palette = _dark
            ? new Palette(
                Window: 0x00202020,
                Text: 0x00FFFFFF,
                SecondaryText: 0x00B0B0B0,
                Track: 0x00383838,
                Accent: Lighten(rgb, 0.20),
                GraphFill: Lighten(rgb, -0.45),
                GraphLine: Lighten(rgb, 0.10),
                GraphGrid: Lighten(rgb, 0.30),
                Border: 0x00404040)
            : new Palette(
                Window: 0x00F3F3F3,
                Text: 0x001A1A1A,
                SecondaryText: 0x005D5D5D,
                Track: 0x00E1E1E1,
                Accent: rgb,
                GraphFill: Lighten(rgb, 0.72),
                GraphLine: rgb,
                GraphGrid: Lighten(rgb, 0.52),
                Border: 0x00D6D6D6);

        _cached = palette;
        return palette;
    }

    internal static bool IsDark => _dark;

    /// <summary>
    /// Paints the window's title bar a step darker than the card.
    ///
    /// <b>Why this is needed.</b> Windows draws a title bar with the Mica material, which
    /// samples the desktop wallpaper and tints the result. That is fine for a window that
    /// leaves its background to the compositor and wrong for one that fills its client
    /// area with an opaque colour: the two halves of the same window end up different
    /// colours, and the difference follows the wallpaper rather than the theme. Removing
    /// the Mica backdrop earlier only fixed the client area -- the title bar is drawn by
    /// DWM and kept it. Confirmed by changing the wallpaper and watching the bar change
    /// with it.
    ///
    /// <b>Why a step darker rather than identical.</b> Matching exactly removes the
    /// mismatch and takes the band with it: the window then reads as one flat block with
    /// a close button on it, with nothing to say where the draggable area begins. A step
    /// of ten or twelve levels of grey keeps the division without reintroducing a colour
    /// from outside the palette.
    ///
    /// <b>Why the palette can be passed straight through.</b> This attribute takes a
    /// COLORREF, 0x00BBGGRR, and the palette is already built that way -- see Lighten,
    /// which reassembles r, g and b in that order. No channel swap is needed, and adding
    /// one would turn the title bar blue where the card is grey.
    ///
    /// <b>Why failure is ignored.</b> The attribute exists from Windows 11 build 22000;
    /// on anything older DwmSetWindowAttribute returns non-zero and the title bar keeps
    /// the system colour. That is a cosmetic degradation, not a fault worth reporting.
    /// </summary>
    internal static void ApplyCaptionColour(nint hwnd, Palette palette)
    {
        try
        {
            int caption = unchecked((int)Darken(palette.Window, _dark ? DarkStep : LightStep));
            DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    // Tuned by eye at 175% on this machine. Dark takes the smaller number because ten
    // levels off 0x20 is a larger proportional step than twelve off 0xF3 -- the two
    // values are not meant to match each other, only to look like the same amount.
    private const int DarkStep = 10;
    private const int LightStep = 12;

    private const int DwmwaCaptionColor = 35;

    /// <summary>Subtracts from each channel, clamping at black.</summary>
    private static uint Darken(uint rgb, int amount)
    {
        uint r = (uint)Math.Max(0, (int)(rgb & 0xFF) - amount);
        uint g = (uint)Math.Max(0, (int)((rgb >> 8) & 0xFF) - amount);
        uint b = (uint)Math.Max(0, (int)((rgb >> 16) & 0xFF) - amount);

        return (b << 16) | (g << 8) | r;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    /// <summary>把颜色向白或向黑推，用于生成同色系的浅色填充。</summary>
    private static uint Lighten(uint rgb, double amount)
    {
        double r = rgb & 0xFF;
        double g = (rgb >> 8) & 0xFF;
        double b = (rgb >> 16) & 0xFF;

        double target = amount >= 0 ? 255 : 0;
        double factor = Math.Abs(amount);

        r += (target - r) * factor;
        g += (target - g) * factor;
        b += (target - b) * factor;

        return (uint)(((int)b << 16) | ((int)g << 8) | (int)r);
    }

    private static uint ReadDword(string subKey, string name, uint fallback)
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key =
                Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subKey);

            return key?.GetValue(name) is int value ? (uint)value : fallback;
        }
        catch (System.Security.SecurityException) { return fallback; }
        catch (UnauthorizedAccessException) { return fallback; }
    }

    /// <summary>
    /// The interface font, in the order Windows itself falls back.
    ///
    /// Segoe UI Variable is the Windows 11 face; Segoe UI is the one every supported
    /// build has. Asking for both by name costs nothing -- CreateFontW substitutes
    /// silently when a face is missing -- and using the real interface font is most
    /// of what makes a window look native rather than merely styled.
    /// </summary>
    internal const string Face = "Segoe UI Variable Text";

    internal const string FallbackFace = "Segoe UI";
}
