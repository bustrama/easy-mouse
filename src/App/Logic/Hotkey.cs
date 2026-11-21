namespace EasyMouse.App.Logic;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x1,   // MOD_ALT
    Ctrl = 0x2,  // MOD_CONTROL
    Shift = 0x4, // MOD_SHIFT
    Win = 0x8,   // MOD_WIN
}

/// <summary>A global hotkey: modifiers plus a Win32 virtual-key code. Text form: <c>Ctrl+Alt+Shift+K</c>.</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, uint VirtualKey)
{
    private static readonly (string Name, uint Vk)[] NamedKeys =
    [
        ("Space", 0x20), ("Enter", 0x0D), ("Tab", 0x09), ("Esc", 0x1B), ("Backspace", 0x08),
        ("PageUp", 0x21), ("PageDown", 0x22), ("End", 0x23), ("Home", 0x24),
        ("Left", 0x25), ("Up", 0x26), ("Right", 0x27), ("Down", 0x28),
        ("Insert", 0x2D), ("Delete", 0x2E), ("Pause", 0x13),
    ];

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var mods = HotkeyModifiers.None;
        uint? vk = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0) return false;
            var mod = raw.ToLowerInvariant() switch
            {
                "ctrl" or "control" => HotkeyModifiers.Ctrl,
                "alt" => HotkeyModifiers.Alt,
                "shift" => HotkeyModifiers.Shift,
                "win" or "windows" => HotkeyModifiers.Win,
                _ => HotkeyModifiers.None,
            };
            if (mod != HotkeyModifiers.None)
            {
                if (mods.HasFlag(mod)) return false;
                mods |= mod;
                continue;
            }
            if (vk is not null) return false; // exactly one non-modifier key
            vk = ParseKey(raw);
            if (vk is null) return false;
        }
        // A bare key (or Shift + key) would hijack normal typing, so require Ctrl, Alt or Win.
        if (vk is null || (mods & (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Win)) == 0) return false;
        hotkey = new Hotkey(mods, vk.Value);
        return true;
    }

    private static uint? ParseKey(string name)
    {
        if (name.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
        }
        if ((name[0] is 'F' or 'f') && int.TryParse(name.AsSpan(1), out var f) && f is >= 1 and <= 24) return (uint)(0x70 + f - 1);
        foreach (var (n, vk) in NamedKeys)
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return vk;
        return null;
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyName(VirtualKey));
        return string.Join('+', parts);
    }

    private static string KeyName(uint vk)
    {
        if (vk is >= 'A' and <= 'Z' or >= '0' and <= '9') return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x70 + 1);
        foreach (var (n, v) in NamedKeys) if (v == vk) return n;
        return "0x" + vk.ToString("X2");
    }
}

public static class HotkeyExtensions
{
    public static bool TryParseHotkey(this string? text, out Hotkey hotkey) => Hotkey.TryParse(text, out hotkey);
}
