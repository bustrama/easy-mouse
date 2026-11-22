using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using EasyMouse.Remotes.Approvals;
using EasyMouse.Remotes.Catalog;
using EasyMouse.Remotes.Execution;
using EasyMouse.Remotes.Layout;
using EasyMouse.Remotes.Model;
using EasyMouse.Remotes.Security;
using EasyMouse.Remotes.Validation;

namespace EasyMouse.Remotes.Tests;

public class ClassificationTests
{
    [Theory]
    [InlineData("""{"do":"key","keys":"F"}""", ActionClass.Safe)]
    [InlineData("""{"do":"media","cmd":"mute"}""", ActionClass.Safe)]
    [InlineData("""{"do":"mouse","cmd":"click"}""", ActionClass.Safe)]
    [InlineData("""{"do":"smtc","cmd":"toggle"}""", ActionClass.Safe)]
    [InlineData("""{"do":"volume","cmd":"mute"}""", ActionClass.Safe)]
    [InlineData("""{"do":"delay","ms":5}""", ActionClass.Safe)]
    [InlineData("""{"do":"text","text":"hi"}""", ActionClass.Sensitive)]
    [InlineData("""{"do":"openUrl","url":"https://example.com"}""", ActionClass.Sensitive)]
    [InlineData("""{"do":"launch","exe":"p"}""", ActionClass.Dangerous)]
    [InlineData("""{"do":"http","url":"http://127.0.0.1/"}""", ActionClass.Dangerous)]
    public void Actions_are_classified_as_in_security_md(string action, ActionClass expected)
    {
        var r = RemoteValidator.Validate(Json.Remote(Json.Button(action)));
        Assert.True(r.IsValid, string.Join("; ", r.Errors));
        Assert.Equal(expected, ActionClassifier.Classify(r.Remote!.Items[0].Actions[0]));
    }

    [Fact]
    public void Focus_is_safe()
    {
        var r = RemoteValidator.Validate(Json.Remote(Json.Button("""{"do":"focus"}"""), extra: """, "target": {"process":"a.exe"}"""));
        Assert.Equal(ActionClass.Safe, ActionClassifier.Classify(r.Remote!.Items[0].Actions[0]));
    }
}

public class ApprovalPinningTests
{
    private const string Text = """{"do":"text","text":"hello"}""";

    [Fact]
    public void Safe_only_remote_is_ready_without_approval()
    {
        using var f = new Fixture(Json.Remote(Json.Button("""{"do":"key","keys":"F"}""")));
        Assert.Equal(RemoteState.Ready, f.Catalog.Entries.Single().State);
    }

    [Fact]
    public async Task Sensitive_remote_needs_review_listing_the_actions_and_refuses_to_run()
    {
        using var f = new Fixture(Json.Remote(Json.Button(Text)));
        var e = f.Catalog.Entries.Single();
        Assert.Equal(RemoteState.NeedsReview, e.State);
        Assert.Equal("text", e.Review.Single().Kind);
        Assert.Equal(ActionClass.Sensitive, e.Review.Single().Class);
        var r = await f.Invoke("0");
        Assert.Equal(new(false, "denied"), r);
        Assert.Equal(0, f.Rec.TotalCalls);
    }

    [Fact]
    public async Task Approval_is_pinned_to_the_hash_so_any_edit_asks_again()
    {
        using var f = new Fixture(Json.Remote(Json.Button(Text)));
        Assert.Null(f.Catalog.Approve("test", f.HashOf("test")));
        Assert.Equal(RemoteState.Ready, f.Catalog.Entries.Single().State);
        Assert.True((await f.Invoke("0")).Ok);
        Assert.Single(f.Rec.Text.Calls);

        // Edit: only whitespace changes, but the hash differs. No Reload() call: execution must still notice.
        File.AppendAllText(Path.Combine(f.User.Path, "test.json"), "\n");
        var r = await f.Invoke("0");
        Assert.Equal(new(false, "denied"), r);
        Assert.Single(f.Rec.Text.Calls);
        Assert.Equal(RemoteState.NeedsReview, f.Catalog.Entries.Single().State);

        // Reverting the edit restores the approved hash.
        f.User.Write("test.json", Json.Remote(Json.Button(Text)));
        Assert.True((await f.Invoke("0")).Ok);
    }

    [Fact]
    public async Task Approving_a_file_that_changed_after_the_prompt_is_refused()
    {
        using var f = new Fixture(Json.Remote(Json.Button(Text)));
        var reviewed = f.Hash; // the hash shown in the prompt
        f.User.Write("test.json", Json.Remote(Json.Button("""{"do":"http","url":"http://127.0.0.1:1/"}""")));
        Assert.Contains("changed after it was reviewed", f.Catalog.Approve("test", reviewed));
        Assert.Equal(RemoteState.NeedsReview, f.Catalog.Entries.Single().State);
        Assert.Equal(new(false, "denied"), await f.Invoke("0"));
    }

    [Fact]
    public async Task A_changed_action_with_the_same_remote_id_is_not_approved()
    {
        using var f = new Fixture(Json.Remote(Json.Button("""{"do":"key","keys":"F"}""")));
        f.Catalog.Approve("test", f.HashOf("test")); // nothing to review, harmless
        f.User.Write("test.json", Json.Remote(Json.Button("""{"do":"http","url":"http://127.0.0.1:1/"}""")));
        Assert.Equal(new(false, "denied"), (await f.Invoke("0")));
    }

    [Fact]
    public void Approvals_survive_a_restart_and_corrupt_files_fail_closed()
    {
        using var f = new Fixture(Json.Remote(Json.Button(Text)));
        f.Catalog.Approve("test", f.HashOf("test"));
        var again = new FileRemoteApprovalStore(f.Data.Path);
        Assert.NotNull(again.Get("test", f.Hash));
        Assert.Null(again.Get("test", "0000"));
        File.WriteAllText(Path.Combine(f.Data.Path, FileRemoteApprovalStore.FileName), "{ broken");
        var broken = new FileRemoteApprovalStore(f.Data.Path);
        Assert.NotNull(broken.LoadError);
        Assert.Null(broken.Get("test", f.Hash));
    }

    [Fact]
    public void Revoke_asks_again_and_invalid_or_duplicate_remotes_are_reported()
    {
        using var f = new Fixture(Json.Remote(Json.Button(Text)));
        f.Catalog.Approve("test", f.HashOf("test"));
        f.Catalog.Revoke("test");
        Assert.Equal(RemoteState.NeedsReview, f.Catalog.Entries.Single().State);
        f.User.Write("bad.json", """{"schema":1,"id":"bad","name":"x","layout":{"cols":1,"items":[{"type":"button","label":"x","action":{"do":"shell"}}]}}""");
        f.User.Write("dup.json", Json.Remote(Json.Button("""{"do":"key","keys":"F"}""")));
        f.Catalog.Reload();
        var bad = f.Catalog.Entries.Single(e => e.FilePath.EndsWith("bad.json"));
        Assert.Equal(RemoteState.Invalid, bad.State);
        Assert.Contains("unknown action", bad.Errors.Single());
        Assert.Equal(RemoteState.Invalid, f.Catalog.Entries.Single(e => e.FilePath.EndsWith("test.json")).State); // dup.json loads first and owns the id
        Assert.Contains(f.Catalog.Entries, e => e.State == RemoteState.Invalid && e.Errors.Any(x => x.Contains("already used")));
        Assert.Null(f.Catalog.Resolve("bad"));
    }

    [Fact]
    public void Launch_slots_need_a_full_program_path_to_approve()
    {
        using var f = new Fixture(Json.Remote(Json.Button("""{"do":"launch","exe":"player","args":["--x"]}""")));
        Assert.Equal(["player"], f.Catalog.Entries.Single().LaunchSlots);
        Assert.NotNull(f.Catalog.Approve("test", f.HashOf("test")));
        Assert.NotNull(f.Catalog.Approve("test", f.HashOf("test"), new Dictionary<string, string> { ["player"] = "player.exe" }));
        Assert.Null(f.Catalog.Approve("test", f.HashOf("test"), new Dictionary<string, string> { ["player"] = @"C:\Tools\player.exe" }));
    }
}

public class HandlerTests
{
    [Fact]
    public async Task Runs_a_safe_remote_through_the_executors_in_order()
    {
        var json = Json.Remote(
            Json.Button("""[{"do":"focus"},{"do":"key","keys":"Ctrl+Right","repeat":2},{"do":"delay","ms":50},{"do":"media","cmd":"next"}]""")
            + ",{\"type\":\"slider\",\"action\":{\"do\":\"volume\"}}",
            extra: """, "target": {"process":"app*.exe"}""");
        using var f = new Fixture(json);
        var run = f.Invoke("0");
        while (!run.IsCompleted && f.Rec.Key.Calls.Count == 0) await Task.Yield();
        for (var i = 0; i < 50 && !run.IsCompleted; i++) { f.Time.Advance(TimeSpan.FromMilliseconds(60)); await Task.Delay(5); }
        Assert.True((await run).Ok);
        Assert.Single(f.Rec.Focus.Calls);
        Assert.Equal("app*.exe", f.Rec.Focus.Calls[0].Context.TargetProcess);
        Assert.Equal(2, f.Rec.Key.Calls.Single().Action.Repeat);
        Assert.Single(f.Rec.Media.Calls);

        Assert.True((await f.Invoke("1", 130)).Ok);
        Assert.Equal(100, f.Rec.Volume.Calls.Single().Context.Value); // clamped
        Assert.Equal(new(false, "unsupported"), await f.Invoke("1")); // slider without value
        Assert.Equal(new(false, "unsupported"), await f.Invoke("nope"));
        Assert.Equal(new(false, "unsupported"), await f.Invoke("0", remote: "other"));
    }

    [Fact]
    public async Task An_executor_failure_is_reported_without_details_and_missing_executor_is_unsupported()
    {
        using var f = new Fixture(Json.Remote(Json.Button("""{"do":"key","keys":"F"}""")));
        f.Rec.Key.Throw = new InvalidOperationException("C:\\secret\\path boom");
        Assert.Equal(new(false, "failed"), await f.Invoke("0"));
        using var g = new Fixture(Json.Remote(Json.Button("""{"do":"key","keys":"F"}""")), new ActionExecutors());
        Assert.Equal(new(false, "unsupported"), await g.Invoke("0"));
    }

    [Fact]
    public async Task Rate_limits_per_remote_and_recovers()
    {
        using var f = new Fixture(Json.Remote(Json.Button("""{"do":"key","keys":"F"}""")), limit: new RemoteRateLimit(2, 3));
        f.User.Write("other.json", Json.Remote(Json.Button("""{"do":"key","keys":"G"}"""), id: "other"));
        f.Catalog.Reload();
        for (var i = 0; i < 3; i++) Assert.True((await f.Invoke("0")).Ok);
        Assert.Equal(new(false, "busy"), await f.Invoke("0"));
        Assert.True((await f.Invoke("0", remote: "other")).Ok); // another remote has its own bucket
        f.Time.Advance(TimeSpan.FromSeconds(1)); // 2 tokens back
        Assert.True((await f.Invoke("0")).Ok);
        Assert.True((await f.Invoke("0")).Ok);
        Assert.Equal(new(false, "busy"), await f.Invoke("0"));
        Assert.Equal(6, f.Rec.Key.Calls.Count); // 3 + 1 (other remote) + 2
    }

    // ---- launch ----

    [Fact]
    public async Task Launch_runs_only_the_program_the_user_picked_with_the_fixed_arguments()
    {
        using var f = new Fixture(Json.Remote(Json.Button("""{"do":"launch","exe":"player","args":["--fullscreen","movie.mkv"]}""")));
        Assert.Equal(new(false, "denied"), await f.Invoke("0")); // not approved
        Assert.Null(f.Catalog.Approve("test", f.HashOf("test"), new Dictionary<string, string> { ["player"] = @"C:\Tools\player.exe" }));
        Assert.True((await f.Invoke("0")).Ok);
        var call = f.Rec.Launch.Calls.Single().Action;
        Assert.Equal(@"C:\Tools\player.exe", call.ExePath);
        Assert.Equal(["--fullscreen", "movie.mkv"], call.Args);
    }

    [Fact]
    public async Task Launch_with_an_unbound_slot_is_refused_even_if_the_remote_is_approved()
    {
        using var f = new Fixture(Json.Remote(Json.Button("""{"do":"launch","exe":"player"}""")));
        f.Approvals.Save(new RemoteApproval("test", f.Hash, DateTimeOffset.UtcNow, [], new Dictionary<string, string>()));
        f.Catalog.Reload();
        Assert.Equal(new(false, "denied"), await f.Invoke("0"));
        Assert.Empty(f.Rec.Launch.Calls);
    }

    [Fact]
    public async Task A_refused_action_stops_the_whole_item_before_anything_runs()
    {
        using var f = new Fixture(Json.Remote(Json.Button("""[{"do":"key","keys":"F"},{"do":"launch","exe":"player"}]""")));
        f.Approvals.Save(new RemoteApproval("test", f.Hash, DateTimeOffset.UtcNow, [], new Dictionary<string, string>()));
        f.Catalog.Reload();
        Assert.False((await f.Invoke("0")).Ok);
        Assert.Equal(0, f.Rec.TotalCalls);
    }

    // ---- http ----

    private static string Http(string url) => Json.Remote(Json.Button($$"""{"do":"http","url":"{{url}}"}"""));

    [Fact]
    public async Task Http_to_loopback_needs_only_the_remote_approval()
    {
        using var f = new Fixture(Http("http://127.0.0.1:32100/play"));
        Assert.Equal(new(false, "denied"), await f.Invoke("0"));
        f.Catalog.Approve("test", f.HashOf("test"));
        Assert.True((await f.Invoke("0")).Ok);
        Assert.Equal("http://127.0.0.1:32100/play", f.Rec.Http.Calls.Single().Action.Url.ToString());
    }

    [Theory]
    [InlineData("http://localhost:3000/x")]
    [InlineData("http://[::1]:3000/x")]
    [InlineData("http://127.5.5.5/x")]
    public async Task All_loopback_spellings_are_allowed(string url)
    {
        using var f = new Fixture(Http(url));
        f.Catalog.Approve("test", f.HashOf("test"));
        Assert.True((await f.Invoke("0")).Ok);
    }

    [Theory]
    [InlineData("http://192.168.1.50/x")]
    [InlineData("https://api.example.com/x")]
    [InlineData("http://127.0.0.1.evil.example/x")]
    public async Task Other_hosts_are_allowed_only_when_the_user_approved_them(string url)
    {
        using var f = new Fixture(Http(url));
        var host = HttpPolicy.HostOf(url)!;
        Assert.Equal([host], f.Catalog.Entries.Single().ExternalHosts);
        // An approval that does not list the host (for example an older or hand-edited store) is not enough.
        f.Approvals.Save(new RemoteApproval("test", f.Hash, DateTimeOffset.UtcNow, ["other.example"], new Dictionary<string, string>()));
        f.Catalog.Reload();
        Assert.Equal(new(false, "denied"), await f.Invoke("0"));
        Assert.Empty(f.Rec.Http.Calls);
        Assert.Null(f.Catalog.Approve("test", f.HashOf("test")));
        Assert.True((await f.Invoke("0")).Ok);
    }

    [Fact]
    public void Policy_rejects_lookalike_loopback_hosts()
    {
        Assert.False(HttpPolicy.IsLoopbackHost("localhost.evil.example"));
        Assert.False(HttpPolicy.IsLoopbackHost("127.0.0.1.nip.io"));
        Assert.False(HttpPolicy.IsLoopbackHost("0.0.0.0"));
        Assert.False(HttpPolicy.IsLoopbackHost("10.0.0.1"));
        Assert.True(HttpPolicy.IsLoopbackHost("LOCALHOST"));
    }

    private static async Task<(int Port, Task<string> Request, TcpListener L)> Serve(string response)
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        var task = Task.Run(async () =>
        {
            using var c = await l.AcceptTcpClientAsync();
            var s = c.GetStream();
            var buf = new byte[8192];
            // Read until the headers are complete and the Content-Length body (if any) has arrived.
            var n = 0;
            string req;
            while (true)
            {
                var read = await s.ReadAsync(buf.AsMemory(n));
                if (read == 0) { req = Encoding.UTF8.GetString(buf, 0, n); break; }
                n += read;
                req = Encoding.UTF8.GetString(buf, 0, n);
                var end = req.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end < 0) continue;
                var m = System.Text.RegularExpressions.Regex.Match(req[..end], @"Content-Length:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var need = m.Success ? int.Parse(m.Groups[1].Value) : 0;
                if (Encoding.UTF8.GetByteCount(req[(end + 4)..]) >= need) break;
            }
            await s.WriteAsync(Encoding.ASCII.GetBytes(response));
            return req;
        });
        await Task.Yield();
        return (port, task, l);
    }

    [Fact]
    public async Task The_real_http_executor_refuses_redirects()
    {
        var (port, request, l) = await Serve("HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:1/stolen\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        try
        {
            using var http = new HttpClientExecutor();
            using var f = new Fixture(Http($"http://127.0.0.1:{port}/go"), new ActionExecutors { Http = http });
            f.Catalog.Approve("test", f.HashOf("test"));
            Assert.Equal(new(false, "failed"), await f.Invoke("0"));
            Assert.StartsWith("GET /go ", await request);
            Assert.Contains("failed", f.Audit.Lines.Single());
        }
        finally { l.Stop(); }
    }

    [Fact]
    public async Task The_real_http_executor_times_out_and_reports_status_codes()
    {
        var (port, _, l) = await Serve("HTTP/1.1 500 X\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        try
        {
            using var http = new HttpClientExecutor();
            using var f = new Fixture(Json.Remote(Json.Button($$"""{"do":"http","url":"http://127.0.0.1:{{port}}/","timeoutMs":300}""")), new ActionExecutors { Http = http });
            f.Catalog.Approve("test", f.HashOf("test"));
            Assert.Equal(new(false, "failed"), await f.Invoke("0"));
        }
        finally { l.Stop(); }

        var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        try
        {
            using var http = new HttpClientExecutor();
            using var f = new Fixture(Json.Remote(Json.Button($$"""{"do":"http","url":"http://127.0.0.1:{{((IPEndPoint)silent.LocalEndpoint).Port}}/","timeoutMs":300}""")), new ActionExecutors { Http = http });
            f.Catalog.Approve("test", f.HashOf("test"));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal(new(false, "failed"), await f.Invoke("0"));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        }
        finally { silent.Stop(); }
    }

    // ---- secrets ----

    [Fact]
    public async Task Secrets_are_substituted_on_the_pc_and_never_leak_into_results_audit_or_review()
    {
        const string secret = "S3CRET-t0ken/+=";
        var (port, request, l) = await Serve("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        try
        {
            using var http = new HttpClientExecutor();
            var json = Json.Remote(Json.Button($$"""{"do":"http","method":"POST","url":"http://127.0.0.1:{{port}}/p?token=${secret.plex}","headers":{"X-Token":"${secret.plex}"},"body":"{\"t\":\"${secret.plex}\"}"}"""));
            using var f = new Fixture(json, new ActionExecutors { Http = http });
            f.Secrets.Set("plex", secret);
            var entry = f.Catalog.Entries.Single();
            Assert.DoesNotContain(secret, JsonSerializer.Serialize(entry.Review));
            Assert.DoesNotContain(secret, File.ReadAllText(entry.FilePath));
            f.Catalog.Approve("test", f.HashOf("test"));
            var result = await f.Invoke("0");
            Assert.True(result.Ok);
            var sent = await request;
            Assert.Contains("token=S3CRET-t0ken%2F%2B%3D", sent); // URL-encoded in the query
            Assert.Contains("X-Token: " + secret, sent);
            Assert.Contains("{\"t\":\"" + secret + "\"}", sent);
            Assert.DoesNotContain(secret, string.Join("|", f.Audit.Lines) + JsonSerializer.Serialize(result));
            Assert.DoesNotContain(secret, File.ReadAllText(Path.Combine(f.Data.Path, FileRemoteApprovalStore.FileName)));
        }
        finally { l.Stop(); }
    }

    [Fact]
    public async Task Resolved_requests_keep_secrets_out_of_everything_but_the_wire_and_missing_secrets_fail()
    {
        using var f = new Fixture(Http("http://127.0.0.1:1/p?t=${secret.nope}"));
        f.Catalog.Approve("test", f.HashOf("test"));
        var r = await f.Invoke("0");
        Assert.Equal(new(false, "failed"), r);
        Assert.Empty(f.Rec.Http.Calls);
        Assert.Equal("http://127.0.0.1:1/p?...", HttpPolicy.Redact("http://127.0.0.1:1/p?t=${secret.x}"));
        // the review text shown to the user hides the query
        Assert.DoesNotContain("secret.nope", f.Catalog.Entries.Single().Review.Single().Summary);
    }

    [Fact]
    public async Task A_secret_value_with_line_breaks_cannot_inject_a_header()
    {
        var json = Json.Remote(Json.Button("""{"do":"http","url":"http://127.0.0.1:1/","headers":{"X-T":"${secret.t}"}}"""));
        using var f = new Fixture(json);
        f.Secrets.Set("t", "a\r\nX-Evil: 1");
        f.Catalog.Approve("test", f.HashOf("test"));
        Assert.Equal(new(false, "failed"), await f.Invoke("0"));
        Assert.Empty(f.Rec.Http.Calls);
    }
}

public class BuiltInAndPushTests
{
    private static RemoteCatalog Builtins() => new(Repo.BuiltIn, null, new InMemoryRemoteApprovalStore());

    [Fact]
    public void Every_builtin_remote_validates_is_ready_and_has_only_safe_actions()
    {
        var c = Builtins();
        Assert.Equal(["browser-video", "plex", "stremio", "vlc"], c.Entries.Select(e => e.Id).OrderBy(x => x));
        foreach (var e in c.Entries)
        {
            Assert.True(e.State == RemoteState.Ready, $"{e.FilePath}: {string.Join("; ", e.Errors)}");
            Assert.Empty(e.Review);
            Assert.Equal(Path.GetFileNameWithoutExtension(e.FilePath), e.Id);
            Assert.False(string.IsNullOrWhiteSpace(e.Definition!.Description));
        }
    }

    [Fact]
    public void Remotes_whose_keys_are_unverified_say_so()
    {
        var plex = Builtins().Entries.Single(e => e.Id == "plex").Definition!;
        Assert.Contains("UNVERIFIED", plex.Description);

    }

    [Fact]
    public void The_pushed_layout_has_the_shape_web_expects_and_fits_one_frame()
    {
        var push = RemoteLayoutPush.Build(Builtins());
        Assert.Empty(push.Omitted);
        Assert.True(Encoding.UTF8.GetByteCount(push.Json) <= RemoteLayoutPush.MaxJsonBytes, $"{Encoding.UTF8.GetByteCount(push.Json)} bytes");
        using var doc = JsonDocument.Parse(push.Json);
        var root = doc.RootElement;
        Assert.Equal("remotes", root.GetProperty("t").GetString());
        Assert.False(push.Json.Contains("\"action\"") || push.Json.Contains("\"do\"") || push.Json.Contains("target") || push.Json.Contains("description"));
        var remotes = root.GetProperty("remotes");
        Assert.InRange(remotes.GetArrayLength(), 1, 32);
        foreach (var r in remotes.EnumerateArray())
        {
            // the rules of web/src/remotes.ts sanitizeRemote
            Assert.Equal(1, r.GetProperty("schema").GetInt32());
            Assert.InRange(r.GetProperty("id").GetString()!.Length, 1, 64);
            Assert.InRange(r.GetProperty("name").GetString()!.Length, 1, 40);
            var layout = r.GetProperty("layout");
            Assert.InRange(layout.GetProperty("cols").GetInt32(), 1, 4);
            var items = layout.GetProperty("items");
            Assert.InRange(items.GetArrayLength(), 1, 64);
            foreach (var it in items.EnumerateArray())
            {
                var type = it.GetProperty("type").GetString();
                Assert.Contains(type, new[] { "button", "slider", "label" });
                string? Get(string n) => it.TryGetProperty(n, out var v) ? v.GetString() : null;
                if (type == "button") Assert.True(Get("icon") is not null || Get("label") is not null);
                if (Get("icon") is { } icon) Assert.Contains(icon, RemoteValidator.Icons);
                if (Get("label") is { } label) Assert.InRange(label.Length, 1, 40);
                if (type == "label") Assert.True(Get("bind") is not null || Get("text") is not null || Get("label") is not null);
                if (Get("bind") is { } bind) Assert.Contains(bind, RemoteValidator.Bindings);
            }
        }
    }

    [Fact]
    public void Pushed_item_ids_equal_the_ids_the_handler_resolves()
    {
        // Web uses it.id ?? String(index); the handler resolves RemoteItem.Id. They must agree for every item.
        var c = Builtins();
        using var doc = JsonDocument.Parse(RemoteLayoutPush.Build(c).Json);
        foreach (var r in doc.RootElement.GetProperty("remotes").EnumerateArray())
        {
            var def = c.Entries.Single(e => e.Id == r.GetProperty("id").GetString()).Definition!;
            var i = 0;
            foreach (var it in r.GetProperty("layout").GetProperty("items").EnumerateArray())
            {
                var webId = it.TryGetProperty("id", out var idEl) ? idEl.GetString()! : i.ToString();
                Assert.Equal(def.Items[i].Id, webId);
                Assert.NotNull(def.FindItem(webId));
                i++;
            }
        }
    }

    [Fact]
    public void Push_carries_only_runnable_remotes_and_drops_what_does_not_fit()
    {
        using var f = new Fixture(Json.Remote(Json.Button("""{"do":"text","text":"x"}""")));
        f.User.Write("safe.json", Json.Remote(Json.Button("""{"do":"key","keys":"F"}"""), id: "safe"));
        f.Catalog.Reload();
        Assert.Equal(["safe"], RemoteLayoutPush.Build(f.Catalog).Included); // needs-review is not pushed
        f.Catalog.Approve("test", f.HashOf("test"));
        Assert.Equal(["safe", "test"], RemoteLayoutPush.Build(f.Catalog).Included.OrderBy(x => x));
        var tiny = RemoteLayoutPush.Build(f.Catalog, maxBytes: 150);
        Assert.NotEmpty(tiny.Omitted);
        Assert.True(Encoding.UTF8.GetByteCount(tiny.Json) <= 150);
    }

    [Fact]
    public void Hebrew_labels_are_sent_as_utf8_not_escapes()
    {
        using var f = new Fixture(Json.Remote("""{"type":"button","label":"השהיה","action":{"do":"key","keys":"Space"}}"""));
        var push = RemoteLayoutPush.Build(f.Catalog);
        Assert.Contains("השהיה", push.Json);
    }
}
