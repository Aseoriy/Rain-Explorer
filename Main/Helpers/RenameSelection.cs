using System.IO;

namespace RainExplorer.Helpers;

internal static class RenameSelection
{
    public static int GetSelectionLength(string name, bool isDirectory, bool excludeFileExtension)
    {
        if (string.IsNullOrEmpty(name) || isDirectory || !excludeFileExtension)
            return name.Length;

        string extension = Path.GetExtension(name);
        int nameLength = name.Length - extension.Length;

        // A dotfile such as ".gitignore" has no separate filename portion to select.
        return extension.Length > 0 && nameLength > 0 ? nameLength : name.Length;
    }
}
