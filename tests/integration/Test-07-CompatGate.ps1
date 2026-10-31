<#
.SYNOPSIS
    Test-07 - compatibility gate: the xUnit matrix, plus proof that an unknown/newer host still runs.

.DESCRIPTION
    Two independent checks:
      1. the CompatibilityMatrix xUnit tests (2026.10.29=Verified, 2027.1.1=UntestedNewer,
         2025.6.1=Supported, 2024.2.1=UntestedOlder, garbage/null=Unknown, HookAllowed always true);
      2. a runtime check: the mock host reports version 2099.1.1 (newer than anything tested) and the
         plugin still ran, resolved every layer and wrote the cookie file - i.e. the gate degrades but
         never blocks.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

Start-TestCase 'Test-07 Compatibility gate'

# ---- 1) xUnit side ----------------------------------------------------------
$testsDll = Find-BuildArtifact -Pattern 'CookieBridge.Tests.dll' -Under (Join-Path (Get-RepoRoot) 'bridge\CookieBridge.Tests\bin')
$runnerDll = Find-BuildArtifact -Pattern 'CookieBridge.TestRunner.dll' -Under (Join-Path (Get-RepoRoot) 'bridge\CookieBridge.TestRunner\bin')
Assert-True ($null -ne $testsDll) 'CookieBridge.Tests.dll is built'
Assert-True ($null -ne $runnerDll) 'CookieBridge.TestRunner.dll is built'

if ($testsDll -and $runnerDll) {
    $result = Invoke-Dotnet -Arguments @($runnerDll, $testsDll, '--filter', 'CompatibilityMatrix')
    $result.Output.Trim() -split "`n" | Where-Object { $_ -match 'PASS|FAIL|TOTAL' } | ForEach-Object { Write-Host "    $_" }
    Assert-Equal 0 $result.ExitCode 'CompatibilityMatrix xUnit tests all pass'
    Assert-True ($result.Output -match 'TOTAL (\d+)  PASSED (\d+)  FAILED 0') 'no CompatibilityMatrix test failed'
} else {
    Write-Check -Message 'CompatibilityMatrix xUnit run' -Ok $false -Detail 'build artifacts missing'
}

# ---- 2) runtime side: unknown/newer version must still be allowed -----------
$root = New-TestRoot -Name 'test-07'
$targetFile = Join-Path $root 'vrcvc-data\youtube_cookies.txt'
$hostExe = Get-MockHostExe -Flavor 'fd'
$hookDll = Get-HookDllPath
$nativeHost = Get-NativeHostPath

$env:VRCCB_ROOT = $root
$env:VRCCB_TARGET_FILE = $targetFile
$env:VRCCB_VRCVC_DATA = (Join-Path $root 'vrcvc-data')

$seed = Invoke-NativeMessage -HostExe $nativeHost -Json (New-CookieUpdateJson -Cookies (New-TestCookieJar -ExtraCookies 2)) -Environment @{
    VRCCB_ROOT = $root; VRCCB_TARGET_FILE = $targetFile
    VRCCB_VRCVC_DATA = (Join-Path $root 'vrcvc-data'); DOTNET_STARTUP_HOOKS = ''
}
Assert-True ($null -ne $seed.Reply) 'vault seeded for the runtime check'

New-Item -ItemType Directory -Path (Split-Path -Parent $targetFile) -Force | Out-Null
Remove-Item $targetFile -Force -ErrorAction SilentlyContinue

$proc = Start-BridgeProcess -FilePath $hostExe -Arguments @('--duration=15') -Environment @{
    DOTNET_STARTUP_HOOKS   = $hookDll
    VRCCB_ROOT             = $root
    VRCCB_TARGET_FILE      = $targetFile
    VRCCB_VRCVC_DATA       = (Join-Path $root 'vrcvc-data')
    VRCCB_MOCK_COOKIE_PATH = $targetFile
}

try {
    $status = Wait-Until -TimeoutSeconds 25 -Condition {
        $s = Read-HookStatus -Root $root
        if ($s -and $s.layers -and ($s.layers.PSObject.Properties.Name -contains 'L5.version')) { return $s }
        return $null
    }
    Assert-True ($null -ne $status) 'hook status published for the unknown/newer host'
    if ($status) {
        Write-Host "    vrcvcVersion=$($status.vrcvcVersion)"
        Assert-Equal '2099.1.1' $status.vrcvcVersion 'host version newer than VerifiedVersion is still reported'
        Assert-Equal 'resolved' (Get-LayerValue -Status $status -Layer 'L2.jarGate') 'layers still resolve for the unknown version'
    }

    $written = Wait-Until -TimeoutSeconds 20 -Condition {
        if (Test-Path $targetFile) { return (Get-Content -Raw $targetFile) }
        return $null
    }
    Assert-True ($null -ne $written) 'cookie file was still written (the gate degrades, it never blocks)'

    $logPath = Join-Path $root 'logs\bridge.log'
    if (Test-Path $logPath) {
        $log = Get-Content -Raw $logPath
        Assert-True ($log -match 'UntestedNewer') 'bridge.log classifies the host as UntestedNewer and continues'
    } else {
        Write-Check -Message 'bridge.log present' -Ok $false
    }
} finally {
    if ($proc -and -not $proc.HasExited) { Stop-ProcessTree -ProcessId $proc.Id }
}

$ok = Complete-TestCase
exit ([int]([bool](-not $ok)))
