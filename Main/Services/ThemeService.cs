using System.Windows;
using System.Windows.Media;
using RainExplorer.Models;

namespace RainExplorer.Services;

/// <summary>
/// Applies a colour theme + view density at runtime. Themes mutate the shared
/// palette brushes defined in Themes/Dark.xaml — every control references those
/// same brush instances via StaticResource, so changing a colour repaints the
/// whole app instantly.
///
/// A theme drives EVERYTHING: the accent family + washes, selection colours,
/// the ambient orb, AND the base surface palette. For dark themes the near-black
/// base is tinted toward the accent so the whole window picks up the theme's
/// hue; a few themes ship a bolder hand-picked background gradient. The window's
/// base fill is exposed as the dynamic brush <c>AppBackgroundBrush</c>.
/// </summary>
public static class ThemeService
{
    /// <summary>
    /// A theme = an accent ramp + flags. <paramref name="BgTop"/>/<paramref name="BgBottom"/>
    /// optionally override the derived base gradient with a hand-picked one.
    /// </summary>
    private sealed record Theme(
        string Accent, string Bright, string Strong, string Deep,
        bool Light = false, string? BgTop = null, string? BgBottom = null);

    // Order here is the order shown in the Settings picker.
    public static readonly IReadOnlyList<string> Names = new[]
    {
        "Violet", "Ocean", "Cyan", "Emerald", "Ember", "Rose",
        "Midnight", "Sunset", "Mono", "Light",
    };

    private static readonly Dictionary<string, Theme> Themes = new()
    {
        ["Violet"]   = new("#A855F7", "#C084FC", "#9333EA", "#7E22CE"),
        ["Ocean"]    = new("#3B82F6", "#60A5FA", "#2563EB", "#1D4ED8"),
        ["Cyan"]     = new("#06B6D4", "#22D3EE", "#0891B2", "#0E7490"),
        ["Emerald"]  = new("#10B981", "#34D399", "#059669", "#047857"),
        ["Ember"]    = new("#F59E0B", "#FBBF24", "#D97706", "#B45309"),
        ["Rose"]     = new("#F43F5E", "#FB7185", "#E11D48", "#BE123C"),
        // Themes with bold custom background gradients.
        ["Midnight"] = new("#6366F1", "#818CF8", "#4F46E5", "#4338CA",
                           BgTop: "#070A16", BgBottom: "#0E1430"),
        ["Sunset"]   = new("#FB7185", "#FDA4AF", "#F97316", "#EA580C",
                           BgTop: "#160A10", BgBottom: "#2A1016"),
        ["Mono"]     = new("#A1A1AA", "#D4D4D8", "#71717A", "#52525B",
                           BgTop: "#0B0B0D", BgBottom: "#121214"),
        ["Light"]    = new("#7C3AED", "#9333EA", "#6D28D9", "#5B21B6", Light: true),
    };

    // ---- Dark (default) surfaces, shared by every non-light theme ----------
    private static readonly Dictionary<string, string> DarkSurfaces = new()
    {
        ["BgRaised"] = "#0D0D14", ["BgChrome"] = "#12121B",
        ["BgAlt"] = "#12121B", ["BgSide"] = "#12121B",
        ["Glass1"] = "#06FFFFFF", ["Glass2"] = "#0BFFFFFF", ["Glass3"] = "#14FFFFFF",
        ["Glass4"] = "#1AFFFFFF", ["BgHover"] = "#14FFFFFF",
        ["Line1"] = "#12FFFFFF", ["Line2"] = "#1FFFFFFF", ["Line3"] = "#2EFFFFFF",
        ["Border"] = "#1FFFFFFF", ["MenuBg"] = "#F2161622",
        ["Text"] = "#F5F4FB", ["TextDim"] = "#9B9AAC", ["TextMuted"] = "#9B9AAC",
        ["TextFaint"] = "#6C6B7E",
    };

    // ---- Light surfaces (dark ink on a soft near-white field) --------------
    private static readonly Dictionary<string, string> LightSurfaces = new()
    {
        ["BgRaised"] = "#FFFFFF", ["BgChrome"] = "#E7E4F0",
        ["BgAlt"] = "#E7E4F0", ["BgSide"] = "#E7E4F0",
        // More opaque "glass" so panels/rows actually read on a light field.
        ["Glass1"] = "#0A000000", ["Glass2"] = "#12000000", ["Glass3"] = "#1E000000",
        ["Glass4"] = "#2A000000", ["BgHover"] = "#16000000",
        ["Line1"] = "#1A000000", ["Line2"] = "#2C000000", ["Line3"] = "#40000000",
        ["Border"] = "#2C000000", ["MenuBg"] = "#FAF7F5FB",
        // Near-black ink + genuinely dark muted tones (the old ones vanished on light).
        ["Text"] = "#0B0A10", ["TextDim"] = "#2E2D38", ["TextMuted"] = "#393843",
        ["TextFaint"] = "#52515D",
    };

    public static void ApplyTheme(string theme)
    {
        if (!Themes.TryGetValue(theme, out var t))
            t = Themes["Violet"];   // legacy "Dark"/unknown -> the brand default

        var res = Application.Current.Resources;
        var surfaces = t.Light ? LightSurfaces : DarkSurfaces;

        foreach (var (key, hex) in surfaces)
            SetBrush(res, key, hex);

        // ---- Base background: derived tint, or a theme's custom gradient ----
        Color accent = Parse(t.Accent), deep = Parse(t.Deep);
        Color bgTop, bgBottom;
        if (t.Light)
        {
            // Slightly deeper than before so dark text has real contrast.
            bgTop = Parse("#ECEAF3");
            bgBottom = Mix(Parse("#DEDBEB"), accent, 0.06);
        }
        else if (t.BgTop is not null && t.BgBottom is not null)
        {
            bgTop = Parse(t.BgTop);
            bgBottom = Parse(t.BgBottom);
        }
        else
        {
            // Near-black, lightly tinted toward the accent so every theme reads.
            Color ink = Parse("#08080C");
            bgTop = Mix(ink, deep, 0.05);
            bgBottom = Mix(ink, deep, 0.16);
        }

        // Flat base brush (used where a solid is needed) = the top of the gradient.
        SetBrushColor(res, "Bg", bgTop);
        // Themed window background gradient (DynamicResource on the root border).
        res["AppBackgroundBrush"] = MakeBackground(bgTop, bgBottom);

        // ---- Accent family + derived washes ----
        SetBrush(res, "Accent", t.Accent);
        SetBrush(res, "AccentBright", t.Bright);
        SetBrush(res, "AccentStrong", t.Strong);
        SetBrushColor(res, "AccentWash", WithAlpha(accent, 0x1F));
        SetBrushColor(res, "AccentWash2", WithAlpha(accent, 0x33));
        SetBrushColor(res, "AccentLine", WithAlpha(accent, 0x73));
        SetBrushColor(res, "BgSel", WithAlpha(accent, 0x33));      // selection wash (hover)
        SetBrushColor(res, "BgSelSoft", WithAlpha(accent, 0x1F));  // selection wash (rest)

        // ---- Primary-button gradients ----
        SetGradient(res, "GradientAccent", t.Accent, t.Strong, t.Deep);
        SetGradient(res, "GradientAccentHover", t.Bright, t.Accent, t.Strong);

        // ---- Ambient orb tint (DynamicResource colours used by MainWindow) ----
        Color orb1 = Parse(t.Strong), orb2 = Parse(t.Deep);
        res["OrbColor1"] = WithAlpha(orb1, t.Light ? (byte)0x12 : (byte)0x4A);
        res["OrbColor2"] = WithAlpha(orb2, t.Light ? (byte)0x0C : (byte)0x30);
        res["OrbColor1Fade"] = WithAlpha(orb1, 0x00);
        res["OrbColor2Fade"] = WithAlpha(orb2, 0x00);
    }

    /// <summary>A small accent→deep gradient swatch for the theme picker.</summary>
    public static Brush Swatch(string name)
    {
        var t = Themes.TryGetValue(name, out var x) ? x : Themes["Violet"];
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        g.GradientStops.Add(new GradientStop(Parse(t.Accent), 0));
        g.GradientStops.Add(new GradientStop(Parse(t.Deep), 1));
        g.Freeze();
        return g;
    }

    public static void ApplyDensity(ViewDensity density) =>
        Application.Current.Resources["RowPadding"] =
            density == ViewDensity.Compact ? new Thickness(10, 1, 10, 1) : new Thickness(10, 5, 10, 5);

    /// <summary>Swap the app-wide UI font (paths keep the mono face).</summary>
    public static void ApplyFont(string family)
    {
        if (string.IsNullOrWhiteSpace(family)) family = "Segoe UI";
        try { Application.Current.Resources["AppFont"] = new FontFamily(family); }
        catch { Application.Current.Resources["AppFont"] = new FontFamily("Segoe UI"); }
    }

    // ---- helpers ------------------------------------------------------------
    private static LinearGradientBrush MakeBackground(Color top, Color bottom)
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0.35, 1) };
        g.GradientStops.Add(new GradientStop(top, 0));
        g.GradientStops.Add(new GradientStop(bottom, 1));
        g.Freeze();
        return g;
    }

    private static void SetBrush(ResourceDictionary res, string key, string hex)
    {
        if (res[key] is SolidColorBrush b && !b.IsFrozen) b.Color = Parse(hex);
    }

    private static void SetBrushColor(ResourceDictionary res, string key, Color c)
    {
        if (res[key] is SolidColorBrush b && !b.IsFrozen) b.Color = c;
    }

    private static void SetGradient(ResourceDictionary res, string key, string a, string b, string c)
    {
        if (res[key] is not LinearGradientBrush g || g.IsFrozen || g.GradientStops.Count < 3) return;
        g.GradientStops[0].Color = Parse(a);
        g.GradientStops[1].Color = Parse(b);
        g.GradientStops[2].Color = Parse(c);
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    /// <summary>Linear blend of two colours; t=0 → a, t=1 → b.</summary>
    private static Color Mix(Color a, Color b, double t)
    {
        byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromRgb(L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }
}
