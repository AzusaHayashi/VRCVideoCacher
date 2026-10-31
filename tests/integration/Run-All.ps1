<#
.SYNOPSIS
    Runs the whole CookieBridge verification suite and prints a PASS/FAIL summary.

.DESCRIPTION
    Order matters:
      0. build/publish everything (Build-All.ps1)
      1. xUnit suite via CookieBridge.TestRunner (see docs/TEST-REPORT.md for why not `dotnet test` here)
      2. integration cases Test-01 .. Test-07

    Exit code 0 only when every case passed. A failing case never stops the run: the summary reports
    exactly which ones failed, so a partial environment can still produce an honest report.
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [string[]]$Only = @()
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

$repo = Get-RepoRoot
$results = New-Object System.Collections.Generic.List[object]
$started = Get-Date

function Add-Result {
    param([string]$Name, [bool]$Ok, [string]$Detail = '')
    $results.Add([pscustomobject]@{ Case = $Name; Ok = $Ok; Detail = $Detail })
    $color = 'Green'; $label = 'PASS'
    if (-not $Ok) { $color = 'Red'; $label = 'FAIL' }
    Write-Host ("[$label] $Name  $Detail") -ForegroundColor $color
}

function Test-Selected {
    param([string]$Name)
    if ($Only.Count -eq 0) { return $true }
    foreach ($pattern in $Only) { if ($Name -like $pattern) { return $true } }
    return $false
}

Write-Host ''
Write-Host '################ CookieBridge integration suite ################' -ForegroundColor Cyan

# ---- 0. build ---------------------------------------------------------------
if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'Build-All.ps1')
    if ($LASTEXITCODE -ne 0) {
        Add-Result 'Build-All' $false 'artefact build failed'
        $results | Format-Table -AutoSize | Out-String | Write-Host
        exit 1
    }
    Add-Result 'Build-All' $true 'hook / native host / both mock hosts / tests / runner'
}

# ---- 1. xUnit ---------------------------------------------------------------
if (Test-Selected 'xUnit') {
    $testsDll = Find-BuildArtifact -Pattern 'CookieBridge.Tests.dll' -Under (Join-Path $repo 'bridge\CookieBridge.Tests\bin')
    $runnerDll = Find-BuildArtifact -Pattern 'CookieBridge.TestRunner.dll' -Under (Join-Path $repo 'bridge\CookieBridge.TestRunner\bin')
    if (-not $testsDll -or -not $runnerDll) {
        Add-Result 'xUnit' $false 'test assembly or runner missing'
    } else {
        $run = Invoke-Dotnet -Arguments @($runnerDll, $testsDll)
        $summary = ($run.Output -split "`n" | Where-Object { $_ -match '^TOTAL' } | Select-Object -Last 1)
        if ($summary) { Write-Host ("  " + $summary.Trim()) }
        $failLines = ($run.Output -split "`n" | Where-Object { $_ -match '^\s+FAIL\s' })
        foreach ($line in $failLines) { Write-Host ("  " + $line.Trim()) -ForegroundColor DarkYellow }
        Add-Result 'xUnit' ($run.ExitCode -eq 0) ($summary.Trim())
    }
}

# ---- 2. integration cases ---------------------------------------------------
$cases = @(
    @{ Name = 'Test-01 vault roundtrip';        Script = 'Test-01-VaultRoundtrip.ps1' },
    @{ Name = 'Test-02 injection (fd)';         Script = 'Test-02-MockHostInjection.ps1' },
    @{ Name = 'Test-02b single-file control';   Script = 'Test-02b-SingleFileControl.ps1' },
    @{ Name = 'Test-03 real VRCVC (read-only)'; Script = 'Test-03-RealVrcvcInjection.ps1' },
    @{ Name = 'Test-04 no vault';               Script = 'Test-04-NoCookieFile.ps1' },
    @{ Name = 'Test-05 disabled flag';          Script = 'Test-05-Disable.ps1' },
    @{ Name = 'Test-06 live reload';            Script = 'Test-06-LiveReload.ps1' },
    @{ Name = 'Test-07 compatibility gate';     Script = 'Test-07-CompatGate.ps1' }
)

foreach ($case in $cases) {
    if (-not (Test-Selected $case.Name)) { continue }
    Write-Host ''
    $logFile = Join-Path (Get-WorkRoot) ("run-" + $case.Script + ".log")
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $case.Script) 2>&1
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previous
    }
    $output | Out-String | Write-Host
    $output | Out-File -FilePath $logFile -Encoding utf8
    $detail = ''
    $resultLine = ($output | Where-Object { $_ -match '^RESULT ' } | Select-Object -Last 1)
    if ($resultLine) { $detail = $resultLine.Trim() }
    Add-Result $case.Name ($code -eq 0) $detail
}

# ---- summary ----------------------------------------------------------------
$elapsed = (Get-Date) - $started
Write-Host ''
Write-Host '################ SUMMARY ################' -ForegroundColor Cyan
$results | Format-Table -AutoSize Case, Ok, Detail | Out-String | Write-Host
$failed = @($results | Where-Object { -not $_.Ok })
Write-Host ("cases: " + $results.Count + "   passed: " + ($results.Count - $failed.Count) + "   failed: " + $failed.Count + "   elapsed: " + [int]$elapsed.TotalSeconds + "s")
if ($failed.Count -eq 0) {
    Write-Host 'ALL INTEGRATION CASES PASSED' -ForegroundColor Green
} else {
    Write-Host ('FAILED: ' + (($failed | ForEach-Object { $_.Case }) -join ', ')) -ForegroundColor Red
}

$results | ConvertTo-Json -Depth 4 | Out-File -FilePath (Join-Path (Get-WorkRoot) 'summary.json') -Encoding utf8

exit ([int]([bool]($failed.Count -gt 0)))
