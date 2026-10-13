param(
    [ValidateSet('Prepare','Transcribe')][string]$Mode = 'Prepare',
    [string]$Config = 'config.json',
    [string]$SessionDirectory,
    [string]$RunDirectory,
    [switch]$UploadApproved
)

# Isolated audio comparison, limited to three-minute samples.
function Assert-NoCapture([string]$Root) {
    foreach ($directory in Get-ChildItem -LiteralPath $Root -Directory -ErrorAction SilentlyContinue) {
        $metadata = Join-Path $directory.FullName 'session.json'
        if ((Test-Path -LiteralPath $metadata) -and
            (Get-Content -LiteralPath $metadata -Raw | ConvertFrom-Json).recordingState -eq 'recording') {
            throw 'Capture active; experiment canceled.'
        }
    }
}

function Invoke-ComparisonFfmpeg([string]$Executable, [string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        [void]$process.Start()
        $errors = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) { $process.Kill($true); throw 'FFmpeg experiment timed out.' }
        [void]$errors.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw 'FFmpeg experiment failed; originals retained.' }
    } finally { $process.Dispose() }
}

function Get-ComparisonText([string]$Raw) {
    try { $response = ConvertFrom-Json -InputObject $Raw -ErrorAction Stop }
    catch { throw 'Malformed API JSON; result not accepted.' }
    if (-not $response.choices -or $response.choices[0].finish_reason -cne 'stop' -or
        $response.choices[0].message.content -isnot [string] -or
        [string]::IsNullOrWhiteSpace($response.choices[0].message.content)) {
        throw 'Missing, empty or incomplete API result; not accepted.'
    }
    return $response.choices[0].message.content
}

function Write-ComparisonFile([string]$Path, [string]$Text) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Text)
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes); $stream.Flush($true) } finally { $stream.Dispose() }
}

function New-ComparisonBody([string]$Model, [string]$Prompt, [byte[]]$Audio) {
    [ordered]@{ model=$Model; stream=$false; messages=@([ordered]@{ role='user'; content=@(
        [ordered]@{ type='text'; text=$Prompt },
        [ordered]@{ type='input_audio'; input_audio=[ordered]@{ data=[Convert]::ToBase64String($Audio); format='mp3' } }
    )}) } | ConvertTo-Json -Depth 10 -Compress
}

if ($MyInvocation.InvocationName -eq '.') { return }
$ErrorActionPreference = 'Stop'
try {
    $configPath = [IO.Path]::GetFullPath($Config)
    $settings = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    $configurationDirectory = [IO.Path]::GetDirectoryName($configPath)
    $root = [IO.Path]::GetFullPath($settings.meetingsRoot, $configurationDirectory)
    Assert-NoCapture $root
    if ($Mode -eq 'Prepare') {
        $session = [IO.Path]::GetFullPath($SessionDirectory)
        $data = Get-Content -LiteralPath (Join-Path $session 'session.json') -Raw | ConvertFrom-Json
        if ($data.recordingState -ne 'recorded' -or $data.durationSeconds -le 0 -or $data.durationSeconds -gt 180) {
            throw 'Experiment requires a successfully recorded sample of at most 180 seconds.'
        }
        $run = Join-Path $session ('processed/experiment_' + [Guid]::NewGuid().ToString('N'))
        [void][IO.Directory]::CreateDirectory($run)
        $me = Join-Path $session 'me.mp3'; $them = Join-Path $session 'them.mp3'
        $sourceHashes = @{ me = (Get-FileHash -LiteralPath $me).Hash; them = (Get-FileHash -LiteralPath $them).Hash }
        $common = @('-hide_banner','-v','error','-nostdin','-n','-i',$me,'-i',$them)
        Invoke-ComparisonFfmpeg $settings.capture.ffmpegPath ($common + @('-filter_complex',
            '[0:a][1:a]amix=inputs=2:duration=longest:dropout_transition=0:normalize=1',
            '-ac','1','-ar','48000','-c:a','libmp3lame','-b:a','96k','-write_xing','1',(Join-Path $run 'mono.mp3')))
        Invoke-ComparisonFfmpeg $settings.capture.ffmpegPath ($common + @('-filter_complex',
            '[0:a][1:a]join=inputs=2:channel_layout=stereo:map=0.0-FL|1.0-FR',
            '-ac','2','-ar','48000','-c:a','libmp3lame','-b:a','192k','-write_xing','1',(Join-Path $run 'stereo.mp3')))
        $manifest = @{ sessionDirectory=$session; sessionId=$data.id; durationSeconds=$data.durationSeconds;
            sourceHashes=$sourceHashes; model=$settings.transcription.model; createdUtc=[DateTimeOffset]::UtcNow.ToString('O') }
        Write-ComparisonFile (Join-Path $run 'manifest.json') ($manifest | ConvertTo-Json -Depth 5)
        [PSCustomObject]@{ RunDirectory=$run; Duration=$data.durationSeconds; MonoBytes=(Get-Item -LiteralPath (Join-Path $run 'mono.mp3')).Length;
            StereoBytes=(Get-Item -LiteralPath (Join-Path $run 'stereo.mp3')).Length } | ConvertTo-Json
        exit 0
    }
    if (-not $UploadApproved) { throw 'Explicit upload approval required; no API request made.' }
    $run = [IO.Path]::GetFullPath($RunDirectory)
    $manifest = Get-Content -LiteralPath (Join-Path $run 'manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.durationSeconds -le 0 -or $manifest.durationSeconds -gt 180) { throw 'Invalid experiment duration.' }
    $session = $manifest.sessionDirectory
    foreach ($source in @('me','them')) {
        if ((Get-FileHash -LiteralPath (Join-Path $session "$source.mp3")).Hash -ne $manifest.sourceHashes.$source) {
            throw 'Original source hash changed; experiment stopped.'
        }
    }
    $uri = [Uri]$settings.transcription.endpoint
    if ($uri.Scheme -ne 'https' -and -not ($uri.Scheme -eq 'http' -and $uri.IsLoopback) -or
        $uri.UserInfo -or $uri.Query -or $uri.Fragment) { throw 'Invalid API endpoint.' }
    $key = [Environment]::GetEnvironmentVariable($settings.transcription.apiKeyEnvironmentVariable)
    if ([string]::IsNullOrWhiteSpace($key)) { $key = [Environment]::GetEnvironmentVariable($settings.transcription.apiKeyEnvironmentVariable, 'User') }
    if ([string]::IsNullOrWhiteSpace($key)) { throw 'API key unavailable; no request made.' }
    $basePrompt = [IO.File]::ReadAllText([IO.Path]::GetFullPath($settings.transcription.promptFile, $configurationDirectory))
    $formatPrompt = ' Each utterance on its own line: [MM:SS] Me/Them/Unknown: text. Timestamps are relative to the start of this audio. Do not invent names; use Unknown if the source is unclear.'
    $variants = @(
        @{ Name='mono'; Path=(Join-Path $run 'mono.mp3'); Hint=' A mono mix of the Me microphone and Them system playback.' },
        @{ Name='stereo'; Path=(Join-Path $run 'stereo.mp3'); Hint=' Stereo: left is the Me microphone, right is Them system playback. Me may contain acoustic echoes of Them; output identical speech only once.' },
        @{ Name='me'; Path=(Join-Path $session 'me.mp3'); Hint=' Separate Me microphone track. It may contain speaker echoes; transcribe all audible speech, including quiet speech.' },
        @{ Name='them'; Path=(Join-Path $session 'them.mp3'); Hint=' Separate Them system playback track.' }
    )
    $client = [Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromMinutes(5)
    try {
        foreach ($variant in $variants) {
            Assert-NoCapture $root
            $prompt = $basePrompt + $formatPrompt + $variant.Hint
            $audio = [IO.File]::ReadAllBytes($variant.Path)
            if ($audio.Length -gt 8MB) { throw 'Experiment audio exceeds the bounded sample limit.' }
            $body = New-ComparisonBody $settings.transcription.model $prompt $audio
            $fingerprint = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($uri.AbsoluteUri + $body)))
            $responsePath = Join-Path $run ($variant.Name + '.response.json')
            $stampPath = Join-Path $run ($variant.Name + '.request.sha256')
            if (Test-Path -LiteralPath $responsePath) {
                if (-not (Test-Path -LiteralPath $stampPath) -or [IO.File]::ReadAllText($stampPath) -ne $fingerprint) {
                    throw 'Existing response has a different request fingerprint; refusing overwrite.'
                }
                [void](Get-ComparisonText ([IO.File]::ReadAllText($responsePath)))
                Write-Output "$($variant.Name): cached successful response; not sent again."
                continue
            }
            $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, $uri)
            $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $key)
            $request.Content = [Net.Http.StringContent]::new($body, [Text.Encoding]::UTF8, 'application/json')
            $cancellation = [Threading.CancellationTokenSource]::new()
            $response = $null
            try {
                Write-Output "$($variant.Name): sending approved audio ($($audio.Length) bytes)."
                $task = $client.SendAsync($request, $cancellation.Token)
                while (-not $task.IsCompleted) {
                    try { Assert-NoCapture $root } catch { $cancellation.Cancel(); throw }
                    Start-Sleep -Milliseconds 250
                }
                $response = $task.GetAwaiter().GetResult()
                if (-not $response.IsSuccessStatusCode) {
                    [Console]::Error.WriteLine("API HTTP $([int]$response.StatusCode); experiment paused.")
                    throw 'API request failed.'
                }
                $raw = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                $text = Get-ComparisonText $raw
                Write-ComparisonFile $responsePath $raw
                Write-ComparisonFile $stampPath $fingerprint
                Write-ComparisonFile (Join-Path $run ($variant.Name + '.md')) $text
                Write-Output "$($variant.Name): stop/nonempty accepted; saved locally, content not logged."
            } finally {
                if ($null -ne $response) { $response.Dispose() }
                $request.Dispose(); $cancellation.Dispose()
            }
        }
    } finally { $client.Dispose(); $key=$null }
    foreach ($source in @('me','them')) {
        if ((Get-FileHash -LiteralPath (Join-Path $session "$source.mp3")).Hash -ne $manifest.sourceHashes.$source) {
            throw 'Original hash changed during experiment.'
        }
    }
    Write-Output 'All four responses saved. Original hashes unchanged. No production job marked complete.'
} catch {
    # Only our fixed diagnostics are printed; raw provider bodies, prompts, transcripts and credentials stay local.
    [Console]::Error.WriteLine('Audio comparison stopped: ' + $_.Exception.GetType().Name + '; script line ' + $_.InvocationInfo.ScriptLineNumber + '. Check configuration, capture state, inputs, response validity and filesystem. No incomplete result accepted.')
    exit 2
}
