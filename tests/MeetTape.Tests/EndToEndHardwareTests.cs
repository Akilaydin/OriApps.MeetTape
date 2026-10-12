using System.Net;
using System.Security.Cryptography;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeetTape.Tests;

[Collection("Hardware")]
public sealed class EndToEndHardwareTests
{
    [HardwareFact]
    [Trait("Category", "Hardware")]
    public async Task HardwareRecordingNamingTrimChunksTranscriptAndRequestedSummary()
    {
        string root = Path.Combine(Path.GetTempPath(), "MeetTapeEndToEnd-" + Guid.NewGuid().ToString("N"));
        string example = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config.example.json"));
        var loaded = ConfigurationLoader.Load(example);
        var config = loaded with { Settings = loaded.Settings with { MeetingsRoot = root, Stages = new() { Summary = true } } };
        using var capture = new Mp3Recorder(config).Capture;
        using var enumerator = new MMDeviceEnumerator();
        using var render = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        try
        {
            capture.Start();
            using (var player = new WasapiOut(render, AudioClientShareMode.Shared, false, 100))
            {
                player.Init(new SignalGenerator(48000, 2) { Frequency = 440, Gain = 0.02 }.ToWaveProvider());
                player.Play(); await Task.Delay(3000); player.Stop();
            }
            capture.Stop();
            Assert.Null(capture.Status.Error);
            string directory = capture.RecordingDirectory!;
            byte[] original = SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, "me.mp3")));
            byte[] originalThem = SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, "them.mp3")));
            using var preparation = new PostRecordingWorkflow("ffmpeg");
            preparation.Begin(directory, "End-to-end validation");
            await preparation.Completion;
            directory = preparation.SelectedDirectory!;
            preparation.Choose("manual", 0.2, 2.5);
            await preparation.Completion;
            using var handler = new ControlledProvider(() => capture.Status.IsActive);
            using var http = new HttpClient(handler);
            using var jobs = new ProcessingJobs(config, () => capture.Status.IsActive, http, readKey: _ => "test-key");
            await jobs.ProcessSessionAsync(directory, default);
            Assert.Equal(JobState.Completed, SessionStore.Read(directory).JobState);
            Assert.True(File.Exists(Path.Combine(directory, "transcription.md")));
            Assert.False(File.Exists(Path.Combine(directory, "summary.md")));
            jobs.RequestSummary(directory);
            await jobs.ProcessSessionAsync(directory, default);
            Assert.True(File.Exists(Path.Combine(directory, "summary.md")));
            Assert.Equal(2, handler.Calls);
            Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, "me.mp3"))));
            Assert.Equal(originalThem, SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, "them.mp3"))));
        }
        finally
        {
            capture.Dispose();
            if (Directory.Exists(root))
            {
                Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(root));
                foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class ControlledProvider(Func<bool> captureActive) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.False(captureActive());
            string text = ++Calls == 1 ? "[00:00] Them: Synthetic protocol result, not speech recognition." : "# Synthetic summary";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = text } } } })) });
        }
    }
}
