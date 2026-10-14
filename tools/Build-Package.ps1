param(
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$VpkPath = 'vpk',
    [string]$OutputDirectory = 'dist/releases'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $repository "dist/publish/$Version"
$output = [IO.Path]::GetFullPath((Join-Path $repository $OutputDirectory))
if ([IO.Directory]::Exists($publish)) { throw "Publish directory already exists: $publish. Use a fresh version/directory." }
if ([IO.File]::Exists((Join-Path $output "OriApps.MeetTape-$Version-full.nupkg"))) { throw 'Package version already exists; refusing to replace it.' }

$env:AVALONIA_TELEMETRY_OPTOUT = '1'
dotnet publish (Join-Path $repository 'src/MeetTape/MeetTape.csproj') --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -p:RuntimeFrameworkVersion=10.0.12 "-p:Version=$Version" --output $publish
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

& (Join-Path $PSScriptRoot 'Get-FFmpeg.ps1')
Copy-Item -LiteralPath (Join-Path $repository 'dist/ffmpeg/ffmpeg.exe'), (Join-Path $repository 'dist/ffmpeg/ffmpeg-LICENSE.txt') -Destination $publish

Copy-Item -LiteralPath (Join-Path $repository 'LICENSE'), (Join-Path $repository 'THIRD-PARTY-NOTICES.txt') -Destination $publish
& (Join-Path $publish 'ffmpeg.exe') -hide_banner -version | Select-Object -First 1
if ($LASTEXITCODE -ne 0) { throw 'Bundled FFmpeg cannot start.' }
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Test-Desktop.ps1') -ExecutablePath (Join-Path $publish 'MeetTape.exe') -SkipNativeWindow
if ($LASTEXITCODE -ne 0) { throw 'Published GUI startup checks failed.' }

# Full packages are the supported fallback. No previous Release is mutated or needed.
& $VpkPath pack --packId OriApps.MeetTape --packVersion $Version --packDir $publish --mainExe MeetTape.exe --packTitle MeetTape --packAuthors OriApps --icon (Join-Path $repository 'assets/MeetTape.ico') --runtime win-x64 --channel win --shortcuts StartMenuRoot --delta None --noPortable --outputDir $output
if ($LASTEXITCODE -ne 0) { throw 'Velopack packaging failed.' }
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Test-Desktop.ps1') -ExecutablePath (Join-Path $output 'OriApps.MeetTape-win-Setup.exe') -IconOnly
if ($LASTEXITCODE -ne 0) { throw 'Setup icon checks failed.' }
foreach ($name in @('OriApps.MeetTape-win-Setup.exe', "OriApps.MeetTape-$Version-full.nupkg", 'releases.win.json')) {
    if (-not [IO.File]::Exists((Join-Path $output $name))) { throw "Missing Velopack asset: $name." }
}
$feed = Get-Content -LiteralPath (Join-Path $output 'releases.win.json') -Raw | ConvertFrom-Json
$release = @($feed.Assets | Where-Object { $_.Version -eq $Version -and $_.Type -eq 'Full' })
if ($release.Count -ne 1 -or $release[0].PackageId -cne 'OriApps.MeetTape') { throw 'Invalid Velopack feed identity/version.' }
$package = Join-Path $output $release[0].FileName
if ((Get-Item -LiteralPath $package).Length -ne $release[0].Size -or (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ine $release[0].SHA256) { throw 'Velopack feed hash/size mismatch.' }
Write-Output "Velopack ${Version}: Setup, full package and verified win feed ready in $output."
