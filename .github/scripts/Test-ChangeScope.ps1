$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$originalOutput = $env:GITHUB_OUTPUT
$env:GITHUB_OUTPUT = $null
$global:changeScopeTestExitCode = 0
function git {
    $global:LASTEXITCODE = $global:changeScopeTestExitCode
    $global:changeScopeTestPaths
}
$cases = @(
    @{ Paths = @('.mailmap'); Software = 'false'; Package = 'false' },
    @{ Paths = @('.mailmap', 'src/MeetTape/TranscriptAssembler.cs'); Software = 'true'; Package = 'false' },
    @{ Paths = @('README.md', 'ARCHITECTURE.md'); Software = 'false'; Package = 'false' },
    @{ Paths = @('src/MeetTape/TranscriptAssembler.cs'); Software = 'true'; Package = 'false' },
    @{ Paths = @('tests/MeetTape.Tests/JobTests.cs'); Software = 'true'; Package = 'false' },
    @{ Paths = @('prompts/transcription.md'); Software = 'true'; Package = 'false' },
    @{ Paths = @('README.md', 'src/MeetTape/AppUpdates.cs'); Software = 'true'; Package = 'true' },
    @{ Paths = @('src/MeetTape/App.cs'); Software = 'true'; Package = 'true' },
    @{ Paths = @('src/MeetTape/MainViewModel.cs'); Software = 'true'; Package = 'true' },
    @{ Paths = @('src/MeetTape/DesktopController.cs'); Software = 'true'; Package = 'true' },
    @{ Paths = @('tools/Test-Package.ps1'); Software = 'true'; Package = 'true' },
    @{ Paths = @('.github/workflows/ci.yml'); Software = 'true'; Package = 'true' },
    @{ Paths = @('src/MeetTape/MeetTape.csproj'); Software = 'true'; Package = 'true' },
    @{ Paths = @('assets/MeetTape.ico'); Software = 'true'; Package = 'true' },
    @{ Paths = @('src/MeetTape/FutureNative.cs'); Software = 'true'; Package = 'true' },
    @{ Paths = @('future/unknown.md'); Software = 'true'; Package = 'true' }
)
try {
    foreach ($case in $cases) {
        $global:changeScopeTestPaths = $case.Paths
        $result = @(& (Join-Path $PSScriptRoot 'Get-ChangeScope.ps1') -BaseSha base -HeadSha head)
        if ($result -cnotcontains "software=$($case.Software)" -or $result -cnotcontains "package=$($case.Package)") {
            throw "Wrong checks for $($case.Paths -join ', ')."
        }
    }
    $global:changeScopeTestPaths = @('README.md', '.mailmap')
    $result = @(& (Join-Path $PSScriptRoot 'Get-ChangeScope.ps1') -BaseSha base -HeadSha head -TargetBranch release)
    if ($result -cnotcontains 'software=true' -or $result -cnotcontains 'package=true') { throw 'Release promotion skipped full checks.' }
    $global:changeScopeTestExitCode = 1
    $failed = $false
    try { & (Join-Path $PSScriptRoot 'Get-ChangeScope.ps1') -BaseSha base -HeadSha head }
    catch { $failed = $true }
    if (-not $failed) { throw 'Failed diff did not stop classification.' }
    Write-Output 'Change-scope gates: documentation, managed logic, native/package changes, unknown paths, release and diff failures passed.'
} finally {
    $env:GITHUB_OUTPUT = $originalOutput
}
