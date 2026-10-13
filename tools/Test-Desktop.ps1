param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [switch]$SkipNativeWindow,
    [switch]$IconOnly
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

# Extract the actual PE resources at Windows shell/DPI sizes. Matching the
# source ICO prevents a valid but generic .NET/Velopack icon from passing.
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Runtime.InteropServices;
public static class MeetTapeIconProbe {
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "PrivateExtractIconsW")]
    private static extern uint Extract(string file, int index, int width, int height,
        IntPtr[] icons, uint[] ids, uint count, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    public static bool Matches(string executable, string reference, int size) {
        var icons = new IntPtr[1]; var sourceIcons = new IntPtr[1];
        try {
            if (Extract(executable, 0, size, size, icons, new uint[1], 1, 0) != 1 || icons[0] == IntPtr.Zero)
                throw new InvalidOperationException("Cannot extract executable icon.");
            if (Extract(reference, 0, size, size, sourceIcons, new uint[1], 1, 0) != 1 || sourceIcons[0] == IntPtr.Zero)
                throw new InvalidOperationException("Cannot extract source icon.");
            using (var actualIcon = Icon.FromHandle(icons[0]))
            using (var expectedIcon = Icon.FromHandle(sourceIcons[0]))
            using (var actual = actualIcon.ToBitmap())
            using (var expected = expectedIcon.ToBitmap()) {
                if (actual.Width != size || actual.Height != size || expected.Width != size || expected.Height != size)
                    throw new InvalidOperationException(String.Format("Icon dimensions at {0}px: actual {1}x{2}, source {3}x{4}.", size, actual.Width, actual.Height, expected.Width, expected.Height));
                for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) {
                    var a = actual.GetPixel(x, y); var b = expected.GetPixel(x, y);
                    if (b.A == 255 && (a.R != b.R || a.G != b.G || a.B != b.B))
                        throw new InvalidOperationException(String.Format("Icon pixel differs at {0}px ({1},{2}): actual {3}, source {4}.", size, x, y, a, b));
                }
                return true;
            }
        } finally {
            if (icons[0] != IntPtr.Zero) DestroyIcon(icons[0]);
            if (sourceIcons[0] != IntPtr.Zero) DestroyIcon(sourceIcons[0]);
        }
    }
}
'@
$icon = Join-Path $repository 'assets/MeetTape.ico'
foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 128, 256)) {
    if (-not [MeetTapeIconProbe]::Matches($executable, $icon, $size)) { throw "Executable icon differs from MeetTape.ico at ${size}px." }
}
Write-Output "Windows PE icon: all 9 source sizes matched for $executable."
if ($IconOnly) { return }

# Exact test-only startup skips real settings, devices, processing and release checks.
if ($SkipNativeWindow) {
    $smokeError = Join-Path $testRoot 'startup-stderr.txt'
    # The disposable CI taskbar check needs a visible button; local startup remains hidden.
    $smokeStyle = if ($env:GITHUB_ACTIONS -eq 'true') { 'Normal' } else { 'Hidden' }
    $smoke = Start-Process -FilePath $executable -ArgumentList '--ui-smoke' -WorkingDirectory $testRoot -PassThru -WindowStyle $smokeStyle -RedirectStandardError $smokeError
    try {
        if (-not $smoke.WaitForExit(20000)) { throw 'Published GUI did not complete its startup/lifetime/Exit smoke.' }
        if ($smoke.ExitCode -ne 0) { Get-Content -LiteralPath $smokeError; throw 'Published GUI smoke failed.' }
        Write-Output 'Published WinExe: isolated GUI startup, full diagnostic text selection, desktop lifetime and explicit Exit passed.'
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
        Write-Output "Native Windows window: creation, hide, reopen, full diagnostic text selection and Exit passed. Screenshot: $testRoot\native-window.png"
        Write-Output 'TrayIcon created by native host; shell icon/menu clicks remain a manual check.'
    } finally {
        if (-not $smoke.HasExited) { $smoke.Kill(); [void]$smoke.WaitForExit(5000) }
        $smoke.Dispose()
    }

}
