using EasyMouse.Remotes.Approvals;
using EasyMouse.Remotes.Catalog;
using EasyMouse.Remotes.Validation;

namespace EasyMouse.Remotes.Tests;

/// <summary>Findings of the pre-release review: strict ids, no invisible characters in reviewed text, IO failures that must not crash.</summary>
public class PreReleaseReviewTests
{
    private static ValidationResult V(string json) => RemoteValidator.Validate(json);

    [Theory]
    [InlineData("media\\n")]    // a trailing newline (JSON escape) slipped through ^...$
    [InlineData("\\nmedia")]
    public void A_remote_id_with_a_line_break_is_refused(string id)
    {
        var r = V(Json.Remote(Json.Button("""{"do":"key","keys":"F"}"""), id: id));
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.StartsWith("$.id", StringComparison.Ordinal));
    }

    [Fact]
    public void An_item_id_launch_slot_and_process_with_a_trailing_newline_are_refused()
    {
        var item = """{ "type":"button","label":"a","id":"x\n","action":{"do":"key","keys":"F"} }""";
        Assert.Contains(V(Json.Remote(item)).Errors, e => e.Contains(".id", StringComparison.Ordinal));
        Assert.False(V(Json.Remote(Json.Button("""{"do":"launch","exe":"player\n"}"""))).IsValid);
        Assert.False(V(Json.Remote(Json.Button("""{"do":"focus"}"""), extra: """, "target": {"process":"a.exe\n"}""")).IsValid);
    }

    [Theory]
    [InlineData("\u202e")]   // right-to-left override
    [InlineData("\u200b")]   // zero-width space
    [InlineData("\u2066")]   // left-to-right isolate
    [InlineData("\ufeff")]
    public void Format_characters_are_refused_in_labels_text_and_launch_arguments(string esc)
    {
        Assert.False(V(Json.Remote(Json.Button("""{"do":"key","keys":"F"}""", label: $"a{esc}b"))).IsValid);
        Assert.False(V(Json.Remote(Json.Button($$"""{"do":"text","text":"rm{{esc}} -rf"}"""))).IsValid);
        Assert.False(V(Json.Remote(Json.Button($$"""{"do":"launch","exe":"p","args":["a{{esc}}b"]}"""))).IsValid);
        Assert.False(V(Json.Remote($$"""{ "type":"label","text":"a{{esc}}b" }""")).IsValid);
    }

    [Fact]
    public void Hebrew_and_ordinary_text_still_validate()
    {
        Assert.True(V(Json.Remote(Json.Button("""{"do":"text","text":"שלום, world 1"}""", label: "הפעל"))).IsValid);
    }

    [Fact]
    public void Every_built_in_remote_still_validates()
    {
        foreach (var file in Directory.EnumerateFiles(Repo.BuiltIn, "*.json"))
        {
            var r = RemoteValidator.Validate(File.ReadAllBytes(file));
            Assert.True(r.IsValid, file + ": " + string.Join("; ", r.Errors));
        }
    }

    // ---- an approval that cannot be saved is reported, and does not leave the remote approved in memory

    private sealed class FailingStore : IRemoteApprovalStore
    {
        public bool Fail { get; set; } = true;
        public RemoteApproval? Get(string remoteId, string currentHash) => null;
        public void Save(RemoteApproval approval) { if (Fail) throw new IOException("disk full"); }
        public void Revoke(string remoteId) { if (Fail) throw new IOException("disk full"); }
    }

    [Fact]
    public void A_failed_save_of_an_approval_is_a_result_not_a_crash()
    {
        using var dir = new TempDir();
        dir.Write("t.json", Json.Remote(Json.Button("""{"do":"text","text":"hi"}"""), id: "t"));
        var catalog = new RemoteCatalog(null, dir.Path, new FailingStore());
        var hash = catalog.Entries.Single().Hash;

        var result = catalog.TryApprove("t", hash);
        Assert.Equal(ApproveFailure.SaveFailed, result.Failure);
        Assert.Contains("disk full", result.Detail);
        Assert.Equal(RemoteState.NeedsReview, catalog.Entries.Single().State);
        catalog.Revoke("t"); // must not throw either
    }

    [Fact]
    public void The_file_approval_store_rolls_back_when_it_cannot_write()
    {
        using var dir = new TempDir();
        var store = new FileRemoteApprovalStore(dir.Path);
        // A directory where the temp file must go makes the write fail.
        Directory.CreateDirectory(Path.Combine(dir.Path, FileRemoteApprovalStore.FileName + ".tmp"));
        var approval = new RemoteApproval("t", "h", DateTimeOffset.UtcNow, [], new Dictionary<string, string>());
        var ex = Assert.ThrowsAny<Exception>(() => store.Save(approval));
        Assert.True(ex is IOException or UnauthorizedAccessException, ex.GetType().Name);
        Assert.Null(store.Get("t", "h")); // not saved means not approved
    }
}
