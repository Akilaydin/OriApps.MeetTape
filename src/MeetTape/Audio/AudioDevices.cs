using OriApps.MeetTape.Settings;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace OriApps.MeetTape.Audio;

public enum CaptureChannel { Me, Them }

public sealed record AudioDevice(string Id, string Name, CaptureChannel Channel, bool IsDefault);

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
        using var enumerator = new MMDeviceEnumerator();
        MMDevice? me = null;
        MMDevice? them = null;
        IWaveIn? microphone = null;
        IWaveIn? loopback = null;
        try
        {
            me = Select(enumerator, settings.MicrophoneDeviceId, DataFlow.Capture);
            them = Select(enumerator, settings.PlaybackDeviceId, DataFlow.Render);
            microphone = new WasapiPacketCapture(me, loopback: false);
            loopback = new WasapiPacketCapture(them, loopback: true);
            return new CaptureSources(microphone, loopback, me, them)
            {
                MeDevice = new(me.ID, me.FriendlyName, CaptureChannel.Me, settings.MicrophoneDeviceId is null),
                ThemDevice = new(them.ID, them.FriendlyName, CaptureChannel.Them, settings.PlaybackDeviceId is null)
            };
        }
        catch
        {
            microphone?.Dispose();
            loopback?.Dispose();
            me?.Dispose();
            them?.Dispose();
            throw;
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

public sealed class CaptureSources(IWaveIn me, IWaveIn them, IDisposable? meDevice = null, IDisposable? themDevice = null) : IDisposable
{
    public IWaveIn Me { get; } = me;
    public IWaveIn Them { get; } = them;
    public AudioDevice? MeDevice { get; init; }
    public AudioDevice? ThemDevice { get; init; }

    public void Dispose()
    {
        try { Me.Dispose(); }
        finally
        {
            try { Them.Dispose(); }
            finally
            {
                try { meDevice?.Dispose(); }
                finally { themDevice?.Dispose(); }
            }
        }
    }
}
