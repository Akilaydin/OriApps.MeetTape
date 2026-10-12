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

# Exact test-only startup skips real settings, devices, processing and release checks.
if ($SkipNativeWindow) {
    $smoke = Start-Process -FilePath $executable -ArgumentList '--ui-smoke' -WorkingDirectory $testRoot -PassThru -WindowStyle Hidden
    try {
        if (-not $smoke.WaitForExit(20000)) { throw 'Published GUI did not complete its startup/lifetime/Exit smoke.' }
        if ($smoke.ExitCode -ne 0) { throw 'Published GUI smoke failed.' }
        Write-Output 'Published WinExe: isolated GUI startup, desktop lifetime and explicit Exit passed.'
    } finally {
        if (-not $smoke.HasExited) { $smoke.Kill(); [void]$smoke.WaitForExit(5000) }
        $smoke.Dispose()
    }
}

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

}
