$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Compare-Audio.ps1')
$body = New-ComparisonBody 'test-model' 'test-prompt' ([byte[]]@(1,2,3))
$child = & pwsh.exe -NoProfile -Command ". '$PSScriptRoot/Compare-Audio.ps1'; New-ComparisonBody 'test-model' 'test-prompt' ([byte[]]@(1,2,3))"
if ($LASTEXITCODE -ne 0 -or $body -cne $child) { throw 'Request fingerprint changes between processes' }
$parsed = $body | ConvertFrom-Json
if ($parsed.model -ne 'test-model' -or $parsed.stream -ne $false -or
    $parsed.messages[0].content[1].input_audio.data -ne 'AQID' -or
    $parsed.messages[0].content[1].input_audio.format -ne 'mp3') { throw 'Invalid input_audio request' }
$valid = '{"choices":[{"finish_reason":"stop","message":{"content":"[00:01] Me: test"}}]}'
if ((Get-ComparisonText $valid) -ne '[00:01] Me: test') { throw 'Valid response rejected' }
$invalid = @('invalid JSON','{}','{"choices":[]}',
    '{"choices":[{"finish_reason":"length","message":{"content":"partial"}}]}',
    '{"choices":[{"finish_reason":"stop","message":{"content":" "}}]}',
    '{"choices":[{"finish_reason":"stop","message":{"content":null}}]}',
    '{"choices":[{"finish_reason":null,"message":{"content":"test"}}]}')
foreach ($raw in $invalid) {
    $rejected = $false
    try { [void](Get-ComparisonText $raw) } catch { $rejected = $true }
    if (-not $rejected) { throw 'Invalid/incomplete response accepted' }
}
$temporary = Join-Path $env:TEMP ('MeetTape-PocTest-' + [Guid]::NewGuid().ToString('N') + '.json')
try {
    Write-ComparisonFile $temporary 'first'
    $rejected = $false
    try { Write-ComparisonFile $temporary 'second' } catch { $rejected = $true }
    if (-not $rejected -or [IO.File]::ReadAllText($temporary) -ne 'first') { throw 'Existing result overwritten' }
} finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
. (Join-Path $PSScriptRoot 'Measure-Echo.ps1') -DefinitionsOnly
$random = [Random]::new(123)
$them = [float[]]::new(4000)
$me = [float[]]::new(4000)
for ($i = 0; $i -lt 4000; $i++) { $them[$i] = [float](($random.NextDouble() - 0.5) * 0.2) }
for ($i = 40; $i -lt 4000; $i++) { $me[$i] = $them[$i - 40] * 0.25 }
$mePath = Join-Path $env:TEMP ('MeetTape-EchoTest-' + [Guid]::NewGuid().ToString('N') + '.f32')
$themPath = Join-Path $env:TEMP ('MeetTape-EchoTest-' + [Guid]::NewGuid().ToString('N') + '.f32')
try {
    $bytes = [byte[]]::new(16000)
    [Buffer]::BlockCopy($me,0,$bytes,0,16000); [IO.File]::WriteAllBytes($mePath,$bytes)
    [Buffer]::BlockCopy($them,0,$bytes,0,16000); [IO.File]::WriteAllBytes($themPath,$bytes)
    $metrics = [SampleEchoMetrics]::Analyze($mePath,$themPath)
    if ($metrics.peakCorrelation -lt 0.999 -or $metrics.lagMilliseconds -ne 20 -or
        [Math]::Abs($metrics.linearProjectionGain - 0.25) -gt 0.0001) { throw 'Known echo delay/gain not recovered' }
} finally {
    if (Test-Path -LiteralPath $mePath) { Remove-Item -LiteralPath $mePath }
    if (Test-Path -LiteralPath $themPath) { Remove-Item -LiteralPath $themPath }
}
Write-Output '12 experiment checks passed: stable request JSON, input_audio, API validation, refusing overwrite and known echo delay/gain.'
