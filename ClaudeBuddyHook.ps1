param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('idle', 'generating', 'waiting', 'ended')]
    [string]$State
)

$ErrorActionPreference = 'SilentlyContinue'

$sessionId = 'unknown'
$cwd = ''
try {
    $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
    if ($payload.session_id) { $sessionId = $payload.session_id }
    if ($payload.cwd) { $cwd = $payload.cwd }
} catch {}

$dir = Join-Path $env:TEMP 'claude_buddy'
if (-not (Test-Path $dir)) {
    New-Item -ItemType Directory -Path $dir | Out-Null
}

$file = Join-Path $dir "$sessionId.txt"

if ($State -eq 'ended') {
    Remove-Item -Path $file -Force
} else {
    $status = @{ state = $State; cwd = $cwd } | ConvertTo-Json -Compress
    Set-Content -Path $file -Value $status
}
