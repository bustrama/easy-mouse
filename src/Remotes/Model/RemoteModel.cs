namespace EasyMouse.Remotes.Model;

public enum ItemType { Button, Slider, Label, Pad }

/// <summary>A validated remote. Only <see cref="RemoteValidator"/> creates these.</summary>
public sealed record RemoteDefinition(
    string Id,
    string Name,
    string? Description,
    string? TargetProcess,
    int Cols,
    IReadOnlyList<RemoteItem> Items)
{
    public RemoteItem? FindItem(string itemId) => Items.FirstOrDefault(i => i.Id == itemId);
}

/// <param name="Id">Effective id: the explicit "id", or the zero-based position as text (docs/protocol.md).</param>
public sealed record RemoteItem(
    int Index,
    string Id,
    ItemType Type,
    string? Icon,
    string? Label,
    string? Text,
    string? Bind,
    IReadOnlyList<RemoteAction> Actions);

public abstract record RemoteAction
{
    /// <summary>The "do" value in the JSON.</summary>
    public abstract string Kind { get; }
}

public sealed record KeyPressAction(KeyChord Chord, string Keys, int Repeat) : RemoteAction { public override string Kind => "key"; }
public sealed record TextAction(string Text) : RemoteAction { public override string Kind => "text"; }
public enum MediaCmd { PlayPause, Next, Prev, Stop, VolUp, VolDown, Mute }
public sealed record MediaAction(MediaCmd Cmd) : RemoteAction { public override string Kind => "media"; }
public enum MouseCmd { Click, RightClick, MiddleClick, DoubleClick, ScrollUp, ScrollDown }
public sealed record MouseAction(MouseCmd Cmd) : RemoteAction { public override string Kind => "mouse"; }
public enum SmtcCmd { Toggle, Play, Pause, Next, Prev, Stop, SeekForward, SeekBack }
/// <param name="Seconds">Seek distance for SeekForward/SeekBack; 0 otherwise.</param>
public sealed record SmtcAction(SmtcCmd Cmd, int Seconds) : RemoteAction { public override string Kind => "smtc"; }
public enum VolumeScope { Master, App }
public enum VolumeCmd { Set, Up, Down, Mute, Unmute, ToggleMute }
/// <param name="Level">For Set from a button (0-100); null when a slider supplies the value.</param>
public sealed record VolumeAction(VolumeScope Scope, VolumeCmd Cmd, int? Level, int Step) : RemoteAction { public override string Kind => "volume"; }
public sealed record FocusAction : RemoteAction { public override string Kind => "focus"; }
/// <param name="Slot">Name of the executable slot the user fills in on the PC when approving the remote.</param>
public sealed record LaunchAction(string Slot, IReadOnlyList<string> Args) : RemoteAction { public override string Kind => "launch"; }
public sealed record OpenUrlAction(string Url) : RemoteAction { public override string Kind => "openUrl"; }
public sealed record HttpAction(
    string Method, string Url, IReadOnlyDictionary<string, string> Headers, string? Body, int TimeoutMs) : RemoteAction
{
    public override string Kind => "http";
}
public sealed record DelayAction(int Ms) : RemoteAction { public override string Kind => "delay"; }

/// <summary>A key plus modifiers, e.g. Ctrl+Shift+Right. Key and modifiers are Windows virtual-key codes.</summary>
public sealed record KeyChord(IReadOnlyList<ushort> Modifiers, ushort Key);
