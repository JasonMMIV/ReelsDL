<#
.SYNOPSIS
    Build script for ReelsDL and its Windows Installer.
.DESCRIPTION
    Compiles ReelsDL.exe, Uninstall.exe, and bundles them into dist\ReelsDL-Setup.exe.
.PARAMETER Clean
    Removes build and dist output directories.
.PARAMETER Run
    Builds and immediately runs the development build.
#>
param(
    [switch]$Clean,
    [switch]$Run
)

$ErrorActionPreference = 'Stop'
$rootDir = $PSScriptRoot
$srcDir = Join-Path $rootDir 'src'
$sharedDir = Join-Path $srcDir 'shared'
$buildDir = Join-Path $rootDir 'build'
$distDir = Join-Path $rootDir 'dist'

if ($Clean) {
    Write-Host "Cleaning build and dist directories..." -ForegroundColor Yellow
    if (Test-Path $buildDir) { Remove-Item $buildDir -Recurse -Force }
    if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force }
    Write-Host "Clean finished." -ForegroundColor Green
    return
}

# Locate C# compiler
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = (Get-Command 'csc.exe' -ErrorAction SilentlyContinue).Source
}
if (-not $csc -or -not (Test-Path $csc)) {
    throw "Microsoft .NET C# compiler (csc.exe) not found."
}

# Ensure directories
if (-not (Test-Path $buildDir)) { New-Item -ItemType Directory -Force -Path $buildDir | Out-Null }
if (-not (Test-Path $distDir)) { New-Item -ItemType Directory -Force -Path $distDir | Out-Null }

$icoPath = Join-Path $srcDir 'app.ico'
$manifest = Join-Path $srcDir 'app.manifest'
$fluentCs = Join-Path $sharedDir 'Fluent.cs'
$assemblyInfo = Join-Path $sharedDir 'AssemblyInfo.cs'
$ps1Path = Join-Path $srcDir 'ReelsDL.ps1'

# The engine is executed by Windows PowerShell 5.1 (powershell.exe). On
# non-UTF-8 code pages a BOM-less UTF-8 script is parsed as ANSI and fails.
# Normalize the source, then fail the build on any parse error.
function Assert-Ps1Engine([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 3 -or $bytes[0] -ne 0xEF -or $bytes[1] -ne 0xBB -or $bytes[2] -ne 0xBF) {
        Write-Host "--> Adding UTF-8 BOM to $Path" -ForegroundColor Yellow
        $text = [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8)
        [IO.File]::WriteAllText($Path, $text, (New-Object Text.UTF8Encoding $true))
    }
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) {
        throw ("PowerShell parse failed ({0} error(s)): {1}" -f $errors.Count, $errors[0].Message)
    }
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Building ReelsDL Project" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

Assert-Ps1Engine $ps1Path

# Run the engine's own offline checks before building anything.
Write-Host "--> Running engine self-tests..." -ForegroundColor Gray
$psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
if (-not (Test-Path $psExe)) { $psExe = 'powershell.exe' }
& $psExe -NoProfile -ExecutionPolicy Bypass -File $ps1Path -SelfTest
if ($LASTEXITCODE -ne 0) { throw "ReelsDL.ps1 self-tests failed." }

# 1. Compile ReelsDL.exe (Fluent Desktop App)
Write-Host "--> Compiling ReelsDL.exe..." -ForegroundColor Gray
& $csc /nologo /codepage:65001 /optimize+ /target:winexe /win32icon:"$icoPath" `
    /win32manifest:"$manifest" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    /out:"$buildDir\ReelsDL.exe" `
    "$srcDir\Launcher.cs" "$srcDir\MainForm.cs" "$fluentCs" "$assemblyInfo"
if ($LASTEXITCODE -ne 0) { throw "Failed to compile ReelsDL.exe" }

# 2. Compile Uninstall.exe (Fluent Uninstaller)
Write-Host "--> Compiling Uninstall.exe uninstaller..." -ForegroundColor Gray
& $csc /nologo /codepage:65001 /optimize+ /target:winexe /win32icon:"$icoPath" `
    /win32manifest:"$manifest" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    /out:"$buildDir\Uninstall.exe" `
    "$srcDir\installer\Uninstall.cs" "$fluentCs" "$assemblyInfo"
if ($LASTEXITCODE -ne 0) { throw "Failed to compile Uninstall.exe" }

# 3. Stage payload files in build/
Copy-Item $ps1Path (Join-Path $buildDir 'ReelsDL.ps1') -Force
Assert-Ps1Engine (Join-Path $buildDir 'ReelsDL.ps1')
Copy-Item $icoPath (Join-Path $buildDir 'app.ico') -Force

# 4. Compile Standalone Installer (dist\ReelsDL-Setup.exe)
Write-Host "--> Bundling & Compiling dist\ReelsDL-Setup.exe installer..." -ForegroundColor Gray
& $csc /nologo /codepage:65001 /optimize+ /target:winexe /win32icon:"$icoPath" `
    /win32manifest:"$manifest" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    /resource:"$buildDir\ReelsDL.exe",ReelsDL.exe `
    /resource:"$buildDir\ReelsDL.ps1",ReelsDL.ps1 `
    /resource:"$buildDir\app.ico",app.ico `
    /resource:"$buildDir\Uninstall.exe",Uninstall.exe `
    /out:"$distDir\ReelsDL-Setup.exe" `
    "$srcDir\installer\Setup.cs" "$fluentCs" "$assemblyInfo"
if ($LASTEXITCODE -ne 0) { throw "Failed to compile ReelsDL-Setup.exe" }

$installer = Get-Item "$distDir\ReelsDL-Setup.exe"
$sizeKb = [math]::Round($installer.Length / 1KB, 1)

Write-Host ""
Write-Host "BUILD SUCCESSFUL!" -ForegroundColor Green
Write-Host "  Output Installer : $($installer.FullName)" -ForegroundColor White
Write-Host "  Installer Size   : $sizeKb KB" -ForegroundColor White
Write-Host ""

if ($Run) {
    Write-Host "Launching development build..." -ForegroundColor Cyan
    Start-Process "$buildDir\ReelsDL.exe"
}
