using System.Runtime.InteropServices;
using OriApps.MeetTape.Settings;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace OriApps.MeetTape.Audio;

public sealed class WasapiPacketEventArgs(byte[] buffer, int count, long devicePosition, long qpcPosition, AudioClientBufferFlags flags)
    : WaveInEventArgs(buffer, count)
{
    public long DevicePosition { get; } = devicePosition;
    public long QpcPosition100ns { get; } = qpcPosition;
    public AudioClientBufferFlags Flags { get; } = flags;
    internal int SourceEpoch { get; set; }
}

// Keep native packet positions: high-level DataAvailable aggregation discards them.
public sealed class WasapiPacketCapture : IWaveIn
{
    private readonly AudioClient client;
    private readonly bool loopback;
    private readonly ManualResetEventSlim stop = new();
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? worker;
    private bool disposed;

    public WasapiPacketCapture(MMDevice device, bool loopback, MicrophoneCaptureMode microphoneMode = MicrophoneCaptureMode.WindowsDefault)
    {
        client = device.CreateAudioClient();
        this.loopback = loopback;
        try
        {
            ApplyMicrophoneMode(loopback, microphoneMode, client.SetClientProperties);
            WaveFormat = client.MixFormat;
        }
        catch { client.Dispose(); throw; }
    }

    internal static void ApplyMicrophoneMode(bool loopback, MicrophoneCaptureMode mode, Action<AudioStreamCategory> setCategory)
    {
        if (loopback)
        {
            if (mode != MicrophoneCaptureMode.WindowsDefault)
                throw new InvalidDataException("System playback (Them) does not support microphone processing modes.");
            return;
        }
        if (mode == MicrophoneCaptureMode.WindowsDefault) return;
        if (mode != MicrophoneCaptureMode.WindowsCommunications)
            throw new InvalidDataException("Unknown microphone capture mode.");
        try { setCategory(AudioStreamCategory.Communications); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException)
        {
            throw new RecordingException("The selected microphone cannot use Windows Communications. Choose Windows Default in Settings → Recording.");
        }
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
        // Initialization is synchronous, but AudioClient.Start runs on the capture thread.
        // A hot endpoint switch must know this client started before retiring a working source.
        started.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }

    private void Run(AudioCaptureClient capture, byte[] buffer)
    {
        Exception? failure = null;
        try
        {
            client.Start();
            started.TrySetResult();
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
        catch (Exception ex) { failure = ex; started.TrySetException(ex); }
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
