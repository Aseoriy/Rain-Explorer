using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using RainArchives.Contracts;

namespace RainArchives.Providers.WinRar;

public static partial class WinRarEncoderProbe
{
    public const string InstalledExecutablePath = @"C:\Program Files\WinRAR\Rar.exe";
    private const int MinimumMinorVersion = 23;
    private const ushort ImageFileMachineAmd64 = 0x8664;
    private const uint WinTrustUiNone = 2;
    private const uint WinTrustRevokeNone = 0;
    private const uint WinTrustChoiceFile = 1;
    private const uint WinTrustStateActionIgnore = 0;
    private const uint WinTrustCacheOnlyUrlRetrieval = 0x00001000;
    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static RarEncoderAvailability ProbeInstalled() =>
        Probe(InstalledExecutablePath, requireInstalledLocation: true);

    internal static RarEncoderAvailability ProbeVerifiedExecutable(string executablePath) =>
        Probe(executablePath, requireInstalledLocation: false);

    private static RarEncoderAvailability Probe(
        string executablePath,
        bool requireInstalledLocation)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return RarEncoderAvailability.UnsupportedPlatform;
        }

        try
        {
            var canonical = Path.GetFullPath(executablePath);
            if (requireInstalledLocation
                && !string.Equals(
                    canonical,
                    Path.GetFullPath(InstalledExecutablePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return RarEncoderAvailability.UntrustedBinary;
            }

            if (!File.Exists(canonical))
            {
                return requireInstalledLocation
                    ? RarEncoderAvailability.NotInstalled
                    : RarEncoderAvailability.UntrustedBinary;
            }

            var attributes = File.GetAttributes(canonical);
            if (attributes.HasFlag(FileAttributes.Directory)
                || attributes.HasFlag(FileAttributes.ReparsePoint)
                || !IsAmd64PortableExecutable(canonical)
                || !HasTrustedWinRarSignature(canonical))
            {
                return RarEncoderAvailability.UntrustedBinary;
            }

            var version = FileVersionInfo.GetVersionInfo(canonical);
            return version.FileMajorPart == 7
                && version.FileMinorPart >= MinimumMinorVersion
                ? RarEncoderAvailability.Available
                : RarEncoderAvailability.UpdateRequired;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or System.Security.Cryptography.CryptographicException)
        {
            return RarEncoderAvailability.UntrustedBinary;
        }
    }

    private static bool IsAmd64PortableExecutable(string executablePath)
    {
        using var stream = new FileStream(
            executablePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.RandomAccess);
        Span<byte> dosHeader = stackalloc byte[64];
        if (stream.Read(dosHeader) != dosHeader.Length
            || dosHeader[0] != (byte)'M'
            || dosHeader[1] != (byte)'Z')
        {
            return false;
        }

        var peOffset = BitConverter.ToInt32(dosHeader[0x3c..0x40]);
        if (peOffset < 64 || peOffset > stream.Length - 6)
        {
            return false;
        }

        stream.Position = peOffset;
        Span<byte> peHeader = stackalloc byte[6];
        return stream.Read(peHeader) == peHeader.Length
            && peHeader[0] == (byte)'P'
            && peHeader[1] == (byte)'E'
            && peHeader[2] == 0
            && peHeader[3] == 0
            && BitConverter.ToUInt16(peHeader[4..6]) == ImageFileMachineAmd64;
    }

    private static bool HasTrustedWinRarSignature(string executablePath)
    {
        if (!VerifyAuthenticodeTrust(executablePath))
        {
            return false;
        }

#pragma warning disable SYSLIB0057 // Authenticode signer extraction has no X509CertificateLoader equivalent.
        using var certificate = new X509Certificate2(
            X509Certificate.CreateFromSignedFile(executablePath));
#pragma warning restore SYSLIB0057
        return string.Equals(
            certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
            "win.rar GmbH",
            StringComparison.Ordinal);
    }

    private static bool VerifyAuthenticodeTrust(string executablePath)
    {
        var pathPointer = Marshal.StringToCoTaskMemUni(executablePath);
        var fileInfoPointer = IntPtr.Zero;
        try
        {
            var fileInfo = new WinTrustFileInfo
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = pathPointer,
            };
            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
            var trustData = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = WinTrustUiNone,
                RevocationChecks = WinTrustRevokeNone,
                UnionChoice = WinTrustChoiceFile,
                FileInfo = fileInfoPointer,
                StateAction = WinTrustStateActionIgnore,
                ProviderFlags = WinTrustCacheOnlyUrlRetrieval,
            };
            var action = WinTrustActionGenericVerifyV2;
            return WinVerifyTrust(IntPtr.Zero, ref action, ref trustData) == 0;
        }
        finally
        {
            if (fileInfoPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(fileInfoPointer);
            }

            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [LibraryImport("wintrust.dll", EntryPoint = "WinVerifyTrust")]
    private static partial int WinVerifyTrust(
        IntPtr windowHandle,
        ref Guid actionId,
        ref WinTrustData trustData);
}
