param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [switch]$SkipNativeWindow
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = Split-Path -Parent $PSScriptRoot
$executable = if ([IO.Path]::IsPathRooted($ExecutablePath)) { [IO.Path]::GetFullPath($ExecutablePath) }
    else { [IO.Path]::GetFullPath((Join-Path $PWD $ExecutablePath)) }
if (-not [IO.File]::Exists($executable)) { throw 'Published executable not found.' }
$testRoot = Join-Path $repository ('dist\desktop-tests\' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$env:TEMP = $testRoot
$env:TMP = $testRoot
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $testRoot 'native-runtime'

function Invoke-Published([string[]]$Arguments, [int]$ExpectedCode, [string]$ExpectedOutput) {
    $info = [Diagnostics.ProcessStartInfo]::new($executable)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.RedirectStandardInput = $true
    $info.WorkingDirectory = $testRoot
    $info.Arguments = ($Arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) -join ' '
    $process = [Diagnostics.Process]::Start($info)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(20000)) { throw 'Published CLI did not exit within 20 seconds.' }
        $text = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne $ExpectedCode -or $text -notmatch [regex]::Escape($ExpectedOutput)) {
            throw "Published CLI contract failed for $($Arguments -join ' '): code=$($process.ExitCode), output=$text"
        }
    } finally {
        if (-not $process.HasExited) { $process.Kill(); [void]$process.WaitForExit(5000) }
        $process.Dispose()
    }
}

Invoke-Published @('--help') 0 'Avalonia UI'
Invoke-Published @('--config', (Join-Path $testRoot 'missing.json'), '--help') 0 'Avalonia UI'
Invoke-Published @('configure', '--help') 0 'graphical settings'
Invoke-Published @('update', '--help') 0 'Stop recording first'
Invoke-Published @('--config') 2 'Missing --config path'
Invoke-Published @('start') 2 'GUI action'
$config = Get-Content -LiteralPath (Join-Path $repository 'config.example.json') -Raw | ConvertFrom-Json
$config.meetingsRoot = Join-Path $testRoot 'Meetings'
$config.stages.trim = $false; $config.stages.transcribe = $false; $config.stages.summary = $false
$configPath = Join-Path $testRoot 'config.json'
[IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
Invoke-Published @('--config', $configPath, 'sessions') 0 'No sessions yet'
Invoke-Published @('--config', $configPath, 'check-config') 0 'No API request made'
Invoke-Published @('--config', $configPath, 'status') 0 'cannot inspect'
Write-Output 'Published WinExe: help, stdout/stderr, exit codes, --config and local diagnostics passed.'

if (-not $SkipNativeWindow) {
    Add-Type -AssemblyName System.Drawing
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class MeetTapeWindowProbe {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
}
'@
    [void][MeetTapeWindowProbe]::SetProcessDPIAware()
    # --ui-smoke bypasses all real configuration, capture, API, and release checks.
    $smoke = Start-Process -FilePath $executable -ArgumentList '--ui-smoke' -PassThru -WindowStyle Hidden
    try {
        $handle = [IntPtr]::Zero
        for ($i = 0; $i -lt 80 -and $handle -eq [IntPtr]::Zero; $i++) {
            Start-Sleep -Milliseconds 50
            $smoke.Refresh(); $handle = $smoke.MainWindowHandle
        }
        if ($handle -eq [IntPtr]::Zero -or -not [MeetTapeWindowProbe]::IsWindowVisible($handle)) { throw 'Native Avalonia window did not appear.' }
        [void][MeetTapeWindowProbe]::SendMessageW($handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 100
        if ($smoke.HasExited -or [MeetTapeWindowProbe]::IsWindowVisible($handle)) { throw 'Window close must hide while the process/tray stays alive.' }
        for ($i = 0; $i -lt 100 -and -not [MeetTapeWindowProbe]::IsWindowVisible($handle); $i++) { Start-Sleep -Milliseconds 50 }
        if (-not [MeetTapeWindowProbe]::IsWindowVisible($handle)) { throw 'Hidden window did not reopen.' }
        $rectangle = [MeetTapeWindowProbe+Rect]::new()
        [void][MeetTapeWindowProbe]::GetWindowRect($handle, [ref]$rectangle)
        $bitmap = [Drawing.Bitmap]::new(($rectangle.Right - $rectangle.Left), ($rectangle.Bottom - $rectangle.Top))
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $dc = $graphics.GetHdc()
        try { [void][MeetTapeWindowProbe]::PrintWindow($handle, $dc, 2) }
        finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
        $bitmap.Save((Join-Path $testRoot 'native-window.png'), [Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
        if (-not $smoke.WaitForExit(15000)) { throw 'Explicit native shutdown did not finish.' }
        $smoke.Refresh()
        if ($smoke.ExitCode -ne 0) { throw 'Native smoke exited with failure.' }
        Write-Output "Native Windows window: creation, hide, reopen and Exit passed. Screenshot: $testRoot\native-window.png"
        Write-Output 'TrayIcon created by native host; shell icon/menu clicks remain a manual check.'
    } finally {
        if (-not $smoke.HasExited) { $smoke.Kill(); [void]$smoke.WaitForExit(5000) }
        $smoke.Dispose()
    }

    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    $gui = Start-Process -FilePath $executable -ArgumentList @('--config', ('"' + $configPath + '"')) -PassThru -WindowStyle Hidden
    try {
        $root = $null; $ready = $null
        for ($i = 0; $i -lt 100 -and $null -eq $ready; $i++) {
            Start-Sleep -Milliseconds 50
            $gui.Refresh()
            if ($gui.MainWindowHandle -eq [IntPtr]::Zero) { continue }
            $root = [Windows.Automation.AutomationElement]::FromHandle($gui.MainWindowHandle)
            $ready = $root.FindFirst([Windows.Automation.TreeScope]::Descendants,
                [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, 'Ready'))
        }
        if ($null -eq $ready) { throw 'Default GUI did not load the isolated --config.' }
        $settingsButton = $root.FindFirst([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, 'Settings'))
        $settingsButton.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        $dialog = $null
        for ($i = 0; $i -lt 100 -and $null -eq $dialog; $i++) {
            Start-Sleep -Milliseconds 50
            $dialog = $root.FindFirst([Windows.Automation.TreeScope]::Descendants,
                [Windows.Automation.AndCondition]::new(
                    [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Window),
                    [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, ('MeetTape ' + [char]0x2014 + ' Settings'))))
            if ($null -eq $dialog) {
                $dialog = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,
                    [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, ('MeetTape ' + [char]0x2014 + ' Settings')))
            }
        }
        if ($null -eq $dialog) {
            $texts = $root.FindAll([Windows.Automation.TreeScope]::Descendants,
                [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Text))
            foreach ($text in $texts) { Write-Output $text.Current.Name }
            throw 'Native Settings dialog did not open.'
        }
        $pathLabel = $dialog.FindFirst([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $configPath))
        if ($null -eq $pathLabel) { throw 'Settings did not honor --config; no save was attempted.' }
        $close = $dialog.FindFirst([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, 'Close'))
        $close.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        $exit = $root.FindFirst([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, 'Exit'))
        $exit.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        if (-not $gui.WaitForExit(15000)) { throw 'GUI Exit button did not finish.' }
        $gui.Refresh()
        if ($gui.ExitCode -ne 0 -or [IO.Directory]::Exists($config.meetingsRoot)) {
            throw 'Default GUI must exit cleanly without creating any recording.'
        }
        Write-Output 'Native default GUI: explicit --config, Settings open/close and actual Exit button passed without capture/API.'
    } finally {
        if (-not $gui.HasExited) { $gui.Kill(); [void]$gui.WaitForExit(5000) }
        $gui.Dispose()
    }
}
