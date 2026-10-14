using System.Runtime.InteropServices;

namespace Wattkin.Platform;

/// <summary>
/// Default speaker / microphone mute through Core Audio. Used to (a) make the laptop's mic-mute key
/// actually mute (MSI did this in software) and (b) keep the keyboard's mute LEDs in sync.
/// Event-driven: Windows calls back on mute changes and default-device changes, so nothing polls.
/// Callbacks arrive on COM worker threads; <see cref="Changed"/> is raised on the thread pool.
/// </summary>
internal sealed class AudioMute : IDisposable
{
    private static readonly Guid IAudioEndpointVolumeId = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private const int CLSCTX_ALL = 0x17;

    private readonly object _gate = new();
    private readonly VolumeCallback _volumeCallback;
    private readonly DeviceCallback _deviceCallback;
    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _speaker;
    private IAudioEndpointVolume? _microphone;
    private bool _started;
    private bool _disposed;

    public AudioMute()
    {
        _volumeCallback = new VolumeCallback(this);
        _deviceCallback = new DeviceCallback(this);
    }

    /// <summary>Mute state of the default speaker or microphone changed (or the default device did).</summary>
    public event Action? Changed;

    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed)
            {
                return;
            }

            try
            {
                _enumerator ??= (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                _enumerator.RegisterEndpointNotificationCallback(_deviceCallback);
                _started = true;
                Resolve();
            }
            catch (COMException ex)
            {
                Log.Error("Audio notifications unavailable", ex);
            }
        }
    }

    public bool? SpeakerMuted
    {
        get
        {
            lock (_gate)
            {
                EnsureResolved();
                return Get(_speaker);
            }
        }
    }

    public bool? MicrophoneMuted
    {
        get
        {
            lock (_gate)
            {
                EnsureResolved();
                return Get(_microphone);
            }
        }
    }

    public bool? ToggleMicrophone()
    {
        lock (_gate)
        {
            EnsureResolved();
            if (_microphone is null)
            {
                return null;
            }

            try
            {
                _microphone.GetMute(out var muted);
                var context = Guid.Empty;
                _microphone.SetMute(!muted, ref context);
                return !muted;
            }
            catch (COMException)
            {
                return null;
            }
        }
    }

    private static bool? Get(IAudioEndpointVolume? endpoint)
    {
        if (endpoint is null)
        {
            return null;
        }

        try
        {
            endpoint.GetMute(out var muted);
            return muted;
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>Used when LED sync is off (mic key only): resolve once, without callbacks.</summary>
    private void EnsureResolved()
    {
        if (_speaker is null && _microphone is null && !_disposed)
        {
            try
            {
                _enumerator ??= (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                Resolve();
            }
            catch (COMException)
            {
            }
        }
    }

    /// <summary>(Re)binds to the current default devices, releasing the previous ones.</summary>
    private void Resolve()
    {
        Release(ref _speaker);
        Release(ref _microphone);
        if (_enumerator is null)
        {
            return;
        }

        _speaker = Activate(_enumerator, EDataFlow.Render);
        _microphone = Activate(_enumerator, EDataFlow.Capture);
        if (!_started)
        {
            return;
        }

        foreach (var endpoint in new[] { _speaker, _microphone })
        {
            try
            {
                endpoint?.RegisterControlChangeNotify(_volumeCallback);
            }
            catch (COMException)
            {
            }
        }
    }

    private void Release(ref IAudioEndpointVolume? endpoint)
    {
        if (endpoint is null)
        {
            return;
        }

        try
        {
            if (_started)
            {
                endpoint.UnregisterControlChangeNotify(_volumeCallback);
            }
        }
        catch (COMException)
        {
        }

        Marshal.ReleaseComObject(endpoint);
        endpoint = null;
    }

    private static IAudioEndpointVolume? Activate(IMMDeviceEnumerator enumerator, EDataFlow flow)
    {
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(flow, ERole.Console, out var device) != 0 || device is null)
            {
                return null;
            }

            try
            {
                var iid = IAudioEndpointVolumeId;
                return device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var volume) == 0 ? volume as IAudioEndpointVolume : null;
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        catch (COMException)
        {
            return null;
        }
    }

    private void RaiseChanged() => Task.Run(() => Changed?.Invoke());

    private void OnDefaultDeviceChanged()
    {
        // Don't re-bind on the notification thread while Core Audio holds its own locks.
        Task.Run(() =>
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    Resolve();
                }
            }

            Changed?.Invoke();
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Release(ref _speaker);
            Release(ref _microphone);
            if (_enumerator is not null)
            {
                try
                {
                    if (_started)
                    {
                        _enumerator.UnregisterEndpointNotificationCallback(_deviceCallback);
                    }
                }
                catch (COMException)
                {
                }

                Marshal.ReleaseComObject(_enumerator);
                _enumerator = null;
            }
        }
    }

    // ---- COM plumbing ---------------------------------------------------------------------------

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class VolumeCallback(AudioMute owner) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr notifyData)
        {
            owner.RaiseChanged();
            return 0;
        }
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class DeviceCallback(AudioMute owner) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string deviceId, int newState) => 0;

        public int OnDeviceAdded(string deviceId) => 0;

        public int OnDeviceRemoved(string deviceId) => 0;

        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId)
        {
            if (role == ERole.Console)
            {
                owner.OnDefaultDeviceChanged();
            }

            return 0;
        }

        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
    }

    internal enum EDataFlow
    {
        Render = 0,
        Capture = 1,
    }

    internal enum ERole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice? device);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);

        void RegisterEndpointNotificationCallback(IMMNotificationClient client);

        void UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport]
    [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMNotificationClient
    {
        [PreserveSig]
        int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);

        [PreserveSig]
        int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        [PreserveSig]
        int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        [PreserveSig]
        int OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);

        [PreserveSig]
        int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? instance);
    }

    [ComImport]
    [Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolumeCallback
    {
        [PreserveSig]
        int OnNotify(IntPtr notifyData);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        void UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        void GetChannelCount(out uint count);
        void SetMasterVolumeLevel(float level, ref Guid context);
        void SetMasterVolumeLevelScalar(float level, ref Guid context);
        void GetMasterVolumeLevel(out float level);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        void GetChannelVolumeLevel(uint channel, out float level);
        void GetChannelVolumeLevelScalar(uint channel, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
