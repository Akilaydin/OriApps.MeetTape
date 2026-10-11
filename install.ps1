param(
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\MeetTape'),
    [switch]$UpdateOnly,
    [switch]$SkipPath,
    [ValidateRange(1, 600)][int]$DownloadIdleTimeoutSeconds = 90,
    [ValidateRange(1, 60)][int]$ProgressIntervalSeconds = 5,
    [object]$HttpClient,
    [string]$FfmpegArchiveUrl = 'https://github.com/GyanD/codexffmpeg/releases/download/9.0/ffmpeg-9.0-essentials_build.zip',
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$FfmpegArchiveSha256 = 'E6B54767A6065919048F1A098EB27211CA4E12B4348A05D88777A5855D0B6E71'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Net.Http
if ($null -ne $HttpClient -and $HttpClient -isnot [Net.Http.HttpClient]) {
    throw 'HttpClient test seam requires a System.Net.Http.HttpClient instance.'
}

$repository = 'Akilaydin/OriApps.MeetTape'
$root = [IO.Path]::GetFullPath($InstallDirectory)
$appDirectory = Join-Path $root 'app'
$executable = Join-Path $appDirectory 'MeetTape.exe'
$ffmpegExecutable = Join-Path $appDirectory 'ffmpeg.exe'
$ffmpegLicense = Join-Path $appDirectory 'ffmpeg-LICENSE.txt'
$ffmpegMarker = Join-Path $appDirectory 'ffmpeg-version.txt'
$ffmpegIdentity = "9.0 $($FfmpegArchiveSha256.ToUpperInvariant())"
$versionFile = Join-Path $root 'installed-version.txt'
$launcher = Join-Path $root 'meettape.cmd'
$pathHelper = Join-Path $PSScriptRoot 'InstallerPath.ps1'
if (-not [IO.File]::Exists($pathHelper)) { $pathHelper = Join-Path $PSScriptRoot 'tools\InstallerPath.ps1' }
. $pathHelper

function Write-Stage([string]$message) {
    Write-Host "[MeetTape] $message"
}

function Install-StagedFile([string]$source, [string]$destination) {
    if ([IO.File]::Exists($destination)) {
        # File.Move(source, destination, overwrite) is unavailable on Windows PowerShell 5.1.
        # .NET Framework requires a backup pathname for File.Replace.
        $backup = Join-Path (Split-Path -Parent $source) ('.replace-' + [guid]::NewGuid().ToString('N'))
        try { [IO.File]::Replace($source, $destination, $backup) }
        finally { if ([IO.File]::Exists($backup)) { [IO.File]::Delete($backup) } }
    } else {
        [IO.File]::Move($source, $destination)
    }
}

function Expand-VerifiedFfmpeg([string]$archivePath, [string]$stagingDirectory) {
    Add-Type -AssemblyName System.IO.Compression
    $archiveStream = [IO.File]::OpenRead($archivePath)
    try {
        $archive = [IO.Compression.ZipArchive]::new($archiveStream, [IO.Compression.ZipArchiveMode]::Read, $false)
        try {
            $executables = @($archive.Entries | Where-Object {
                $_.FullName.Replace('\', '/') -match '^[^/]+/bin/ffmpeg\.exe$'
            })
            if ($executables.Count -ne 1) { throw 'FFmpeg archive must contain exactly one bin/ffmpeg.exe.' }
            $packageRoot = $executables[0].FullName.Replace('\', '/').Split('/')[0]
            $licenses = @($archive.Entries | Where-Object {
                $_.FullName.Replace('\', '/') -ieq "$packageRoot/LICENSE" -or
                $_.FullName.Replace('\', '/') -ieq "$packageRoot/LICENSE.txt"
            })
            if ($licenses.Count -ne 1) { throw 'FFmpeg archive is missing its packaged license.' }

            foreach ($item in @(
                @{ Entry = $executables[0]; Name = 'ffmpeg.exe'; Limit = 512MB },
                @{ Entry = $licenses[0]; Name = 'ffmpeg-LICENSE.txt'; Limit = 4MB }
            )) {
                $entry = $item.Entry
                if ($entry.Length -le 0 -or $entry.Length -gt $item.Limit) {
                    throw "FFmpeg archive contains an invalid $($item.Name) size."
                }
                $inputStream = $entry.Open()
                $outputStream = $null
                try {
                    $outputStream = [IO.File]::Open((Join-Path $stagingDirectory $item.Name),
                        [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                    $inputStream.CopyTo($outputStream)
                    $outputStream.Flush($true)
                    if ($outputStream.Length -ne $entry.Length) {
                        throw "Incomplete FFmpeg archive entry: $($item.Name)."
                    }
                } finally {
                    if ($outputStream) { $outputStream.Dispose() }
                    $inputStream.Dispose()
                }
            }
        } finally { $archive.Dispose() }
    } finally { $archiveStream.Dispose() }
}

function Read-ReleaseMetadata {
    $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(60))
    $response = $null
    try {
        $response = $HttpClient.GetAsync("https://api.github.com/repos/$repository/releases/latest", $deadline.Token).GetAwaiter().GetResult()
        [void]$response.EnsureSuccessStatusCode()
        $json = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        return ($json | ConvertFrom-Json)
    } catch {
        if ($deadline.IsCancellationRequested) { throw 'GitHub Release lookup timed out after 60 seconds. Check GitHub connectivity.' }
        throw 'Cannot read the public GitHub Release. Check repository availability and network access.'
    } finally {
        if ($response) { $response.Dispose() }
        $deadline.Dispose()
    }
}

function Download-Release([string]$tag, [string]$destination, [long]$expectedSize, [string]$fileName, [string]$url) {
    $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(15))
    $idle = [Threading.CancellationTokenSource]::CreateLinkedTokenSource($deadline.Token)
    $response = $null
    $stream = $null
    $file = $null
    try {
        $idle.CancelAfter([TimeSpan]::FromSeconds($DownloadIdleTimeoutSeconds))
        $response = $HttpClient.GetAsync($url, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $idle.Token).GetAwaiter().GetResult()
        [void]$response.EnsureSuccessStatusCode()
        $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $file = [IO.File]::Open((Join-Path $destination $fileName), [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write, [IO.FileShare]::Read)
        $elapsed = [Diagnostics.Stopwatch]::StartNew()
        $lastReport = -[double]$ProgressIntervalSeconds
        [long]$received = 0
        $buffer = [byte[]]::new(81920)
        while ($true) {
            $idle.CancelAfter([TimeSpan]::FromSeconds($DownloadIdleTimeoutSeconds))
            $read = $stream.ReadAsync($buffer, 0, $buffer.Length, $idle.Token)
            do {
                $readCompleted = $read.Wait(250)
                if (-not $readCompleted) { $idle.Token.ThrowIfCancellationRequested() }
                if ($elapsed.Elapsed.TotalSeconds - $lastReport -ge $ProgressIntervalSeconds) {
                    $progress = if ($expectedSize -gt 0) {
                        '{0:N0}% ({1:N1} / {2:N1} MiB)' -f (100 * $received / $expectedSize), ($received / 1MB), ($expectedSize / 1MB)
                    } else { '{0:N1} MiB' -f ($received / 1MB) }
                    Write-Stage "Downloading $fileName ($tag): $progress, $([int]$elapsed.Elapsed.TotalSeconds)s elapsed"
                    $lastReport = $elapsed.Elapsed.TotalSeconds
                }
            } while (-not $readCompleted)
            $count = $read.GetAwaiter().GetResult()
            if ($count -eq 0) { break }
            $file.Write($buffer, 0, $count)
            $received += $count
            if ($expectedSize -gt 0 -and $received -gt $expectedSize) {
                throw 'Downloaded asset exceeds its published size.'
            }
        }
        if ($expectedSize -gt 0 -and $received -ne $expectedSize) {
            throw 'Downloaded asset does not match its published size.'
        }
        $file.Flush($true)
    } catch {
        if ($deadline.IsCancellationRequested) { throw 'Download exceeded 15 minutes. Check the network connection and retry.' }
        if ($idle.IsCancellationRequested) {
            throw "Download stalled for $DownloadIdleTimeoutSeconds seconds without new bytes. Check GitHub asset/CDN access, then retry."
        }
        throw "Cannot download $fileName from the public GitHub Release; check network access, disk space and permissions."
    } finally {
        $idle.Cancel()
        if ($file) { $file.Dispose() }
        if ($stream) { $stream.Dispose() }
        if ($response) { $response.Dispose() }
        $idle.Dispose()
        $deadline.Dispose()
    }
}

if ($UpdateOnly -and -not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw 'MeetTape is not installed. Run .\install.ps1 first.'
}
if (-not [Uri]::IsWellFormedUriString($FfmpegArchiveUrl, [UriKind]::Absolute) -or
    ([Uri]$FfmpegArchiveUrl).Scheme -cne 'https') {
    throw 'FFmpeg archive URL must be absolute HTTPS.'
}

Write-Stage 'Checking the latest public GitHub Release...'
[IO.Directory]::CreateDirectory($root) | Out-Null
try {
    $lock = [IO.File]::Open((Join-Path $root '.install.lock'), [IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
} catch [IO.IOException] {
    throw 'Another MeetTape update is running. Stop the previous command with Ctrl+C before retrying.'
}
$ownsClient = $null -eq $HttpClient
try {
    if ($ownsClient) {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        $HttpClient = [Net.Http.HttpClient]::new()
        $HttpClient.Timeout = [Threading.Timeout]::InfiniteTimeSpan
    }
    $HttpClient.DefaultRequestHeaders.Authorization = $null
    if (-not $HttpClient.DefaultRequestHeaders.Contains("User-Agent")) { $HttpClient.DefaultRequestHeaders.Add("User-Agent", "MeetTape") }
    $release = Read-ReleaseMetadata
    $tag = [string]$release.tag_name
    if ($release.draft -or $tag -notmatch '^v\d+\.\d+\.\d+$') {
        throw 'The latest MeetTape release is not a published semantic version.'
    }

    $scriptNames = @('install.ps1', 'update.ps1', 'InstallerPath.ps1')
    $legalNames = @('LICENSE', 'THIRD-PARTY-NOTICES.txt')
    $requiredNames = @('MeetTape.exe') + $scriptNames + $legalNames
    $assets = @{}
    foreach ($name in $requiredNames) {
        $matching = @($release.assets | Where-Object { $_.name -ceq $name })
        if ($matching.Count -ne 1 -or $matching[0].digest -notmatch '^sha256:[0-9a-fA-F]{64}$' -or $matching[0].size -le 0 -or
            $matching[0].browser_download_url -cne "https://github.com/$repository/releases/download/$tag/$name") {
            throw "The release must contain exactly one $name with a SHA-256 digest."
        }
        $assets[$name] = $matching[0]
    }

    $installedTag = if (Test-Path -LiteralPath $versionFile -PathType Leaf) {
        (Get-Content -LiteralPath $versionFile -Raw).Trim()
    } else { '' }
    $isInstalled = Test-Path -LiteralPath $executable -PathType Leaf
    if ($installedTag -match '^v\d+\.\d+\.\d+$' -and [version]$installedTag.Substring(1) -gt [version]$tag.Substring(1)) {
        throw "Installed $installedTag is newer than the published $tag; refusing downgrade."
    }
    $needsInstall = -not $isInstalled -or $installedTag -notmatch '^v\d+\.\d+\.\d+$' -or
        [version]$installedTag.Substring(1) -lt [version]$tag.Substring(1)
    Write-Stage "Installed version: $(if ($installedTag) { $installedTag } else { 'unknown' }); latest: $tag."

    $filesToInstall = @()
    if ($needsInstall) { $filesToInstall += 'MeetTape.exe' }
    foreach ($name in ($scriptNames + $legalNames)) {
        $path = Join-Path $appDirectory $name
        if ($needsInstall -or -not [IO.File]::Exists($path) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $assets[$name].digest.Substring(7)) {
            $filesToInstall += $name
        }
    }
    # A pre-existing binary without our marker belongs to the user, not the installer.
    $unmanagedFfmpeg = [IO.File]::Exists($ffmpegExecutable) -and -not [IO.File]::Exists($ffmpegMarker)
    $needsFfmpeg = -not $unmanagedFfmpeg -and (
        -not [IO.File]::Exists($ffmpegExecutable) -or
        -not [IO.File]::Exists($ffmpegLicense) -or
        -not [IO.File]::Exists($ffmpegMarker) -or
        ([IO.FileInfo]$ffmpegExecutable).Length -eq 0 -or ([IO.FileInfo]$ffmpegLicense).Length -eq 0)
    if (-not $needsFfmpeg) {
        if (-not $unmanagedFfmpeg) {
            $needsFfmpeg = (Get-Content -LiteralPath $ffmpegMarker -Raw).Trim() -cne $ffmpegIdentity
        } else {
            Write-Stage 'Preserving existing user-supplied ffmpeg.exe.'
        }
    }
    if ($filesToInstall.Count -gt 0 -or $needsFfmpeg) {
        $stagingDirectory = Join-Path $root ('.staging-' + [guid]::NewGuid().ToString('N'))
        [IO.Directory]::CreateDirectory($stagingDirectory) | Out-Null
        $stagedExecutable = Join-Path $stagingDirectory 'MeetTape.exe'
        try {
            foreach ($name in $filesToInstall) {
                $asset = $assets[$name]
                Write-Stage "Downloading $name ($('{0:N1}' -f ($asset.size / 1MB)) MiB)..."
                Download-Release $tag $stagingDirectory ([long]$asset.size) $name ([string]$asset.browser_download_url)
                $stagedPath = Join-Path $stagingDirectory $name
                if (-not [IO.File]::Exists($stagedPath) -or
                    (Get-FileHash -LiteralPath $stagedPath -Algorithm SHA256).Hash -ine $asset.digest.Substring(7)) {
                    throw "Downloaded $name is missing or does not match the published SHA-256 digest."
                }
            }
            if ($needsFfmpeg) {
                $archiveName = 'ffmpeg-9.0-essentials_build.zip'
                Write-Stage 'Downloading FFmpeg 9.0...'
                Download-Release 'FFmpeg 9.0' $stagingDirectory 0 $archiveName $FfmpegArchiveUrl
                $archivePath = Join-Path $stagingDirectory $archiveName
                if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ine $FfmpegArchiveSha256) {
                    throw 'Downloaded FFmpeg archive does not match the pinned SHA-256.'
                }
                Expand-VerifiedFfmpeg $archivePath $stagingDirectory
                [IO.File]::Delete($archivePath)
                [IO.File]::WriteAllText((Join-Path $stagingDirectory 'ffmpeg-version.txt'),
                    "$ffmpegIdentity`n", [Text.Encoding]::ASCII)
            }

            if ($needsInstall) {
                Write-Stage 'Checking executable startup...'
                & $stagedExecutable --help | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Downloaded MeetTape.exe failed its startup check.' }
            }

            Write-Stage 'Installing verified MeetTape files...'
            [IO.Directory]::CreateDirectory($appDirectory) | Out-Null
            $versionTemp = Join-Path $root ('.version-' + [guid]::NewGuid().ToString('N'))
            try {
                if ($needsInstall) { [IO.File]::WriteAllText($versionTemp, "$tag`n", [Text.Encoding]::ASCII) }
                # Preflight every replacement before changing installed files.
                $filesToCommit = @($filesToInstall)
                if ($needsFfmpeg) {
                    $filesToCommit += @('ffmpeg.exe', 'ffmpeg-LICENSE.txt', 'ffmpeg-version.txt')
                }
                foreach ($name in $filesToCommit) {
                    $destination = Join-Path $appDirectory $name
                    if ([IO.File]::Exists($destination)) {
                        $stream = [IO.File]::Open($destination, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite,
                            [IO.FileShare]::None)
                        $stream.Dispose()
                    }
                }
                if ($needsInstall) {
                    # A locked EXE must fail before changing any installed companion script.
                    Install-StagedFile $stagedExecutable $executable
                }
                if ($needsFfmpeg) {
                    Install-StagedFile (Join-Path $stagingDirectory 'ffmpeg.exe') $ffmpegExecutable
                    Install-StagedFile (Join-Path $stagingDirectory 'ffmpeg-LICENSE.txt') $ffmpegLicense
                }
                foreach ($name in ($scriptNames + $legalNames)) {
                    if ($filesToInstall -contains $name) {
                        Install-StagedFile (Join-Path $stagingDirectory $name) (Join-Path $appDirectory $name)
                    }
                }
                if ($needsInstall) {
                    Install-StagedFile $versionTemp $versionFile
                }
                if ($needsFfmpeg) {
                    Install-StagedFile (Join-Path $stagingDirectory 'ffmpeg-version.txt') $ffmpegMarker
                }
            } finally {
                if ([IO.File]::Exists($versionTemp)) { [IO.File]::Delete($versionTemp) }
            }
            Write-Output "MeetTape $tag files installed at $appDirectory"
        } finally {
            foreach ($name in (@($filesToInstall) + @('ffmpeg.exe', 'ffmpeg-LICENSE.txt', 'ffmpeg-version.txt', 'ffmpeg-9.0-essentials_build.zip'))) {
                $stagedPath = Join-Path $stagingDirectory $name
                if ([IO.File]::Exists($stagedPath)) { [IO.File]::Delete($stagedPath) }
            }
            # Only this invocation's known, empty staging directory is removed.
            if ([IO.Directory]::Exists($stagingDirectory)) { [IO.Directory]::Delete($stagingDirectory) }
        }
    } else {
        Write-Output "MeetTape $installedTag is already up to date."
    }

    if (-not $SkipPath) {
        & $executable --help | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Installed executable failed direct startup; PATH and launcher unchanged.' }
        $previousPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        $newPath = Get-MeetTapeUserPath $previousPath $root
        $pathChanged = $newPath -cne $previousPath
        $launcherRemoved = $false
        try {
            if ($pathChanged) { [Environment]::SetEnvironmentVariable('Path', $newPath, 'User') }
            $launcherRemoved = Remove-MeetTapeLegacyLauncher $launcher
        } catch {
            if ($pathChanged) { [Environment]::SetEnvironmentVariable('Path', $previousPath, 'User') }
            throw
        }
        if ($pathChanged) { Write-Output 'Added the MeetTape app directory to the user PATH (open a new terminal).' }
        if ($launcherRemoved) { Write-Output 'Removed obsolete meettape.cmd; MeetTape.exe now runs directly.' }
    } elseif ([IO.File]::Exists($launcher)) {
        Write-Output 'Legacy launcher preserved because -SkipPath was specified; run without -SkipPath to migrate it.'
    }
    Write-Output 'For first-time configuration run: meettape init'
} finally {
    if ($ownsClient -and $HttpClient) { $HttpClient.Dispose() }
    $lock.Dispose()
}
