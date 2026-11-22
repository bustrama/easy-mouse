using System.Diagnostics;
using System.Runtime.InteropServices;
using EasyMouse.Core.Abstractions;
using EasyMouse.Core.Protocol;
using EasyMouse.Platform.Windows;
using Windows.Media.Control;

// Non-destructive checks of the real Windows implementations. Does nothing unless --run is given.
if (!args.Contains("--run"))
{
    Console.WriteLine("Platform.SmokeTest: read-only volume/SMTC checks, moves the cursor 2 px and back, taps Shift.");
    Console.WriteLine("Run with: dotnet run --project tools/Platform.SmokeTest -- --run");
    return 0;
}

var failures = 0;
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? ": " + detail : "")}");
    if (!ok) failures++;
}

Console.WriteLine("== Volume and SMTC (WindowsMediaController)");
using var media = new WindowsMediaController();
var changes = 0;
media.Changed += (_, _) => Interlocked.Increment(ref changes);
await media.Initialization.WaitAsync(TimeSpan.FromSeconds(10));
await Task.Delay(1000);
var snap = media.GetSnapshot();
Console.WriteLine($"Volume: {(snap.Volume is { } v ? $"{v.Level}% muted={v.Muted}" : "no playback device")}");
Check("volume readable", snap.Volume is not null);

var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
var sessions = manager.GetSessions();
Console.WriteLine($"SMTC sessions: {sessions.Count}");
foreach (var s in sessions)
{
    var info = s.GetPlaybackInfo();
    var props = await s.TryGetMediaPropertiesAsync();
    var tl = s.GetTimelineProperties();
    Console.WriteLine($"  {s.SourceAppUserModelId}: {info.PlaybackStatus} | '{props?.Title}' by '{props?.Artist}' | pos {tl?.Position} / end {tl?.EndTime} (updated {tl?.LastUpdatedTime:O})");
}
var np = snap.NowPlaying;
Console.WriteLine(np is null ? "Selected now-playing: none" : $"Selected now-playing: {np.Status} '{np.Title}' by '{np.Artist}' pos={np.PositionMs}ms dur={np.DurationMs}ms");
if (np is { Status: PlaybackStatus.Playing, PositionMs: not null })
{
    await Task.Delay(2000);
    var later = media.GetSnapshot().NowPlaying;
    Console.WriteLine($"2 s later: pos={later?.PositionMs}ms");
    Check("position extrapolates while playing", later?.PositionMs > np.PositionMs);
}
Console.WriteLine($"Changed events seen: {changes}");

Console.WriteLine("== Host status (HostStatusMonitor)");
var status = new HostStatus();
using (var monitor = new HostStatusMonitor(status))
{
    Console.WriteLine($"Locked={status.Locked} Elevated(foreground blocks input)={status.Elevated}");
    Check("not locked while interactive", !status.Locked);
}
Console.WriteLine($"Foreground blocked (non-elevated us): {HostStatusMonitor.IsForegroundBlocked(selfElevated: false)}");
foreach (var name in new[] { "explorer", "winlogon", "services" })
{
    var p = Process.GetProcessesByName(name).FirstOrDefault();
    if (p is not null) Console.WriteLine($"  {name} (pid {p.Id}): {HostStatusMonitor.ElevationOfProcess((uint)p.Id)}");
}

Console.WriteLine("== Input (SendInputSink)");
using var sink = new SendInputSink();
GetCursorPos(out var start);
sink.MoveRelative(2, 0);
GetCursorPos(out var moved);
sink.MoveRelative(-2, 0);
GetCursorPos(out var back);
SetCursorPos(start.X, start.Y); // pointer acceleration can make the relative return inexact; restore exactly
Console.WriteLine($"Cursor {start.X},{start.Y} -> {moved.X},{moved.Y} -> {back.X},{back.Y} (restored to {start.X},{start.Y})");
Check("cursor moved right", moved.X > start.X);
sink.Wheel(0, 0); // no-op batch must not throw

sink.Key(0x10, KeyAction.Down);
var heldDown = (GetAsyncKeyState(0x10) & 0x8000) != 0;
sink.Key(0x10, KeyAction.Up);
var released = (GetAsyncKeyState(0x10) & 0x8000) == 0;
Check("Shift reads as down while held", heldDown);
Check("Shift reads as up after release", released);

sink.Key(0x10, KeyAction.Down);
sink.ReleaseAll();
Check("ReleaseAll lifts a held Shift", (GetAsyncKeyState(0x10) & 0x8000) == 0);

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

[DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
[DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
[StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
