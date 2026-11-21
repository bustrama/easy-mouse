namespace EasyMouse.Core.Protocol;

/// <summary>A decoded plaintext frame.</summary>
public abstract record Message(Op Op);

public sealed record MoveMessage(short Dx, short Dy) : Message(Op.Move);
public sealed record ButtonMessage(MouseButton Button, bool Down) : Message(Op.Button);
public sealed record WheelMessage(short Dx, short Dy) : Message(Op.Wheel);
public sealed record TextMessage(string Text) : Message(Op.Text);
public sealed record KeyMessage(ushort VirtualKey, KeyAction Action) : Message(Op.Key);
public sealed record MediaMessage(MediaCommand Command) : Message(Op.Media);
/// <summary>0x10: UTF-8 JSON control message. Well-formedness is checked by the consumer.</summary>
public sealed record JsonMessage(string Json) : Message(Op.Json);
/// <summary>0x11: UTF-8 JSON state, server to client only.</summary>
public sealed record StateMessage(string Json) : Message(Op.State);
public sealed record PingMessage() : Message(Op.Ping);
