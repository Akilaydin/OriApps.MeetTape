function Get-MeetTapeUserPath([string]$currentPath, [string]$installRoot) {
    $root = [IO.Path]::GetFullPath($installRoot).TrimEnd('\', '/')
    $app = Join-Path $root 'app'
    $entries = [Collections.Generic.List[string]]::new()
    $appPresent = $false

    foreach ($entry in ($currentPath -split ';')) {
        if ([string]::IsNullOrWhiteSpace($entry)) { continue }
        $candidate = [Environment]::ExpandEnvironmentVariables($entry.Trim().Trim('"')).TrimEnd('\', '/')
        if ($candidate -ieq $root) {
            # Keep command resolution priority by placing the EXE where the old launcher used to be.
            if (-not $appPresent) { $entries.Add($app); $appPresent = $true }
            continue
        }
        if ($candidate -ieq $app) {
            if (-not $appPresent) { $entries.Add($entry) }
            $appPresent = $true
            continue
        }
        $entries.Add($entry)
    }
    if (-not $appPresent) { $entries.Add($app) }
    return [string]::Join(';', $entries)
}

function Remove-MeetTapeLegacyLauncher([string]$launcher) {
    if (-not [IO.File]::Exists($launcher)) { return $false }
    [IO.File]::Delete($launcher)
    return $true
}
