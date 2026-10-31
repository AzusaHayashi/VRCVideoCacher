<#
.SYNOPSIS
    Builds the complete CookieBridge distribution into .\dist .

.DESCRIPTION
    Produces a self-contained, copy-and-run layout:

        dist\
          CookieBridge.Manager.exe        GUI (needs .NET 10 Desktop Runtime)
          CookieBridge.Core.dll
          host\CookieBridge.NativeHost.exe   native-messaging host (self-contained, no runtime needed)
          hook\CookieBridge.Hook.dll         optional in-process plugin
          browser-extension\                 load-unpacked extension
          Install-CookieBridge.ps1           same script as tools\, copied for convenience
          docs\                              documentation snapshot

.PARAMETER SdkPath
    Path to a .NET SDK root when `dotnet` is not on PATH (this machine keeps one in
    %USERPROFILE%\dotnet-sdk\sdk).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\Build-Dist.ps1
#>
[CmdletBinding()]
param(
    [string]$SdkPath = "$env:USERPROFILE\dotnet-sdk\sdk",
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$repo = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $repo 'dist'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    if (Test-Path (Join-Path $SdkPath 'dotnet.exe')) {
        $env:PATH = "$SdkPath;$env:PATH"
        Write-Host "[*] using SDK at $SdkPath"
    } else {
        throw 'dotnet not found. Pass -SdkPath <folder containing dotnet.exe>.'
    }
}

Write-Host "[*] dotnet $(dotnet --version)"

Write-Host '[*] building hook'
dotnet build (Join-Path $repo 'bridge\CookieBridge.Hook\CookieBridge.Hook.csproj') -c $Configuration --nologo | Out-Null

Write-Host '[*] publishing native host (self-contained single file)'
dotnet publish (Join-Path $repo 'bridge\CookieBridge.NativeHost\CookieBridge.NativeHost.csproj') -c $Configuration -o (Join-Path $dist 'host') --nologo | Out-Null

Write-Host '[*] publishing manager'
dotnet publish (Join-Path $repo 'bridge\CookieBridge.Manager\CookieBridge.Manager.csproj') -c $Configuration -o $dist --nologo | Out-Null

Write-Host '[*] staging hook + extension + docs'
New-Item -ItemType Directory -Force -Path (Join-Path $dist 'hook') | Out-Null
Copy-Item (Join-Path $repo "bridge\CookieBridge.Hook\bin\$Configuration\net10.0-windows\CookieBridge.Hook.dll") (Join-Path $dist 'hook') -Force

if (Test-Path (Join-Path $dist 'browser-extension')) { Remove-Item (Join-Path $dist 'browser-extension') -Recurse -Force }
Copy-Item (Join-Path $repo 'browser-extension') (Join-Path $dist 'browser-extension') -Recurse -Force

if (Test-Path (Join-Path $dist 'docs')) { Remove-Item (Join-Path $dist 'docs') -Recurse -Force }
Copy-Item (Join-Path $repo 'docs') (Join-Path $dist 'docs') -Recurse -Force

Copy-Item (Join-Path $repo 'tools\Install-CookieBridge.ps1') $dist -Force
Copy-Item (Join-Path $repo 'tools\Verify-Contract.ps1') $dist -Force

Get-ChildItem $dist | Select-Object Name, Length | Format-Table -AutoSize
Write-Host '[+] dist ready'
