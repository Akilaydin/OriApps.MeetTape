![MeetTape](assets/MeetTape.png)

# MeetTape

## About

Windows desktop app (Avalonia 12, .NET 10) for recording microphone (**Me**) and system playback (**Them**) and producing Markdown transcripts. Audio stays in local session folders; transcription sends audio and optional Summary sends text to your configured API.

Copyright (C) 2026 OriApps. Licensed under GPL-3.0-only.

## Workflow

1. Open **MeetTape** from the Start menu, or run `meettape`. First use opens Settings. Select microphone/playback devices, meetings folder, FFmpeg and API stages. Newly entered API keys are hidden and stored in Windows user environment variables; existing prompts are retained. Exit and reopen after changing settings.
2. Click **Start recording** → **Stop**. Enter an optional title, then **Continue**. If trimming is enabled, choose **Accept**, **Skip**, or a manual shared interval. Closing the window hides it to the tray and keeps recording; **Exit** finalizes capture and saves background work.
3. Follow session progress, retry/errors and resume paused work in the window. Open the meeting folder or completed documents. **Request Summary** explicitly starts the optional Summary stage after a complete transcript exists.

The editable transcription prompt targets Russian (`ru`) by default. Enabled transcription uploads audio to your configured API; Summary uploads the completed transcript. The bounded startup GitHub version check does not block recording.

`meettape configure` opens Settings. For terminal automation, use `meettape-cli --help`, `meettape-cli sessions`, `devices`, `check-config`, or `help advanced`. The installed CMD wrapper waits for the WinExe and retains stdout/stderr and exit codes; direct PowerShell invocation of `MeetTape.exe` may return early. `--config <path>` works for the GUI and diagnostic commands. `configure --console` retains the optional diagnostic wizard. The interactive `meettape>` shell is removed.

## Installation

Download `install.cmd` from [GitHub Releases](https://github.com/Akilaydin/OriApps.MeetTape/releases) and run it. It installs MeetTape and FFmpeg for the current user, adds the app directory to PATH and creates a Start menu shortcut. Open a new terminal afterward. For updates, Stop recording and use Exit in every MeetTape window/tray; then use the GUI Update action or `meettape-cli update`. The updater never kills a running recorder or changes user recordings/settings.

To uninstall, use Exit in every MeetTape window/tray. Remove the MeetTape Start menu shortcut. Back up user-owned files in `%LOCALAPPDATA%\Programs\MeetTape` (especially `app\ffmpeg.exe` if `ffmpeg-version.txt` is absent), then delete that folder and remove `%LOCALAPPDATA%\Programs\MeetTape\app` from your user `PATH`. For optional data cleanup, inspect the active config (default `%APPDATA%\MeetTape\config.json`, custom `--config`, or working-directory `config.json`) and its prompt and `meetingsRoot` paths. Back up recordings and delete only MeetTape-specific config/prompt files and session folders (default `%LOCALAPPDATA%\MeetTape\Meetings`), plus any dedicated API-key environment variable. Never delete shared directories or prompt files.

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
