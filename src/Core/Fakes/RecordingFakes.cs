using EasyMouse.Core.Abstractions;
using EasyMouse.Core.Protocol;

namespace EasyMouse.Core.Fakes;

/// <summary>Records every call as a readable string, in order. For tests and headless runs.</summary>
public sealed class RecordingInputSink : IInputSink
{
    private readonly object _gate = new();
    private readonly List<string> _calls = [];

    public IReadOnlyList<string> Calls { get { lock (_gate) return [.. _calls]; } }

    private void Record(string call) { lock (_gate) _calls.Add(call); }

    public void MoveRelative(int dx, int dy) => Record($"move {dx} {dy}");
    public void Button(MouseButton button, bool down) => Record($"button {button} {(down ? "down" : "up")}");
    public void Wheel(int dx, int dy) => Record($"wheel {dx} {dy}");
    public void Text(string text) => Record($"text {text}");
    public void Key(ushort virtualKey, KeyAction action) => Record($"key {virtualKey} {action}");
}

public sealed class RecordingMediaController : IMediaController
{
    private readonly object _gate = new();
    private readonly List<MediaCommand> _commands = [];
    private MediaSnapshot _snapshot = new(null, null);

    public event EventHandler? Changed;

    public IReadOnlyList<MediaCommand> Commands { get { lock (_gate) return [.. _commands]; } }

    public void Execute(MediaCommand command) { lock (_gate) _commands.Add(command); }

    public MediaSnapshot GetSnapshot() { lock (_gate) return _snapshot; }

    public void SetSnapshot(MediaSnapshot snapshot)
    {
        lock (_gate) _snapshot = snapshot;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
