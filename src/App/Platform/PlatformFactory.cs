using EasyMouse.Core.Abstractions;
using EasyMouse.Core.Fakes;

namespace EasyMouse.App.Platform;

/// <summary>
/// THE ONE PLACE where the PC-side implementations are chosen. Until <c>src/Platform.Windows</c> lands this returns the
/// recording fakes (input and media do nothing on the PC). Wiring the real SendInput and media/volume controllers is a
/// one-line change here: return <c>new WindowsInputSink()</c> and <c>new WindowsMediaController()</c>.
/// </summary>
public static class PlatformFactory
{
    public static (IInputSink Input, IMediaController Media) Create()
        => (new RecordingInputSink(), new RecordingMediaController());
}
