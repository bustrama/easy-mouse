using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace EasyMouse.Platform.Windows.Audio;

// Hand-written Core Audio COM (source-generated, so trimming and AOT safe). Unused methods are declared only to keep
// the vtable slots right; they take raw pointers and are never called.

[GeneratedComInterface]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal partial interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out nint devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[GeneratedComInterface]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal partial interface IMMDevice
{
    [PreserveSig] int Activate(in Guid iid, uint clsCtx, nint activationParams, out nint iface);
    [PreserveSig] int OpenPropertyStore(uint access, out nint properties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out uint state);
}

[GeneratedComInterface]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
internal partial interface IMMNotificationClient
{
    [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint newState);
    [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);
    [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, nint key);
}

[GeneratedComInterface]
[Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
internal partial interface IAudioEndpointVolumeCallback
{
    [PreserveSig] int OnNotify(nint notifyData);
}

[GeneratedComInterface]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
internal partial interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
    [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
    [PreserveSig] int GetChannelCount(out uint count);
    [PreserveSig] int SetMasterVolumeLevel(float db, in Guid eventContext);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, in Guid eventContext);
    [PreserveSig] int GetMasterVolumeLevel(out float db);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig] int SetChannelVolumeLevel(uint channel, float db, in Guid eventContext);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, in Guid eventContext);
    [PreserveSig] int GetChannelVolumeLevel(uint channel, out float db);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, in Guid eventContext);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
    [PreserveSig] int VolumeStepUp(in Guid eventContext);
    [PreserveSig] int VolumeStepDown(in Guid eventContext);
    [PreserveSig] int QueryHardwareSupport(out uint mask);
    [PreserveSig] int GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
}

[GeneratedComClass]
internal sealed partial class VolumeCallback(Action onChange) : IAudioEndpointVolumeCallback
{
    public int OnNotify(nint notifyData) { onChange(); return 0; }
}

[GeneratedComClass]
internal sealed partial class DeviceNotificationClient(Action onDefaultRenderChanged) : IMMNotificationClient
{
    private const int eRender = 0, eConsole = 0;
    public int OnDeviceStateChanged(string deviceId, uint newState) => 0;
    public int OnDeviceAdded(string deviceId) => 0;
    public int OnDeviceRemoved(string deviceId) => 0;
    public int OnDefaultDeviceChanged(int flow, int role, string? defaultDeviceId)
    {
        if (flow == eRender && role == eConsole) onDefaultRenderChanged();
        return 0;
    }
    public int OnPropertyValueChanged(string deviceId, nint key) => 0;
}

internal static partial class Ole32
{
    public const uint CLSCTX_ALL = 0x17;
    public static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    public static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint clsContext, in Guid iid, out nint instance);

    public static T CreateInstance<T>(in Guid clsid, in Guid iid) where T : class
    {
        Marshal.ThrowExceptionForHR(CoCreateInstance(clsid, 0, CLSCTX_ALL, iid, out var ptr));
        return Wrap<T>(ptr);
    }

    public static T Wrap<T>(nint comPointer) where T : class
    {
        try { return (T)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(comPointer, CreateObjectFlags.UniqueInstance); }
        finally { Marshal.Release(comPointer); }
    }
}
