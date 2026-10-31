<#
.SYNOPSIS
    Test-04 - no usable vault: the hook must skip silently and never disturb the host.

.DESCRIPTION
    Same injected framework-dependent mock host, but VRCCB_ROOT has no vault.json at all. The plugin must:
      * stay alive and log the reason ("vault-missing"),
      * not create or touch the cookie file,
      * leave the host running normally.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

Start-TestCase 'Test-04 Missing vault - hook skips silently'

$root = New-TestRoot -Name 'test-04'
$targetFile = Join-Path $root 'vrcvc-data\youtube_cookies.txt'
$hostExe = Get-MockHostExe -Flavor 'fd'
$hookDll = Get-HookDllPath

New-Item -ItemType Directory -Path (Split-Path -Parent $targetFile) -Force | Out-Null
Assert-FileMissing (Join-Path $root 'vault.json') 'precondition: no vault in the isolated root'

$proc = Start-BridgeProcess -FilePath $hostExe -Arguments @('--duration=14') -Environment @{
    DOTNET_STARTUP_HOOKS   = $hookDll
    VRCCB_ROOT             = $root
    VRCCB_TARGET_FILE      = $targetFile
    VRCCB_VRCVC_DATA       = (Join-Path $root 'vrcvc-data')
    VRCCB_MOCK_COOKIE_PATH = $targetFile
}
Write-Host "mock host pid: $($proc.Id)"

try {
    $logPath = Join-Path $root 'logs\bridge.log'
    $hint = Wait-Until -TimeoutSeconds 20 -Condition {
        if (Test-Path $logPath) {
            $text = Get-Content -Raw $logPath
            if ($text -match 'vault-missing') { return $text }
        }
        return $null
    }
    Assert-True ($null -ne $hint) 'bridge.log reports vault-missing (sync skipped, nothing else happened)'

    Start-Sleep -Seconds 1
    Assert-True (-not $proc.HasExited) 'host process is still alive'
    Assert-FileMissing $targetFile 'cookie file was not created'

    $status = Read-HookStatus -Root $root
    Assert-True ($null -ne $status) 'hook-status.json exists (the hook is running, it just has nothing to sync)'
    if ($status) {
        Assert-Equal 'vault:vault-missing' $status.lastSyncOutcome 'hook recorded the skipped sync reason'
    }

    $stdout = Get-ProcessStdOut -Process $proc
    Assert-True ($stdout -match 'MOCKHOST ready') 'host started normally despite the missing vault'
} finally {
    if ($proc -and -not $proc.HasExited) { Stop-ProcessTree -ProcessId $proc.Id }
    $err = Get-ProcessStdErr -Process $proc
    Assert-True ([string]::IsNullOrWhiteSpace($err)) 'host produced no stderr output' ($err)
}

$ok = Complete-TestCase
exit ([int]([bool](-not $ok)))
