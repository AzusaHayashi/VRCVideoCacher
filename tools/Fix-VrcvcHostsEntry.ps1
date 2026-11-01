# Adds the hosts entry VRCVideoCacher needs for its web server prefix, cleans the
# accumulated commented duplicates, and keeps a backup. Must run elevated.
$ErrorActionPreference = 'Stop'
$hosts = Join-Path $env:WINDIR 'System32\drivers\etc\hosts'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backup = "$hosts.cookiebridge-backup-$stamp"
Copy-Item $hosts $backup -Force
"backup: $backup"

$lines = Get-Content $hosts
$kept = New-Object System.Collections.Generic.List[string]
$removed = 0
foreach ($line in $lines) {
    if ($line -match '^\s*#\s*127\.0\.0\.1\s+localhost\.youtube\.com\s*$') { $removed++; continue }   # dead duplicates
    if ($line -match '^\s*127\.0\.0\.1\s+localhost\.youtube\.com\s*$') { $removed++; continue }        # will re-add one
    $kept.Add($line)
}
# Drop trailing blank lines, then append exactly one active entry.
while ($kept.Count -gt 0 -and [string]::IsNullOrWhiteSpace($kept[$kept.Count - 1])) { $kept.RemoveAt($kept.Count - 1) }
$kept.Add('')
$kept.Add('127.0.0.1 localhost.youtube.com')
Set-Content -Path $hosts -Value $kept -Encoding ASCII
"rewrote hosts (removed $removed old entries, added 1 active)"

"--- active entries now ---"
(Select-String -Path $hosts -Pattern 'localhost\.youtube\.com' | Where-Object { $_.Line -notmatch '^\s*#' }).Line

"--- resolution ---"
try { (Resolve-DnsName localhost.youtube.com -Type A -ErrorAction Stop | Select-Object -First 1).IPAddress } catch { "resolve failed: $($_.Exception.Message)" }

"--- bind test on the exact prefix VRCVideoCacher uses ---"
try {
    $l = New-Object Net.HttpListener
    $l.Prefixes.Add('http://localhost.youtube.com:9696/')
    $l.Start()
    'bind OK'
    $l.Stop(); $l.Close()
} catch { "bind FAILED: $($_.Exception.Message)" }
