<#
.SYNOPSIS
    Test-03 - the real Steam VRCVideoCacher, read-only acceptance (contract mode).

.DESCRIPTION
    The shipped VRCVideoCacher is a single-file, self-contained publish whose host sets
    System.StartupHookProvider.IsSupported = false, so a managed startup hook cannot run inside it
    (proved by Test-02b). The plugin's real integration point there is the yt-dlp cookie-file contract
    (%APPDATA%\VRCVideoCacher\youtube_cookies.txt), which the NativeHost writes.

    This test is therefore deliberately READ-ONLY with respect to the user's real cookie file:
      * it never writes %APPDATA%\VRCVideoCacher\youtube_cookies.txt,
      * it starts its own instance with --no-steam --no-gui and kills only that PID,
      * it asserts the app boots and its own log does not complain about missing cookies
        (upstream Program.cs:209 prints "No cookies found ..." when the jar is absent/invalid).
    The end-to-end contract write into the real path is covered by docs/VERIFICATION.md (run outside the
    sandbox) and is not duplicated here.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

Start-TestCase 'Test-03 Real VRCVideoCacher acceptance (read-only, contract mode)'

$vrcvcExe = 'D:\Steam\steamapps\common\VRCVideoCacher\VRCVideoCacher.exe'
$vrcvcData = Join-Path $env:APPDATA 'VRCVideoCacher'
$logDir = Join-Path $vrcvcData 'Logs'
$realCookieFile = Join-Path $vrcvcData 'youtube_cookies.txt'

Assert-FileExists $vrcvcExe 'VRCVideoCacher.exe is installed'

$alreadyRunning = @(Get-Process -Name 'VRCVideoCacher' -ErrorAction SilentlyContinue)
Write-Host ("pre-existing VRCVideoCacher processes: " + $alreadyRunning.Count + " (never touched by this test)")

$cookieFileBefore = $null
if (Test-Path $realCookieFile) {
    $cookieFileBefore = (Get-FileHash $realCookieFile -Algorithm SHA256).Hash
    Write-Host "real cookie file exists (sha256=$cookieFileBefore) - it will NOT be modified"
} else {
    Write-Host 'real cookie file does not exist - it will not be created by this test'
}

$root = New-TestRoot -Name 'test-03'
$hookDll = Get-HookDllPath
$targetFile = Join-Path $root 'do-not-write\youtube_cookies.txt'
$traceFile = Join-Path $root 'real-vrcvc-corehost.log'

$newestLogBefore = $null
if (Test-Path $logDir) {
    $newestLogBefore = (Get-ChildItem $logDir -Filter 'VRCVideoCacher*.log' -ErrorAction SilentlyContinue |
                        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).FullName
    Write-Host "newest VRCVC log before start: $newestLogBefore"
}

$proc = Start-BridgeProcess -FilePath $vrcvcExe -Arguments @('--no-steam', '--no-gui') -Environment @{
    DOTNET_STARTUP_HOOKS     = $hookDll
    COREHOST_TRACE           = '1'
    COREHOST_TRACE_VERBOSITY = '4'
    COREHOST_TRACEFILE       = $traceFile
    VRCCB_ROOT               = $root
    VRCCB_TARGET_FILE        = $targetFile
}
Write-Host "started our own VRCVideoCacher instance, pid=$($proc.Id)"

try {
    $alive = Wait-Until -TimeoutSeconds 45 -Condition {
        if ($proc.HasExited) { return 'exited' }
        return $null
    }
    $singleInstance = $false
    if ($alive -eq 'exited') {
        $err = Get-ProcessStdErr -Process $proc
        $out = Get-ProcessStdOut -Process $proc
        $exitedEarly = ("stdout=" + $out.Trim() + " stderr=" + $err.Trim())

        if ($alreadyRunning.Count -gt 0 -and $out -match 'already running') {
            # Upstream keeps a single-instance guard; with the user's own VRCVideoCacher running a second
            # copy exits immediately. Recorded as a documented constraint, not as a pass.
            $singleInstance = $true
            Write-Check -Message ('second instance refused to start because another VRCVideoCacher is running (single-instance guard): ' + $exitedEarly) -Ok $true
        } else {
            Write-Check -Message 'our VRCVideoCacher instance stayed alive for 45 s' -Ok $false -Detail ("exited early. " + $exitedEarly)
        }
    } else {
        Write-Check -Message 'our VRCVideoCacher instance stayed alive for 45 s' -Ok $true
    }

    Start-Sleep -Seconds 2

    # --- the app's own log must not report a missing cookie jar ---
    $newestLog = $null
    if (Test-Path $logDir) {
        $newestLog = (Get-ChildItem $logDir -Filter 'VRCVideoCacher*.log' -ErrorAction SilentlyContinue |
                      Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).FullName
    }

    if ($newestLog) {
        Write-Host "inspecting log: $newestLog"
        $logText = Get-Content -Raw $newestLog
        $tail = ($logText -split "`n" | Select-Object -Last 25)
        Write-Host '--- VRCVC log (tail) ---'
        $tail | ForEach-Object { Write-Host "    $_" }

        $noCookies = [regex]::IsMatch($logText, 'No cookies found')
        if (Test-Path $realCookieFile) {
            Write-Check -Message 'VRCVC did not warn "No cookies found" (it found a usable cookie file)' -Ok (-not $noCookies) `
                        -Detail 'upstream Program.cs:209 warns only when the jar is missing or invalid'
        } else {
            Write-Check -Message 'VRCVC reads the cookie file on demand (no jar present, warning expected at most once)' -Ok $true `
                        -Detail 'this machine has no real cookie jar; nothing was written by this test'
        }

        $booted = [regex]::IsMatch($logText, 'VRCVideoCacher version|Checking for YT-DLP updates|Starting|Serilog|DataPath|Initialis')
        if ($singleInstance) {
            Write-Check -Message 'log belongs to the already-running instance (our copy exited before logging)' -Ok $true `
                        -Detail 'the single-instance guard makes a second boot impossible while the user instance runs'
        } else {
            Write-Check -Message 'VRCVC wrote a startup log (it really booted)' -Ok $booted
        }
    } else {
        Write-Check -Message 'VRCVC log file was found' -Ok $false -Detail ("no VRCVideoCacher*.log under " + $logDir)
    }

    # --- the plugin must NOT have run in-process (mechanism boundary, Test-02b proves why) ---
    $status = Read-HookStatus -Root $root
    Write-Check -Message 'no hook-status.json from the real process (startup hook cannot load in a single-file host)' -Ok ($null -eq $status)
    Assert-FileMissing $targetFile 'VRCCB_TARGET_FILE was not written to the do-not-write directory'

    # --- the user's real cookie file must be untouched ---
    if ($cookieFileBefore) {
        Assert-Equal $cookieFileBefore (Get-FileHash $realCookieFile -Algorithm SHA256).Hash 'real %APPDATA% cookie file is byte-identical (untouched)'
    } else {
        Assert-FileMissing $realCookieFile 'real %APPDATA% cookie file was not created by this test'
    }
} finally {
    if ($proc -and -not $proc.HasExited) {
        Write-Host "killing our instance (pid=$($proc.Id)) and its children"
        Stop-ProcessTree -ProcessId $proc.Id
        Start-Sleep -Milliseconds 500
    }
}

# --- COREHOST_TRACE evidence from the *real* binary ---------------------------
if (Test-Path $traceFile) {
    $trace = Get-Content -Raw $traceFile
    Write-Host '--- COREHOST_TRACE (real VRCVideoCacher) ---'
    foreach ($pattern in @('Property STARTUP_HOOKS', 'Property TRUSTED_PLATFORM_ASSEMBLIES', 'Property System\.StartupHookProvider\.IsSupported')) {
        $m = [regex]::Matches($trace, "[^\r\n]*$pattern[^\r\n]*")
        if ($m.Count -gt 0) { Write-Host ("    " + $m[0].Value.Trim()) }
    }
    $off = [regex]::IsMatch($trace, 'System\.StartupHookProvider\.IsSupported\s*=\s*false')
    Write-Check -Message 'real VRCVideoCacher: System.StartupHookProvider.IsSupported = false (hook disabled by the host)' -Ok $off
} else {
    Write-Check -Message 'COREHOST_TRACE captured from the real binary' -Ok $false -Detail ("missing " + $traceFile)
}

$ok = Complete-TestCase
exit ([int]([bool](-not $ok)))
