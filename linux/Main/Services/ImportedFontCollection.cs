using System.Collections;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;

namespace RainExplorer.Services;

/// <summary>
/// Fonts imported through Settings, loaded straight from their files so they work right away.
/// Avalonia (through fontconfig) only reads the system font list once at startup, so a newly installed
/// font would otherwise fall back to the default until Rain restarts. Registered as
/// <c>fonts:RainImported</c>; <see cref="ThemeService.ApplyFont"/> looks here first.
/// </summary>
public sealed class ImportedFontCollection : FontCollectionBase
{
    public static readonly Uri CollectionKey = new("fonts:RainImported");
    public static ImportedFontCollection Instance { get; } = new();

    private IFontManagerImpl? _fontManager;
    private Dictionary<string, List<IGlyphTypeface>> _families = new(StringComparer.OrdinalIgnoreCase);
    private List<FontFamily> _familyList = [];
    private FontManager? _registeredWith;

    public override Uri Key => CollectionKey;
    public override int Count => _familyList.Count;
    public override FontFamily this[int index] => _familyList[index];
    public override IEnumerator<FontFamily> GetEnumerator() => _familyList.GetEnumerator();

    /// <summary>Family names of the imported fonts.</summary>
    public IReadOnlyList<string> FamilyNames => _familyList.Select(f => f.Name).ToList();

    public bool Contains(string family) => _families.ContainsKey(family);

    /// <summary>Add the collection to Avalonia's font manager (at startup; again if the manager is replaced).</summary>
    public static void Register()
    {
        try
        {
            FontManager current = FontManager.Current;
            if (ReferenceEquals(Instance._registeredWith, current)) return;
            current.AddFontCollection(Instance);
            Instance._registeredWith = current;
        }
        catch { /* no font manager (some tests): imported fonts then work after a restart */ }
    }

    public override void Initialize(IFontManagerImpl fontManager)
    {
        _fontManager = fontManager;
        Reload();
    }

    /// <summary>Re-read the import folder (after an import).</summary>
    public void Reload()
    {
        if (_fontManager is null) return;
        var families = new Dictionary<string, List<IGlyphTypeface>>(StringComparer.OrdinalIgnoreCase);
        string folder = FontImportService.ImportFolder;
        if (Directory.Exists(folder))
        {
            foreach (string file in Directory.EnumerateFiles(folder))
            {
                if (!FontImportService.FontExtensions.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
                try
                {
                    using var stream = File.OpenRead(file);
                    if (!_fontManager.TryCreateGlyphTypeface(stream, FontSimulations.None, out IGlyphTypeface? typeface)
                        || string.IsNullOrWhiteSpace(typeface.FamilyName))
                        continue;
                    if (!families.TryGetValue(typeface.FamilyName, out var list))
                        families[typeface.FamilyName] = list = [];
                    list.Add(typeface);
                }
                catch { /* unreadable file: skip */ }
            }
        }
        _families = families;
        _familyList = families.Keys.Order(StringComparer.CurrentCultureIgnoreCase)
            .Select(name => new FontFamily(CollectionKey, name)).ToList();
        _glyphTypefaceCache.Clear();
    }

    public override bool TryGetGlyphTypeface(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
        out IGlyphTypeface glyphTypeface)
    {
        glyphTypeface = null!;
        if (!_families.TryGetValue(familyName, out var faces) || faces.Count == 0) return false;
        // Closest face: matching style first, then nearest weight, then nearest stretch.
        glyphTypeface = faces
            .OrderBy(f => f.Style == style ? 0 : 1)
            .ThenBy(f => Math.Abs((int)f.Weight - (int)weight))
            .ThenBy(f => Math.Abs((int)f.Stretch - (int)stretch))
            .First();
        return true;
    }
}
