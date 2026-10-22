using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

// Every native import in Rain Explorer targets a Windows system library. Restrict
// resolution to System32 so a same-named DLL beside a browsed file cannot be loaded.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
[assembly: InternalsVisibleTo("RainExplorer.Git.Tests")]
