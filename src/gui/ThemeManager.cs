using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Ostrasort.Gui;

/// <summary>
/// Light/dark theming. WPF's Fluent <c>ThemeMode</c> supplies the chrome
/// (buttons, tabs, the grid, scrollbars, dialogs); this class supplies the
/// app's own severity/accent brushes for both palettes, detects the OS theme
/// for "system" mode, and pushes the brushes into a window's resources so
/// <c>DynamicResource</c> references track the theme. Code that sets a brush
/// directly reads the static properties (which return the current palette);
/// after a live theme switch the caller re-renders so those re-apply.
/// </summary>
public static class ThemeManager
{
    public static string Mode { get; private set; } = "system";   // "system" | "light" | "dark"
    public static bool Dark { get; private set; }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    // severity / accent - light (the app's original values)
    private static readonly Brush LNormal = Frozen(0x00, 0x00, 0x00), LDim = Frozen(0x80, 0x80, 0x80),
        LGood = Frozen(0x00, 0x80, 0x00), LWarn = Frozen(0xB8, 0x6E, 0x00), LBad = Frozen(0xB2, 0x22, 0x22);
    // severity / accent - dark (lightened for contrast on a dark background)
    private static readonly Brush DNormal = Frozen(0xE6, 0xE6, 0xE6), DDim = Frozen(0x9A, 0xA0, 0xA6),
        DGood = Frozen(0x5D, 0xBB, 0x7D), DWarn = Frozen(0xE0, 0xA6, 0x4B), DBad = Frozen(0xF0, 0x73, 0x6A);

    public static Brush Normal => Dark ? DNormal : LNormal;
    public static Brush Dim    => Dark ? DDim    : LDim;
    public static Brush Good   => Dark ? DGood   : LGood;
    public static Brush Warn   => Dark ? DWarn   : LWarn;
    public static Brush Bad    => Dark ? DBad    : LBad;

    // FFU banner backgrounds / borders
    private static readonly Brush LInfoBg = Frozen(0xFF, 0xF8, 0xE7), LInfoBd = Frozen(0xD9, 0xA4, 0x00),
        LAlarmBg = Frozen(0xFD, 0xEC, 0xEA), LAlarmBd = Frozen(0xB2, 0x22, 0x22);
    private static readonly Brush DInfoBg = Frozen(0x33, 0x2B, 0x12), DInfoBd = Frozen(0x8A, 0x6D, 0x00),
        DAlarmBg = Frozen(0x3A, 0x1E, 0x1C), DAlarmBd = Frozen(0xC0, 0x39, 0x2B);
    public static Brush BannerInfoBg      => Dark ? DInfoBg  : LInfoBg;
    public static Brush BannerInfoBorder  => Dark ? DInfoBd  : LInfoBd;
    public static Brush BannerAlarmBg     => Dark ? DAlarmBg : LAlarmBg;
    public static Brush BannerAlarmBorder => Dark ? DAlarmBd : LAlarmBd;

    // chrome accents referenced by DynamicResource in XAML
    private static readonly Brush LGrid = Frozen(0xEE, 0xEE, 0xEE), DGrid = Frozen(0x3A, 0x3A, 0x3A);
    private static readonly Brush LBorder = Frozen(0xDD, 0xDD, 0xDD), DBorder = Frozen(0x45, 0x45, 0x45);
    public static Brush GridLine    => Dark ? DGrid   : LGrid;
    public static Brush PanelBorder => Dark ? DBorder : LBorder;

    /// <summary>Set the mode, resolve dark vs light (reading the OS for "system"), and apply to a window.</summary>
    public static void Apply(Window w, string mode)
    {
        Mode = mode is "light" or "dark" ? mode : "system";
        Dark = Mode switch { "dark" => true, "light" => false, _ => OsIsDark() };
        ApplyTo(w);
    }

    /// <summary>Apply the current theme to a window: Fluent chrome + the custom resource brushes.</summary>
    public static void ApplyTo(Window w)
    {
        // ThemeMode is the supported Fluent switch; guard so the headless
        // --smoke-gui path (no Application) can still construct windows.
#pragma warning disable WPF0001
        try { w.ThemeMode = Dark ? ThemeMode.Dark : ThemeMode.Light; } catch { }
#pragma warning restore WPF0001

        var r = w.Resources;
        r["SevNormalBrush"] = Normal;
        r["SevDimBrush"] = Dim;
        r["SevGoodBrush"] = Good;
        r["SevWarnBrush"] = Warn;
        r["SevBadBrush"] = Bad;
        r["SecondaryTextBrush"] = Dim;
        r["GridLineBrush"] = GridLine;
        r["PanelBorderBrush"] = PanelBorder;
    }

    /// <summary>The Windows "apps" theme: AppsUseLightTheme = 0 means dark. Defaults to light if unreadable.</summary>
    private static bool OsIsDark()
    {
        try
        {
            return Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1) is int i && i == 0;
        }
        catch { return false; }
    }
}
