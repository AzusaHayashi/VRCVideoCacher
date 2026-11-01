<#
.SYNOPSIS
    Publishes this working copy as a GitHub fork of EllyVR/VRCVideoCacher.

.DESCRIPTION
    This repository is already a fork in the git sense: `upstream` points at
    EllyVR/VRCVideoCacher and the CookieBridge work lives on the
    `feature/cookie-persistence` branch. What is missing is *your* GitHub identity,
    which no tool can supply on your behalf.

    Two ways to authenticate, both free and account-only:

      A. GitHub CLI (recommended)
           winget install --id GitHub.cli -e
           gh auth login           # device flow in the browser, no password to paste
           powershell -ExecutionPolicy Bypass -File tools\Push-Fork.ps1

      B. Personal access token
           https://github.com/settings/tokens  ->  Fine-grained or classic token
           with the `repo` scope (and `workflow` if you want Actions)
           $env:GITHUB_TOKEN = 'ghp_...'
           powershell -ExecutionPolicy Bypass -File tools\Push-Fork.ps1

.PARAMETER Account
    GitHub user/org that will own the fork. Defaults to the authenticated account.

.PARAMETER RepoName
    Name of the fork repository (default: VRCVideoCacher).

.PARAMETER Branch
    Branch to push (default: feature/cookie-persistence).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\Push-Fork.ps1 -Account myname
#>
[CmdletBinding()]
param(
    [string]$Account,
    [string]$RepoName = 'VRCVideoCacher',
    [string]$Branch = 'feature/cookie-persistence'
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    Write-Host '[*] current remotes' -ForegroundColor Cyan
    git remote -v

    $token = $env:GITHUB_TOKEN
    $gh = Get-Command gh -ErrorAction SilentlyContinue

    if ($gh) {
        $status = (& gh auth status 2>&1) -join "`n"
        if ($LASTEXITCODE -ne 0) { throw "gh is installed but not logged in. Run: gh auth login`n$status" }
        if (-not $Account) { $Account = (& gh api user --jq .login).Trim() }
        Write-Host "[*] authenticated GitHub account: $Account" -ForegroundColor Cyan
    } elseif ($token) {
        if (-not $Account) { throw 'GITHUB_TOKEN is set but -Account was not supplied (needed to build the remote URL).' }
        Write-Host "[*] using GITHUB_TOKEN for $Account" -ForegroundColor Cyan
    } else {
        throw @'
No GitHub credentials available.

Pick one:
  A) winget install --id GitHub.cli -e   then   gh auth login
  B) create a token at https://github.com/settings/tokens (scope: repo)
     then  $env:GITHUB_TOKEN = 'ghp_...'  and pass -Account <your-login>
'@
    }

    # 1) make sure the fork exists
    if ($gh) {
        Write-Host '[*] ensuring the fork exists (gh repo fork)' -ForegroundColor Cyan
        & gh repo fork EllyVR/VRCVideoCacher --clone=false 2>&1 | ForEach-Object { "    $_" }
    } else {
        Write-Host '[*] token mode: create the fork in the web UI if it does not exist yet:' -ForegroundColor Yellow
        Write-Host '    https://github.com/EllyVR/VRCVideoCacher/fork'
        Write-Host '    (continuing - push will fail if the target repo is missing)'
    }

    # 2) point origin at the fork
    $url = if ($token) {
        "https://$Account`:$token@github.com/$Account/$RepoName.git"
    } else {
        "https://github.com/$Account/$RepoName.git"
    }
    Write-Host "[*] origin -> https://github.com/$Account/$RepoName.git" -ForegroundColor Cyan
    git remote remove origin 2>$null
    git remote add origin $url
    git remote set-url --push origin $url

    # 3) push the branch (and mirror upstream main so the fork shows the relationship)
    Write-Host "[*] pushing $Branch" -ForegroundColor Cyan
    git push -u origin $Branch
    if ($LASTEXITCODE -ne 0) { throw 'push failed' }

    Write-Host '[*] pushing upstream main (so GitHub shows the fork relationship)' -ForegroundColor Cyan
    git push origin upstream/main:refs/heads/main 2>&1 | ForEach-Object { "    $_" }

    Write-Host ''
    Write-Host "[+] done: https://github.com/$Account/$RepoName/tree/$Branch" -ForegroundColor Green
    Write-Host '    Verify: the repo header should read "forked from EllyVR/VRCVideoCacher".'
} finally {
    Pop-Location
}
