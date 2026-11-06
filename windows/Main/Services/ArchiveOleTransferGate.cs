using System.Runtime.InteropServices;
using System.Windows;

namespace RainExplorer.Services;

/// <summary>
/// M6-E's production WPF/OLE surface is intentionally fixed off. WPF's data
/// object wrapper does not expose the native asynchronous-operation contract,
/// so synchronous GetData could block the STA and no target-consumption lifetime
/// signal is available for exact cleanup.
/// </summary>
internal static class ArchiveOleTransferGate
{
    internal const bool IsEnabled = false;
    internal static readonly bool HasWorkerPublicationIdentity = false;
    internal const string DisabledReason =
        "Copying archive selections is disabled because protocol 1.22 does not bind the committed "
        + "destination identity and WPF does not expose a proven IDataObjectAsyncCapability "
        + "StartOperation/InOperation/EndOperation lifetime.";
    internal static readonly Guid DataObjectAsyncCapabilityInterfaceId =
        new("3D8B0590-F691-11D2-8EA9-006097DF5BD4");

    public static bool TryCreateDataObject(
        ArchiveTransferOwner owner,
        out DataObject? dataObject)
    {
        ArgumentNullException.ThrowIfNull(owner);
        dataObject = null;
        return false;
    }

    internal static int QueryAsyncCapability(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        IntPtr unknown = IntPtr.Zero;
        IntPtr capability = IntPtr.Zero;
        try
        {
            unknown = Marshal.GetIUnknownForObject(value);
            Guid interfaceId = DataObjectAsyncCapabilityInterfaceId;
            return Marshal.QueryInterface(unknown, in interfaceId, out capability);
        }
        finally
        {
            if (capability != IntPtr.Zero) _ = Marshal.Release(capability);
            if (unknown != IntPtr.Zero) _ = Marshal.Release(unknown);
        }
    }
}
