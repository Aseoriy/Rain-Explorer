using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ApplicationIconTests
{
    private const string ExpectedIconSha256 =
        "554013891F9E3F50F2993CE8ACF636CB80457A9CF8A81099F69A0C675A7720A0";
    private const int ExpectedIconBytes = 381_038;
    private static readonly int[] ExpectedSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    [TestMethod]
    public void SourceIconIsDeterministicMultiResolutionAssetWithBoundedPixels()
    {
        var repository = GetRepositoryRoot();
        var projectPath = Path.Combine(
            repository,
            "src",
            "RainArchives.App",
            "RainArchives.App.csproj");
        var project = XDocument.Load(projectPath, LoadOptions.PreserveWhitespace);
        var iconBindings = project
            .Descendants("ApplicationIcon")
            .Select(element => element.Value)
            .ToArray();
        Assert.HasCount(1, iconBindings);
        Assert.AreEqual(@"Assets\RainArchives.ico", iconBindings[0]);
        Assert.IsFalse(Path.IsPathRooted(iconBindings[0]), "The icon binding must remain project-relative.");

        var iconPath = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(projectPath)!,
            iconBindings[0]));
        Assert.IsTrue(
            iconPath.StartsWith(
                Path.GetDirectoryName(projectPath)! + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase),
            "The icon binding escaped the App project.");
        var payload = File.ReadAllBytes(iconPath);
        Assert.AreEqual(ExpectedIconBytes, payload.Length);
        Assert.AreEqual(ExpectedIconSha256, Convert.ToHexString(SHA256.HashData(payload)));

        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream);
        Assert.AreEqual((ushort)0, reader.ReadUInt16(), "ICO reserved field changed.");
        Assert.AreEqual((ushort)1, reader.ReadUInt16(), "ICO type is not an icon.");
        Assert.AreEqual((ushort)ExpectedSizes.Length, reader.ReadUInt16(), "ICO frame count changed.");

        var entries = new List<IconDirectoryEntry>();
        foreach (var expectedSize in ExpectedSizes)
        {
            var width = DecodeDimension(reader.ReadByte());
            var height = DecodeDimension(reader.ReadByte());
            Assert.AreEqual(expectedSize, width);
            Assert.AreEqual(expectedSize, height);
            Assert.AreEqual((byte)0, reader.ReadByte(), "ICO color-count field changed.");
            Assert.AreEqual((byte)0, reader.ReadByte(), "ICO entry reserved field changed.");
            Assert.AreEqual((ushort)1, reader.ReadUInt16(), "ICO frame plane count changed.");
            Assert.AreEqual((ushort)32, reader.ReadUInt16(), "ICO frame must remain 32-bit.");
            entries.Add(new IconDirectoryEntry(
                expectedSize,
                reader.ReadUInt32(),
                reader.ReadUInt32()));
        }

        long expectedOffset = 6 + (16 * ExpectedSizes.Length);
        foreach (var entry in entries)
        {
            Assert.AreEqual(expectedOffset, (long)entry.Offset, "ICO frame offsets are not tightly deterministic.");
            AssertFrame(reader, entry);
            expectedOffset += entry.Bytes;
        }

        Assert.AreEqual(expectedOffset, payload.LongLength, "ICO payload has trailing or missing bytes.");
    }

    [TestMethod]
    public void BuiltApplicationExecutableExposesSmallAndLargeWindowsIcons()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows shell icon extraction is Windows-specific.");
        }

        var executable = Path.Combine(AppContext.BaseDirectory, "RainArchives.exe");
        Assert.IsTrue(File.Exists(executable), "The unit-test output does not contain RainArchives.exe.");
        Assert.IsGreaterThan(0u, ExtractIconEx(executable, -1, null, null, 0),
            "The built executable has no icon group.");

        var large = new IntPtr[1];
        var small = new IntPtr[1];
        Assert.IsGreaterThan(0u, ExtractIconEx(executable, 0, large, small, 1),
            "Windows could not extract the App icon group.");
        Assert.AreNotEqual(IntPtr.Zero, large[0], "Windows did not return a large App icon.");
        Assert.AreNotEqual(IntPtr.Zero, small[0], "Windows did not return a small App icon.");

        try
        {
            var largeSize = GetIconDimensions(large[0]);
            var smallSize = GetIconDimensions(small[0]);
            Assert.IsTrue(largeSize.Width >= smallSize.Width);
            Assert.IsTrue(largeSize.Height >= smallSize.Height);
            Assert.IsGreaterThan(0, smallSize.Width);
            Assert.IsGreaterThan(0, smallSize.Height);
            Assert.AreEqual(largeSize.Width, largeSize.Height, "The extracted large icon is not square.");
            Assert.AreEqual(smallSize.Width, smallSize.Height, "The extracted small icon is not square.");
        }
        finally
        {
            _ = DestroyIcon(large[0]);
            _ = DestroyIcon(small[0]);
        }
    }

    [TestMethod]
    public void BuiltApplicationWindowUsesExecutableIconWithoutXamlOverride()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows caption icon inspection is Windows-specific.");
        }

        var repository = GetRepositoryRoot();
        var mainWindow = XDocument.Load(Path.Combine(
            repository,
            "src",
            "RainArchives.App",
            "MainWindow.xaml"));
        Assert.IsNull(mainWindow.Root?.Attribute("Icon"),
            "MainWindow must use the single executable icon instead of a duplicate XAML binding.");
        Assert.HasCount(0, mainWindow.Descendants().Where(element => element.Name.LocalName == "Window.Icon"));

        var executable = Path.Combine(AppContext.BaseDirectory, "RainArchives.exe");
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
        Assert.IsNotNull(process);

        try
        {
            Assert.IsTrue(process.WaitForInputIdle(5_000), "The WPF App did not create its window in time.");
            var window = WaitForTopLevelWindow(process, TimeSpan.FromSeconds(5));
            Assert.AreNotEqual(IntPtr.Zero, window, "The WPF App did not expose a top-level window.");
            Assert.AreNotEqual(IntPtr.Zero, ResolveWindowIcon(window, large: true),
                "ApplicationIcon did not supply the large caption/taskbar icon.");
            Assert.AreNotEqual(IntPtr.Zero, ResolveWindowIcon(window, large: false),
                "ApplicationIcon did not supply the small caption icon.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(5_000))
                {
                    process.Kill(entireProcessTree: true);
                    Assert.IsTrue(process.WaitForExit(5_000),
                        "The test-owned WPF App process did not terminate.");
                }
            }
        }
    }

    private static void AssertFrame(BinaryReader reader, IconDirectoryEntry entry)
    {
        var size = entry.Size;
        var maskStride = ((size + 31) / 32) * 4;
        var xorBytes = checked(size * size * 4);
        Assert.AreEqual((uint)(40 + xorBytes + (maskStride * size)), entry.Bytes);
        Assert.IsTrue((ulong)entry.Offset + entry.Bytes <= (ulong)reader.BaseStream.Length,
            "ICO frame exceeds the asset boundary.");
        reader.BaseStream.Position = entry.Offset;
        Assert.AreEqual(40u, reader.ReadUInt32(), "ICO DIB header size changed.");
        Assert.AreEqual(size, reader.ReadInt32(), "ICO DIB width changed.");
        Assert.AreEqual(size * 2, reader.ReadInt32(), "ICO DIB height must include the XOR and AND planes.");
        Assert.AreEqual((ushort)1, reader.ReadUInt16(), "ICO DIB plane count changed.");
        Assert.AreEqual((ushort)32, reader.ReadUInt16(), "ICO DIB bit depth changed.");
        Assert.AreEqual(0u, reader.ReadUInt32(), "ICO DIB must remain uncompressed BI_RGB.");
        Assert.AreEqual((uint)xorBytes, reader.ReadUInt32(), "ICO DIB XOR byte count changed.");
        Assert.AreEqual(0, reader.ReadInt32());
        Assert.AreEqual(0, reader.ReadInt32());
        Assert.AreEqual(0u, reader.ReadUInt32());
        Assert.AreEqual(0u, reader.ReadUInt32());

        var alphaValues = new byte[size * size];
        var transparent = 0;
        var opaque = 0;
        var partial = 0;
        var violet = 0;
        var softLight = 0;
        var nearBlack = 0;
        var minX = size;
        var minY = size;
        var maxX = -1;
        var maxY = -1;
        for (var storedY = 0; storedY < size; storedY++)
        {
            var y = size - 1 - storedY;
            for (var x = 0; x < size; x++)
            {
                var blue = reader.ReadByte();
                var green = reader.ReadByte();
                var red = reader.ReadByte();
                var alpha = reader.ReadByte();
                alphaValues[(y * size) + x] = alpha;
                if (alpha == 0)
                {
                    transparent++;
                    continue;
                }

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
                if (alpha == byte.MaxValue)
                {
                    opaque++;
                }
                else
                {
                    partial++;
                }

                if (alpha >= 192 && red >= 225 && green >= 220 && blue >= 225)
                {
                    softLight++;
                }
                else if (alpha >= 192 && blue >= 160 && red >= 80 && green <= 150)
                {
                    violet++;
                }
                else if (alpha >= 128 && red <= 80 && green <= 80 && blue <= 90)
                {
                    nearBlack++;
                }
            }
        }

        Assert.IsGreaterThan(0, transparent, $"{size}px frame lost transparent pixels.");
        Assert.IsGreaterThan(0, opaque, $"{size}px frame lost opaque pixels.");
        Assert.IsGreaterThan(0, partial, $"{size}px frame lost anti-aliased alpha.");
        Assert.IsGreaterThan(size, softLight, $"{size}px frame lost the light archive glyph.");
        Assert.IsGreaterThan(size, violet, $"{size}px frame lost the Rain violet tile.");
        Assert.IsGreaterThan(0, nearBlack, $"{size}px frame lost the near-black edge.");
        Assert.IsTrue(minX >= 0);
        Assert.IsTrue(minY >= 0);
        Assert.IsTrue(maxX < size);
        Assert.IsTrue(maxY < size);
        Assert.IsTrue(
            Math.Abs(minX - (size - 1 - maxX)) <= 1,
            $"{size}px frame is not horizontally balanced.");
        Assert.IsTrue(
            Math.Abs(minY - (size - 1 - maxY)) <= 1,
            $"{size}px frame is not vertically balanced.");

        for (var storedY = 0; storedY < size; storedY++)
        {
            var y = size - 1 - storedY;
            for (var byteIndex = 0; byteIndex < maskStride; byteIndex++)
            {
                var mask = reader.ReadByte();
                for (var bit = 0; bit < 8; bit++)
                {
                    var x = (byteIndex * 8) + bit;
                    var expected = x < size && alphaValues[(y * size) + x] < 128;
                    Assert.AreEqual(expected, (mask & (0x80 >> bit)) != 0,
                        $"{size}px AND mask disagrees with alpha at ({x}, {y}).");
                }
            }
        }
    }

    private static int DecodeDimension(byte value) => value == 0 ? 256 : value;

    private static IconSize GetIconDimensions(IntPtr icon)
    {
        Assert.IsTrue(GetIconInfo(icon, out var info), "GetIconInfo failed for an extracted App icon.");
        try
        {
            Assert.AreNotEqual(IntPtr.Zero, info.ColorBitmap, "The extracted App icon has no color bitmap.");
            Assert.AreEqual(Marshal.SizeOf<NativeBitmap>(), GetObject(
                info.ColorBitmap,
                Marshal.SizeOf<NativeBitmap>(),
                out var bitmap),
                "Windows could not inspect the extracted App icon bitmap.");
            return new IconSize(bitmap.Width, bitmap.Height);
        }
        finally
        {
            if (info.ColorBitmap != IntPtr.Zero)
            {
                _ = DeleteObject(info.ColorBitmap);
            }

            if (info.MaskBitmap != IntPtr.Zero)
            {
                _ = DeleteObject(info.MaskBitmap);
            }
        }
    }

    private static IntPtr ResolveWindowIcon(IntPtr window, bool large)
    {
        const uint wmGetIcon = 0x007F;
        const int iconSmall = 0;
        const int iconBig = 1;
        const int iconSmall2 = 2;
        const int classLargeIcon = -14;
        const int classSmallIcon = -34;

        if (!large)
        {
            var small = SendMessage(window, wmGetIcon, new IntPtr(iconSmall2), IntPtr.Zero);
            if (small == IntPtr.Zero)
            {
                small = SendMessage(window, wmGetIcon, new IntPtr(iconSmall), IntPtr.Zero);
            }

            return small != IntPtr.Zero ? small : GetClassLongPtr(window, classSmallIcon);
        }

        var icon = SendMessage(window, wmGetIcon, new IntPtr(iconBig), IntPtr.Zero);
        return icon != IntPtr.Zero ? icon : GetClassLongPtr(window, classLargeIcon);
    }

    private static IntPtr WaitForTopLevelWindow(Process process, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        do
        {
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                return process.MainWindowHandle;
            }

            if (process.HasExited)
            {
                break;
            }

            Thread.Sleep(50);
        }
        while (stopwatch.Elapsed < timeout);

        return IntPtr.Zero;
    }

    private static string GetRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "RainArchives.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("The Rain Archives repository root was not found.");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string file,
        int iconIndex,
        IntPtr[]? largeIcons,
        IntPtr[]? smallIcons,
        uint iconCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr icon, out NativeIconInfo iconInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(
        IntPtr window,
        uint message,
        IntPtr wordParameter,
        IntPtr longParameter);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW", SetLastError = true)]
    private static extern IntPtr GetClassLongPtr(IntPtr window, int index);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetObject(
        IntPtr nativeObject,
        int bytes,
        out NativeBitmap bitmap);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr nativeObject);

    private sealed record IconDirectoryEntry(int Size, uint Bytes, uint Offset);

    private sealed record IconSize(int Width, int Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeIconInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool IsIcon;
        public uint HotspotX;
        public uint HotspotY;
        public IntPtr MaskBitmap;
        public IntPtr ColorBitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPerPixel;
        public IntPtr Bits;
    }
}
