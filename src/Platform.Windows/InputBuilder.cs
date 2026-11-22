using EasyMouse.Core.Protocol;
using EasyMouse.Platform.Windows.Native;

namespace EasyMouse.Platform.Windows;

/// <summary>Pure construction of INPUT records. No Win32 calls, so it is unit tested.</summary>
internal static class InputBuilder
{
    /// <summary>Largest wheel delta accepted in one action (50 notches). Protects against a runaway client.</summary>
    public const int MaxWheel = 120 * 50;

    public static INPUT Mouse(uint flags, int dx = 0, int dy = 0, int data = 0) => new()
    {
        type = InputConst.INPUT_MOUSE,
        u = { mi = { dx = dx, dy = dy, mouseData = unchecked((uint)data), dwFlags = flags } },
    };

    public static INPUT MouseMove(int dx, int dy) => Mouse(InputConst.MOUSEEVENTF_MOVE, dx, dy);

    public static INPUT MouseButton(MouseButton button, bool down) => Mouse(button switch
    {
        Core.Protocol.MouseButton.Left => down ? InputConst.MOUSEEVENTF_LEFTDOWN : InputConst.MOUSEEVENTF_LEFTUP,
        Core.Protocol.MouseButton.Right => down ? InputConst.MOUSEEVENTF_RIGHTDOWN : InputConst.MOUSEEVENTF_RIGHTUP,
        Core.Protocol.MouseButton.Middle => down ? InputConst.MOUSEEVENTF_MIDDLEDOWN : InputConst.MOUSEEVENTF_MIDDLEUP,
        _ => throw new ArgumentOutOfRangeException(nameof(button)),
    });

    /// <summary>Wheel events: vertical first, then horizontal. A zero axis produces no event. Positive dy = up, positive dx = right.</summary>
    public static int Wheel(int dx, int dy, Span<INPUT> destination)
    {
        var n = 0;
        if (dy != 0) destination[n++] = Mouse(InputConst.MOUSEEVENTF_WHEEL, data: ClampWheel(dy));
        if (dx != 0) destination[n++] = Mouse(InputConst.MOUSEEVENTF_HWHEEL, data: ClampWheel(dx));
        return n;
    }

    public static int ClampWheel(int delta) => Math.Clamp(delta, -MaxWheel, MaxWheel);

    /// <summary>Virtual keys that Windows only recognises with KEYEVENTF_EXTENDEDKEY (the grey keys, right-hand Ctrl/Alt/Win).</summary>
    public static bool IsExtendedKey(ushort vk) => vk switch
    {
        0x21 or 0x22 or 0x23 or 0x24 => true, // PageUp, PageDown, End, Home
        0x25 or 0x26 or 0x27 or 0x28 => true, // Left, Up, Right, Down
        0x2C or 0x2D or 0x2E => true,         // PrintScreen, Insert, Delete
        0x5B or 0x5C or 0x5D => true,         // LWin, RWin, Apps
        0x6F => true,                         // Numpad divide
        0x90 => true,                         // NumLock
        0xA3 or 0xA5 => true,                 // RControl, RMenu
        _ => false,
    };

    public static INPUT Key(ushort vk, bool down)
    {
        var flags = (IsExtendedKey(vk) ? InputConst.KEYEVENTF_EXTENDEDKEY : 0u) | (down ? 0u : InputConst.KEYEVENTF_KEYUP);
        return new INPUT { type = InputConst.INPUT_KEYBOARD, u = { ki = { wVk = vk, dwFlags = flags } } };
    }

    public static INPUT UnicodeUnit(char unit, bool down) => new()
    {
        type = InputConst.INPUT_KEYBOARD,
        u = { ki = { wScan = unit, dwFlags = InputConst.KEYEVENTF_UNICODE | (down ? 0u : InputConst.KEYEVENTF_KEYUP) } },
    };

    /// <summary>
    /// Text to key events, layout independent. Each UTF-16 unit becomes a down/up pair (a surrogate pair is two
    /// units, which Windows reassembles). CR, LF and CRLF become one Enter, tab becomes Tab, backspace stays Backspace;
    /// other control characters and unpaired surrogates are dropped.
    /// </summary>
    public static List<INPUT> Text(ReadOnlySpan<char> text)
    {
        var list = new List<INPUT>(text.Length * 2);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                AddPress(list, InputConst.VK_RETURN);
            }
            else if (c == '\t') AddPress(list, InputConst.VK_TAB);
            else if (c == '\b') AddPress(list, InputConst.VK_BACK);
            else if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    AddUnit(list, c);
                    AddUnit(list, text[++i]);
                }
            }
            else if (char.IsLowSurrogate(c) || char.IsControl(c)) { /* dropped */ }
            else AddUnit(list, c);
        }
        return list;
    }

    private static void AddUnit(List<INPUT> list, char c)
    {
        list.Add(UnicodeUnit(c, true));
        list.Add(UnicodeUnit(c, false));
    }

    private static void AddPress(List<INPUT> list, ushort vk)
    {
        list.Add(Key(vk, true));
        list.Add(Key(vk, false));
    }
}
