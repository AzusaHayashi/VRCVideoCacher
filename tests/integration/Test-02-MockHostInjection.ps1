<#
.SYNOPSIS
    Test-02 - positive injection proof: framework-dependent mock host + DOTNET_STARTUP_HOOKS.

.DESCRIPTION
    The mock host mirrors the VRCVideoCacher shapes the plugin probes reflectively (Program.Version /
    Program.NotifyCookiesUpdated / Program.IsCookiesValid / YtdlManager.CookiesPath / YtdlCookieJar.AcquireAsync).

    The framework-dependent publish keeps the CoreCLR startup-hook feature switch enabled (the single-file
    publish in Test-02b turns it off), so this is the run that proves the whole chain:
        DOTNET_STARTUP_HOOKS -> StartupHook.Initialize -> reflection probe -> cookie file write.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

Start-TestCase 'Test-02 Mock host injection (framework-dependent host)'

$root = New-TestRoot -Name 'test-02'
$targetFile = Join-Path $root 'vrcvc-data\youtube_cookies.txt'
$hostExe = Get-MockHostExe -Flavor 'fd'
$hookDll = Get-HookDllPath
$nativeHost = Get-NativeHostPath

Write-Host "mock host  : $hostExe"
Write-Host "hook dll   : $hookDll"
Write-Host "VRCCB_ROOT : $root"

$env:VRCCB_ROOT = $root
$env:VRCCB_TARGET_FILE = $targetFile
$env:VRCCB_VRCVC_DATA = (Join-Path $root 'vrcvc-data')

# 1. Seed the vault (and prove the host-side write path) with the real NativeHost.
$jar = New-TestCookieJar -ExtraCookies 3
$seed = Invoke-NativeMessage -HostExe $nativeHost -Json (New-CookieUpdateJson -Cookies $jar) -Environment @{
    VRCCB_ROOT           = $root
    VRCCB_TARGET_FILE    = $targetFile
    VRCCB_VRCVC_DATA     = (Join-Path $root 'vrcvc-data')
    DOTNET_STARTUP_HOOKS = ''
}
Write-Host "seed reply : $($seed.Reply)"
Assert-True ($null -ne $seed.Reply) 'vault seeded through NativeHost'

# 2. Start the mock host with the hook injected. Cover the file the plugin will write.
New-Item -ItemType Directory -Path (Split-Path -Parent $targetFile) -Force | Out-Null
Remove-Item $targetFile -Force -ErrorAction SilentlyContinue

$proc = Start-BridgeProcess -FilePath $hostExe -Arguments @('--duration=25') -Environment @{
    DOTNET_STARTUP_HOOKS    = $hookDll
    VRCCB_ROOT              = $root
    VRCCB_TARGET_FILE       = $targetFile
    VRCCB_VRCVC_DATA        = (Join-Path $root 'vrcvc-data')
    VRCCB_MOCK_COOKIE_PATH  = $targetFile
}
$collector = Start-ProcessCollector -Process $proc
Write-Host "mock host pid: $($proc.Id)"

try {
    # 3. The plugin must publish a fresh heartbeat.
    $status = Wait-Until -TimeoutSeconds 30 -Condition {
        $s = Read-HookStatus -Root $root
        if ($s -and (Test-HookStatusFresh -Status $s)) { return $s }
        return $null
    }

    Assert-True ($null -ne $status) 'hook-status.json appeared and IsFresh = true (heartbeat < 90s, pid alive)'
    if ($status) {
        Write-Host "hook status: vrcvcVersion=$($status.vrcvcVersion) pid=$($status.pid) layers=($($status.layers | ConvertTo-Json -Compress))"
        Assert-Equal $proc.Id $status.pid 'hook reports the mock host pid'
        Assert-Equal 'resolved' (Get-LayerValue -Status $status -Layer 'L1.cookiePath') 'L1.cookiePath = resolved (YtdlManager.CookiesPath found)'
        Assert-Equal 'resolved' (Get-LayerValue -Status $status -Layer 'L2.jarGate') 'L2.jarGate = resolved (YtdlCookieJar.AcquireAsync found)'
        Assert-Equal 'resolved' (Get-LayerValue -Status $status -Layer 'L3.notify') 'L3.notify = resolved (Program.NotifyCookiesUpdated found)'
        Assert-Equal 'resolved' (Get-LayerValue -Status $status -Layer 'L4.validity') 'L4.validity = resolved (Program.IsCookiesValid found)'
        Assert-Equal 'resolved' (Get-LayerValue -Status $status -Layer 'L5.version') 'L5.version = resolved (Program.Version found)'
        Assert-Equal '2099.1.1' $status.vrcvcVersion 'vrcvcVersion read reflectively = 2099.1.1'
    }

    # 4. The plugin must mirror the vault into the host's cookie file.
    $written = Wait-Until -TimeoutSeconds 30 -Condition {
        if (Test-Path $targetFile) { return (Get-Content -Raw $targetFile) }
        return $null
    }
    Assert-True ($null -ne $written) 'hook wrote the VRCVC cookie file'
    if ($written) {
        $rows = @($written -split "`n" | Where-Object { $_.Trim().Length -gt 0 -and -not $_.StartsWith('#') })
        $withNames = @($rows | Where-Object { $_ -match '(?m)^[^\t]+\t[^\t]+\t[^\t]+\t[^\t]+\t[^\t]+\t[^t]+\t[^t]+$' }).Count
        Assert-Equal 6 $rows.Count 'written cookie file has the 6 vault cookies'
        Assert-Equal 0 (@($rows | Where-Object { ($_ -split "`t").Count -ne 7 }).Count) 'written rows have 7 fields'
        Assert-True ($written -match 'LOGIN_INFO') 'written cookie file contains LOGIN_INFO'

        # 5. What the host itself reports must agree with the vault.
        $hostOutput = $collector.StdOut.ToString()
        $polls = [regex]::Matches($hostOutput, 'MOCKHOST poll[^\r\n]*count=(\d+)')
        Assert-True ($polls.Count -gt 0) 'mock host polled its cookie file'
        if ($polls.Count -gt 0) {
            $lastCount = [int]$polls[$polls.Count - 1].Groups[1].Value
            Write-Host ("last mock poll : count=" + $lastCount)
            Assert-Equal 6 $lastCount 'host-reported cookie count matches the vault (6)'
            Assert-True ($hostOutput -match 'login=True') 'host reported login=True for the injected jar'
        } else {
            Write-Host "host stdout so far:"
            Write-Host $hostOutput
        }
    }

    Assert-True (-not $proc.HasExited) 'mock host is still alive (the hook did not break it)'
} finally {
    if ($proc -and -not $proc.HasExited) { Stop-ProcessTree -ProcessId $proc.Id }
    $logPath = Join-Path $root 'logs\bridge.log'
    if (Test-Path $logPath) {
        Write-Host "--- bridge.log (tail) ---"
        Get-Content $logPath -Tail 12 | ForEach-Object { Write-Host "    $_" }
        $log = Get-Content -Raw $logPath
        Assert-True ($log -match 'Startup hook initialised inside the host process') 'bridge.log proves StartupHook.Initialize ran in-process'
        Assert-True ($log -match 'Attached to host PID') 'bridge.log shows the hook attached to the host pid'
    }
}

$ok = Complete-TestCase
exit ([int]([bool](-not $ok)))
