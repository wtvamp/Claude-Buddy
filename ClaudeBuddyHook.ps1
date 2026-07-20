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
    exit 0
}

# Identify the terminal hosting this session so a click on the orb can jump
# to it. Windows Terminal advertises itself via WT_SESSION (which flows
# through WSL too, via WSLENV); VS Code's integrated terminal sets
# TERM_PROGRAM. For native sessions, walk up the parent process chain to
# the first process that owns a top-level window — that's the terminal
# (WindowsTerminal.exe, Code.exe, the conhost shell, ...). The walk finds
# nothing for WSL sessions (the Windows-side parent is an interop bridge,
# not the terminal), which is what the term_program fallback is for.
$termProgram = ''
if ($env:WT_SESSION) { $termProgram = 'WindowsTerminal' }
elseif ($env:TERM_PROGRAM) { $termProgram = $env:TERM_PROGRAM }

$termPid = 0
try {
    $cur = Get-CimInstance Win32_Process -Filter "ProcessId=$PID"
    for ($i = 0; $i -lt 10 -and $cur; $i++) {
        $parentId = $cur.ParentProcessId
        if (-not $parentId) { break }
        $proc = Get-Process -Id $parentId -ErrorAction Stop
        if (-not $proc) { break }
        if ($proc.MainWindowHandle -ne 0) { $termPid = [int]$parentId; break }
        $cur = Get-CimInstance Win32_Process -Filter "ProcessId=$parentId"
    }
} catch {}

$status = @{
    state        = $State
    cwd          = $cwd
    term_program = $termProgram
    term_pid     = $termPid
} | ConvertTo-Json -Compress
Set-Content -Path $file -Value $status
