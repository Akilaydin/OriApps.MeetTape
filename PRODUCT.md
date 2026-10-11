# Product

MeetTape is a Windows CLI for recording a microphone (**Me**) and all playback on the selected output device (**Them**) as separate, synchronized MP3 tracks. Recordings and documents stay in the configured meeting folder. Transcription sends audio to a user-configured Chat Completions API; Summary sends the completed transcript.

## Recording and commands

`meettape` opens the interactive shell. `start` and `stop` run inside that same process. Only one recording is active in a process; starting another is rejected. Starting capture cancels and joins its local preparation and API jobs first. Processing resumes when `stop` returns capture to idle. `exit`, `quit` or EOF finalize active capture and stop background processing.

After `stop`, enter an optional title and choose trimming when enabled. `status` shows this process's capture state; `sessions` lists stored recordings and processing states. A separate `meettape status` cannot inspect another process's live capture and warns that its status applies only to the new process.

One-shot commands are `configure`, `devices`, `check-config`, `status`, `sessions` and `update`. `devices` also works in the interactive shell. `help <command>` or `meettape <command> --help` explains each command without loading configuration. `help advanced` groups Summary, trimming, recovery and support-only commands; `chunks` and `assemble` remain available for manual recovery. Interactive `update` requires idle capture and exits the CLI after launching the updater.

## Configuration

`install.cmd` installs MeetTape and a pinned FFmpeg version in the user's app directory and adds it to PATH. `meettape update` updates installed files without altering user data. `meettape configure` starts an interactive settings wizard. On first use it creates editable configuration and both prompt files; later runs offer existing values (Enter keeps them) and never overwrite user prompts. The default user file is `%APPDATA%\MeetTape\config.json`; it remains directly editable. The output root, devices, FFmpeg path, stage flags, chunk duration, API endpoints/models, API key variable names and prompt paths are configurable. Relative meeting-root and prompt paths resolve from the configuration file; default FFmpeg selection prefers `ffmpeg.exe` beside the application. `--config <path>` selects the wizard's file explicitly; otherwise the wizard edits the default user file. Normal CLI configuration lookup still prefers existing user settings for installed builds and a current-directory `config.json` for source builds. Changes require restarting the CLI.

The default transcription prompt targets **Russian (`ru`)** and is editable. The wizard accepts an API key with hidden interactive console input and stores newly supplied keys in Windows user environment variables and the current process, never in JSON, prompts or session metadata. Enter retains existing keys; keys are optional for recording. Windows user environment variables are stored in the user registry, not in a dedicated encrypted secrets vault. Other already-running terminals may require reopening to pick up new keys. Interactive startup validates enabled-stage prompts; `check-config` also checks enabled-stage keys locally. One-shot `sessions` and `status` require valid configuration but no usable prompts or API keys.

`stages.summary` requires `stages.transcribe`. Enabling Summary never starts it automatically. `chunkSeconds` accepts 1–340 seconds.

## Trimming and transcription

Edge-silence detection considers activity in both tracks. Choose `accept`, `skip` or `trim <start> <end>`; manual times accept seconds, `MM:SS` or `HH:MM:SS` and require `0 <= start < end <= duration`. Both tracks use one shared interval; interior silence remains. No detected activity keeps the full recording. `prepare <session-id>` reopens unfinished preparation. Originals remain unchanged; selected derivatives are saved only after successful preparation.

After a saved trim choice, or with trimming disabled, enabled transcription runs serially in the background and resumes unfinished jobs at interactive startup. Each chunk uses stereo MP3 with Me on the left and Them on the right, with boundary context. Successful results are saved before progress advances; only a complete set produces `transcription.md`.

The transcript has approximate timestamps relative to the original recording and Me/Them/Unknown source labels. Unsupported labels become Unknown. Repeated same-source boundary context is reconciled conservatively. Source labels are model estimates, not guaranteed individual identities; microphone audio can contain playback echo. Recognition accuracy and exact deduplication are not guaranteed.

An exact `[NO_SPEECH]` response adds no utterances, allowing silent chunks to complete. Empty, malformed or truncated responses pause processing. Each audio upload is limited to 8 MiB, each API response to 2 MiB, and each request to five minutes.

## Summary

`summary <session-id>` explicitly requests `summary.md` from a complete published transcript, using independent API settings and an editable prompt. It never replaces the transcript or summarizes partial results. Combined prompt/transcript UTF-8 input is limited to 2 MiB; oversized input pauses without truncation. A provider may enforce a smaller context limit.

Successful Summary results are checkpointed before publication. Repeated requests retain existing output; missing output can be restored from its checkpoint without another API request.

## Files and recovery

Each recording has a unique folder and stable session ID under `meetingsRoot`:

- `me.mp3`, `them.mp3`: preserved source recordings.
- `session.json`: identity, title, capture parameters, selected interval, stages, progress and diagnostics.
- `processed/`: selected trim derivatives and stereo chunks when needed.
- `chunks/`: successful transcription checkpoints.
- `transcription.md`: complete transcript; `summary.json` and `summary.md`: optional Summary checkpoint and document.

Closing the CLI retains saved progress. Restart validates and reuses completed chunks without re-uploading them. Corrupt or mismatched checkpoints pause the job. Differing existing transcript/Summary documents are preserved and diagnosed. `assemble <session-id>` rebuilds a transcript from completed checkpoints; `chunks <session-id>` prepares missing chunks; `rechunk <session-id>` rebuilds unfinished oversized input using the current chunk duration while retaining verified successful results.

Timeouts, HTTP 408/429/5xx and temporary connection faults retry indefinitely with exponential backoff capped at five minutes, including capped Retry-After. Other HTTP errors, invalid responses and configuration/filesystem faults pause the affected job. Fix the cause, restart for changed settings, then use `resume <session-id>`. If saving a pause fails, the CLI reports it and blocks that session in the current process.

Device, encoder or storage failures stop both sources and retain surviving audio. After an asynchronous capture failure, issue `stop` or restart the shell to restart queued jobs. Abandoned captures are reported as interrupted with processing paused; they are not automatically declared complete. Capture streams audio without keeping the whole recording in memory. Trimming and chunk preparation accept recordings up to three hours; capture does not stop automatically at that boundary. Full-duration hardware capture remains unverified.
