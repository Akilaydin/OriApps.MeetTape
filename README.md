# MeetTape

## About

Windows CLI for recording microphone (**Me**) and system playback (**Them**) and producing Markdown transcripts. Audio stays in local session folders; transcription sends audio and optional Summary sends text to your configured API.

Copyright (C) 2026 OriApps. Licensed under GPL-3.0-only.

## Workflow

1. With MeetTape on PATH, run `meettape configure`. The interactive wizard creates or edits `%APPDATA%\MeetTape\config.json`, configures the API and accepts an API key for your Windows user environment without echoing it in an interactive console. Enter keeps each current value; existing prompts are not overwritten. Restart MeetTape after changing settings and open a new terminal after changing the key.
2. Open `meettape`; use `start` → `stop`, enter a title and choose trimming.
3. Transcription runs in the background. With `stages.summary` enabled, request `summary <session-id>` after transcription completes.

On interactive startup, MeetTape shows its current version, the latest public GitHub Release (when reachable), and the `update` command if a newer release is available. Network failures do not block recording.

The editable transcription prompt targets Russian (`ru`) by default. Use `help` for commands and `devices` inside MeetTape to list audio device IDs.

## Installation

Download `install.cmd` from [GitHub Releases](https://github.com/Akilaydin/OriApps.MeetTape/releases) and run it. It installs MeetTape and FFmpeg for the current user and adds `meettape` to PATH. Open a new terminal afterward; use `meettape update` for later updates.

To uninstall, close MeetTape. Back up user-owned files in `%LOCALAPPDATA%\Programs\MeetTape` (especially `app\ffmpeg.exe` if `ffmpeg-version.txt` is absent), then delete that folder and remove `%LOCALAPPDATA%\Programs\MeetTape\app` from your user `PATH`. For optional data cleanup, inspect the active config (default `%APPDATA%\MeetTape\config.json`, custom `--config`, or working-directory `config.json`) and its prompt and `meetingsRoot` paths. Back up recordings and delete only MeetTape-specific config/prompt files and session folders (default `%LOCALAPPDATA%\MeetTape\Meetings`), plus any dedicated API-key environment variable. Never delete shared directories or prompt files.

## Output

```text
<session-folder>/
  me.mp3             # Original microphone audio
  them.mp3           # Original system audio
  transcription.md   # Complete transcript
  summary.md         # Optional Summary
  session.json       # Saved state and progress
```

## Documentation

- [LICENSE](LICENSE) — GPL-3.0-only for MeetTape; [third-party notices](THIRD-PARTY-NOTICES.txt) are included in releases and installations.
- [PRODUCT.md](PRODUCT.md) — user behavior.
- [ARCHITECTURE.md](ARCHITECTURE.md) — implementation.
- [AGENTS.md](AGENTS.md) — coding rules.

## Prerequisites

- Windows x64; installation downloads FFmpeg automatically.
- For transcription: a Chat Completions API/model supporting MP3 `input_audio`; credentials for enabled API stages.
- Built-in Windows PowerShell 5.1 for installation; .NET 10 SDK for source builds.
