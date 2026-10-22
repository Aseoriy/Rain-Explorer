using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using RainExplorer.Models;

namespace RainExplorer.Services;

/// <summary>
/// Applies a colour theme + view density at runtime by replacing the shared palette
/// resources defined in Themes/Theme.axaml. Every consumer uses DynamicResource, so a
/// replaced brush repaints immediately.
///
/// A theme drives EVERYTHING: the accent family + washes, selection colours, the
/// ambient orb, AND the base surface palette. For dark themes the near-black base is
/// tinted toward the accent so the whole window picks up the theme's hue; a few themes
/// ship a bolder hand-picked background gradient. Same palette as the Windows build.
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

    // ---- Dark fallback surfaces (replaced from the selected theme background) ----
    private static readonly Dictionary<string, string> DarkSurfaces = new()
    {
        ["BgRaised"] = "#0D0D14", ["BgChrome"] = "#12121B",
        ["BgAlt"] = "#12121B", ["BgSide"] = "#12121B",
        ["Glass1"] = "#06FFFFFF", ["Glass2"] = "#0BFFFFFF", ["Glass3"] = "#14FFFFFF",
        ["Glass4"] = "#1AFFFFFF", ["BgHover"] = "#14FFFFFF",
        ["Line1"] = "#12FFFFFF", ["Line2"] = "#1FFFFFFF", ["Line3"] = "#2EFFFFFF",
        // Menus are intentionally opaque so a popup never reveals the file list below it.
        ["Border"] = "#1FFFFFFF", ["MenuBg"] = "#FF161622",
        ["Text"] = "#F5F4FB", ["TextDim"] = "#9B9AAC", ["TextMuted"] = "#9B9AAC",
        ["TextFaint"] = "#6C6B7E",
        ["ScrollThumbBrush"] = "#26FFFFFF", ["SettingsScrim"] = "#CC000000", ["SettingsHeader"] = "#26000000",
    };

    // ---- Light surfaces (dark ink on a soft near-white field) --------------
    private static readonly Dictionary<string, string> LightSurfaces = new()
    {
        ["BgRaised"] = "#FFFFFF", ["BgChrome"] = "#E7E4F0",
        ["BgAlt"] = "#E7E4F0", ["BgSide"] = "#E7E4F0",
        ["Glass1"] = "#0A000000", ["Glass2"] = "#12000000", ["Glass3"] = "#1E000000",
        ["Glass4"] = "#2A000000", ["BgHover"] = "#16000000",
        ["Line1"] = "#1A000000", ["Line2"] = "#2C000000", ["Line3"] = "#40000000",
        ["Border"] = "#2C000000", ["MenuBg"] = "#FFF7F5FB",
        ["Text"] = "#0B0A10", ["TextDim"] = "#2E2D38", ["TextMuted"] = "#393843",
        ["TextFaint"] = "#52515D",
        ["ScrollThumbBrush"] = "#40000000", ["SettingsScrim"] = "#CCFFFFFF", ["SettingsHeader"] = "#26FFFFFF",
    };

    private static IResourceDictionary Res => Application.Current!.Resources;

    public static void ApplyTheme(string theme)
    {
        if (!Themes.TryGetValue(theme, out var t))
            t = Themes["Violet"];   // legacy "Dark"/unknown -> the brand default

        var surfaces = t.Light ? LightSurfaces : DarkSurfaces;
        foreach (var (key, hex) in surfaces)
            SetBrush(key, Color.Parse(hex));

        // Fluent controls (menus, scrollbars, text boxes) follow the light/dark variant.
        if (Application.Current is { } app)
            app.RequestedThemeVariant = t.Light ? ThemeVariant.Light : ThemeVariant.Dark;

        // ---- Base background: derived tint, or a theme's custom gradient ----
        Color accent = Color.Parse(t.Accent), deep = Color.Parse(t.Deep);
        Color bgTop, bgBottom;
        if (t.Light)
        {
            bgTop = Color.Parse("#ECEAF3");
            bgBottom = Mix(Color.Parse("#DEDBEB"), accent, 0.06);
        }
        else if (t.BgTop is not null && t.BgBottom is not null)
        {
            bgTop = Color.Parse(t.BgTop);
            bgBottom = Color.Parse(t.BgBottom);
        }
        else
        {
            // Near-black, lightly tinted toward the accent so every theme reads.
            Color ink = Color.Parse("#08080C");
            bgTop = Mix(ink, deep, 0.05);
            bgBottom = Mix(ink, deep, 0.16);
        }

        if (!t.Light)
            ApplyDarkSurfacePalette(bgTop, bgBottom);

        SetBrush("Bg", bgTop);
        Res["AccentColor"] = accent;
        Res["AppBackgroundBrush"] = MakeBackground(bgTop, bgBottom);

        // ---- Accent family + derived washes ----
        SetBrush("Accent", Color.Parse(t.Accent));
        SetBrush("AccentBright", Color.Parse(t.Bright));
        SetBrush("AccentStrong", Color.Parse(t.Strong));
        SetBrush("AccentWash", WithAlpha(accent, 0x1F));
        SetBrush("AccentWash2", WithAlpha(accent, 0x33));
        SetBrush("AccentLine", WithAlpha(accent, 0x73));
        SetBrush("BgSel", WithAlpha(accent, 0x33));      // selection wash (hover)
        SetBrush("BgSelSoft", WithAlpha(accent, 0x1F));  // selection wash (rest)

        // Fluent's own accent keys, so focus rings, sliders and toggles match.
        Res["SystemAccentColor"] = accent;
        Res["SystemAccentColorLight1"] = Color.Parse(t.Bright);
        Res["SystemAccentColorDark1"] = Color.Parse(t.Strong);

        // ---- Glows (WPF DropShadowEffect equivalents) ----
        Res["PrimaryGlow"] = new BoxShadows(new BoxShadow { Blur = 14, Color = WithAlpha(accent, 0x73) });
        Res["PrimaryGlowHover"] = new BoxShadows(new BoxShadow { Blur = 24, Color = WithAlpha(accent, 0xB3) });
        Res["TabGlow"] = new BoxShadows(new BoxShadow { Blur = 8, Color = WithAlpha(accent, 0xB3) });

        // ---- Primary-button gradients ----
        Res["GradientAccent"] = MakeGradient(Color.Parse(t.Accent), Color.Parse(t.Strong), Color.Parse(t.Deep));
        Res["GradientAccentHover"] = MakeGradient(Color.Parse(t.Bright), Color.Parse(t.Accent), Color.Parse(t.Strong));

        // ---- Ambient orb tint ----
        Color orb1 = Color.Parse(t.Strong), orb2 = Color.Parse(t.Deep);
        Res["OrbBrush1"] = MakeOrb(WithAlpha(orb1, t.Light ? (byte)0x12 : (byte)0x4A), new RelativePoint(0.8, -0.05, RelativeUnit.Relative), 0.62);
        Res["OrbBrush2"] = MakeOrb(WithAlpha(orb2, t.Light ? (byte)0x0C : (byte)0x30), new RelativePoint(-0.05, 1.05, RelativeUnit.Relative), 0.55);
    }

    /// <summary>A small accent→deep gradient swatch for the theme picker.</summary>
    public static IBrush Swatch(string name)
    {
        var t = Themes.TryGetValue(name, out var x) ? x : Themes["Violet"];
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse(t.Accent), 0),
                new GradientStop(Color.Parse(t.Deep), 1),
            },
        };
    }

    public static void ApplyDensity(ViewDensity density) =>
        Res["RowPadding"] = density == ViewDensity.Compact ? new Thickness(10, 1, 10, 1) : new Thickness(10, 5, 10, 5);

    /// <summary>Swap the app-wide UI font (paths keep the mono face).</summary>
    public static void ApplyFont(string family)
    {
        if (string.IsNullOrWhiteSpace(family)) family = AppSettings.DefaultFontFamily;
        Res["AppFont"] = new FontFamily($"{family}, Noto Sans, DejaVu Sans, sans-serif");
    }

    // ---- helpers ------------------------------------------------------------
    private static IBrush MakeBackground(Color top, Color bottom) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0.35, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(top, 0), new GradientStop(bottom, 1) },
    };

    private static IBrush MakeGradient(Color a, Color b, Color c) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(a, 0), new GradientStop(b, 0.55), new GradientStop(c, 1) },
    };

    private static IBrush MakeOrb(Color color, RelativePoint center, double radius) => new RadialGradientBrush
    {
        Center = center,
        GradientOrigin = center,
        RadiusX = new RelativeScalar(radius, RelativeUnit.Relative),
        RadiusY = new RelativeScalar(radius, RelativeUnit.Relative),
        GradientStops = { new GradientStop(color, 0), new GradientStop(WithAlpha(color, 0), 1) },
    };

    private static void SetBrush(string key, Color c) => Res[key] = new SolidColorBrush(c);

    private static void ApplyDarkSurfacePalette(Color bgTop, Color bgBottom)
    {
        // Keep secondary surfaces opaque, but lift them only a little from the
        // selected background so custom gradients remain visible as a theme.
        Color neutral = Colors.White;
        Color midpoint = Mix(bgTop, bgBottom, 0.5);
        SetBrush("BgRaised", Mix(bgTop, neutral, 0.06));
        SetBrush("BgChrome", Mix(bgBottom, neutral, 0.04));
        SetBrush("BgAlt", Mix(midpoint, neutral, 0.05));
        SetBrush("BgSide", Mix(bgBottom, neutral, 0.06));
        SetBrush("MenuBg", Mix(bgBottom, neutral, 0.08));
    }

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    /// <summary>Linear blend of two colours; t=0 → a, t=1 → b.</summary>
    private static Color Mix(Color a, Color b, double t)
    {
        byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromRgb(L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }
}
