using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace OriApps.MeetTape.Audio;

public sealed class WasapiPacketEventArgs(byte[] buffer, int count, long devicePosition, long qpcPosition, AudioClientBufferFlags flags)
    : WaveInEventArgs(buffer, count)
{
    public long DevicePosition { get; } = devicePosition;
    public long QpcPosition100ns { get; } = qpcPosition;
    public AudioClientBufferFlags Flags { get; } = flags;
}

// Keep native packet positions: high-level DataAvailable aggregation discards them.
public sealed class WasapiPacketCapture : IWaveIn
{
    private readonly AudioClient client;
    private readonly bool loopback;
    private readonly ManualResetEventSlim stop = new();
    private Thread? worker;
    private bool disposed;

    public WasapiPacketCapture(MMDevice device, bool loopback)
    {
        client = device.AudioClient;
        this.loopback = loopback;
        try { WaveFormat = client.MixFormat; }
        catch { client.Dispose(); throw; }
    }

    public WaveFormat WaveFormat { get; set; }
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public void StartRecording()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (worker is not null)
            throw new InvalidOperationException("Create a new capture source for each session.");
        client.Initialize(AudioClientShareMode.Shared,
            loopback ? AudioClientStreamFlags.Loopback : AudioClientStreamFlags.None,
            1_000_000, 0, WaveFormat, Guid.Empty);
        var capture = client.AudioCaptureClient;
        var buffer = new byte[checked(client.BufferSize * WaveFormat.BlockAlign)];
        worker = new Thread(() => Run(capture, buffer)) { IsBackground = true, Name = loopback ? "MeetTape-Them" : "MeetTape-Me" };
        worker.Start();
    }

    private void Run(AudioCaptureClient capture, byte[] buffer)
    {
        Exception? failure = null;
        try
        {
            client.Start();
            while (!stop.Wait(10))
            {
                while (!stop.IsSet && capture.GetNextPacketSize() > 0)
                {
                    IntPtr data = capture.GetBuffer(out int frames, out var flags, out long devicePosition, out long qpcPosition);
                    try
                    {
                        int count = checked(frames * WaveFormat.BlockAlign);
                        if (count > buffer.Length)
                            throw new InvalidDataException("WASAPI packet exceeds the allocated capture buffer.");
                        if ((flags & AudioClientBufferFlags.Silent) != 0)
                            Array.Clear(buffer, 0, count);
                        else
                            Marshal.Copy(data, buffer, 0, count);
                        DataAvailable?.Invoke(this, new WasapiPacketEventArgs(buffer, count, devicePosition, qpcPosition, flags));
                    }
                    finally { capture.ReleaseBuffer(frames); }
                }
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            // Device invalidation may also make Stop fail. Never let it escape the worker thread.
            try { client.Stop(); }
            catch (Exception ex) { failure ??= ex; }
        }
        RecordingStopped?.Invoke(this, new StoppedEventArgs(failure));
    }

    public void StopRecording() => stop.Set();

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        stop.Set();
        worker?.Join();
        try { client.Dispose(); }
        finally { stop.Dispose(); }
    }
}
