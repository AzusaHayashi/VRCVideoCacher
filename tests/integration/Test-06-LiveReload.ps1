<#
.SYNOPSIS
    Test-06 - live reload: a new vault update reaches the host cookie file without restarting the host.

.DESCRIPTION
    The mock host keeps running with the hook injected. While it runs, a second `cookie.update` with a
    different number of cookies is delivered through the real NativeHost. The plugin watches the vault
    (FileSystemWatcher + 1 s debounce) and must rewrite the host's cookie file within a few seconds.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

Start-TestCase 'Test-06 Live reload without restarting the host'

$root = New-TestRoot -Name 'test-06'
$targetFile = Join-Path $root 'vrcvc-data\youtube_cookies.txt'
$hostExe = Get-MockHostExe -Flavor 'fd'
$hookDll = Get-HookDllPath
$nativeHost = Get-NativeHostPath

$env:VRCCB_ROOT = $root
$env:VRCCB_TARGET_FILE = $targetFile
$env:VRCCB_VRCVC_DATA = (Join-Path $root 'vrcvc-data')

$firstJar = New-TestCookieJar -ExtraCookies 3          # LOGIN_INFO + SID + HSID + 3 = 6
$secondJar = New-TestCookieJar -ExtraCookies 7         # 10 cookies

$seed = Invoke-NativeMessage -HostExe $nativeHost -Json (New-CookieUpdateJson -Cookies $firstJar) -Environment @{
    VRCCB_ROOT = $root; VRCCB_TARGET_FILE = $targetFile
    VRCCB_VRCVC_DATA = (Join-Path $root 'vrcvc-data'); DOTNET_STARTUP_HOOKS = ''
}
Assert-Equal 6 ($seed.Reply | ConvertFrom-Json).cookieCount 'first jar delivered (6 cookies)'

New-Item -ItemType Directory -Path (Split-Path -Parent $targetFile) -Force | Out-Null
Remove-Item $targetFile -Force -ErrorAction SilentlyContinue

$proc = Start-BridgeProcess -FilePath $hostExe -Arguments @('--duration=40') -Environment @{
    DOTNET_STARTUP_HOOKS   = $hookDll
    VRCCB_ROOT             = $root
    VRCCB_TARGET_FILE      = $targetFile
    VRCCB_VRCVC_DATA       = (Join-Path $root 'vrcvc-data')
    VRCCB_MOCK_COOKIE_PATH = $targetFile
}
Write-Host "mock host pid: $($proc.Id)"

try {
    $initial = Wait-Until -TimeoutSeconds 25 -Condition {
        if (Test-Path $targetFile) {
            $text = Get-Content -Raw $targetFile
            if ((Get-JarCookieCount -Text $text) -eq 6) { return $text }
        }
        return $null
    }
    Assert-True ($null -ne $initial) 'hook wrote the first jar (6 cookies)'

    # hook-status.json is replaced atomically; a read can land mid-swap, so retry until it parses.
    $statusBefore = Wait-Until -TimeoutSeconds 10 -Condition {
        $s = Read-HookStatus -Root $root
        if ($s -and $s.pid) { return $s }
        return $null
    }
    $pidBefore = $statusBefore.pid
    $syncBefore = $statusBefore.syncCount
    Write-Host "before update: pid=$pidBefore syncCount=$syncBefore"

    # --- live update while the host keeps running ---
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $second = Invoke-NativeMessage -HostExe $nativeHost -Json (New-CookieUpdateJson -Cookies $secondJar) -Environment @{
        VRCCB_ROOT = $root; VRCCB_TARGET_FILE = $targetFile
        VRCCB_VRCVC_DATA = (Join-Path $root 'vrcvc-data'); DOTNET_STARTUP_HOOKS = ''
    }
    Assert-Equal 10 ($second.Reply | ConvertFrom-Json).cookieCount 'second jar delivered (10 cookies)'

    $updated = Wait-Until -TimeoutSeconds 10 -Condition {
        if (Test-Path $targetFile) {
            $text = Get-Content -Raw $targetFile
            if ((Get-JarCookieCount -Text $text) -eq 10) { return $text }
        }
        return $null
    }
    $watch.Stop()

    Assert-True ($null -ne $updated) 'cookie file was updated to the new jar' ("waited " + [int]$watch.Elapsed.TotalSeconds + "s")
    Assert-True ($watch.Elapsed.TotalSeconds -le 10) 'update arrived within 10 seconds' ([math]::Round($watch.Elapsed.TotalSeconds, 2).ToString() + 's')
    Write-Host ("live update latency: " + [math]::Round($watch.Elapsed.TotalSeconds, 2) + "s")

    Assert-True (-not $proc.HasExited) 'the host never restarted (same process still running)'
    $statusAfter = Wait-Until -TimeoutSeconds 10 -Condition {
        $s = Read-HookStatus -Root $root
        if ($s -and $s.pid) { return $s }
        return $null
    }
    Assert-Equal $pidBefore $statusAfter.pid 'hook still reports the same host pid (no restart)'
    Assert-True ([int]$statusAfter.syncCount -gt [int]$syncBefore) 'hook sync counter increased (a new sync happened)'

    $sawTen = Wait-Until -TimeoutSeconds 10 -Condition {
        $text = Get-ProcessStdOut -Process $proc
        if ($text -match 'MOCKHOST poll[^\r\n]*count=10') { return $text }
        return $null
    }
    Assert-True ($null -ne $sawTen) 'the host polled the file and reported 10 cookies'
} finally {
    if ($proc -and -not $proc.HasExited) { Stop-ProcessTree -ProcessId $proc.Id }
}

$ok = Complete-TestCase
exit ([int]([bool](-not $ok)))
