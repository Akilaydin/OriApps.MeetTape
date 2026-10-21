using OriApps.MeetTape.Settings;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace OriApps.MeetTape.Audio;

public enum CaptureChannel { Me, Them }

public sealed record AudioDevice(string Id, string Name, CaptureChannel Channel, bool IsDefault);

internal enum EndpointChangeKind { DefaultChanged, Added, Removed, StateChanged }
internal sealed record EndpointChange(EndpointChangeKind Kind, string? Id, CaptureChannel? Channel = null, bool Active = false);

internal sealed class CaptureSource(IWaveIn audio, IDisposable? endpoint = null, AudioDevice? info = null) : IDisposable
{
    private readonly object lifecycle = new();
    private bool active, stopped;
    public IWaveIn Audio { get; } = audio;
    public AudioDevice? Info { get; } = info;
    internal bool Activate()
    {
        lock (lifecycle)
        {
            if (stopped) return false;
            active = true;
            return true;
        }
    }

    // An endpoint can stop while its replacement is still in quarantine.
    // Remember that failure, or deliver it after activation, without losing the event.
    internal bool ObserveStop()
    {
        lock (lifecycle) { stopped = true; return active; }
    }

    public void Dispose()
    {
        try { Audio.Dispose(); }
        finally { endpoint?.Dispose(); }
    }
}

public static class AudioDevices
{
    public static IReadOnlyList<AudioDevice> List()
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<AudioDevice>();
        foreach (var (flow, channel) in new[] { (DataFlow.Capture, CaptureChannel.Me), (DataFlow.Render, CaptureChannel.Them) })
        {
            string? defaultId = null;
            if (enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia))
            {
                using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                defaultId = device.ID;
            }
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                    result.Add(new(device.ID, device.FriendlyName, channel, device.ID == defaultId));
            }
        }
        return result;
    }

    public static CaptureSources Open(CaptureSettings settings)
    {
        CaptureSource? me = null;
        CaptureSource? them = null;
        try
        {
            me = OpenChannel(settings, CaptureChannel.Me);
            them = OpenChannel(settings, CaptureChannel.Them);
            return new CaptureSources(me, them)
            {
                MeDevice = me.Info,
                ThemDevice = them.Info,
                Reopen = channel => OpenChannel(settings, channel),
                Observe = Observe
            };
        }
        catch
        {
            me?.Dispose();
            them?.Dispose();
            throw;
        }
    }

    private static CaptureSource OpenChannel(CaptureSettings settings, CaptureChannel channel)
    {
        bool me = channel == CaptureChannel.Me;
        string? selectedId = me ? settings.MicrophoneDeviceId : settings.PlaybackDeviceId;
        using var enumerator = new MMDeviceEnumerator();
        var endpoint = Select(enumerator, selectedId, me ? DataFlow.Capture : DataFlow.Render);
        try
        {
            var source = new WasapiPacketCapture(endpoint, loopback: !me,
                me ? settings.MicrophoneMode : MicrophoneCaptureMode.WindowsDefault);
            return new(source, endpoint, new(endpoint.ID, endpoint.FriendlyName, channel, selectedId is null));
        }
        catch { endpoint.Dispose(); throw; }
    }

    private static IDisposable Observe(Action<EndpointChange> changed)
    {
        var enumerator = new MMDeviceEnumerator();
        try
        {
            var client = enumerator.CreateNotificationClient(useSynchronizationContext: false);
            client.DefaultDeviceChanged += (_, args) =>
            {
                if (args.Role != Role.Multimedia) return;
                CaptureChannel? channel = args.Flow switch
                {
                    DataFlow.Capture => CaptureChannel.Me,
                    DataFlow.Render => CaptureChannel.Them,
                    _ => null
                };
                if (channel is not null) changed(new(EndpointChangeKind.DefaultChanged, args.DeviceId, channel));
            };
            client.DeviceAdded += (_, args) => changed(new(EndpointChangeKind.Added, args.DeviceId));
            client.DeviceRemoved += (_, args) => changed(new(EndpointChangeKind.Removed, args.DeviceId));
            client.DeviceStateChanged += (_, args) => changed(new(EndpointChangeKind.StateChanged, args.DeviceId,
                Active: args.NewState == DeviceState.Active));
            return new EndpointObserver(enumerator, client);
        }
        catch { enumerator.Dispose(); throw; }
    }

    private sealed class EndpointObserver(MMDeviceEnumerator enumerator, MMDeviceNotificationClient client) : IDisposable
    {
        public void Dispose()
        {
            try { client.Dispose(); }
            finally { enumerator.Dispose(); }
        }
    }

    private static MMDevice Select(MMDeviceEnumerator enumerator, string? id, DataFlow flow)
    {
        var device = id is null
            ? enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia)
            : enumerator.GetDevice(id);
        if (device.State != DeviceState.Active || device.DataFlow != flow)
        {
            device.Dispose();
            throw new InvalidOperationException("Selected endpoint is unavailable or has the wrong capture/render direction.");
        }
        return device;
    }
}

public sealed class CaptureSources : IDisposable
{
    private CaptureSource? me;
    private CaptureSource? them;
    public CaptureSources(IWaveIn me, IWaveIn them, IDisposable? meDevice = null, IDisposable? themDevice = null)
        : this(new(me, meDevice), new(them, themDevice)) { }
    internal CaptureSources(CaptureSource me, CaptureSource them)
    {
        this.me = me; this.them = them;
    }
    public IWaveIn Me => me?.Audio ?? throw new InvalidOperationException("Me endpoint unavailable.");
    public IWaveIn Them => them?.Audio ?? throw new InvalidOperationException("Them endpoint unavailable.");
    public AudioDevice? MeDevice { get; init; }
    public AudioDevice? ThemDevice { get; init; }
    internal Func<CaptureChannel, CaptureSource>? Reopen { get; init; }
    internal Func<Action<EndpointChange>, IDisposable>? Observe { get; init; }
    internal CaptureSource? Current(CaptureChannel channel) => channel == CaptureChannel.Me ? Volatile.Read(ref me) : Volatile.Read(ref them);
    internal CaptureSource? Replace(CaptureChannel channel, CaptureSource? source)
    {
        return channel == CaptureChannel.Me ? Interlocked.Exchange(ref me, source) : Interlocked.Exchange(ref them, source);
    }

    public void Dispose()
    {
        try { Replace(CaptureChannel.Me, null)?.Dispose(); }
        finally { Replace(CaptureChannel.Them, null)?.Dispose(); }
    }
}
