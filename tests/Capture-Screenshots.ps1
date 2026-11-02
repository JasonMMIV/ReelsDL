# Captures the README screenshot from the built application.
# Run .\build.ps1 first, then:
#   powershell -ExecutionPolicy Bypass -File .\tests\Capture-Screenshots.ps1
# Requires an interactive desktop session (the window must be visible).

param(
    [string]$SampleUrl = 'https://www.instagram.com/reel/C8xYzAbCdEf/'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

if (-not ('Win32Capture' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public class Win32Capture {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
}
'@
}

$root = Split-Path -Parent $PSScriptRoot
$outputDir = Join-Path $root 'docs\screenshots'
[IO.Directory]::CreateDirectory($outputDir) | Out-Null

function Wait-WindowHandle {
    param([System.Diagnostics.Process]$Process, [int]$TimeoutSeconds = 15)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $Process.Refresh()
        if ($Process.HasExited) { throw 'Process exited before a window appeared.' }
        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) { return $Process.MainWindowHandle }
        Start-Sleep -Milliseconds 200
    }
    throw "Window did not appear within $TimeoutSeconds seconds."
}

function Test-BitmapContent {
    param([System.Drawing.Bitmap]$Bitmap)

    $colors = @{}
    $stepX = [Math]::Max(1, [int]($Bitmap.Width / 24))
    $stepY = [Math]::Max(1, [int]($Bitmap.Height / 24))
    for ($x = 0; $x -lt $Bitmap.Width; $x += $stepX) {
        for ($y = 0; $y -lt $Bitmap.Height; $y += $stepY) {
            $colors[$Bitmap.GetPixel($x, $y).ToArgb()] = $true
        }
    }
    return ($colors.Count -ge 8)
}

function Save-WindowScreenshot {
    param(
        [System.Diagnostics.Process]$Process,
        [string]$Path,
        [int]$SettleMilliseconds = 1200
    )

    $hwnd = Wait-WindowHandle -Process $Process
    [void][Win32Capture]::ShowWindow($hwnd, 9)   # SW_RESTORE
    [void][Win32Capture]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds $SettleMilliseconds

    $rect = New-Object Win32Capture+RECT
    if (-not [Win32Capture]::GetWindowRect($hwnd, [ref]$rect)) { throw 'GetWindowRect failed.' }

    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -le 0 -or $height -le 0) { throw 'Window has an invalid size.' }

    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    try {
        $printed = $false
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $hdc = $graphics.GetHdc()
            try { $printed = [Win32Capture]::PrintWindow($hwnd, $hdc, 2) }
            finally { $graphics.ReleaseHdc($hdc) }
        } finally { $graphics.Dispose() }

        # Some WinForms controls do not render through PrintWindow; fall back
        # to a screen capture of the (foreground) window rectangle.
        if (-not $printed -or -not (Test-BitmapContent -Bitmap $bitmap)) {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($width, $height)))
            } finally { $graphics.Dispose() }
        }

        if (-not (Test-BitmapContent -Bitmap $bitmap)) {
            throw 'Captured image appears blank. Unlock the desktop and keep the window visible.'
        }

        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $bitmap.Dispose()
    }

    $length = (Get-Item -LiteralPath $Path).Length
    Write-Host ("Saved {0} ({1}x{2}, {3:N0} KB)" -f $Path, $width, $height, ($length / 1KB))
}

$existing = @(Get-Process -Name 'ReelsDL' -ErrorAction SilentlyContinue)
if ($existing.Count -gt 0) {
    throw 'Close ReelsDL before capturing the screenshot.'
}

$appPath = Join-Path $root 'build\ReelsDL.exe'
if (-not (Test-Path -LiteralPath $appPath)) {
    throw 'build\ReelsDL.exe not found. Run .\build.ps1 first.'
}

# The main window pre-fills the URL from the clipboard on startup.
$savedClipboard = $null
try { $savedClipboard = [System.Windows.Forms.Clipboard]::GetText() } catch { }
try {
    try { [System.Windows.Forms.Clipboard]::SetText($SampleUrl) } catch { }

    $app = Start-Process -FilePath $appPath -PassThru
    try {
        Save-WindowScreenshot -Process $app -Path (Join-Path $outputDir 'main-window.png')
    } finally {
        if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force }
    }
} finally {
    if ($null -ne $savedClipboard) {
        try { [System.Windows.Forms.Clipboard]::SetText($savedClipboard) } catch { }
    }
}

