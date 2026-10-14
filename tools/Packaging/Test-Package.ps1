param([switch]$InstallOnCi)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$feed = Join-Path $repository 'dist/releases'
$testRoot = Join-Path $repository ('dist/package-tests/' + [guid]::NewGuid().ToString('N'))
$root = Join-Path $testRoot 'installation'
$env:TEMP = Join-Path $testRoot 'temp'
$env:TMP = $env:TEMP
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
[IO.Directory]::CreateDirectory($env:TEMP) | Out-Null

$smokeProject = Join-Path $PSScriptRoot 'UpdateSmoke/UpdateSmoke.csproj'
dotnet build $smokeProject --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'SDK smoke build failed.' }
$smokeHost = Join-Path $PSScriptRoot 'UpdateSmoke/bin/Release/net10.0-windows10.0.17763.0/UpdateSmoke.dll'

function Invoke-Smoke([string]$mode) {
    dotnet $smokeHost $mode $feed $root '1.0.0'
    if ($LASTEXITCODE -ne 0) { throw "SDK smoke failed: $mode." }
}

function Invoke-Native([string]$executable, [string[]]$arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($executable)
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    try {
        if (-not $process.WaitForExit(120000)) { throw 'Native package operation timed out.' }
        if ($process.ExitCode -ne 0) { throw "Native package operation failed: $($process.ExitCode)." }
    } finally { $process.Dispose() }
}

if ($InstallOnCi -and ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted')) {
    throw 'Install/uninstall smoke is restricted to disposable GitHub-hosted runners.'
}

Invoke-Smoke 'decline'
Invoke-Smoke 'download'
Invoke-Smoke 'startup'
Invoke-Smoke 'startup'

$corruptFeed = Join-Path $testRoot 'corrupt-feed'
[IO.Directory]::CreateDirectory($corruptFeed) | Out-Null
Copy-Item -LiteralPath (Join-Path $feed 'releases.win.json') -Destination $corruptFeed
$feedData = Get-Content -LiteralPath (Join-Path $feed 'releases.win.json') -Raw | ConvertFrom-Json
$newest = @($feedData.Assets | Where-Object { $_.Version -eq '1.0.1' -and $_.Type -eq 'Full' })[0].FileName
Copy-Item -LiteralPath (Join-Path $feed $newest) -Destination $corruptFeed
$stream = [IO.File]::OpenWrite((Join-Path $corruptFeed $newest))
try { $stream.WriteByte(0); $stream.Flush($true) } finally { $stream.Dispose() }
dotnet $smokeHost checksum $corruptFeed (Join-Path $testRoot 'checksum-installation') '1.0.0'
if ($LASTEXITCODE -ne 0) { throw 'SDK checksum rejection failed.' }

# Verify every full package contains the pinned binary and licenses, with no CLI wrapper.
foreach ($version in @('1.0.0', '1.0.1')) {
    $package = [IO.Compression.ZipFile]::OpenRead((Join-Path $feed "OriApps.MeetTape-$version-full.nupkg"))
    try {
        foreach ($name in @('MeetTape.exe', 'ffmpeg.exe', 'ffmpeg-LICENSE.txt', 'LICENSE', 'THIRD-PARTY-NOTICES.txt')) {
            if (@($package.Entries | Where-Object { $_.FullName -eq "lib/app/$name" }).Count -ne 1) { throw "Missing package file: $name." }
        }
        if (@($package.Entries | Where-Object { $_.Name -eq 'meettape-cli.cmd' }).Count -ne 0) { throw 'Obsolete CLI wrapper is present.' }
    } finally { $package.Dispose() }
}
if (-not $InstallOnCi) { Write-Output 'Local package/source/pending-restart checks passed; native install/uninstall not run.'; return }

# Only CI may register shortcuts/uninstaller. No setup is run on the owner's Windows account.
$install = Join-Path $testRoot 'native-installation'
$configRoot = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'MeetTape'
$meetingRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MeetTape/Meetings'
if ([IO.Directory]::Exists($configRoot) -or [IO.Directory]::Exists($meetingRoot)) { throw 'Disposable runner already has MeetTape user data; refusing to modify it.' }
[IO.Directory]::CreateDirectory((Join-Path $configRoot 'prompts')) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $meetingRoot 'fixture/chunks')) | Out-Null
$dataFiles = @((Join-Path $configRoot 'config.json'), (Join-Path $configRoot 'prompts/transcription.md'),
    (Join-Path $meetingRoot 'fixture/me.mp3'), (Join-Path $meetingRoot 'fixture/chunks/0001.json'))
foreach ($file in $dataFiles) { [IO.File]::WriteAllText($file, "preserve-$file") }
$before = @($dataFiles | Get-FileHash -Algorithm SHA256 | ForEach-Object Hash)
$keyName = 'MEETTAPE_PACKAGE_TEST_' + [guid]::NewGuid().ToString('N')
[Environment]::SetEnvironmentVariable($keyName, 'fixture-only', 'User')
try {
    Invoke-Native (Join-Path $feed 'first-Setup.exe') @('--silent', '--installto', $install)
    if (-not [IO.File]::Exists((Join-Path $install 'Update.exe'))) { throw 'Installed updater missing.' }
    $uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\OriApps.MeetTape'
    if (-not (Test-Path -LiteralPath $uninstallKey)) { throw 'Windows uninstaller is not registered.' }
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'MeetTape.lnk'
    if (-not [IO.File]::Exists($shortcut)) { throw 'Start menu shortcut missing.' }
    $shell = New-Object -ComObject WScript.Shell
    try {
        $link = $shell.CreateShortcut($shortcut) # Read our existing CI-created shortcut; do not Save.
        $location = $link.IconLocation
        if ([string]::IsNullOrWhiteSpace($location)) { $location = $link.TargetPath }
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link)
        $iconExecutable = $location -replace ',\s*-?\d+\s*$', ''
        $iconExecutable = [Environment]::ExpandEnvironmentVariables($iconExecutable.Trim('"'))
        $installationPrefix = [IO.Path]::GetFullPath($install).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (-not [IO.Path]::GetFullPath($iconExecutable).StartsWith($installationPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Start menu icon must reference this isolated installation.'
        }
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot '../Tests/Test-Desktop.ps1') -ExecutablePath $iconExecutable -IconOnly
        if ($LASTEXITCODE -ne 0) { throw 'Installed shortcut icon checks failed.' }
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot '../Tests/Test-Desktop.ps1') -ExecutablePath (Join-Path $install 'Update.exe') -IconOnly
    if ($LASTEXITCODE -ne 0) { throw 'Installed updater icon checks failed.' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot '../Tests/Test-Desktop.ps1') -ExecutablePath (Join-Path $install 'current/MeetTape.exe') -SkipNativeWindow
    if ($LASTEXITCODE -ne 0) { throw 'Installed GUI startup failed.' }
    & (Join-Path $install 'current/ffmpeg.exe') -hide_banner -version | Select-Object -First 1
    if ($LASTEXITCODE -ne 0) { throw 'Installed FFmpeg failed.' }
    $oldMarker = [IO.File]::ReadAllText((Join-Path $install 'current/sq.version'))
    $pending = Join-Path $feed 'OriApps.MeetTape-1.0.1-full.nupkg'
    Copy-Item -LiteralPath $pending -Destination (Join-Path $install 'packages')
    Invoke-Native (Join-Path $install 'current/MeetTape.exe') @('--ui-smoke')
    if ([IO.File]::ReadAllText((Join-Path $install 'current/sq.version')) -cne $oldMarker) { throw 'Restart auto-applied pending update.' }
    Invoke-Native (Join-Path $install 'Update.exe') @('apply', '--package', $pending, '--norestart', '--silent')
    if ([IO.File]::ReadAllText((Join-Path $install 'current/sq.version')) -notmatch '<version>1.0.1</version>') { throw 'Second version was not applied.' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot '../Tests/Test-Desktop.ps1') -ExecutablePath (Join-Path $install 'current/MeetTape.exe') -SkipNativeWindow
    if ($LASTEXITCODE -ne 0) { throw 'Updated GUI/icon checks failed.' }
    Invoke-Native (Join-Path $install 'Update.exe') @('uninstall', '--silent')
    if ([IO.Directory]::Exists((Join-Path $install 'current'))) { throw 'Uninstall retained application binaries.' }
    if ((Test-Path -LiteralPath $uninstallKey) -or [IO.File]::Exists($shortcut)) { throw 'Uninstall retained registry entry/shortcut.' }
    $after = @($dataFiles | Get-FileHash -Algorithm SHA256 | ForEach-Object Hash)
    if (Compare-Object $before $after) { throw 'User fixture data changed.' }
    if ([Environment]::GetEnvironmentVariable($keyName, 'User') -cne 'fixture-only') { throw 'User key fixture changed.' }
    Write-Output 'Disposable runner: fresh setup, installed GUI/FFmpeg, pending restart unchanged, full update to 1.0.1, uninstall and fixture preservation passed.'
} finally {
    [Environment]::SetEnvironmentVariable($keyName, $null, 'User')
}
