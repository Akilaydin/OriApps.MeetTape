![MeetTape](assets/MeetTape.png)

# MeetTape

## About

Windows desktop app (Avalonia 12, .NET 10) for recording microphone (**Me**) and system playback (**Them**) and producing Markdown transcripts. Audio stays in local session folders; transcription sends audio and optional Summary sends text to your configured API.

Copyright (C) 2026 OriApps. Licensed under GPL-3.0-only.

## Workflow

1. Open **MeetTape** from the Start menu, or run `MeetTape.exe`. First use opens Settings. Select microphone/playback devices, meetings folder, FFmpeg and API stages. Newly entered API keys are hidden and stored in Windows user environment variables; prompts can be edited under **API & prompts**. Save applies settings without restarting; active capture keeps its streams and the next recording uses the changes.

   **Settings → Recording → Microphone processing (Me only)** offers **Windows Default** (unchanged recording behavior, including existing configurations) and **Windows Communications** (requests device-dependent Windows processing, potentially noise suppression, gain control and echo cancellation). The selected mode is saved per recording. This does not add processing to the `Them` loopback track, but Windows Communications can trigger system playback ducking: compare both tracks, especially quiet voices, before relying on it. Unsupported requests fail with a clear message; the app never silently falls back. No FFmpeg noise filtering is enabled by this setting.
2. Click **Start recording** → **Stop**. Enter an optional title, then **Continue** or **Start next recording** to immediately record another meeting. Unfinished trimming remains available through **Name / Trim** afterward. If trimming is enabled, choose **Accept**, **Skip**, or a manual shared interval. Closing the window hides it to the tray and keeps recording; **Exit** finalizes capture and saves background work.
   If a selected audio device disconnects while recording, the other track continues; MeetTape shows a warning and records silence on the unavailable track until it reconnects. Windows Default follows a newly selected default device. A specifically selected device reconnects only to itself. Real encoder/storage errors still stop the recording.
3. Follow session progress, retry/errors and resume paused work in the window. **Cancel processing** skips Name / Trim after Stop or stops processing the selected meeting, retaining its audio and checkpoints. Canceled work stays stopped after restart; use **Resume processing** or unfinished **Name / Trim** to continue explicitly. Right-click a meeting row or use its three-dot menu for files, Copy, Name / Trim, Resume, Summary, Cancel, Delete and full Details. Double-click opens its folder. Enable **Automatic Summary** in Settings / Processing to create Summary after transcription for new recordings. **Request Summary** also works with automatic mode off once a complete transcript exists.

Launching MeetTape again reopens its existing window and keeps that instance's recording or processing running.

To remove a saved meeting, select it and use **Delete recording** in the Meetings context menu. Confirm its title/date and the entire-folder removal. Unfinished processing must first be canceled; unavailable Recycle Bin or unsafe folder ownership refuses the operation. Successful deletion moves the folder to Windows Recycle Bin for recovery.

**Ctrl+Shift+F9** toggles Start/Stop by default while MeetTape runs, including when hidden in the tray. Change **Recording hotkey** in Settings → Recording and Save to apply and retain another combination without restarting. An occupied shortcut leaves the previous binding available. Finish Name / Trim after Stop before starting again. Windows notifications confirm actual recording transitions without focusing the window; status remains available in the window/tray if notifications are suppressed. If the shortcut is occupied at startup, use the buttons or tray.

**Settings → Recording → Offer recording when another app uses a microphone** is optional and disabled by default. While MeetTape runs (including in the tray), it can suggest **Start recording** in a Windows notification when another app opens a microphone capture stream. Only clicking Start initiates recording. Microphone activity is not proof of a meeting: device tests and dictation can trigger offers, muted/listen-only meetings may not, and already active streams at MeetTape startup are ignored. Offers are suppressed during recording and while activity continues; closing MeetTape stops observation.

**Settings → Reminders** lets you add local meeting reminders with an optional name, date/time for one-time alerts, or time and selected weekdays for recurring alerts. Notifications appear one minute before the meeting, with **Start recording** and **Remind in 5 min**; dismissing an alert leaves future repeats enabled. Start recording never stops an active capture. Save settings to apply edits. Optionally select **Run MeetTape when Windows starts (in the tray)** in an installed build. MeetTape must remain running (the window may be hidden in the tray); **Exit** stops reminders.

Shipped UI text and prompt instructions are in English. The editable transcription prompt targets Russian (`ru`) while preserving the original spoken words, including repetitions and self-corrections; the default Summary requests Russian output. Updates preserve existing custom prompts and documents. To adopt the latest [built-in transcription prompt](prompts/transcription.md) in an existing installation, copy its text into **Settings → API & prompts → Transcription** and Save; this is optional and never automatic. Enabled transcription uploads audio to your configured API; Summary uploads the completed transcript. The bounded startup GitHub version check does not block recording.

## Installation

Download **OriApps.MeetTape-win-Setup.exe** from [GitHub Releases](https://github.com/Akilaydin/OriApps.MeetTape/releases) and run it. Velopack installs for the current user without administrator rights, creates a Start menu shortcut and registers MeetTape in Windows Installed apps. FFmpeg 9.0 and its license are bundled; no separate download is required.

For updates, stop recording and finish naming the meeting. Open **Settings / About**, use **Check for updates** to check availability, then **Update** to review current/available versions and explicitly confirm **Download and restart**. Checking never downloads; save or discard Settings edits before updating. Declining or closing the dialog keeps the current version. Downloads never install automatically on the next launch. MeetTape joins preparation/processing and saves checkpoints before restarting. Use Exit in other MeetTape windows/trays first. Updates require an installed build; source/portable builds display instructions instead.

To uninstall, use **Exit** in every MeetTape window/tray, then remove **MeetTape** from Windows **Settings → Apps → Installed apps**. User settings/prompts (`%APPDATA%\MeetTape`), recordings/checkpoints (default `%LOCALAPPDATA%\MeetTape\Meetings`) and named User environment API keys remain. Keep prompt and meeting paths outside `%LOCALAPPDATA%\OriApps.MeetTape`; its `current` directory is replaced by updates and its installation root is removed on uninstall.

## Output

Persistent diagnostics are stored in `%LOCALAPPDATA%\MeetTape\Logs\meettape.log`, with up to five files of 1 MiB each (`.1`–`.4` are older logs). Logs contain technical errors and workflow stages, survive restarts, and redact API keys and URL credentials. Use **Open logs folder** in Settings to open Windows Explorer; a missing folder is created without changing settings or log files. Logging failure never blocks recording. The UI shows the cause and available recovery action.

Final folders use `Weekly sync_13.10.2026 10-30 01h25m`: title, local start time and audio duration in whole hours/minutes. Empty titles use `Unnamed meeting`; occupied destinations are preserved and the next available numeric suffix (` (2)`, ` (3)`, ...) is appended automatically. The session UUID stays in `session.json`.

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

- Windows x64; FFmpeg 9.0 is included in Setup and update packages.
- For transcription: a Chat Completions API/model supporting MP3 `input_audio`; credentials for enabled API stages.
- .NET 10 SDK for source builds; the installer includes the application runtime.

## Packaging from source

Install the build-only tool with `dotnet tool install vpk --version 1.2.161 --tool-path dist/vpk`, then run `./tools/Packaging/Build-Package.ps1 -Version <major.minor.patch> -VpkPath ./dist/vpk/vpk.exe` in PowerShell 7. It publishes win-x64, verifies the pinned FFmpeg archive SHA-256 and generates Setup, a full package and `releases.win.json` under `dist/releases`. Full packages provide the supported update fallback; delta generation is disabled. Software/headless tests and two-version package/install/uninstall smoke run on disposable GitHub CI runners. Production publication runs only after a reviewed PR promotes `main` to `release`.
