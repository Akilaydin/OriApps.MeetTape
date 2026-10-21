using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System.Diagnostics;
using OriApps.MeetTape.Diagnostics;

namespace OriApps.MeetTape.Audio;

internal sealed record MicrophoneSession(int Pid, string? Application);
internal sealed record MicrophoneOffer(Guid Token, string? Application);

internal interface IMicrophoneSessionScanner
{
    IReadOnlyList<MicrophoneSession> Scan();
}

// Inspects Windows capture *sessions*, without opening a capture stream or reading audio.
internal sealed class WasapiMicrophoneSessionScanner : IMicrophoneSessionScanner
{
    public IReadOnlyList<MicrophoneSession> Scan()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        var active = new List<MicrophoneSession>();
        foreach (var device in endpoints)
        {
            using (device)
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    using var session = sessions[i];
                    if (session.State != AudioSessionState.AudioSessionStateActive) continue;
                    uint pid = session.GetProcessID;
                    // A zero/nonrepresentable PID cannot safely be classified as another app.
                    if (pid is 0 or > int.MaxValue) continue;
                    string? name = null;
                    try
                    {
                        using var process = Process.GetProcessById((int)pid);
                        name = process.ProcessName;
                    }
                    catch (Exception error) when (error is ArgumentException or InvalidOperationException
                        or System.ComponentModel.Win32Exception)
                    {
                        // Process exited or cannot be inspected. A generic offer remains possible.
                    }
                    active.Add(new MicrophoneSession((int)pid, name));
                }
            }
        }
        return active;
    }
}

// The COM scan runs off the UI thread. A failed read leaves the last good
// baseline untouched instead of fabricating an app exit/reentry.
internal sealed class MicrophoneActivityPoller(IMicrophoneSessionScanner scanner, MicrophoneOfferTracker tracker)
{
    private bool reportedFailure;

    public async Task<MicrophoneOffer?> PollAsync(Func<bool> canOffer)
    {
        IReadOnlyList<MicrophoneSession> sessions;
        try { sessions = await Task.Run(scanner.Scan); reportedFailure = false; }
        catch (Exception error)
        {
            if (!reportedFailure)
            {
                reportedFailure = true;
                AppDiagnostics.Error("microphone-monitor", "Enumerate capture sessions", error);
            }
            return null;
        }
        return tracker.Observe(sessions, canOffer());
    }
}

// Stateful policy: one offer per continuous application activity, never on the initial scan.
// The scanner and clock are separate so the decision can be tested without audio hardware.
internal sealed class MicrophoneOfferTracker(int ownPid, TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Dictionary<string, Seen> active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> lastOffered = new(StringComparer.OrdinalIgnoreCase);
    private bool initialized;
    private Guid? pendingToken;
    private string? pendingKey;
    internal static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(3);

    public MicrophoneOffer? Observe(IReadOnlyList<MicrophoneSession> sessions, bool canOffer)
    {
        var current = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in sessions)
        {
            if (session.Pid <= 0 || session.Pid == ownPid) continue;
            string? name = SafeName(session.Application);
            string key = name is null ? $"pid:{session.Pid}" : $"app:{name}";
            // The same app may hold multiple streams or use multiple microphones.
            current.TryAdd(key, name);
        }

        if (!initialized)
        {
            foreach (var (key, name) in current)
                active[key] = new Seen(name, 2, Handled: true);
            initialized = true;
            return null;
        }

        foreach (string key in active.Keys.Except(current.Keys, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            active.Remove(key);
            if (key.Equals(pendingKey, StringComparison.OrdinalIgnoreCase)) CancelPending();
        }

        var now = time.GetUtcNow();
        MicrophoneOffer? offer = null;
        foreach (var (key, name) in current)
        {
            if (!active.TryGetValue(key, out var seen))
            {
                active[key] = new Seen(name, 1, Handled: false);
                continue;
            }
            if (seen.Handled) continue;
            int count = seen.Consecutive + 1;
            active[key] = seen with { Consecutive = count, Handled = count >= 2 };
            if (count < 2 || !canOffer || offer is not null) continue;
            if (lastOffered.TryGetValue(key, out var last) && now - last < Cooldown) continue;
            offer = new MicrophoneOffer(Guid.NewGuid(), name);
            pendingToken = offer.Token;
            pendingKey = key;
            lastOffered[key] = now;
        }
        // Bound cooldown data to recent activity rather than retaining every process forever.
        foreach (string key in lastOffered.Where(item => now - item.Value >= Cooldown).Select(item => item.Key).ToArray())
            lastOffered.Remove(key);
        return offer;
    }

    public bool TryConsume(Guid token)
    {
        if (pendingToken != token || pendingKey is null || !active.ContainsKey(pendingKey)) return false;
        CancelPending();
        return true;
    }

    public void CancelPending()
    {
        pendingToken = null;
        pendingKey = null;
    }

    private static string? SafeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || name.Any(char.IsControl)) return null;
        return name.Trim();
    }

    private sealed record Seen(string? Application, int Consecutive, bool Handled);
}
