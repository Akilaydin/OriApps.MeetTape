using OriApps.MeetTape.Diagnostics;
using System.Diagnostics;

namespace OriApps.MeetTape.Audio;

public sealed class FfmpegEncoder : IDisposable
{
    private readonly Process process;
    private readonly Task drainErrors;
    public Stream Input => process.StandardInput.BaseStream;

    public FfmpegEncoder(string executable, string outputPath)
    {
        if (File.Exists(outputPath))
            throw new RecordingException("Original MP3 already exists; refusing to overwrite it.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "-hide_banner", "-loglevel", "error", "-nostats", "-nostdin", "-n",
            "-f", "f32le", "-ar", "48000", "-ac", "1", "-probesize", "32", "-analyzeduration", "0",
            "-i", "pipe:0", "-c:a", "libmp3lame", "-b:a", "96k", "-map_metadata", "-1", "-write_xing", "1", "-flush_packets", "1", outputPath })
            start.ArgumentList.Add(argument);
        try { process = Process.Start(start) ?? throw new IOException(); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            AppDiagnostics.Error("ffmpeg", "Start encoder", ex);
            throw new RecordingException(AppDiagnostics.UserMessage(ex, "Cannot start FFmpeg; check capture.ffmpegPath and installation."));
        }
        drainErrors = Task.Run(async () =>
        {
            // Drain continuously; retain only bounded error diagnostics.
            char[] buffer = new char[1024];
            var details = new System.Text.StringBuilder();
            int count;
            while ((count = await process.StandardError.ReadAsync(buffer.AsMemory())) > 0)
            {
                details.Append(buffer, 0, Math.Min(count, 4096 - details.Length));
            }
            if (details.Length > 0) AppDiagnostics.Error("ffmpeg", "Encoder stderr", new IOException(details.ToString()));
        });
    }

    public async Task CompleteAsync()
    {
        Input.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await drainErrors.WaitAsync(TimeSpan.FromSeconds(5));
        if (process.ExitCode != 0)
            throw new RecordingException($"FFmpeg failed (exit {process.ExitCode}); check encoder availability, output permissions and disk space. Partial originals retained.");
    }

    public void Dispose()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1000);
            }
        }
        finally { process.Dispose(); }
    }
}
