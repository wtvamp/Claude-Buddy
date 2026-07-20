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

# ${TMPDIR} is what .NET's Path.GetTempPath() returns on macOS, so the app
# and this script agree on the folder (both are per-user).
DIR="${TMPDIR:-/tmp/}"
DIR="${DIR%/}/claude_buddy"
FILE="$DIR/$SESSION_ID.txt"

if [ "$STATE" = "ended" ]; then
    rm -f "$FILE"
else
    mkdir -p "$DIR"
    printf '{"state":"%s","cwd":"%s"}' "$STATE" "$CWD" > "$FILE"
fi

exit 0
