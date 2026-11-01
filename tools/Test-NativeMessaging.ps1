<#
.SYNOPSIS
    Regression test: drives the native-messaging host exactly the way Chrome does.

.DESCRIPTION
    Chrome on Windows does not just spawn the host — it also passes <c>--parent-window=&lt;handle&gt;</c>
    (and newer versions pass the caller origin). If the host treats any argument as a CLI command it
    prints usage and exits, and the extension shows
    "Error when communicating with the native messaging host".

    This script starts the host with that argument, writes one length-prefixed cookie.update message on
    stdin and asserts a well-formed framed response plus the expected files. Everything is written to a
    scratch directory — a real user profile is never touched.

.PARAMETER HostExe
    Path to CookieBridge.NativeHost.exe (defaults to the installed copy).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\Test-NativeMessaging.ps1
#>
[CmdletBinding()]
param(
    [string]$HostExe = "$env:LOCALAPPDATA\VRCVideoCacher-CookieBridge\host\CookieBridge.NativeHost.exe",
    [string]$WorkDir = "$PSScriptRoot\..\.native-messaging-test"
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

if (-not (Test-Path $HostExe)) { throw "host not found: $HostExe (run tools\Build-Dist.ps1 / Install-CookieBridge.ps1 first)" }

$WorkDir = [IO.Path]::GetFullPath($WorkDir)
Remove-Item $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $WorkDir | Out-Null

$failures = 0
function Check($name, $condition, $detail = '') {
    if ($condition) { Write-Host "  PASS  $name" -ForegroundColor Green }
    else { Write-Host "  FAIL  $name  $detail" -ForegroundColor Red; $script:failures++ }
}

# Same shape the browser sends: header lines plus 7 tab-separated fields, one ST- row to be filtered.
$jar = "# Netscape HTTP Cookie File`n" +
       ".youtube.com`tTRUE`t/`tTRUE`t1790000000`tLOGIN_INFO`tFAKE_LOGIN_VALUE`n" +
       ".youtube.com`tTRUE`t/`tTRUE`t1790000000`tSID`tFAKE_SID`n" +
       ".youtube.com`tTRUE`t/`tTRUE`t1790000000`tST-bot`tDROP_ME`n"

$message = @{
    type             = 'cookie.update'
    browser          = 'chrome'
    extensionVersion = '1.0.0'
    cookies          = $jar
    capturedUtc      = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
} | ConvertTo-Json -Compress

$payload = [Text.Encoding]::UTF8.GetBytes($message)
$frame = [BitConverter]::GetBytes([uint32]$payload.Length) + $payload

$psi = New-Object Diagnostics.ProcessStartInfo
$psi.FileName = $HostExe
$psi.Arguments = '--parent-window=65552'          # exactly what Chrome adds on Windows
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.EnvironmentVariables['VRCCB_ROOT'] = Join-Path $WorkDir 'root'
$psi.EnvironmentVariables['VRCCB_TARGET_FILE'] = Join-Path $WorkDir 'youtube_cookies.txt'

$process = [Diagnostics.Process]::Start($psi)
$process.StandardInput.BaseStream.Write($frame, 0, $frame.Length)
$process.StandardInput.BaseStream.Flush()
Start-Sleep -Milliseconds 700
$process.StandardInput.Close()

$stdoutStream = $process.StandardOutput.BaseStream
$header = New-Object byte[] 4
$read = 0
while ($read -lt 4) {
    $n = $stdoutStream.Read($header, $read, 4 - $read)
    if ($n -le 0) { break }
    $read += $n
}

$json = $null
if ($read -eq 4) {
    $length = [BitConverter]::ToUInt32($header, 0)
    $body = New-Object byte[] $length
    $offset = 0
    while ($offset -lt $length) {
        $n = $stdoutStream.Read($body, $offset, $length - $offset)
        if ($n -le 0) { break }
        $offset += $n
    }
    if ($offset -eq $length) { $json = [Text.Encoding]::UTF8.GetString($body) }
}

$stderr = $process.StandardError.ReadToEnd()
$process.WaitForExit(5000) | Out-Null

Check 'framed response received' ($null -ne $json) "stdout bytes=$read stderr=$stderr"
if ($json) {
    $response = $json | ConvertFrom-Json
    Check 'response ok=true' ($response.ok -eq $true) $json
    Check 'vault updated' ($response.vaultUpdated -eq $true) $json
    Check 'cookie file written' ($response.targetWritten -eq $true) $json
    Check 'cookie count = 2 (ST- filtered)' ($response.cookieCount -eq 2) "count=$($response.cookieCount)"
    Check 'response leaks no cookie value' ($json -notmatch 'FAKE_LOGIN_VALUE|FAKE_SID')
}

$targetPath = Join-Path $WorkDir 'youtube_cookies.txt'
Check 'target file exists' (Test-Path $targetPath)
if (Test-Path $targetPath) {
    $lines = Get-Content $targetPath | Where-Object { $_ -and -not $_.StartsWith('#') }
    Check 'every row has 7 fields' (@($lines | Where-Object { ($_ -split "`t").Count -ne 7 }).Count -eq 0)
    Check 'ST- row filtered out' (-not ($lines -match "`tST-"))
    Check 'LOGIN_INFO kept' ([bool]($lines -match 'LOGIN_INFO'))
}

Check 'host logged its start' ((Get-Content (Join-Path $WorkDir 'root\logs\bridge.log') -Raw -ErrorAction SilentlyContinue) -match 'Native messaging host started')
Check 'CLI still works (--version)' ((& $HostExe --version) -match '^\d+\.\d+')

Write-Host ''
if ($failures -eq 0) { Write-Host 'ALL PASS' -ForegroundColor Green; exit 0 }
Write-Host "$failures FAILED" -ForegroundColor Red
exit 1
