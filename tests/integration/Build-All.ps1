<#
.SYNOPSIS
    Builds every artefact the integration tests need.

.DESCRIPTION
    Hook      : dotnet build -c Release        (net10.0-windows)  -> the DLL injected into a host
    NativeHost: dotnet publish -c Release      (single-file)      -> the browser-side host under test
    MockHost  : dotnet publish -p:MockHostFlavor=fd|single        -> positive / negative control hosts
    Tests     : dotnet build -c Release                           -> CookieBridge.Tests.dll
    Runner    : dotnet build -c Release                           -> in-process executor (see TEST-REPORT)
#>
[CmdletBinding()]
param([switch]$Force)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

$repo = Get-RepoRoot
$artifacts = Get-ArtifactsRoot
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null

Write-Host '=== building CookieBridge artefacts ===' -ForegroundColor Cyan
[void](Initialize-Dotnet)

$steps = @(
    @{ Name = 'Hook (Release)';        Args = @('build', (Join-Path $repo 'bridge\CookieBridge.Hook\CookieBridge.Hook.csproj'), '-c', 'Release') },
    @{ Name = 'Tests (Release)';       Args = @('build', (Join-Path $repo 'bridge\CookieBridge.Tests\CookieBridge.Tests.csproj'), '-c', 'Release') },
    @{ Name = 'TestRunner (Release)';  Args = @('build', (Join-Path $repo 'bridge\CookieBridge.TestRunner\CookieBridge.TestRunner.csproj'), '-c', 'Release') },
    @{ Name = 'NativeHost (publish)';  Args = @('publish', (Join-Path $repo 'bridge\CookieBridge.NativeHost\CookieBridge.NativeHost.csproj'), '-c', 'Release', '-o', (Join-Path $artifacts 'nativehost')) },
    @{ Name = 'MockHost fd (publish)'; Args = @('publish', (Join-Path $repo 'bridge\CookieBridge.MockHost\CookieBridge.MockHost.csproj'), '-c', 'Release', '-p:MockHostFlavor=fd', '-o', (Join-Path $artifacts 'mockhost-fd')) },
    @{ Name = 'MockHost single-file (publish)'; Args = @('publish', (Join-Path $repo 'bridge\CookieBridge.MockHost\CookieBridge.MockHost.csproj'), '-c', 'Release', '-p:MockHostFlavor=single', '-o', (Join-Path $artifacts 'mockhost-single')) }
)

$failed = @()
foreach ($step in $steps) {
    Write-Host ("  " + $step.Name + ' ...') -NoNewline
    $result = Invoke-Dotnet -Arguments $step.Args
    if ($result.ExitCode -ne 0) {
        Write-Host ' FAILED' -ForegroundColor Red
        Write-Host $result.Output
        $failed += $step.Name
    } else {
        Write-Host ' ok' -ForegroundColor Green
    }
}

if ($failed.Count -gt 0) {
    Write-Host ("build failures: " + ($failed -join ', ')) -ForegroundColor Red
    exit 1
}

Write-Host 'all artefacts built' -ForegroundColor Green
exit 0
