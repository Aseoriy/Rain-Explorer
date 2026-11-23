using System.Security.Cryptography;
using RainArchives.Contracts;
using RainArchives.Core;
using SharpSevenZip;

namespace RainArchives.Providers.SevenZip;

/// <summary>
/// The bundled official 7-Zip 26.02 x64 <c>7z.dll</c>. It is loaded only from the app-private
/// <c>native\win-x64</c> folder next to the Worker, and only when its bytes match the pinned
/// official build (recorded in docs/dependency-evidence.md and THIRD-PARTY-NOTICES.md). The current
/// directory, PATH, registry installs and user-chosen DLLs are never probed.
/// </summary>
public static class SevenZipNativeLibrary
{
    public const string Version = "26.02";
    public const long PinnedLength = 1_906_688;
    public const string PinnedSha256 = "69FD4DF057985C40E510E2FAC182881C7F85E90AA13EC703F763A8FDB2CE61F8";

    private static readonly object Gate = new();
    private static bool? s_verified;

    public static string LibraryPath { get; } = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "native", "win-x64", "7z.dll"));

    /// <summary>True when the pinned DLL is present and has been handed to SharpSevenZip.</summary>
    public static bool TryInitialize()
    {
        lock (Gate)
        {
            if (s_verified is { } known) return known;
            s_verified = Verify();
            return s_verified.Value;
        }
    }

    internal static void EnsureInitialized()
    {
        if (!TryInitialize())
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsupportedFormat,
                "7zip.library_unavailable",
                "The bundled 7-Zip engine is missing or does not match the pinned official build.");
        }
    }

    private static bool Verify()
    {
        if (!Environment.Is64BitProcess || !OperatingSystem.IsWindows()) return false;
        try
        {
            var info = new FileInfo(LibraryPath);
            if (!info.Exists || info.Length != PinnedLength
                || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            using (var stream = new FileStream(LibraryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var actual = SHA256.HashData(stream);
                if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(PinnedSha256)))
                {
                    return false;
                }
            }

            SharpSevenZipBase.SetLibraryPath(LibraryPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or NotSupportedException
                                              or SharpSevenZip.Exceptions.SharpSevenZipLibraryException)
        {
            return false;
        }
    }
}
