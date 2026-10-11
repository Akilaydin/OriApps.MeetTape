$ErrorActionPreference = 'Stop'
$source = Join-Path (Split-Path -Parent $PSScriptRoot) 'install.cmd'
$content = [IO.File]::ReadAllText($source)
$marker = ':__MEETTAPE_POWERSHELL__'
$start = $content.LastIndexOf($marker, [StringComparison]::Ordinal)
if ($start -lt 0) { throw 'Installer bootstrap marker missing.' }
$header = $content.Substring(0, $start + $marker.Length)
$script = $content.Substring($start + $marker.Length)
$tokens = $null
$parseErrors = $null
$null = [Management.Automation.Language.Parser]::ParseInput($script, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors) { throw "Installer PowerShell syntax invalid: $($parseErrors[0].Message)" }

$testRoot = Join-Path (Split-Path -Parent $PSScriptRoot) ('dist/bootstrap-test-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
try {
    $mock = Join-Path $testRoot 'install.cmd'
    [IO.File]::WriteAllText($mock, $header + [Environment]::NewLine + "Write-Output 'BOOTSTRAP_OK'", [Text.Encoding]::ASCII)
    $output = & $mock 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or $output -notmatch 'BOOTSTRAP_OK') {
        throw 'The CMD launcher did not execute its embedded PowerShell under Windows PowerShell.'
    }
    Write-Output 'One-click CMD bootstrap syntax and PowerShell handoff passed.'
}
finally {
    $base = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\dist')).TrimEnd('\', '/')
    if ([IO.Path]::GetFullPath($testRoot).StartsWith($base + '\', [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $testRoot) -like 'bootstrap-test-*') {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
