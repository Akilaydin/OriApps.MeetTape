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
$managedPaths = @(
    'src/MeetTape/Audio/AudioChunker.cs',
    'src/MeetTape/Audio/AudioDevices.cs',
    'src/MeetTape/Audio/AudioPacket.cs',
    'src/MeetTape/Audio/AudioTrimmer.cs',
    'src/MeetTape/Audio/CaptureCoordinator.cs',
    'src/MeetTape/Audio/FfmpegEncoder.cs',
    'src/MeetTape/Audio/Mp3Recorder.cs',
    'src/MeetTape/Audio/Mp3Track.cs',
    'src/MeetTape/Audio/PcmTimeline.cs',
    'src/MeetTape/Api/ChatApiClient.cs',
    'src/MeetTape/Processing/PostRecordingWorkflow.cs',
    'src/MeetTape/Processing/ProcessingJobs.cs',
    'src/MeetTape/Processing/TranscriptAssembler.cs',
    'src/MeetTape/Sessions/ChunkStore.cs',
    'src/MeetTape/Sessions/SessionData.cs',
    'src/MeetTape/Sessions/SessionStore.cs',
    'src/MeetTape/Settings/Configuration.cs',
    'src/MeetTape/Settings/ConfigurationBootstrap.cs',
    'src/MeetTape/Settings/ConfigurationStore.cs',
    'src/MeetTape/Settings/PromptDocument.cs',
    'src/MeetTape/Desktop/DeleteRecordingWindow.cs',
    'src/MeetTape/Desktop/SettingsViewModel.cs',
    'src/MeetTape/Desktop/MainWindow.axaml',
    'src/MeetTape/Desktop/MainWindow.axaml.cs',
    'src/MeetTape/Desktop/SettingsWindow.axaml',
    'src/MeetTape/Desktop/SettingsWindow.axaml.cs'
)
foreach ($file in $files) {
    if ($file -eq '.mailmap') { continue }
    if ($file -match '^(README|PRODUCT|ARCHITECTURE|CONTRIBUTING|AGENTS)\.md$|^docs/.*\.md$|^\.github/pull_request_template\.md$') { continue }
    $software = $true
    if ($file -notin $managedPaths -and $file -notmatch '^tests/.*\.cs$|^prompts/(transcription|summary)\.md$|^config\.example\.json$') {
        $package = $true
    }
}
if ($package) { $software = $true }
$results = @("software=$($software.ToString().ToLowerInvariant())", "package=$($package.ToString().ToLowerInvariant())")
$results
if ($env:GITHUB_OUTPUT) { $results | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append }
