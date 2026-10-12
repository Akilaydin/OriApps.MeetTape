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

function Write-MeetTapeLaunchers([string]$appDirectory, [string]$shortcutPath) {
    $executable = Join-Path $appDirectory 'MeetTape.exe'
    $consoleLauncher = Join-Path $appDirectory 'meettape-cli.cmd'
    # A batch invocation waits for WinExe; PowerShell's direct GUI invocation may return early.
    # Never overwrite a pre-existing custom launcher.
    if (-not [IO.File]::Exists($consoleLauncher)) {
        [IO.File]::WriteAllText($consoleLauncher, "@echo off`r`n`"%~dp0MeetTape.exe`" %*`r`nexit /b %errorlevel%`r`n", [Text.Encoding]::ASCII)
    }
    [IO.Directory]::CreateDirectory((Split-Path -Parent $shortcutPath)) | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $null
    try {
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = $executable
        $shortcut.WorkingDirectory = $appDirectory
        $shortcut.Description = 'MeetTape meeting recorder'
        $shortcut.Save()
    } finally {
        if ($shortcut) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) }
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
    }
}
