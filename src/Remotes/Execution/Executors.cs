using EasyMouse.Core.Abstractions;
using EasyMouse.Core.Protocol;
using EasyMouse.Remotes.Model;

namespace EasyMouse.Remotes.Execution;

/// <param name="Value">Slider position (0-100) for a slider item; null for buttons.</param>
public sealed record ActionContext(string RemoteId, string? TargetProcess, double? Value);

/// <summary>Runs one kind of action. The Windows implementations live in the platform layer; tests use recorders.</summary>
public interface IActionExecutor<in TAction>
{
    Task ExecuteAsync(TAction action, ActionContext context, CancellationToken cancellationToken);
}

/// <summary>A launch the handler already checked: the program is the one the user picked for this remote.</summary>
public sealed record ResolvedLaunch(string ExePath, IReadOnlyList<string> Args);

/// <summary>An http call the handler already checked (host allowed) and filled in (secrets substituted). Never log it.</summary>
public sealed record ResolvedHttp(string Method, Uri Url, IReadOnlyDictionary<string, string> Headers, string? Body, TimeSpan Timeout);

/// <summary>An executor failed or refused. <see cref="Code"/> is the short protocol error code; the message never holds secrets.</summary>
public sealed class ActionFailedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>The executors a host provides. A missing one makes that action kind answer "unsupported".</summary>
public sealed class ActionExecutors
{
    public IActionExecutor<KeyPressAction>? Key { get; init; }
    public IActionExecutor<TextAction>? Text { get; init; }
    public IActionExecutor<MediaAction>? Media { get; init; }
    public IActionExecutor<MouseAction>? Mouse { get; init; }
    public IActionExecutor<SmtcAction>? Smtc { get; init; }
    public IActionExecutor<VolumeAction>? Volume { get; init; }
    public IActionExecutor<FocusAction>? Focus { get; init; }
    public IActionExecutor<ResolvedLaunch>? Launch { get; init; }
    public IActionExecutor<OpenUrlAction>? OpenUrl { get; init; }
    public IActionExecutor<ResolvedHttp>? Http { get; init; }
}

// ---- implementations on top of the Core abstractions (no Win32) ----

public sealed class InputSinkKeyExecutor(IInputSink sink) : IActionExecutor<KeyPressAction>
{
    public Task ExecuteAsync(KeyPressAction action, ActionContext context, CancellationToken cancellationToken)
    {
        for (var n = 0; n < action.Repeat; n++)
        {
            var downed = new List<ushort>();
            try
            {
                foreach (var m in action.Chord.Modifiers) { sink.Key(m, Core.Protocol.KeyAction.Down); downed.Add(m); }
                sink.Key(action.Chord.Key, Core.Protocol.KeyAction.Press);
            }
            finally
            {
                for (var i = downed.Count - 1; i >= 0; i--) sink.Key(downed[i], Core.Protocol.KeyAction.Up);
            }
        }
        return Task.CompletedTask;
    }
}

public sealed class InputSinkTextExecutor(IInputSink sink) : IActionExecutor<TextAction>
{
    public Task ExecuteAsync(TextAction action, ActionContext context, CancellationToken cancellationToken)
    {
        sink.Text(action.Text);
        return Task.CompletedTask;
    }
}

public sealed class InputSinkMouseExecutor(IInputSink sink) : IActionExecutor<MouseAction>
{
    public Task ExecuteAsync(MouseAction action, ActionContext context, CancellationToken cancellationToken)
    {
        switch (action.Cmd)
        {
            case MouseCmd.Click: Click(MouseButton.Left); break;
            case MouseCmd.RightClick: Click(MouseButton.Right); break;
            case MouseCmd.MiddleClick: Click(MouseButton.Middle); break;
            case MouseCmd.DoubleClick: Click(MouseButton.Left); Click(MouseButton.Left); break;
            case MouseCmd.ScrollUp: sink.Wheel(0, 120); break;
            case MouseCmd.ScrollDown: sink.Wheel(0, -120); break;
        }
        return Task.CompletedTask;
    }

    private void Click(MouseButton b) { sink.Button(b, true); sink.Button(b, false); }
}

public sealed class MediaControllerExecutor(IMediaController media) : IActionExecutor<MediaAction>
{
    public Task ExecuteAsync(MediaAction action, ActionContext context, CancellationToken cancellationToken)
    {
        media.Execute(action.Cmd switch
        {
            MediaCmd.PlayPause => MediaCommand.PlayPause,
            MediaCmd.Next => MediaCommand.Next,
            MediaCmd.Prev => MediaCommand.Previous,
            MediaCmd.Stop => MediaCommand.Stop,
            MediaCmd.VolUp => MediaCommand.VolumeUp,
            MediaCmd.VolDown => MediaCommand.VolumeDown,
            _ => MediaCommand.Mute,
        });
        return Task.CompletedTask;
    }
}

// ---- recording fakes ----

/// <summary>Records every call; can be told to fail. For tests and headless runs.</summary>
public sealed class RecordingExecutor<TAction> : IActionExecutor<TAction>
{
    private readonly object _gate = new();
    private readonly List<(TAction Action, ActionContext Context)> _calls = [];

    public Exception? Throw { get; set; }
    public IReadOnlyList<(TAction Action, ActionContext Context)> Calls { get { lock (_gate) return [.. _calls]; } }

    public Task ExecuteAsync(TAction action, ActionContext context, CancellationToken cancellationToken)
    {
        lock (_gate) _calls.Add((action, context));
        return Throw is null ? Task.CompletedTask : Task.FromException(Throw);
    }
}

public sealed class RecordingActionExecutors
{
    public RecordingExecutor<KeyPressAction> Key { get; } = new();
    public RecordingExecutor<TextAction> Text { get; } = new();
    public RecordingExecutor<MediaAction> Media { get; } = new();
    public RecordingExecutor<MouseAction> Mouse { get; } = new();
    public RecordingExecutor<SmtcAction> Smtc { get; } = new();
    public RecordingExecutor<VolumeAction> Volume { get; } = new();
    public RecordingExecutor<FocusAction> Focus { get; } = new();
    public RecordingExecutor<ResolvedLaunch> Launch { get; } = new();
    public RecordingExecutor<OpenUrlAction> OpenUrl { get; } = new();
    public RecordingExecutor<ResolvedHttp> Http { get; } = new();

    public ActionExecutors Executors => new()
    {
        Key = Key, Text = Text, Media = Media, Mouse = Mouse, Smtc = Smtc, Volume = Volume, Focus = Focus,
        Launch = Launch, OpenUrl = OpenUrl, Http = Http,
    };

    public int TotalCalls => Key.Calls.Count + Text.Calls.Count + Media.Calls.Count + Mouse.Calls.Count + Smtc.Calls.Count
        + Volume.Calls.Count + Focus.Calls.Count + Launch.Calls.Count + OpenUrl.Calls.Count + Http.Calls.Count;
}
