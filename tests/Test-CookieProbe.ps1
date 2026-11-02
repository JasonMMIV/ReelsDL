# Read-only diagnostic for the cookie probe embedded in src\ReelsDL.ps1.
# Prints cookie names and host keys only; never prints cookie values.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$engine = Join-Path $root 'src\ReelsDL.ps1'
$src = [IO.File]::ReadAllText($engine)

$marker = "`$script:CookieProbeSource = @'"
$start = $src.IndexOf($marker)
if ($start -lt 0) { throw 'CookieProbe source not found in ReelsDL.ps1' }
$start = $src.IndexOf("`n", $start) + 1
$end = $src.IndexOf("`n'@", $start)
if ($end -lt 0) { throw 'CookieProbe terminator not found in ReelsDL.ps1' }

Add-Type -TypeDefinition $src.Substring($start, $end - $start) -Language CSharp

$profileRoot = Join-Path $env:LOCALAPPDATA 'ReelsDL\profile'
if (-not (Test-Path -LiteralPath $profileRoot)) {
    Write-Host 'No profile directory found.'
    exit 0
}

$databases = @()
foreach ($dir in @(Get-ChildItem -LiteralPath $profileRoot -Directory -ErrorAction SilentlyContinue)) {
    $db = Join-Path $dir.FullName 'Network\Cookies'
    if (Test-Path -LiteralPath $db) { $databases += (Get-Item -LiteralPath $db) }
}
if ($databases.Count -eq 0) {
    Write-Host 'No cookie database found.'
    exit 0
}

foreach ($database in $databases) {
    Write-Host ("Database: " + $database.FullName)
    $rows = [CookieProbe]::Read($database.FullName)
    Write-Host ("rows=" + $rows.Count)
    $rows | Group-Object Host | Sort-Object Count -Descending |
        Select-Object -First 10 | ForEach-Object { Write-Host ("{0} x{1}" -f $_.Name, $_.Count) }
    $rows | Where-Object { $_.Name -match 'session|auth|user|sid|csrftoken|mid' } |
        Select-Object -First 20 | ForEach-Object {
            Write-Host ("{0} | {1} | exp={2} | he={3}" -f $_.Host, $_.Name, $_.ExpiresUtc, $_.HasExpires)
        }
}
