# Product

MeetTape is a Windows desktop application on Avalonia 12 and .NET 10 for recording a microphone (**Me**) and all playback on the selected output device (**Them**) as separate, synchronized MP3 tracks. Recordings and documents stay in the configured meeting folder. Transcription sends audio to a user-configured Chat Completions API; Summary sends the completed transcript.

## Recording and desktop lifecycle

`meettape` opens one window and a system tray icon, without a required console. The window and tray share Start/Stop; the window shows elapsed duration, active device names, capture warnings/errors and saved sessions. Closing the window hides it and does not stop recording. Open from the tray to restore it. Explicit Exit finalizes both MP3s and cancels/joins preparation and processing before the process exits. Only one capture is active per process. A second Start is rejected.

Start cancels and joins local preparation/API jobs before opening capture; an unconfirmed join blocks capture. No conflicting processing runs during recording. Stop finalizes audio on a worker, then awaits an optional meeting title and trim decision. Jobs remain deferred while the just-stopped recording awaits its title; Continue saves the title (empty is unnamed), offers trimming when enabled and lets eligible processing resume. Capture failures retain surviving audio and restart deferred jobs for eligible earlier meetings.

The session list shows dates, title, capture/job states, chunk counts, retry time and diagnostics. Select a session to open its folder, transcription.md or summary.md, resume paused jobs or reopen unfinished Name / Trim. Request Summary is an explicit action; enabling the stage does not request it. UI/controller state and processing notifications are typed; warnings are not inferred from console text. Capture timing corrections persist in session metadata. Settings changes require Exit/restart; no hot reload is provided.

## Diagnostic commands and updates

Useful one-shot commands remain: `devices`, `check-config`, `status`, `sessions`, `update`, `chunks <id>`, `rechunk <id>` and `assemble <id>`. Help and `help <command>` load no configuration or devices. `--config <path>` selects the file for GUI or diagnostics. The interactive shell is removed; Start, Stop, Resume, trim and Summary are window/tray actions. `configure` opens graphical Settings; `configure --console` keeps a diagnostic wizard.

The installed `meettape-cli.cmd` wrapper waits for Windows WinExe diagnostics and preserves stdout/stderr and exit codes. Use `meettape-cli` for terminal automation; a direct `MeetTape.exe` invocation in PowerShell may return before the GUI process exits. `status` describes only the newly started process; use the running window for live capture. One-shot inspection makes no API calls. Local recovery requires closing the GUI first; session leases preserve existing outputs. `assemble` validates local checkpoints without starting Summary or making API requests.

GUI startup displays the executable version and performs a bounded two-second public GitHub Release check. Failures do not block capture; source version 0.0.0 skips comparison. One-shot commands and help do not check releases. The GUI Update action requires idle capture, joins local work, launches the updater and exits. Terminal `update` also waits for its owner process to exit. The installer refuses file changes while an installed EXE is running, including same-version repairs. It never terminates a recorder or deletes user data.

## Configuration

`install.cmd` installs MeetTape and pinned FFmpeg for the current user, creates a Start menu shortcut and adds the app directory to PATH. Settings selects concrete or Windows-default devices, meetings folder, FFmpeg, trim/transcription/Summary stages, chunkSeconds, endpoints/models, prompt paths and credential variable names. Candidate JSON passes the existing loader/prompt validation before atomic replacement, under the existing configuration mutex. Concurrent file edits are detected; unchanged files and user prompts are retained.

The default file is `%APPDATA%\MeetTape\config.json`, still directly editable. On first use editable configuration and default prompts are created without overwriting existing files. Installed builds prefer user settings; source builds prefer an existing current-directory config.json. Relative meetings/prompt paths resolve from the configuration directory. FFmpeg selection prefers an adjacent ffmpeg.exe when configured by its default name. Explicit --config selects both runtime and Settings file.

Existing API keys are never loaded into controls. New keys use masked fields and are saved only to the Windows User environment and the current process, never JSON, prompts, logs or session metadata. Empty fields retain existing credentials. These variables are user registry storage, not an encrypted secrets vault. Restart MeetTape to apply changed settings; other running terminals may need reopening to inherit changed variables. Keys are optional for recording; check-config checks credentials for enabled API stages locally.

The default transcription prompt targets Russian (`ru`) and remains user-editable. `stages.summary` requires `stages.transcribe`; chunkSeconds accepts 1–340 seconds. Restart also restores eligible saved jobs and presents unfinished trim selection through Name / Trim; no new Summary request is inferred from startup.

## Trimming and transcription

Edge-silence detection considers activity in both tracks. Choose Accept, Skip or Apply interval in the window; manual times accept seconds, `MM:SS` or `HH:MM:SS` and require `0 <= start < end <= duration`. Both tracks use one shared interval; interior silence remains. No detected activity keeps the full recording. Name / Trim reopens unfinished preparation. Originals remain unchanged; selected derivatives are saved only after successful preparation.

After a saved trim choice, or with trimming disabled, enabled transcription runs serially in the background and resumes unfinished jobs at GUI startup. Each chunk uses stereo MP3 with Me on the left and Them on the right, with boundary context. Successful results are saved before progress advances; only a complete set produces `transcription.md`.

The transcript has approximate timestamps relative to the original recording and Me/Them/Unknown source labels. Unsupported labels become Unknown. Repeated same-source boundary context is reconciled conservatively. Source labels are model estimates, not guaranteed individual identities; microphone audio can contain playback echo. Recognition accuracy and exact deduplication are not guaranteed.

An exact `[NO_SPEECH]` response adds no utterances, allowing silent chunks to complete. Empty, malformed or truncated responses pause processing. Each audio upload is limited to 8 MiB, each API response to 2 MiB, and each request to five minutes.

## Summary

Request Summary explicitly requests `summary.md` from a complete published transcript, using independent API settings and an editable prompt. It never replaces the transcript or summarizes partial results. Combined prompt/transcript UTF-8 input is limited to 2 MiB; oversized input pauses without truncation. A provider may enforce a smaller context limit.

Successful Summary results are checkpointed before publication. Repeated requests retain existing output; missing output can be restored from its checkpoint without another API request.

## Files and recovery

Each recording has a unique folder and stable session ID under `meetingsRoot`:

- `me.mp3`, `them.mp3`: preserved source recordings.
- `session.json`: identity, title, capture parameters, selected interval, stages, progress and diagnostics.
- `processed/`: selected trim derivatives and stereo chunks when needed.
- `chunks/`: successful transcription checkpoints.
- `transcription.md`: complete transcript; `summary.json` and `summary.md`: optional Summary checkpoint and document.

Exiting MeetTape retains saved progress. Restart validates and reuses completed chunks without re-uploading them. Corrupt or mismatched checkpoints pause the job. Differing existing transcript/Summary documents are preserved and diagnosed. `assemble <session-id>` rebuilds a transcript from completed checkpoints; `chunks <session-id>` prepares missing chunks; `rechunk <session-id>` rebuilds unfinished oversized input using the current chunk duration while retaining verified successful results.

Timeouts, HTTP 408/429/5xx and temporary connection faults retry indefinitely with exponential backoff capped at five minutes, including capped Retry-After. Other HTTP errors, invalid responses and configuration/filesystem faults pause the affected job. Fix the cause, restart for changed settings, then use Resume. If saving a pause fails, the GUI reports it and blocks that session in the current process.

Device, encoder or storage failures stop both sources and retain surviving audio; already accepted PCM is flushed to MP3 when possible before a forced encoder shutdown. After an asynchronous capture failure, deferred processing automatically resumes for other eligible recordings. Failed recordings still require manual verification before recovery. WASAPI timestamp errors, clock resets and overlapping packet positions never stop capture on their own: the recorder places late packets after committed audio and uses arrival time when QPC metadata is invalid. This prioritizes retaining speech over exact alignment. The first warning of each type per track appears in the GUI while recording; a larger overlap exceeding one packet's duration is also reported. Live capture status and `session.json` contain per-track correction counts, invalid timestamp counts, largest shifts and the latest metadata-only diagnostic. Actual WASAPI discontinuities remain visible. Both MP3s are finalized at a common length, including recovered speech that extends beyond the wall-clock stop time; `session.json.durationSeconds` reflects the saved audio length and `endedAtUtc` remains the actual stop time. Abandoned captures are reported as interrupted with processing paused; they are not automatically declared complete. Capture streams audio without keeping the whole recording in memory. Trimming and chunk preparation accept recordings up to three hours; capture does not stop automatically at that boundary. Full-duration hardware capture remains unverified.
