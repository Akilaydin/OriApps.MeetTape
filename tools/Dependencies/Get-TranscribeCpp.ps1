$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$source = Join-Path $repository 'dist/native/transcribe.cpp'
$build = Join-Path $repository 'dist/native/transcribe.cpp/build'
$output = Join-Path $repository 'dist/local-asr'
$commit = '4d7c1ccefd39e5a9ea36cca5f8fefc6dcc14b99b' # transcribe.cpp v0.4.1
$executable = Join-Path $build 'bin/Release/transcribe-cli.exe'

if (-not (Test-Path -LiteralPath $source)) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $source) -Force | Out-Null
    git clone --depth 1 --branch v0.4.1 'https://github.com/handy-computer/transcribe.cpp.git' $source
    if ($LASTEXITCODE -ne 0) { throw 'Cannot retrieve the pinned transcribe.cpp source.' }
}
$actual = (& git -C $source rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actual -cne $commit) { throw 'transcribe.cpp source revision does not match the pinned version.' }

if (-not (Test-Path -LiteralPath $executable)) {
    cmake -S $source -B $build -DGGML_NATIVE=OFF -DTRANSCRIBE_USE_OPENMP=OFF -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded
    if ($LASTEXITCODE -ne 0) { throw 'Cannot configure transcribe.cpp.' }
    cmake --build $build --target transcribe-cli --config Release --parallel 8
    if ($LASTEXITCODE -ne 0) { throw 'Cannot compile transcribe.cpp CLI.' }
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
Copy-Item -LiteralPath $executable -Destination (Join-Path $output 'transcribe-cli.exe')
$licenses = @(
    @('LICENSE', 'transcribe-LICENSE.txt'),
    @('ggml/LICENSE', 'ggml-LICENSE.txt'),
    @('src/third_party/miniz/LICENSE', 'miniz-LICENSE.txt'),
    @('src/third_party/silero_vad/LICENSE', 'silero-vad-LICENSE.txt')
)
foreach ($pair in $licenses) {
    $origin = Join-Path $source $pair[0]
    if (-not (Test-Path -LiteralPath $origin)) { throw "Missing native license: $($pair[0])" }
    Copy-Item -LiteralPath $origin -Destination (Join-Path $output $pair[1])
}
& (Join-Path $output 'transcribe-cli.exe') --help *> $null
if ($LASTEXITCODE -ne 0) { throw 'transcribe.cpp CLI failed startup verification.' }
Write-Output 'Pinned transcribe.cpp CLI (CPU) and its licenses are ready.'
