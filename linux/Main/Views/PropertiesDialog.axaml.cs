using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using RainExplorer.Helpers;
using RainExplorer.Models;
using RainExplorer.Services;

namespace RainExplorer.Views;

/// <summary>
/// File/folder properties, ported from the Windows build's four-tab dialog. General shows
/// the editable name, location, sizes and dates; Permissions replaces the NTFS security
/// tab with an owner/group/others × read/write/execute editor; Details and Hashes match
/// Windows. "Save changes" applies permission and name changes.
/// </summary>
public partial class PropertiesDialog : DialogWindow
{
    private readonly CancellationTokenSource _cts = new();
    private readonly FileOperationsService _ops = new();
    private readonly string _path;
    private readonly string _originalName;
    private readonly bool _isDir;
    private readonly bool _isLink;
    private readonly UnixFileMode _originalMode;
    private readonly bool _canEditMode;
    private readonly CheckBox[,] _bits;
    private bool _detailsBuilt;
    private bool _syncingBits;
    private bool _changed;

    private static readonly UnixFileMode[,] Masks =
    {
        { UnixFileMode.UserRead, UnixFileMode.UserWrite, UnixFileMode.UserExecute },
        { UnixFileMode.GroupRead, UnixFileMode.GroupWrite, UnixFileMode.GroupExecute },
        { UnixFileMode.OtherRead, UnixFileMode.OtherWrite, UnixFileMode.OtherExecute },
    };

    private const UnixFileMode AnyWrite = UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    public PropertiesDialog() : this(XdgDirs.Home) { }

    private PropertiesDialog(string path)
    {
        InitializeComponent();
        _bits = new[,]
        {
            { OwnerRead, OwnerWrite, OwnerExec },
            { GroupRead, GroupWrite, GroupExec },
            { OtherRead, OtherWrite, OtherExec },
        };

        _path = path;
        FileSystemInfo info = Directory.Exists(path) && !File.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        _isLink = info.LinkTarget is not null;
        _isDir = Directory.Exists(path);
        _originalName = PathUtil.DisplayName(path);
        var item = FileSystemService.CreateItem(info, showHidden: true) ?? new FileItem
        {
            Name = _originalName, FullPath = path, IsDirectory = _isDir,
        };
        var stat = UnixStat.TryGet(path);

        // Header: the same system icon the file list shows, Lucide glyph as fallback.
        GlyphIcon.Kind = item.IconKey;
        GlyphIcon.Foreground = item.IconBrush;
        IconThemeService.Load(item, 48, bitmap =>
        {
            GlyphImage.Source = bitmap;
            GlyphImage.IsVisible = true;
            GlyphIcon.IsVisible = false;
        });

        NameBox.Text = _originalName;
        NameBox.IsEnabled = path != "/";
        TypeText.Text = _isLink ? (_isDir ? "Link to folder" : $"Link to {item.TypeLabel.ToLowerInvariant()}") : item.TypeLabel;
        LocationText.Text = Path.GetDirectoryName(path.TrimEnd('/')) ?? "/";
        if (_isLink)
        {
            LinkLabel.IsVisible = LinkText.IsVisible = true;
            LinkText.Text = info.LinkTarget;
        }
        CreatedText.Text = stat?.BirthTimeUtc is { } born ? Format(born.ToLocalTime()) : "—";
        ModifiedText.Text = Format(SafeTime(() => info.LastWriteTime));
        AccessedText.Text = Format(SafeTime(() => info.LastAccessTime));
        HiddenNote.IsVisible = _originalName.StartsWith('.');

        try { _originalMode = File.GetUnixFileMode(path); } catch { _originalMode = 0; }
        uint uid = CurrentUid();
        _canEditMode = !_isLink && stat is { } owned && (owned.Uid == uid || uid == 0);

        if (_isDir && !_isLink)
        {
            ContainsLabel.IsVisible = ContainsText.IsVisible = true;
            ContainsText.Text = SizeText.Text = SizeOnDiskText.Text = "Calculating…";
            _ = ComputeFolderSizeAsync(path, _cts.Token);

            HashGrid.IsVisible = HashButton.IsVisible = false;
            HashStatus.Text = "Checksums aren't available for folders.";
            ExecutableCheck.IsVisible = false;
            ExecuteHeader.Text = "Enter";
        }
        else
        {
            long length = _isLink ? System.Text.Encoding.UTF8.GetByteCount(info.LinkTarget ?? "") : item.Size;
            SizeText.Text = SizeLabel(length);
            SizeOnDiskText.Text = stat is { } s ? SizeLabel((long)s.Blocks * 512) : "—";
        }

        // "Run as a program" only means something for files.
        if (_isDir) ExecutableCheck.IsVisible = false;

        if (_isLink)
        {
            HashGrid.IsVisible = HashButton.IsVisible = false;
            HashStatus.Text = "Checksums aren't available for symbolic links.";
        }

        // Permissions tab + General attribute shortcuts share the same bits.
        if (stat is { } st)
        {
            OwnerText.Text = $"{UserName(st.Uid)}  ({st.Uid})";
            GroupText.Text = $"{GroupName(st.Gid)}  ({st.Gid})";
        }
        else
        {
            OwnerText.Text = GroupText.Text = "—";
        }
        for (int r = 0; r < 3; r++)
        for (int c = 0; c < 3; c++)
        {
            _bits[r, c].IsChecked = (_originalMode & Masks[r, c]) != 0;
            _bits[r, c].IsEnabled = _canEditMode;
            _bits[r, c].IsCheckedChanged += (_, _) => SyncFromBits();
        }
        ReadOnlyCheck.IsEnabled = ExecutableCheck.IsEnabled = _canEditMode;
        ReadOnlyCheck.IsCheckedChanged += (_, _) => ApplyReadOnlyShortcut();
        ExecutableCheck.IsCheckedChanged += (_, _) => ApplyExecutableShortcut();
        SyncFromBits();
        if (!_canEditMode)
        {
            SecurityStatus.IsVisible = true;
            SecurityStatus.Text = _isLink
                ? "A link's permissions come from the item it points to."
                : "Only the owner (or root) can change these permissions.";
        }

        NavList.SelectionChanged += (_, _) => ShowPane(NavList.SelectedIndex);
        DragFrom(TitleBar);
        SetDefault(Save);

        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            NameBox.Focus();
            NameBox.SelectionStart = 0;
            NameBox.SelectionEnd = RenameSelection.GetSelectionLength(_originalName, _isDir,
                SettingsStore.Instance.Settings.SelectFileNameWithoutExtensionOnRename);
        }, DispatcherPriority.Input);
        Closed += (_, _) =>
        {
            _cts.Cancel();
            _cts.Dispose();
        };
    }

    /// <summary>Show properties for <paramref name="path"/>. Returns true if something changed.</summary>
    public static async Task<bool> ShowFor(Window? owner, string path) =>
        await new PropertiesDialog(path).ShowFor<bool>(owner);

    // ---- Tabs ------------------------------------------------------------------

    private void ShowPane(int index)
    {
        PaneGeneral.IsVisible = index <= 0;
        PaneSecurity.IsVisible = index == 1;
        PaneDetails.IsVisible = index == 2;
        PaneHashes.IsVisible = index == 3;
        if (index == 2 && !_detailsBuilt)
        {
            _detailsBuilt = true;
            BuildDetails();
        }
    }

    // ---- Permissions -------------------------------------------------------------

    private UnixFileMode CurrentMode()
    {
        UnixFileMode mode = _originalMode;
        for (int r = 0; r < 3; r++)
        for (int c = 0; c < 3; c++)
        {
            if (_bits[r, c].IsChecked == true) mode |= Masks[r, c];
            else mode &= ~Masks[r, c];
        }
        return mode;
    }

    private void SyncFromBits()
    {
        if (_syncingBits) return;
        _syncingBits = true;
        UnixFileMode mode = CurrentMode();
        OctalText.Text = Convert.ToString((int)mode & 0xFFF, 8).PadLeft(4, '0') + "   " + Symbolic(mode);
        ReadOnlyCheck.IsChecked = (mode & AnyWrite) == 0;
        ExecutableCheck.IsChecked = (mode & UnixFileMode.UserExecute) != 0;
        _syncingBits = false;
    }

    private void ApplyReadOnlyShortcut()
    {
        if (_syncingBits) return;
        bool readOnly = ReadOnlyCheck.IsChecked == true;
        _syncingBits = true;
        if (readOnly)
        {
            for (int r = 0; r < 3; r++) _bits[r, 1].IsChecked = false;
        }
        else
        {
            // Restore the write bits the item had, or at least the owner's.
            bool any = false;
            for (int r = 0; r < 3; r++)
            {
                bool had = (_originalMode & Masks[r, 1]) != 0;
                _bits[r, 1].IsChecked = had;
                any |= had;
            }
            if (!any) _bits[0, 1].IsChecked = true;
        }
        _syncingBits = false;
        SyncFromBits();
    }

    private void ApplyExecutableShortcut()
    {
        if (_syncingBits) return;
        bool exec = ExecutableCheck.IsChecked == true;
        _syncingBits = true;
        for (int r = 0; r < 3; r++)
        {
            // Like `chmod +x`: give execute to whoever can read.
            if (exec) _bits[r, 2].IsChecked = _bits[r, 0].IsChecked == true || r == 0;
            else _bits[r, 2].IsChecked = false;
        }
        _syncingBits = false;
        SyncFromBits();
    }

    private static string Symbolic(UnixFileMode mode)
    {
        Span<char> chars = stackalloc char[9];
        for (int r = 0; r < 3; r++)
        {
            chars[r * 3] = (mode & Masks[r, 0]) != 0 ? 'r' : '-';
            chars[r * 3 + 1] = (mode & Masks[r, 1]) != 0 ? 'w' : '-';
            chars[r * 3 + 2] = (mode & Masks[r, 2]) != 0 ? 'x' : '-';
        }
        return new string(chars);
    }

    // ---- Details -----------------------------------------------------------------

    private void BuildDetails()
    {
        var stat = UnixStat.TryGet(_path);
        AddDetailHeader("ITEM");
        AddDetail("Name", _originalName);
        string mime = _isDir ? MimeService.Directory : MimeService.ForFileName(_originalName);
        AddDetail("Type", _isDir ? "Folder" : MimeService.Comment(mime) ?? "File");
        AddDetail("MIME type", mime, mono: true);
        if (!_isDir)
        {
            string ext = Path.GetExtension(_originalName);
            AddDetail("Extension", string.IsNullOrEmpty(ext) ? "(none)" : ext);
        }
        AddDetail("Full path", _path, mono: true);
        AddDetail("Parent folder", Path.GetDirectoryName(_path.TrimEnd('/')) ?? "—", mono: true);

        AddDetailHeader("FILESYSTEM");
        AddDetail("Mount point", MountService.GetMountPoint(_path), mono: true);
        if (stat is { } s)
        {
            AddDetail("Inode", s.Inode.ToString(), mono: true);
            AddDetail("Hard links", s.LinkCount.ToString());
            AddDetail("Mode", Convert.ToString((int)s.Mode, 8), mono: true);
        }
        if (!_isDir && !_isLink && ThumbnailService.ReadPixelSize(_path) is { } px)
            AddDetail("Dimensions", $"{px.Width} × {px.Height} px");

        AddDetailHeader("TIMESTAMPS");
        FileSystemInfo info = _isDir ? new DirectoryInfo(_path) : new FileInfo(_path);
        AddDetail("Created", stat?.BirthTimeUtc is { } born ? Format(born.ToLocalTime(), seconds: true) : "—", mono: true);
        AddDetail("Modified", Format(SafeTime(() => info.LastWriteTime), seconds: true), mono: true);
        AddDetail("Accessed", Format(SafeTime(() => info.LastAccessTime), seconds: true), mono: true);
    }

    private void AddDetailHeader(string text)
    {
        var header = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            Margin = new Avalonia.Thickness(0, DetailsPanel.Children.Count == 0 ? 2 : 14, 0, 8),
        };
        header.Bind(TextBlock.ForegroundProperty, header.GetResourceObservable("TextFaint"));
        DetailsPanel.Children.Add(header);
    }

    private void AddDetail(string label, string value, bool mono = false)
    {
        var grid = new Grid { Margin = new Avalonia.Thickness(0, 5), ColumnDefinitions = new ColumnDefinitions("110,*") };
        var l = new TextBlock { Text = label, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        l.Bind(TextBlock.ForegroundProperty, l.GetResourceObservable("TextMuted"));
        var v = new SelectableTextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
        v.Bind(SelectableTextBlock.ForegroundProperty, v.GetResourceObservable("Text"));
        if (mono)
        {
            v.Bind(SelectableTextBlock.FontFamilyProperty, v.GetResourceObservable("MonoFont"));
            v.FontSize = 12;
        }
        Grid.SetColumn(v, 1);
        grid.Children.Add(l);
        grid.Children.Add(v);
        DetailsPanel.Children.Add(grid);
    }

    // ---- Hashes ------------------------------------------------------------------

    private void ComputeHashes_Click(object? sender, RoutedEventArgs e)
    {
        if (!_isDir && !_isLink) _ = ComputeHashesAsync(_cts.Token);
    }

    private async Task ComputeHashesAsync(CancellationToken ct)
    {
        HashButton.IsEnabled = false;
        HashStatus.Text = "Computing…";
        Md5Text.Text = Sha1Text.Text = Sha256Text.Text = "…";
        try
        {
            var (md5, sha1, sha256) = await Task.Run(() =>
            {
                using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    1 << 20, FileOptions.SequentialScan);
                using var md5h = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                using var sha1h = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                using var sha256h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[1 << 20];
                int n;
                while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    md5h.AppendData(buffer, 0, n);
                    sha1h.AppendData(buffer, 0, n);
                    sha256h.AppendData(buffer, 0, n);
                }
                return (Convert.ToHexString(md5h.GetHashAndReset()).ToLowerInvariant(),
                    Convert.ToHexString(sha1h.GetHashAndReset()).ToLowerInvariant(),
                    Convert.ToHexString(sha256h.GetHashAndReset()).ToLowerInvariant());
            }, ct);
            if (ct.IsCancellationRequested) return;
            Md5Text.Text = md5;
            Sha1Text.Text = sha1;
            Sha256Text.Text = sha256;
            HashStatus.Text = "Done";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            HashStatus.Text = ex.Message;
            Md5Text.Text = Sha1Text.Text = Sha256Text.Text = "";
        }
        finally
        {
            if (!ct.IsCancellationRequested) HashButton.IsEnabled = true;
        }
    }

    private void CopyHash_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control) return;
        string? text = (control.Tag as string) switch
        {
            "md5" => Md5Text.Text,
            "sha1" => Sha1Text.Text,
            _ => Sha256Text.Text,
        };
        if (string.IsNullOrWhiteSpace(text) || text == "…") return;
        _ = ClipboardService.SetTextAsync(this, text);
    }

    // ---- Save --------------------------------------------------------------------

    private void Save_Click(object? sender, RoutedEventArgs e) => Save();

    private void Save()
    {
        ErrorText.IsVisible = false;

        // 1) Permissions, applied on the current path before any rename.
        UnixFileMode mode = CurrentMode();
        if (_canEditMode && mode != _originalMode)
        {
            try
            {
                File.SetUnixFileMode(_path, mode);
                _changed = true;
            }
            catch (Exception ex)
            {
                ShowError($"Couldn't change permissions: {ex.Message}");
                return;
            }
        }

        // 2) Rename if the name field changed.
        string newName = NameBox.Text?.Trim() ?? string.Empty;
        if (!string.Equals(newName, _originalName, StringComparison.Ordinal))
        {
            string? err = _ops.Rename(_path, newName);
            if (err is not null)
            {
                ShowError(err);
                return;
            }
            string newPath = Path.Combine(Path.GetDirectoryName(_path.TrimEnd('/'))!, newName);
            UndoService.Instance.Push(new RenameAction(newPath, _path));
            _changed = true;
        }

        Finish(_changed);
    }

    private void ShowError(string message)
    {
        // Make sure the error is visible even if the user is on another tab.
        NavList.SelectedIndex = 0;
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Finish(_changed);

    // ---- Location actions --------------------------------------------------------

    private void CopyPath_Click(object? sender, RoutedEventArgs e) => _ = ClipboardService.SetTextAsync(this, _path);

    private void OpenLocation_Click(object? sender, RoutedEventArgs e)
    {
        string? folder = Path.GetDirectoryName(_path.TrimEnd('/'));
        if (folder is not null && Owner is MainWindow main) main.OpenForwardedTarget(folder, _path);
    }

    // ---- Folder size walk (size, size on disk, contains) ----------------------------

    private async Task ComputeFolderSizeAsync(string root, CancellationToken token)
    {
        try
        {
            var (bytes, onDisk, files, folders) = await Task.Run(() =>
            {
                long total = 0, allocated = 0;
                int f = 0, d = 0;
                var stack = new Stack<string>();
                stack.Push(root);
                var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
                while (stack.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    IEnumerable<FileSystemInfo> entries;
                    try { entries = new DirectoryInfo(stack.Pop()).EnumerateFileSystemInfos("*", options); }
                    catch { continue; }
                    foreach (FileSystemInfo entry in entries)
                    {
                        token.ThrowIfCancellationRequested();
                        // Links are counted, never followed.
                        if (entry.LinkTarget is not null) { f++; continue; }
                        if (entry is FileInfo file)
                        {
                            f++;
                            total += file.Length;
                            allocated += (long)(UnixStat.TryGet(file.FullName)?.Blocks ?? 0) * 512;
                        }
                        else
                        {
                            d++;
                            stack.Push(entry.FullName);
                        }
                    }
                }
                return (total, allocated, f, d);
            }, token);
            SizeText.Text = SizeLabel(bytes);
            SizeOnDiskText.Text = SizeLabel(onDisk);
            ContainsText.Text = $"{files:N0} file{(files == 1 ? "" : "s")}, {folders:N0} folder{(folders == 1 ? "" : "s")}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SizeText.Text = SizeOnDiskText.Text = "—";
            ContainsText.Text = $"Couldn't measure: {ex.Message}";
        }
    }

    // ---- Helpers -----------------------------------------------------------------

    private static string SizeLabel(long bytes) =>
        bytes <= 0 ? "0 bytes" : $"{FileItem.FormatSize(bytes)}  ({bytes:N0} bytes)";

    private static DateTime SafeTime(Func<DateTime> read)
    {
        try { return read(); } catch { return default; }
    }

    private static string Format(DateTime when, bool seconds = false) =>
        when.Year < 1971 ? "—" : when.ToString(seconds ? "yyyy-MM-dd  HH:mm:ss" : "yyyy-MM-dd  HH:mm");

    [DllImport("libc")]
    private static extern uint getuid();

    private static uint CurrentUid()
    {
        try { return getuid(); } catch { return uint.MaxValue; }
    }

    private static string UserName(uint uid) => LookupName("/etc/passwd", uid) ?? uid.ToString();
    private static string GroupName(uint gid) => LookupName("/etc/group", gid) ?? gid.ToString();

    /// <summary>Resolve an id from the local passwd/group database (field 3 is the id).</summary>
    private static string? LookupName(string file, uint id)
    {
        try
        {
            foreach (string line in File.ReadLines(file))
            {
                string[] parts = line.Split(':');
                if (parts.Length > 2 && uint.TryParse(parts[2], out uint value) && value == id) return parts[0];
            }
        }
        catch { }
        return null;
    }
}
