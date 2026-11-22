using System.Text;
using System.Text.Json;
using EasyMouse.Remotes.Catalog;
using EasyMouse.Remotes.Model;

namespace EasyMouse.Remotes.Layout;

public sealed record LayoutPush(string Json, IReadOnlyList<string> Included, IReadOnlyList<string> Omitted);

/// <summary>
/// Builds the S-to-C <c>{"t":"remotes",...}</c> message (docs/protocol.md, "0x10 json, remote layouts").
/// Only runnable remotes are sent, as a projection without actions, target or description: the phone
/// learns what to draw and which item ids exist, nothing about what the PC will do.
/// </summary>
public static class RemoteLayoutPush
{
    public const int MaxRemotes = 32;
    /// <summary>
    /// The Core puts no cap on messages it sends, but the web client rejects any frame over 4096 + 64 bytes
    /// (web/src/protocol/crypto.ts). A frame is 8 bytes header + 1 op byte + JSON + 16 bytes tag, so the JSON
    /// can be 4135 bytes; 4000 leaves room. The phone replaces its list on every <c>remotes</c> message, so the
    /// whole set must fit in one message.
    /// </summary>
    public const int MaxJsonBytes = 4000;

    public static LayoutPush Build(IRemoteCatalog catalog, int maxBytes = MaxJsonBytes)
    {
        var runnable = catalog.Entries.Where(e => e.State == RemoteState.Ready && e.Definition is not null)
            .Select(e => e.Definition!).Take(MaxRemotes).ToList();
        var included = new List<RemoteDefinition>();
        var omitted = new List<string>();
        foreach (var r in runnable)
        {
            included.Add(r);
            if (Encoding.UTF8.GetByteCount(Serialize(included)) > maxBytes)
            {
                included.RemoveAt(included.Count - 1);
                omitted.Add(r.Id);
            }
        }
        omitted.AddRange(catalog.Entries.Where(e => e.State == RemoteState.Ready && e.Definition is not null).Skip(MaxRemotes).Select(e => e.Id));
        return new LayoutPush(Serialize(included), included.Select(r => r.Id).ToList(), omitted);
    }

    public static string Serialize(IReadOnlyList<RemoteDefinition> remotes)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("t", "remotes");
            w.WriteStartArray("remotes");
            foreach (var r in remotes) WriteRemote(w, r);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteRemote(Utf8JsonWriter w, RemoteDefinition r)
    {
        w.WriteStartObject();
        w.WriteNumber("schema", RemoteValidator_SchemaVersion);
        w.WriteString("id", r.Id);
        w.WriteString("name", r.Name);
        w.WriteStartObject("layout");
        w.WriteNumber("cols", r.Cols);
        w.WriteStartArray("items");
        foreach (var i in r.Items)
        {
            w.WriteStartObject();
            w.WriteString("type", i.Type.ToString().ToLowerInvariant());
            // The id is the position unless set explicitly; omit it when it equals the position.
            if (i.Id != i.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)) w.WriteString("id", i.Id);
            if (i.Icon is not null) w.WriteString("icon", i.Icon);
            if (i.Label is not null) w.WriteString("label", i.Label);
            if (i.Text is not null) w.WriteString("text", i.Text);
            if (i.Bind is not null) w.WriteString("bind", i.Bind);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private const int RemoteValidator_SchemaVersion = Validation.RemoteValidator.SchemaVersion;
}
