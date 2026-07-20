# Claude Buddy

One tiny always-on-top orb per running Claude Code session, stacked in the
top-right corner of your screen. Each has three states:
- **Slate-blue, gentle breathing** — truly idle, nothing happening.
- **Violet, medium pulse** — Claude is actively generating a response or
  running tools.
- **Amber, fast pulse** — Claude needs something from you specifically: a
  tool-permission approval, or an answer to an interactive question.
  Claude finishing a response and waiting for you to type whatever's next
  does *not* trigger this — that's deliberate, not a bug; see the matcher
  note below if you want it back.

Hover an orb to see which session it is (its working directory). Right-click
for a menu (reset that session to idle / exit Claude Buddy entirely).

It works by watching a small folder (`%TEMP%\claude_buddy\`) that fills up
with one JSON status file per session — `<session_id>.txt`, containing
`{"state": "...", "cwd": "..."}` — written by a PowerShell script
(`ClaudeBuddyHook.ps1`) that Claude Code hooks invoke. No network calls, no
polling of Claude Code itself, no persistent process beyond the hook calls
themselves.

A session's orb disappears when its `SessionEnd` hook fires (clean exits
like `/exit`) or — since `SessionEnd` is documented as unreliable on
ungraceful termination, notably Ctrl+C — once its file hasn't been touched
in 5 minutes, whichever comes first. **Exception**: a session sitting on
`waiting` (amber) is never pruned by the 5-minute timer, deliberately —
nothing else refreshes that file while you're away from an unanswered
prompt, so timing it out would hide the orb exactly when it's trying hardest
to get your attention. If a session gets Ctrl+C'd right at a prompt, its
orb will sit there indefinitely; right-click → "Reset this session to idle"
clears it manually, after which the normal 5-minute rule applies.

**Scope**: this only tracks Claude Code sessions that read a `settings.json`
you've wired up per step 2 below. Each Claude Code install — WSL (per Linux
user) and native Windows — has its own, unrelated `settings.json`, so a
session won't show up until you add the matching hooks to *its* config.
The app itself doesn't care where a status file came from; both WSL and
native Windows hooks ultimately run `powershell.exe` as a normal Windows
process, so `$env:TEMP` resolves to the same real folder either way and
their orbs happily stack together in one running `ClaudeBuddy.exe`. This is
just a matter of wiring more hook configs, not a hard limitation — a
different WSL user's install is the one combination left unwired, since
that would need hooks added inside *their* Linux user account.

## 1. Build it

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download) on Windows.

```powershell
cd ClaudeBuddy
dotnet publish -c Release -r win-x64 --self-contained true
```

The exe lands in
`bin\Release\net8.0-windows\win-x64\publish\ClaudeBuddy.exe` — it's
self-contained, so you can copy that one file anywhere (e.g. a `Tools`
folder) and it'll run without needing .NET installed separately.

**Run it from a local drive, not a network/UNC path.** If the project
(or the built exe) lives somewhere like `\\wsl.localhost\...` or any
other UNC share, WPF's stylus/ink input subsystem fails to load its
native DLL on startup (`DllNotFoundException: PenIMC_cor3.dll`) and the
app crashes silently right as the window tries to show. Building from
a network path is fine — just copy the published `ClaudeBuddy.exe` (and
`ClaudeBuddyHook.ps1`, see step 2) to a local folder (e.g.
`%LOCALAPPDATA%\ClaudeBuddy\`) before running it.

Run it once to sanity-check: since no session has written a status file
yet, you should see **zero orbs** — that's correct, not broken. Left-click-
drag an orb to reposition it once one appears; dragging is only honored
until the next time a session is added or removed, at which point the
whole stack reflows back to its default layout.

## 2. Wire up Claude Code

Each Claude Code install you want tracked (WSL, native Windows, another WSL
user, ...) needs its own copy of these hooks added to *its own*
`settings.json` — installs don't share config. Repeat this section once per
install.

**Copy `ClaudeBuddyHook.ps1` to the same local folder as the exe** (e.g.
`%LOCALAPPDATA%\ClaudeBuddy\ClaudeBuddyHook.ps1`) — the hooks below call it
by absolute path. One copy is enough; every install's hooks can point at
the same file, since it's just a normal Windows PowerShell script and isn't
WSL-specific itself.

Pick the snippet that matches where the Claude Code session you're wiring
up actually runs, then open **that install's** `~/.claude/settings.json`
(create it if it doesn't exist) and merge in the snippet's contents,
replacing every `<YOUR_USERNAME>` with your actual Windows username:

- **Claude Code running inside WSL** → `claude-hooks-snippet-wsl.json`.
  `~/.claude/settings.json` here means the Linux user's home directory
  (e.g. `/root/.claude/settings.json` or `/home/<user>/.claude/settings.json`
  depending which WSL user runs it) — a completely separate file from any
  Windows-side config.
- **Claude Code installed natively on Windows** (not through WSL) →
  `claude-hooks-snippet-windows.json`. `~/.claude/settings.json` here means
  `%USERPROFILE%\.claude\settings.json`, i.e.
  `C:\Users\<YOUR_USERNAME>\.claude\settings.json`.

The two snippets do the same thing and differ only in how they invoke
`powershell.exe` (see the platform notes below) — the hook logic, matchers,
and states are identical.

**If you already have a `hooks` block with other events in it**, don't
replace the whole thing — add `Notification`, `UserPromptSubmit`,
`PreToolUse`, `Stop`, and `SessionEnd` as sibling keys inside your existing
`hooks` object, and if you already have any of those five keys, append
these entries to their existing arrays instead of overwriting them.

What each hook does — every one of them invokes `ClaudeBuddyHook.ps1
-State <idle|generating|waiting>`, which reads `session_id` and `cwd` off
the hook's own stdin JSON and writes/updates that session's status file:
- **`UserPromptSubmit`**: fires when you send Claude a message → `generating`.
- **`PreToolUse`** (matcher `.*`, all tools): fires right before any tool
  call, including the moment right after you approve a permission
  prompt → `generating`, keeping the orb violet through multi-step tool use.
- **`Notification`** (matchers `permission_prompt` and
  `elicitation_dialog`): fires when Claude is genuinely blocked on you —
  a tool-approval dialog, or an interactive question tool (like
  `AskUserQuestion`) waiting for your answer → `waiting`. There's also an
  `idle_prompt` matcher (fires whenever Claude finishes a turn and is
  waiting for your *next free-form message*, approval-related or not) —
  deliberately left out here since it fires constantly and isn't a
  reliable "needs you" signal; add it back to the `Notification` array if
  you'd rather have that broader behavior.
- **`Notification`** with matcher `elicitation_complete`: fires right
  after you answer an interactive question → `generating`, so the orb
  doesn't stay stuck amber while Claude processes your answer (there's no
  `PreToolUse` between answering and Claude resuming, so without this the
  gap would show amber even though Claude's already back to work).
- **`Stop`**: fires when Claude's turn is fully done (no more tool calls,
  nothing pending) → `idle`.
- **`SessionEnd`**: fires `ClaudeBuddyHook.ps1 -State ended`, which
  **deletes** the session's status file (rather than writing to it) so its
  orb disappears immediately on a clean exit. It's a nice-to-have, not the
  primary cleanup mechanism — it's documented as unreliable on ungraceful
  termination (Ctrl+C notably; the hook gets cancelled before it can run),
  so the app still prunes stale files as a fallback (see `StaleAfter` in
  `SessionManager.cs`, and the "waiting is never pruned" note above).

Run `/hooks` inside Claude Code afterward to confirm all five events are
registered — do this separately for each install, since `/hooks` only
shows the config for the session you run it in.

**WSL** (hooks execute via a Linux shell that then calls out to Windows):
`claude-hooks-snippet-wsl.json` uses `powershell.exe`'s full path
(`/mnt/c/WINDOWS/System32/WindowsPowerShell/v1.0/powershell.exe`) plus
`-ExecutionPolicy Bypass` — both load-bearing, not stylistic:
- **Full path, not just `powershell.exe`**: hook commands run in a
  stripped-down environment that doesn't include the Windows PATH
  entries WSL normally injects into interactive shells, so a bare
  `powershell.exe` can't be found.
- **`-ExecutionPolicy Bypass`**: without it, running a `.ps1` file (as
  opposed to an inline `-Command` string) can hit `AuthorizationManager
  check failed` depending on the machine's default execution policy and
  the script's location/zone.

**Native Windows** (hooks execute directly as a Windows process, no Linux
shell in between): `claude-hooks-snippet-windows.json` calls plain
`powershell.exe` — it's already on the native Windows PATH, so no
`/mnt/c/...` prefix is needed or correct here (that path doesn't exist
outside WSL). `-ExecutionPolicy Bypass` is still needed for the same
reason as WSL.

Both variants land in the same real `%TEMP%\claude_buddy\` folder, since
`powershell.exe` resolves `$env:TEMP` to the actual Windows temp directory
regardless of which shell launched it — so a WSL session and a native
Windows session can run side by side and show up as two independent orbs
in the same `ClaudeBuddy.exe`.

These symptoms (and an earlier WSL-only one from before this script
existed — unescaped `$env:TEMP` getting mangled by the outer Linux shell
before PowerShell ever saw it) all look identical from the outside: the
hook fires, but the status file never updates and the orb never reacts. If
you suspect a hook isn't actually reaching PowerShell, temporarily add a
throwaway sibling hook to confirm the hook itself is firing before
debugging further downstream — `echo fired >> /tmp/some.log` on WSL, or
`cmd.exe /c echo fired >> %TEMP%\some.log` on native Windows.

## 3. (Optional) Launch it automatically

Press `Win+R`, type `shell:startup`, and drop a shortcut to
`ClaudeBuddy.exe` in the folder that opens. It'll then start quietly
whenever you log in.

## Notes / things you might want to tweak

- **Colors and animation**: `OrbWindow.xaml.cs` has `IdleColor` /
  `GeneratingColor` / `WaitingColor` at the top, and the breathing/pulse
  animations are in `StartIdleBreathing()` / `StartGeneratingPulse()` /
  `StartWaitingPulse()` — easy to retune speed, scale, or swap in
  different colors.
- **Stacking layout and staleness**: `SessionManager.cs` has the stacking
  math (`ReflowPositions()`) and the `StaleAfter` constant (5 minutes)
  that controls how long an idle/generating session's orb sticks around
  before being pruned — `waiting` is exempt, see above.
- **A session's orb doesn't appear until its first message.** There's no
  `SessionStart` hook wired up, so nothing writes a status file (and no
  orb appears) until the first hook that *is* wired fires — in practice,
  `UserPromptSubmit`, meaning you have to actually send a message.
  Add a `SessionStart` hook (`-State idle`) if you'd rather see the orb
  the moment a session launches.
- **Sound**: no audio right now, purely visual per your original ask. If
  you later want a soft sound on the waiting transition, that's a
  one-line `SystemSounds.Asterisk.Play()` (or point it at a custom .wav)
  in `OrbWindow.ApplyState()`.
