using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

// Run this executable from Rain Explorer and from Windows Explorer, using the same
// Node installation. It only reads process policies and writes an isolated fixture
// and report under TEMP. It never changes mitigations, registry keys, or user data.
bool inspect = args.Length > 0 && args[0] == "--inspect";
string node = args.Length > 0 && !inspect ? Path.GetFullPath(args[0]) : Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
var samples = new List<object>();
bool queryFailed = false;
void Sample(int id, string label)
{
    try
    {
        using var process = Process.GetProcessById(id);
        // Access failures are errors, never a false "disabled" result.
        if (!Native.GetProcessMitigationPolicy(process.Handle, 16, out uint flags, (nuint)sizeof(uint)))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        samples.Add(new { label, pid = id, image = process.MainModule?.FileName,
            startUtc = process.StartTime.ToUniversalTime(), flags,
            enforceRedirectionTrust = (flags & 1) != 0, auditRedirectionTrust = (flags & 2) != 0 });
    }
    catch (Exception ex) { queryFailed = true; samples.Add(new { label, pid = id, error = ex.Message }); }
}
Sample(Environment.ProcessId, "diagnostic");
if (inspect)
{
    foreach (string value in args.Skip(1)) Sample(int.Parse(value), "requested");
    Console.WriteLine(JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
    Environment.ExitCode = queryFailed ? 2 : 0;
    return;
}
string root = Path.Combine(Path.GetTempPath(), "RainExplorer-Mitigation-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string script = Path.Combine(root, "probe.cjs");
File.WriteAllText(script, """
    const fs = require('node:fs');
    const path = require('node:path');
    const root = __dirname;
    const result = { pid: process.pid, ppid: process.ppid, node: process.execPath };
    try {
      fs.mkdirSync(path.join(root, 'target'));
      fs.writeFileSync(path.join(root, 'target', 'probe.txt'), 'Rain Explorer junction probe');
      fs.symlinkSync(path.join(root, 'target'), path.join(root, 'junction'), 'junction');
      result.content = fs.readFileSync(path.join(root, 'junction', 'probe.txt'), 'utf8');
      result.junctionRead = result.content === 'Rain Explorer junction probe';
    } catch (e) { result.junctionRead = false; result.error = String(e); result.code = e.code; }
    fs.writeFileSync(path.join(root, 'node.json.tmp'), JSON.stringify(result));
    fs.renameSync(path.join(root, 'node.json.tmp'), path.join(root, 'node.json'));
    const timer = setInterval(() => {
      if (fs.existsSync(path.join(root, 'done'))) { clearInterval(timer); }
    }, 100);
    setTimeout(() => { clearInterval(timer); }, 15000).unref();
    """);
foreach (var process in Process.GetProcessesByName("RainExplorer"))
{
    using (process) Sample(process.Id, "RainExplorer");
}
foreach (var process in Process.GetProcessesByName("explorer"))
{
    using (process) Sample(process.Id, "Windows Explorer");
}
object? nodeResult = null;
string? error = null;
bool junctionRead = false;
using var identity = WindowsIdentity.GetCurrent();
bool elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
try
{
    if (elevated) throw new InvalidOperationException("Run unelevated so the fixture tests a user-created junction.");
    if (!File.Exists(node)) throw new FileNotFoundException("Pass the absolute path to node.exe.", node);
    if (node.IndexOfAny(['\"', '%', '\r', '\n']) >= 0 || script.IndexOfAny(['\"', '%', '\r', '\n']) >= 0)
        throw new ArgumentException("The diagnostic paths cannot contain CMD expansion or quoting characters.");
    // The same shell-execute mechanism used by Rain Explorer's terminal and address bar.
    // No STARTUPINFOEX, parent substitution, or mitigation override is applied.
    var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
    {
        UseShellExecute = true, WorkingDirectory = root, WindowStyle = ProcessWindowStyle.Hidden,
        Arguments = $"/d /s /c \"\"{node}\" \"{script}\"\"",
    };
    using var cmd = Process.Start(start) ?? throw new InvalidOperationException("CMD did not start.");
    string nodeReport = Path.Combine(root, "node.json");
    var wait = Stopwatch.StartNew();
    while (!File.Exists(nodeReport) && !cmd.HasExited && wait.Elapsed < TimeSpan.FromSeconds(12))
        Thread.Sleep(50);
    Sample(cmd.Id, "CMD");
    using var json = JsonDocument.Parse(File.ReadAllText(nodeReport));
    nodeResult = json.RootElement.Clone();
    junctionRead = json.RootElement.GetProperty("junctionRead").GetBoolean();
    Sample(json.RootElement.GetProperty("pid").GetInt32(), "Node");
    File.WriteAllText(Path.Combine(root, "done"), "done");
    if (!cmd.WaitForExit(5000)) error = "CMD did not exit within five seconds; Node has a bounded timer.";
}
catch (Exception ex) { error = ex.ToString(); }
string report = Path.Combine(root, "report.json");
File.WriteAllText(report, JsonSerializer.Serialize(new { utc = DateTime.UtcNow,
    os = Environment.OSVersion.VersionString, runtime = Environment.Version.ToString(),
    elevated, samples, nodeResult, error }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(report);
Environment.ExitCode = error is not null || queryFailed ? 2 : junctionRead ? 0 : 1;

internal static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessMitigationPolicy(nint process, int policy, out uint flags, nuint size);
}
