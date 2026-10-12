# Architecture

## Runtime

One `net10.0-windows` WinExe in `src/MeetTape`, software tests and a separate xUnit v3 Avalonia Headless test host. `Program.Main` is STA: GUI by default; diagnostic arguments attach to the parent console while preserving redirected handles. `App` owns the classic desktop lifetime, window, FluentTheme and TrayIcon. AXAML uses compiled bindings with explicit data types. MainWindow delegates to MainViewModel; IDesktopController is the fake-service seam for UI tests. No MVVM framework or extra DI container is used.

`DesktopController` owns `Mp3Recorder.Capture`, `PostRecordingWorkflow` and `ProcessingJobs`. Its semaphore serializes lifecycle transitions; synchronous device/FFmpeg/processing joins run on workers, never the UI dispatcher. Start joins local jobs/preparation before capture. Stop awaits title/trim; shutdown finalizes capture even if a background join fails. Capture Finished callbacks enqueue recovery outside the capture lock. Failed shutdown can be retried while further starts remain blocked.

`OperationNotice` carries typed severity/session context from capture, preparation and jobs. Preparation/jobs have no TextWriter or console-color dependencies; CLI recovery subscribes to these same events. The UI polls typed DesktopSnapshot/session StageProgress/RetryProgress asynchronously once per second; it never parses TextWriter output. Full durable processing/capture state remains in existing session folders. Closing MainWindow hides it; explicit Exit and desktop shutdown requests await the controller. UI tests cover controls with fake services and Skia rendering.

For GUI startup only, `StartupReleaseNotice` returns display text from the assembly informational version and queries the public GitHub `releases/latest` endpoint with `HttpClient`. A bounded two-second wait and failure fallback keep GitHub availability outside the recording critical path. Local source builds use version `0.0.0` and skip release comparison; release CI sets the published version explicitly.

## Capture

```text
AudioDevices → WasapiPacketCapture → CaptureCoordinator
    → Mp3Recorder → Mp3Track → PcmTimeline → FfmpegEncoder
    → me.mp3 + them.mp3 + session.json
```

`AudioDevices` selects microphone/render endpoints. `WasapiPacketCapture` uses NAudio WASAPI clients to preserve native device positions, QPC timestamps and discontinuity flags. `CaptureCoordinator` owns the two-source lifecycle and reports failures. `Mp3Recorder` creates and finalizes the session.

Each `Mp3Track` has a bounded 100-packet queue of pooled mono samples and a streaming FFmpeg encoder. `PcmTimeline` maps device timing onto a shared 48 kHz grid, estimates rate from adjacent anchors, interpolates samples and represents gaps as silence. It does not fail on QPC/device-position anomalies: overlaps of any size append the complete packet after committed audio; invalid/future timestamps use the monotonic callback arrival time. This can shift audio and is reported, but does not interrupt capture. `CaptureCoordinator` aggregates per-track timing metrics while capturing and emits bounded live warnings; `Mp3Recorder` persists them at stop. The existing 500 ms horizon protects silence commitment; adjacent packets stream promptly. On stop, both track workers drain their pending PCM, then `Mp3Recorder` selects the later of their actual ends and the wall-clock stop frame; both tracks pad to that common frame before FFmpeg finalization. The persisted audio duration covers recovered speech beyond the wall-clock stop, without changing `endedAtUtc`. Originals are mono 96 kbps MP3; queue exhaustion or actual device/encoder/storage failures still stop capture with surviving audio retained. After recording faults, FFmpeg receives a bounded opportunity to flush accepted audio before disposal.

Capture ownership is process-local. Controller transitions cancel and join preparation/API jobs before starting capture; an unconfirmed shutdown blocks the start. An asynchronous capture failure restarts the canceled job runner after capture finalization, under the same synchronization gate as GUI job actions.

## Processing

```text
PostRecordingWorkflow → AudioTrimmer → selected shared interval
ProcessingJobs → AudioChunker → stereo chunks → ChatApiClient → ChunkStore
    → TranscriptAssembler → transcription.md
Explicit Summary request → ChatApiClient → ChunkStore → summary.md
```

`PostRecordingWorkflow` coordinates UI-independent title/trim selection and cancellable FFmpeg preparation. `AudioTrimmer` combines edge activity from both tracks and writes separate derivatives for one shared interval. `AudioChunker` creates verified 48 kHz/192 kbps stereo MP3, L=Me/R=Them, with frame-based ownership and bounded context. Trimming and chunk preparation reject recordings longer than three hours. Oversized unfinished input can be rebuilt while retaining a verified checkpoint prefix.

`ProcessingJobs` runs a serial queue, persists stage/retry state and recovers eligible sessions from disk. Its discovery cache stores modification time and length for completed, ineligible metadata, skipping JSON rereads until those attributes change; inspection remains uncached. Explicit recovery invalidates cached entries. Session leases exclude concurrent work on the same directory.

`ChatApiClient` sends non-streaming Chat Completions requests with base64 MP3 `input_audio`, or text-only Summary messages. Provider requests disable redirects and use a five-minute deadline, 8 MiB audio cap and 2 MiB response/Summary-input caps. Timeout, temporary connection and HTTP 408/429/5xx failures retry indefinitely with backoff capped at five minutes; invalid responses and permanent/configuration/filesystem faults pause.

`TranscriptAssembler` validates saved timed text or the exact `[NO_SPEECH]` marker, maps chunk timestamps to the original timeline and conservatively reconciles repeated same-source context. Only a complete checkpoint set publishes a transcript. Summary requires an explicit persisted request and the completed transcript; its saved result is tied to the transcript hash. Source labels and model timestamps remain estimates; no acoustic echo cancellation is implemented.

## Persistence

`SessionData` schema version 1 stores identity, capture/processing parameters, selected audio, stage progress, retry state and transcript hash. Unknown extension fields survive updates; unsupported schemas fail validation. `SessionStore` creates GUID-identified folders and provides exclusive `.session.lock` leases. Finalized originals receive the read-only attribute; transformations write separate files.

Metadata is flushed to same-directory temporary files before replacement. `ChunkStore` flushes successful checkpoints before job progress advances. Recovery validates session/chunk identity, intervals and audio hashes before reuse; corrupt results pause instead of triggering re-upload. Transcript/Summary publication preserves differing existing documents and accepts identical output after an interrupted metadata update. Abandoned captures become interrupted/paused.

## Configuration

`ConfigurationBootstrap` embeds default configuration/prompts and writes first-use files through flushed no-overwrite moves. Graphical Settings and optional `configure --console` reuse `ConfigurationWizard.Save` to persist current values; candidate JSON is validated before atomic replacement and unchanged configuration/prompts are left untouched. Explicit `--config` selects the wizard's file or runtime configuration; installed executables prefer existing user config, source builds prefer an existing current-directory config. `ConfigurationLoader` rejects malformed/unknown fields and invalid stage combinations; enabled prompts are validated for processing, while one-shot inspection skips prompt reads.

Meeting-root and prompt paths resolve against the configuration directory. FFmpeg receives its configured executable path/name. Endpoints require HTTPS, with HTTP allowed for loopback; API credentials come from named environment variables at request time. Settings masks new key entry and never reads an existing key into a control; the diagnostic wizard hides interactive key entry and writes keys to the Windows user environment and current process, not the configuration file. Session snapshots retain variable names, never key values; diagnostics omit provider bodies and audio payloads.

## Distribution

Windows single-file self-contained publish includes native extraction for Avalonia/Skia/HarfBuzz. install.cmd delegates to install.ps1, which verifies release digests, preserves data and unmanaged FFmpeg, creates a Start menu shortcut and a waiting meettape-cli.cmd wrapper. update.ps1 waits for its owner; the installer rejects a running EXE before replacements. CI runs software/headless tests, uploads UI screenshots, publishes win-x64 and checks redirected help/diagnostic contracts through tools/Test-Desktop.ps1. Native window smoke is available separately on an interactive Windows desktop. Release publication remains triggered only by the release branch.
