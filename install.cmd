@echo off
setlocal
set "MEETTAPE_BOOTSTRAP_FILE=%~f0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$text = [IO.File]::ReadAllText($env:MEETTAPE_BOOTSTRAP_FILE); $marker = ':__MEETTAPE_POWERSHELL__'; $start = $text.LastIndexOf($marker); if ($start -lt 0) { throw 'Invalid MeetTape installer.' }; & ([scriptblock]::Create($text.Substring($start + $marker.Length)))"
set "MEETTAPE_INSTALL_EXIT=%ERRORLEVEL%"
if not "%MEETTAPE_INSTALL_EXIT%"=="0" pause
exit /b %MEETTAPE_INSTALL_EXIT%
:__MEETTAPE_POWERSHELL__
$ErrorActionPreference = 'Stop'
$repository = 'Akilaydin/OriApps.MeetTape'
$downloadRoot = Join-Path ([IO.Path]::GetTempPath()) ('MeetTapeBootstrap-' + [guid]::NewGuid().ToString('N'))
$client = $null
try {
    if (-not [Environment]::Is64BitOperatingSystem) { throw 'MeetTape requires Windows x64.' }
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Add-Type -AssemblyName System.Net.Http
    $client = [Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds(60)
    $client.DefaultRequestHeaders.Add('User-Agent', 'MeetTape-Installer')
    $release = $client.GetStringAsync("https://api.github.com/repos/$repository/releases/latest").GetAwaiter().GetResult() | ConvertFrom-Json
    $tag = [string]$release.tag_name
    if ($release.draft -or $tag -notmatch '^v\d+\.\d+\.\d+$') { throw 'No published MeetTape release found.' }

    [IO.Directory]::CreateDirectory($downloadRoot) | Out-Null
    foreach ($name in @('install.ps1', 'InstallerPath.ps1')) {
        $matching = @($release.assets | Where-Object { $_.name -ceq $name })
        if ($matching.Count -ne 1) { throw "Missing Release asset: $name" }
        $asset = $matching[0]
        if ($asset.digest -notmatch '^sha256:[0-9a-fA-F]{64}$' -or
            $asset.browser_download_url -cne "https://github.com/$repository/releases/download/$tag/$name" -or
            $asset.size -le 0 -or $asset.size -gt 262144) {
            throw "Invalid Release asset metadata: $name"
        }
        $destination = Join-Path $downloadRoot $name
        $contents = $client.GetByteArrayAsync([string]$asset.browser_download_url).GetAwaiter().GetResult()
        if ($contents.Length -ne $asset.size) { throw "Invalid downloaded size: $name" }
        [IO.File]::WriteAllBytes($destination, $contents)
        $sha256 = [Security.Cryptography.SHA256]::Create()
        $stream = $null
        try {
            $stream = [IO.File]::OpenRead($destination)
            $actualHash = [BitConverter]::ToString($sha256.ComputeHash($stream)).Replace('-', '')
        } finally {
            if ($stream) { $stream.Dispose() }
            $sha256.Dispose()
        }
        if ($actualHash -ine $asset.digest.Substring(7)) {
            throw "Invalid downloaded SHA-256: $name"
        }
    }
    & (Join-Path $downloadRoot 'install.ps1')
    if (-not $?) { throw 'MeetTape installation failed.' }
}
catch {
    Write-Host "[MeetTape] Installation failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    if ($client) { $client.Dispose() }
    if ([IO.Directory]::Exists($downloadRoot) -and
        [IO.Path]::GetFullPath($downloadRoot).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $downloadRoot) -like 'MeetTapeBootstrap-*') {
        [IO.Directory]::Delete($downloadRoot, $true)
    }
}
