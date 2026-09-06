# Claude Buddy

One tiny always-on-top orb per running Claude Code session, stacked in the
top-right corner of your screen. Runs on **Windows and macOS** (Avalonia,
one codebase). Each orb has three states:
- **Slate-blue, gentle breathing, flat** — truly idle, nothing happening.
- **Violet, medium pulse, glowing** — Claude is actively generating a response
  or running tools.
- **Amber, fast pulse, glowing** — Claude needs something from you specifically:
  a tool-permission approval, or an answer to an interactive question.
  Claude finishing a response and waiting for you to type whatever's next
  does *not* trigger this — that's deliberate, not a bug; see the matcher
  note below if you want it back.

Only the two active states carry the halo. Idle is what most orbs are in most
of the time, so glowing about it spends the screen's attention on the one state
that wants none of it — the slow breath already says the session is there. It
matters more once you've picked your own colours: a dark idle colour renders a
halo as a smudge that darkens what's under it rather than as light.

Each orb shows the first letter of **what the chat is named**, in preference
order: the agent's own name if it's a member of an agent team (see below),
else the name you gave it with `/rename`, else the title Claude Code
generates once there's enough conversation to summarize, else the working
directory's name. A `/rename` wins even if Claude Code has since re-titled
the session, and the letter changes over as soon as a name appears — so two
sessions in one repo stop looking identical. Hover for the name and the full
path; the right-click menu leads with both, then offers to reset that session
to idle, **dismiss the orb** (removes it from screen by deleting its status
file — the session itself carries on, and its next hook event puts the orb
back), **end the session** (sends its `claude` process a polite terminate;
deliberate, irreversible, and never automatic), or exit Claude Buddy entirely.
The last two only appear for a session this app can actually act on: a local
CLI session for dismiss, and one whose pid was recorded for end.

Turn on **Two-letter initials** in Settings for a wider glyph: one letter
from each of the first two words of that same name (`Menu UX` → `Mu`), or
the first two letters of it when there's only one word. Off by default, so
existing installs keep looking exactly as they always have.

**Each local CLI orb wears a mark** in the bottom-left so Claude, Codex, Grok
and OpenClaw read apart from across the room: a terracotta spark, a green
star, a black X, a lobster. Remote-control orbs skip it — they are Claude
Code on another machine and already have a kind badge. Usage orbs wear the
same mark.

**Left-click-drag an orb to put it wherever you want**, and it stays there:
it holds that spot as other sessions come and go (the rest of the stack
closes up behind it), and it's remembered across restarts of Claude Buddy —
per working directory, since session ids are new every time. Right-click →
**"Return this orb to the stack"** to give the placement up and have that
orb rejoin the default top-right column. Two live sessions in the same
directory share one remembered spot: the first orb to appear takes it and
the other stacks normally, so they never land on top of each other. An orb
whose remembered spot is on a monitor you no longer have starts out back in
the stack.

If you've given a session a color with **`/color`**, that color becomes the
orb's **border and letter**. The fill is left alone deliberately — it's the
state signal, and amber-means-Claude-needs-you only works if it means that on
every orb. So color says *which* session, fill says *what it's doing*.

The three state colors are **yours to pick** — slate, violet and amber are only
the defaults. See **Orb colours** in the settings window; the rest of this file
calls them by their default names, since that's what you'll see until you change
them.

**By default, only colors you set with `/color` show up.** Claude Code also
gives every session an automatic accent (the color of its prompt border and name
chip), but that one is per-process and isn't written to the transcript — or
anywhere else on disk — so the hook has nothing to read and those orbs keep the
plain hairline border. Run `/color` in a session and its orb picks the color up
on the next hook fire, within a couple of seconds.

**Or let it colour them for you.** Settings → Claude Code sessions → **Give each
session a colour** is off by default, and when it's on a session with no colour
gets one. It is not a stand-in the app invented: `/color` persists a colour by
appending a single `{"type":"agent-color",…}` record to the transcript, and
Claude Code reads those records back when a session resumes — so the hook writes
the same record, and the colour survives a resume with the terminal agreeing.
That is the whole reason this is offered at all; a derived colour only this app
could see would disagree with what the terminal shows, and a ring is more useful
when it always means something real.

The colour comes from the working directory, so a project keeps the same one
across sessions and restarts, and `/color` still wins — yours is written later
and the newest record is the one read. It's off by default because it is the one
setting that writes to a file the app doesn't own.

**Codex has no per-session colour to write**, so its colour works the other way
round. Codex's colours live on *sections* — named groups of threads, `/section`
in its TUI — as an `{icon, color}` appearance, and a Codex orb takes its
section's colour when the session is filed under one. That is read and never
written: creating a section to hold a colour would reorganise your own thread
list in Codex's sidebar.

Most sessions are in no section, so with the setting on a Codex orb falls back
to a colour derived from the working directory — the same one the same project
gets under Claude Code, so a project looks the same whichever CLI you opened it
with. That is invented rather than native, and it is the one place this app
invents a colour. It is safe here for the reason it wasn't for Claude Code:
Codex displays no per-session colour anywhere, so there is nothing for a derived
one to disagree with.

**Agent teams get drawn as teams.** Every member of a team is a separate
`claude` process with its own session id, so a team of four arrives as four
unrelated-looking orbs. Instead, each member's orb is drawn **smaller** and
**points a tapered arrow at the orb of the session leading the team**, tinted
with the member's own `/color` so several arrows into one lead stay apart. The
stack gathers each team together — a lead, then its members, then whatever came
next — and **dragging a lead moves its whole team with it**, keeping the shape
you arranged them in. Dragging a *member* moves only that member, which is how
you pull one out to look at it; its arrow stretches to follow. The arrows are
click-through, so they never eat a click meant for the desktop underneath.

Each agent orb is lettered and labelled with **the agent's own name** —
`MenuUX`, `Narrative`, `HitReactSpec` — not the team's. Every member inherits
the team session's title, so a team of four otherwise drew the same letter four
times while your terminal had been calling them by name all along. Hover for
the agent and its team; the tray menu lists them by agent name too.

Nothing about this is guessed, and **no hook change is needed** — Claude Code
spawns each member as its own `claude` process and hands it
`--parent-session-id <lead>`, `--agent-name <name>` and `--agent-color <name>`
on the command line, so the app reads all three off the process it is already
tracking. The assigned colour becomes the orb's ring and its arrow when the
agent hasn't run `/color` itself; it is not the automatic accent described
above, which really is nowhere on disk.
Sessions that aren't in a team are completely unaffected: no arrow, full-size
orb, nothing read that wasn't already being read.

A team member whose lead has gone is **dimmed** rather than drawn as though it
were still part of something: its arrow has nowhere to point, and a full-bright
orb in a group whose lead has ended says the team is still running. It stays
clickable, and clicking it still goes somewhere — see **Background jobs and
parked sessions** below for what "dimmed" means and what the click does.

Team orbs obey **"Keep orbs for"** like everything else. An agent that has
finished its work goes quiet, and a quiet session fires no hooks, so its file
stops being touched and its orb is pruned on the usual schedule even though the
process is still alive — a team of three that has finished two of them shows
one. That's deliberate: the lifetime setting is yours, and a team isn't special
enough to overrule it. Raise it (or set **Forever**) if you'd rather watch a
whole team sit there.

Names and colors come out of the session's own transcript, where Claude Code
records them as `{"type":"custom-title",...}`, `{"type":"ai-title",...}` and
`{"type":"agent-color","agentColor":"green"}` — the hook reads the newest of
each and the app never has to guess. **Left-click an orb to jump to that
session's terminal**, best-effort:
- macOS: the exact iTerm2 pane or Terminal.app tab when possible,
  otherwise just activating the terminal app. **tmux sessions land on the
  right pane** — see below. The first click asks for macOS Automation
  permission to control your terminal — approve it once.

  **If clicks silently stop working**, that grant has been invalidated —
  macOS ties it to the app's code identity, and re-signing or replacing the
  bundle (any local rebuild, or switching between a locally built copy and an
  installed release) counts as a change. It is easy to misread, because a
  denied click looks exactly like one that landed on a terminal that was
  already frontmost: with a single terminal window you notice nothing until
  you click from *another desktop*, and then it looks like a Spaces bug.
  Worse, System Settings → Privacy & Security → **Automation** won't list an
  app whose consent was invalidated, so there is nothing there to re-tick.
  Force a fresh prompt:

  ```bash
  tccutil reset AppleEvents io.github.wtvamp.claudebuddy
  ```

  Then click an orb and approve. Running an installed copy from
  `/Applications` rather than out of `dist/` avoids this entirely, since its
  Developer ID identity is stable across rebuilds. The app now also logs this
  case explicitly rather than failing silently.

  **Crossing desktops takes two rules to get right**, both of them macOS's
  rather than ours, and both learned the hard way from clicks that appeared to
  do nothing:

  1. *The click has to arrive at all.* macOS spends the click that activates an
     inactive app: the window comes forward, the view never sees it, unless it
     answers `YES` to `acceptsFirstMouse:`. Claude Buddy is a background app and
     is essentially never the active one when you're on another desktop, so the
     first click was always the one being eaten — it looked exactly like "single
     click does nothing, double click works". Avalonia's view doesn't implement
     it and exposes no hook, so the answer is installed onto its class through
     the Objective-C runtime (`MacOSWindowExtensions.AcceptFirstClick`).
  2. *Activating is not the same as being taken there.* Activating an app raises
     whichever of its windows are on the desktop you're looking at, and only
     follows it to another one when it has none here. Ordering a specific window
     front does pull you across — but only once the app is **already active**;
     an activation still in flight lands afterwards and raises the local window,
     undoing it. So the scripts activate, wait for that to land, then select.
     Read them in `ITermSelectScript`: the comment there records both readings
     of this, because testing from a desktop with no terminal on it gives the
     opposite answer and looks conclusive.

  A related dead end, recorded so it isn't retried: `[NSApp deactivate]` before
  activating the terminal. Giving up active status hands it to whatever app was
  frontmost on the desktop you were *on*, and macOS follows that app — pulling
  you back where you started.
- Windows: the terminal window the session runs in — verified for Windows
  Terminal, plain `conhost`, and VS Code's integrated terminal, including
  restoring a minimized window. WSL sessions fall back to activating Windows
  Terminal / VS Code, since the Windows-side parent chain can't be traced from
  inside WSL.

  **With more than one Windows Terminal window open, a click may raise the
  wrong one**, and this is a dead end rather than a to-do. WT's
  monarch/peasant model puts every window of a launch context in a *single*
  process, so `Process.MainWindowHandle` — one handle per process — can't name
  the right one, and no WT API answers "which window hosts pid X".
  Enumerating the process's windows and reading their titles does work
  mechanically (both `EnumWindows` and UI Automation, which even exposes
  per-tab names), but Claude Code sets every console title to the same literal
  string `claude`, so there is nothing to match on. The hook could stamp a
  unique marker into the console title, except that the title is shared
  per-conpty and any prompt that rewrites it — oh-my-posh, most bash prompts —
  would erase the marker between the hook write and the click. An unreliable
  fix that also visibly renames your tabs is worse than a click that lands on
  a window of the right process. Selecting the exact *tab* is out for the same
  reason.

Click-to-focus needs the hook script from this version; sessions started
under an older copy just won't respond to clicks until they're restarted.

### tmux (macOS)

A session running inside tmux needs two separate things to happen, and doing
only one of them leaves you looking at the wrong thing:

1. **tmux has to select the pane.** The attached client is probably showing
   some other window, so activating its terminal alone would drop you
   somewhere else entirely. The hook records `$TMUX_PANE` (a server-unique
   pane id like `%3`) plus the socket path from `$TMUX`, and a click runs
   `select-window` + `select-pane` against them. If nothing is attached, the
   pane is still selected, so it's already current next time you attach.
2. **The right terminal window has to come forward.** Which app that is
   *can't* be recorded when the hook runs — you can detach a tmux session and
   reattach it from a different terminal, or from none — so it's resolved on
   every click from the live client's tty, by walking up that tty's process
   tree until it hits an `.app` bundle. That works for any terminal without a
   case per app: iTerm2, Terminal.app, Ghostty, WezTerm, kitty, Alacritty,
   VS Code. iTerm2 and Terminal.app additionally get the *exact tab* selected,
   since they expose a session's tty to AppleScript; everything else gets
   activated and relies on step 1 to have put the right pane on screen.

Details worth knowing:
- If the session is attached from several terminals at once, the most
  recently active client wins. If no client is on that session, the most
  recently active client elsewhere gets switched to it.
- iTerm2's native tmux integration (`tmux -CC`) is handled specially: the
  control client's tty is a hidden control tab, so that case skips exact-tab
  selection and just activates iTerm2, which mirrors tmux windows as native
  tabs and follows the pane selection itself.
- Inside tmux the hook deliberately does **not** record `ITERM_SESSION_ID`.
  It's inherited from whenever the pane was created and is stale as often as
  not; jumping to the wrong pane is worse than not jumping.
- The app can't rely on `PATH` to find `tmux` — launched from Finder or Login
  Items it gets the bare system `PATH`, with no Homebrew in it — so the hook
  records the tmux binary's location, with the usual install paths as
  fallbacks.
- **The Claude Desktop profile switcher ships on Windows too.** It was macOS-only
  when this file first described it, and two revisions of this bullet have since
  been overtaken — first by the Windows port landing, then by the mechanism
  changing underneath both platforms. What follows is what is true now; the
  measurements below were taken on a real Windows 11 box and still stand. There,
  Claude Desktop installs as an **MSIX package** (`C:\Program
  Files\WindowsApps\Claude_...`, ACL'd so the payload is readable but not
  executable).
  - **The app itself supports profiles**, and the same startup branch is present
    in the Windows bundle with no platform guard:
    ```js
    if (process.env.CLAUDE_USER_DATA_DIR) {
      const A = process.env.CLAUDE_USER_DATA_DIR;
      app.setPath("userData", A); app.setPath("logs", resolve(A, "Logs"));
    }
    ```
    Present is not the same as reached: on the builds measured for the section
    above, setting the variable no longer changes where macOS writes, and this
    app stopped depending on it on either platform. Note what that branch does
    with `logs` — moving them was the *variable's* doing, which is why they stay
    put now.
  - **The environment variable is the wrong lever on Windows.** Package
    activation doesn't inherit the launching process's environment (probe
    directory created, stayed empty), and setting it as a *user* environment
    variable in the registry doesn't reach it either, because the activation
    broker builds its environment at logon. Executing the packaged binary
    directly fails with `Access is denied`.
  - **A command-line argument is the right lever, and it works.** Being Electron,
    the app honors `--user-data-dir=<path>` at the Chromium level, with no
    environment variable involved — verified by launching with the flag alone and
    watching a complete 27-entry Electron userData tree appear in the target
    directory. And a packaged app *can* be handed a command line, unelevated, via
    `IApplicationActivationManager::ActivateApplication` (AUMID
    `Claude_pzs8sxrjxfjjc!Claude`). Verified end to end: `ACTIVATED pid=12604`,
    27 entries in the profile directory, while the already-running packaged
    instance carried on untouched — so the single-instance lock is per profile
    directory here as on macOS.

    The catch that hid this: `ActivateApplication` is a shell API and returns
    `E_ACCESSDENIED` from a non-interactive logon, which is what an SSH session
    gets. So does `Invoke-CommandInDesktopPackage`. Both work from the user's
    interactive session, which is where a tray app already lives. An earlier
    revision of this file concluded from those denials that the whole approach
    was impossible, and then that a 577 MB unpackaged copy was the only way
    through. Neither is true; no copy is needed.

    A bonus over the macOS path: because the argument sits on the main process's
    command line, working out which profile a running instance belongs to is just
    `Win32_Process.CommandLine` — no `KERN_PROCARGS2` equivalent, no memory
    reading.

    Not yet verified: two accounts signed in side by side on Windows. What's
    verified is that the profile directory is honored and a second instance runs
    against it.
  - **Icons can't be tinted.** No Dock, and the taskbar icon comes from the signed
    package, so the APFS-clone trick has no analogue.

  What Windows gets today: session orbs, the tray icon and menu, click-to-focus,
  chat names and colours, the settings window, persisted settings under
  `%APPDATA%\ClaudeBuddy`, **and the profile section** — `LaunchWindows` passes
  `--user-data-dir` through `ActivateApplication`, and `WindowsProcessScan` reads
  it back off `Win32_Process.CommandLine`, the equivalent of `KERN_PROCARGS2` on
  macOS with no memory reading needed. What does *not* port is the tinted Dock
  icon, for the reason above, and the LaunchServices URL routing, which has no
  Windows analogue to work around.
- **WSL + tmux is not covered.** The Windows hook is PowerShell running
  outside the Linux environment, so it never sees `$TMUX`; clicks on those
  orbs behave as they always have (activate the terminal window).

There's also a **status-bar icon** — macOS menu bar, Windows notification
area — that's there whether or not any session is running. Its color tracks
the most urgent session (amber if any session needs you, violet if any is
working, otherwise slate — or whatever you've set those three to, since the
icon is re-tinted to match), and its menu lists the live sessions by chat name
(falling back to folder name, same as the orbs, and truncated if it runs
long) — click one to jump to its terminal, same as clicking its orb. Two
sessions that end up with the same label get a short session-id suffix so you
can tell which is which. The menu is also the app's only permanent control
surface, since with zero sessions there are no orbs to right-click:
- **Show orbs** — hide the orbs and run status-bar-only. Sessions keep being
  tracked, so the icon and menu stay live. Remembered across relaunches, along
  with everything else in the settings window.
- **Reset all sessions to idle** — the bulk version of an orb's
  right-click reset, for an orb whose process is alive but whose colour is
  stuck. It used to be the tool for clearing up after Ctrl+C'd sessions as
  well; that part is now largely automatic, because a session whose process
  has gone loses its orb on the next scan and its leftover file a few minutes
  later (rules 3 and 5 under pruning below).
- **Settings…** — a small preferences window, grouped the way macOS groups
  its own: **Orbs** (show them at all, and **"Keep orbs for"** — 1 minute
  through 4 hours, or **Forever**, covered under pruning below), **Orb
  colours** (one colour picker per state, plus a **Reset** that puts the
  shipped three back), **Claude Desktop** (window tinting) and **Profiles**
  (per-profile name, colour and what the colour applies to). Everything applies
  as you change it and is written to disk immediately — the colour pickers wait
  a quarter-second after you stop dragging, since they'd otherwise rewrite the
  file on every pointer move, but the orbs and the menu-bar icon follow the
  spectrum live either way. There's no OK or Cancel, and on macOS no Done
  button either — Escape, Cmd-W or the window's close button, like any other
  Mac window.

  Two things a custom colour doesn't reach. The orb's letter and its plain
  hairline border stay near-white, so a very pale fill makes the letter hard to
  read — the fills are meant to be saturated. And the **app icon** (Dock,
  Finder, the .exe) keeps the defaults permanently: it's baked into the bundle
  at build time, and rewriting an installed app at runtime is exactly the
  privacy wall the Claude Desktop Dock-icon tinting already has to work around.
- **Quit Claude Buddy**.

On macOS the menu opens on a left-click of the menu-bar icon. On Windows it's
a **right-click**, and there's one wrinkle worth knowing: Windows 11 does not
put newly registered tray icons on the taskbar. It files them in the hidden
overflow behind the **`^`** chevron, so after the first launch you'll find
Claude Buddy there — drag it onto the taskbar once to pin it (that's what
sets `IsPromoted` in `HKCU\Control Panel\NotifyIconSettings`, which Windows
then remembers). Nothing to configure in the app; it's how Windows 11 treats
every new icon.

### Voice dictation (optional, off by default)

Turn on **Enable voice input** in the settings window and hovering an orb
fades in a small mic badge over its corner. Click it, speak, click again (or
wait 30 seconds) to stop. What you said is transcribed **entirely on this
machine** — no cloud speech-to-text service, no Anthropic API call, nothing
sent anywhere — and typed into that session's terminal for you to read over.
**Enter is never pressed for you**; a misheard word is easy to fix or discard
before it becomes a prompt.

That "entirely on this machine" part is the point, not an afterthought: this
has to work on a plain Claude subscription rather than API billing, so the
whole pipeline avoids anything that would need its own separate account or
per-minute charge. Transcription is [Whisper.net](https://github.com/sandrohanea/whisper.net)
against a small English-only model (`ggml-base.en`, downloaded once — about
150MB — the first time you turn the setting on, cached beside `settings.json`
so it isn't fetched again). Typing the result in reuses the exact terminal/
tmux-pane identification click-to-focus already does (see above and
`TerminalFocuser.cs`); dictation just adds a second step after focusing —
typing the words in — rather than a new way of finding the pane.

**None of this has been hand-verified on a real machine yet** — it's new, and
this repository is deliberately careful about that distinction elsewhere (see
`docs/*-findings.md`). What's true by construction versus what still needs a
real run-through:

- **tmux (macOS)**: reuses the pane resolution described above, then runs
  `tmux send-keys -t <pane> -l "<text>"` — literal mode, so tmux won't try to
  interpret the words as key names. Should work wherever click-to-focus
  already does; not yet confirmed against a live session.
- **Non-tmux macOS** (iTerm2, Terminal.app, or just whichever terminal is
  frontmost): after focusing, runs `tell application "System Events" to
  keystroke "<text>"`. This needs **Accessibility** permission — a *different*
  grant from the Automation permission click-to-focus uses, under System
  Settings → Privacy & Security → **Accessibility**. Not yet confirmed whether
  macOS prompts for it automatically the first time or whether it has to be
  added by hand; if dictated text silently doesn't appear, check there first.
  Like the Automation grant, expect it to be tied to the app's code identity —
  a rebuild may invalidate it the same way (`tccutil reset Accessibility
  io.github.wtvamp.claudebuddy` to force a fresh prompt).
- **Windows**: focuses the terminal the same way a click does, then types the
  words via `SendInput` (Unicode key events, not `SendKeys` — dictated text
  isn't written in `SendKeys`'s escaping language). Not yet run against a real
  Windows Terminal session.
- **Microphone capture**: [PvRecorder](https://github.com/Picovoice/pvrecorder)
  opens the input device directly; on macOS this needs the
  `NSMicrophoneUsageDescription` Info.plist key (present in
  `tools/build-macos-app.sh`) and, on a hardened-runtime signed build, the
  `com.apple.security.device.audio-input` entitlement — believed necessary by
  the same reasoning as the Automation entitlement, not separately confirmed.

If you turn this on and something above doesn't work as described, that's the
gap this section is flagging — not a contradiction of it.

### High-quality voice (optional, off by default)

The speaker button on an orb's flyout reads the latest assistant turn aloud. On
macOS that uses `say` with Apple's Enhanced and Premium voices and sounds good.
On Windows the best voice any third-party app can reach is `Microsoft Zira
Desktop`, which is over a decade old and sounds it.

Windows *does* ship better voices, and they are deliberately out of reach.
Narrator's natural/HD voices — the ones you add under Settings → Accessibility →
Narrator → Add natural voices — install as model data that registers no voice
token in either the SAPI5 or `Speech_OneCore` registry hive. They are invisible
to `System.Speech` and to WinRT's `SpeechSynthesizer.AllVoices` alike, and the
only bridge that exists works by extracting encryption keys out of system files.
Microsoft's own on-device neural TTS (Azure Embedded Speech) is available by
application only, to customers with a direct Microsoft account team. So the only
way for this app to sound better on Windows is to bring its own model.

**High-quality voice (experimental)** in the settings window does that. It
downloads a neural speech engine and its model — about 300 MB in total, once —
into `%APPDATA%\ClaudeBuddy\speech-engine` (`~/Library/Application
Support/ClaudeBuddy/speech-engine` on macOS), and speaks entirely on this
machine. No cloud service, no API key, nothing leaves the computer, same as
dictation.

It is offered on macOS too, even though Apple's voices are already decent: some
people prefer Kokoro's, and the engine turned out to be genuinely portable. The
one thing that isn't shared is playback — macOS synthesises to audio and plays it
with `afplay` rather than through KokoroSharp's own OpenAL path, which crashes
before making a sound. `docs/macos-neural-voice-findings.md` has the evidence.

Worth knowing before you turn it on:

- **It takes a few seconds to start talking** — roughly three, against about half
  a second for the built-in voices. Most of that is loading the model and its
  phoneme lexicon. The speaker button turns amber with an hourglass while it
  works and blue with a stop square once audio is playing, so the wait is
  visible rather than mysterious.
- **It costs real CPU**: about one core-second per second of speech, against a
  fifteenth of that for the built-in voices, deliberately capped at two threads
  so a background utility can't commandeer the machine. Nothing is used while
  it's silent.
- **Nothing ships in the installer.** The engine is a separate downloaded
  process, so the app stays the same size and anyone who doesn't enable this pays
  nothing for it — on either platform.

Built on [Kokoro](https://huggingface.co/hexgrad/Kokoro-82M) via
[KokoroSharp](https://github.com/Lyrcaxis/KokoroSharp); the voices are
Apache-2.0 and their licence travels inside the downloaded bundle.

#### Adding voices

Drop `.npy` Kokoro voice files into `%APPDATA%\ClaudeBuddy\voices`
(`~/Library/Application Support/ClaudeBuddy/voices` on macOS) and they
appear in the picker alongside the bundled ones. That directory is deliberately
outside the engine's own folder, which an upgrade replaces wholesale — anything
put beside the bundled voices would be deleted by the next release.

The filename matters more than you'd expect: Kokoro reads the language and
gender from its prefix, so `af_` (American female), `am_` (American male),
`bf_`/`bm_` (British) are what make a voice show up in the picker. A prefix
claiming another language is what hides one — `zf_mine.npy` is filed under
Mandarin and filtered out of an English list. A name with no recognisable prefix
at all is *not* dropped; it falls through to the American English list and
appears like any other voice. Copying the naming of the bundled ones is still
the safe move, since it's the prefix that decides how the voice is treated.

Worth knowing what a Kokoro "voice" is before hunting for more: it's a 510 KB
array of style vectors for that one model, not an engine and not a recording. So
files from other systems — Piper's `.onnx` voices, SAPI voice packs, RVC `.pth`
models — are not interchangeable with it, and the 54 that ship are essentially
the whole published set.

### Speaking with something else entirely

If you want a different engine, a different voice, or a chain of both, point the
app at your own command and it will use that instead of anything built in:

```jsonc
// %APPDATA%\ClaudeBuddy\settings.json  (~/Library/Application Support/… on macOS)
"speakCommand": "C:\\tools\\my-voice.cmd",
"speakCommandArgs": ["--voice", "whatever"]
```

Its voices then appear in the **Speak voice** picker alongside the built-in ones,
marked `(custom)` — the system voices are marked `(system)` and the neural ones
`(Kokoro)`. Picking a voice is what selects the engine, so all three are
available at once rather than one hiding the others.

The whole contract:

- The text arrives on **stdin as UTF-8**. Not as an argument — an assistant turn
  runs to 1500 characters of quotes, apostrophes, newlines and code punctuation,
  and a pipe has no escaping rules to get wrong.
- **Exit when you've finished speaking.** The app treats your process being alive
  as "speaking", which is what makes the flyout's speaker button show a stop
  square.
- **Being killed means stop.** Pressing the button again kills your process; you
  don't need to handle anything.
- Optionally, print `speaking` on stdout the moment audio actually starts, and
  the button will show an hourglass until then instead. Skip it and the button
  simply shows stop for the whole run.

That's it — no plugin API, no manifest, nothing to compile against. A batch file
that pipes stdin into some other tool is a complete implementation. Arguments go
through `ArgumentList`, so paths with spaces need no quoting of your own.

#### Letting it offer more than one voice

If your command can speak in several voices, tell the app how to list them and
they each get their own entry in the picker:

```jsonc
"speakVoicesCommand": "C:\\tools\\my-voice.cmd",
"speakVoicesCommandArgs": ["--list-voices"]
```

- It should print **one voice name per line** on stdout and exit. Nothing else is
  parsed.
- The chosen name reaches `speakCommand` in the **`CLAUDEBUDDY_VOICE`**
  environment variable. Not as an argument: `speakCommandArgs` is yours, and
  appending a positional argument would break a wrapper that takes fixed ones. A
  command that ignores the variable keeps working.
- `speakVoicesCommandArgs` is deliberately separate from `speakCommandArgs`. One
  script usually serves both roles by branching on a flag, and sharing one list
  would hand `--list-voices` to the speaking call too — which would then list
  voices instead of talking.
- Leave `speakVoicesCommand` unset and the picker shows a single **Custom
  command** entry, meaning "this command picks its own voice".

The listing command is run while the settings window is being built, and is given
ten seconds before it's killed — a wrapper that hangs shouldn't hang the UI with
it. The result is cached until the window is reopened.

Two deliberate consequences. Your command takes precedence over both built-in
engines, and if it fails to start the app **does not** quietly fall back to a
system voice — it reports the failure on stderr and stays silent, because a
silent substitution looks like your engine working badly rather than not running
at all. And whatever is on the other end is yours to choose and yours to license;
the app makes no assumptions about it, exactly as it makes none about the hook
script it asks you to wire into Claude Code.

Settings-file only, with no row in the settings window: a free-text command box
invites pasting something and hoping, and this belongs next to the hook JSON with
the rest of the power-user surface.

### Claude Desktop profiles (macOS)

Unrelated to session monitoring, and sharing nothing with it but the menu:
the status-bar menu can run several copies of the **Claude Desktop** app side
by side, each signed into a different Anthropic account. Claude Desktop signs
into one account at a time and keeps that login in its user-data directory
(`Cookies` → `sessionKey`, `config.json` → `oauth:tokenCache`) rather than the
Keychain, so a second account is a second directory — selected with
Chromium's `--user-data-dir` switch, and it takes no single-instance lock, so
the instances genuinely coexist. (`CLAUDE_USER_DATA_DIR` is passed alongside it
and used to be the whole mechanism; Claude Desktop 1.34493.1 ignores it, which
is why the switch is now what actually decides. See the notes below.)

Profiles are **discovered from disk**, not configured: any directory in
`~/Library/Application Support` named `Claude` or `Claude-<something>` that
looks like a real profile (or is empty). `Claude` shows as **Default**,
`Claude-work` as **work**. Each gets a submenu with launch/bring-to-front,
quit, and reveal logs; a filled dot means it's running. **New profile**
creates `Claude-Profile-N` and launches it — sign in there with the second
account. Renaming one means quitting it and renaming the folder, which is
what **Reveal profiles folder** is for. The section is hidden entirely if
`Claude.app` isn't installed.

Each profile gets a **colour**, derived from its folder name so it survives
restarts and needs no config, and that one colour shows up on four surfaces:

- **The tray menu** — a real swatch beside each row (filled = running, hollow =
  stopped). Colour is identity, fill is state, exactly as with the orbs.
- **The Dock** — each created profile launches from its own APFS clone of
  `Claude.app` whose icon is Claude's mark recoloured. 1.5 MB of real disk for a
  754 MB bundle. Default keeps the bundle you installed, icon and all.
- **The window itself** — the frontmost instance gets a coloured border and a
  faint wash, drawn by a click-through overlay pinned to its frame.
- **Light or dark** — each profile's own `userThemeMode`, set from its submenu
  while it's stopped.

Details worth knowing:

- **Why the Dock clone is safe.** A custom Finder icon lives in an `Icon\r` file
  at the bundle root plus a `com.apple.FinderInfo` xattr — both *outside*
  `Contents/`, which is what the code signature seals. The result: `codesign
  --verify` passes, `spctl` still reports "Notarized Developer ID", and the
  CDHash is byte-identical to Anthropic's. That last part is the point — the
  running code identity is unchanged, so the `Claude Safe Storage` keychain ACL
  still matches (stored logins keep decrypting) and existing TCC grants still
  apply. Renaming the app would mean editing `Info.plist`, which forces a
  re-sign and loses all of it, so every clone still calls itself "Claude".
  Only `codesign --verify --strict` objects, over the xattr.
- **Clones go stale after a Claude update.** Squirrel only updates
  `/Applications/Claude.app`, so **Dock icons → Rebuild after a Claude update**
  re-clones. Bundles live in `~/Library/Application Support/ClaudeBuddy/bundles/`
  and are pure cache — deleting them only costs the colours. Each is named
  exactly `Claude.app` inside a per-profile directory, because the process scan
  matches on the path suffix `/Claude.app/Contents/MacOS/Claude`; naming bundles
  after profiles would silently break running-detection for cloned instances.
- **Why the window tint is an overlay rather than real theming.** There is no way
  in: the app has no accent-colour concept (its theme is a `body` class driven by
  `prefers-color-scheme`), Chromium removed `--user-stylesheet` years ago (0
  occurrences in the shipped Electron binary), and remote debugging — the one
  route that could inject CSS — is refused unless `CLAUDE_CDP_AUTH` carries an
  Ed25519 signature over `timestamp.base64(userDataDir)`, verified against a key
  embedded in `app.asar`, bound to that exact profile path and valid for five
  minutes. So the tint is drawn over the app instead. Frames come from
  `CGWindowListCopyWindowInfo`, which gives bounds and owner pid with **no**
  permission prompt (only titles and images need Screen Recording).
- **The tint only follows the frontmost instance.** The overlay is topmost, so
  showing it for a background window would drop a coloured rectangle on top of
  whatever app you were actually using. Windows on other Spaces are skipped too:
  they still count as "on screen" to CGWindowList but report coordinates in that
  Space's frame, far outside any display. Toggle it under **Dock icons → Tint the
  active window**, or in the settings window; like the orb toggle, it's
  remembered across relaunches. Verified
  pixel-exact against a live window, and click-through, so clicks reach Claude.
  Only tested on a single display.
- **`Claude-3p` and `Claude-dev` are skipped.** `-3p` is Claude Desktop's own
  sidecar config directory (`configLibrary/`, `deploymentMode`) that a normally
  launched instance reads and writes — offering it as a profile would point a
  second Chromium at a directory the running app is already using, and
  concurrent access to one user-data directory corrupts leveldb and SQLite.
- **A profile is selected by two things, and only one of them still works.**
  Every created profile is launched with both `--env
  CLAUDE_USER_DATA_DIR=<dir>` and `--args --user-data-dir=<dir>`. The variable
  is what Claude Desktop's own JavaScript reads, and **1.34493.1 ignores it**:
  an instance launched with it pointed at an empty scratch directory left that
  directory empty and opened 45 files under `~/Library/Application
  Support/Claude` instead. Measured with `lsof`, against the installed bundle
  rather than a tinted clone. That is what "it opens the same profile twice"
  was — every row launched a second Chromium onto Default, which is also the
  concurrent-leveldb hazard this feature exists to prevent.
  `--user-data-dir` is Chromium's own switch, handled inside the Electron
  framework rather than in Claude Desktop's JavaScript, so it still works;
  Windows has passed it since the port. Both are sent, so a build honouring
  either lands in the right place, and `--args` goes last because `open(1)`
  hands everything after it to the application untouched.
- **Default is launched differently, on purpose** — plain `open -n -a <path>`,
  with neither selector. Pointing either at the app's own default directory is
  not the same thing to it as omitting them: it suppresses the app's own
  resolution of that sidecar directory, so a tray launch could re-trigger the
  enterprise deployment-mode chooser on an already-configured profile. (This
  used to add "and it would start a second log history under `Claude/Logs/`",
  which was true while the variable was the mechanism and is not true now —
  see the next bullet.)
- **Reveal logs picks by recency, because no fixed answer is right.** Logs used
  to follow the profile — but only because `CLAUDE_USER_DATA_DIR` moved them by
  hand, in Claude Desktop's own JavaScript. `--user-data-dir` is Chromium's
  switch and sets userData only, so on a current build every profile's logs stay
  at `~/Library/Logs/Claude` while the `<profile>/Logs` directory left over from
  when the variable worked sits there looking correct and weeks stale. Reveal
  logs therefore opens whichever of the two was *written to* most recently,
  which needs no opinion about which Desktop build is installed and is the
  question you were asking anyway.
- **Running instances are detected by scanning processes, not by tracking the
  ones we launched** — `proc_listallpids` + `proc_pidpath` to find Claude
  Desktop main processes, then `sysctl KERN_PROCARGS2` to read
  `--user-data-dir` off each one's command line, falling back to
  `CLAUDE_USER_DATA_DIR` in its environment — which is right on a Claude Desktop
  build that still honours the variable, and a misreport on one that doesn't:
  there, an instance carrying only the variable is really on Default, and
  reading it as the profile's hides a second Chromium on Default rather than
  counting it. Nothing in argv distinguishes the two cases, so this is a known
  cost of keeping the fallback, not an oversight. So an instance you
  started from the Dock shows up too, and the state survives restarting Claude
  Buddy. (Not `ps eww`: it prints the environment space-separated, and every
  profile path contains a space — `Application Support` — so its output can't
  be parsed back into paths.)
- **Quit is a real quit on macOS**, an Apple Event via `NSRunningApplication`,
  so it runs the app's shutdown and can be refused — by an unsaved-work dialog,
  or by a Cowork VM or local-agent session. A refusal shows up as *"allow
  Automation"* if it was a permission problem, and after a timeout the item
  becomes **Force quit**, which needs a second deliberate click. Nothing on
  macOS ever escalates to a kill on its own.
- **On Windows there is no such thing, so Quit asks and then terminates.**
  Measured on a real installed build: `WM_CLOSE` makes Claude Desktop hide to
  the tray and keep running, and `WM_ENDSESSION` — the message Windows' own
  shutdown sequence uses, which Electron is documented to honour where
  `WM_QUERYENDSESSION` is ignored — did nothing across two instances. There is
  no external "please quit" for this app. So Quit posts `WM_CLOSE` to every
  window of the process, waits 2.5s off the UI thread in case a future build
  does honour it, and otherwise ends the process tree. Measured end to end at
  ~2.5–3.2s from click to gone.

  That is safe, and it was measured rather than assumed: three
  kill-and-relaunch cycles on a live profile, `PRAGMA integrity_check` on all
  five of its real SQLite stores (`Network\Cookies`, `DIPS`,
  `Network\Trust Tokens`, `Shared Dictionary\db`, `WebStorage\QuotaManager`),
  15 checks, every one `ok`, and the profile started clean each time. Chromium
  is built to survive abrupt termination — it has to survive power loss. The
  corruption this project is careful about is a *different* failure: two
  instances sharing one userData directory, which is what `LaunchGate` and the
  re-scan inside it exist to prevent. Conflating the two is what kept Quit
  broken longer than it needed to be.

  **Force quit stays in the menu**, now as a fallback rather than the routine
  path. It also can no longer strand an instance: the offer used to expire
  after 60s while the app was still running, and because
  `Process.CloseMainWindow()` only finds *visible* windows, a second Quit on an
  already-hidden instance failed with *"couldn't quit"* and left no route to
  end it from the app at all.
- **The auto-updater is shared.**
  `~/Library/Caches/com.anthropic.claudefordesktop.ShipIt/` is keyed by bundle
  id, not by profile, so two instances updating at once can collide. Nothing
  the app can do about it.
- **Each profile is a separate device** as far as the server is concerned —
  its own `ant-did`.
- `CLAUDE_BUDDY_PROFILE_ROOT` overrides the directory profiles are discovered
  in, which is how to try this out without touching your real one.
- `CLAUDE_BUDDY_BUNDLE_ROOT` overrides where the cloned, recoloured `Claude.app`
  bundles are cached (normally
  `~/Library/Application Support/ClaudeBuddy/bundles`). Same purpose: the cache
  holds real 753MB clones whose icons you are looking at, so anything poking at
  it — a test, or a manual experiment — should be pointed somewhere else first.
  Deleting either directory costs nothing but the coloured icons, which are
  rebuilt on next launch.

It works by watching a small folder in the OS temp directory
(`%TEMP%\claude_buddy\` on Windows, `$TMPDIR/claude_buddy/` on macOS) that
fills up with one JSON status file per session — `<session_id>.txt`,
containing `{"state": "...", "cwd": "...", "title": "...", "color": "...", ...}`
— written by a tiny script that Claude Code hooks invoke:
`ClaudeBuddyHook.ps1` (PowerShell, Windows/WSL) or
`ClaudeBuddyHook.sh` (bash, macOS). No network calls, no polling of Claude
Code itself, no persistent process beyond the hook calls themselves.

An orb goes away when any of six things happens.

1. **Its `SessionEnd` hook fires** — a clean exit like `/exit` — which deletes
   the status file outright.
2. **Another session id from the same process has overtaken it.** A `claude`
   process mints a new session id whenever you `/clear`, resume, or start a new
   conversation, and the hook writes a *new* status file each time — nothing
   deletes the old one, because `SessionEnd` only fires when the process itself
   exits. Within one pid only the most recently written file is the live
   session, so the rest go immediately. Without this they were invisible to
   rules 3 and 4 alike (the pid is live, the file exists) and one terminal
   showed several orbs for a whole lifetime, some of them stuck on a `generating`
   they'd never be told to leave. Sessions running side by side are separate
   processes with separate pids, so this never merges two real ones; files from
   a hook too old to record a pid are left out of it entirely.
3. **Its `claude` process is gone.** Both hooks record that process's pid
   (`session_pid`), and every scan checks whether it still exists; if it
   doesn't, the orb goes regardless of the lifetime below, including under
   Forever, and including a session sitting on `waiting`. This is the Ctrl+C
   case: `SessionEnd` is documented as unreliable on ungraceful termination, so
   the file survives its session, and the pid is what tells "still running"
   from "left behind". A session started under a hook older than this field has
   no pid recorded and falls back to the timer alone.
4. **The lifetime expires** — its file hasn't been touched for however long
   **Settings → Orbs → "Keep orbs for"** says. That's 5 minutes out of the box,
   anything from a minute to four hours, or **Forever**, which never prunes on
   time at all. A session on `waiting` (amber) is exempt from *this* rule
   deliberately: nothing refreshes that file while you're away from an
   unanswered prompt, so timing it out would hide the orb exactly when it's
   trying hardest to get your attention. Rules 2 and 3 still apply to it, which
   is what keeps a Ctrl+C'd prompt — or a `/clear`ed one — from sitting on
   screen forever.

5. **You dismissed it** — right-click → "Dismiss this orb", which deletes the
   status file the way rule 1 does. Not permanent by nature: the session is
   untouched, so its next hook event writes the file again and the orb comes
   back. That is the point of it being separate from "End this session", which
   stops the process and takes the orb with it through rule 3.

6. **Its turn was backgrounded.** Backgrounding a running turn forks the
   conversation into a background job — the fork takes the title, gets its own
   session id and its own status file, and wears the gear-badged orb — while
   the interactive session it forked from never fires another hook: no `Stop`
   event ends the turn it handed away. Its file freezes on `generating` with a
   live pid and a real terminal, so rules 2, 3 and 4 all wave it through and
   the same conversation draws two orbs, one of them a lie no hook will ever
   correct. Every scan therefore reads the last 32KB of such a session's own
   transcript, and a handoff marker with nothing conversational after it takes
   the leftover orb. It comes straight back the moment anyone types in that
   session again — the transcript grows and the marker stops being the last
   word — and a transcript that can't be read takes nothing, so the failure
   direction is a duplicate orb rather than a hidden session. A session the
   daemon lists as a running job is never asked at all, which is what stops
   the fork (whose transcript inherits the marker) from reading itself as the
   leftover.

**The app also deletes status files it is sure are finished with.** Until
recently nothing did, apart from the `SessionEnd` hook — so a Ctrl+C'd session's
file stayed in the temp directory for good, and a finished background job's
stayed *and could never be caught*, because the pooled worker behind it is kept
alive on purpose and so its pid answers forever. Directories with dozens of them
were normal. Every scan now looks for the three facts that mean a session is
genuinely over — **its process has exited**, **the daemon says its job is
done**, or **its own transcript records the turn being handed to a background
job with nothing having happened since** — and deletes the file about ten
minutes after it first sees one. The third is the only one of them that deletes
a file whose process is still alive, and what makes that safe is particular to
it: the conversation fires its hooks under the fork's session id now, so
nothing will ever write that file again. If the interactive session is used
after all, its next hook event writes it back from scratch, exactly as after a
dismissal.

The ten minutes is a grace period, not a delay for tidiness: the evidence can be
briefly wrong (a pid that could not be read for a moment, a job reported done
just before it is resumed), and the clock restarts the instant the evidence goes
away. Nothing else counts as evidence — not a quiet session, not one whose orb
was pruned by "Keep orbs for", not one nothing could be clicked through to. Those
are statements about what this app can *see*, and a status file is the only place
a live session's terminal coordinates and colour live. If the app does delete one
it should not have, the session's next hook event writes it back.

Right-click → "Reset this session to idle" is still there for a session whose
process is alive but whose orb is stuck amber.

**Scope**: this only tracks Claude Code sessions that read a `settings.json`
you've wired up per step 2 below. Each Claude Code install — WSL (per Linux
user), native Windows, macOS — has its own, unrelated `settings.json`, so a
session won't show up until you add the matching hooks to *its* config.
The app itself doesn't care where a status file came from. On Windows, both
WSL and native Windows hooks ultimately run `powershell.exe` as a normal
Windows process, so `$env:TEMP` resolves to the same real folder either way
and their orbs happily stack together in one running `ClaudeBuddy.exe`.
This is just a matter of wiring more hook configs, not a hard limitation — the
Windows installer, `install-windows-hooks.ps1 -Wsl`, and the running app's
Settings window (see below) all reach every WSL distro's *default* Linux user
automatically. A second Linux user account inside the same distro is the one
combination left unwired, since that needs hooks added inside *their* account
specifically — the "By hand" section further down still covers that case.

## Background jobs and parked sessions

Claude Code can run work with nobody sitting in front of it: a background job
(`claude bg-...`, or one dispatched from `claude agents`) runs inside a pooled
worker with no terminal of its own. Those sessions fire the same hooks as any
other, so they have always had orbs — and until recently they were drawn
identically to a session someone was typing in. Fifteen orbs breathing away on a
machine whose owner considered it idle is what prompted this section.

Three things now say what they are.

- **A gear badge (⚙)**, bottom-right, the same slot the `⇄` badge uses for a
  session on another machine. It marks a background job whether or not anything
  is happening in it: the badge says what a session *is*, and that does not
  change while it runs.
- **Dimming.** A job sitting between turns — its worker alive and resumable,
  nothing in flight — is drawn at reduced opacity and stops breathing. So is a
  team member whose lead has gone. The orb keeps its colour and its letters, so
  you can still find it; it just stops claiming to be busy. Work resuming
  restores it on the next scan, which is about two seconds.
- **Clicking one opens it.** There is no window to jump to, so the click runs
  `claude attach` on that session in a terminal instead. A team member whose
  pane is alive in a tmux server nothing is attached to gets a terminal attached
  to that server, landing on its pane. Both are macOS only for now.

Where "between turns" comes from: `claude agents --json`, which the app already
asks about once per scan at most, and only when something on the machine could
be a background session — a machine running nothing but terminal sessions never
pays for the question. The daemon's answer is cached for about ten seconds, so
an orb can take that long to go dim; going *bright* again is immediate, because
the session's own status file reports the new state instantly and the app
believes the fresher of the two. An orb that is late to dim says nothing is
happening a few seconds after it stopped; an orb that was late to brighten would
be a lie about work you are watching happen.

Nothing here hides an orb. A parked job is real, resumable and worth clicking,
and how long a quiet session stays on screen is what **"Keep orbs for"** is for.

**Messaging one.** A background job or an `--agent` child never has a terminal
to type into or attach to at all, on any machine — the ⚙ badge above is what it
is, not a temporary state. Its chat panel offers a send anyway: the message is
handed to Claude Code's own IPC socket for that session rather than typed
into a pane that doesn't exist, and it reads at the session's next turn, not
immediately — the composer says so ("Message it — it reads this at its next
turn") rather than implying a pane it doesn't have. It arrives as a message
from Claude Buddy, not as keystrokes, so **built-in slash commands don't run**
this way, though a project's own custom skill commands do, since those are
just instructions the model reads. This works both for a background job on
this machine and, over the mirror link, for one on a peer machine — the same
mechanism either way, just addressed through the far Buddy instead of straight
to the socket. See `docs/headless-delivery-findings.md` for what was measured.

## Chatting with a session from its orb

Hover an orb and the flyout has a keyboard button (⌨). It opens a small panel
under the orb with that session's conversation in it — what it said, what it is
thinking, the tools it reached for — and a line to type in.

**It is the same conversation as the terminal's, not a copy.** Claude Code
writes every session's transcript to a file, the hook already tells Claude Buddy
where, and the panel reads it. So anything you type in the terminal shows up in
the panel. And sending from the panel types into the session's tmux pane, so
anything you send from the orb shows up in the terminal too, exactly as if you
had typed it there. There is no second conversation to get out of step.

Two honest limits. The panel updates a **block at a time** rather than a word at
a time — each thinking pass, each tool call and each paragraph appears as it
finishes, a few seconds behind the terminal's own streaming. And a half-typed
draft is not shared: the panel keeps its own, and so does the terminal.

Clicking the orb still goes to the terminal. That is what a click has always
meant here and the panel is a second destination, not a replacement — which is
why it's a separate button rather than a change to the click. (Gateway orbs have
no terminal to go to, so for those the click opens the panel directly.)

**Typing back is a second switch, off by default:** Settings → Claude Code
sessions → **Allow replying to sessions**. With it off the panel is a live view
of what your sessions are doing. With it on you can also type into them, answer
their permission prompts, and interrupt a run. Seeing what a session is doing
and being able to drive it are different powers, so the second one is asked for
separately — the same split the OpenClaw section below makes, for the same
reason.

**Sessions not running under tmux stay read-only.** The only way to type into
those is to bring their terminal to the front first, which defeats the point of
chatting from an orb; dictation already does that and is welcome to, but a chat
panel that raised a window on every message would not be one. The input box says
so rather than being greyed out.

When a session stops for a **permission prompt**, the panel says so and offers
the dialog's own options as buttons. It reads them off the pane with
`tmux capture-pane` rather than assuming what "1" means — the dialog is drawn by
the terminal UI and never reaches the transcript, so the screen is the only
place its wording exists. If it can't read the dialog it says only "answer in
the terminal", because a button labelled "Approve" that sent something else
would be worse than no button. That parsing has a test suite of its own
(`dotnet run --project tests/TranscriptTests`) whose fixtures are transcribed
from real captures.

## OpenClaw agents (experimental, off by default)

Claude Buddy can also show an orb for each recently active session on an
[OpenClaw](https://docs.openclaw.ai) gateway — the agents you talk to through
Discord or its TUI — beside your Claude Code ones. They breathe when idle and
pulse violet while an agent is working, the same as any other orb.

It is **off until you turn it on**, and while it is off the app opens no socket,
starts no background task and generates no key. Turn it on in **Settings →
OpenClaw agents**, then give it:

- **Gateway address** — the machine running the gateway. An IP address rather
  than a hostname, because the certificate it serves carries no hostname to
  validate against.
- **Gateway token** — the `gateway.auth.token` from the gateway's own
  `openclaw.json`. It is kept out of `settings.json`, in a file only your user
  can read, beside the device key.

The first connection asks the gateway to pair this machine, and then waits: on
the gateway, run `openclaw devices approve --latest` and check it names
`gateway-client` before approving. Claude Buddy asks for **`operator.read` and
nothing else**, so it can see what your agents are doing and cannot ask them to
do anything.

Orbs are named for the agent, not its id: OpenClaw keeps a name per agent, so
an orb reads **Lilibeth — #general** rather than `main`, and its letter is L
rather than a fourth M. The second half says which conversation it is, because
one agent commonly has a DM with you, a DM with someone else and two channels
going at once.

**An agent's picture is its orb, and a channel's orb is everyone in it.** An
agent with an avatar set in OpenClaw wears it instead of its letters, with the
state moving out to the ring. The orb Claude Buddy draws for a *channel* — the
one every agent talking in that channel points at — is cut into a wedge per
member: half each for two agents, quarters for four. Someone with no picture
still takes a wedge, in the colour their ring wears everywhere else, and a
channel where nobody has one keeps the channel's initials. At most four are
drawn, and they are the four most recently active; the wedges are ordered so
that somebody speaking does not move anyone's face.

**A channel gets its own orb only once a second agent is in it.** One agent
talking in a channel is drawn as that agent — its own orb, wearing its own face,
with the `#` badge saying which kind of conversation it is. A second agent
joining is what makes the channel a thing in its own right, and that is when the
room orb appears with both of them pointing at it.

**Only agents with an orb of their own get a wedge** — so a channel two agents
are working in is drawn as two, not as everyone who has ever spoken there. An
agent that has been quiet for longer than **Show sessions active within** is
still in the channel's *conversation*, and its messages still appear when you
open the room, but it is no longer one of the faces on it. That setting is the
dial: widen it and more of the channel's regulars count as present, narrow it
and the orb tracks who is talking right now.

**Only recently active sessions get orbs.** A gateway remembers every
conversation it has ever had — 59 of them on the machine this was developed
against — so an orb per session would bury the screen. **Show sessions active
within** controls how far back to look; anything currently working shows
regardless. That is deliberately separate from **Keep orbs for**, which is about
how long a session lingers *after* it goes quiet.

Be aware that the gateway's own idea of "recent" is unreliable — it reported
nearly two hours since last activity for a Discord chat that was happening at
that moment — so Claude Buddy also counts anything it has watched happen since
it started. Conversations from before it connected are the ones that depend on
the setting.

**A beating heart marks a session the gateway's heartbeat drives.** OpenClaw
wakes each agent on a timer to do background work, and it does that in the
agent's *own main session* — so on a gateway with several agents, that many orbs
go active together every few minutes with nobody on the other end. Without the
heart they read as somebody waiting for you; with it, the motion says the thing
on the other end is a clock.

**Heartbeat sessions** and **Cron sessions** each decide what those timer-driven
orbs do, and there are three answers rather than two:

| | |
| --- | --- |
| **Hidden** | No orb at all. |
| **With the chats** | An orb, gathered into the same shape as everything else when you press the arrange button. The default, and what the app has always done. |
| **Own shape** | An orb, gathered into a shape of its own, drawn beside your conversations rather than among them. |

Set both to *Own shape* and the arrange button draws three patterns side by side
— your chats, the heartbeats, the crons — each with its own entry in Settings
picking from the same six shapes. Leave both alone and it is one shape, exactly
as before. The agents keep their colours in any channel they are in whichever you
pick.

The shapes sit beside each other rather than anywhere you place them
individually: each gets a share of the screen in proportion to how many orbs it
is holding, they are drawn as one group centred on wherever you last dragged the
arrangement, and dragging it moves all of them together. Two shapes cannot be
drawn on top of each other, which is the reason it works that way rather than by
giving each shape its own position to lose.

An upgrade changes nothing on screen. If you had turned the old **Show heartbeat
sessions** switch off, that becomes *Hidden*; on — or never touched — becomes
*With the chats*. Crons had no setting before this and always joined the one
shape, which is what *With the chats* means.

The heart marks **where a heartbeat lands**, not which individual turns were
one — the gateway does not report heartbeats at all, and its own Control UI
hides their prompts the same way. Two honest consequences: an agent whose
heartbeat is switched off still gets a heart (whether it is enabled is config
behind a scope Claude Buddy does not ask for), and a heartbeat retargeted at a
channel with a job's `session` override is not marked. See
`docs/openclaw-findings.md` for what was measured.

**Click one of these orbs and a small chat panel opens under it** — the last
turns, what the agent is thinking, the tools it reaches for, and a line to type
in. Escape, Cmd-W, the close button or clicking away all dismiss it, and your
half-typed draft survives being dismissed. Enter sends, Shift+Enter starts a new
line, and with voice input on the mic drops what you said into the box rather
than sending it, exactly as dictation into a terminal already does.

Drag any edge or corner and the panel resizes — new turns then scroll inside it
rather than growing it out from under your hands. **The size belongs to the
agent, not to the window**: each one reopens at whatever you last dragged its
panel to, across restarts, and an agent you have never resized still opens at
the shipped 340x420. That lives in `chatPanelSizes` in `settings.json`, keyed
the same way dragged orb positions are — by the agent rather than by the
session, since Claude Code mints a new session id every conversation and a size
saved under one would never be found again.

**Cmd+ and Cmd- change the text size, and Cmd+0 puts it back** — Ctrl on
Windows, the way every other app on each platform spells the same gesture. It
works while the caret is in the composer, which is where it usually is, and it
moves the whole conversation together: bubbles, headings, code blocks, the
speaker chips, the timestamps, the box you type into, and a permission prompt
standing between them. The window's own header and buttons stay where they
are, the same way Messages scales messages and not its toolbar.

Unlike the panel size, **the text size is one setting for every agent**, not one
per agent — it is a preference about your eyes rather than about a
conversation. It lives in `chatTextScale` in `settings.json` as a multiplier
over the shipped size, runs from 0.8x to 2x in eight steps, and there is a
**Text size** slider under **Chat panel** in settings for anyone who never tries
the keyboard. A hand-edited value outside that range is pinned back into it on
read, so a mistyped `40` cannot leave you with a panel too large to find the
setting that caused it.

Replying is a **second switch**, off by default: **Allow replying to agents**.
Turning it on asks the gateway for write permission as well as read, which it
treats as a new pairing — so approve the device again there
(`openclaw devices approve --latest`) and the status row will tell you it is
waiting until you do. Seeing what your agents are doing and being able to make
them do things are different powers, which is why the second one is asked for
separately rather than coming along with the first.

**If the status row says `can't reach the gateway: No route to host`, check
macOS's Local Network permission before you check your network.** macOS grants
local network access per app *identity*, and installing an upgrade replaces the
app bundle — so the permission does not survive the update, and Claude Buddy
silently loses the ability to reach a gateway that is running perfectly well.
Open **System Settings → Privacy & Security → Local Network** and make sure
Claude Buddy is switched on. The app now says so in that row itself, but the
underlying error still reads like a network fault, which is why it is worth
naming here.

Testing it from a terminal will mislead you: `ping`, `nc`, `curl` and `ssh` are
all built into macOS and exempt from that permission, so they will report the
gateway reachable while the app cannot open a socket to it at all.

Two things worth knowing if you are wiring this up yourself: the connection uses
its own TLS stack (BouncyCastle) because the gateway requires TLS 1.3 and .NET
on macOS cannot speak it, and the certificate is trusted by fingerprint on first
connection rather than through the system trust store. `docs/openclaw-findings.md`
records what was measured against a real gateway, including several places where
the published protocol documentation disagrees with the running software.

## Usage orbs for each account (off by default)

If you run more than one Claude Code account — `~/.claude` plus whatever you
have wired up under **Claude Code → additional accounts** — the only way to see
how much of each one you have spent is to open a session on it and run
`/usage`, once per account. Turn on **Usage orbs for each account** in Settings
and each account gets an orb instead.

Each orb wears three rings. Outside in, they are **this week** (the 7-day
window), **this session** (the 5-hour window), and **extra usage**. The colour
is how much room is left rather than which account it is — green under 60%,
amber to 85%, red above — so an account you do not need to think about is a
quiet green outline and nothing else, and a row of them reads from across the
room without a number on screen. Which account an orb *is* stays where it
always is: the two letters in the middle, plus the same Claude / Codex /
Grok mark the session orbs wear.

Hover an orb for the numbers, the reset times and the extra-usage position.
Click it to keep that card up — pin two and they sit side by side, which is the
only way to compare accounts at a glance. Click again to put it away. The orbs
drag like the session ones and stay where you leave them.

Some things the rings deliberately do not do:

- **A window that has reset is not drawn.** Once a five-hour or weekly window
  passes its reset time its percentage is about a period that has ended, so the
  ring empties rather than showing yesterday's number.
- **The inner ring has three states, not two.** Extra usage shows a real
  percentage when it is switched on *and* has a spending limit. When the
  month's limit has been **reached**, the ring is full — you have spent it, and
  that is the opposite of not having any. When there is genuinely no extra
  usage on the account, the ring is a dotted outline instead of a gauge sitting
  at zero, because an empty gauge would claim a budget you do not have.
- **The card never guesses why.** It says "Extra usage limit reached for this
  month" or "switched off for this account" only when the API states those as
  facts; any other reason is shown as the raw code the API sent, in
  parentheses, rather than translated into a sentence. An earlier version did
  translate one, and told somebody their organisation had disabled extra usage
  when in truth they had simply spent that month's budget.
- **An orb that could not be read goes dim** rather than dropping to zero, with
  how old the reading is in its card. Age means the age of the *number*, not of
  the read: a Codex or Grok figure comes out of a file its CLI last wrote
  whenever it last ran, so the card dates it "Usage as of 2h ago" and the orb
  dims once that number is more than fifteen minutes old, even though Claude
  Buddy re-read the file seconds ago. A Codex orb usually escapes this, because
  Codex can be asked for its usage directly and answers about now; a Grok orb
  cannot, because Grok writes its credit figure once when it starts and never
  again for the life of that process.

**Where the numbers come from.** Claude Buddy asks Claude Code itself, once
every five minutes per account, over the same control protocol its SDK uses —
roughly `claude -p --input-format stream-json` with a `get_usage` request and
`CLAUDE_CONFIG_DIR` set. Two consequences worth knowing:

- **It costs no tokens.** The request makes no model call; Claude Code answers
  from the usage endpoint it already talks to. It does start a short-lived
  `claude` process per account per poll, which is the reason for the
  five-minute floor — Claude Code caches the underlying fetch for five minutes,
  so asking more often could not return a newer number anyway.
- **Nothing here touches your credentials.** Claude Buddy never reads your
  login token or your keychain; Claude Code handles its own authentication and
  simply answers the question. That is also why this works the same on macOS
  and Windows.

An account signed in with an API key, or running against Bedrock or Vertex, has
no subscription windows to report. Its orb says so in the card rather than
showing zeros.

The response shape this reads is marked experimental by Claude Code, so it may
change. If it does, the orbs go quiet rather than showing something wrong.

## Sessions on other machines (off by default)

If you run Claude Code on more than one machine — a desktop at home, a server,
a laptop you left on — Claude Buddy can show those sessions as orbs too, and let
you send them instructions.

There are two ways it can reach them, and it prefers the second when it is
available. **Through Remote Control** (macOS only) there is no port to open, no
tunnel and nothing to install on the other machine, and it works from a hotel or
a diner exactly as it does from your desk — at the cost of your Claude usage and
of transcripts that take minutes to arrive. **Directly** (both platforms) is
instant and free, and needs both machines on the same network. The rest of this
section covers Remote Control; [connecting directly](#connecting-directly-instead-both-platforms-off-by-default)
is at the end of it.

The requirement is that the session on the other machine has **Remote Control
on** (`claude --remote-control`, or `/remote-control` in a running session).
Those are the sessions you could already reach from your phone; this makes them
reachable from Claude Buddy as well. A session without it stays invisible here,
which is the right default — it is also how you keep one private.

How it works is worth knowing, because it explains the one cost. Anthropic's
Remote Control relay has no API for third-party apps, so Claude Buddy cannot ask
it anything directly. What it can do is start **a hidden Claude Code session of
its own** with Remote Control on, because such a session is given tools that
reach the account's other sessions wherever they are. That session is the relay:
your own account, no server of ours in the path. It also means the feature uses
your account and counts against your usage while it is running — which is why it
is off until you turn it on, why it only starts when you ask, and why it shuts
itself down again when you stop using it.

Turn it on in **Settings → Other machines**, then:

- **Account** — which Claude Code config directory the relay signs in as. Remote
  Control only shows sessions on the same account, so pick the one your other
  machines use. If that is a second account, add it under **Claude Code
  profiles** first.
- **Stop the relay after** — how long it may sit unused before shutting down. It
  starts again by itself the next time you open or send to a remote session.
- **Start the relay when Claude Buddy starts** — for the machine nobody is
  sitting at. The live view below is *served* by the Buddy on the other
  machine, and until this switch existed that Buddy's relay could only be
  started by a hand on that machine — a headless Mac in a cupboard could never
  serve its sessions unattended. Switch it on there (usually together with
  **Stop the relay after: Never**), and the relay comes up with the app. Both
  are plain keys in `settings.json`, so an SSH session and an app relaunch is
  enough to manage a machine you can't see.

  **A locked screen is fine, and that took two goes to be true.** The relay
  starts before the app waits for the screen to unlock, and — since CB-39 — it
  is also *driven* before then, by a pump that does not belong to the UI thread.
  Without that second half the relay came up, registered, and answered nothing:
  from every other machine its orbs looked alive and its panels all said the
  other machine wasn't running Remote Control. Serving is files and a tmux pane
  with no display anywhere in it, so it no longer waits for one.

Nothing else starts merely because the switch is on. Use **Connect to other
machines** in the tray menu, or the button in Settings, or just open a remote
session's chat — any of those brings the relay up, and orbs for the sessions it
can see appear a few seconds later, badged `⇄`.

Clicking one opens a chat panel, since there is no terminal on this machine to
jump to. What that panel *is* depends on one thing: **whether the other machine
is also running Claude Buddy.**

**If it is, you get a live view.** The panel shows that session's own
conversation — the same words the person sitting in front of it sees — because
the Buddy over there reads its transcript off its own disk and sends it across
verbatim. You can scroll back through it. What you type is typed into that
session's terminal, so **every slash command works**, `/color` and `/rename`
included, exactly as it would locally. Updates arrive as the session works
rather than only when it finishes.

**If it is not, you get a messaging channel**, which is what this used to be
always, and the panel says so in as many words. Without a Buddy on the other
side there is no way to read a file there: the only channel is peer messaging,
which reaches the far session's *model* rather than its terminal. So what comes
back is a reply that model composed for you — its own account of its
conversation rather than the conversation. That is a real limitation of the
transport and not something politeness fixes; it was measured being asked
nicely and it paraphrased anyway. The panel labels it instead of letting a
summary pass for a transcript, the orb pulses, and it says "…is working" while
the far session is busy.

The live view is **verify-or-refuse**. Every piece of transcript that crosses
the wire carries a SHA-256 of what it is supposed to be, because the thing
relaying it is a language model pasting text it cannot read. A piece that
arrives altered is asked for again and then refused — the panel shows an error
and nothing else. It never quietly falls back to the model-written version,
which would substitute a summary at the exact moment something was going wrong.

One thing a remote orb cannot know: **which computer it is on**. A peer is
reported as a name, a kind and a status, with no hostname anywhere, so the title
is that name alone.

Its **colour** it does know, by asking. A session's `/color` lives on its own
machine, so Buddy asks each remote session once what colour it is and uses the
answer; until it replies, or if it has none set, the orb takes a colour derived
from its name so several remote orbs are still telling apart. That costs one
message per remote session each time Buddy starts, which is the only route
available when there is no Buddy on the other side —
`docs/remote-control-findings.md` explains why nothing cheaper works. When there
is one, it comes across with the rest of what that Buddy reports and costs
nothing extra.

**Which slash commands the panel offers** depends on the same thing, and the
difference is the clearest illustration of what a live view buys.

With one, the list is everything that session can run — built-ins included —
read off the far machine's own disk by the Buddy beside it, and they genuinely
run, because your message goes into that CLI's input line.

Without one, Claude Code's **built-ins** — `/compact`, `/color`, `/agents` —
cannot work at all, because a message reaches the other session's *model* and
never its command handler. Custom commands can, since those are just
instructions the model reads, so the panel asks each session which ones it has
and offers only those. Until it answers, the panel offers nothing rather than
offering commands that would fail; it re-asks a few times before giving up, and
a session that has none keeps an empty list rather than being given a plausible
one.

`docs/remote-control-findings.md` records what was measured against two real
machines before any of this was built — including what the relay does and does
not expose, and the two things a stronger test caught that a weaker one had
passed.

### Connecting directly instead (both platforms, off by default)

Everything above goes through Anthropic's Remote Control cloud, carried by a
hidden Claude Code session that Claude Buddy runs as a relay. That works from a
hotel, and it has two costs that are hard to miss once you have watched it: it
uses your Claude account, and a transcript arrives in **minutes** — the relay's
*model* retypes the transcript by hand, about four minutes per six kilobytes.

When both machines are on the same network, they can talk to each other
directly instead. Turn on **"Connect directly to other machines"** in Settings →
Other machines, on both. Transcripts then arrive in a moment, nothing signs into
your account, and nothing counts against your usage.

Machines find each other by announcing themselves on the local network, so
paired ones reconnect on their own whenever both are up. Two things have to
happen once:

1. **Pair them.** On one machine, press **Show a code**. On the other, find that
   machine in the list and type the code beside it. Both then remember each
   other's certificate, and the code is good for five minutes and one pairing.
2. **Say yes to the permission.** The first time it listens, macOS asks for
   Local Network access and Windows raises a firewall prompt. Both are the
   feature working; a "no" here looks exactly like the network being broken.

Two situations need a different route in, and both have one.

**A machine with no screen** — a Mac mini serving its sessions unattended — has
no button to press. Write the code into a file instead, and it opens the same
window for the same five minutes:

```bash
ssh your-mini
echo 123456 > "$HOME/Library/Application Support/ClaudeBuddy/pair-open"
```

Then pair from the machine that does have a screen, typing that code. The file
is read once and deleted, so a forgotten one is not a standing invitation.

**A machine this one cannot see** — on another subnet, behind a VPN, or on a
network that does not carry the announcements — is added by hand. Use **Add a
machine by address** with its address (and `:port` if you changed it) and the
code it is showing. Its name fills itself in as soon as it answers, because the
machine on the other end is the one that knows it.

Both machines still need Claude Buddy running, but the far one no longer needs
Remote Control on, or a Claude account at all: this path never asks a model for
anything.

**A Mac with no screen can serve, but cannot start a conversation.** macOS gates
the machine that *opens* a local connection, not the one that accepts it — and a
headless Mac has nobody to approve the prompt, so it never gets the grant. What
that looks like, measured on a real one:

```
peer-connect-failed to=your-laptop   No route to host — macOS may be blocking
  local network access — check System Settings → Privacy & Security → Local Network
discovery-announce-failed            No route to host — …
```

Both of those are the *same* missing grant. The second is the one that misleads:
a headless Mac cannot send its announcements either, so it never appears in
anyone's list however healthy it is — which reads as the network not carrying
multicast rather than as a permission.

It still works, because none of that is needed in the direction that matters:

- Pair from the machine **with** a screen, using the `pair-open` file above.
- Add the headless one **by address**, not by waiting for it to appear.
- The machine with a screen keeps the connection up. The headless one answers on
  it, in both directions, because one connection carries both.

If you want the headless machine to dial as well, screen-share into it once and
approve Local Network access there; it is a one-time prompt like any other.

**If a machine never appears**, the likeliest cause on macOS is that Local
Network access was refused or lost. It is tied to the app's code signature and
**does not survive an upgrade**, and the symptom is a connection error that
reads like an ordinary network fault. `ping`, `nc`, `curl` and `ssh` are all
Apple-signed and exempt from the gate, so every obvious check will tell you the
machine is perfectly reachable while Claude Buddy cannot open a socket to it.
Check System Settings → Privacy & Security → Local Network. `docs/` and CB-38
have the full diagnosis.

## 1. Install it

Either download an installer or build from source — both are fully supported,
and the installers are just the build scripts run in CI.

### Download an installer

Grab the latest from
[**Releases**](https://github.com/Uplift-Foundation/Claude-Buddy/releases). Nothing else
is needed; the .NET runtime is bundled.

| Platform | File |
| --- | --- |
| macOS, Apple silicon | `ClaudeBuddy-<version>-osx-arm64.dmg` |
| macOS, Intel | `ClaudeBuddy-<version>-osx-x64.dmg` |
| Windows 10/11, 64-bit | `ClaudeBuddy-<version>-win-x64-setup.exe` |

**macOS**: open the DMG, drag Claude Buddy to Applications, then double-click
**Install Hooks.command** — that runs step 2 below for you. The
builds are signed and notarized, so they open without a Gatekeeper override.

**Windows**: run the setup. It installs per-user under
`%LOCALAPPDATA%\Programs\ClaudeBuddy`, so there is no UAC prompt, and it
offers to do step 2 and to start the app at sign-in. It is **not** code-signed
yet, so SmartScreen shows a warning — choose *More info → Run anyway*.
Uninstall through Apps & Features; that also removes the hook entries from
`settings.json`.

Either way, **don't skip step 2**. Orbs come from a Claude Code hook, and until
it's wired up the app runs correctly and displays nothing, which looks broken
but isn't. The installers offer to do it; let them.

### Build from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download) or newer,
on either platform.

#### macOS — build the app bundle

```bash
./tools/build-macos-app.sh             # -> dist/Claude Buddy.app
./tools/build-macos-app.sh --install   # ...and copy it to /Applications
./tools/build-macos-app.sh --rid osx-x64   # cross-build for Intel
```

Then launch it like any other app: double-click it in Finder, or

```bash
open "dist/Claude Buddy.app"      # or: open -a "Claude Buddy"
```

Nothing appears in the Dock and nothing opens a window — **look for the orb
in the menu bar**, that's the app running. Quit it from that menu.

The bundle is worth using over the loose binary for reasons beyond
double-clickability: it's `LSUIElement`, so macOS itself treats it as a
menu-bar app; it declares `NSAppleEventsUsageDescription`, without which
macOS won't even offer the Automation prompt that click-to-focus depends on;
and it has a stable code identity, so that Automation grant attaches to
"Claude Buddy" rather than to whichever terminal launched a bare binary.
A local build is ad-hoc signed, which means each rebuild changes the signature
and macOS may ask for Automation permission again — expected, not a bug. Set
`MACOS_SIGNING_IDENTITY` to a "Developer ID Application: …" identity to get a
stable, distributable signature instead.

#### Windows (and the loose-binary route on macOS)

```
dotnet publish -c Release -r win-x64     # Windows
dotnet publish -c Release -r osx-arm64   # macOS on Apple silicon
dotnet publish -c Release -r osx-x64     # macOS on Intel
```

The binary lands in `bin/Release/net10.0/<rid>/publish/ClaudeBuddy` (`.exe`
on Windows) — it's self-contained, so you can copy that one file anywhere
(e.g. a `Tools` folder) and run it without needing .NET installed
separately. For local hacking on either platform, plain `dotnet run` works
too.

Run it once to sanity-check: until a session writes a status file you should
see **zero orbs** and a slate-colored status-bar icon whose menu says "No
Claude Code sessions" — that's correct, not broken. Left-click-drag an orb
to reposition it once one appears; where you drop it is remembered (see
above), so a test run leaves `orbPositions` entries in `settings.json` for
whatever directories you dragged.

The icons are generated, not checked in as hand-drawn art — rerun
`python3 tools/make-icons.py` (stdlib only) after editing it to regenerate
`Assets/` (the tray PNGs, the `.app` icon source, and `ClaudeBuddy.ico` for
the Windows executable and installer).

#### Build the installers

The same scripts CI runs, so a local run reproduces a release artifact:

```bash
./tools/build-macos-dmg.sh                    # -> dist/ClaudeBuddy-<ver>-osx-arm64.dmg
./tools/build-macos-dmg.sh --rid osx-x64      # Intel
```

```powershell
.\tools\build-windows-installer.ps1           # -> dist\ClaudeBuddy-<ver>-win-x64-setup.exe
```

The Windows one needs [Inno Setup 6](https://jrsoftware.org/isdl.php)
(`winget install -e --id JRSoftware.InnoSetup`). Both work unsigned for local
testing — see [Releasing](#releasing) for what CI adds on top.

## 2. Wire up your agent CLIs

Claude Buddy tracks **Claude Code**, **OpenAI's Codex CLI**, and **Grok Build**.
Each install you
want tracked (WSL, native Windows, macOS, ...) needs its own copy of the hooks
added to *its own* config — installs don't share config.

### The scripted way

One script wires whatever it finds. It's the recommended route: it backs up
each config first, preserves everything already in it (including other tools'
hooks), and converges rather than duplicating, so re-running it repairs a broken
setup — or picks up the second CLI after you install it. Pass `--uninstall` /
`-Uninstall` to remove just our entries, from everything.

```bash
./tools/install-hooks.sh
# installed from a DMG instead of a clone? it ships inside the app:
"/Applications/Claude Buddy.app/Contents/Resources/install-hooks.sh"
```

```powershell
.\tools\install-hooks.ps1
# or, installed from the setup:
& "$env:LOCALAPPDATA\Programs\ClaudeBuddy\tools\install-hooks.ps1"
```

A CLI you don't have is skipped and said so, not treated as an error. Underneath
it are one installer per CLI — `install-macos-hooks.sh` /
`install-windows-hooks.ps1` for Claude Code, `install-codex-hooks.sh` /
`install-codex-hooks.ps1` for Codex, `install-grok-hooks.sh` /
`install-grok-hooks.ps1` for Grok Build — and those still take the per-CLI options
described below. You only need them if you want one of those options; the
wrapper is what an install runs.

### Grok Build

Grok loads hooks from `$GROK_HOME/hooks/*.json` (`~/.grok/hooks/` by default)
and those files are **always trusted** — there is no Codex-style review step.
`install-hooks.sh` wires them when it finds Grok; `install-grok-hooks.sh` is
the per-CLI script if you only want that.

Two differences from a Claude Code orb, both because Grok works differently:

- **No `/color`.** Grok's `/theme` is TUI-wide. Auto-color derives a ring from
  the working directory and never writes into Grok's `updates.jsonl`.
- **The name comes from `summary.json`**, Grok's own title (and `/rename` when
  that has been used). The conversation in the chat panel is the ACP update
  stream at `updates.jsonl`, not Claude Code JSONL.
- **Usage orbs** (Settings → Grok Build) draw the weekly credit window Grok
  already fetches. Grok has no five-hour cap, so that ring is omitted rather
  than drawn at zero. The figure is as fresh as the last Grok session on this
  machine — Claude Buddy does not hold Grok's login token — and the orb says so
  rather than implying otherwise: Grok writes its credit figure once, at
  startup, so a machine that last ran `grok` on Monday shows a dimmed orb and a
  card reading "Usage as of 2d ago".
- **Keep Grok usage fresh automatically** (same section, off by default) starts
  and stops Grok in the background roughly every twenty minutes purely to force
  that number to refresh, since there is no lighter way to ask Grok for it —
  confirmed against `grok models`, `doctor`, `inspect`, `sessions` and `grok
  agent stdio`, none of which trigger it. It runs in a scratch folder rather
  than one of your projects, needs macOS (Windows would need a pty API this
  app does not wrap, so it no-ops there rather than guessing), and is
  deliberately a second switch from the orb itself: reading a log file and
  starting your real terminal app in the background are different classes of
  action.

See `docs/grok-findings.md` for what was measured on a real session.

### Codex

**Codex will not run a hook it has not been told to trust.** A `hooks.json`
written by anything other than Codex starts out untrusted, so after wiring, the
first time you start Codex, accept the hook review it shows you — or run
`/hooks` inside it and trust the Claude Buddy entries. Until you do, no hook
fires, no Codex orb appears, and **nothing anywhere tells you why**. Editing
`hooks.json` later, including re-running the installer, changes its hash and
asks you again.

The hooks go in `$CODEX_HOME/hooks.json` (`~/.codex/hooks.json` by default),
which Codex discovers on its own — nothing needs adding to `config.toml`.

Two differences from a Claude Code orb, both because Codex works differently
rather than because the support is unfinished:

- **No `/color`.** Codex has no equivalent, so a Codex orb keeps the plain ring
  and wears the green star instead of a `/color` accent. Its *name* does come
  from Codex — `/rename` if you've set one, otherwise Codex's own title, taken
  from your first message.
- **Usage orbs** (Settings → Codex sessions) draw the five-hour and weekly
  windows, and they are **live**: Claude Buddy asks `codex app-server` for them
  directly, which costs no model call, needs no session open, and never touches
  Codex's login token. If Codex cannot be reached that way it falls back to the
  windows Codex writes onto each turn of the rollout — the newest snapshot that
  carries a window, across every rollout, which is not the same as the newest
  line in the newest file: Codex sends a *window-less* snapshot to every live
  session when the workspace runs out of credits, and that one is routinely the
  most recent thing on disk. A fallback figure is only as fresh as the last
  Codex session, and the orb dims and the card dates it when that is what you
  are looking at. See `docs/codex-findings.md`.
- **A Codex orb appears on the session's first message, not when Codex opens.**
  Codex fires no hooks until a thread exists, and a thread is created when you
  first speak to it — so an open-but-untouched session has no orb, and neither
  does a resumed one until you send something. Claude Code fires its
  `SessionStart` on launch, which is why the two feel different for the same
  actions. This is Codex's behaviour and nothing the hook can change.
- **The chat panel works the same way**, with its own pair of switches under
  Settings → **Codex sessions**. It reads the rollout Codex already writes, and
  with replying on it types into the session's tmux pane — including answering
  approval prompts, which are numbered exactly as Claude Code's are.

The Windows installer runs this for you if you leave the checkbox ticked, and
the macOS DMG's **Install Hooks.command** is a wrapper around it.

**WSL** is covered by the same script, opt-in via a couple of extra flags —
native Windows wiring is unaffected either way:

```powershell
.\tools\install-windows-hooks.ps1 -Wsl                       # every WSL distro that has Claude Code
.\tools\install-windows-hooks.ps1 -Wsl -WslDistro Ubuntu      # just one distro
.\tools\install-windows-hooks.ps1 -UninstallWsl -WslDistro Ubuntu   # unwire just that one
```

`-Wsl` skips a distro it doesn't detect `claude` on the PATH of (pass `-Force`
to wire it anyway); `-Uninstall` (the full teardown, as opposed to
`-UninstallWsl`) always sweeps every WSL distro regardless of how it was
originally wired, so an uninstall never leaves a dangling hook pointing at a
deleted script. The Windows installer offers a matching **"Also wire up hooks
for Claude Code running under WSL"** checkbox (shown only when it detects
`wsl.exe`), and once Claude Buddy is running, its **Settings window** lists
every WSL distro with a checkbox per distro to wire or unwire it on the spot —
no script or installer re-run needed for that. Both routes only reach each
distro's *default* Linux user (see the Scope note above).

**Multiple accounts** — `CLAUDE_CONFIG_DIR` for Claude Code, `CODEX_HOME` for
Codex, e.g. an alias like `alias kwork="CLAUDE_CONFIG_DIR=~/.claude-work claude"`
— are a separate config file each, invisible to the default wiring above. The
**Settings window** has a "Claude Code profiles" and a "Codex profiles" section
on **both platforms**: add a directory name once and it's wired immediately and
re-applied on every future install, repair and uninstall. The installers read
the same list, so a fresh install picks up whatever is saved there.

macOS can also pass them explicitly:

```bash
./tools/install-macos-hooks.sh --profile-dir .claude-work
./tools/install-codex-hooks.sh --profile-dir .codex-work
```

The Windows-specific parts of this — WSL distros, and the extra flags below —
stay Windows-only, because WSL does. Note that `claude`'s own PATH detection
(the `-Wsl` skip/`-Force` logic just above) tries several shells before
giving up specifically because of this: nvm/pyenv/rustup-style installs put
their PATH line in `~/.bashrc` or `~/.zshrc` depending on which shell you
use, both of which only get read by an *interactive* shell, not the login
shell a fresh `wsl.exe -d <distro> --` invocation starts as — so a
single-shell-mode check would silently and incorrectly report `claude` as
missing for a large share of real setups. Extra accounts aren't
auto-discovered (only `~/.claude` is touched by default), but can be added
explicitly:

```powershell
.\tools\install-windows-hooks.ps1 -ProfileDir .claude-work                    # native, in addition to the default
.\tools\install-windows-hooks.ps1 -Wsl -WslProfileDir .claude-work,.claude-personal
```

or, easier for ongoing use, the **Settings window**'s **"Claude Code
profiles"** section — add a directory name there once, and it's wired on
native Windows immediately and re-applied to every already-wired WSL distro,
with no need to pass either flag by hand again. A repair or reinstall through
the Windows installer also picks up whatever's saved there automatically,
since the script reads the same file the Settings window writes to.

Whichever route you take, **restart any Claude Code sessions you already have
open** — hooks are read once at session start, so existing sessions won't
produce orbs until they're restarted.

### By hand

Pick the snippet that matches where the Claude Code session you're wiring
up actually runs, then open **that install's** `~/.claude/settings.json`
(create it if it doesn't exist) and merge in the snippet's contents:

- **Claude Code on macOS** → `claude-hooks-snippet-macos.json`. First copy
  the hook script into place and make it executable:
  ```bash
  mkdir -p ~/.claude/claude-buddy
  cp ClaudeBuddyHook.sh ~/.claude/claude-buddy/
  chmod +x ~/.claude/claude-buddy/ClaudeBuddyHook.sh
  ```
  The snippet references it via `$HOME`, so there's no username to replace.
- **Claude Code running inside WSL** → `claude-hooks-snippet-wsl.json`. Only
  needed for a second Linux user account inside a distro — `install-windows-
  hooks.ps1 -Wsl`, the installer checkbox, and the app's Settings window (see
  "The scripted way" above) all handle a distro's *default* user
  automatically, and are the easier route for that case. For a second account:
  copy `ClaudeBuddyHook.ps1` to a local Windows folder (e.g.
  `%LOCALAPPDATA%\ClaudeBuddy\`) and replace every `<YOUR_USERNAME>` in the
  snippet with your Windows username. `~/.claude/settings.json` here means
  the Linux user's home directory (e.g. `/home/<user>/.claude/settings.json`)
  — a completely separate file from any Windows-side config.
- **Claude Code installed natively on Windows** (not through WSL) →
  `claude-hooks-snippet-windows.json`. Same `ClaudeBuddyHook.ps1` copy and
  `<YOUR_USERNAME>` replacement; `~/.claude/settings.json` here means
  `C:\Users\<YOUR_USERNAME>\.claude\settings.json`. One copy of the .ps1 is
  enough for both Windows-side variants — every install's hooks can point
  at the same file.

All snippets do the same thing and differ only in how they invoke the hook
script (see the platform notes below) — the hook logic, matchers, and
states are identical.

**If you already have a `hooks` block with other events in it**, don't
replace the whole thing — add `SessionStart`, `Notification`,
`UserPromptSubmit`, `PreToolUse`, `Stop`, and `SessionEnd` as sibling keys
inside your existing `hooks` object, and if you already have any of those
six keys, append these entries to their existing arrays instead of
overwriting them.

What each hook does — every one of them invokes the hook script with a
state (`idle`, `generating`, or `waiting`), which reads `session_id` and
`cwd` off the hook's own stdin JSON and writes/updates that session's
status file:
- **`SessionStart`**: fires when a Claude Code session starts (including
  `/clear` and `/compact`, which re-fire it) → `idle`, so the orb appears
  right away instead of waiting for the first prompt or tool call.
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
- **`SessionEnd`**: invokes the script with `ended`, which **deletes** the
  session's status file (rather than writing to it) so its orb disappears
  immediately on a clean exit. It's a nice-to-have, not the primary cleanup
  mechanism — it's documented as unreliable on ungraceful termination
  (Ctrl+C notably; the hook gets cancelled before it can run), so the app
  still prunes stale files as a fallback (see `StaleAfter` in
  `SessionManager.cs`, which reads the "Keep orbs for" setting, and the
  "waiting is never pruned" note above).

Run `/hooks` inside Claude Code afterward to confirm all six events are
registered — do this separately for each install, since `/hooks` only
shows the config for the session you run it in.

### Platform notes

**macOS**: the hooks call `bash` with the script's absolute path — nothing
else needed. The script writes to `$TMPDIR/claude_buddy/`, which is the
same per-user folder .NET's `Path.GetTempPath()` returns, so the app and
hooks agree automatically. No `jq` dependency; the script extracts
`session_id`/`cwd`/`transcript_path` with `sed`, and the chat name and color
with `grep`.

**WSL, chat names and colors**: the PowerShell hook reads the same transcript
records as the bash one, but a WSL session's `transcript_path` is a Linux path
that `powershell.exe` can't open, so those orbs keep the folder-name fallback
and the plain border. Native Windows sessions get both normally.

**Encoding, on Windows PowerShell 5.1 specifically**: the hook reads the
transcript with `-Encoding UTF8` and writes its status file with
`[System.IO.File]::WriteAllText` and a no-BOM `UTF8Encoding`, rather than
`Get-Content`/`Set-Content` defaults. Both are load-bearing and were caught on
a real Windows box: 5.1 reads UTF-8 as the ANSI codepage (turning `café` into
`cafÃ©`) and writes ANSI on the way out (turning it into `caf?` — actual data
loss, since chat names carry em dashes and accents far more often than paths
do). The BOM matters too: `System.Text.Json` treats a leading BOM as an
invalid start of value, so a BOM would make the app skip the file and drop
that orb entirely. PowerShell 7 defaults are already correct; being explicit
is right on both.

**Codex on Windows** carries one gap worth knowing about: a Codex orb there is
named after your first message, not after `/rename`. Codex keeps both names in a
SQLite database, and macOS can read it because the system ships a `sqlite3`
binary — Windows ships `winsqlite3.dll` but no command-line client, and
PowerShell has no built-in provider, so the hook falls back to the first message
out of the rollout. That is the same message Codex builds its own title from, so
the name is true, just not the one you chose. It matches what a Claude Code
session under WSL already does for the same category of reason.

Windows Codex support is **partly verified**, and the split is worth stating
precisely. CI builds the installer, runs it silently on a Windows runner, and
checks the result — so the setup wiring, `install-hooks.ps1`'s detection and
dispatch, and the "Codex is absent, write nothing for it" path are all exercised
on a real machine every time they change. What is **not** exercised is anything
that needs Codex actually installed: `install-codex-hooks.ps1`'s install path,
and the hook script's own Codex branch. Those are written and reviewed but have
never run. See `docs/codex-findings.md`.

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

Both Windows-side variants land in the same real `%TEMP%\claude_buddy\`
folder, since `powershell.exe` resolves `$env:TEMP` to the actual Windows
temp directory regardless of which shell launched it — so a WSL session
and a native Windows session can run side by side and show up as two
independent orbs in the same `ClaudeBuddy.exe`.

These symptoms (and an earlier WSL-only one from before this script
existed — unescaped `$env:TEMP` getting mangled by the outer Linux shell
before PowerShell ever saw it) all look identical from the outside: the
hook fires, but the status file never updates and the orb never reacts. If
you suspect a hook isn't actually reaching the script, temporarily add a
throwaway sibling hook to confirm the hook itself is firing before
debugging further downstream — `echo fired >> /tmp/some.log` on
WSL/macOS, or `cmd.exe /c echo fired >> %TEMP%\some.log` on native
Windows.

## 3. (Optional) Launch it automatically

- **Windows**: the installer does this if you tick "Start Claude Buddy
  automatically when I sign in". By hand: press `Win+R`, type `shell:startup`,
  and drop a shortcut to `ClaudeBuddy.exe` in the folder that opens.
- **macOS**: install the app (from the DMG, or
  `./tools/build-macos-app.sh --install`), then System Settings → General →
  Login Items → **+** → pick **Claude Buddy** from /Applications.

It'll then start quietly whenever you log in.

## Releasing

Releases are built by `.github/workflows/release.yml` and triggered by a tag.
`ClaudeBuddy.csproj`'s `<Version>` is the single source of truth — the
packaging scripts and the installer filenames all read it, and CI refuses to
publish if the tag disagrees with it.

```bash
# 1. bump <Version> in ClaudeBuddy.csproj, e.g. to 0.2.0-beta
# 2. write .github/release-notes/v0.2.0-beta.md
# 3. commit, then:
git tag v0.2.0-beta && git push origin v0.2.0-beta
```

CI then builds both DMGs and the Windows setup, signs and notarizes the macOS
ones, generates `SHA256SUMS.txt`, and publishes a release using those notes.
A tag containing a hyphen is marked as a prerelease automatically.

The release also carries `ClaudeBuddySpeech-<version>-<rid>.zip` for each of
`win-x64`, `osx-arm64` and `osx-x64` — the optional high-quality speech engine.
Those assets are not decoration: the toggle in the settings window downloads the
one matching its own version *and architecture* from this exact release, so a
release published without them hands a 404 to everyone who turns the feature on.

The Windows one is built by `tools/build-speech-engine.ps1` in the Windows job;
the macOS ones by `tools/build-speech-engine.sh` in the macOS job's rid matrix.
They are split that way because an osx-* engine cannot be cross-built — the SDK
ad-hoc signs the apphost with `codesign`, and Apple Silicon will not exec an
arm64 binary that carries no signature at all. Nothing about any of this needs
doing by hand, but a change to either script is a change to the release, which
`workflow_dispatch` is the way to test.

`workflow_dispatch` runs the same build without publishing, which is the way to
test packaging changes. Every push and PR also builds and packages via
`.github/workflows/ci.yml`, unsigned — so a break in the packaging path shows
up before a tag exists rather than halfway through a release.

Signing and notarization are handled by repository secrets, so they happen
automatically for maintainers and need no setup to contribute — a fork or a PR
builds unsigned and that's fine. Maintainer-side certificate setup lives in
[`docs/releasing.md`](docs/releasing.md).

One consequence worth knowing while working on the macOS side: notarization
requires the hardened runtime, and the hardened runtime requires the
entitlements in `tools/ClaudeBuddy.entitlements` — JIT and unsigned executable
memory for CoreCLR, library validation off for the bundled native libs, and
Apple Events for click-to-focus. Removing any of them still notarizes cleanly
but breaks the app at runtime, so they can only be validated by running a
signed build.

## When it crashes

Buddy writes unhandled exceptions to a file, on by default and with nothing to
switch on:

| | |
| --- | --- |
| macOS | `~/Library/Logs/ClaudeBuddy/crash.log` |
| Windows | `%LOCALAPPDATA%\ClaudeBuddy\Logs\crash.log` |

One entry per crash, newest last, each starting with `===` and a timestamp, and
naming which of three paths caught it — a throw nothing caught
(`AppDomain.UnhandledException`), a faulted task nobody awaited
(`TaskScheduler.UnobservedTaskException`), or a throw inside a UI callback
(`Dispatcher.UnhandledException`). The file rolls over to `crash.log.1` at
256 KB, and one previous generation is kept.

It exists because the alternative was a macOS `.ips` crash report, whose managed
frames are unsymbolicated addresses: when Buddy aborted twice on an unattended
Mac mini, identifying the exception took two crash reports, a read of Avalonia's
source and a purpose-built probe. It is not telemetry — nothing is sent
anywhere, and deleting the file is always safe.

If Buddy vanished and that file is empty, the process did not die of a managed
exception: look at Console.app's crash reports instead, and at whether something
outside the app (a launchd agent, an installer replacing the bundle) stopped it.

## Notes / things you might want to tweak

- **Chat names and colors**: both hook scripts pull the newest
  `custom-title` / `ai-title` / `agent-color` records out of the session's
  transcript (`transcript_path`, straight off the hook payload) and record
  them as `title` and `color`. All three come from one read of the file's
  tail, with a full scan only as a fallback for when a long run of tool output
  has pushed them all out of that window — this runs on every tool call, so it
  stays cheap (~15 ms on a 4 MB transcript). If Claude Code ever changes those
  records' shape, the matches simply fail and everything falls back to folder
  names and the plain orb. Consumers: `OrbWindow.UpdateFrom` (glyph, tooltip,
  context menu), `OrbWindow.ApplyAccent` (border + letter color) and
  `TrayController.DisplayName`.
- **Agent teams**: `AgentTeam.cs` answers "which session leads this one", by
  reading `--parent-session-id` (plus `--agent-name` and `--agent-color`) off
  the member's own process — `KERN_PROCARGS2` on macOS via `MacOSProcessScan.ArgumentValues`,
  WMI on Windows — keyed by the `session_pid` the liveness check already uses,
  cached per pid with a one-minute valve so a recycled pid can't pin a wrong
  answer. These are Claude Code internals rather than an interface; if they
  change, the lookup returns nothing and orbs are drawn the way they were
  before teams existed.

  The first version asked the hooks instead: `teamName` out of the member's
  transcript, then `leadSessionId` out of `~/.claude/teams/<team>/config.json`.
  It worked, and it was still wrong — a hook only learns the answer when one
  next *fires*, so an agent that had gone quiet, or was already running when the
  hook was updated, kept a status file with no team in it and sat there looking
  unrelated. Found exactly that way, with two live agents and no arrows. Reading
  the process is true the moment the orb appears and needs no hook at all, which
  is also why the hook scripts carry nothing about teams.

  Downstream, that one value does three things: `OrbWindow.SetTeamRole` draws a
  member smaller (the *window* stays 56x56, so stacking, dragging and remembered
  positions are untouched), `SessionManager.DisplayOrder()` gathers each team
  behind its lead, and `TeamLinks.cs` draws the arrows, one click-through window
  per arrow, parked and reused rather than closed (see `ClaudeDesktopOverlay`
  for why closing them is unsafe). Arrow geometry measures the screen-coordinate
  units per DIP with `PointToScreen` instead of reading `Scaling`: on macOS
  Avalonia hands out points, not pixels, and assuming otherwise put every arrow
  half an orb off on a Retina display. Dragging a lead moves its members with it
  (`SessionManager.MembersOf`, captured on press in `OrbWindow`); their new spots
  are only *remembered* when their `PositionKey` differs from the lead's, since
  a team usually shares one directory and positions are keyed by directory.
- **Color palette**: `AgentColors` at the top of `OrbWindow.axaml.cs` maps
  `/color` names to hex. Claude Code renders its accents as xterm-256 indices,
  so these are the matching cube values — but only `green` (index 35) and the
  two auto-assigned accents seen in other sessions (37 teal, 175 pink) are
  confirmed; the rest are same-band guesses for their hue. To correct one,
  set that color in a session and read the escape sequence Claude Code emits:
  `tmux capture-pane -p -e | grep -o $'\033\[38;5;[0-9]*m'`. An unrecognized
  name (one added to Claude Code later) falls back to the plain border and
  white letter, so add a line there rather than expecting a crash.
- **Colors and animation**: `OrbColors.cs` is the one place that answers "what
  colour is this state" — `DefaultIdle` / `DefaultGenerating` / `DefaultWaiting`
  are the shipped three, and the live values are a projection over the
  `orbColors` block in `settings.json` (`null` there means "use the default", so
  retuning a shipped colour still reaches anyone who never picked their own).
  Three things read it: `OrbWindow`'s fill and glow, `TrayController.Tinted()`,
  which recolours the baked tray PNGs at runtime, and the settings window's
  pickers. Nothing observes it, so a writer calls
  `SessionManager.ReapplyStateColors()` — needed because `UpdateFrom` only
  re-applies a colour when the *state* changes, so a quiet orb would otherwise
  keep the old fill forever. The breathing/pulse timings stay in `ApplyState()`
  / `StartPulse()`, and `ApplyState`'s switch is now about motion only.

  `tools/make-icons.py` still holds a hand-synced copy of the three defaults,
  but what matters at runtime is the *alpha* channel of `Assets/tray-*.png`:
  each is a single colour over an alpha mask, which is what makes an exact
  re-tint possible — redrawing the ring in C# instead would change its shape.
- **When an orb goes away**: `SessionManager.ScanAndUpdate()` has all five rules
  in order — superseded-session-id (`Superseded()`, newest file wins per
  `session_pid`), then process-gone (`ProcessLiveness.IsRunning`, a
  `kill(pid, 0)` on Unix and `Process.GetProcessById` on Windows), then the
  backgrounded-husk test (`TranscriptHandoff.EndsBackgrounded`, gated by
  `SessionPresence.CouldBeABackgroundedHusk` so only a session the daemon does
  not vouch for pays the stat), then the `waiting` exemption, then the lifetime
  timer. The husk test sits *above* the `waiting` exemption deliberately: a husk
  frozen on `waiting` is not waiting on anyone. The scan reads every status file
  into a `ScanEntry` list *before* judging any of them, because `Superseded`
  needs to compare files against each other; that pre-pass is also where the
  mtime the timer uses comes from, so it's read once per file per scan rather
  than twice. A recycled pid reads as alive, which errs toward keeping an orb
  rather than dropping a live session's, and the timer still catches that unless
  the lifetime is Forever.
- **Stacking layout and staleness**: `SessionManager.cs` has the stacking
  math (`ReflowPositions()`, which steps over orbs the user has dragged —
  those live in `orbPositions` in `settings.json`, keyed by the session's
  directory; see `RestoreOrbPosition()`) and `StaleAfter`, which is read from
  the "Keep orbs for" setting rather than hard-coded — it controls how long an
  idle/generating session's orb sticks around before being pruned, and is
  `null` for Forever. `waiting` is exempt either way, see above. The choices
  the settings window offers live in `LifetimeChoices` in
  `SettingsWindow.cs`.
- **Reading `settings.json` by hand**: `ClaudeBuddySettings.cs` maps every field
  itself rather than deserializing a type, and the whole read sits in one
  `catch` that falls back to *all* defaults — so a wrong-typed value costs you
  the entire file, profile names and dragged orb positions included. The
  `orbColors` block is read through `Text()` for exactly that reason: a
  `"idle": 5` there degrades to the default colour and nothing else. The older
  fields still use `GetValue<T>()` and still have the sharp edge.
- **The settings window**: `SettingsWindow.cs`, built in code rather than
  XAML. `ClaudeBuddy --settings` opens it straight at launch, which beats
  clicking through the status-bar menu when the window itself is what you're
  editing.

  Controls are **not** styled here. On macOS the app loads
  [Devolutions' MIT AppKit theme](https://github.com/Devolutions/avalonia-extensions/tree/master/src/Devolutions.AvaloniaTheme.MacOS)
  (`<DevolutionsMacOsTheme />` in `App.axaml`) and Windows swaps back to
  Fluent in `App.Initialize`. Avalonia draws every control itself — it has no
  native AppKit controls to use — so the alternative was hand-restyling
  switches, pop-ups and checkboxes, which kept landing close-but-wrong because
  AppKit's metrics and states aren't published anywhere to copy from. What
  *is* still hand-built is the layout around them: the grouped cards, the
  hairlines and the window tint, since no control theme has an opinion about
  those. `TransparencyLevelHint` asks for the vibrant material (`AcrylicBlur`
  maps to `NSVisualEffectView`; Windows takes Mica) and the card brushes are
  mixed per theme variant in one place at the top of the "Mac-ish chrome"
  section.

  Two things to know before touching this. The theme must be declared **in
  XAML**: added from code its templates render nothing at all — labels appear
  and every switch, field and pop-up comes out invisible. And its
  `ToggleSwitch` template is broken against Avalonia's control, which demands
  a `Panel` named `PART_MovingKnobs`; the first switch measured throws and
  takes the app down. Confirmed on Avalonia 11.3.7 *and* 12.0.2 with the
  theme's newest build for each, so it isn't a version mismatch.
  `BorrowFluentToggleSwitch()` lends Fluent's switch template to this one
  window as the workaround, and `Switch()` degrades to a checkbox if even that
  stops working. Delete both once upstream fixes it.
- **macOS + Spaces**: orbs follow you across Spaces and show alongside
  full-screen apps. Avalonia doesn't expose `NSWindow.collectionBehavior`,
  so `MacOSWindowExtensions.cs` sets it (`canJoinAllSpaces` +
  `fullScreenAuxiliary`) through the native window handle when each orb
  opens — that's the file to tweak if you'd rather orbs stay put.
- **Status-bar icon and menu**: `TrayController.cs`. Two things there are
  load-bearing rather than stylistic: its single `NativeMenu` is repopulated
  in place (assigning a *new* `NativeMenu` to an already-exported `TrayIcon`
  throws "The menu being updated does not match" on macOS), and the menu is
  only rebuilt when a signature of the session list actually changes —
  otherwise the 2-second poll would dismiss the menu while you're reading
  it. Icon art comes from `Assets/tray-*.png`, drawn by
  `tools/make-icons.py`. The Claude Desktop section folds its own digest into
  that same signature, and additionally holds rebuilds back while the menu is
  open (`NativeMenu.Opening` / `Closed`), since submenus make people linger.
  The tray *icon* is never held back — it's the urgent half.
- **Claude Desktop profiles**: `ClaudeDesktopManager.cs` (discovery, the
  process scan, launch/quit/reveal), `ClaudeDesktopSection.cs` (the menu
  block), `MacOSProcessScan.cs` (libproc + `sysctl`), `MacOSAppActivation.cs`
  (`NSRunningApplication`). `TrayController` calls two methods on the section
  and knows nothing else about it, so removing the feature is a small revert
  plus deleting those four files.
- **Bundle metadata**: `tools/build-macos-app.sh` writes `Info.plist`
  inline — bundle id, version, `LSUIElement`, and the Automation usage
  string all live there.
- **Click-to-focus coverage**: `TerminalFocuser.cs` maps what the hook
  scripts record (`term_program`, iTerm session UUID, tty, tmux socket/pane
  on macOS; `term_pid` on Windows; `session_pid` on both) to an AppleScript that selects the right
  window, an `open -a` activation, or a `SetForegroundWindow` call. Adding a
  terminal only means adding a case if you want *exact tab* selection for it
  — plain activation already works for anything that lives in an `.app`.
  Focus work runs on a background thread (it shells out and waits), so a
  click can't stall the orb animations.
- **Sound**: no audio right now, purely visual per your original ask. If
  you later want a soft sound on the waiting transition, that's one line
  in `OrbWindow.ApplyState()` — e.g. shell out to `afplay` on macOS or
  play a system sound on Windows.
