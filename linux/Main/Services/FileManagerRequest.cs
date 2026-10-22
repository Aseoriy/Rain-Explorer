namespace RainExplorer.Services;

public enum FileManagerAction { ShowItems, ShowFolders, ShowItemProperties }

public sealed record FileManagerTarget(string Folder, IReadOnlyList<string> Selection);

public sealed record FileManagerRequest(FileManagerAction Action, IReadOnlyList<string> Paths, string StartupId)
{
    public static FileManagerRequest Resolve(FileManagerAction action, IReadOnlyList<string> uris, string startupId)
    {
        if (!Enum.IsDefined(action)) throw new ArgumentException("Unknown file-manager action.");
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string value in uris)
        {
            string path = LocalPathFromUri(value);
            // A dangling link is still an item that can be revealed or inspected.
            bool link = new FileInfo(path).LinkTarget is not null;
            if (!link && !File.Exists(path) && !Directory.Exists(path))
                throw new FileNotFoundException($"“{PathUtil.DisplayName(path)}” no longer exists.", path);
            if (action == FileManagerAction.ShowFolders && !Directory.Exists(path))
                throw new ArgumentException($"“{PathUtil.DisplayName(path)}” is not a folder.");
            if (seen.Add(path)) paths.Add(path);
        }
        return new FileManagerRequest(action, paths, startupId);
    }

    internal static string LocalPathFromUri(string value)
    {
        if (!value.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsFile
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0
            || uri.Host.Length != 0 && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || value.Any(char.IsControl))
            throw new ArgumentException("Rain can reveal local file:// URIs only.");
        // .NET's file-URI well-formed check rejects valid escaped # and backslashes.
        for (int i = 0; i < value.Length; i++)
            if (value[i] == '%' && (i + 2 >= value.Length || !Uri.IsHexDigit(value[++i]) || !Uri.IsHexDigit(value[++i])))
                throw new ArgumentException("Invalid file URI escape.");
        // LocalPath treats a localhost authority as a Windows UNC path, even on Linux.
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Uri.UnescapeDataString(uri.AbsolutePath)));
        if (path.Contains('\0')) throw new ArgumentException("Invalid file path.");
        return path;
    }

    public IReadOnlyList<FileManagerTarget> Targets()
    {
        if (Action == FileManagerAction.ShowFolders)
            return Paths.Select(path => new FileManagerTarget(path, [])).ToArray();
        var folders = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string path in Paths)
        {
            string folder = Path.GetDirectoryName(path) ?? path;
            if (!folders.TryGetValue(folder, out var selection)) folders.Add(folder, selection = []);
            if (!PathUtil.Same(path, folder)) selection.Add(path);
        }
        return folders.Select(pair => new FileManagerTarget(pair.Key, pair.Value)).ToArray();
    }
}
