using System.Runtime.Versioning;

// This is the Linux build of Rain Explorer; Unix-only APIs (file modes, statx) are expected.
[assembly: SupportedOSPlatform("linux")]

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("RainExplorer.Tests")]
