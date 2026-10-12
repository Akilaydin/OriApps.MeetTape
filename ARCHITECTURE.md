# Architecture

## Runtime

One `net10.0-windows` executable in `src/MeetTape` and one test project. `Program` enters `Cli`, which owns `Mp3Recorder`, `PostRecordingWorkflow` and `ProcessingJobs`. State is stored in session folders; background work lives in the interactive process.

For interactive startup only, `StartupReleaseNotice` reads the assembly informational version and queries the public GitHub `releases/latest` endpoint with `HttpClient`. A bounded two-second wait and failure fallback keep GitHub availability outside the recording critical path. Local source builds use version `0.0.0` and skip release comparison; release CI sets the published version explicitly.

## Capture

```text
AudioDevices → WasapiPacketCapture → CaptureCoordinator
    → Mp3Recorder → Mp3Track → PcmTimeline → FfmpegEncoder
    → me.mp3 + them.mp3 + session.json
```

`AudioDevices` selects microphone/render endpoints. `WasapiPacketCapture` uses NAudio WASAPI clients to preserve native device positions, QPC timestamps and discontinuity flags. `CaptureCoordinator` owns the two-source lifecycle and reports failures. `Mp3Recorder` creates and finalizes the session.

Each `Mp3Track` has a bounded 100-packet queue of pooled mono samples and a streaming FFmpeg encoder. `PcmTimeline` maps device timing onto a shared 48 kHz grid, estimates rate from adjacent anchors, interpolates samples and represents gaps as silence. Implausibly compressed QPC intervals are not used as packet boundaries; up to 20 ms of overlap is repaired by shifting the whole packet forward without discarding its audio. Larger conflicts stop capture and attach up to 12 recent WASAPI packet-metadata records to the diagnostic. The 500 ms horizon protects silence commitment; normal adjacent audio is still streamed promptly. Normal stop uses a common end frame. Originals are mono 96 kbps MP3; queue exhaustion or late data stops capture with surviving audio retained. After recording faults, FFmpeg receives a bounded opportunity to flush accepted audio before disposal.

Capture ownership is process-local. CLI commands cancel and join preparation/API jobs before starting capture; an unconfirmed shutdown blocks the start. An asynchronous capture failure restarts the canceled job runner after capture finalization, under the same synchronization gate as interactive job commands.

## Processing

```text
PostRecordingWorkflow → AudioTrimmer → selected shared interval
ProcessingJobs → AudioChunker → stereo chunks → ChatApiClient → ChunkStore
    → TranscriptAssembler → transcription.md
Explicit Summary request → ChatApiClient → ChunkStore → summary.md
```

`PostRecordingWorkflow` coordinates title/trim selection and cancellable FFmpeg preparation. `AudioTrimmer` combines edge activity from both tracks and writes separate derivatives for one shared interval. `AudioChunker` creates verified 48 kHz/192 kbps stereo MP3, L=Me/R=Them, with frame-based ownership and bounded context. Trimming and chunk preparation reject recordings longer than three hours. Oversized unfinished input can be rebuilt while retaining a verified checkpoint prefix.

`ProcessingJobs` runs a serial queue, persists stage/retry state and recovers eligible sessions from disk. Its discovery cache stores modification time and length for completed, ineligible metadata, skipping JSON rereads until those attributes change; inspection remains uncached. Explicit recovery invalidates cached entries. Session leases exclude concurrent work on the same directory.

`ChatApiClient` sends non-streaming Chat Completions requests with base64 MP3 `input_audio`, or text-only Summary messages. Provider requests disable redirects and use a five-minute deadline, 8 MiB audio cap and 2 MiB response/Summary-input caps. Timeout, temporary connection and HTTP 408/429/5xx failures retry indefinitely with backoff capped at five minutes; invalid responses and permanent/configuration/filesystem faults pause.

`TranscriptAssembler` validates saved timed text or the exact `[NO_SPEECH]` marker, maps chunk timestamps to the original timeline and conservatively reconciles repeated same-source context. Only a complete checkpoint set publishes a transcript. Summary requires an explicit persisted request and the completed transcript; its saved result is tied to the transcript hash. Source labels and model timestamps remain estimates; no acoustic echo cancellation is implemented.

## Persistence

`SessionData` schema version 1 stores identity, capture/processing parameters, selected audio, stage progress, retry state and transcript hash. Unknown extension fields survive updates; unsupported schemas fail validation. `SessionStore` creates GUID-identified folders and provides exclusive `.session.lock` leases. Finalized originals receive the read-only attribute; transformations write separate files.

Metadata is flushed to same-directory temporary files before replacement. `ChunkStore` flushes successful checkpoints before job progress advances. Recovery validates session/chunk identity, intervals and audio hashes before reuse; corrupt results pause instead of triggering re-upload. Transcript/Summary publication preserves differing existing documents and accepts identical output after an interrupted metadata update. Abandoned captures become interrupted/paused.

## Configuration

`ConfigurationBootstrap` embeds default configuration/prompts and writes first-use files through flushed no-overwrite moves. One-shot `configure` uses `ConfigurationWizard` to edit current values; candidate JSON is validated before atomic replacement and unchanged configuration/prompts are left untouched. Explicit `--config` selects the wizard's file or runtime configuration; installed executables prefer existing user config, source builds prefer an existing current-directory config. `ConfigurationLoader` rejects malformed/unknown fields and invalid stage combinations; enabled prompts are validated for processing, while one-shot inspection skips prompt reads.

Meeting-root and prompt paths resolve against the configuration directory. FFmpeg receives its configured executable path/name. Endpoints require HTTPS, with HTTP allowed for loopback; API credentials come from named environment variables at request time. `configure` hides interactive key entry and writes keys to the Windows user environment and current process, not the configuration file. Session snapshots retain variable names, never key values; diagnostics omit provider bodies and audio payloads.
