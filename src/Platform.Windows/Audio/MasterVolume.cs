using System.Runtime.InteropServices;
using EasyMouse.Core.Abstractions;

namespace EasyMouse.Platform.Windows.Audio;

/// <summary>
/// Master volume and mute of the default playback device (IAudioEndpointVolume). Follows default-device changes and
/// raises <see cref="Changed"/> (on a COM thread) when volume or mute change from anywhere, including the phone.
/// With no playback device present, <see cref="Read"/> returns null.
/// </summary>
internal sealed class MasterVolume : IDisposable
{
    private const int E_NOTFOUND = unchecked((int)0x80070490);
    private const int eRender = 0, eConsole = 0;
    private static readonly Guid s_noContext = Guid.Empty;

    private readonly object _gate = new();
    private readonly IMMDeviceEnumerator _enumerator;
    private readonly VolumeCallback _callback;
    private readonly DeviceNotificationClient _deviceClient;
    private IAudioEndpointVolume? _endpoint;
    private bool _disposed;

    public event Action? Changed;

    public MasterVolume()
    {
        _enumerator = Ole32.CreateInstance<IMMDeviceEnumerator>(Ole32.CLSID_MMDeviceEnumerator, Ole32.IID_IMMDeviceEnumerator);
        _callback = new VolumeCallback(() => Changed?.Invoke());
        _deviceClient = new DeviceNotificationClient(() => ThreadPool.QueueUserWorkItem(_ => Rebind()));
        Check(_enumerator.RegisterEndpointNotificationCallback(_deviceClient));
        Rebind();
    }

    public VolumeState? Read()
    {
        lock (_gate)
        {
            if (_endpoint is null) return null;
            Check(_endpoint.GetMasterVolumeLevelScalar(out var level));
            Check(_endpoint.GetMute(out var muted));
            return new VolumeState((int)Math.Round(level * 100f), muted);
        }
    }

    public void SetLevel(int percent)
    {
        lock (_gate)
        {
            if (_endpoint is null) throw new InvalidOperationException("No playback device.");
            Check(_endpoint.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, s_noContext));
        }
    }

    public void SetMuted(bool muted)
    {
        lock (_gate)
        {
            if (_endpoint is null) throw new InvalidOperationException("No playback device.");
            Check(_endpoint.SetMute(muted, s_noContext));
        }
    }

    private void Rebind()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_endpoint is not null)
            {
                _endpoint.UnregisterControlChangeNotify(_callback);
                _endpoint = null;
            }
            var hr = _enumerator.GetDefaultAudioEndpoint(eRender, eConsole, out var device);
            if (hr == E_NOTFOUND) { Changed?.Invoke(); return; } // no playback device right now
            Check(hr);
            Check(device.Activate(Ole32.IID_IAudioEndpointVolume, Ole32.CLSCTX_ALL, 0, out var ptr));
            var endpoint = Ole32.Wrap<IAudioEndpointVolume>(ptr);
            Check(endpoint.RegisterControlChangeNotify(_callback));
            _endpoint = endpoint;
        }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _enumerator.UnregisterEndpointNotificationCallback(_deviceClient);
            _endpoint?.UnregisterControlChangeNotify(_callback);
            _endpoint = null;
        }
    }

    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);
}
