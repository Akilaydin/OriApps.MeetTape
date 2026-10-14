param(
    [Parameter(Mandatory = $true)][string]$BaseSha,
    [Parameter(Mandatory = $true)][string]$HeadSha,
    [string]$TargetBranch = 'main'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$files = @(git diff --no-renames --name-only $BaseSha $HeadSha --)
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine changed paths; refusing to skip checks.' }
$software = $false
$package = $TargetBranch -eq 'release'
foreach ($file in $files) {
    if ($file -eq '.mailmap') { continue }
    if ($file -match '^(README|PRODUCT|ARCHITECTURE|CONTRIBUTING|AGENTS)\.md$|^docs/.*\.md$|^\.github/pull_request_template\.md$') { continue }
    $software = $true
    if ($file -match '^src/MeetTape/(App|AppUpdates|Program|DesktopInstance|RecordingControls|RecordingTaskbar|StartupReleaseNotice|WindowsRecycleBin|DesktopController|MainViewModel|UpdateWindow|SessionRemoval)\.cs$' -or
        $file -notmatch '^src/MeetTape/(AudioChunker|AudioDevices|AudioPacket|AudioTrimmer|CaptureCoordinator|ChatApiClient|ChunkStore|Configuration|ConfigurationBootstrap|ConfigurationStore|DeleteRecordingWindow|FfmpegEncoder|Mp3Recorder|Mp3Track|PcmTimeline|PostRecordingWorkflow|ProcessingJobs|PromptDocument|SessionData|SessionStore|SettingsViewModel|TranscriptAssembler)\.cs$|^src/MeetTape/(MainWindow|SettingsWindow)\.axaml(\.cs)?$|^tests/.*\.cs$|^prompts/(transcription|summary)\.md$|^config\.example\.json$') {
        $package = $true
    }
}
if ($package) { $software = $true }
$results = @("software=$($software.ToString().ToLowerInvariant())", "package=$($package.ToString().ToLowerInvariant())")
$results
if ($env:GITHUB_OUTPUT) { $results | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append }
