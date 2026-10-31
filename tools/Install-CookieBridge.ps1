<#
.SYNOPSIS
    Installs (or removes) the VRCVideoCacher CookieBridge local side.

.DESCRIPTION
    Deploys the native-messaging host, the optional in-process hook plugin and the browser-side files
    into  %LOCALAPPDATA%\VRCVideoCacher-CookieBridge , then registers the native-messaging host for the
    current user (HKCU only — no administrator rights, nothing machine-wide).

    It never touches the VRCVideoCacher installation folder or its configuration. The bridge only ever
    writes %APPDATA%\VRCVideoCacher\youtube_cookies.txt , which is the cookie file VRCVideoCacher already
    owns and hands to yt-dlp.

.PARAMETER Source
    Folder that contains host\, hook\ and browser-extension\ (defaults to the script's own folder).

.PARAMETER RegisterStartupHook
    Also registers the plugin in the user-level DOTNET_STARTUP_HOOKS variable.
    Only useful for a self-built (fork) VRCVideoCacher or a framework-dependent host: the Steam build is a
    single-file publish whose host disables the CoreCLR startup-hook feature switch.

.PARAMETER Uninstall
    Removes the native-messaging registration, the environment entry and the deployed hook.
    The encrypted vault and logs are kept unless -Purge is also given.

.PARAMETER Purge
    With -Uninstall: delete %LOCALAPPDATA%\VRCVideoCacher-CookieBridge entirely (vault, logs, backups).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Install-CookieBridge.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Install-CookieBridge.ps1 -Uninstall -Purge
#>
[CmdletBinding()]
param(
    [string]$Source = $PSScriptRoot,
    [string]$ChromiumExtensionId = 'fdjmcdfolkgicljalacnlemepfhmnmfp',
    [string]$FirefoxExtensionId = 'vrexcookiebridge@local',
    [switch]$Uninstall,
    [switch]$Purge,
    [switch]$RegisterStartupHook
)

$ErrorActionPreference = 'Stop'

# PowerShell 5.1 defaults to the OEM code page, which turns the Chinese status lines into mojibake.
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $OutputEncoding = [System.Text.Encoding]::UTF8
} catch { }

$Root      = Join-Path $env:LOCALAPPDATA 'VRCVideoCacher-CookieBridge'
$HostDir   = Join-Path $Root 'host'
$HookDir   = Join-Path $Root 'hook'
$HostExe   = Join-Path $HostDir 'CookieBridge.NativeHost.exe'
$HookDll   = Join-Path $HookDir 'CookieBridge.Hook.dll'
$VrcvcData = Join-Path $env:APPDATA 'VRCVideoCacher'
$CookieTxt = Join-Path $VrcvcData 'youtube_cookies.txt'

function Write-Step($text) { Write-Host "[*] $text" -ForegroundColor Cyan }
function Write-Ok($text)   { Write-Host "[+] $text" -ForegroundColor Green }
function Write-Warn2($text){ Write-Host "[!] $text" -ForegroundColor Yellow }

function Get-UserEnv([string]$name) {
    [Environment]::GetEnvironmentVariable($name, 'User')
}
function Set-UserEnv([string]$name, [string]$value) {
    [Environment]::SetEnvironmentVariable($name, $value, 'User')
}

if ($Uninstall) {
    Write-Step 'Removing CookieBridge'

    if (Test-Path $HostExe) {
        & $HostExe --unregister-host
    } else {
        # Host binary already gone: clear the per-user registry keys directly.
        $keys = @(
            'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.vrcvideocacher.cookiebridge',
            'HKCU:\Software\Chromium\NativeMessagingHosts\com.vrcvideocacher.cookiebridge',
            'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.vrcvideocacher.cookiebridge',
            'HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.vrcvideocacher.cookiebridge',
            'HKCU:\Software\Vivaldi\NativeMessagingHosts\com.vrcvideocacher.cookiebridge',
            'HKCU:\Software\Mozilla\NativeMessagingHosts\com.vrcvideocacher.cookiebridge'
        )
        foreach ($k in $keys) {
            if (Test-Path $k) { Remove-Item $k -Recurse -Force; Write-Ok "removed $k" }
        }
    }

    $hooks = Get-UserEnv 'DOTNET_STARTUP_HOOKS'
    if ($hooks) {
        $kept = @($hooks -split ';' | Where-Object { $_ -and ($_ -notmatch 'CookieBridge\.Hook\.dll$') })
        if ($kept.Count -eq 0) { Set-UserEnv 'DOTNET_STARTUP_HOOKS' $null }
        else { Set-UserEnv 'DOTNET_STARTUP_HOOKS' ($kept -join ';') }
        Write-Ok 'DOTNET_STARTUP_HOOKS cleaned (other entries preserved)'
    }

    if (Test-Path $HookDir) { Remove-Item $HookDir -Recurse -Force; Write-Ok 'hook removed' }
    Remove-Item (Join-Path $Root 'install.json') -Force -ErrorAction SilentlyContinue

    if ($Purge) {
        if (Test-Path $Root) { Remove-Item $Root -Recurse -Force; Write-Ok 'bridge data purged (vault, logs, backups)' }
    } else {
        Write-Warn2 "Encrypted vault and logs kept in $Root (use -Purge to delete them)."
    }

    Write-Host ''
    Write-Ok 'Uninstall complete. VRCVideoCacher is back to its stock behaviour.'
    exit 0
}

# ------------------------------------------------------------------ install
Write-Step "Installing CookieBridge into $Root"
New-Item -ItemType Directory -Force -Path $Root, $HostDir, $HookDir | Out-Null

$srcHost = Join-Path $Source 'host\CookieBridge.NativeHost.exe'
$srcHook = Join-Path $Source 'hook\CookieBridge.Hook.dll'
if (-not (Test-Path $srcHost)) { throw "Native host not found at $srcHost (pass -Source <dist folder>)" }
if (-not (Test-Path $srcHook)) { throw "Hook plugin not found at $srcHook (build bridge/CookieBridge.Hook first)" }

Copy-Item -Path (Join-Path $Source 'host\*') -Destination $HostDir -Force
Copy-Item -Path $srcHook -Destination $HookDir -Force
if (Test-Path (Join-Path $Source 'browser-extension')) {
    Copy-Item -Path (Join-Path $Source 'browser-extension') -Destination $Root -Recurse -Force
}
Write-Ok 'files deployed'

Write-Step 'Registering the native-messaging host (HKCU)'
& $HostExe --register-host --chromium-id $ChromiumExtensionId --firefox-id $FirefoxExtensionId | Out-Null
Write-Ok "registered for chrome-extension://$ChromiumExtensionId/ and $FirefoxExtensionId"

if ($RegisterStartupHook) {
    Write-Step 'Registering DOTNET_STARTUP_HOOKS (opt-in)'
    $current = Get-UserEnv 'DOTNET_STARTUP_HOOKS'
    $entries = @()
    if ($current) { $entries = @($current -split ';' | Where-Object { $_ -and ($_ -notmatch 'CookieBridge\.Hook\.dll$') }) }
    $entries += $HookDll
    Set-UserEnv 'DOTNET_STARTUP_HOOKS' ($entries -join ';')
    Write-Ok 'DOTNET_STARTUP_HOOKS updated (existing hooks preserved)'
} else {
    Write-Warn2 'DOTNET_STARTUP_HOOKS not touched (not needed for the Steam build — contract mode is used).'
}

Write-Host ''
Write-Step 'Verification'
& $HostExe --status

Write-Host ''
Write-Ok 'Install complete.'
Write-Host @"
Next steps
  1. Load the browser extension from: $Root\browser-extension
       Chrome/Edge: chrome://extensions -> 开发者模式 -> 加载已解压的扩展程序 -> 选择该文件夹
       Firefox    : about:debugging -> 临时载入附加组件 -> manifest.json
     Expected extension id: $ChromiumExtensionId
  2. Open https://www.youtube.com in a browser where you are logged in.
     The extension badge turns green once the cookie jar has been saved.
  3. Start VRCVideoCacher and play a YouTube video.
Cookie file consumed by VRCVideoCacher:
  $CookieTxt
"@
