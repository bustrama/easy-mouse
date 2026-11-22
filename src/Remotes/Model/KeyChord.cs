namespace EasyMouse.Remotes.Model;

public static class KeyParser
{
    public const ushort Ctrl = 0xA2, Alt = 0xA4, Shift = 0xA0, Win = 0x5B;

    private static readonly Dictionary<string, ushort> Mods = new(StringComparer.OrdinalIgnoreCase)
    { ["Ctrl"] = Ctrl, ["Alt"] = Alt, ["Shift"] = Shift };

    private static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = 0x0D, ["Escape"] = 0x1B, ["Space"] = 0x20, ["Tab"] = 0x09, ["Backspace"] = 0x08,
        ["Delete"] = 0x2E, ["Insert"] = 0x2D, ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
        ["Plus"] = 0xBB, ["Minus"] = 0xBD, ["Comma"] = 0xBC, ["Period"] = 0xBE, ["Slash"] = 0xBF,
        ["LeftBracket"] = 0xDB, ["RightBracket"] = 0xDD,
    };

    /// <summary>Parses "Ctrl+Shift+Right", "F", "F11". The Win key is not allowed. Returns an error text or null.</summary>
    public static string? TryParse(string keys, out KeyChord chord)
    {
        chord = new KeyChord([], 0);
        var parts = keys.Split('+');
        // "Plus" is spelled out, so a literal '+' is never ambiguous.
        var mods = new List<ushort>();
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!Mods.TryGetValue(parts[i], out var m)) return $"unknown modifier \"{parts[i]}\" (Ctrl, Alt, Shift)";
            if (mods.Contains(m)) return $"modifier \"{parts[i]}\" repeated";
            mods.Add(m);
        }
        var k = parts[^1];
        ushort vk;
        if (k.Length == 1 && char.IsAsciiLetter(k[0])) vk = (ushort)char.ToUpperInvariant(k[0]);
        else if (k.Length == 1 && char.IsAsciiDigit(k[0])) vk = (ushort)k[0];
        else if (k.Length is 2 or 3 && (k[0] is 'F' or 'f') && int.TryParse(k.AsSpan(1), out var f) && f is >= 1 and <= 24)
            vk = (ushort)(0x6F + f);
        else if (!Named.TryGetValue(k, out vk)) return $"unknown key \"{k}\"";
        chord = new KeyChord(mods, vk);
        return null;
    }
}
