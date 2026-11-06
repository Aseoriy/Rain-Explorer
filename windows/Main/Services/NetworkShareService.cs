using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using RainExplorer.Models;

namespace RainExplorer.Services;

internal static class NetworkShareService
{
    private const int MoreData = 234;
    private const uint ShareTypeMask = 0xFF;
    private const uint SpecialShare = 0x80000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ShareInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? Name;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Remark;
    }

    internal delegate int ShareEnumerator(string server, uint level, out IntPtr buffer,
        uint preferredLength, out uint entriesRead, out uint totalEntries, ref uint resumeHandle);

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int NetShareEnum(string server, uint level, out IntPtr buffer,
        uint preferredLength, out uint entriesRead, out uint totalEntries, ref uint resumeHandle);

    [DllImport("Netapi32.dll", ExactSpelling = true)]
    private static extern int NetApiBufferFree(IntPtr buffer);

    public static IReadOnlyList<FileItem> ReadShares(string path, bool showHidden, CancellationToken ct) =>
        ReadShares(path, showHidden, ct, NetShareEnum, buffer => { _ = NetApiBufferFree(buffer); });

    internal static IReadOnlyList<FileItem> ReadShares(string path, bool showHidden,
        CancellationToken ct, ShareEnumerator enumerate, Action<IntPtr> freeBuffer)
    {
        if (!NetworkPath.TryGetServerRoot(path, out string server))
            throw new ArgumentException("Enter a network server address such as \\\\server.", nameof(path));

        var result = new List<FileItem>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resumeHandles = new HashSet<uint>();
        uint resume = 0;
        int size = Marshal.SizeOf<ShareInfo>();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            IntPtr buffer = IntPtr.Zero;
            try
            {
                int status = enumerate(server, 1, out buffer, 64 * 1024,
                    out uint count, out _, ref resume);
                ct.ThrowIfCancellationRequested();
                if (status != 0 && status != MoreData)
                    throw new Win32Exception(status, $"Couldn't list shared folders: {new Win32Exception(status).Message}");
                if (count > 0 && buffer == IntPtr.Zero)
                    throw new IOException("The server returned an invalid shared-folder list.");

                for (uint i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var share = Marshal.PtrToStructure<ShareInfo>(IntPtr.Add(buffer, checked((int)i * size)));
                    // Only disk shares are folders. IPC endpoints and printers cannot be browsed.
                    if ((share.Type & ShareTypeMask) != 0 || !NetworkPath.IsValidShareName(share.Name)) continue;
                    if (!showHidden && ((share.Type & SpecialShare) != 0 || share.Name!.EndsWith('$'))) continue;
                    if (!names.Add(share.Name!)) continue;
                    result.Add(new FileItem
                    {
                        Name = share.Name!,
                        FullPath = server + @"\" + share.Name,
                        IsDirectory = true,
                        IsNetworkShare = true,
                    });
                }

                if (status == 0) return result;
                if (count == 0 || resume == 0 || !resumeHandles.Add(resume))
                    throw new IOException("The server couldn't finish listing its shared folders.");
            }
            finally
            {
                if (buffer != IntPtr.Zero) freeBuffer(buffer);
            }
        }
    }
}
