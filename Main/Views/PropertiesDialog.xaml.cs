using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RainExplorer.Models;
using RainExplorer.Services;

namespace RainExplorer.Views;

public partial class PropertiesDialog : Window
{
    private readonly CancellationTokenSource _cts = new();
    private readonly FileOperationsService _ops = new();
    private readonly bool _isDir;
    private string _path;
    private readonly string _originalName;
    private bool _detailsBuilt;
    private bool _hashesStarted;

    /// <summary>True when the name or attributes were changed and applied — caller should reload.</summary>
    public bool Changed { get; private set; }

    public PropertiesDialog(FileItem item)
    {
        InitializeComponent();

        _path = item.FullPath;
        _isDir = item.IsDirectory;
        _originalName = item.Name;

        GlyphIcon.Data = TryFindResource("Ic." + item.IconKey) as Geometry;
        GlyphIcon.Stroke = item.IconBrush;
        NameBox.Text = item.Name;
        TypeText.Text = item.TypeLabel;
        LocationText.Text = Path.GetDirectoryName(_path) ?? _path;
        ModifiedText.Text = item.ModifiedText;

        try
        {
            CreatedText.Text = (_isDir
                ? new DirectoryInfo(_path).CreationTime
                : new FileInfo(_path).CreationTime).ToString("yyyy-MM-dd  HH:mm");
            AccessedText.Text = (_isDir
                ? new DirectoryInfo(_path).LastAccessTime
                : new FileInfo(_path).LastAccessTime).ToString("yyyy-MM-dd  HH:mm");

            var attr = File.GetAttributes(_path);
            ReadOnlyCheck.IsChecked = attr.HasFlag(FileAttributes.ReadOnly);
            HiddenCheck.IsChecked = attr.HasFlag(FileAttributes.Hidden);
            ArchiveCheck.IsChecked = attr.HasFlag(FileAttributes.Archive);
            if (attr.HasFlag(FileAttributes.System)) SystemNote.Visibility = Visibility.Visible;
        }
        catch
        {
            CreatedText.Text = AccessedText.Text = "—";
        }

        if (_isDir)
        {
            ContainsLabel.Visibility = Visibility.Visible;
            ContainsText.Visibility = Visibility.Visible;
            ContainsText.Text = "Calculating…";
            SizeText.Text = "Calculating…";
            SizeOnDiskText.Text = "Calculating…";
            _ = ComputeFolderSizeAsync(_path, _cts.Token);

            // Hashes don't apply to folders.
            HashGrid.Visibility = Visibility.Collapsed;
            HashButton.Visibility = Visibility.Collapsed;
            HashStatus.Text = "Checksums aren't available for folders.";
        }
        else
        {
            long len = item.Size;
            SizeText.Text = $"{FormatSize(len)}  ({len:N0} bytes)";
            long onDisk = RoundToCluster(len, ClusterSize(_path));
            SizeOnDiskText.Text = $"{FormatSize(onDisk)}  ({onDisk:N0} bytes)";
        }

        Closed += (_, _) => _cts.Cancel();
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    // ---- Tab switching ------------------------------------------------------
    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PaneGeneral is null) return;   // not built yet (fires during init)
        int idx = NavList.SelectedIndex;

        PaneGeneral.Visibility = idx == 0 ? Visibility.Visible : Visibility.Collapsed;
        PaneDetails.Visibility = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
        PaneHashes.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;

        if (idx == 1 && !_detailsBuilt) BuildDetails();
        if (idx == 2 && !_hashesStarted && !_isDir)
        {
            _hashesStarted = true;
            _ = ComputeHashesAsync(_cts.Token);
        }
    }

    // ---- Details tab --------------------------------------------------------
    private void BuildDetails()
    {
        _detailsBuilt = true;

        AddDetailHeader("ITEM");
        AddDetail("Name", Path.GetFileName(_path));
        AddDetail("Type", _isDir ? "Folder" : DescribeType());
        if (!_isDir)
        {
            string ext = Path.GetExtension(_path);
            AddDetail("Extension", string.IsNullOrEmpty(ext) ? "(none)" : ext);
        }
        AddDetail("Full path", _path, mono: true);
        AddDetail("Parent folder", Path.GetDirectoryName(_path) ?? "—", mono: true);

        AddDetailHeader("ATTRIBUTES");
        try { AddDetail("Flags", DescribeAttributes(File.GetAttributes(_path))); }
        catch { AddDetail("Flags", "—"); }

        AddDetailHeader("TIMESTAMPS");
        try
        {
            DateTime created = _isDir ? new DirectoryInfo(_path).CreationTime : new FileInfo(_path).CreationTime;
            DateTime modified = _isDir ? new DirectoryInfo(_path).LastWriteTime : new FileInfo(_path).LastWriteTime;
            DateTime accessed = _isDir ? new DirectoryInfo(_path).LastAccessTime : new FileInfo(_path).LastAccessTime;
            AddDetail("Created", created.ToString("yyyy-MM-dd  HH:mm:ss"), mono: true);
            AddDetail("Modified", modified.ToString("yyyy-MM-dd  HH:mm:ss"), mono: true);
            AddDetail("Accessed", accessed.ToString("yyyy-MM-dd  HH:mm:ss"), mono: true);
        }
        catch { AddDetail("Created", "—"); }
    }

    private string DescribeType()
    {
        string ext = Path.GetExtension(_path).TrimStart('.').ToUpperInvariant();
        return ext.Length > 0 ? $"{ext} file" : "File";
    }

    private static string DescribeAttributes(FileAttributes a)
    {
        var flags = new[]
        {
            FileAttributes.ReadOnly, FileAttributes.Hidden, FileAttributes.System,
            FileAttributes.Archive, FileAttributes.Compressed, FileAttributes.Encrypted,
            FileAttributes.ReparsePoint, FileAttributes.Temporary,
        };
        var on = flags.Where(f => a.HasFlag(f)).Select(f => f.ToString()).ToList();
        return on.Count == 0 ? "Normal" : string.Join(", ", on);
    }

    private void AddDetailHeader(string text) =>
        DetailsPanel.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = (Brush)FindResource("TextFaint"),
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, DetailsPanel.Children.Count == 0 ? 2 : 14, 0, 8),
        });

    private void AddDetail(string label, string value, bool mono = false)
    {
        var g = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var l = new TextBlock
        {
            Text = label,
            Foreground = (Brush)FindResource("TextMuted"),
            VerticalAlignment = VerticalAlignment.Top,
        };
        var v = new TextBlock
        {
            Text = value,
            Foreground = (Brush)FindResource("Text"),
            TextWrapping = TextWrapping.Wrap,
        };
        if (mono) { v.FontFamily = (FontFamily)FindResource("MonoFont"); v.FontSize = 12; }

        Grid.SetColumn(l, 0);
        Grid.SetColumn(v, 1);
        g.Children.Add(l);
        g.Children.Add(v);
        DetailsPanel.Children.Add(g);
    }

    // ---- Hashes tab ---------------------------------------------------------
    private void ComputeHashes_Click(object sender, RoutedEventArgs e)
    {
        if (_isDir) return;
        _ = ComputeHashesAsync(_cts.Token);
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
                using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
                using var md5h = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                using var sha1h = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                using var sha256h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                byte[] buf = new byte[1 << 20];
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    md5h.AppendData(buf, 0, n);
                    sha1h.AppendData(buf, 0, n);
                    sha256h.AppendData(buf, 0, n);
                }
                return (
                    Convert.ToHexString(md5h.GetHashAndReset()).ToLowerInvariant(),
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
            HashButton.IsEnabled = true;
        }
    }

    private void CopyHash_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        string text = (fe.Tag as string) switch
        {
            "md5" => Md5Text.Text,
            "sha1" => Sha1Text.Text,
            _ => Sha256Text.Text,
        };
        if (string.IsNullOrWhiteSpace(text) || text == "…") return;
        try { Clipboard.SetText(text); } catch { /* clipboard busy */ }
    }

    // ---- Save: apply attribute + name changes ------------------------------
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;

        // 1) Attributes (apply on the current path, before any rename).
        try
        {
            var attr = File.GetAttributes(_path);
            attr = Toggle(attr, FileAttributes.ReadOnly, ReadOnlyCheck.IsChecked == true);
            attr = Toggle(attr, FileAttributes.Hidden, HiddenCheck.IsChecked == true);
            attr = Toggle(attr, FileAttributes.Archive, ArchiveCheck.IsChecked == true);
            if (attr != File.GetAttributes(_path))
            {
                File.SetAttributes(_path, attr);
                Changed = true;
            }
        }
        catch (Exception ex)
        {
            ShowError($"Couldn't update attributes: {ex.Message}");
            return;
        }

        // 2) Rename if the name field changed.
        string newName = NameBox.Text.Trim();
        if (!string.Equals(newName, _originalName, StringComparison.Ordinal))
        {
            string? err = _ops.Rename(_path, newName);
            if (err is not null) { ShowError(err); return; }
            Changed = true;
        }

        DialogResult = true;
        Close();
    }

    private static FileAttributes Toggle(FileAttributes attr, FileAttributes flag, bool on) =>
        on ? attr | flag : attr & ~flag;

    private void ShowError(string msg)
    {
        // Make sure the error is visible even if the user is on another tab.
        NavList.SelectedIndex = 0;
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
    }

    // ---- Location actions ---------------------------------------------------
    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_path); } catch { /* clipboard busy */ }
    }

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_path}\"") { UseShellExecute = true }); }
        catch { /* best effort */ }
    }

    // ---- Folder size walk (size, size-on-disk, contains) -------------------
    private async Task ComputeFolderSizeAsync(string path, CancellationToken ct)
    {
        long cluster = ClusterSize(path);
        try
        {
            var (bytes, onDisk, files, dirs) = await Task.Run(() =>
            {
                long total = 0, alloc = 0;
                int fileCount = 0, dirCount = 0;
                var stack = new Stack<string>();
                stack.Push(path);
                while (stack.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    string dir = stack.Pop();
                    try
                    {
                        foreach (string f in Directory.EnumerateFiles(dir))
                        {
                            ct.ThrowIfCancellationRequested();
                            try
                            {
                                long len = new FileInfo(f).Length;
                                total += len;
                                alloc += RoundToCluster(len, cluster);
                                fileCount++;
                            }
                            catch { }
                        }
                        foreach (string d in Directory.EnumerateDirectories(dir))
                        {
                            dirCount++;
                            stack.Push(d);
                        }
                    }
                    catch { /* skip unreadable folders */ }
                }
                return (total, alloc, fileCount, dirCount);
            }, ct);

            if (ct.IsCancellationRequested) return;
            SizeText.Text = $"{FormatSize(bytes)}  ({bytes:N0} bytes)";
            SizeOnDiskText.Text = $"{FormatSize(onDisk)}  ({onDisk:N0} bytes)";
            ContainsText.Text = $"{files:N0} files, {dirs:N0} folders";
        }
        catch (OperationCanceledException) { }
        catch
        {
            SizeText.Text = SizeOnDiskText.Text = ContainsText.Text = "—";
        }
    }

    // ---- Cluster size (for "size on disk") ---------------------------------
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool GetDiskFreeSpace(string root, out uint sectorsPerCluster,
        out uint bytesPerSector, out uint freeClusters, out uint totalClusters);

    private static long ClusterSize(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (root is not null && GetDiskFreeSpace(root, out uint spc, out uint bps, out _, out _) && spc > 0 && bps > 0)
                return (long)spc * bps;
        }
        catch { }
        return 4096; // sensible NTFS default
    }

    private static long RoundToCluster(long bytes, long cluster)
    {
        if (cluster <= 0) cluster = 4096;
        return (bytes + cluster - 1) / cluster * cluster;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double n = bytes;
        int i = 0;
        while (n >= 1024 && i < units.Length - 1) { n /= 1024; i++; }
        return i == 0 ? $"{n:0} {units[i]}" : $"{n:0.0} {units[i]}";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Title_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
