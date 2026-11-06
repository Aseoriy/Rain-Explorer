using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.Providers.WinRar;

internal sealed partial class WinRarEncoderWorkspace
{
    // Each entry pins only its destination ancestry. Rescanning all earlier
    // materialized files for every new entry makes preparation quadratic.
    // Full-tree verification still brackets encoder use and exact cleanup.
    internal IDisposable PrepareMaterializationDirectory(
        string directoryPath,
        ref int objectCount,
        int maximumObjects,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(objectCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumObjects);
        var prefix = DirectoryPath + Path.DirectorySeparatorChar;
        var canonical = Path.GetFullPath(directoryPath);
        if (!canonical.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsafePath,
                "rar.materialization_path_escape",
                "The RAR materialization directory is outside its private workspace.");
        }

        EnsureDirectoryHandleIsOrdinary(_directoryHandle);
        var lease = new MaterializationDirectoryLease();
        try
        {
            var current = DirectoryPath;
            foreach (var component in canonical[prefix.Length..].Split(Path.DirectorySeparatorChar))
            {
                cancellationToken.ThrowIfCancellationRequested();
                current = Path.Combine(current, component);
                if (!Directory.Exists(current))
                {
                    EnsureMaterializationObjectAvailable(objectCount, maximumObjects);
                    if (CreateDirectory(current, IntPtr.Zero))
                    {
                        objectCount++;
                    }
                    else
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error is not ErrorAlreadyExists and not ErrorFileExists)
                        {
                            throw new Win32Exception(error,
                                "The private RAR materialization directory could not be created.");
                        }
                    }
                }

                // Open the component itself without following a reparse, then
                // keep it non-renamable until the caller closes its output file.
                var handle = OpenDirectory(current, shareDelete: false);
                lease.Handles.Add(handle);
                EnsureDirectoryHandleIsOrdinary(handle);
            }

            return lease;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is not 5 and not 1314)
        {
            lease.Dispose();
            throw DescribeMaterializationFailure(exception);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    internal static ArchiveProviderException DescribeMaterializationFailure(Win32Exception exception) => new(
        ArchiveErrorCategory.ProviderFailure,
        exception.NativeErrorCode == 206 ? "rar.workspace_path_too_long" : "rar.workspace_io_failure",
        exception.NativeErrorCode == 206
            ? "A RAR preparation path or name exceeds the filesystem limit."
            : "RAR preparation could not create or pin a private workspace directory.",
        exception);

    internal static void EnsureMaterializationObjectAvailable(int objectCount, int maximumObjects)
    {
        if (objectCount >= maximumObjects)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ResourceLimit,
                "rar.workspace_object_limit",
                "The private RAR encoder workspace exceeded its bounded object count.");
        }
    }

    private sealed class MaterializationDirectoryLease : IDisposable
    {
        internal List<SafeFileHandle> Handles { get; } = [];

        public void Dispose()
        {
            for (var index = Handles.Count - 1; index >= 0; index--)
            {
                Handles[index].Dispose();
            }
            Handles.Clear();
        }
    }
}
