using System.Diagnostics;
using EasyMouse.Core.Abstractions;
using EasyMouse.Platform.Windows.Remotes;
using EasyMouse.Remotes.Execution;
using EasyMouse.Remotes.Model;

namespace EasyMouse.Platform.Windows.Tests;

public class RemoteExecutorTests
{
    private static ActionContext Ctx(string? target = null, double? value = null) => new("r", target, value);

    // ---------------------------------------------------------------- process patterns and focus

    [Theory]
    [InlineData("stremio*.exe", "stremio.exe", true)]
    [InlineData("stremio*.exe", "Stremio-Runtime.EXE", true)]
    [InlineData("vlc.exe", "vlc.exe", true)]
    [InlineData("vlc", "vlc.exe", true)]
    [InlineData("vlc.exe", "notvlc.exe", false)]
    [InlineData("vl?.exe", "vlc.exe", true)]
    [InlineData("vlc.exe", "vlc.exe.bak", false)]
    public void Process_patterns(string pattern, string exe, bool expected) => Assert.Equal(expected, ProcessPattern.IsMatch(pattern, exe));

    private sealed class FakeWindows : IWindowSystem
    {
        public List<WindowInfo> Windows { get; } = [];
        public nint Fg { get; set; }
        public HashSet<nint> Minimized { get; } = [];
        public List<string> Calls { get; } = [];
        /// <summary>When true, RequestForeground is refused (Windows' foreground lock).</summary>
        public bool Refuse { get; set; }

        public IReadOnlyList<WindowInfo> MainWindows() => Windows;
        public nint Foreground() => Fg;
        public bool IsMinimized(nint h) => Minimized.Contains(h);
        public void Restore(nint h) { Calls.Add($"restore {h}"); Minimized.Remove(h); }
        public void RequestForeground(nint h) { Calls.Add($"fg {h}"); if (!Refuse) Fg = h; }
    }

    private static FocusExecutor Focus(FakeWindows w) => new(w, TimeSpan.FromMilliseconds(120));

    [Fact]
    public async Task Focus_brings_the_matching_window_forward()
    {
        var w = new FakeWindows { Fg = 1 };
        w.Windows.AddRange([new(1, 10, "chrome.exe", "x"), new(2, 20, "stremio.exe", "Stremio")]);
        await Focus(w).ExecuteAsync(new FocusAction(), Ctx("stremio*.exe"), CancellationToken.None);
        Assert.Equal(2, w.Fg);
        Assert.Equal(["fg 2"], w.Calls);
    }

    [Fact]
    public async Task Focus_does_nothing_when_the_target_is_already_in_front_and_prefers_it_over_other_matches()
    {
        var w = new FakeWindows { Fg = 3 };
        w.Windows.AddRange([new(2, 20, "vlc.exe", "a"), new(3, 21, "vlc.exe", "b")]);
        await Focus(w).ExecuteAsync(new FocusAction(), Ctx("vlc.exe"), CancellationToken.None);
        Assert.Empty(w.Calls);
    }

    [Fact]
    public async Task Focus_restores_only_a_minimised_window()
    {
        var w = new FakeWindows { Fg = 1 };
        w.Windows.AddRange([new(2, 20, "vlc.exe", "a")]);
        w.Minimized.Add(2);
        await Focus(w).ExecuteAsync(new FocusAction(), Ctx("vlc.exe"), CancellationToken.None);
        Assert.Equal(["restore 2", "fg 2"], w.Calls);

        var maximised = new FakeWindows { Fg = 1 };
        maximised.Windows.Add(new(5, 20, "vlc.exe", "a")); // not minimised: must not be restored (that would un-maximise or leave fullscreen)
        await Focus(maximised).ExecuteAsync(new FocusAction(), Ctx("vlc.exe"), CancellationToken.None);
        Assert.Equal(["fg 5"], maximised.Calls);
    }

    [Fact]
    public async Task Focus_fails_closed_when_the_program_is_not_running_or_windows_refuses()
    {
        var none = new FakeWindows { Fg = 1 };
        none.Windows.Add(new(1, 10, "chrome.exe", "x"));
        var ex = await Assert.ThrowsAsync<ActionFailedException>(() => Focus(none).ExecuteAsync(new FocusAction(), Ctx("vlc.exe"), CancellationToken.None));
        Assert.Equal("failed", ex.Code);

        var refused = new FakeWindows { Fg = 1, Refuse = true };
        refused.Windows.AddRange([new(1, 10, "chrome.exe", "x"), new(2, 20, "vlc.exe", "a")]);
        ex = await Assert.ThrowsAsync<ActionFailedException>(() => Focus(refused).ExecuteAsync(new FocusAction(), Ctx("vlc.exe"), CancellationToken.None));
        Assert.Equal("failed", ex.Code); // so the keys that follow never reach Chrome
        Assert.Equal(1, refused.Fg);
    }

    [Fact]
    public async Task Focus_without_a_target_is_unsupported()
    {
        var ex = await Assert.ThrowsAsync<ActionFailedException>(() => Focus(new FakeWindows()).ExecuteAsync(new FocusAction(), Ctx(null), CancellationToken.None));
        Assert.Equal("unsupported", ex.Code);
    }

    [Fact]
    public void The_real_window_enumeration_runs_and_finds_windows()
    {
        // Headless CI has no windows; on a desktop there are some. Either way this must not throw.
        var windows = new Win32WindowSystem().MainWindows();
        Assert.All(windows, w => Assert.False(string.IsNullOrEmpty(w.ExeName)));
    }

    // ---------------------------------------------------------------- volume and smtc

    private sealed class FakeVolume : IVolumeControl
    {
        public VolumeState? State { get; set; } = new(50, false);
        public VolumeState? GetVolume() => State;
        public void SetVolume(int percent) => State = new(percent, State?.Muted ?? false);
        public void SetMuted(bool muted) => State = new(State?.Level ?? 0, muted);
    }

    [Fact]
    public async Task Volume_set_up_down_and_mute()
    {
        var v = new FakeVolume();
        var exec = new VolumeExecutor(v);
        await exec.ExecuteAsync(new VolumeAction(VolumeScope.Master, VolumeCmd.Set, null, 2), Ctx(value: 37), CancellationToken.None);
        Assert.Equal(37, v.State!.Level);
        await exec.ExecuteAsync(new VolumeAction(VolumeScope.Master, VolumeCmd.Set, 80, 2), Ctx(), CancellationToken.None);
        Assert.Equal(80, v.State.Level);
        await exec.ExecuteAsync(new VolumeAction(VolumeScope.Master, VolumeCmd.Up, null, 5), Ctx(), CancellationToken.None);
        Assert.Equal(85, v.State.Level);
        await exec.ExecuteAsync(new VolumeAction(VolumeScope.Master, VolumeCmd.Down, null, 90), Ctx(), CancellationToken.None);
        Assert.Equal(0, v.State.Level);
        await exec.ExecuteAsync(new VolumeAction(VolumeScope.Master, VolumeCmd.ToggleMute, null, 2), Ctx(), CancellationToken.None);
        Assert.True(v.State.Muted);
        await exec.ExecuteAsync(new VolumeAction(VolumeScope.Master, VolumeCmd.Unmute, null, 2), Ctx(), CancellationToken.None);
        Assert.False(v.State.Muted);
    }

    [Fact]
    public async Task Volume_refuses_per_app_scope_and_a_missing_level()
    {
        var exec = new VolumeExecutor(new FakeVolume());
        var ex = await Assert.ThrowsAsync<ActionFailedException>(() => exec.ExecuteAsync(new VolumeAction(VolumeScope.App, VolumeCmd.Up, null, 2), Ctx(), CancellationToken.None));
        Assert.Equal("unsupported", ex.Code);
        ex = await Assert.ThrowsAsync<ActionFailedException>(() => exec.ExecuteAsync(new VolumeAction(VolumeScope.Master, VolumeCmd.Set, null, 2), Ctx(), CancellationToken.None));
        Assert.Equal("unsupported", ex.Code);
    }

    private sealed class FakeSmtc : ISmtcControl
    {
        public bool Accept { get; set; } = true;
        public List<(SmtcTransport, int)> Sent { get; } = [];
        public Task<bool> SendAsync(SmtcTransport command, int seconds, CancellationToken ct) { Sent.Add((command, seconds)); return Task.FromResult(Accept); }
    }

    [Fact]
    public async Task Smtc_maps_commands_and_reports_a_refusal()
    {
        var s = new FakeSmtc();
        var exec = new SmtcExecutor(s);
        await exec.ExecuteAsync(new SmtcAction(SmtcCmd.Toggle, 0), Ctx(), CancellationToken.None);
        await exec.ExecuteAsync(new SmtcAction(SmtcCmd.Prev, 0), Ctx(), CancellationToken.None);
        await exec.ExecuteAsync(new SmtcAction(SmtcCmd.SeekBack, 15), Ctx(), CancellationToken.None);
        Assert.Equal([(SmtcTransport.Toggle, 0), (SmtcTransport.Previous, 0), (SmtcTransport.SeekBack, 15)], s.Sent);

        s.Accept = false;
        var ex = await Assert.ThrowsAsync<ActionFailedException>(() => exec.ExecuteAsync(new SmtcAction(SmtcCmd.Next, 0), Ctx(), CancellationToken.None));
        Assert.Equal("failed", ex.Code);
    }

    // ---------------------------------------------------------------- launch and openUrl

    private sealed class RecordingStarter : IProcessStarter
    {
        public List<ProcessStartInfo> Started { get; } = [];
        public void Start(ProcessStartInfo info) => Started.Add(info);
    }

    [Fact]
    public async Task Launch_starts_only_an_existing_exe_without_a_shell_and_one_argument_per_entry()
    {
        var starter = new RecordingStarter();
        var exec = new LaunchExecutor(starter);
        var exe = Environment.ProcessPath!; // dotnet.exe or the test host: a real .exe on disk
        Assert.EndsWith(".exe", exe, StringComparison.OrdinalIgnoreCase);

        await exec.ExecuteAsync(new ResolvedLaunch(exe, ["--a b", "c&d", "\"quoted\""]), Ctx(), CancellationToken.None);
        var info = Assert.Single(starter.Started);
        Assert.False(info.UseShellExecute);
        Assert.Equal(exe, info.FileName);
        Assert.Equal(["--a b", "c&d", "\"quoted\""], info.ArgumentList);
    }

    [Theory]
    [InlineData("relative.exe")]
    [InlineData(@"C:\Windows\System32\cmd.bat")]
    [InlineData(@"C:\Windows\notepad.lnk")]
    [InlineData(@"C:\Windows\explorer")]
    public async Task Launch_refuses_what_is_not_an_exe_at_a_full_path(string path)
    {
        var starter = new RecordingStarter();
        await Assert.ThrowsAsync<ActionFailedException>(() => new LaunchExecutor(starter).ExecuteAsync(new ResolvedLaunch(path, []), Ctx(), CancellationToken.None));
        Assert.Empty(starter.Started);
    }

    [Fact]
    public async Task Launch_fails_when_the_chosen_program_is_gone()
    {
        var starter = new RecordingStarter();
        var ex = await Assert.ThrowsAsync<ActionFailedException>(() => new LaunchExecutor(starter)
            .ExecuteAsync(new ResolvedLaunch(Path.Combine(Path.GetTempPath(), "em-missing-" + Guid.NewGuid().ToString("N") + ".exe"), []), Ctx(), CancellationToken.None));
        Assert.Equal("failed", ex.Code);
        Assert.Empty(starter.Started);
    }

    [Theory]
    [InlineData("https://example.com/watch?v=1", true)]
    [InlineData("http://192.168.1.5:8080/", true)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ms-settings:network", false)]
    [InlineData("calc.exe", false)]
    [InlineData("https://exa\nmple.com", false)]
    [InlineData("ftp://example.com", false)]
    public async Task OpenUrl_allows_only_http_and_https(string url, bool allowed)
    {
        var starter = new RecordingStarter();
        var exec = new OpenUrlExecutor(starter);
        if (allowed)
        {
            await exec.ExecuteAsync(new OpenUrlAction(url), Ctx(), CancellationToken.None);
            Assert.True(Assert.Single(starter.Started).UseShellExecute);
        }
        else
        {
            var ex = await Assert.ThrowsAsync<ActionFailedException>(() => exec.ExecuteAsync(new OpenUrlAction(url), Ctx(), CancellationToken.None));
            Assert.Equal("denied", ex.Code);
            Assert.Empty(starter.Started);
        }
    }
}
