using EasyMouse.Core.Protocol;

namespace EasyMouse.Core.Abstractions;

/// <summary>Injects validated input into the PC. The Windows implementation (SendInput) lives in the App.</summary>
public interface IInputSink
{
    void MoveRelative(int dx, int dy);
    void Button(MouseButton button, bool down);
    /// <summary>Wheel delta in units of 1/120 notch.</summary>
    void Wheel(int dx, int dy);
    /// <summary>Layout-independent text (Unicode), at most 1024 UTF-8 bytes.</summary>
    void Text(string text);
    void Key(ushort virtualKey, KeyAction action);
    /// <summary>
    /// Lifts every key and mouse button this sink is still holding down. The session calls it when the controlling
    /// phone goes away, so a dropped connection never leaves Ctrl or a mouse button pressed on the PC.
    /// </summary>
    void ReleaseAll() { }
}
