param(
    [string]$Url,
    [string]$Out,
    [switch]$Cli,
    [switch]$Login,
    [string]$Platform,
    [string]$Log,
    [switch]$CookieState,
    [switch]$SelfTest,
    [switch]$Help
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# ==============================================================================
#  ReelsDL - Universal Short Video Downloader
#  Console-only extraction engine. The desktop UI lives in ReelsDL.exe
#  (MainForm.cs) and drives this script with -Cli / -Login / -CookieState.
#  Supported: Threads, Instagram, X (Twitter), TikTok, Facebook
# ==============================================================================

$script:ProfileDir = Join-Path $env:LOCALAPPDATA 'ReelsDL\profile'
$script:MaxDownloadBytes = 8GB
$script:CdpId = 0

$legacyProfiles = @(
    (Join-Path $env:LOCALAPPDATA 'SocialDL\profile'),
    (Join-Path $env:LOCALAPPDATA 'ThreadsDL\profile')
)

# ------------------------------------------------------------------------------
#  Read-only cookie database probe.
#
#  Chrome encrypts cookie values but stores host_key and name as plain text.
#  Reading raw bytes cannot tell which name belongs to which host, which used
#  to report "logged in" when e.g. TikTok's sessionid and Instagram's csrftoken
#  were both present. Windows ships winsqlite3.dll, so query the table directly.
# ------------------------------------------------------------------------------
$script:CookieProbeSource = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public class CookieRecord {
    public string Host;
    public string Name;
    public long ExpiresUtc;
    public long HasExpires;
}

public static class CookieProbe {
    private const int SQLITE_OK = 0;
    private const int SQLITE_ROW = 100;
    private const int SQLITE_DONE = 101;
    private const int SQLITE_OPEN_READONLY = 1;
    private const int SQLITE_OPEN_READWRITE = 2;
    private const int SQLITE_OPEN_CREATE = 4;
    private const int SQLITE_OPEN_NOMUTEX = 0x00008000;

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr db, int flags, IntPtr vfs);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int numBytes, out IntPtr stmt, IntPtr tail);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern long sqlite3_column_int64(IntPtr stmt, int col);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(IntPtr db);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr arg, IntPtr errmsg);

    // Test helper: create a small synthetic database for the self-test.
    public static void Exec(string path, string sql) {
        IntPtr db = IntPtr.Zero;
        try {
            int rc = sqlite3_open_v2(path, out db, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE, IntPtr.Zero);
            if (rc != SQLITE_OK || db == IntPtr.Zero) throw new InvalidOperationException("sqlite open failed: " + rc);
            rc = sqlite3_exec(db, sql, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (rc != SQLITE_OK) throw new InvalidOperationException("sqlite exec failed: " + rc);
        } finally {
            if (db != IntPtr.Zero) sqlite3_close(db);
        }
    }

    public static List<CookieRecord> Read(string path) {
        IntPtr db = IntPtr.Zero;
        try {
            int rc = sqlite3_open_v2(path, out db, SQLITE_OPEN_READONLY | SQLITE_OPEN_NOMUTEX, IntPtr.Zero);
            if (rc != SQLITE_OK || db == IntPtr.Zero) throw new InvalidOperationException("sqlite open failed: " + rc);
            List<CookieRecord> list = new List<CookieRecord>();
            if (Query(db, "SELECT host_key, name, expires_utc, has_expires FROM cookies", 0, list)) return list;
            list.Clear();
            if (Query(db, "SELECT host_key, name, expires_utc FROM cookies", 1, list)) return list;
            list.Clear();
            if (Query(db, "SELECT host_key, name FROM cookies", 2, list)) return list;
            throw new InvalidOperationException("cookies table is not readable");
        } finally {
            if (db != IntPtr.Zero) sqlite3_close(db);
        }
    }

    private static bool Query(IntPtr db, string sql, int mode, List<CookieRecord> list) {
        IntPtr stmt = IntPtr.Zero;
        try {
            int rc = sqlite3_prepare_v2(db, sql, -1, out stmt, IntPtr.Zero);
            if (rc != SQLITE_OK || stmt == IntPtr.Zero) return false;
            int step;
            while ((step = sqlite3_step(stmt)) == SQLITE_ROW) {
                CookieRecord record = new CookieRecord();
                record.Host = ReadText(sqlite3_column_text(stmt, 0));
                record.Name = ReadText(sqlite3_column_text(stmt, 1));
                record.ExpiresUtc = mode < 2 ? sqlite3_column_int64(stmt, 2) : 0;
                record.HasExpires = mode == 0 ? sqlite3_column_int64(stmt, 3) : (mode == 1 ? 1 : 0);
                if (record.Name.Length > 0) list.Add(record);
            }
            if (step != SQLITE_DONE) throw new InvalidOperationException("sqlite step failed: " + step);
            return true;
        } finally {
            if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
        }
    }

    private static string ReadText(IntPtr pointer) {
        if (pointer == IntPtr.Zero) return "";
        int length = 0;
        while (Marshal.ReadByte(pointer, length) != 0) length++;
        if (length == 0) return "";
        byte[] buffer = new byte[length];
        Marshal.Copy(pointer, buffer, 0, length);
        return Encoding.UTF8.GetString(buffer);
    }
}
'@

$script:CookieProbeReady = $false
$script:CookieProbeFailed = $false
$script:CookieProbeError = ''

# ------------------------------------------------------------------------------
#  Logging
# ------------------------------------------------------------------------------

function Write-Log {
    param([string]$Message)
    # Log lines are a single-line protocol; strip newlines from remote text.
    $Message = ([string]$Message) -replace '[\r\n]+', ' '
    $line = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $Message
    if ($Log) {
        try { [IO.File]::AppendAllText($Log, $line + "`r`n", (New-Object Text.UTF8Encoding $false)) } catch { }
    }
    Write-Host $line
}

# Structured progress events for the desktop app (written to the -Log file only).
# Format: ##EVT|<type>|<field>|<field>...
function Write-Evt {
    param([string]$Type, [object[]]$Fields = @())
    if (-not $Log) { return }
    $clean = @($Fields | ForEach-Object { ([string]$_) -replace '[\|\r\n]', ' ' })
    $line = '##EVT|' + $Type
    if ($clean.Count -gt 0) { $line += '|' + ($clean -join '|') }
    try { [IO.File]::AppendAllText($Log, $line + "`r`n", (New-Object Text.UTF8Encoding $false)) } catch { }
}

# One-time, atomic migration from the old profile locations. Runs only for
# actual download/login jobs, never for -CookieState or -SelfTest.
function Import-LegacyProfile {
    if (Test-Path -LiteralPath $script:ProfileDir) { return }
    foreach ($legacy in $legacyProfiles) {
        if (-not (Test-Path -LiteralPath $legacy)) { continue }
        $temp = $script:ProfileDir + '.migrating'
        try {
            $parent = Split-Path -Parent $script:ProfileDir
            if (-not (Test-Path -LiteralPath $parent)) { [IO.Directory]::CreateDirectory($parent) | Out-Null }
            if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
            Copy-Item -LiteralPath $legacy -Destination $temp -Recurse -Force
            Move-Item -LiteralPath $temp -Destination $script:ProfileDir -Force
            Write-Log ("Imported browser session from " + $legacy)
            return
        } catch {
            if (Test-Path -LiteralPath $temp) {
                try { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue } catch { }
            }
        }
    }
}

# ------------------------------------------------------------------------------
#  Download / filename helpers
# ------------------------------------------------------------------------------

# Returns a delay in seconds for the next attempt. Honors Retry-After on 429.
function Get-RetryDelay {
    param($Failure, [int]$Attempt)
    $delay = [Math]::Pow(2, [Math]::Max(0, $Attempt - 1))
    try {
        $response = $Failure.Exception.Response
        if ($response -and [int]$response.StatusCode -eq 429) {
            $retryAfter = $response.Headers['Retry-After']
            if ($retryAfter -and ([string]$retryAfter) -match '^\d+$') {
                $delay = [Math]::Min([int]$retryAfter, 30)
            }
        }
    } catch { }
    return [int][Math]::Min([Math]::Max($delay, 1), 30)
}

# Streams a remote file to disk while emitting progress events.
# Returns an object with Bytes and ContentType. A .part file is used so an
# interrupted transfer never leaves a corrupt media file behind.
function Save-RemoteFile {
    param(
        [string]$Url,
        [string]$Path,
        [string]$Referer,
        [string]$UserAgent,
        [int]$Index,
        [int]$Count,
        [string]$ExpectedType = ''
    )

    if (-not $Url -or $Url -notmatch '^https?://') {
        throw "Unsupported media URL: $Url"
    }

    $req = [System.Net.HttpWebRequest]::Create($Url)
    $req.UserAgent = $UserAgent
    $req.Accept = '*/*'
    if ($Referer) { $req.Referer = $Referer }
    $req.Timeout = 30000
    $req.ReadWriteTimeout = 60000
    $req.AllowAutoRedirect = $true

    $tmp = $Path + '.part'
    $resp = $null; $in = $null; $out = $null
    $moved = $false
    try {
        $resp = $req.GetResponse()
        $ct = [string]$resp.ContentType

        if ($ct -match 'text/html|application/json|text/plain') {
            throw "Unexpected content type: $ct"
        }
        if ($ExpectedType -eq 'video' -and $ct -and $ct -notmatch '^(video/|application/(mp4|octet-stream)|binary/octet-stream)') {
            throw "Expected video but received: $ct"
        }
        if ($ExpectedType -eq 'image' -and $ct -and $ct -notmatch '^(image/|application/(jpeg|jpg|png|webp|gif|octet-stream)|binary/octet-stream)') {
            throw "Expected image but received: $ct"
        }

        $total = [long]$resp.ContentLength
        if ($total -gt $script:MaxDownloadBytes) {
            throw ("File is too large ({0:N1} GB); the limit is 8 GB." -f ($total / 1GB))
        }

        $in = $resp.GetResponseStream()
        $out = [IO.File]::Create($tmp)
        $buf = New-Object byte[] 262144
        $read = [long]0
        $sw = [Diagnostics.Stopwatch]::StartNew()
        Write-Evt 'prog' @($Index, $Count, 0, $total)
        while (($n = $in.Read($buf, 0, $buf.Length)) -gt 0) {
            $out.Write($buf, 0, $n)
            $read += $n
            if ($read -gt $script:MaxDownloadBytes) {
                throw 'Download exceeded the 8 GB size limit.'
            }
            if ($sw.ElapsedMilliseconds -ge 200) {
                Write-Evt 'prog' @($Index, $Count, $read, $total)
                $sw.Restart()
            }
        }
        $out.Dispose(); $out = $null

        if ($total -gt 0 -and $read -ne $total) {
            throw "Incomplete download: $read of $total bytes"
        }

        Write-Evt 'prog' @($Index, $Count, $read, $(if ($total -gt 0) { $total } else { $read }))
        Move-Item -LiteralPath $tmp -Destination $Path -Force
        $moved = $true
        return [pscustomobject]@{ Bytes = $read; ContentType = $ct }
    }
    finally {
        if ($out) { $out.Dispose() }
        if ($in) { $in.Dispose() }
        if ($resp) { $resp.Close() }
        if (-not $moved -and (Test-Path -LiteralPath $tmp)) {
            try { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue } catch { }
        }
    }
}

function Get-MediaExtension {
    param([string]$ContentType, [string]$DefaultExtension)
    $ct = ([string]$ContentType).Trim().ToLowerInvariant()
    if ($ct -and $ct.IndexOf(';') -ge 0) { $ct = ($ct -split ';')[0].Trim() }
    $map = @{
        'video/mp4'          = '.mp4'
        'application/mp4'    = '.mp4'
        'video/webm'         = '.webm'
        'video/quicktime'    = '.mov'
        'video/x-matroska'   = '.mkv'
        'video/x-msvideo'    = '.avi'
        'video/mpeg'         = '.mpg'
        'video/ogg'          = '.ogv'
        'video/3gpp'         = '.3gp'
        'image/jpeg'         = '.jpg'
        'image/jpg'          = '.jpg'
        'application/jpeg'   = '.jpg'
        'image/png'          = '.png'
        'application/png'    = '.png'
        'image/webp'         = '.webp'
        'image/gif'          = '.gif'
        'image/bmp'          = '.bmp'
        'image/avif'         = '.avif'
        'image/heic'         = '.heic'
        'image/tiff'         = '.tiff'
    }
    if ($map.ContainsKey($ct)) { return $map[$ct] }
    return $DefaultExtension
}

function Get-UniqueFilePath {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $Path }
    $dir = Split-Path -Parent $Path
    $base = [IO.Path]::GetFileNameWithoutExtension($Path)
    $ext = [IO.Path]::GetExtension($Path)
    for ($i = 1; $i -lt 10000; $i++) {
        $candidate = Join-Path $dir ("{0} ({1}){2}" -f $base, $i, $ext)
        if (-not (Test-Path -LiteralPath $candidate)) { return $candidate }
    }
    throw "Unable to find a unique filename for $Path"
}

# Produces a Windows-safe base filename: strips reserved characters, trailing
# dots/spaces, reserved device names, and caps the length well below MAX_PATH.
function Get-SafeBaseName {
    param([string]$Name, [int]$MaxLength = 120)
    if (-not $Name) { $Name = 'media' }
    $safe = $Name -replace '[\\/:*?"<>|]', '_'
    $safe = $safe.Trim()
    $safe = $safe.TrimEnd([char[]]@('.', ' '))
    if ($safe.Length -gt $MaxLength) {
        $safe = $safe.Substring(0, $MaxLength).TrimEnd([char[]]@('.', ' '))
    }
    if ($safe -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)') { $safe = '_' + $safe }
    if (-not $safe) { $safe = 'media' }
    return $safe
}

# Respects a relocated Downloads known folder (OneDrive / group policy).
function Get-DownloadsFolder {
    try {
        $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders'
        $guid = '{374DE290-123F-4565-9164-39C4925E467B}'
        $value = (Get-ItemProperty -LiteralPath $key -Name $guid -ErrorAction Stop).$guid
        if ($value) {
            $expanded = [Environment]::ExpandEnvironmentVariables([string]$value)
            if ($expanded) { return $expanded }
        }
    } catch { }
    return (Join-Path $env:USERPROFILE 'Downloads')
}

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
} catch { }

# ------------------------------------------------------------------------------
#  Browser / platform detection
# ------------------------------------------------------------------------------

function Get-BrowserPath {
    $roots = @(${env:ProgramFiles}, ${env:ProgramFiles(x86)}, $env:LOCALAPPDATA)
    $relative = @(
        'Google\Chrome\Application\chrome.exe',
        'Microsoft\Edge\Application\msedge.exe'
    )
    foreach ($rel in $relative) {
        foreach ($root in $roots) {
            if (-not $root) { continue }
            $candidate = Join-Path $root $rel
            if (Test-Path -LiteralPath $candidate) { return $candidate }
        }
    }
    throw 'No supported browser found (Chrome or Edge). Please install one.'
}

function Get-BrowserUserAgent {
    param([string]$BrowserPath)
    $major = '131'
    try {
        $version = (Get-Item -LiteralPath $BrowserPath).VersionInfo.ProductVersion
        if ($version -match '^(\d+)') { $major = $Matches[1] }
    } catch { }

    $ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/$major.0.0.0 Safari/537.36"
    if ($BrowserPath -match 'msedge\.exe$') {
        $ua += " Edg/$major.0.0.0"
    }
    return $ua
}

function Test-UrlHost {
    param([string]$TargetUrl, [string]$Domain)
    try {
        $u = [Uri]$TargetUrl
        if (-not $u.IsAbsoluteUri) { return $false }
        $h = $u.Host.ToLowerInvariant()
        return ($h -eq $Domain -or $h.EndsWith(('.' + $Domain), [StringComparison]::Ordinal))
    } catch {
        return $false
    }
}

function Get-UrlPlatform {
    param([string]$TargetUrl)
    if (-not $TargetUrl) { return 'Unknown' }
    if (Test-UrlHost $TargetUrl 'instagram.com') { return 'Instagram' }
    if ((Test-UrlHost $TargetUrl 'threads.com') -or (Test-UrlHost $TargetUrl 'threads.net')) { return 'Threads' }
    if ((Test-UrlHost $TargetUrl 'x.com') -or (Test-UrlHost $TargetUrl 'twitter.com')) { return 'X' }
    if (Test-UrlHost $TargetUrl 'tiktok.com') { return 'TikTok' }
    if ((Test-UrlHost $TargetUrl 'facebook.com') -or (Test-UrlHost $TargetUrl 'fb.watch')) { return 'Facebook' }
    return 'General'
}

# ------------------------------------------------------------------------------
#  Profile / process handling
# ------------------------------------------------------------------------------

function Get-ProfileBrowserProcesses {
    try {
        $procs = @(Get-CimInstance Win32_Process -Filter "Name='chrome.exe' OR Name='msedge.exe'" -ErrorAction SilentlyContinue)
    } catch {
        return @()
    }
    $hits = @()
    foreach ($p in $procs) {
        if ($p.CommandLine -and $p.CommandLine.IndexOf($script:ProfileDir, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $hits += $p
        }
    }
    return $hits
}

function Get-ProfileMainProcesses {
    return @(Get-ProfileBrowserProcesses | Where-Object { $_.CommandLine -notmatch '--type=' })
}

# Only an interactive login window should block a job. Leftover headless
# browsers are safe to terminate and must not deadlock future downloads.
function Get-ProfileBlockingProcesses {
    return @(Get-ProfileMainProcesses | Where-Object { $_.CommandLine -notmatch '--headless' })
}

function Stop-ProfileBrowsers {
    foreach ($p in @(Get-ProfileBrowserProcesses)) {
        try { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue } catch { }
    }
}

# ------------------------------------------------------------------------------
#  Cookie database access
# ------------------------------------------------------------------------------

function Initialize-CookieProbe {
    if ($script:CookieProbeReady) { return $true }
    if ($script:CookieProbeFailed) { return $false }
    try {
        if (-not ('CookieProbe' -as [type])) {
            Add-Type -TypeDefinition $script:CookieProbeSource -Language CSharp -ErrorAction Stop
        }
        $script:CookieProbeReady = $true
        return $true
    } catch {
        $script:CookieProbeFailed = $true
        $script:CookieProbeError = $_.Exception.Message
        return $false
    }
}

function Get-CookieFiles {
    $files = @()
    if (-not (Test-Path -LiteralPath $script:ProfileDir)) { return $files }
    foreach ($dir in @(Get-ChildItem -LiteralPath $script:ProfileDir -Directory -ErrorAction SilentlyContinue)) {
        $base = Join-Path $dir.FullName 'Network\Cookies'
        foreach ($suffix in @('', '-wal', '-shm', '-journal')) {
            $path = $base + $suffix
            if (Test-Path -LiteralPath $path) { $files += (Get-Item -LiteralPath $path) }
        }
    }
    return $files
}

function Get-CookieDatabases {
    return @(Get-CookieFiles | Where-Object { $_.Name -eq 'Cookies' })
}

# Chrome may hold the database open. If SQLite reports BUSY, read a shared
# copy (including the WAL) instead of blaming the user for a lock.
function Copy-CookieDatabase {
    param([string]$Path)
    $dir = Join-Path ([IO.Path]::GetTempPath()) ('reelsdl_cookies_' + [guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($dir) | Out-Null
    foreach ($suffix in @('', '-wal', '-shm')) {
        $src = $Path + $suffix
        if (-not (Test-Path -LiteralPath $src)) { continue }
        $dst = Join-Path $dir ('Cookies' + $suffix)
        $reader = [IO.File]::Open($src, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try {
            $writer = [IO.File]::Create($dst)
            try { $reader.CopyTo($writer) } finally { $writer.Dispose() }
        } finally { $reader.Dispose() }
    }
    $db = Join-Path $dir 'Cookies'
    if (-not (Test-Path -LiteralPath $db)) { throw 'Cookie database could not be copied.' }
    return [pscustomobject]@{ Dir = $dir; Db = $db }
}

function Get-CookieRecords {
    $list = New-Object System.Collections.Generic.List[object]
    $ok = $false
    foreach ($db in @(Get-CookieDatabases)) {
        try {
            foreach ($record in [CookieProbe]::Read($db.FullName)) { $list.Add($record) }
            $ok = $true
            continue
        } catch { }
        $copy = $null
        try {
            $copy = Copy-CookieDatabase -Path $db.FullName
            foreach ($record in [CookieProbe]::Read($copy.Db)) { $list.Add($record) }
            $ok = $true
        } catch { }
        finally {
            if ($copy -and (Test-Path -LiteralPath $copy.Dir)) {
                Remove-Item -LiteralPath $copy.Dir -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
    return [pscustomobject]@{ Ok = $ok; Records = $list }
}

function Test-CookieToken {
    param([string]$Text, [string]$Name)
    $pattern = '(?:^|[\x00-\x20])' + [regex]::Escape($Name) + '(?:[\x00-\x20]|$)'
    return [regex]::IsMatch($Text, $pattern, [Text.RegularExpressions.RegexOptions]::IgnoreCase)
}

# Fallback for systems without winsqlite3.dll. Still requires both a token and
# a matching platform domain, but cannot pair them at the record level.
function Get-CookieStateFallback {
    $state = [ordered]@{ Instagram = $false; Threads = $false; Facebook = $false; X = $false; TikTok = $false }
    $fbUser = $false
    $fbXs = $false
    foreach ($file in @(Get-CookieFiles)) {
        try {
            $text = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($file.FullName))
            $hasMeta = (Test-CookieToken $text 'sessionid') -or (Test-CookieToken $text 'ds_user_id')
            $hasX = Test-CookieToken $text 'auth_token'
            $hasTt = (Test-CookieToken $text 'sessionid') -or (Test-CookieToken $text 'sid_guard')
            $igDomain = $text.IndexOf('instagram.com', [StringComparison]::OrdinalIgnoreCase) -ge 0
            $thDomain = ($text.IndexOf('threads.com', [StringComparison]::OrdinalIgnoreCase) -ge 0) -or ($text.IndexOf('threads.net', [StringComparison]::OrdinalIgnoreCase) -ge 0)
            $fbDomain = $text.IndexOf('facebook.com', [StringComparison]::OrdinalIgnoreCase) -ge 0
            $xDomain = ($text.IndexOf('x.com', [StringComparison]::OrdinalIgnoreCase) -ge 0) -or ($text.IndexOf('twitter.com', [StringComparison]::OrdinalIgnoreCase) -ge 0)
            $ttDomain = $text.IndexOf('tiktok.com', [StringComparison]::OrdinalIgnoreCase) -ge 0
            if ($hasMeta -and $igDomain) { $state.Instagram = $true }
            if ($hasMeta -and $thDomain) { $state.Threads = $true }
            if ($hasX -and $xDomain) { $state.X = $true }
            if ($hasTt -and $ttDomain) { $state.TikTok = $true }
            if (Test-CookieToken $text 'c_user') { $fbUser = $true }
            if (Test-CookieToken $text 'xs') { $fbXs = $true }
            if ($fbUser -and $fbXs -and $fbDomain) { $state.Facebook = $true }
        } catch { }
    }
    return $state
}

function Get-CookieStateFromRecords {
    param($Records)
    $state = [ordered]@{ Instagram = $false; Threads = $false; Facebook = $false; X = $false; TikTok = $false }
    $fbUser = $false
    $fbXs = $false
    if ($null -eq $Records) { return $state }

    # Iterate directly: PowerShell 5.1's @() throws on List[object].
    foreach ($record in $Records) {
        $name = [string]$record.Name
        $hostName = ([string]$record.Host).TrimStart('.')
        if (-not $hostName -or -not $name) { continue }
        if ($record.HasExpires -and $record.ExpiresUtc -gt 0) {
            try {
                $expires = [DateTime]::FromFileTimeUtc(([long]$record.ExpiresUtc) * 10)
                if ($expires -lt [DateTime]::UtcNow) { continue }
            } catch { }
        }
        if ($hostName -match '(^|\.)instagram\.com$') {
            if ($name -eq 'sessionid' -or $name -eq 'ds_user_id') { $state.Instagram = $true }
        } elseif ($hostName -match '(^|\.)threads\.(com|net)$') {
            if ($name -eq 'sessionid' -or $name -eq 'ds_user_id') { $state.Threads = $true }
        } elseif ($hostName -match '(^|\.)facebook\.com$') {
            if ($name -eq 'c_user') { $fbUser = $true }
            if ($name -eq 'xs') { $fbXs = $true }
        } elseif ($hostName -match '(^|\.)(x|twitter)\.com$') {
            if ($name -eq 'auth_token') { $state.X = $true }
        } elseif ($hostName -match '(^|\.)tiktok\.com$') {
            if ($name -eq 'sessionid' -or $name -eq 'sid_guard') { $state.TikTok = $true }
        }
    }
    $state.Facebook = $fbUser -and $fbXs
    return $state
}

function Get-CookieState {
    if (Initialize-CookieProbe) {
        $probe = Get-CookieRecords
        if ($probe.Ok) {
            return Get-CookieStateFromRecords -Records $probe.Records
        }
    }
    return Get-CookieStateFallback
}

function Test-LoggedIn {
    param([string]$PlatformName = 'Any')
    $state = Get-CookieState
    if ($PlatformName -and $PlatformName -ne 'Any') {
        if (-not $state.Contains($PlatformName)) { return $false }
        return [bool]$state[$PlatformName]
    }
    foreach ($value in $state.Values) {
        if ($value) { return $true }
    }
    return $false
}

# ------------------------------------------------------------------------------
#  Login flow
# ------------------------------------------------------------------------------

function Invoke-Login {
    param([string]$TargetUrl, [string]$PlatformName = 'Auto')

    Import-LegacyProfile

    $browser = Get-BrowserPath
    if (-not (Test-Path -LiteralPath $script:ProfileDir)) {
        [IO.Directory]::CreateDirectory($script:ProfileDir) | Out-Null
    }

    if (@(Get-ProfileBlockingProcesses).Count -gt 0) {
        Write-Log 'A login browser window is already open. Please complete login and close it.'
        return $false
    }
    Stop-ProfileBrowsers
    Start-Sleep -Milliseconds 300

    $targetPlatform = if ($PlatformName -and $PlatformName -ne 'Auto') { $PlatformName } else { Get-UrlPlatform $TargetUrl }

    switch ($targetPlatform) {
        'Instagram' {
            $defaultUrl = 'https://www.instagram.com/accounts/login/'
            $pName = 'Instagram'
        }
        'Threads' {
            $defaultUrl = 'https://www.threads.com/login'
            $pName = 'Threads'
        }
        'Facebook' {
            $defaultUrl = 'https://www.facebook.com/login'
            $pName = 'Facebook'
        }
        'X' {
            $defaultUrl = 'https://x.com/i/flow/login'
            $pName = 'X (Twitter)'
        }
        'TikTok' {
            $defaultUrl = 'https://www.tiktok.com/login'
            $pName = 'TikTok'
        }
        default {
            $defaultUrl = 'https://www.instagram.com/accounts/login/'
            $pName = 'Account'
        }
    }

    $url = $defaultUrl
    if ($TargetUrl -and $TargetUrl -match '^https?://') { $url = $TargetUrl }

    Write-Log ("Browser engine: " + (Split-Path $browser -Leaf))
    Write-Log "Opening window. Please log in to $pName."
    Write-Log 'Once logged in, simply CLOSE the browser window. Session will be saved automatically.'

    $argStr = '--no-first-run --no-default-browser-check --disable-extensions ' +
              '--hide-crash-restore-bubble --disable-session-crashed-bubble ' +
              ('--user-data-dir="' + $script:ProfileDir + '" ') + $url
    $proc = Start-Process -FilePath $browser -ArgumentList $argStr -PassThru

    # Chrome's launcher process may exit before the real browser process is
    # visible in WMI. Give startup a grace period before deciding it failed.
    $started = $false
    $startDeadline = (Get-Date).AddSeconds(12)
    while ((Get-Date) -lt $startDeadline) {
        Start-Sleep -Milliseconds 500
        if (@(Get-ProfileBlockingProcesses).Count -gt 0) { $started = $true; break }
        if ($proc.HasExited) {
            Start-Sleep -Milliseconds 500
            if (@(Get-ProfileBlockingProcesses).Count -gt 0) { $started = $true; break }
        }
    }
    if (-not $started) {
        Write-Log 'Login browser did not start or was closed immediately.'
    }

    $deadline = (Get-Date).AddHours(2)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        if (@(Get-ProfileBlockingProcesses).Count -eq 0) { break }
    }
    if (@(Get-ProfileBlockingProcesses).Count -gt 0) {
        Write-Log 'Login timed out. Closing the login browser.'
        Stop-ProfileBrowsers
        return $false
    }

    Start-Sleep -Milliseconds 600
    Stop-ProfileBrowsers
    Start-Sleep -Milliseconds 400

    # Chrome flushes the cookie database asynchronously on close.
    $logged = $false
    for ($i = 0; $i -lt 30 -and -not $logged; $i++) {
        $logged = Test-LoggedIn -PlatformName $targetPlatform
        if (-not $logged) { Start-Sleep -Milliseconds 500 }
    }

    if ($logged) {
        Write-Log "Login state saved successfully ($pName). Future downloads will reuse this session ✓"
        return $true
    }
    Write-Log "No session detected. If downloading private posts, please log in again."
    return $false
}

# ------------------------------------------------------------------------------
#  CDP plumbing
# ------------------------------------------------------------------------------

function Get-FreePort {
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    return $port
}

function Receive-CdpText {
    param($Ws, [int]$TimeoutMs = 30000)
    $ms = New-Object IO.MemoryStream
    try {
        $buf = New-Object byte[] 131072
        while ($true) {
            $seg = New-Object 'System.ArraySegment[byte]' -ArgumentList @(, $buf)
            $task = $Ws.ReceiveAsync($seg, [Threading.CancellationToken]::None)
            if (-not $task.Wait($TimeoutMs)) { throw 'Browser response timed out' }
            $res = $task.Result
            if ($res.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) { throw 'Browser connection closed' }
            $ms.Write($buf, 0, $res.Count)
            if ($res.EndOfMessage) { break }
        }
        return [Text.Encoding]::UTF8.GetString($ms.ToArray())
    } finally {
        $ms.Dispose()
    }
}

function Invoke-Cdp {
    param($Ws, [string]$Method, $Params)
    $script:CdpId++
    $id = $script:CdpId
    $msg = [ordered]@{ id = $id; method = $Method }
    if ($null -ne $Params) { $msg['params'] = $Params }
    $json = $msg | ConvertTo-Json -Compress -Depth 30
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $seg = New-Object 'System.ArraySegment[byte]' -ArgumentList @(, $bytes)
    $send = $Ws.SendAsync($seg, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None)
    if (-not $send.Wait(15000)) { throw 'Browser send timed out' }

    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline) {
        $obj = (Receive-CdpText -Ws $Ws) | ConvertFrom-Json
        if ($obj.PSObject.Properties['id'] -and $obj.id -eq $id) {
            if ($obj.PSObject.Properties['error'] -and $obj.error) { throw ('CDP Error: ' + $obj.error.message) }
            return $obj.result
        }
    }
    throw 'Browser response timed out'
}

function Invoke-Js {
    param($Ws, [string]$Expression)
    $r = Invoke-Cdp -Ws $Ws -Method 'Runtime.evaluate' -Params @{
        expression    = $Expression
        returnByValue = $true
        awaitPromise  = $true
        userGesture   = $true
    }
    if ($r.PSObject.Properties['exceptionDetails'] -and $r.exceptionDetails) {
        throw ('Script evaluation error: ' + $r.exceptionDetails.text)
    }
    return $r.result.value
}

# ==============================================================================
#  Universal Multi-Platform Media Extractor (ExtractJs)
# ==============================================================================
$script:ExtractJs = @'
(async function () {
  var href = location.href;
  var path = location.pathname;
  var host = (location.hostname || '').toLowerCase();
  var isFacebook = /(^|\.)facebook\.com$/i.test(host) || /(^|\.)fb\.watch$/i.test(host);

  function snapshot() {
    var b = document.body;
    return b ? (b.innerText || '').slice(0, 1500) : '';
  }

  function cleanUrl(u) {
    if (!u || typeof u !== 'string') return '';
    if (u.indexOf('blob:') === 0) return '';
    return u.replace(/\\u0026/g, '&').replace(/&amp;/g, '&').replace(/\\\//g, '/');
  }

  var items = [];
  var seen = {};

  // Shortcode / status id from the URL. Used to ignore recommended posts
  // that are also present in the page hydration JSON.
  var code = 'video';
  var mCode = path.match(/\/(?:post|p|reel|tv|status|video)\/([A-Za-z0-9_-]+)/);
  if (mCode) { code = mCode[1]; }
  else {
    var parts = path.split('/').filter(Boolean);
    if (parts.length) code = parts[parts.length - 1];
  }
  var target = code;

  function addItem(type, url, source) {
    url = cleanUrl(url);
    if (!url || seen[url]) return;
    seen[url] = true;
    items.push({ type: type, url: url, source: source || 'json' });
  }

  function firstUrl(value) {
    if (!value) return '';
    if (typeof value === 'string') return value;
    if (value.url_list && value.url_list.length) return value.url_list[0];
    if (value.urlList && value.urlList.length) return value.urlList[0];
    if (value.url) return value.url;
    if (value.src) return value.src;
    return '';
  }

  // --- 1. Deep Recursive JSON Tree Scanner ---
  function pickLargest(candidates) {
    var best = '', bestPx = -1;
    if (!candidates) return best;
    for (var i = 0; i < candidates.length; i++) {
      var c = candidates[i];
      if (!c) continue;
      var u = c.url || c.src;
      if (!u) continue;
      var px = (c.width || 0) * (c.height || 0);
      if (px > bestPx) { bestPx = px; best = u; }
    }
    return best;
  }

  function bestMp4(variants) {
    var best = '', maxBr = -1;
    if (!variants) return best;
    for (var i = 0; i < variants.length; i++) {
      var v = variants[i];
      if (!v || !v.url) continue;
      var type = v.content_type || v.mime_type || '';
      if (type && type.indexOf('video/mp4') < 0) continue;
      if ((v.bitrate || 0) >= maxBr) { maxBr = v.bitrate || 0; best = v.url; }
    }
    return best;
  }

  // The post id carried by a media node, used to ignore related posts that
  // are hydrated into the same page.
  function nodeMediaId(node) {
    if (node.shortcode) return String(node.shortcode);
    if (node.code && typeof node.code === 'string') return node.code;
    if (node.aweme_id) return String(node.aweme_id);
    if (node.rest_id && (node.extended_entities || node.entities || node.legacy)) return String(node.rest_id);
    if (node.id && (node.video || node.imagePost || node.stats)) return String(node.id);
    return '';
  }

  function parseMediaNode(node) {
    if (!node || typeof node !== 'object') return false;
    var found = false;

    // (A) Meta carousels (Instagram / Threads)
    var carousel = node.carousel_media ||
      (node.edge_sidecar_to_children && node.edge_sidecar_to_children.edges);
    if (carousel && carousel.length) {
      for (var i = 0; i < carousel.length; i++) {
        var sub = carousel[i].node ? carousel[i].node : carousel[i];
        if (parseMediaNode(sub)) found = true;
      }
      if (found) return true;
    }

    // (B) X / Twitter: every photo, or the highest-bitrate MP4
    var legacy = node.legacy || node;
    var xMedia = (node.extended_entities && node.extended_entities.media) ||
                 (node.entities && node.entities.media) ||
                 (legacy.extended_entities && legacy.extended_entities.media) ||
                 (legacy.entities && legacy.entities.media);
    if (xMedia && xMedia.length) {
      for (var x = 0; x < xMedia.length; x++) {
        var m = xMedia[x];
        if (!m) continue;
        if (m.video_info && m.video_info.variants) {
          var mp4 = bestMp4(m.video_info.variants);
          if (mp4) { addItem('video', mp4); found = true; }
        } else {
          var photo = m.media_url_https || m.media_url;
          if (photo) { addItem('image', photo); found = true; }
        }
      }
      if (found) return true;
    }

    // (C) TikTok video: prefer the highest bitrate, then fall back to the
    // progressive download/play addresses.
    var ttVideo = node.video || node;
    var ttUrl = '';
    if (ttVideo.bitrateInfo && ttVideo.bitrateInfo.length) {
      var bestTt = '', bestTtBr = -1;
      for (var b = 0; b < ttVideo.bitrateInfo.length; b++) {
        var info = ttVideo.bitrateInfo[b];
        var addr = info && info.PlayAddr && info.PlayAddr.UrlList && info.PlayAddr.UrlList[0];
        if (addr && (info.Bitrate || 0) >= bestTtBr) { bestTtBr = info.Bitrate || 0; bestTt = addr; }
      }
      ttUrl = bestTt;
    }
    if (!ttUrl) {
      ttUrl = ttVideo.downloadAddr || ttVideo.playAddr ||
              ttVideo.download_addr || ttVideo.play_addr ||
              node.downloadAddr || node.playAddr;
    }
    if (!ttUrl && ttVideo.PlayAddr && ttVideo.PlayAddr.UrlList && ttVideo.PlayAddr.UrlList.length) {
      ttUrl = ttVideo.PlayAddr.UrlList[0];
    }
    if (ttUrl) { addItem('video', ttUrl); found = true; }

    // (D) TikTok photo slideshow
    if (node.imagePost && node.imagePost.images && node.imagePost.images.length) {
      for (var t = 0; t < node.imagePost.images.length; t++) {
        var img = node.imagePost.images[t];
        var imgUrl = img && (firstUrl(img.imageURL) || firstUrl(img.displayImage) || firstUrl(img.thumbnail));
        if (imgUrl) { addItem('image', imgUrl); found = true; }
      }
    }
    if (!found && node.imagePost && node.imagePost.cover) {
      var coverUrl = firstUrl(node.imagePost.cover);
      if (coverUrl) { addItem('image', coverUrl); found = true; }
    }

    // (E) Facebook progressive / HD video
    var fbUrl = node.playable_url_quality_hd || node.browser_native_hd_url ||
                node.playable_url || node.browser_native_sd_url ||
                node.hd_src || node.sd_src;
    if (fbUrl) { addItem('video', fbUrl); found = true; }

    // (F) Instagram / Threads progressive MP4
    if (node.video_versions && node.video_versions.length) {
      var bestV = '', maxPx = -1;
      for (var v = 0; v < node.video_versions.length; v++) {
        var cand = node.video_versions[v];
        if (!cand || !cand.url) continue;
        var px = (cand.width || 0) * (cand.height || 0);
        if (px > maxPx) { maxPx = px; bestV = cand.url; }
      }
      if (bestV) { addItem('video', bestV); found = true; }
    } else if (node.video_url) {
      addItem('video', node.video_url);
      found = true;
    }

    // (G) Images: pick the largest candidate, never just the first one.
    if (!found) {
      var bestImg = pickLargest(node.image_versions2 && node.image_versions2.candidates);
      if (!bestImg && node.image_versions && node.image_versions.candidates) {
        bestImg = pickLargest(node.image_versions.candidates);
      }
      if (!bestImg && node.display_url) bestImg = node.display_url;
      if (!bestImg && node.display_resources) bestImg = pickLargest(node.display_resources);
      if (bestImg) { addItem('image', bestImg); found = true; }
    }
    return found;
  }

  function isMediaNode(node) {
    return !!(node && typeof node === 'object' && (
      node.video_versions || node.video_url || node.carousel_media ||
      node.video_info || node.playable_url || node.playable_url_quality_hd ||
      node.browser_native_hd_url || node.browser_native_sd_url ||
      node.downloadAddr || node.playAddr || node.imagePost ||
      node.image_versions2 || node.image_versions ||
      (node.video && (node.video.downloadAddr || node.video.playAddr || node.video.bitrateInfo)) ||
      (node.legacy && (node.legacy.extended_entities || node.legacy.entities)) ||
      (node.edge_sidecar_to_children && node.edge_sidecar_to_children.edges) ||
      (node.extended_entities && node.extended_entities.media)
    ));
  }

  function searchJson(obj, depth) {
    if (!obj || typeof obj !== 'object' || depth > 32) return;
    if (Array.isArray(obj)) {
      for (var i = 0; i < obj.length; i++) searchJson(obj[i], depth + 1);
      return;
    }

    if (isMediaNode(obj)) {
      // Facebook video ids are not guaranteed to equal the reel id, so the
      // id filter would hide the actual video. Other platforms use stable
      // post ids and benefit from filtering recommended posts.
      if (!isFacebook) {
        var id = nodeMediaId(obj);
        if (id && target && id !== target) return;
      }
      parseMediaNode(obj);
    }

    for (var k in obj) {
      if (Object.prototype.hasOwnProperty.call(obj, k)) {
        var val = obj[k];
        if (val && typeof val === 'object') searchJson(val, depth + 1);
      }
    }
  }

  // Scan embedded JSON hydration scripts
  var scripts = document.querySelectorAll('script[type="application/json"], script[type="application/ld+json"], script[id*="STATE"], script[id*="DATA"], script[data-sjs]');
  for (var s = 0; s < scripts.length; s++) {
    var txt = scripts[s].textContent || '';
    if (txt.indexOf('video') >= 0 || txt.indexOf('play') >= 0 || txt.indexOf('media') >= 0 ||
        txt.indexOf('image') >= 0 || txt.indexOf('photo') >= 0 || txt.indexOf('display') >= 0 ||
        txt.indexOf('urlList') >= 0) {
      try {
        var parsed = JSON.parse(txt);
        searchJson(parsed, 0);
      } catch (e) { }
    }
  }

  // --- 2. OpenGraph / Twitter Stream Meta Tag Fallback ---
  if (!items.length) {
    var metaSelectors = [
      'meta[property="og:video"]',
      'meta[property="og:video:url"]',
      'meta[property="og:video:secure_url"]',
      'meta[name="twitter:player:stream"]'
    ];
    for (var mIdx = 0; mIdx < metaSelectors.length; mIdx++) {
      var metaEl = document.querySelector(metaSelectors[mIdx]);
      if (metaEl && metaEl.content) { addItem('video', metaEl.content); break; }
    }
  }

  // --- 3. DOM <video> Element Fallback ---
  function checkDomVideo() {
    var vids = document.querySelectorAll('video');
    for (var i = 0; i < vids.length; i++) {
      var v = vids[i];
      var u = v.currentSrc || v.src || (v.querySelector('source') ? v.querySelector('source').src : '');
      if (u && !u.startsWith('blob:')) addItem('video', u, 'dom');
    }
  }
  if (!items.length) checkDomVideo();

  // --- 4. MP4 resource fallback (player fetched a stream not present in JSON) ---
  if (!items.length && window.performance && performance.getEntriesByType) {
    var entries = performance.getEntriesByType('resource');
    for (var r = 0; r < entries.length; r++) {
      var ru = entries[r].name || '';
      if (!/\.mp4(\?|$)/i.test(ru) || ru.indexOf('blob:') === 0) continue;
      var sameHost = false;
      try { sameHost = (new URL(ru, location.href)).host === location.host; } catch (e) { }
      if (entries[r].initiatorType === 'video' || sameHost) addItem('video', ru, 'resource');
    }
  }

  // --- 5. OpenGraph Image Fallback (strictly for photo posts, NEVER for video/reel URLs) ---
  var isVideoUrl = /(?:reel|video|watch|tv)/i.test(path);
  function plausibleImage(u) {
    if (!u) return false;
    if (u.indexOf('data:') === 0 || u.indexOf('blob:') === 0) return false;
    if (/\.svg(\?|$)/i.test(u)) return false;
    if (/(favicon|sprite|logo|placeholder|profile_pic|default_profile|anonymous_user)/i.test(u)) return false;
    return true;
  }
  if (!items.length && !isVideoUrl) {
    var metaImg = document.querySelector('meta[property="og:image"]');
    if (metaImg && plausibleImage(metaImg.content)) addItem('image', metaImg.content, 'og');
  }

  // If this was a video/reel URL and we found video(s), strip any accidental image items
  if (isVideoUrl && items.some(function (it) { return it.type === 'video'; })) {
    items = items.filter(function (it) { return it.type === 'video'; });
  }

  // --- 6. Author Extraction ---
  var author = 'user';

  var mAuth = path.match(/\/@([A-Za-z0-9_.]+)/) || path.match(/\/([A-Za-z0-9_.]+)\/status\//);
  if (mAuth) { author = mAuth[1]; }
  else {
    var tMatch = document.title.match(/@([A-Za-z0-9_.]+)/) || document.title.match(/^([A-Za-z0-9_.]+) on /);
    if (tMatch) { author = tMatch[1]; }
  }

  return {
    code: code,
    author: author,
    count: items.length,
    items: items,
    text: snapshot(),
    href: href
  };
})()
'@

# Probe Script for Authentication Detection
$script:ProbeJs = @'
(function () {
  var href = location.href;
  var host = (location.hostname || '').toLowerCase();
  var path = location.pathname;
  var body = document.body ? (document.body.innerText || '') : '';

  function visible(el) {
    return !!(el && (el.offsetWidth || el.offsetHeight || (el.getClientRects && el.getClientRects().length)));
  }

  var loginReq = false;

  if (/(^|\.)instagram\.com$/.test(host)) {
    if (path.indexOf('/accounts/login') >= 0 || visible(document.querySelector('input[name="username"]'))) {
      loginReq = true;
    }
  } else if (/(^|\.)threads\.(com|net)$/.test(host)) {
    if (path.indexOf('/login') >= 0 || visible(document.querySelector('input[name="username"]'))) {
      loginReq = true;
    }
  } else if (/(^|\.)facebook\.com$/.test(host) || /(^|\.)fb\.watch$/.test(host)) {
    if (path.indexOf('/login') >= 0 || !!document.querySelector('form#login_form') ||
        body.indexOf('你必須先登入') >= 0 || body.indexOf('Log in to Facebook') >= 0) {
      loginReq = true;
    }
  } else if (/(^|\.)(x|twitter)\.com$/.test(host)) {
    if (path.indexOf('/login') >= 0 || path.indexOf('/i/flow/login') >= 0 ||
        visible(document.querySelector('input[autocomplete="username"]'))) {
      loginReq = true;
    }
  } else if (/(^|\.)tiktok\.com$/.test(host)) {
    if (path.indexOf('/login') >= 0 || visible(document.querySelector('input[type="password"]'))) {
      loginReq = true;
    }
  }

  return {
    ready: document.readyState,
    videos: document.querySelectorAll('video').length,
    scripts: document.querySelectorAll('script').length,
    loginRequired: loginReq
  };
})()
'@

# ==============================================================================
#  Download orchestration
# ==============================================================================
function Invoke-Download {
    param([string]$TargetUrl, [string]$OutDir)

    if (-not ($TargetUrl -match '^https?://')) {
        throw 'Only http:// and https:// URLs are supported.'
    }

    Import-LegacyProfile

    if (-not $OutDir) {
        $OutDir = Join-Path (Get-DownloadsFolder) 'ReelsDL'
    }

    $platformName = Get-UrlPlatform -TargetUrl $TargetUrl
    $browser = Get-BrowserPath
    $port = Get-FreePort
    $profile = $script:ProfileDir
    $proc = $null
    $ws = $null

    # Only a visible login window blocks a download. Leftover headless
    # browsers from a crashed job are cleaned up instead of deadlocking.
    if (@(Get-ProfileBlockingProcesses).Count -gt 0) {
        throw 'A login browser window is currently open. Please close it first.'
    }
    Stop-ProfileBrowsers
    if (-not (Test-Path -LiteralPath $profile)) {
        [IO.Directory]::CreateDirectory($profile) | Out-Null
    }
    $hadLogin = Test-LoggedIn -PlatformName $platformName

    try {
        [IO.Directory]::CreateDirectory($OutDir) | Out-Null

        Write-Evt 'stage' @('starting', "Preparing browser for $platformName...")
        Write-Log ("Platform: " + $platformName)
        Write-Log ("Browser Engine: " + (Split-Path $browser -Leaf))
        if ($hadLogin) {
            Write-Log "Auth Status: Logged in ($platformName) ✓"
        } else {
            Write-Log "Auth Status: Guest mode (For private or restricted posts, please log in first)"
        }

        $ua = Get-BrowserUserAgent -BrowserPath $browser
        $argStr = '--headless=new --disable-gpu --no-first-run --no-default-browser-check ' +
                  '--mute-audio --hide-scrollbars --disable-extensions ' +
                  '--disable-blink-features=AutomationControlled ' +
                  ("--user-agent=`"$ua`" ") +
                  ("--remote-debugging-port=$port --remote-allow-origins=http://127.0.0.1:$port --window-size=1280,1800 ") +
                  ('--user-data-dir="' + $profile + '" about:blank')
        $proc = Start-Process -FilePath $browser -ArgumentList $argStr -PassThru -WindowStyle Hidden

        $page = $null
        for ($i = 0; $i -lt 100 -and -not $page; $i++) {
            try {
                $raw = Invoke-RestMethod ("http://127.0.0.1:$port/json/list") -TimeoutSec 2
                $list = @()
                foreach ($item in @($raw)) {
                    if ($item -is [System.Array]) { foreach ($sub in $item) { if ($sub) { $list += $sub } } }
                    elseif ($item) { $list += $item }
                }
                foreach ($t in $list) {
                    if ($t.type -eq 'page' -and $t.url -eq 'about:blank') { $page = $t; break }
                }
                if (-not $page) {
                    foreach ($t in $list) { if ($t.type -eq 'page') { $page = $t; break } }
                }
            } catch { }
            if (-not $page) { Start-Sleep -Milliseconds 200 }
        }
        if (-not $page) { throw 'Unable to initialize headless browser page.' }

        $ws = New-Object System.Net.WebSockets.ClientWebSocket
        $ws.ConnectAsync([Uri]$page.webSocketDebuggerUrl, [Threading.CancellationToken]::None).Wait(15000) | Out-Null
        Invoke-Cdp -Ws $ws -Method 'Page.enable' -Params $null | Out-Null
        Invoke-Cdp -Ws $ws -Method 'Runtime.enable' -Params $null | Out-Null

        # Inject stealth anti-detection script
        Invoke-Cdp -Ws $ws -Method 'Page.addScriptToEvaluateOnNewDocument' -Params @{
            source = @'
Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
window.chrome = { runtime: {} };
'@
        } | Out-Null

        Write-Evt 'stage' @('navigating', 'Connecting to page...')
        Write-Log 'Navigating to page...'
        Invoke-Cdp -Ws $ws -Method 'Page.navigate' -Params @{ url = $TargetUrl } | Out-Null

        $sw = [Diagnostics.Stopwatch]::StartNew()
        $ready = $false
        $loginDetected = $false
        while ($sw.Elapsed.TotalSeconds -lt 40) {
            Start-Sleep -Milliseconds 800
            try { $p = Invoke-Js -Ws $ws -Expression $script:ProbeJs } catch { continue }
            if ($p) {
                # A login modal over a playable public video is not a wall.
                if ($p.loginRequired -and $p.videos -eq 0) {
                    $loginDetected = $true
                    break
                }
                if ($p.videos -gt 0 -or $p.scripts -gt 25) {
                    $ready = $true
                    Start-Sleep -Milliseconds 1200
                    break
                }
            }
        }

        if ($loginDetected) {
            throw "This $platformName post requires login. Please click 'Account Login' to authenticate, then retry."
        }

        Write-Evt 'stage' @('extracting', 'Resolving media streams...')
        if (-not $ready) { Write-Log 'Page loaded. Extracting media...' }

        $data = $null
        $items = @()

        # Hydration is asynchronous: retry extraction briefly and nudge lazy
        # players by scrolling before declaring the post empty.
        for ($attempt = 1; $attempt -le 6; $attempt++) {
            try { $data = Invoke-Js -Ws $ws -Expression $script:ExtractJs } catch { $data = $null }
            if ($data -and $data.items) { $items = @($data.items) }
            if ($items.Count -gt 0) { break }
            if ($attempt -lt 6) {
                Start-Sleep -Milliseconds 1500
                try {
                    Invoke-Js -Ws $ws -Expression "window.scrollTo(0, document.body.scrollHeight); document.querySelectorAll('video').length" | Out-Null
                } catch { }
            }
        }

        if ($items.Count -eq 0) {
            $pageText = if ($data -and $data.text) { [string]$data.text } else { '' }
            $href = if ($data -and $data.href) { [string]$data.href } else { '' }

            $loginUrl = '/(login|accounts)(/|$)|/i/flow/login'
            $loginText = 'Log in to continue|Sign in to continue|登入後繼續|請先登入|你必須先登入'
            if ($href -match $loginUrl -or $pageText -match $loginText) {
                if ($hadLogin) {
                    throw "Unable to access this $platformName post (may be private and un-followed, or session expired)."
                }
                throw "This $platformName post requires login. Please click 'Account Login', authenticate, then retry."
            }
            throw "No downloadable video or photo found (the post may have been removed or the URL is invalid)."
        }

        $author = if ($data.author) { $data.author } else { 'user' }
        $code = if ($data.code) { $data.code } else { 'video' }

        Write-Evt 'meta' @($platformName, $author, $code, $items.Count)
        Write-Log ("Found " + $items.Count + " media item(s). Starting download...")
        $n = 0
        $saved = @()

        foreach ($it in $items) {
            $n++
            $type = if ($it.type -eq 'video') { 'video' } else { 'image' }
            $defaultExt = if ($type -eq 'video') { '.mp4' } else { '.jpg' }
            $suffix = if ($items.Count -gt 1) { "_$n" } else { '' }
            $baseName = Get-SafeBaseName -Name ("${platformName}_${author}_${code}${suffix}")

            $path = Get-UniqueFilePath -Path (Join-Path $OutDir ($baseName + $defaultExt))
            $name = Split-Path -Leaf $path
            Write-Evt 'item' @($n, $items.Count, $name, $it.type)
            Write-Log ("  ($n/$($items.Count)) $name")

            $ok = $false
            $maxAttempts = 3
            for ($try = 1; $try -le $maxAttempts -and -not $ok; $try++) {
                try {
                    $res = Save-RemoteFile -Url $it.url -Path $path -Referer $TargetUrl `
                        -UserAgent $ua -Index $n -Count $items.Count -ExpectedType $type

                    # Trust the server's Content-Type for the final extension
                    # (e.g. TikTok/Threads may return WebM or JPEG).
                    $ext = Get-MediaExtension -ContentType $res.ContentType -DefaultExtension $defaultExt
                    if ($ext -ne $defaultExt) {
                        $finalPath = Get-UniqueFilePath -Path (Join-Path $OutDir ($baseName + $ext))
                        Move-Item -LiteralPath $path -Destination $finalPath -Force
                        $path = $finalPath
                        $name = Split-Path -Leaf $path
                    }
                    $ok = $true
                } catch {
                    if ($try -ge $maxAttempts) {
                        Write-Log ("    Failed: " + $_.Exception.Message)
                    } else {
                        $delay = Get-RetryDelay -Failure $_ -Attempt $try
                        Write-Log ("    Retrying in ${delay}s: " + $_.Exception.Message)
                        Start-Sleep -Seconds $delay
                    }
                }
            }
            if ($ok) {
                $size = [math]::Round((Get-Item -LiteralPath $path).Length / 1MB, 2)
                Write-Log ("    OK  $size MB")
                Write-Evt 'saved' @($n, $items.Count, $name, $size, $path, $type)
                $saved += $path
            }
        }

        if ($saved.Count -eq 0) { throw 'All downloads failed (URL stream may have expired).' }
        Write-Evt 'done' @($saved.Count, $OutDir)
        Write-Log ("Done! Successfully downloaded " + $saved.Count + " file(s).")
        Write-Log ("Directory: " + $OutDir)
    }
    finally {
        if ($ws) { try { $ws.Dispose() } catch { } }
        # Kill every browser process using this profile, not just the
        # launcher PID, so no headless children are left behind.
        Stop-ProfileBrowsers
        if ($proc) { try { $proc.Dispose() } catch { } }
        Start-Sleep -Milliseconds 400
    }
}

# ------------------------------------------------------------------------------
#  Self-test / usage
# ------------------------------------------------------------------------------

function Invoke-SelfTest {
    $script:SelfTestFailures = 0
    $script:SelfTestChecks = 0

    function Assert-True {
        param([bool]$Condition, [string]$Name)
        $script:SelfTestChecks++
        if (-not $Condition) {
            $script:SelfTestFailures++
            Write-Host ("FAIL: " + $Name)
        }
    }

    Assert-True ((Get-MediaExtension -ContentType 'video/webm' -DefaultExtension '.mp4') -eq '.webm') 'webm extension'
    Assert-True ((Get-MediaExtension -ContentType 'video/quicktime; codecs=avc1' -DefaultExtension '.mp4') -eq '.mov') 'quicktime extension'
    Assert-True ((Get-MediaExtension -ContentType 'video/x-matroska' -DefaultExtension '.mp4') -eq '.mkv') 'matroska extension'
    Assert-True ((Get-MediaExtension -ContentType 'image/jpeg' -DefaultExtension '.jpg') -eq '.jpg') 'jpeg extension'
    Assert-True ((Get-MediaExtension -ContentType 'application/octet-stream' -DefaultExtension '.mp4') -eq '.mp4') 'octet-stream fallback'

    Assert-True (Test-UrlHost 'https://www.instagram.com/reel/x' 'instagram.com') 'host subdomain'
    Assert-True (-not (Test-UrlHost 'https://evilinstagram.com/x' 'instagram.com')) 'host suffix safety'
    Assert-True (-not (Test-UrlHost 'https://instagram.com.evil.com/x' 'instagram.com')) 'host dot safety'
    Assert-True ((Get-UrlPlatform 'https://vt.tiktok.com/abc') -eq 'TikTok') 'tiktok short link'

    Assert-True (Test-CookieToken 'xx sessionid yy' 'sessionid') 'cookie token'
    Assert-True (-not (Test-CookieToken 'sessionid_extra' 'sessionid')) 'cookie token boundary'

    $invalid = Get-SafeBaseName -Name 'a:b*c?.mp4'
    Assert-True ($invalid -notmatch '[\\/:*?"<>|]') 'filename sanitizing'
    Assert-True ((Get-SafeBaseName -Name 'CON') -ne 'CON') 'reserved filename'
    Assert-True ((Get-SafeBaseName -Name ('x' * 300)).Length -le 120) 'filename length'

    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('reelsdl_test_' + [guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($tmp) | Out-Null
    try {
        $first = Join-Path $tmp 'a.mp4'
        [IO.File]::WriteAllText($first, 'x')
        Assert-True ((Get-UniqueFilePath -Path $first) -eq (Join-Path $tmp 'a (1).mp4')) 'unique filename'
    } finally {
        Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }

    Assert-True ([bool](Get-DownloadsFolder)) 'downloads folder'

    Assert-True (Initialize-CookieProbe) ('cookie probe compiles: ' + $script:CookieProbeError)
    $state = Get-CookieState
    Assert-True ($state.Keys.Count -eq 5) 'cookie state has five platforms'
    Assert-True ($state.Contains('Instagram')) 'cookie state keys'

    # Exact regression this probe was written for: a TikTok sessionid must
    # not be reported as an Instagram login just because instagram.com
    # cookies (csrftoken) exist in the same database.
    if ($script:CookieProbeReady) {
        $db = Join-Path ([IO.Path]::GetTempPath()) ('reelsdl_cookie_test_' + [guid]::NewGuid().ToString('N') + '.db')
        try {
            [CookieProbe]::Exec($db, "CREATE TABLE cookies (host_key TEXT, name TEXT, expires_utc INTEGER, has_expires INTEGER); INSERT INTO cookies VALUES ('.instagram.com','csrftoken',0,0); INSERT INTO cookies VALUES ('.tiktok.com','sessionid',0,0);")
            $synthetic = Get-CookieStateFromRecords -Records ([CookieProbe]::Read($db))
            Assert-True (-not $synthetic.Instagram) 'tiktok sessionid is not instagram'
            Assert-True ($synthetic.TikTok) 'tiktok sessionid detected'
        } catch {
            $ex = $_.Exception
            while ($ex.InnerException) { $ex = $ex.InnerException }
            if ($ex -is [System.DllNotFoundException]) {
                # The runtime has a file-level fallback; do not fail the build
                # on systems that do not ship winsqlite3.dll.
                Write-Host 'Skipping synthetic cookie test: winsqlite3.dll is not available.'
            } else {
                Assert-True $false ('synthetic cookie test: ' + $ex.Message)
            }
        } finally {
            Remove-Item -LiteralPath $db -Force -ErrorAction SilentlyContinue
        }
    }

    if ($script:SelfTestFailures -gt 0) {
        Write-Host ("Self-test failed: {0} of {1} checks failed." -f $script:SelfTestFailures, $script:SelfTestChecks)
        return $false
    }
    Write-Host ("Self-test passed ({0} checks)." -f $script:SelfTestChecks)
    return $true
}

function Show-Usage {
    Write-Host 'ReelsDL engine (console only)'
    Write-Host ''
    Write-Host 'Usage:'
    Write-Host '  powershell -File ReelsDL.ps1 -Cli -Url <url> [-Out <dir>] [-Log <file>]'
    Write-Host '  powershell -File ReelsDL.ps1 -Login [-Platform <name>] [-Url <url>] [-Log <file>]'
    Write-Host '  powershell -File ReelsDL.ps1 -CookieState'
    Write-Host '  powershell -File ReelsDL.ps1 -SelfTest'
    Write-Host ''
    Write-Host 'Exit codes: 0 success, 1 error, 2 login cancelled, 3 login required.'
}

# ------------------------------------------------------------------------------
#  Entry point
# ------------------------------------------------------------------------------

if ($Help) {
    Show-Usage
    exit 0
}

if ($SelfTest) {
    if (Invoke-SelfTest) { exit 0 }
    exit 1
}

if ($CookieState) {
    try {
        $state = Get-CookieState
        foreach ($key in $state.Keys) {
            [Console]::Out.WriteLine(('{0}={1}' -f $key, $(if ($state[$key]) { 1 } else { 0 })))
        }
        exit 0
    } catch {
        exit 1
    }
}

if ($Login) {
    try {
        if (Invoke-Login -TargetUrl $Url -PlatformName $Platform) { exit 0 }
        exit 2
    } catch {
        Write-Log ('Error: ' + $_.Exception.Message)
        exit 1
    }
}

if ($Cli) {
    try {
        Invoke-Download -TargetUrl $Url -OutDir $Out
        exit 0
    } catch {
        $msg = $_.Exception.Message
        Write-Log ('Error: ' + $msg)
        if ($msg -match 'requires login|請先按「登入」') { exit 3 }
        exit 1
    }
}

Show-Usage
exit 1
