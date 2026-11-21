using System.Reflection;
using Microsoft.AspNetCore.StaticFiles;

namespace EasyMouse.Server;

/// <summary>
/// The web client served at <c>/</c>. Normally the contents of <c>web/dist</c> embedded in this assembly
/// as <c>web/*</c> resources; when the web client has not been built, a one-page stub is served instead.
/// </summary>
public sealed class StaticAssets
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();
    private readonly Dictionary<string, byte[]> _files;

    public StaticAssets(IReadOnlyDictionary<string, byte[]> files)
    {
        _files = new Dictionary<string, byte[]>(files, StringComparer.Ordinal);
        if (!_files.ContainsKey("index.html")) _files["index.html"] = StubPage;
    }

    public bool IsStub => _files.Count == 1 && ReferenceEquals(_files["index.html"], StubPage);

    private static readonly byte[] StubPage = System.Text.Encoding.UTF8.GetBytes(
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Easy Mouse</title></head>" +
        "<body><p>Easy Mouse is running, but the web client was not built into this binary.</p></body></html>");

    public static StaticAssets FromAssembly(Assembly assembly)
    {
        const string prefix = "web/";
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            files[name[prefix.Length..].Replace('\\', '/')] = ms.ToArray();
        }
        return new StaticAssets(files);
    }

    public bool TryGet(string requestPath, out byte[] content, out string contentType)
    {
        content = [];
        contentType = "application/octet-stream";
        var path = requestPath.TrimStart('/');
        if (path.Length == 0) path = "index.html";
        if (path.Contains("..", StringComparison.Ordinal) || path.Contains('\\')) return false;
        if (!_files.TryGetValue(path, out var bytes)) return false;
        content = bytes;
        if (ContentTypes.TryGetContentType(path, out var type)) contentType = type;
        return true;
    }
}
