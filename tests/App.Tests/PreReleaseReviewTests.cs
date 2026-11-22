using System.Net;
using System.Net.NetworkInformation;
using EasyMouse.App.Logic;
using EasyMouse.Core.Devices;
using EasyMouse.Remotes.Approvals;
using EasyMouse.Remotes.Catalog;

namespace EasyMouse.App.Tests;

/// <summary>What the Remotes window shows the user before they approve a remote: everything that made it need review, unhidden and unspoofable.</summary>
[Collection("Loc")]
public class ReviewTextTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "em-review-" + Guid.NewGuid().ToString("N"));

    public ReviewTextTests()
    {
        Directory.CreateDirectory(_dir);
        Loc.Instance.SetLanguage("en");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static string Remote(string items, string extra = "") =>
        $$"""{ "schema": 1, "id": "t", "name": "T"{{extra}}, "layout": { "cols": 2, "items": [ {{items}} ] } }""";

    private static string Button(string action, string label = "Go") => $$"""{ "type": "button", "label": "{{label}}", "action": {{action}} }""";

    private RemoteEntry Entry(string json)
    {
        File.WriteAllText(Path.Combine(_dir, "t.json"), json);
        return new RemoteCatalog(null, _dir, new InMemoryRemoteApprovalStore()).Entries.Single();
    }

    private IReadOnlyList<ReviewLine> Lines(string json) => ReviewText.For(Entry(json));

    [Fact]
    public void A_key_chain_that_needs_review_is_shown_with_every_key()
    {
        var entry = Entry(Remote(Button("""[{"do":"key","keys":"Ctrl+Escape"},{"do":"key","keys":"C"},{"do":"key","keys":"M"},{"do":"key","keys":"D"},{"do":"key","keys":"Enter","repeat":2}]""")));
        Assert.Equal(RemoteState.NeedsReview, entry.State); // there is something to approve ...
        var line = Assert.Single(ReviewText.For(entry));    // ... and the list is not empty
        Assert.Contains("5 keys in a row", line.What);
        Assert.Contains("Ctrl+Escape, C, M, D, Enter ×2", line.What);
        Assert.Equal("Button “Go”", line.Where);
    }

    [Fact]
    public void A_launcher_chord_alone_is_called_out()
    {
        var line = Assert.Single(Lines(Remote(Button("""{"do":"key","keys":"Ctrl+Shift+Escape"}"""))));
        Assert.Contains("Ctrl+Shift+Escape", line.What);
        Assert.Contains("Start menu or Task Manager", line.What);
    }

    [Fact]
    public void Everything_that_needs_review_has_a_line()
    {
        // The invariant behind the window: a remote that waits for approval never shows an empty list.
        foreach (var action in new[]
        {
            """{"do":"key","keys":"Ctrl+Escape"}""",
            """[{"do":"key","keys":"A"},{"do":"key","keys":"B"},{"do":"key","keys":"C"},{"do":"key","keys":"D"}]""",
            """{"do":"text","text":"x"}""",
            """{"do":"openUrl","url":"https://example.com"}""",
            """{"do":"launch","exe":"p"}""",
            """{"do":"http","url":"http://127.0.0.1:1/x"}""",
        })
        {
            var entry = Entry(Remote(Button(action)));
            Assert.Equal(RemoteState.NeedsReview, entry.State);
            Assert.NotEmpty(ReviewText.For(entry));
        }
    }

    [Fact]
    public void Ordinary_shortcuts_produce_no_lines()
    {
        Assert.Empty(Lines(Remote(Button("""[{"do":"focus"},{"do":"key","keys":"Alt+Right"}]"""), """, "target": {"process":"vlc.exe"}""")));
    }

    [Fact]
    public void The_program_that_gets_the_focus_is_shown_with_what_follows()
    {
        var lines = Lines(Remote(Button("""[{"do":"focus"},{"do":"text","text":"hello"}]"""), """, "target": {"process":"cmd.exe"}"""));
        Assert.Equal(2, lines.Count);
        Assert.Contains("“cmd.exe”", lines[0].What);
        Assert.Contains("hello", lines[1].What);
    }

    [Fact]
    public void Long_text_is_shown_in_full_with_line_breaks_made_visible()
    {
        var tail = new string('z', 300) + "END";
        var json = Remote(Button("{\"do\":\"text\",\"text\":\"first line\\nsecond " + tail + "\"}"));
        var line = Assert.Single(Lines(json));
        Assert.Contains("first line⏎second", line.What);
        Assert.EndsWith("END”", line.What);
        Assert.Contains("(" + ("first line\nsecond " + tail).Length + " characters)", line.What);
    }

    [Fact]
    public void Direction_and_invisible_characters_are_spelled_out()
    {
        // The validator refuses these in a file; the prompt still escapes them for any other source of a definition.
        const char Rlo = (char)0x202E, Zwsp = (char)0x200B, LineSep = (char)0x2028;
        Assert.Equal("a⟨U+202E⟩b⟨U+200B⟩c", ReviewText.Clean($"a{Rlo}b{Zwsp}c"));
        Assert.Equal("x⏎y⏎z⇥", ReviewText.Clean("x\r\ny\nz\t"));
        Assert.Equal("שלום ok", ReviewText.Clean("שלום ok"));
        Assert.Equal("⟨U+2028⟩", ReviewText.Clean(LineSep.ToString()));
        Assert.Equal("a⟨U+E0041⟩b", ReviewText.Clean("a" + char.ConvertFromUtf32(0xE0041) + "b"));
        Assert.Equal("😀", ReviewText.Clean(char.ConvertFromUtf32(0x1F600)));
    }

    [Fact]
    public void A_remote_with_such_characters_in_its_file_is_invalid_so_it_is_never_offered_for_approval()
    {
        var entry = Entry(Remote(Button("{\"do\":\"text\",\"text\":\"a" + (char)0x202E + "b\"}")));
        Assert.Equal(RemoteState.Invalid, entry.State);
        Assert.Empty(ReviewText.For(entry));
    }
}

/// <summary>Settings and network-polling fixes of the pre-release review, over a real controller with fake machine parts.</summary>
[Collection("Loc")]
public class ControllerReviewFixTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "em-ctlfix-" + Guid.NewGuid().ToString("N"));
    private readonly List<AppController> _controllers = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _controllers) await c.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class XorProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plain) => plain.Select(b => (byte)(b ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] data) => data.Select(b => (byte)(b ^ 0x5A)).ToArray();
    }

    private static readonly AdapterInfo Adapter = new("{TEST-LAN}", "Test LAN", "Test LAN", NetworkInterfaceType.Ethernet, IPAddress.Parse("192.0.2.10"), 24, true, 1);

    private static NetworkSnapshot PrivateLan() => new([Adapter], new Dictionary<string, NetCategory> { [Adapter.Id] = NetCategory.Private });

    private AppController NewApp(Func<NetworkSnapshot>? probe = null)
    {
        Directory.CreateDirectory(_dir);
        var app = new AppController(new AppPaths(_dir) { BuiltInRemotes = Path.Combine(_dir, "none") }, new AppEnvironment
        {
            SystemIntegration = false,
            Protector = new XorProtector(),
            RemoteProtector = new XorProtector(),
            RemotesEnabled = true,
            Probe = probe ?? PrivateLan,
        });
        _controllers.Add(app);
        return app;
    }

    [Fact]
    public async Task Concurrent_settings_changes_are_all_applied_and_the_file_is_valid()
    {
        var app = NewApp();
        await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
            Task.Run(() => app.UpdateSettingsAsync(s => i % 2 == 0 ? s with { StartMinimised = !s.StartMinimised } : s with { IncludePrereleases = !s.IncludePrereleases }))));
        // 20 flips of each flag leave both where they started: no update was lost or applied twice.
        Assert.Equal(new AppSettings().StartMinimised, app.Settings.StartMinimised);
        Assert.Equal(new AppSettings().IncludePrereleases, app.Settings.IncludePrereleases);
        Assert.Equal(app.Settings, new SettingsStore(app.Paths.Settings).Load());
        Assert.Empty(Directory.GetFiles(_dir, "settings.json.*.tmp"));
    }

    [Fact]
    public async Task A_settings_file_that_cannot_be_written_keeps_the_change_for_this_session_and_does_not_throw()
    {
        var app = NewApp();
        Directory.CreateDirectory(app.Paths.Settings); // a directory where the file belongs: every save fails
        var wanted = !app.Settings.StartMinimised;
        await app.UpdateSettingsAsync(s => s with { StartMinimised = wanted });
        Assert.Equal(wanted, app.Settings.StartMinimised);
        Assert.Empty(Directory.GetFiles(_dir, "settings.json.*.tmp"));
    }

    [Fact]
    public async Task A_network_probe_that_throws_stops_listening_and_polling_recovers_on_the_next_try()
    {
        var fail = false;
        var app = NewApp(() => fail ? throw new NetworkInformationException(5) : PrivateLan());

        fail = true;
        await app.RefreshNetworkAsync();           // must not throw
        Assert.False(app.IsListening);             // not seeing the network is not proof that it is Private
        Assert.Equal(StopReason.NoNetwork, app.Listener.Plan!.Reason);

        fail = false;
        await app.RefreshNetworkAsync();
        Assert.NotEqual(StopReason.NoNetwork, app.Listener.Plan!.Reason);
    }

    [Fact]
    public void The_settings_store_reports_a_failed_save_instead_of_throwing()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        Directory.CreateDirectory(path);
        var store = new SettingsStore(path);
        Assert.False(store.Save(new AppSettings()));
        Assert.NotNull(store.SaveError);
        Assert.Empty(Directory.GetFiles(_dir, "settings.json.*.tmp"));
    }
}

public class FileStoresIoFailureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "em-iofail-" + Guid.NewGuid().ToString("N"));

    public FileStoresIoFailureTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class XorProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plain) => plain;
        public byte[] Unprotect(byte[] data) => data;
    }

    [Fact]
    public void A_secret_that_cannot_be_saved_is_reported_and_not_kept()
    {
        var path = Path.Combine(_dir, "secrets.json");
        Directory.CreateDirectory(path + ".tmp"); // the temp file cannot be created
        var store = new FileSecretStore(path, new XorProtector());
        Assert.False(store.Set("token", "abc"));
        Assert.NotNull(store.SaveError);
        Assert.Null(store.Get("token"));
    }

    [Fact]
    public void Importing_into_a_folder_that_cannot_be_written_is_a_result_not_a_crash()
    {
        var blocked = Path.Combine(_dir, "remotes");
        File.WriteAllText(blocked, "a file where the folder should be");
        var bytes = System.Text.Encoding.UTF8.GetBytes("""{ "schema": 1, "id": "mine", "name": "Mine", "layout": { "cols": 1, "items": [ { "type": "button", "label": "x", "action": { "do": "key", "keys": "F" } } ] } }""");
        var catalog = new RemoteCatalog(null, null, new InMemoryRemoteApprovalStore());
        var result = RemoteImporter.Import(bytes, blocked, catalog);
        Assert.Equal(ImportOutcome.WriteFailed, result.Outcome);
        Assert.NotEmpty(result.Messages);
    }

    [Fact]
    public void An_id_with_a_trailing_newline_is_not_imported()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("{ \"schema\": 1, \"id\": \"media\\n\", \"name\": \"M\", \"layout\": { \"cols\": 1, \"items\": [ { \"type\": \"button\", \"label\": \"x\", \"action\": { \"do\": \"key\", \"keys\": \"F\" } } ] } }");
        var result = RemoteImporter.Import(bytes, Path.Combine(_dir, "remotes"), new RemoteCatalog(null, null, new InMemoryRemoteApprovalStore()));
        Assert.Equal(ImportOutcome.Invalid, result.Outcome);
    }
}

/// <summary>CI must refuse exactly the tags the app's own version parser refuses, so it never signs a release the app would ignore.</summary>
public class ReleaseTagRuleTests
{
    private static (System.Text.RegularExpressions.Regex Rx, int MaxLength) WorkflowRule()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, ".github", "workflows", "release.yml"))) dir = dir.Parent;
        var yml = File.ReadAllText(Path.Combine(dir!.FullName, ".github", "workflows", "release.yml"));
        var m = System.Text.RegularExpressions.Regex.Match(yml, @"\[\[ ! ""\$tag"" =~ (\^v\S+\$) \|\| \$\{#tag\} -gt (\d+) \]\]");
        Assert.True(m.Success, "the tag check line in release.yml was not found");
        return (new System.Text.RegularExpressions.Regex(m.Groups[1].Value, System.Text.RegularExpressions.RegexOptions.CultureInvariant), int.Parse(m.Groups[2].Value));
    }

    [Theory]
    [InlineData("v1.0.0")]
    [InlineData("v0.0.1")]
    [InlineData("v1.2.3-rc.1")]
    [InlineData("v1.2.3-0")]
    [InlineData("v1.2.3-alpha-1.beta")]
    [InlineData("v1.2.3-1a")]
    [InlineData("v999999999.0.0")]
    [InlineData("v1.0.0-01")]            // leading zero in a numeric identifier
    [InlineData("v01.0.0")]
    [InlineData("v1.0")]
    [InlineData("v1.0.0-")]              // empty pre-release
    [InlineData("v1.0.0-a..b")]          // empty identifier
    [InlineData("v1.0.0-a.")]
    [InlineData("v1.0.0+build")]         // build metadata is not used for tags
    [InlineData("v1.2147483648.0")]      // does not fit the app's int
    [InlineData("v1.0.0-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // longer than 64
    [InlineData("v1.0.0-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]          // 64 exactly with the v: ok
    [InlineData("1.0.0")]
    public void The_workflow_accepts_a_tag_exactly_when_the_app_parses_its_version(string tag)
    {
        var (rx, max) = WorkflowRule();
        var ci = rx.IsMatch(tag) && tag.Length <= max;
        var app = tag.StartsWith('v') && !tag.Contains('+') && Updates.SemVer.TryParse(tag[1..], out _);
        Assert.Equal(app, ci);
    }
}
