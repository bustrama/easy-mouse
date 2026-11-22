using EasyMouse.App.Logic;
using EasyMouse.App.ViewModels;
using EasyMouse.Core.Abstractions;
using EasyMouse.Core.Devices;
using EasyMouse.Remotes.Catalog;
using EasyMouse.Remotes.Model;

namespace EasyMouse.App.Tests;

/// <summary>The remote engine wired into the app: catalog, handler, layouts, approval window logic and import. Never touches the real PC.</summary>
public class RemotesWiringTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "em-remotes-app-" + Guid.NewGuid().ToString("N"));
    private readonly List<AppController> _controllers = [];

    private sealed class NoopProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plain) => plain.Select(b => (byte)(b ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] data) => data.Select(b => (byte)(b ^ 0x5A)).ToArray();
    }

    private sealed class FakeDialogs : IRemotesDialogs
    {
        public string? RemoteFile { get; set; }
        public string? Program { get; set; }
        public string? PickRemoteFile() => RemoteFile;
        public string? PickProgram() => Program;
    }

    private static string RepoBuiltIn()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "remotes", "builtin"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "remotes", "builtin");
    }

    private AppController NewApp(bool builtIn = true)
    {
        Directory.CreateDirectory(_dir);
        var app = new AppController(new AppPaths(_dir) { BuiltInRemotes = builtIn ? RepoBuiltIn() : Path.Combine(_dir, "none") }, new AppEnvironment
        {
            SystemIntegration = false,
            Protector = new NoopProtector(),
            RemoteProtector = new NoopProtector(),
            RemotesEnabled = true,
        });
        _controllers.Add(app);
        return app;
    }

    private static string UserRemote(string items, string id = "mine") =>
        $$"""{ "schema": 1, "id": "{{id}}", "name": "Mine", "layout": { "cols": 2, "items": [ {{items}} ] } }""";

    private static string Button(string action, string label = "Go") => $$"""{ "type": "button", "label": "{{label}}", "action": {{action}} }""";

    private static Task<RemoteActionResult> Invoke(AppController app, string remote, string item, double? value = null)
        => app.Core.Actions.InvokeAsync(new RemoteInvocation(remote, item, value), CancellationToken.None);

    // ------------------------------------------------------------------ built-ins end to end (through the handler, recording executors)

    [Fact]
    public async Task Built_in_remotes_load_ready_are_pushed_to_the_phone_and_their_buttons_run()
    {
        var app = NewApp();
        Assert.All(app.Remotes.Entries, e => Assert.Equal(RemoteState.Ready, e.State));
        Assert.Equal(["browser-video", "plex", "stremio", "vlc"], app.Remotes.Entries.Select(e => e.Id).Order().ToArray());

        var json = app.Core.Layouts.CurrentJson!;
        Assert.StartsWith("{\"t\":\"remotes\"", json);
        Assert.Contains("\"id\":\"stremio\"", json);
        Assert.DoesNotContain("\"action\"", json);
        Assert.DoesNotContain("stremio*.exe", json); // the phone never learns what runs

        // Stremio item 1 = "Play / Pause" = focus the Stremio window, then press Space.
        var play = await Invoke(app, "stremio", "1");
        Assert.True(play.Ok, play.Error);
        var rec = app.RecordedActions!;
        Assert.Equal("stremio*.exe", Assert.Single(rec.Focus.Calls).Context.TargetProcess);
        Assert.Equal(0x20, Assert.Single(rec.Key.Calls).Action.Chord.Key); // VK_SPACE

        // Browser video "Play / Pause" = SMTC toggle.
        Assert.True((await Invoke(app, "browser-video", "1")).Ok);
        Assert.Equal(SmtcCmd.Toggle, Assert.Single(rec.Smtc.Calls).Action.Cmd);

        // The volume slider carries its value.
        Assert.True((await Invoke(app, "vlc", "13", 42)).Ok);
        Assert.Equal(42, Assert.Single(rec.Volume.Calls).Context.Value);

        Assert.Equal("unsupported", (await Invoke(app, "stremio", "99")).Error);
        Assert.Equal("unsupported", (await Invoke(app, "nope", "0")).Error);
    }

    // ------------------------------------------------------------------ approval flow

    [Fact]
    public async Task A_remote_that_types_text_is_off_until_approved_then_runs_and_stops_when_the_file_is_edited()
    {
        var app = NewApp(builtIn: false);
        var file = Path.Combine(app.Paths.UserRemotes, "mine.json");
        File.WriteAllText(file, UserRemote(Button("""{ "do": "text", "text": "hello" }""")));
        app.Remotes.Reload();

        Assert.Equal(RemoteState.NeedsReview, app.Remotes.Entries.Single().State);
        Assert.Null(app.Core.Layouts.CurrentJson); // not offered to the phone yet
        Assert.Equal("denied", (await Invoke(app, "mine", "0")).Error);
        Assert.Empty(app.RecordedActions!.Text.Calls);

        using var vm = new RemotesViewModel(app, new FakeDialogs());
        var row = Assert.Single(vm.Rows);
        Assert.Equal(RemoteRowState.NeedsReview, row.State);
        Assert.Equal(row.HashShort, app.Remotes.Entries.Single().Hash[..12]);
        row.ApproveCommand!.Execute(null);

        Assert.Equal(RemoteRowState.Ready, Assert.Single(vm.Rows).State);
        Assert.Contains("\"id\":\"mine\"", app.Core.Layouts.CurrentJson);
        Assert.True((await Invoke(app, "mine", "0")).Ok);
        Assert.Equal("hello", Assert.Single(app.RecordedActions.Text.Calls).Action.Text);

        // Any edit, even a space, withdraws the approval.
        File.WriteAllText(file, UserRemote(Button("""{ "do": "text", "text": "hello" }""")) + " ");
        Assert.Equal("denied", (await Invoke(app, "mine", "0")).Error);
        Assert.Single(app.RecordedActions.Text.Calls);
        Assert.Null(app.Core.Layouts.CurrentJson);
    }

    [Fact]
    public void Approving_a_row_whose_file_changed_after_it_was_shown_is_refused()
    {
        var app = NewApp(builtIn: false);
        var file = Path.Combine(app.Paths.UserRemotes, "mine.json");
        File.WriteAllText(file, UserRemote(Button("""{ "do": "openUrl", "url": "https://example.com" }""")));
        app.Remotes.Reload();
        using var vm = new RemotesViewModel(app, new FakeDialogs());
        var shown = Assert.Single(vm.Rows); // the user is looking at this one

        File.WriteAllText(file, UserRemote(Button("""{ "do": "openUrl", "url": "https://evil.example" }""")));
        shown.ApproveCommand!.Execute(null);

        Assert.True(vm.NoticeIsError);
        Assert.Contains("changed", vm.Notice);
        Assert.Equal(RemoteState.NeedsReview, app.Remotes.Entries.Single().State);
        Assert.Equal(RemoteRowState.NeedsReview, Assert.Single(vm.Rows).State); // listed again, with the new text, for a fresh review
        Assert.Contains("evil.example", Assert.Single(vm.Rows).Lines.Single().What);
    }

    [Fact]
    public async Task A_launch_remote_needs_a_program_chosen_before_approve_and_runs_exactly_that_program()
    {
        var app = NewApp(builtIn: false);
        File.WriteAllText(Path.Combine(app.Paths.UserRemotes, "mine.json"),
            UserRemote(Button("""{ "do": "launch", "exe": "player", "args": ["--fullscreen"] }""")));
        app.Remotes.Reload();
        var dialogs = new FakeDialogs();
        using var vm = new RemotesViewModel(app, dialogs);
        var row = Assert.Single(vm.Rows);

        var slot = Assert.Single(row.Slots);
        Assert.False(row.ApproveCommand!.CanExecute(null)); // no program chosen
        Assert.Contains("--fullscreen", row.Lines.Single().What);
        Assert.True(row.Lines.Single().Dangerous);

        dialogs.Program = @"C:\Program Files\Player\player.exe";
        slot.ChooseCommand.Execute(null);
        row = Assert.Single(vm.Rows);
        Assert.True(row.ApproveCommand!.CanExecute(null));
        row.ApproveCommand.Execute(null);

        Assert.True((await Invoke(app, "mine", "0")).Ok);
        var launch = Assert.Single(app.RecordedActions!.Launch.Calls).Action;
        Assert.Equal(@"C:\Program Files\Player\player.exe", launch.ExePath);
        Assert.Equal(["--fullscreen"], launch.Args);
    }

    [Fact]
    public void Reject_keeps_the_remote_off_and_review_again_brings_the_prompt_back_and_turn_off_revokes()
    {
        var app = NewApp(builtIn: false);
        File.WriteAllText(Path.Combine(app.Paths.UserRemotes, "mine.json"), UserRemote(Button("""{ "do": "text", "text": "x" }""")));
        app.Remotes.Reload();
        using var vm = new RemotesViewModel(app, new FakeDialogs());

        vm.Rows.Single().RejectCommand.Execute(null);
        Assert.Equal(RemoteRowState.Rejected, vm.Rows.Single().State);
        Assert.Equal(RemoteState.NeedsReview, app.Remotes.Entries.Single().State);
        vm.Rows.Single().ReviewAgainCommand.Execute(null);
        Assert.Equal(RemoteRowState.NeedsReview, vm.Rows.Single().State);

        vm.Rows.Single().ApproveCommand!.Execute(null);
        Assert.True(vm.Rows.Single().IsApproved);
        vm.Rows.Single().RevokeCommand.Execute(null);
        Assert.Equal(RemoteRowState.NeedsReview, vm.Rows.Single().State);
        Assert.Null(app.Core.Layouts.CurrentJson);
    }

    [Fact]
    public void Approvals_survive_a_restart_for_the_same_file_only()
    {
        var app = NewApp(builtIn: false);
        var file = Path.Combine(app.Paths.UserRemotes, "mine.json");
        File.WriteAllText(file, UserRemote(Button("""{ "do": "text", "text": "x" }""")));
        app.Remotes.Reload();
        Assert.Null(app.Remotes.Approve("mine", app.Remotes.Entries.Single().Hash));

        var again = NewApp(builtIn: false);
        Assert.Equal(RemoteState.Ready, again.Remotes.Entries.Single().State);
        File.AppendAllText(file, "\n");
        again.Remotes.Reload();
        Assert.Equal(RemoteState.NeedsReview, again.Remotes.Entries.Single().State);
    }

    // ------------------------------------------------------------------ plain language

    [Fact]
    public void Every_sensitive_or_dangerous_action_is_described_in_plain_words_and_secrets_are_named_never_shown()
    {
        var app = NewApp(builtIn: false);
        File.WriteAllText(Path.Combine(app.Paths.UserRemotes, "mine.json"), UserRemote(string.Join(",",
            Button("""{ "do": "text", "text": "hi\nthere" }""", "Type"),
            Button("""{ "do": "openUrl", "url": "https://example.com/a" }""", "Site"),
            Button("""{ "do": "launch", "exe": "app", "args": ["-x"] }""", "Start"),
            Button("""{ "do": "http", "method": "POST", "url": "http://192.168.1.20/api?token=${secret.hue}", "headers": { "X-Key": "${secret.hue}" } }""", "Lamp"),
            Button("""{ "do": "key", "keys": "F" }""", "Safe"))));
        app.Remotes.Reload();
        using var vm = new RemotesViewModel(app, new FakeDialogs());
        var row = Assert.Single(vm.Rows);

        Assert.Equal(4, row.Lines.Count); // the safe key press is not listed
        Assert.Contains("types this text", row.Lines[0].What);
        Assert.Contains("opens https://example.com/a in your default browser", row.Lines[1].What);
        Assert.Contains("starts the program you choose for “app”", row.Lines[2].What);
        var http = row.Lines[3].What;
        Assert.Contains("POST", http);
        Assert.Contains("outside this PC", http);
        Assert.Contains("hue", http);
        Assert.DoesNotContain("token=", http); // the query (where tokens live) is not echoed
        Assert.Equal("It will be allowed to contact: 192.168.1.20", row.HostsText);
        Assert.Equal([false, false, true, true], row.Lines.Select(l => l.Dangerous).ToArray());
    }

    // ------------------------------------------------------------------ import

    [Fact]
    public void Import_copies_a_valid_file_untouched_and_shows_each_validation_error_for_a_bad_one()
    {
        var app = NewApp();
        var dialogs = new FakeDialogs();
        using var vm = new RemotesViewModel(app, dialogs);

        var src = Path.Combine(_dir, "incoming.json");
        Directory.CreateDirectory(_dir);
        var good = UserRemote(Button("""{ "do": "key", "keys": "F" }"""), "newone");
        File.WriteAllText(src, good);
        dialogs.RemoteFile = src;
        vm.ImportCommand.Execute(null);
        Assert.False(vm.NoticeIsError);
        Assert.Equal(good, File.ReadAllText(Path.Combine(app.Paths.UserRemotes, "newone.json")));
        Assert.Contains(vm.Rows, r => r.Id == "newone" && r.State == RemoteRowState.Ready);
        Assert.Contains("\"id\":\"newone\"", app.Core.Layouts.CurrentJson);

        vm.ImportCommand.Execute(null); // same file again
        Assert.Contains("already imported", vm.Notice);

        File.WriteAllText(src, """{ "schema": 1, "id": "Bad Id", "name": "x", "layout": { "cols": 2, "items": [ { "type": "button", "action": { "do": "shell", "cmd": "calc" } } ] } }""");
        vm.ImportCommand.Execute(null);
        Assert.True(vm.NoticeIsError);
        Assert.Contains("cannot be used", vm.Notice);
        Assert.True(vm.NoticeDetails.Count >= 2);
        Assert.Contains(vm.NoticeDetails, d => d.StartsWith("$.id"));
        Assert.False(File.Exists(Path.Combine(app.Paths.UserRemotes, "Bad Id.json")));

        File.WriteAllText(src, UserRemote(Button("""{ "do": "key", "keys": "F" }"""), "stremio"));
        vm.ImportCommand.Execute(null);
        Assert.True(vm.NoticeIsError);
        Assert.Contains("built-in remote already uses", vm.Notice);

        File.WriteAllText(src, "not json at all");
        vm.ImportCommand.Execute(null);
        Assert.True(vm.NoticeIsError);
        Assert.NotEmpty(vm.NoticeDetails);
    }

    [Fact]
    public void Importing_a_changed_version_replaces_the_file_and_asks_for_approval_again()
    {
        var app = NewApp(builtIn: false);
        var v1 = UserRemote(Button("""{ "do": "text", "text": "one" }"""), "memo");
        Assert.Equal(ImportOutcome.Imported, RemoteImporter.Import(System.Text.Encoding.UTF8.GetBytes(v1), app.Paths.UserRemotes, app.Remotes).Outcome);
        Assert.Null(app.Remotes.Approve("memo", app.Remotes.Entries.Single().Hash));
        Assert.Equal(RemoteState.Ready, app.Remotes.Entries.Single().State);

        var v2 = UserRemote(Button("""{ "do": "text", "text": "two" }"""), "memo");
        Assert.Equal(ImportOutcome.Replaced, RemoteImporter.Import(System.Text.Encoding.UTF8.GetBytes(v2), app.Paths.UserRemotes, app.Remotes).Outcome);
        Assert.Equal(RemoteState.NeedsReview, app.Remotes.Entries.Single().State);
    }

    [Fact]
    public void A_second_file_with_an_id_already_used_by_another_file_is_refused()
    {
        var app = NewApp(builtIn: false);
        File.WriteAllText(Path.Combine(app.Paths.UserRemotes, "first.json"), UserRemote(Button("""{ "do": "key", "keys": "F" }"""), "dup"));
        app.Remotes.Reload();
        var result = RemoteImporter.Import(System.Text.Encoding.UTF8.GetBytes(UserRemote(Button("""{ "do": "key", "keys": "G" }"""), "dup")), app.Paths.UserRemotes, app.Remotes);
        Assert.Equal(ImportOutcome.Rejected, result.Outcome);
        Assert.Equal("first.json", result.OtherFile);
    }

    // ------------------------------------------------------------------ secrets

    [Fact]
    public void Secrets_are_stored_protected_and_survive_a_restart()
    {
        var path = Path.Combine(_dir, "remote-secrets.json");
        Directory.CreateDirectory(_dir);
        var store = new FileSecretStore(path, new NoopProtector());
        store.Set("hue", "s3cr3t-value");
        Assert.DoesNotContain("s3cr3t-value", File.ReadAllText(path));

        var again = new FileSecretStore(path, new NoopProtector());
        Assert.Equal("s3cr3t-value", again.Get("hue"));
        again.Remove("hue");
        Assert.Null(new FileSecretStore(path, new NoopProtector()).Get("hue"));

        File.WriteAllText(path, "{ broken");
        var broken = new FileSecretStore(path, new NoopProtector());
        Assert.Null(broken.Get("hue"));
        Assert.NotNull(broken.LoadError);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _controllers) await c.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
