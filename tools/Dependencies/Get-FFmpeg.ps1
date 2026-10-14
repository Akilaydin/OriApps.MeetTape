$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$archivePath = Join-Path $repository 'dist/downloads/ffmpeg-9.0-essentials_build.zip'
$archiveSha256 = 'E6B54767A6065919048F1A098EB27211CA4E12B4348A05D88777A5855D0B6E71'
$output = Join-Path $repository 'dist/ffmpeg'
[IO.Directory]::CreateDirectory((Split-Path -Parent $archivePath)) | Out-Null
if (-not [IO.File]::Exists($archivePath)) {
    Invoke-WebRequest -Uri 'https://github.com/GyanD/codexffmpeg/releases/download/9.0/ffmpeg-9.0-essentials_build.zip' -OutFile $archivePath -TimeoutSec 900
}
if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -cne $archiveSha256) { throw 'FFmpeg archive does not match the pinned SHA-256.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $executables = @($archive.Entries | Where-Object { $_.FullName -match '^[^/]+/bin/ffmpeg\.exe$' })
    if ($executables.Count -ne 1) { throw 'FFmpeg archive must contain exactly one bin/ffmpeg.exe.' }
    $packageRoot = $executables[0].FullName.Split('/')[0]
    $probes = @($archive.Entries | Where-Object { $_.FullName -ceq "$packageRoot/bin/ffprobe.exe" })
    $licenses = @($archive.Entries | Where-Object { $_.FullName -ieq "$packageRoot/LICENSE" -or $_.FullName -ieq "$packageRoot/LICENSE.txt" })
    if ($probes.Count -ne 1 -or $licenses.Count -ne 1) { throw 'FFmpeg archive is missing its probe or license.' }
    foreach ($item in @(
        @{ Entry = $executables[0]; Name = 'ffmpeg.exe'; Limit = 512MB },
        @{ Entry = $probes[0]; Name = 'ffprobe.exe'; Limit = 512MB },
        @{ Entry = $licenses[0]; Name = 'ffmpeg-LICENSE.txt'; Limit = 4MB }
    )) {
        if ($item.Entry.Length -le 0 -or $item.Entry.Length -gt $item.Limit) { throw "Invalid FFmpeg entry size: $($item.Name)." }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($item.Entry, (Join-Path $output $item.Name), $true)
    }
} finally { $archive.Dispose() }
foreach ($name in @('ffmpeg.exe', 'ffprobe.exe')) {
    $versionOutput = @(& (Join-Path $output $name) -hide_banner -version)
    if ($LASTEXITCODE -ne 0) { throw "Pinned $name cannot start." }
    $versionOutput | Select-Object -First 1
}
