param(
    [string]$InstallDirectory,
    [switch]$SkipPath,
    [ValidateRange(1, 600)][int]$DownloadIdleTimeoutSeconds = 90,
    [ValidateRange(1, 60)][int]$ProgressIntervalSeconds = 5,
    [ValidateRange(1, 2147483647)][int]$WaitForProcessId,
    [object]$HttpClient,
    [string]$FfmpegArchiveUrl = 'https://github.com/GyanD/codexffmpeg/releases/download/9.0/ffmpeg-9.0-essentials_build.zip',
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$FfmpegArchiveSha256 = 'E6B54767A6065919048F1A098EB27211CA4E12B4348A05D88777A5855D0B6E71'
)

$ErrorActionPreference = 'Stop'
try {
    if (-not $InstallDirectory) {
        $InstallDirectory = if ((Split-Path -Leaf $PSScriptRoot) -ieq 'app') {
            Split-Path -Parent $PSScriptRoot
        } else {
            Join-Path $env:LOCALAPPDATA 'Programs\MeetTape'
        }
    }
    if ($WaitForProcessId -gt 0) {
        $owner = Get-Process -Id $WaitForProcessId -ErrorAction SilentlyContinue
        if ($owner) {
            try {
                if (-not $owner.WaitForExit(90000)) { throw 'MeetTape did not finish shutting down; update canceled.' }
            } finally { $owner.Dispose() }
        }
    }
    Write-Host '[MeetTape] Checking for updates (use Exit in every MeetTape window/tray first)...'
    & (Join-Path $PSScriptRoot 'install.ps1') -InstallDirectory $InstallDirectory -UpdateOnly -SkipPath:$SkipPath -DownloadIdleTimeoutSeconds $DownloadIdleTimeoutSeconds -ProgressIntervalSeconds $ProgressIntervalSeconds -HttpClient $HttpClient -FfmpegArchiveUrl $FfmpegArchiveUrl -FfmpegArchiveSha256 $FfmpegArchiveSha256
} catch {
    if ($WaitForProcessId -gt 0) {
        Write-Host "[MeetTape] Update failed: $($_.Exception.Message)" -ForegroundColor Red
        # Only a CLI-spawned updater with an interactive console pauses for diagnostics.
        # Direct/manual invocations and redirected-input automation keep normal error behavior.
        if (-not [Console]::IsInputRedirected) {
            [void](Read-Host 'Press Enter to close the updater window')
        }
    }
    throw
}
