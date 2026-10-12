# Product

MeetTape is a Windows desktop application on Avalonia 12 and .NET 10 for recording a microphone (**Me**) and all playback on the selected output device (**Them**) as separate, synchronized MP3 tracks. Recordings and documents stay in the configured meeting folder. Transcription sends audio to a user-configured Chat Completions API; Summary sends the completed transcript.

## Recording and desktop lifecycle

`MeetTape.exe` opens one window and a system tray icon, without a console. The window and tray share Start/Stop; the window shows elapsed duration, active device names, capture warnings/errors and saved sessions. Closing the window hides it and does not stop recording. Open from the tray or launch MeetTape again to restore it; a repeated ordinary launch does not create another recorder, job runner or tray icon. Explicit Exit finalizes both MP3s and cancels/joins preparation and processing before the process exits; a new ordinary launch then starts normally. A second Start is rejected.

Start cancels and joins local preparation/API jobs before opening capture; an unconfirmed join blocks capture. No conflicting processing runs during recording. Stop finalizes audio on a worker, then awaits an optional meeting title and trim decision. Jobs remain deferred while the just-stopped recording awaits its title; Continue saves the title (empty is unnamed), offers trimming when enabled and lets eligible processing resume. Start next recording saves the optional title and starts another capture in one action; unfinished trim can be reopened through Name / Trim afterward. A failed start retains the previous meeting and leaves recording available to retry. Capture failures retain surviving audio and restart deferred jobs for eligible earlier meetings.

The session list shows dates, title, capture/job states, chunk counts, retry time and diagnostics. Select a session to open its folder, transcription.md or summary.md, resume paused jobs or reopen unfinished Name / Trim. Resume processing is available only for recoverable paused processing, after capture and trim are complete; automatic retries need no manual resume. Request Summary requires current settings to allow it and a complete published transcription; a pending, running or paused request is not offered again. A completed Summary can be restored from its saved checkpoint if summary.md is missing, without another API request. Enabling the stage does not request it. UI/controller state and processing notifications are typed; warnings are not inferred from console text. Capture timing corrections persist in session metadata. Settings changes require Exit/restart; no hot reload is provided. User arguments show a GUI error without loading settings, recording or processing.

## Updates

GUI startup displays the installed version and checks the public Velopack GitHub feed with a bounded two-second wait. Failures do not block capture; source/portable builds skip the check. Update requires idle capture and no unfinished naming/current operation. It shows current/available versions and asks for explicit consent before downloading. Decline/close leaves the UI usable; even a previously downloaded package never auto-applies on startup. After download, capture is checked again and local work is joined before applying/restarting. Failed joins block apply. Use Exit in other MeetTape windows/trays first.

## Configuration

Velopack Setup installs MeetTape and bundled FFmpeg 9.0 for the current user without UAC, creates a Start menu shortcut and registers the Windows uninstaller. Updates/uninstall retain user config, prompts, recordings, checkpoints and API key environment variables. Settings selects concrete or Windows-default devices, meetings folder, FFmpeg, trim/transcription/Summary stages, chunkSeconds, endpoints/models, prompt paths and credential variable names. Candidate JSON passes the existing loader/prompt validation before atomic replacement, under the existing configuration mutex. Concurrent file edits are detected; unchanged files and user prompts are retained.

The default file is `%APPDATA%\MeetTape\config.json`, still directly editable. On first use editable configuration and default prompts are created without overwriting existing files. Installed and source builds use this same user settings path. Prompt and meetings paths must remain outside the installation directory. Relative meetings/prompt paths resolve from the configuration directory. FFmpeg selection prefers an adjacent ffmpeg.exe when configured by its default name.

Existing API keys are never loaded into controls. New keys use masked fields and are saved only to the Windows User environment and the current process, never JSON, prompts, logs or session metadata. Empty fields retain existing credentials. These variables are user registry storage, not an encrypted secrets vault. Restart MeetTape to apply changed settings; other running terminals may need reopening to inherit changed variables. Keys are optional for recording; missing or invalid credentials pause API processing without exposing their values.

API requests use the named Process environment key, falling back to the Windows User environment when the Process value is absent. Missing or empty enabled prompt files do not disable recording; they pause the affected processing stage with a safe diagnostic. Fix the prompt file or key, restart after changing configuration settings, then use Resume; completed chunks are retained.

The default transcription prompt targets Russian (`ru`) and remains user-editable. `stages.summary` requires `stages.transcribe`; chunkSeconds accepts 1–340 seconds. Restart also restores eligible saved jobs and presents unfinished trim selection through Name / Trim; no new Summary request is inferred from startup.

## Trimming and transcription

Edge-silence detection considers activity in both tracks. Choose Accept, Skip or Apply interval in the window; manual times accept seconds, `MM:SS` or `HH:MM:SS` and require `0 <= start < end <= duration`. Both tracks use one shared interval; interior silence remains. No detected activity keeps the full recording. Name / Trim reopens unfinished preparation. Originals remain unchanged; selected derivatives are saved only after successful preparation.

After a saved trim choice, or with trimming disabled, enabled transcription runs serially in the background and resumes unfinished jobs at GUI startup. Each chunk uses stereo MP3 with Me on the left and Them on the right, with boundary context. Successful results are saved before progress advances; only a complete set produces `transcription.md`.

The transcript has approximate timestamps relative to the original recording and Me/Them/Unknown source labels. Unsupported labels become Unknown. Repeated same-source boundary context is reconciled conservatively. Source labels are model estimates, not guaranteed individual identities; microphone audio can contain playback echo. Recognition accuracy and exact deduplication are not guaranteed.

An exact `[NO_SPEECH]` response adds no utterances, allowing silent chunks to complete. Empty, malformed or truncated responses pause processing. Each audio upload is limited to 8 MiB, each API response to 2 MiB, and each request to five minutes.

## Summary

Request Summary explicitly requests `summary.md` from a complete published transcript, using independent API settings and an editable prompt. It never replaces the transcript or summarizes partial results. Combined prompt/transcript UTF-8 input is limited to 2 MiB; oversized input pauses without truncation. A provider may enforce a smaller context limit.

Successful Summary results are checkpointed before publication. Repeated requests retain existing output; missing output can be restored from its checkpoint without another API request.

## Files and saved progress

Each recording has a unique folder and stable session ID under `meetingsRoot`:

- `me.mp3`, `them.mp3`: preserved source recordings.
- `session.json`: identity, title, capture parameters, selected interval, stages, progress and diagnostics.
- `processed/`: selected trim derivatives and stereo chunks when needed.
- `chunks/`: successful transcription checkpoints.
- `transcription.md`: complete transcript; `summary.json` and `summary.md`: optional Summary checkpoint and document.

Only the current session/checkpoint model is supported. Legacy recovery manifests and compatibility across previous versions are not provided; unsupported metadata is diagnosed while its original files remain untouched.

Exiting MeetTape retains saved progress. Restart validates and reuses completed chunks without re-uploading them. Corrupt or mismatched checkpoints pause the job. Differing existing transcript/Summary documents are preserved and diagnosed.

Timeouts, HTTP 408/429/5xx and temporary connection faults retry indefinitely with exponential backoff capped at five minutes, including capped Retry-After. Other HTTP errors, invalid responses and configuration/filesystem faults pause the affected job. Fix the cause, restart for changed settings, then use Resume. If saving a pause fails, the GUI reports it and blocks that session in the current process.

Device, encoder or storage failures stop both sources and retain surviving audio; already accepted PCM is flushed to MP3 when possible before a forced encoder shutdown. After an asynchronous capture failure, deferred processing automatically resumes for other eligible recordings. Failed recordings retain surviving originals for manual verification and do not enter automatic processing. WASAPI timestamp errors, clock resets and overlapping packet positions never stop capture on their own: the recorder places late packets after committed audio and uses arrival time when QPC metadata is invalid. This prioritizes retaining speech over exact alignment. The first warning of each type per track appears in the GUI while recording; a larger overlap exceeding one packet's duration is also reported. The compact capture panel shows status, duration and brief warnings/errors. Details retains full device names, per-track correction counts, invalid timestamp counts, largest shifts and the latest metadata-only diagnostic; its read-only text supports selection/Ctrl+C and Copy details. Repeated capture warnings are deduplicated and bounded; background job issues appear separately. Capture timing diagnostics remain in `session.json`. Actual WASAPI discontinuities remain visible. Both MP3s are finalized at a common length, including recovered speech that extends beyond the wall-clock stop time; `session.json.durationSeconds` reflects the saved audio length and `endedAtUtc` remains the actual stop time. Abandoned captures are reported as interrupted with processing paused; they are not automatically declared complete. Capture streams audio without keeping the whole recording in memory. Trimming and chunk preparation accept recordings up to three hours; capture does not stop automatically at that boundary. Full-duration hardware capture remains unverified.
