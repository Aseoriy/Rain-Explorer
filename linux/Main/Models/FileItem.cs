using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using RainExplorer.Services;

namespace RainExplorer.Models;

/// <summary>
/// A single row in the file list: a file or a folder.
/// </summary>
public sealed class FileItem : INotifyPropertyChanged
{
    private string _name = "";
    public required string Name
    {
        get => _name;
        init => _name = value;
    }

    private string _fullPath = "";
    public required string FullPath
    {
        get => _fullPath;
        init => _fullPath = value;
    }
    public bool IsDirectory { get; init; }
    /// <summary>A symbolic link (to a file or a folder). Links are never followed by file operations.</summary>
    public bool IsSymlink { get; init; }
    public bool IsHidden { get; init; }
    public bool HasPhysicalPath => !string.IsNullOrWhiteSpace(FullPath);
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

    /// <summary>Lucide icon key for this entry (see Themes/Icons.axaml). Folders are violet;
    /// media / code / archives carry a restrained tint, everything else stays muted.</summary>
    public string IconKey =>
        IsDirectory ? "folder" : Icons.KeyForExtension(Extension.ToLowerInvariant());

    /// <summary>Tint brush for the type icon (folders violet; file types per <see cref="Icons"/>).</summary>
    public IBrush IconBrush =>
        IsDirectory ? Icons.FolderBrush : Icons.BrushForExtension(Extension.ToLowerInvariant());

    /// <summary>Dimmed rows for dotfiles, like other Linux file managers.</summary>
    public double RowOpacity => IsHidden ? 0.6 : 1.0;

    // ---- Real desktop icon (lazy) -------------------------------------------
    private Bitmap? _icon;
    private bool _iconRequested;
    /// <summary>The icon-theme icon for this item (folder, MIME type, special folder). Loaded on
    /// first access off the UI thread; until it arrives this is null and the view falls back to
    /// the vector glyph (<see cref="IconKey"/>) — the same contract as the Windows build.</summary>
    public Bitmap? Icon
    {
        get
        {
            if (_icon is null && !_iconRequested && HasPhysicalPath)
            {
                _iconRequested = true;
                IconThemeService.Load(this, 48, img =>
                {
                    _icon = img;
                    OnPropertyChanged(nameof(Icon));
                    OnPropertyChanged(nameof(HasIcon));
                });
            }
            return _icon;
        }
    }

    public bool HasIcon => _icon is not null;

    private Bitmap? _largeIcon;
    private bool _largeIconRequested;
    /// <summary>A larger rendering of <see cref="Icon"/> for the Tiles / Grid layouts.</summary>
    public Bitmap? LargeIcon
    {
        get
        {
            if (_largeIcon is null && !_largeIconRequested && HasPhysicalPath)
            {
                _largeIconRequested = true;
                IconThemeService.Load(this, 128, img =>
                {
                    _largeIcon = img;
                    OnPropertyChanged(nameof(LargeIcon));
                    OnPropertyChanged(nameof(HasLargeIcon));
                });
            }
            return _largeIcon;
        }
    }

    public bool HasLargeIcon => _largeIcon is not null;

    // ---- Grid thumbnail (lazy, photos only) --------------------------------
    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "gif", "webp", "bmp",
    };

    public bool IsPhoto => !IsDirectory && PhotoExtensions.Contains(Extension);

    private Bitmap? _gridThumbnail;
    private bool _gridThumbnailRequested;
    /// <summary>A larger photo preview requested only when grid view binds to it.</summary>
    public Bitmap? GridThumbnail
    {
        get
        {
            if (IsPhoto && _gridThumbnail is null && !_gridThumbnailRequested)
            {
                _gridThumbnailRequested = true;
                ThumbnailService.LoadAsync(FullPath, 160, img =>
                {
                    _gridThumbnail = img;
                    OnPropertyChanged(nameof(GridThumbnail));
                    OnPropertyChanged(nameof(HasGridThumbnail));
                });
            }
            return _gridThumbnail;
        }
    }

    public bool HasGridThumbnail => _gridThumbnail is not null;

    public string TypeLabel =>
        IsSymlink ? (IsDirectory ? "Folder link" : "Link")
        : IsDirectory ? "Folder"
        : Extension.Length > 0 ? $"{Extension} file" : "File";

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
        set
        {
            if (_isEditing == value) return;
            _isEditing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNotEditing));
        }
    }

    public bool IsNotEditing => !_isEditing;

    private string? _editName;
    /// <summary>Working buffer while inline-editing; defaults to the current name.</summary>
    public string EditName
    {
        get => _editName ?? Name;
        set { _editName = value; OnPropertyChanged(); }
    }

    /// <summary>Apply a successful same-folder rename without re-reading the directory.</summary>
    public void ApplyRename(string newPath)
    {
        _fullPath = newPath;
        _name = Path.GetFileName(newPath);
        _editName = _name;
        _gridThumbnail = null;
        _gridThumbnailRequested = false;
        _icon = null;
        _iconRequested = false;
        _largeIcon = null;
        _largeIconRequested = false;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(FullPath));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Extension));
        OnPropertyChanged(nameof(IconKey));
        OnPropertyChanged(nameof(IconBrush));
        OnPropertyChanged(nameof(GridThumbnail));
        OnPropertyChanged(nameof(HasGridThumbnail));
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(HasIcon));
        OnPropertyChanged(nameof(LargeIcon));
        OnPropertyChanged(nameof(HasLargeIcon));
        OnPropertyChanged(nameof(TypeLabel));
        OnPropertyChanged(nameof(EditName));
    }

    // ---- Drag-and-drop highlight (UI only) ---------------------------------
    private bool _isDropTarget;
    /// <summary>True while a drag is hovering this folder row — the row paints an accent highlight.</summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set { if (_isDropTarget != value) { _isDropTarget = value; OnPropertyChanged(); } }
    }

    // ---- Cut highlight (UI only) --------------------------------------------
    private bool _isCut;
    /// <summary>True while this item sits on the clipboard as a cut — the row is drawn faded.</summary>
    public bool IsCut
    {
        get => _isCut;
        set { if (_isCut != value) { _isCut = value; OnPropertyChanged(); } }
    }

    /// <summary>Used by the list's type-to-select search.</summary>
    public override string ToString() => Name;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public static string FormatSize(long bytes)
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
        ["png"] = ("image", "#7DD3FC"),
        ["jpg"] = ("image", "#7DD3FC"),
        ["jpeg"] = ("image", "#7DD3FC"),
        ["gif"] = ("image", "#7DD3FC"),
        ["webp"] = ("image", "#7DD3FC"),
        ["svg"] = ("image", "#7DD3FC"),
        ["bmp"] = ("image", "#7DD3FC"),
        ["ico"] = ("image", "#7DD3FC"),
        // video (fuchsia)
        ["mp4"] = ("film", "#F0ABFC"),
        ["mkv"] = ("film", "#F0ABFC"),
        ["mov"] = ("film", "#F0ABFC"),
        ["avi"] = ("film", "#F0ABFC"),
        ["webm"] = ("film", "#F0ABFC"),
        // audio (green)
        ["mp3"] = ("music", "#86EFAC"),
        ["wav"] = ("music", "#86EFAC"),
        ["flac"] = ("music", "#86EFAC"),
        ["ogg"] = ("music", "#86EFAC"),
        ["opus"] = ("music", "#86EFAC"),
        ["m4a"] = ("music", "#86EFAC"),
        // docs
        ["pdf"] = ("file-text", "#FCA5A5"),
        ["doc"] = ("file-text", "#93C5FD"),
        ["docx"] = ("file-text", "#93C5FD"),
        ["odt"] = ("file-text", "#93C5FD"),
        ["xls"] = ("file-text", "#86EFAC"),
        ["xlsx"] = ("file-text", "#86EFAC"),
        ["ods"] = ("file-text", "#86EFAC"),
        ["ppt"] = ("file-text", "#FDBA74"),
        ["pptx"] = ("file-text", "#FDBA74"),
        ["odp"] = ("file-text", "#FDBA74"),
        ["txt"] = ("file-text", null),
        ["md"] = ("file-text", null),
        ["rtf"] = ("file-text", null),
        ["log"] = ("file-text", null),
        // code
        ["js"] = ("file-code", "#FDE047"),
        ["ts"] = ("file-code", "#7DD3FC"),
        ["jsx"] = ("file-code", "#7DD3FC"),
        ["tsx"] = ("file-code", "#7DD3FC"),
        ["json"] = ("file-code", "#FCD34D"),
        ["html"] = ("globe", "#FDBA74"),
        ["css"] = ("file-code", "#A5B4FC"),
        ["py"] = ("file-code", "#7DD3FC"),
        ["rs"] = ("file-code", "#FCA5A5"),
        ["go"] = ("file-code", "#67E8F9"),
        ["c"] = ("file-code", null),
        ["h"] = ("file-code", null),
        ["cpp"] = ("file-code", null),
        ["cs"] = ("file-code", "#C4B5FD"),
        ["java"] = ("file-code", null),
        ["sh"] = ("terminal", null),
        ["toml"] = ("file-code", null),
        ["yaml"] = ("file-code", null),
        ["yml"] = ("file-code", null),
        ["xml"] = ("file-code", null),
        // archives (amber)
        ["zip"] = ("archive", "#FCD34D"),
        ["rar"] = ("archive", "#FCD34D"),
        ["7z"] = ("archive", "#FCD34D"),
        ["tar"] = ("archive", "#FCD34D"),
        ["gz"] = ("archive", "#FCD34D"),
        ["xz"] = ("archive", "#FCD34D"),
        ["bz2"] = ("archive", "#FCD34D"),
        ["zst"] = ("archive", "#FCD34D"),
        ["iso"] = ("archive", "#FCD34D"),
        // packages and executables (violet)
        ["deb"] = ("package", "#C084FC"),
        ["rpm"] = ("package", "#C084FC"),
        ["flatpakref"] = ("package", "#C084FC"),
        ["appimage"] = ("zap", "#C084FC"),
        ["desktop"] = ("zap", "#C084FC"),
        ["run"] = ("zap", "#C084FC"),
        ["exe"] = ("zap", "#C084FC"),
        ["msi"] = ("zap", "#C084FC"),
    };

    /// <summary>Violet folder tint (accent-bright).</summary>
    public static readonly IBrush FolderBrush = new ImmutableSolidColorBrush(Color.Parse("#C084FC"));
    private static readonly IBrush MutedBrush = new ImmutableSolidColorBrush(Color.Parse("#9B9AAC"));
    private static readonly Dictionary<string, IBrush> BrushCache = new();

    public static string KeyForExtension(string ext) =>
        Map.TryGetValue(ext, out var m) ? m.Key : "file";

    public static IBrush BrushForExtension(string ext)
    {
        if (!Map.TryGetValue(ext, out var m) || m.Tint is null) return MutedBrush;
        if (!BrushCache.TryGetValue(m.Tint, out var b))
            BrushCache[m.Tint] = b = new ImmutableSolidColorBrush(Color.Parse(m.Tint));
        return b;
    }
}
