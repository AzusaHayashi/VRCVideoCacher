<#
.SYNOPSIS
    Test-01 - vault round trip through the real Native Messaging host.

.DESCRIPTION
    Starts the published CookieBridge.NativeHost.exe exactly like Chrome would, writes one length-prefixed
    `cookie.update` message with a synthetic jar, and asserts:
      * vault.json appears under VRCCB_ROOT and contains no plaintext,
      * the VRCVC cookie file (VRCCB_TARGET_FILE) is written with 7 tab-separated fields,
      * the reply itself carries no cookie material.
    Nothing outside the workspace is touched.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

Start-TestCase 'Test-01 Vault round trip (NativeHost)'

$root = New-TestRoot -Name 'test-01'
$targetFile = Join-Path $root 'vrcvc-data\youtube_cookies.txt'
$hostExe = Get-NativeHostPath
Write-Host "native host : $hostExe"
Write-Host "VRCCB_ROOT  : $root"

$env:VRCCB_ROOT = $root
$env:VRCCB_TARGET_FILE = $targetFile
$env:VRCCB_VRCVC_DATA = (Join-Path $root 'vrcvc-data')

$jar = New-TestCookieJar -ExtraCookies 4 -WithStCookies
$expectedCount = Get-JarCookieCount -Text $jar   # ST- row is dropped by canonicalisation
Write-Host "synthetic jar: $(Get-JarCookieCount -Text $jar) rows (LOGIN_INFO present, one ST- row to be filtered)"

$reply = Invoke-NativeMessage -HostExe $hostExe -Json (New-CookieUpdateJson -Cookies $jar) -Environment @{
    VRCCB_ROOT        = $root
    VRCCB_TARGET_FILE = $targetFile
    VRCCB_VRCVC_DATA  = (Join-Path $root 'vrcvc-data')
    DOTNET_STARTUP_HOOKS = ''
}

Write-Host "reply      : $($reply.Reply)"
Write-Host "exit code  : $($reply.ExitCode)"
if ($reply.Stderr) { Write-Host "stderr     : $($reply.Stderr)" }

$root
$response = $null
if ($reply.Reply) { $response = $reply.Reply | ConvertFrom-Json }

Assert-True ($null -ne $response) 'native host returned a length-prefixed JSON reply'
Assert-Equal 0 $reply.ExitCode 'native host exited cleanly'
Assert-True ($response.ok -eq $true) 'reply.ok = true' ("error=" + $response.error)
Assert-True ($response.vaultUpdated -eq $true) 'reply.vaultUpdated = true'
Assert-Equal 7 $response.cookieCount 'reply.cookieCount matches the canonical jar (7 = 1 login + 2 + 4 extras)'

$vaultPath = Join-Path $root 'vault.json'
Assert-FileExists $vaultPath 'vault.json created under VRCCB_ROOT'

if (Test-Path $vaultPath) {
    $vaultJson = Get-Content -Raw $vaultPath
    Assert-True ($vaultJson -notmatch 'LOGIN_INFO') 'vault.json contains no plaintext cookie name'
    Assert-True ($vaultJson -notmatch 'FAKE_SID_VALUE') 'vault.json contains no plaintext cookie value'
    $vault = $vaultJson | ConvertFrom-Json
    Assert-Equal 1 $vault.formatVersion 'vault formatVersion = 1'
    Assert-Equal 'dpapi-current-user' $vault.protection 'vault protection = dpapi-current-user'
    Assert-True ($vault.metadata.cookieCount -eq 7) 'vault metadata.cookieCount = 7'
    Assert-True ($vault.metadata.loginCookiePresent -eq $true) 'vault metadata.loginCookiePresent = true'
}

Assert-FileExists $targetFile 'VRCVC cookie file written to VRCCB_TARGET_FILE'
if (Test-Path $targetFile) {
    $written = Get-Content -Raw $targetFile
    $rows = @($written -split "`n" | Where-Object { $_.Trim().Length -gt 0 -and -not $_.StartsWith('#') })
    Assert-Equal 7 $rows.Count 'cookie file has 7 rows (LOGIN_INFO + SID + HSID + 4 extras)'
    $badFieldCount = @($rows | Where-Object { ($_ -split "`t").Count -ne 7 }).Count
    Assert-Equal 0 $badFieldCount 'every row has exactly 7 tab separated fields'
    Assert-True ($written -match 'LOGIN_INFO') 'cookie file contains LOGIN_INFO'
    Assert-True ($written -notmatch 'ST-FILTERME') 'ST- prefixed cookie was filtered out'
    Assert-True ($written -match '^# Netscape HTTP Cookie File') 'cookie file keeps the Netscape header'
}

Assert-True ($reply.Reply -notmatch 'LOGIN_INFO') 'reply JSON carries no cookie name'
Assert-True ($reply.Reply -notmatch 'FAKE_SID_VALUE') 'reply JSON carries no cookie value'

$ok = Complete-TestCase
exit ([int]([bool](-not $ok)))
