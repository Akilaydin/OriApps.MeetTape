$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'InstallerPath.ps1')
$testParent = Join-Path $repository 'dist\tests'
$testRoot = Join-Path $testParent ('MeetTapeInstallerTests-' + [guid]::NewGuid().ToString('N'))
$originalPath = $env:PATH
$originalTemp = $env:TEMP
$originalTmp = $env:TMP
[IO.Directory]::CreateDirectory($testParent) | Out-Null
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$env:TEMP = $testRoot
$env:TMP = $testRoot

try {
    # CLI-launched updates must retain a visible failure diagnostic and return a
    # nonzero exit code when input is redirected (no interactive Read-Host wait).
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = (Get-Command powershell.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.RedirectStandardInput = $true
    $updaterPath = Join-Path $repository 'update.ps1'
    $missingInstallPath = Join-Path $testRoot 'missing-install'
    if ($updaterPath.Contains('"') -or $missingInstallPath.Contains('"')) { throw 'Unsupported test directory name.' }
    $info.Arguments = '-NoProfile -File "' + $updaterPath +
        '" -WaitForProcessId 2147483647 -InstallDirectory "' + $missingInstallPath + '" -SkipPath'
    $failedUpdater = [Diagnostics.Process]::Start($info)
    if (-not $failedUpdater) { throw 'Cannot start updater failure regression.' }
    try {
        $stdout = $failedUpdater.StandardOutput.ReadToEndAsync()
        $stderr = $failedUpdater.StandardError.ReadToEndAsync()
        $failedUpdater.StandardInput.Close()
        if (-not $failedUpdater.WaitForExit(20000)) { throw 'Redirected updater failure did not terminate.' }
        if ($failedUpdater.ExitCode -eq 0 -or
            $stdout.GetAwaiter().GetResult() -notmatch '\[MeetTape\] Update failed:') {
            throw 'Updater failure must be reported and exit nonzero.'
        }
        [void]$stderr.GetAwaiter().GetResult()
    } finally {
        if (-not $failedUpdater.HasExited) {
            $failedUpdater.Kill($true)
            [void]$failedUpdater.WaitForExit(5000)
        }
        $failedUpdater.Dispose()
    }

    $simulatedRoot = Join-Path $testRoot 'legacy-install'
    $otherPath = Join-Path $testRoot 'other-app'
    $currentPath = "$otherPath;$simulatedRoot;$simulatedRoot\app;$simulatedRoot;C:\Windows"
    $expectedPath = "$otherPath;$simulatedRoot\app;C:\Windows"
    $migratedPath = Get-MeetTapeUserPath $currentPath $simulatedRoot
    if ($migratedPath -cne $expectedPath -or (Get-MeetTapeUserPath $migratedPath $simulatedRoot) -cne $expectedPath) {
        throw 'PATH migration must remove old root, retain other entries, and be idempotent.'
    }
    if ((Get-MeetTapeUserPath "$otherPath;$simulatedRoot" $simulatedRoot) -cne "$otherPath;$simulatedRoot\app") {
        throw 'PATH migration must append app when it is not present.'
    }

    [IO.Directory]::CreateDirectory($simulatedRoot) | Out-Null
    $launcherToMigrate = Join-Path $simulatedRoot 'meettape.cmd'
    [IO.File]::WriteAllText($launcherToMigrate, 'legacy launcher')
    $unrelated = Join-Path $simulatedRoot 'config.json'
    [IO.File]::WriteAllText($unrelated, 'Keep configuration')
    if (-not (Remove-MeetTapeLegacyLauncher $launcherToMigrate) -or
        [IO.File]::Exists($launcherToMigrate) -or
        (Remove-MeetTapeLegacyLauncher $launcherToMigrate) -or
        (Get-Content -LiteralPath $unrelated -Raw) -cne 'Keep configuration' -or
        @(Get-ChildItem -LiteralPath $simulatedRoot -File).Count -ne 1) {
        throw 'Launcher removal must not create extra files or modify unrelated configuration.'
    }

    $distribution = Join-Path $testRoot 'distribution'
    [IO.Directory]::CreateDirectory($distribution) | Out-Null
    $source = Join-Path $distribution 'MeetTape.exe'
    # Compile a local .NET Framework fixture using the built-in Windows PowerShell compiler.
    # Avoids SDK restores, external package sources and modifying machine installations.
    Add-Type -TypeDefinition @'
public static class Program {
    public static void Main(string[] args) {
        System.Console.WriteLine("MeetTape test executable");
    }
}
'@ -OutputAssembly $source -OutputType ConsoleApplication
    $digest = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $fixtureRoot = Join-Path $testRoot 'ffmpeg-fixture'
    $ffmpegPackage = Join-Path $fixtureRoot 'ffmpeg-9.0-essentials_build'
    [IO.Directory]::CreateDirectory((Join-Path $ffmpegPackage 'bin')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $ffmpegPackage 'bin\ffmpeg.exe'), 'Fake FFmpeg executable; not launched')
    [IO.File]::WriteAllText((Join-Path $ffmpegPackage 'LICENSE'), 'Fixture FFmpeg license')
    $ffmpegZip = Join-Path $testRoot 'ffmpeg-fixture.zip'
    $fixtureArchive = [IO.Compression.ZipFile]::Open($ffmpegZip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($fixtureArchive,
            (Join-Path $ffmpegPackage 'bin\ffmpeg.exe'), 'ffmpeg-9.0-essentials_build/bin/ffmpeg.exe')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($fixtureArchive,
            (Join-Path $ffmpegPackage 'LICENSE'), 'ffmpeg-9.0-essentials_build/LICENSE')
    } finally { $fixtureArchive.Dispose() }
    $ffmpegHash = (Get-FileHash -LiteralPath $ffmpegZip -Algorithm SHA256).Hash
    $ffmpegUrl = 'https://fixtures.meettape.invalid/ffmpeg-9.0-essentials_build.zip'
    $ffmpegArgs = @{ FfmpegArchiveUrl = $ffmpegUrl; FfmpegArchiveSha256 = $ffmpegHash }

    Add-Type -ReferencedAssemblies 'System.Net.Http' -TypeDefinition @'
using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public sealed class MeetTapeReleaseHandler : HttpMessageHandler {
    public string ReleaseTag { get; set; }
    public string Digest { get; set; }
    public int Downloads { get; set; }
    public Hashtable Files { get; set; }
    public bool Stall { get; set; }
    public bool InvalidAssetUrl { get; set; }
    public HttpStatusCode MetadataStatus { get; set; }
    public string FfmpegArchiveUrl { get; set; }
    public string FfmpegArchivePath { get; set; }
    public int FfmpegDownloads { get; set; }
    public MeetTapeReleaseHandler() { MetadataStatus = HttpStatusCode.OK; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        if (request.Method != HttpMethod.Get || request.Headers.Authorization != null ||
            request.Headers.UserAgent.Count == 0) throw new Exception("Expected anonymous GET with User-Agent.");
        const string api = "https://api.github.com/repos/Akilaydin/OriApps.MeetTape/releases/latest";
        const string downloads = "https://github.com/Akilaydin/OriApps.MeetTape/releases/download/";
        if (request.RequestUri.AbsoluteUri == api) {
            var json = new StringBuilder();
            json.Append("{\"tag_name\":\"").Append(ReleaseTag).Append("\",\"draft\":false,\"assets\":[");
            bool first = true;
            foreach (DictionaryEntry entry in Files) {
                string name = entry.Key.ToString(), path = entry.Value.ToString();
                string digest;
                if (name == "MeetTape.exe") digest = Digest;
                else {
                    using (var file = File.OpenRead(path))
                    using (var sha = SHA256.Create())
                        digest = "sha256:" + BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
                }
                if (!first) json.Append(",");
                first = false;
                json.Append("{\"name\":\"").Append(name).Append("\",\"digest\":\"").Append(digest)
                    .Append("\",\"size\":").Append(new FileInfo(path).Length)
                    .Append(",\"browser_download_url\":\"")
                    .Append(InvalidAssetUrl ? "https://example.invalid/asset" : downloads + ReleaseTag + "/" + name)
                    .Append("\"}");
            }
            json.Append("]}");
            return Task.FromResult(new HttpResponseMessage(MetadataStatus) { Content = new StringContent(
                json.ToString(), Encoding.UTF8, "application/json") });
        }
        if (request.RequestUri.AbsoluteUri == FfmpegArchiveUrl) {
            FfmpegDownloads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StreamContent(File.OpenRead(FfmpegArchivePath)) });
        }
        string prefix = downloads + ReleaseTag + "/";
        if (!request.RequestUri.AbsoluteUri.StartsWith(prefix, StringComparison.Ordinal))
            throw new Exception("Unexpected download URL.");
        string asset = request.RequestUri.AbsoluteUri.Substring(prefix.Length);
        if (!Files.ContainsKey(asset)) throw new Exception("Unknown asset.");
        Downloads++;
        Stream content = Stall ? (Stream)new StalledDownload() : File.OpenRead(Files[asset].ToString());
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(content) });
    }
}
public sealed class StalledDownload : Stream {
    public override bool CanRead { get { return true; } }
    public override bool CanSeek { get { return false; } }
    public override bool CanWrite { get { return false; } }
    public override long Length { get { throw new NotSupportedException(); } }
    public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) {
        await Task.Delay(Timeout.Infinite, token);
        return 0;
    }
    public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
    public override void SetLength(long value) { throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
}
'@
    $handler = [MeetTapeReleaseHandler]::new()
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [Threading.Timeout]::InfiniteTimeSpan
    $handler.FfmpegArchiveUrl = $ffmpegUrl
    $handler.FfmpegArchivePath = $ffmpegZip

    $handler.ReleaseTag = 'v1.2.3'
    $handler.Digest = "sha256:$digest"
    $handler.Downloads = 0
    $handler.Files = @{
        'MeetTape.exe' = $source
        'install.ps1' = Join-Path $repository 'install.ps1'
        'update.ps1' = Join-Path $repository 'update.ps1'
        'InstallerPath.ps1' = Join-Path $repository 'tools\InstallerPath.ps1'
        'LICENSE' = Join-Path $repository 'LICENSE'
        'THIRD-PARTY-NOTICES.txt' = Join-Path $repository 'THIRD-PARTY-NOTICES.txt'
    }

    $install = Join-Path $testRoot 'installation'
    $failedAsExpected = $false
    try { & (Join-Path $repository 'update.ps1') -InstallDirectory $install -SkipPath -HttpClient $client @ffmpegArgs | Out-Null }
    catch { $failedAsExpected = $true }
    if (-not $failedAsExpected -or (Test-Path -LiteralPath $install)) { throw 'Update should reject missing installation.' }

    [IO.Directory]::CreateDirectory($install) | Out-Null
    $settings = Join-Path $install 'private-settings.json'
    [IO.File]::WriteAllText($settings, 'Do not overwrite my config')
    $legacyLauncher = Join-Path $install 'meettape.cmd'
    [IO.File]::WriteAllText($legacyLauncher, 'custom launcher')

    & (Join-Path $repository 'install.ps1') -InstallDirectory $install -SkipPath -HttpClient $client @ffmpegArgs | Out-Null
    $binary = Join-Path $install 'app\MeetTape.exe'
    $marker = Join-Path $install 'installed-version.txt'
    if (-not (Test-Path -LiteralPath $binary) -or (Get-Content -LiteralPath $marker -Raw).Trim() -ne 'v1.2.3') {
        throw 'Initial install failed.'
    }
    if ($handler.Downloads -ne 6 -or $handler.FfmpegDownloads -ne 1) {
        throw 'Initial install must download EXE, three scripts, two legal files and one FFmpeg archive exactly once.'
    }
    foreach ($entry in @(
        @{ Name = 'ffmpeg.exe'; Content = 'Fake FFmpeg executable; not launched' },
        @{ Name = 'ffmpeg-LICENSE.txt'; Content = 'Fixture FFmpeg license' }
    )) {
        if ((Get-Content -LiteralPath (Join-Path $install "app\$($entry.Name)") -Raw) -cne $entry.Content) {
            throw "FFmpeg $($entry.Name) was not extracted alongside MeetTape.exe."
        }
    }
    $ffmpegMarker = Join-Path $install 'app\ffmpeg-version.txt'
    if ((Get-Content -LiteralPath $ffmpegMarker -Raw).Trim() -cne "9.0 $ffmpegHash") {
        throw 'FFmpeg version/hash marker was not written.'
    }
    foreach ($name in @('install.ps1', 'update.ps1', 'InstallerPath.ps1', 'LICENSE', 'THIRD-PARTY-NOTICES.txt')) {
        $installedScript = Join-Path $install "app\$name"
        if (-not (Test-Path -LiteralPath $installedScript) -or
            (Get-FileHash -LiteralPath $installedScript -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $handler.Files[$name] -Algorithm SHA256).Hash) {
            throw "Installed release file is missing or unverified: $name"
        }
    }

    & (Join-Path $repository 'install.ps1') -InstallDirectory $install -SkipPath -HttpClient $client @ffmpegArgs | Out-Null
    if ($handler.Downloads -ne 6 -or $handler.FfmpegDownloads -ne 1) {
        throw 'Unchanged release should not redownload FFmpeg or release files.'
    }

    # Verify that the installed updater uses its adjacent scripts, not the source repository.
    & (Join-Path $install 'app\update.ps1') -SkipPath -HttpClient $client @ffmpegArgs | Out-Null
    if ($handler.Downloads -ne 6 -or $handler.FfmpegDownloads -ne 1) {
        throw 'Installed updater should be a no-op for the current release.'
    }

    $handler.ReleaseTag = 'v1.2.4'
    & (Join-Path $install 'app\update.ps1') -SkipPath -HttpClient $client @ffmpegArgs | Out-Null
    if ((Get-Content -LiteralPath $marker -Raw).Trim() -ne 'v1.2.4' -or
        $handler.Downloads -ne 12 -or $handler.FfmpegDownloads -ne 1) {
        throw 'Update did not replace the previous release.'
    }
    # Repair a same-version script without downloading or replacing the installed executable.
    $installedUpdate = Join-Path $install 'app\update.ps1'
    [IO.File]::WriteAllText($installedUpdate, 'old updater')
    & (Join-Path $repository 'install.ps1') -InstallDirectory $install -SkipPath -HttpClient $client @ffmpegArgs | Out-Null
    if ($handler.Downloads -ne 13 -or
        (Get-FileHash -LiteralPath $installedUpdate -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $handler.Files['update.ps1'] -Algorithm SHA256).Hash) {
        throw 'Installer did not repair a missing or outdated companion script.'
    }
    if ((Get-Content -LiteralPath $settings -Raw) -ne 'Do not overwrite my config' -or
        (Get-Content -LiteralPath $legacyLauncher -Raw) -ne 'custom launcher') {
        throw 'Existing settings or launcher were modified.'
    }

    $handler.ReleaseTag = 'v1.2.5'
    # Make the next release's script genuinely different so a partial update is detectable.
    $futureUpdate = Join-Path $testRoot 'future-update.ps1'
    [IO.File]::WriteAllText($futureUpdate, 'different future updater')
    $handler.Files['update.ps1'] = $futureUpdate
    $scriptHashesBefore = @{}
    foreach ($name in @('install.ps1', 'update.ps1', 'InstallerPath.ps1', 'LICENSE', 'THIRD-PARTY-NOTICES.txt')) {
        $scriptHashesBefore[$name] = (Get-FileHash -LiteralPath (Join-Path $install "app\$name") -Algorithm SHA256).Hash
    }
    $locked = [IO.File]::Open($binary, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        [IO.FileShare]::None)
    $failedAsExpected = $false
    try { & (Join-Path $repository 'update.ps1') -InstallDirectory $install -SkipPath -HttpClient $client @ffmpegArgs | Out-Null }
    catch { $failedAsExpected = $true }
    finally { $locked.Dispose() }
    if (-not $failedAsExpected -or (Get-Content -LiteralPath $marker -Raw).Trim() -ne 'v1.2.4' -or
        (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash -ne $digest -or
        @(Get-ChildItem -LiteralPath $install -Filter '.version-*').Count -ne 0) {
        throw 'Locked executable must reject update without changing binary or version marker.'
    }
    foreach ($name in $scriptHashesBefore.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $install "app\$name") -Algorithm SHA256).Hash -ne
            $scriptHashesBefore[$name]) {
            throw "Locked EXE update changed installed script $name."
        }
    }
    $handler.Files['update.ps1'] = Join-Path $repository 'update.ps1'

    $handler.ReleaseTag = 'v1.2.5'
    $handler.Digest = 'sha256:' + ('0' * 64)
    $failedAsExpected = $false
    try { & (Join-Path $repository 'update.ps1') -InstallDirectory $install -SkipPath -HttpClient $client @ffmpegArgs | Out-Null }
    catch { $failedAsExpected = $true }
    if (-not $failedAsExpected -or (Get-Content -LiteralPath $marker -Raw).Trim() -ne 'v1.2.4') {
        throw 'Bad release checksum must not change the installed version.'
    }
    if (@(Get-ChildItem -LiteralPath $install -Filter '.staging-*').Count -gt 0 -or
        @((Get-ChildItem -LiteralPath (Join-Path $install 'app') -File)).Count -ne 9 -or
        (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash -ne $digest) {
        throw 'Failed download must retain the previous verified EXE and clean temporary files.'
    }

    $fresh = Join-Path $testRoot 'fresh-install'
    $handler.ReleaseTag = 'v1.2.4'
    $handler.Digest = "sha256:$digest"
    & (Join-Path $repository 'install.ps1') -InstallDirectory $fresh -SkipPath -HttpClient $client @ffmpegArgs | Out-Null
    if (Test-Path -LiteralPath (Join-Path $fresh 'meettape.cmd')) {
        throw 'Fresh install must not create a command launcher.'
    }
    foreach ($name in @('install.ps1', 'update.ps1', 'InstallerPath.ps1', 'LICENSE', 'THIRD-PARTY-NOTICES.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $fresh "app\$name"))) {
            throw "Fresh install did not place $name beside MeetTape.exe."
        }
    }
    # An existing unmanaged FFmpeg must never be replaced during initial setup or upgrades.
    $customRoot = Join-Path $testRoot 'custom-ffmpeg-install'
    $customApp = Join-Path $customRoot 'app'
    [IO.Directory]::CreateDirectory($customApp) | Out-Null
    $customBinary = Join-Path $customApp 'ffmpeg.exe'
    [IO.File]::WriteAllText($customBinary, 'Custom user-supplied FFmpeg')
    $ffmpegDownloadsBefore = $handler.FfmpegDownloads
    & (Join-Path $repository 'install.ps1') -InstallDirectory $customRoot -SkipPath -HttpClient $client @ffmpegArgs | Out-Null
    & (Join-Path $repository 'update.ps1') -InstallDirectory $customRoot -SkipPath -HttpClient $client @ffmpegArgs | Out-Null
    if (([IO.File]::ReadAllText($customBinary)) -cne 'Custom user-supplied FFmpeg' -or
        [IO.File]::Exists((Join-Path $customApp 'ffmpeg-version.txt')) -or
        $handler.FfmpegDownloads -ne $ffmpegDownloadsBefore) {
        throw 'Existing unmanaged FFmpeg must be preserved and never marked or downloaded as managed.'
    }
    $env:PATH = "$(Join-Path $fresh 'app');$originalPath"
    Push-Location $testRoot
    try {
        $resolved = (Get-Command MeetTape -CommandType Application | Select-Object -First 1).Source
        if ($resolved -ine (Join-Path $fresh 'app\MeetTape.exe')) {
            throw 'Direct MeetTape command did not resolve to installed EXE.'
        }
        & MeetTape --help | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Direct installed executable fails outside the repository.' }
    } finally {
        Pop-Location
        $env:PATH = $originalPath
    }

    # The mock response stream exercises the production download timeout without network access.
    $handler.ReleaseTag = 'v2.0.0'
    $handler.Stall = $true
    $started = [Diagnostics.Stopwatch]::StartNew()
    $failedAsExpected = $false
    try {
        & (Join-Path $repository 'update.ps1') -InstallDirectory $install -SkipPath -HttpClient $client -DownloadIdleTimeoutSeconds 2 -ProgressIntervalSeconds 1 @ffmpegArgs | Out-Null
    } catch {
        if ($_.Exception.Message -notmatch 'Download stalled for 2 seconds') { throw }
        $failedAsExpected = $true
    }
    if (-not $failedAsExpected -or $started.Elapsed.TotalSeconds -gt 8) {
        throw 'Stalled HTTP download was not terminated promptly.'
    }
    $handler.Stall = $false
    if ((Get-Content -LiteralPath $marker -Raw).Trim() -ne 'v1.2.4' -or
        @(Get-ChildItem -LiteralPath $install -Filter '.staging-*').Count -ne 0) {
        throw 'Stalled download changed the existing installation or left staging files.'
    }
    $handler.MetadataStatus = [Net.HttpStatusCode]::NotFound
    $failedAsExpected = $false
    try { & (Join-Path $repository 'update.ps1') -InstallDirectory $install -SkipPath -HttpClient $client @ffmpegArgs | Out-Null }
    catch {
        if ($_.Exception.Message -notmatch 'Cannot read the public GitHub Release') { throw }
        $failedAsExpected = $true
    }
    if (-not $failedAsExpected) { throw 'Private/unavailable release must fail without authentication.' }
    $handler.MetadataStatus = [Net.HttpStatusCode]::OK
    $handler.InvalidAssetUrl = $true
    $downloadsBefore = $handler.Downloads
    $failedAsExpected = $false
    try { & (Join-Path $repository 'update.ps1') -InstallDirectory $install -SkipPath -HttpClient $client @ffmpegArgs | Out-Null }
    catch { $failedAsExpected = $true }
    if (-not $failedAsExpected -or $handler.Downloads -ne $downloadsBefore) {
        throw 'Unexpected asset URL must be rejected before downloading.'
    }
    if ((Get-Content -LiteralPath $marker -Raw).Trim() -ne 'v1.2.4' -or
        (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash -ne $digest -or
        (Get-Content -LiteralPath $settings -Raw) -ne 'Do not overwrite my config') {
        throw 'HTTP failures changed installed files or user settings.'
    }

    Write-Output 'Installer tests passed: direct EXE launch, updater failure diagnostics, adjacent scripts, script repair, PATH migration, FFmpeg ZIP/license and unmanaged binary preservation, install/update/no-op, locked EXE, checksum failure, anonymous HTTP, private-release failure, URL validation, download timeout.'
} finally {
    $env:PATH = $originalPath
    $env:TEMP = $originalTemp
    $env:TMP = $originalTmp
    if (Get-Variable client -ErrorAction SilentlyContinue) { $client.Dispose() }
    if ([IO.Path]::GetFullPath($testRoot).StartsWith(
        [IO.Path]::GetFullPath($testParent).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $testRoot) -like 'MeetTapeInstallerTests-*') {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
