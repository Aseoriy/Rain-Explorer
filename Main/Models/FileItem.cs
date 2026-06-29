using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using RainExplorer.Services;

namespace RainExplorer.Models;

/// <summary>
/// A single row in the file list: a file or a folder.
/// </summary>
public sealed class FileItem : INotifyPropertyChanged
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public DateTime Created { get; init; }

    public string Extension =>
        IsDirectory ? string.Empty : Path.GetExtension(Name).TrimStart('.').ToUpperInvariant();

    /// <summary>Name shown in the list — drops the extension when that setting is off.</summary>
    public string DisplayName
    {
        get
        {
            if (IsDirectory || SettingsStore.Instance.Settings.ShowFileExtensions) return Name;
            string stem = Path.GetFileNameWithoutExtension(Name);
            return string.IsNullOrEmpty(stem) ? Name : stem;
        }
    }

    /// <summary>Lucide icon key for this entry (see Themes/Icons.xaml). Folders are violet;
    /// media / code / archives carry a restrained tint, everything else stays muted.</summary>
    public string IconKey =>
        IsDirectory ? "folder" : Icons.KeyForExtension(Extension.ToLowerInvariant());

    /// <summary>Tint brush for the type icon (folders violet; file types per <see cref="Icons"/>).</summary>
    public Brush IconBrush =>
        IsDirectory ? Icons.FolderBrush : Icons.BrushForExtension(Extension.ToLowerInvariant());

    // ---- Real Windows associated icon (lazy) -------------------------------
    private ImageSource? _icon;
    private bool _iconRequested;
    /// <summary>The actual Explorer icon for this item (exe-embedded icon, registered
    /// file-type icon, folder icon). Loaded on first access off the UI thread; until it
    /// arrives this is null and the view falls back to the vector glyph (<see cref="IconKey"/>).</summary>
    public ImageSource? Icon
    {
        get
        {
            if (_icon is null && !_iconRequested)
            {
                _iconRequested = true;
                ShellIconService.LoadAsync(FullPath, IsDirectory, img =>
                {
                    _icon = img;
                    OnPropertyChanged(nameof(Icon));
                });
            }
            return _icon;
        }
    }

    public string TypeLabel =>
        IsDirectory ? "Folder" : (Extension.Length > 0 ? $"{Extension} file" : "File");

    // Folder size is computed lazily in the background when the setting is on.
    private long? _folderSize;
    public bool FolderSizeKnown => _folderSize.HasValue;
    /// <summary>Set a computed recursive size for a folder and refresh its size cell.</summary>
    public void SetFolderSize(long bytes)
    {
        _folderSize = bytes;
        OnPropertyChanged(nameof(SizeText));
    }

    public string SizeText =>
        IsDirectory ? (_folderSize is long fs ? FormatSize(fs) : string.Empty) : FormatSize(Size);

    public string ModifiedText => Modified == default ? string.Empty : Modified.ToString("yyyy-MM-dd  HH:mm");
    public string CreatedText => Created == default ? string.Empty : Created.ToString("yyyy-MM-dd  HH:mm");

    // ---- Inline-rename state (UI only) -------------------------------------
    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        set { if (_isEditing != value) { _isEditing = value; OnPropertyChanged(); } }
    }

    private string? _editName;
    /// <summary>Working buffer while inline-editing; defaults to the current name.</summary>
    public string EditName
    {
        get => _editName ?? Name;
        set { _editName = value; OnPropertyChanged(); }
    }

    // ---- Drag-and-drop highlight (UI only) ---------------------------------
    private bool _isDropTarget;
    /// <summary>True while a drag is hovering this folder row — the row paints an accent highlight.</summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set { if (_isDropTarget != value) { _isDropTarget = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return string.Empty;
        // Binary = 1024-based with IEC units (KiB/MiB/GiB); Decimal = 1000-based SI units (KB/MB/GB).
        bool binary = SettingsStore.Instance.Settings.SizeFormat == SizeFormat.Binary;
        double divisor = binary ? 1024 : 1000;
        string[] units = binary
            ? new[] { "B", "KiB", "MiB", "GiB", "TiB" }
            : new[] { "B", "KB", "MB", "GB", "TB" };
        double n = bytes;
        int i = 0;
        while (n >= divisor && i < units.Length - 1) { n /= divisor; i++; }
        return i == 0 ? $"{n:0} {units[i]}" : $"{n:0.0} {units[i]}";
    }
}

/// <summary>Maps file extensions to a Lucide icon key + a restrained tint brush.</summary>
internal static class Icons
{
    // ext -> (icon key, tint hex or null for muted). Mirrors the design system's EXT_MAP.
    private static readonly Dictionary<string, (string Key, string? Tint)> Map = new()
    {
        // images (cyan)
        ["png"] = ("image", "#7DD3FC"), ["jpg"] = ("image", "#7DD3FC"), ["jpeg"] = ("image", "#7DD3FC"),
        ["gif"] = ("image", "#7DD3FC"), ["webp"] = ("image", "#7DD3FC"), ["svg"] = ("image", "#7DD3FC"),
        ["bmp"] = ("image", "#7DD3FC"), ["ico"] = ("image", "#7DD3FC"),
        // video (fuchsia)
        ["mp4"] = ("film", "#F0ABFC"), ["mkv"] = ("film", "#F0ABFC"), ["mov"] = ("film", "#F0ABFC"),
        ["avi"] = ("film", "#F0ABFC"), ["webm"] = ("film", "#F0ABFC"),
        // audio (green)
        ["mp3"] = ("music", "#86EFAC"), ["wav"] = ("music", "#86EFAC"), ["flac"] = ("music", "#86EFAC"),
        ["ogg"] = ("music", "#86EFAC"), ["m4a"] = ("music", "#86EFAC"),
        // docs
        ["pdf"] = ("file-text", "#FCA5A5"), ["doc"] = ("file-text", "#93C5FD"), ["docx"] = ("file-text", "#93C5FD"),
        ["xls"] = ("file-text", "#86EFAC"), ["xlsx"] = ("file-text", "#86EFAC"),
        ["ppt"] = ("file-text", "#FDBA74"), ["pptx"] = ("file-text", "#FDBA74"),
        ["txt"] = ("file-text", null), ["md"] = ("file-text", null), ["rtf"] = ("file-text", null),
        // code
        ["js"] = ("file-code", "#FDE047"), ["ts"] = ("file-code", "#7DD3FC"), ["jsx"] = ("file-code", "#7DD3FC"),
        ["tsx"] = ("file-code", "#7DD3FC"), ["json"] = ("file-code", "#FCD34D"), ["html"] = ("globe", "#FDBA74"),
        ["css"] = ("file-code", "#A5B4FC"), ["py"] = ("file-code", "#7DD3FC"), ["rs"] = ("file-code", "#FCA5A5"),
        ["go"] = ("file-code", "#67E8F9"), ["c"] = ("file-code", null), ["cpp"] = ("file-code", null),
        ["cs"] = ("file-code", "#C4B5FD"), ["java"] = ("file-code", null), ["sh"] = ("file-code", null),
        // archives (amber)
        ["zip"] = ("archive", "#FCD34D"), ["rar"] = ("archive", "#FCD34D"), ["7z"] = ("archive", "#FCD34D"),
        ["tar"] = ("archive", "#FCD34D"), ["gz"] = ("archive", "#FCD34D"),
        // executables (violet)
        ["exe"] = ("zap", "#C084FC"), ["msi"] = ("zap", "#C084FC"), ["bat"] = ("zap", "#C084FC"), ["cmd"] = ("zap", "#C084FC"),
    };

    /// <summary>Violet folder tint (accent-bright).</summary>
    public static readonly Brush FolderBrush = Freeze("#C084FC");
    private static readonly Brush MutedBrush = Freeze("#9B9AAC");
    private static readonly Dictionary<string, Brush> BrushCache = new();

    public static string KeyForExtension(string ext) =>
        Map.TryGetValue(ext, out var m) ? m.Key : "file";

    public static Brush BrushForExtension(string ext)
    {
        if (!Map.TryGetValue(ext, out var m) || m.Tint is null) return MutedBrush;
        if (!BrushCache.TryGetValue(m.Tint, out var b)) BrushCache[m.Tint] = b = Freeze(m.Tint);
        return b;
    }

    private static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        b.Freeze();
        return b;
    }
}
