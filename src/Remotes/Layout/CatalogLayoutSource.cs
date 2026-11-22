using EasyMouse.Core.Abstractions;
using EasyMouse.Remotes.Catalog;

namespace EasyMouse.Remotes.Layout;

/// <summary>
/// Feeds the Core's layout push from an <see cref="IRemoteCatalog"/>: the current <see cref="RemoteLayoutPush"/>, re-announced
/// whenever the catalog changes. The Core skips a push whose text did not change, so the many reloads that change nothing are free.
/// </summary>
public sealed class CatalogLayoutSource : IRemoteLayoutSource, IDisposable
{
    private readonly IRemoteCatalog _catalog;

    public CatalogLayoutSource(IRemoteCatalog catalog)
    {
        _catalog = catalog;
        _catalog.Changed += OnChanged;
    }

    public event EventHandler? Changed;

    /// <summary>Null when no remote is runnable (nothing to show).</summary>
    public string? CurrentJson => Current.Included.Count == 0 ? null : Current.Json;

    /// <summary>What was and was not included in the push (remotes that did not fit in one frame are omitted).</summary>
    public LayoutPush Current => RemoteLayoutPush.Build(_catalog);

    private void OnChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose() => _catalog.Changed -= OnChanged;
}
