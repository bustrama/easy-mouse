namespace EasyMouse.Core.Protocol;

/// <summary>Frame opcodes (first plaintext byte).</summary>
public enum Op : byte
{
    Move = 0x01,
    Button = 0x02,
    Wheel = 0x03,
    Text = 0x04,
    Key = 0x05,
    Media = 0x06,
    Json = 0x10,
    State = 0x11,
    Art = 0x12,
    Ping = 0x7F,
}

/// <summary>Image format of an 0x12 art frame.</summary>
public enum ArtworkType : byte { Jpeg = 0, Png = 1 }

public enum MouseButton : byte { Left = 0, Right = 1, Middle = 2 }

[Flags]
public enum KeyAction : byte { Down = 1, Up = 2, Press = 3 }

public enum MediaCommand : byte
{
    PlayPause = 0,
    Next = 1,
    Previous = 2,
    Stop = 3,
    VolumeUp = 4,
    VolumeDown = 5,
    Mute = 6,
}
