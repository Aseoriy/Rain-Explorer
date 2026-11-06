using System.IO;
using System.Runtime.InteropServices;

namespace RainExplorer.Services;

/// <summary>Opens the documented Windows app chooser for one existing ordinary file.</summary>
internal static class OpenWithDialogService
{
    private const uint OaifAllowRegistration = 0x00000001;
    private const uint OaifExecute = 0x00000004;
    private const int CancelledHResult = unchecked((int)0x800704C7);

    internal static bool TryShow(IntPtr ownerHandle, string path, out string? error)
    {
        error = null;
        if (!TryNormalizeExistingFile(path, out string canonicalPath))
        {
            error = "The selected file is no longer available.";
            return false;
        }

        var request = new OpenAsInfo
        {
            File = canonicalPath,
            Class = null,
            Flags = OaifAllowRegistration | OaifExecute,
        };
        int result;
        try
        {
            result = SHOpenWithDialog(ownerHandle, ref request);
        }
        catch (Exception exception) when (exception is DllNotFoundException
                                              or EntryPointNotFoundException
                                              or ExternalException)
        {
            error = "Windows could not open the app chooser.";
            return false;
        }

        if (result >= 0) return true;
        if (result == CancelledHResult) return false;
        error = "Windows could not open the app chooser.";
        return false;
    }

    internal static bool TryNormalizeExistingFile(string? path, out string canonicalPath)
    {
        canonicalPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        try
        {
            canonicalPath = Path.GetFullPath(path);
            return File.Exists(canonicalPath)
                && !File.GetAttributes(canonicalPath).HasFlag(FileAttributes.Directory);
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or IOException
                                              or NotSupportedException
                                              or PathTooLongException
                                              or UnauthorizedAccessException)
        {
            canonicalPath = string.Empty;
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenAsInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string File;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Class;

        public uint Flags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHOpenWithDialog(IntPtr ownerHandle, ref OpenAsInfo request);
}
