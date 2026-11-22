using System.Text;
using EasyMouse.Remotes.Approvals;
using EasyMouse.Remotes.Catalog;
using EasyMouse.Remotes.Layout;

namespace EasyMouse.Remotes.Tests;

public class LayoutSourceTests
{
    [Fact]
    public void The_four_built_in_remotes_all_fit_in_one_phone_frame()
    {
        var catalog = new RemoteCatalog(Repo.BuiltIn, null, new InMemoryRemoteApprovalStore());
        var push = new CatalogLayoutSource(catalog).Current;
        Assert.Empty(push.Omitted);
        Assert.Equal(["browser-video", "plex", "stremio", "vlc"], push.Included.Order().ToArray());
        Assert.True(Encoding.UTF8.GetByteCount(push.Json) <= RemoteLayoutPush.MaxJsonBytes, $"{Encoding.UTF8.GetByteCount(push.Json)} bytes");
    }

    [Fact]
    public void Source_announces_catalog_changes_and_has_no_json_without_runnable_remotes()
    {
        using var f = new Fixture(Json.Remote(Json.Button("""{ "do": "text", "text": "hello" }""")));
        using var source = new CatalogLayoutSource(f.Catalog);
        Assert.Null(source.CurrentJson); // needs review: not runnable, nothing to show
        var changes = 0;
        source.Changed += (_, _) => changes++;

        Assert.Null(f.Catalog.Approve("test", f.Hash));
        Assert.True(changes >= 1);
        Assert.Contains("\"id\":\"test\"", source.CurrentJson);
        Assert.DoesNotContain("hello", source.CurrentJson); // the action never reaches the phone
    }
}
