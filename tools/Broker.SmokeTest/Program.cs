using System.Diagnostics;
using EasyMouse.Core.Abstractions;
using EasyMouse.Core.Protocol;
using EasyMouse.Platform.Windows;
using EasyMouse.Platform.Windows.Broker;

// Manual smoke test for the SYSTEM broker. It talks to the broker exactly like the App does (BrokerInputSink: pipe owner check
// included), so it must run as the file "EasyMouse.exe" next to EasyMouse.Broker.exe, because the broker only accepts that image.
//
//   --status                   service state, whether the foreground window blocks normal input
//   --probe                    try normal SendInput (Shift tap) and the broker (Shift tap) and say what happened to each
//   --type "text" [--enter]    type through the broker (focus an elevated window first; use --delay 5 to get time)
//   --uac MARKER_FILE          start an elevated "cmd /c echo ok > MARKER_FILE" (Windows shows the UAC prompt), then press Alt+Y
//                              on the prompt through the broker. Succeeds if the marker file appears.
//   --delay SECONDS            wait before acting (to switch windows yourself)
var arguments = args.ToList();
string? Option(string name) { var i = arguments.IndexOf(name); return i >= 0 && i + 1 < arguments.Count ? arguments[i + 1] : null; }
bool Flag(string name) => arguments.Contains(name);

if (arguments.Count == 0)
{
    Console.WriteLine("Broker.SmokeTest: see the comment at the top of Program.cs and tools/Broker.SmokeTest/README.md");
    return 2;
}

var failures = 0;
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? ": " + detail : "")}");
    if (!ok) failures++;
}

if (Option("--acl") is { } aclPath)
{
    // Would the broker run from this file? (Under Program Files by path, and not writable by ordinary users by access list.)
    Console.WriteLine($"{aclPath}: safe to run as SYSTEM = {BrokerTrust.IsInstallLocationSafe(aclPath, [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)])}");
    return 0;
}

Console.WriteLine($"service: {BrokerService.Query()}");
Console.WriteLine($"foreground blocks normal input: {HostStatusMonitor.IsForegroundBlocked(selfElevated: false)}");
if (Flag("--wts"))
{
    // What the agent sees when it asks Terminal Services whether this session is locked (1 = unlocked, 0 = locked). Run it once normally;
    // to check the locked case, run: Start-Sleep 8; <this command>  from a prompt, then lock the PC with Win+L before the 8 s are over.
    var session = Process.GetCurrentProcess().SessionId;
    Console.WriteLine(WtsProbe.Describe((uint)session));
    return 0;
}
if (Flag("--status")) return 0;

if (int.TryParse(Option("--delay"), out var delay) && delay > 0)
{
    Console.WriteLine($"waiting {delay} s ...");
    Thread.Sleep(delay * 1000);
    Console.WriteLine($"foreground blocks normal input now: {HostStatusMonitor.IsForegroundBlocked(selfElevated: false)}");
}

using var broker = new BrokerInputSink();

if (Flag("--probe"))
{
    using var normal = new SendInputSink();
    string Try(Action a)
    {
        try { a(); return "ok"; }
        catch (InputBlockedException ex) { return "blocked: " + ex.Message; }
    }
    Console.WriteLine("normal SendInput, Shift tap: " + Try(() => normal.Key(0xA0, KeyAction.Press)));
    var viaBroker = Try(() => broker.Key(0xA0, KeyAction.Press));
    Console.WriteLine("broker, Shift tap: " + viaBroker);
    Check("broker accepts input", viaBroker == "ok", viaBroker);
}

if (Option("--type") is { } text)
{
    try
    {
        broker.Text(text);
        if (Flag("--enter")) broker.Key(0x0D, KeyAction.Press);
        Check($"typed {text.Length} characters through the broker", true);
    }
    catch (InputBlockedException ex) { Check("typing through the broker", false, ex.Message); }
}

if (Option("--uac") is { } marker)
{
    File.Delete(marker);
    var psi = new ProcessStartInfo("cmd.exe", $"/c echo ok > \"{marker}\"") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
    using var started = Process.Start(psi); // blocks only until Windows has shown the prompt; the prompt is on the secure desktop
    Console.WriteLine("UAC prompt requested; waiting for the secure desktop ...");
    var deadline = Environment.TickCount64 + 10_000;
    while (Environment.TickCount64 < deadline && !HostStatusMonitor.IsInputDesktopUnavailable()) Thread.Sleep(50);
    Check("the secure desktop is up (normal input cannot reach it)", HostStatusMonitor.IsInputDesktopUnavailable());
    Thread.Sleep(700); // let the consent UI settle
    try
    {
        broker.Key(0x12, KeyAction.Down); // Alt
        broker.Key((ushort)'Y', KeyAction.Press);
        broker.Key(0x12, KeyAction.Up);
        Check("Alt+Y sent to the UAC prompt through the broker", true);
    }
    catch (InputBlockedException ex) { Check("pressing Alt+Y through the broker", false, ex.Message); }
    deadline = Environment.TickCount64 + 10_000;
    while (Environment.TickCount64 < deadline && !File.Exists(marker)) Thread.Sleep(100);
    Check("the elevated command ran (the UAC prompt was approved by the broker)", File.Exists(marker));
}

Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;

internal static partial class WtsProbe
{
    [System.Runtime.InteropServices.LibraryImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool Query(nint server, uint session, int infoClass, out nint buffer, out uint bytes);

    [System.Runtime.InteropServices.LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(nint memory);

    public static string Describe(uint session)
    {
        if (!Query(0, session, 25, out var buffer, out var bytes)) return "WTSQuerySessionInformation(WTSSessionInfoEx) failed";
        try
        {
            var level = System.Runtime.InteropServices.Marshal.ReadInt32(buffer, 0);
            var flags = System.Runtime.InteropServices.Marshal.ReadInt32(buffer, 16);
            return $"session {session}: level={level} bytes={bytes} SessionFlags={flags} ({(flags == 1 ? "unlocked" : flags == 0 ? "LOCKED" : "unknown")})";
        }
        finally { WTSFreeMemory(buffer); }
    }
}
