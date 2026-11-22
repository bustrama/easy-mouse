using EasyMouse.App.Logic;
using EasyMouse.Core;
using EasyMouse.Core.Abstractions;
using EasyMouse.Core.Devices;

namespace EasyMouse.App.Tests;

/// <summary>DEPRECATED: Remotes is disabled for 0.1 (docs/remotes.md). With the default environment the app loads, pushes and runs nothing.</summary>
public class RemotesDisabledTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "em-remotes-off-" + Guid.NewGuid().ToString("N"));
    private AppController? _app;

    private sealed class NoopProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plain) => plain;
        public byte[] Unprotect(byte[] data) => data;
    }

    [Fact]
    public async Task Flag_is_off_and_the_default_app_loads_pushes_and_runs_no_remote()
    {
        Assert.False(FeatureFlags.Remotes);
        var builtIn = Path.Combine(AppContext.BaseDirectory, "builtin-present");
        Directory.CreateDirectory(builtIn);
        File.WriteAllText(Path.Combine(builtIn, "x.json"), """{ "schema": 1, "id": "x", "name": "X", "layout": { "cols": 1, "items": [ { "type": "button", "label": "Go", "action": { "do": "key", "keys": "G" } } ] } }""");
        Directory.CreateDirectory(_dir);
        _app = new AppController(new AppPaths(_dir) { BuiltInRemotes = builtIn }, new AppEnvironment
        {
            SystemIntegration = false, Protector = new NoopProtector(), RemoteProtector = new NoopProtector(),
        });

        Assert.Empty(_app.Remotes.Entries);
        Assert.False(Directory.Exists(_app.Paths.UserRemotes));
        Assert.Null(_app.Core.Layouts.CurrentJson);
        var result = await _app.Core.Actions.InvokeAsync(new RemoteInvocation("x", "0", null), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("unsupported", result.Error);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
