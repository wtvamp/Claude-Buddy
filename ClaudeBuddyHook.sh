#!/bin/bash
# Claude Buddy hook for macOS/Linux — bash twin of ClaudeBuddyHook.ps1.
# Usage (from a Claude Code hook): ClaudeBuddyHook.sh <idle|generating|waiting|ended>
# Reads the hook payload JSON on stdin for session_id and cwd.

STATE="$1"
case "$STATE" in
    idle|generating|waiting|ended) ;;
    *) exit 0 ;;
esac

PAYLOAD=$(cat)

# No jq dependency: session_id is a UUID and cwd is a path, neither of which
# contains embedded quotes in practice, so simple sed extraction is enough.
SESSION_ID=$(printf '%s' "$PAYLOAD" | sed -n 's/.*"session_id"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
CWD=$(printf '%s' "$PAYLOAD" | sed -n 's/.*"cwd"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
[ -n "$SESSION_ID" ] || SESSION_ID="unknown"

# Identify the terminal hosting this session so a click on the orb can jump
# to it. This script runs inside the terminal's process tree, so:
# - ITERM_SESSION_ID ("w0t0p0:UUID") pins the exact iTerm2 pane, and is
#   preferred over TERM_PROGRAM since tmux masks the latter;
# - the controlling tty of the nearest ancestor that has one (the claude
#   TUI process — this hook itself runs on a pipe, not a tty) pins the
#   exact Terminal.app tab.
TERM_ID=""
if [ -n "$ITERM_SESSION_ID" ]; then
    TERM_ID="${ITERM_SESSION_ID#*:}"
fi

TTY=""
PID=$$
for _ in 1 2 3 4 5; do
    PID=$(ps -o ppid= -p "$PID" 2>/dev/null | tr -d ' ')
    { [ -z "$PID" ] || [ "$PID" = "0" ] || [ "$PID" = "1" ]; } && break
    T=$(ps -o tty= -p "$PID" 2>/dev/null | tr -d ' ')
    if [ -n "$T" ] && [ "$T" != "??" ]; then TTY="$T"; break; fi
done

# ${TMPDIR} is what .NET's Path.GetTempPath() returns on macOS, so the app
# and this script agree on the folder (both are per-user).
DIR="${TMPDIR:-/tmp/}"
DIR="${DIR%/}/claude_buddy"
FILE="$DIR/$SESSION_ID.txt"

if [ "$STATE" = "ended" ]; then
    rm -f "$FILE"
else
    mkdir -p "$DIR"
    printf '{"state":"%s","cwd":"%s","term_program":"%s","term_id":"%s","tty":"%s"}' \
        "$STATE" "$CWD" "$TERM_PROGRAM" "$TERM_ID" "$TTY" > "$FILE"
fi

exit 0
