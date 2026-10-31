<#
.SYNOPSIS
    Test-05 - disabled.flag: the plugin must do nothing at all.

.DESCRIPTION
    With a valid vault present and disabled.flag created, StartupHook.Initialize returns immediately, so:
      * no hook-status.json,
      * no cookie file write,
      * the host still runs normally.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

Start-TestCase 'Test-05 disabled.flag - plugin stays passive'

$root = New-TestRoot -Name 'test-05'
$targetFile = Join-Path $root 'vrcvc-data\youtube_cookies.txt'
$hostExe = Get-MockHostExe -Flavor 'fd'
$hookDll = Get-HookDllPath
$nativeHost = Get-NativeHostPath

$env:VRCCB_ROOT = $root
$env:VRCCB_TARGET_FILE = $targetFile
$env:VRCCB_VRCVC_DATA = (Join-Path $root 'vrcvc-data')

# A valid vault exists, and the target file is pre-written by the host path, so a *working* hook would
# have something to do. Then disable the plugin.
$seed = Invoke-NativeMessage -HostExe $nativeHost -Json (New-CookieUpdateJson -Cookies (New-TestCookieJar -ExtraCookies 3)) -Environment @{
    VRCCB_ROOT           = $root
    VRCCB_TARGET_FILE    = $targetFile
    VRCCB_VRCVC_DATA     = (Join-Path $root 'vrcvc-data')
    DOTNET_STARTUP_HOOKS = ''
}
Assert-True ($null -ne $seed.Reply) 'vault seeded before disabling'

Remove-Item $targetFile -Force -ErrorAction SilentlyContinue
New-Item -ItemType File -Path (Join-Path $root 'disabled.flag') -Force | Out-Null
Assert-FileExists (Join-Path $root 'disabled.flag') 'disabled.flag created'

$proc = Start-BridgeProcess -FilePath $hostExe -Arguments @('--duration=12') -Environment @{
    DOTNET_STARTUP_HOOKS   = $hookDll
    VRCCB_ROOT             = $root
    VRCCB_TARGET_FILE      = $targetFile
    VRCCB_VRCVC_DATA       = (Join-Path $root 'vrcvc-data')
    VRCCB_MOCK_COOKIE_PATH = $targetFile
}
Write-Host "mock host pid: $($proc.Id)"

try {
    Start-Sleep -Seconds 6
    Assert-True (-not $proc.HasExited) 'host process is still alive'
    Assert-FileMissing $targetFile 'cookie file was NOT written while disabled'

    $status = Read-HookStatus -Root $root
    Assert-True ($null -eq $status) 'no hook-status.json (the plugin returned during Initialize)'

    $logPath = Join-Path $root 'logs\bridge.log'
    $log = ''
    if (Test-Path $logPath) { $log = Get-Content -Raw $logPath }
    Assert-True ($log -match 'disabled\.flag present') 'bridge.log records that the plugin stayed passive'
} finally {
    if ($proc -and -not $proc.HasExited) { Stop-ProcessTree -ProcessId $proc.Id }
}

$ok = Complete-TestCase
exit ([int]([bool](-not $ok)))
