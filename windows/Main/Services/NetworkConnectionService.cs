using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RainExplorer.Services;

internal static class NetworkConnectionService
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetworkResource
    {
        public uint Scope, Type, DisplayType, Usage;
        [MarshalAs(UnmanagedType.LPWStr)] public string? LocalName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? RemoteName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Provider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WNetAddConnection3W(IntPtr owner, ref NetworkResource resource,
        string? password, string? userName, uint flags);

    internal static bool RequiresSignIn(Exception error)
    {
        int code = error is Win32Exception native ? native.NativeErrorCode : error.HResult & 0xFFFF;
        return code is 5 or 86 or 1326;
    }

    public static async Task<bool> SignInAsync(string path, IntPtr owner, CancellationToken ct)
    {
        string remote;
        if (NetworkPath.TryGetServerRoot(path, out string server)) remote = server + @"\IPC$";
        else if (NetworkPath.TryGetShareRoot(path, out string share)) remote = share;
        else return false;

        ct.ThrowIfCancellationRequested();
        int result = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var resource = new NetworkResource { RemoteName = remote };
            // Windows owns the credential dialog. No drive mapping, stored password,
            // persistent profile entry, or disconnection of an existing session.
            return WNetAddConnection3W(owner, ref resource, null, null, 0x08 | 0x10);
        }, ct);
        ct.ThrowIfCancellationRequested();
        if (result == 1223) return false; // User cancelled the Windows dialog.
        if (result != 0) throw new Win32Exception(result);
        return true;
    }
}
