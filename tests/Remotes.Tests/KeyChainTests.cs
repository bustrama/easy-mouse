using EasyMouse.Remotes.Catalog;

namespace EasyMouse.Remotes.Tests;

/// <summary>
/// SR-07 (docs/security-review.md): `key` is a Safe action, so a community remote made only of key presses ran without
/// any prompt. Ctrl+Escape opens the Start menu and a few more key presses type a command into it.
/// </summary>
public class KeyChainTests
{
    private const string Spell = """[{"do":"key","keys":"Ctrl+Escape"},{"do":"delay","ms":300},{"do":"key","keys":"C"},{"do":"key","keys":"M"},{"do":"key","keys":"D"},{"do":"key","keys":"Enter"}]""";

    [Fact]
    public void A_key_only_remote_that_launches_a_command_needs_review()
    {
        using var f = new Fixture(Json.Remote(Json.Button(Spell)));
        Assert.Equal(RemoteState.NeedsReview, f.Catalog.Entries.Single().State);
    }

    [Fact]
    public void A_launcher_chord_alone_needs_review()
    {
        using var f = new Fixture(Json.Remote(Json.Button("""{"do":"key","keys":"Ctrl+Shift+Escape"}""")));
        Assert.Equal(RemoteState.NeedsReview, f.Catalog.Entries.Single().State);
    }

    [Fact]
    public async Task The_phone_cannot_run_an_unapproved_key_chain()
    {
        using var f = new Fixture(Json.Remote(Json.Button(Spell)));
        Assert.Equal(new(false, "denied"), await f.Invoke("0"));
        Assert.Equal(0, f.Rec.TotalCalls);
    }

    [Fact]
    public void Ordinary_player_shortcuts_stay_ready()
    {
        using var f = new Fixture(Json.Remote(
            Json.Button("""[{"do":"focus"},{"do":"key","keys":"Alt+Right"}]""") + "," + Json.Button("""{"do":"key","keys":"Escape"}""") + "," + Json.Button("""{"do":"key","keys":"F","repeat":3}"""),
            extra: """, "target": {"process":"vlc.exe"}"""));
        Assert.Equal(RemoteState.Ready, f.Catalog.Entries.Single().State);
    }

    [Fact]
    public void Built_in_remotes_still_load_without_review()
    {
        using var u = new TempDir();
        var catalog = new RemoteCatalog(Repo.BuiltIn, u.Path, new EasyMouse.Remotes.Approvals.InMemoryRemoteApprovalStore());
        Assert.NotEmpty(catalog.Entries);
        Assert.All(catalog.Entries, e => Assert.NotEqual(RemoteState.NeedsReview, e.State));
    }
}
