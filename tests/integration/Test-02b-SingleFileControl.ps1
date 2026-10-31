<#
.SYNOPSIS
    Test-02b - negative control: single-file self-contained host does NOT load the startup hook.

.DESCRIPTION
    Same mock host code, published exactly like the shipped VRCVideoCacher (PublishSingleFile +
    SelfContained + PublishTrimmed). For single-file hosts the CoreCLR host sets
    System.StartupHookProvider.IsSupported = false, so DOTNET_STARTUP_HOOKS is ignored.

    This test documents that boundary with evidence instead of assuming it:
      * the plugin must NOT log "Startup hook initialised inside the host process",
      * no hook-status.json may appear,
      * the cookie file must NOT be written by a hook.
    It also captures the COREHOST_TRACE lines that show the feature switch being turned off.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

Start-TestCase 'Test-02b Single-file host is the negative control (hook must not load)'

$root = New-TestRoot -Name 'test-02b'
$targetFile = Join-Path $root 'vrcvc-data\youtube_cookies.txt'
$hostExe = Get-MockHostExe -Flavor 'single'
$hookDll = Get-HookDllPath
$nativeHost = Get-NativeHostPath
$traceFile = Join-Path $root 'corehost-trace.log'

Write-Host "mock host  : $hostExe (single-file self-contained)"
Write-Host "hook dll   : $hookDll"

$env:VRCCB_ROOT = $root
$env:VRCCB_TARGET_FILE = $targetFile
$env:VRCCB_VRCVC_DATA = (Join-Path $root 'vrcvc-data')

# Seed a vault so a working hook *would* have something to write.
$seed = Invoke-NativeMessage -HostExe $nativeHost -Json (New-CookieUpdateJson -Cookies (New-TestCookieJar -ExtraCookies 3)) -Environment @{
    VRCCB_ROOT           = $root
    VRCCB_TARGET_FILE    = $targetFile
    VRCCB_VRCVC_DATA     = (Join-Path $root 'vrcvc-data')
    DOTNET_STARTUP_HOOKS = ''
}
Write-Host "seed reply : $($seed.Reply)"
Assert-True ($null -ne $seed.Reply) 'vault seeded (a working hook would have work to do)'

Remove-Item $targetFile -Force -ErrorAction SilentlyContinue

$proc = Start-BridgeProcess -FilePath $hostExe -Arguments @('--duration=14') -Environment @{
    DOTNET_STARTUP_HOOKS       = $hookDll
    COREHOST_TRACE             = '1'
    COREHOST_TRACE_VERBOSITY   = '4'
    COREHOST_TRACEFILE         = $traceFile
    VRCCB_ROOT                 = $root
    VRCCB_TARGET_FILE          = $targetFile
    VRCCB_VRCVC_DATA           = (Join-Path $root 'vrcvc-data')
    VRCCB_MOCK_COOKIE_PATH     = $targetFile
}
Write-Host "mock host pid: $($proc.Id)"

try {
    $null = Wait-Until -TimeoutSeconds 12 -Condition { if ($proc.HasExited) { return $true } return $null }
    Start-Sleep -Milliseconds 500

    $status = Read-HookStatus -Root $root
    Assert-True ($null -eq $status) 'no hook-status.json was produced (the plugin never ran in-process)'

    Assert-FileMissing $targetFile 'cookie file was NOT written by a hook inside the single-file host'

    $logPath = Join-Path $root 'logs\bridge.log'
    $log = ''
    if (Test-Path $logPath) { $log = Get-Content -Raw $logPath }
    Assert-True ($log -notmatch 'Startup hook initialised inside the host process') 'bridge.log has no "Startup hook initialised" line for the single-file host'

    $stdout = Get-ProcessStdOut -Process $proc
    Write-Host '--- mock host stdout (first lines) ---'
    ($stdout -split "`n" | Select-Object -First 3) | ForEach-Object { Write-Host "    $_" }
    Assert-True ($stdout -match 'MOCKHOST ready') 'the single-file host itself started normally (the app is unaffected)'
    Assert-True ($stdout -match 'count=0') 'the host sees no cookie file (nothing wrote it)'
} finally {
    if ($proc -and -not $proc.HasExited) { Stop-ProcessTree -ProcessId $proc.Id }
}

# ---- COREHOST_TRACE evidence -------------------------------------------------
if (Test-Path $traceFile) {
    $trace = Get-Content -Raw $traceFile
    Write-Host '--- COREHOST_TRACE evidence ---'
    $patterns = @(
        'Property STARTUP_HOOKS',
        'Property TRUSTED_PLATFORM_ASSEMBLIES',
        'Property System\.StartupHookProvider\.IsSupported',
        'STARTUP_HOOKS ='
    )
    foreach ($pattern in $patterns) {
        $matches = [regex]::Matches($trace, "[^\r\n]*$pattern[^\r\n]*")
        if ($matches.Count -gt 0) {
            $line = ($matches[0].Value).Trim()
            Write-Host "    $line"
        }
    }
    $supportedOff = [regex]::IsMatch($trace, 'System\.StartupHookProvider\.IsSupported\s*=\s*false')
    Assert-True $supportedOff 'COREHOST_TRACE shows System.StartupHookProvider.IsSupported = false'
    $tpaEmpty = [regex]::IsMatch($trace, 'TRUSTED_PLATFORM_ASSEMBLIES\s*=\s*(\r?\n|$)')
    Assert-True $tpaEmpty 'COREHOST_TRACE shows an empty TRUSTED_PLATFORM_ASSEMBLIES (single-file bundle)'
} else {
    Assert-True $false 'COREHOST_TRACE file was produced' ("missing: " + $traceFile)
}

$ok = Complete-TestCase
exit ([int]([bool](-not $ok)))
