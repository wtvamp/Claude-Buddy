using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ClaudeBuddy
{
    // Best-effort "take me to that session's terminal" for a left-click on
    // an orb. Silently does nothing when the status file predates the hook
    // scripts that record terminal info.
    //
    // Precision degrades gracefully. macOS: the exact tmux pane (see
    // FocusTmux), the exact iTerm2 pane (via its session UUID), the exact
    // Terminal.app tab (via tty), otherwise just activate the terminal app;
    // the first click triggers a macOS Automation permission prompt for
    // controlling the terminal — that's expected; approve it once.
    // Windows: for Windows Terminal, the exact tab when its title
    // unambiguously identifies the session (see TrySelectWindowsTerminalTab);
    // otherwise the terminal window whose PID the hook recorded, or any
    // window of the app named by term_program (the WSL case, where the
    // Windows-side parent chain dead-ends in an interop bridge).
    // Excluded from coverage: this is the OS boundary itself. Every method here
    // runs tmux, ps, osascript or PowerShell as a real subprocess, sends
    // synthetic keystrokes through SendInput, or drives UI Automation against a
    // live Windows Terminal window. CLAUDE.md already records why a headless
    // runner must not reach it: a synthesized orb click lands here, and these
    // calls have no OS guard at their own entry point, so on a CI runner they
    // would be real, unpredictable side effects rather than a test.
    //
    // What used to be testable-by-association is now in TerminalScripts, which
    // is not excluded: the AppleScript builders, the tmux socket-pinning rule,
    // the path-leaf rule and the AppleScript escaping. Those are the parts that
    // decide anything.
    [ExcludeFromCodeCoverage]
    internal static class TerminalFocuser
    {
        // teamLead is where the click goes when this session has nowhere of its
        // own to go: an agent-team member runs in a pane of a *detached* tmux
        // server, so there is no window anywhere showing it, and a click on its
        // orb otherwise did nothing at all. The session running the team is the
        // honest answer — it's where that agent's work is being driven from.
        // sessionId is the orb's own id, which SessionStatus doesn't carry —
        // it's the status file's name, not a field inside it. Needed for the
        // background case at the end, where the only way to reach a session is
        // to name it.
        // paneClaimsByOthers is SessionManager's answer to "which tmux panes do
        // other sessions record as their own, and under what title", handed in the
        // same way teamLead is and for the same reason: which status files exist is
        // not this file's knowledge. The title is there because a claim only counts
        // while it is still true — see SessionPresence.ClaimStillHolds. Only the
        // viewer scan uses any of it, and only for its riskier branch.
        // acknowledge is called when the click was answered *without creating
        // anything* — the session was already on screen, or its pane was simply
        // selected. A callback rather than this file reaching for the orb, because
        // the layering runs the other way everywhere else here: TerminalFocuser is
        // handed the facts it needs and hands back what it did.
        //
        // It exists because round seven worked and nobody could tell. Doing
        // nothing is the correct answer to a click on a session you are already
        // looking at, and it is indistinguishable from the broken clicks this
        // whole ticket is about unless the orb says so.
        //
        // Invoked from the pool thread, so the caller marshals — see
        // OrbWindow.GoToSession.
        public static void Focus(
            SessionStatus? status,
            SessionStatus? teamLead = null,
            string? sessionId = null,
            IReadOnlyDictionary<string, string>? paneClaimsByOthers = null,
            Action? acknowledge = null)
        {
            if (status is null) return;

            // A gateway session has no terminal anywhere and no local process,
            // so there is nothing to focus. Returning here also keeps it out of
            // the background-session branch below, which reads pid <= 0 as "a
            // local `claude daemon` session" and would open a tmux window trying
            // to attach a session id that exists only on another machine.
            //
            // Widened from Claude Code to any local CLI: a Codex session is in
            // a terminal like any other and everything below finds it the same
            // way, from the tmux pane or the tty the hook recorded. The one
            // exception is that background-session branch, which stays Claude
            // Code's — see the guard on it.
            if (!status.IsLocalCli) return;

            // Resolving a target runs several short-lived processes (tmux
            // queries, ps walks, osascript) and waits on their output; doing
            // that on the UI thread would stall every orb's animation for the
            // duration of the click.
            Task.Run(() =>
            {
                // detached is what the tmux attempt learned on its way past: the
                // pane is alive, it has been selected, and no client is attached
                // to that server anywhere. Kept rather than re-asked, because
                // answering it costs three subprocesses and because it is a fact
                // about a moment that has already gone.
                if (FocusCore(status, out var detached)) return;

                // Nothing on screen shows this session. Which way out applies is
                // decided by ClickRouting, which is pure and covered per case; the
                // reason there is any way out at all is that every failure above
                // this line is silent.
                //
                // Asked *before* the team lead is tried, which is the fix for
                // CB-13's team-orb bug and the reverse of what this method did.
                // The lead used to come first, and a lead that could be focused
                // ended the click there — so a teammate whose own session had a
                // perfectly good answer waiting (a terminal on its detached swarm
                // socket, landing on the pane the tmux attempt above had just
                // selected) never got it, and the user got a window that was
                // already in front of them. See ClickRouting.LeadMayAnswer, which
                // is where that ordering now lives and why.
                var fallback = ClickRouting.FallbackFor(status, sessionId, detached);

                // The lead only when nothing would show the session that was
                // clicked. It is still the right answer then — the lead is where
                // that agent's work is being driven from, and a window showing
                // the wrong session beats no window at all — but it is the last
                // answer rather than the first.
                if (ClickRouting.LeadMayAnswer(fallback)
                    && teamLead is not null
                    && FocusCore(teamLead))
                {
                    return;
                }

                RunFallback(fallback, status, sessionId, paneClaimsByOthers, acknowledge);
            });
        }

        // One place the four answers are carried out, because two gestures reach
        // them: a click on an orb, and the chat panel's button for a session it
        // cannot type into. Two switches would be two chances for the same orb to
        // send its click and its button to different places.
        //
        // Off the UI thread by contract — every arm here runs subprocesses — which
        // Focus above satisfies by being inside its own Task.Run, and Elsewhere
        // below by starting one.
        private static void RunFallback(
            ClickFallback fallback, SessionStatus status, string? sessionId,
            IReadOnlyDictionary<string, string>? paneClaimsByOthers, Action? acknowledge)
        {
            switch (fallback)
            {
                case ClickFallback.AgentsView:
                    // Where these sessions are managed from. Focused if a roster
                    // is already open, opened if not, and finished off through the
                    // ordinary pane path when it went into tmux.
                    FocusPaneIfAny(AgentTeamViewer.OpenOrFocusAgentsView(status.Cwd), status.Cwd);
                    break;

                case ClickFallback.AttachBackground:
                case ClickFallback.AttachById:
                    // Step zero, ahead of anything that creates a window: is a
                    // pane already showing this conversation?
                    //
                    // "Nobody wants the same chat in two windows next to each
                    // other!! This is the chat!!" — said after round 6a split a
                    // second view of the session the user was reading into the
                    // window they were reading it in. Every earlier round assumed
                    // this was undiscoverable, and the socket, file, argv and
                    // environment proofs of that all stand; what none of them
                    // covered is that the TUI publishes the conversation title to
                    // the terminal, where tmux keeps it in #{pane_title}.
                    //
                    // Only the two attach answers ask. The socket answer is about a
                    // pane in a *detached* server, which by definition nobody is
                    // looking at, and the roster is a destination the user named
                    // outright from a menu.
                    // ...but not for a session that recorded a pane of its own.
                    //
                    // The scan finds a pane by *title*, which is a guess at where a
                    // conversation is being displayed. For a session that told us
                    // its own pane the guess cannot outrank the statement, and for
                    // a teammate the guess is provably undecidable: a teammate
                    // inherits its lead's title, so the lead's viewer pane and the
                    // teammate's own are indistinguishable by the only thing this
                    // scan reads. Answering that click with the lead's window is
                    // what round thirteen was reported for.
                    //
                    // Belt to the ordering above, which already sends such a
                    // session to its own pane. This is the brace: a recorded pane
                    // that is *not* currently alive falls past that rule, and this
                    // stops the title scan picking up the pieces with somebody
                    // else's window.
                    var (verdict, showing) = ClickRouting.RecordedItsOwnPane(status)
                        ? (SessionPresence.ViewerVerdict.NoneFound, (SessionPresence.ViewerPane?)null)
                        : AgentTeamViewer.ViewingPane(status.Title, paneClaimsByOthers);

                    // One branch for both found verdicts, because both mean the
                    // same thing to the destination — see
                    // SessionPresence.AnswersTheClick, and note that being
                    // tmux-active is not the same as being on the user's screen.
                    // The orbs float over every application and the terminal is
                    // routinely behind something else when one is clicked, so
                    // "already here" has to raise the terminal too; for a pane that
                    // is already current, the raise is the *only* part of this that
                    // does anything.
                    if (SessionPresence.AnswersTheClick(verdict) && showing is { } pane)
                    {
                        // Acknowledged first, and deliberately. The flash is a Post
                        // to the UI thread so it cannot delay the raise, and making
                        // the one thing that answers the gesture immediately wait
                        // on a few hundred milliseconds of tmux and osascript is
                        // the same mistake as the invisible no-op it exists to fix.
                        acknowledge?.Invoke();

                        FocusPaneIfAny(pane.Pane, status.Cwd, pane.Socket);
                        return;
                    }

                    FocusPaneIfAny(AgentTeamViewer.AttachSession(sessionId!, status.Cwd), status.Cwd);
                    break;

                case ClickFallback.AttachSocket:
                    // Arrives beside the user, like every other answer here: a
                    // pane in the window they are already in, running an attach
                    // targeted at the clicked session. The pane was selected on
                    // the way past by FocusTmux, and round 14's `-t` is what makes
                    // the attach land on that session rather than on whichever the
                    // server used last — the selects aim within a session and the
                    // target chooses which.
                    FocusPaneIfAny(
                        AgentTeamViewer.AttachTmuxSocket(
                            ResolveTmuxBinary(status.TmuxBin) ?? "", status.TmuxSocket,
                            status.TmuxPane, status.Cwd),
                        status.Cwd);
                    break;

                case ClickFallback.None:
                    // Coordinates were recorded and could not be resolved.
                    // Deliberately nothing further: opening a second window onto a
                    // session that already has one would hide a real failure
                    // behind a new window every time. The failure is reported (to
                    // stderr, which a bundled app has nowhere to show — a
                    // follow-up ticket, not this one).
                    break;
            }
        }

        // "Open agents view", from a background orb's right-click menu.
        //
        // Its own entry point because the roster is no longer an answer to a
        // click — see ClickRouting.AgentsView for the misreading that made it one
        // for an hour, and ClickRouting.OffersTheAgentsView for which orbs offer
        // it. Named rather than routed through FallbackFor, since the user has
        // said which destination they want and there is nothing left to decide.
        //
        // Through RunFallback all the same, so the pane-focusing tail is shared:
        // a roster that opens into tmux still has to be selected and its client's
        // window raised, and that step being forgotten is exactly what once made
        // a second click look like it did nothing.
        public static void OpenAgentsView(SessionStatus? status)
        {
            if (status is null) return;

            Task.Run(() => RunFallback(
                ClickFallback.AgentsView, status, sessionId: null,
                paneClaimsByOthers: null, acknowledge: null));
        }

        // The chat panel's half of the same answer: it has made no focus attempt,
        // so it asks the rule with nothing learned about detached panes and
        // carries out whatever comes back. One verb, one destination — see
        // ClickRouting.AttachWouldReach, which decides whether the button is
        // offered at all.
        public static void Elsewhere(
            SessionStatus? status, string? sessionId,
            IReadOnlyDictionary<string, string>? paneClaimsByOthers = null,
            Action? acknowledge = null)
        {
            if (status is null) return;

            Task.Run(() => RunFallback(
                ClickRouting.FallbackFor(status, sessionId, paneAliveButDetached: false),
                status, sessionId, paneClaimsByOthers, acknowledge));
        }

        // The tail every opener above shares: if what it opened (or found) went
        // into tmux, the pane still has to be selected and its client's window
        // raised, which FocusCore already knows how to do for every other pane in
        // the app. Null means it went into a window of its own, or did not happen
        // — in both cases there is nothing left here to do.
        //
        // This was the part missing when the attach path was first written, and
        // its absence is what made a second click look like it did nothing: the
        // window existed and was reachable by hand, and the click stopped short of
        // switching to it.
        private static void FocusPaneIfAny(string? pane, string cwd, string? socket = null)
        {
            if (string.IsNullOrEmpty(pane)) return;

            // The socket matters as much as the pane id, because a pane id is per
            // server: `%98` on one server and `%98` on another are different panes,
            // and looking for the wrong one finds nothing, says nothing, and drops
            // the click through to minting a new window. Harmless while only the
            // default server was ever read; a live bug the moment the viewer scan
            // could return a pane from a second attached server, which is exactly
            // what round nine's visible universe allows.
            FocusCore(new SessionStatus { TmuxPane = pane, TmuxSocket = socket ?? "", Cwd = cwd });
        }

        // Types transcribed speech into the exact terminal/pane a session's
        // orb represents — the voice-dictation mic's send path (see
        // OrbWindow's recording state and SpeechTranscriber). Never presses
        // Enter: the text lands in the prompt line for the user to review,
        // same as if they'd typed it themselves.
        //
        // Deliberately no team-lead fallback, unlike Focus above: if this
        // specific session has no window or pane of its own, there is
        // nowhere safe to type. The team lead's pane belongs to a *different*
        // session, and typing into it would land words in the wrong place
        // rather than nowhere at all — worse than doing nothing.
        //
        // Unlike Focus, this is awaited rather than fire-and-forget. Focus is
        // fired from a mouse click and must never stall the UI thread; this
        // is the tail of an already-async pipeline (record -> transcribe ->
        // inject) with no UI thread waiting on it, so there's nothing lost by
        // waiting out the same settle time the focus step already needs
        // before it's safe to start typing.
        public static Task SendText(SessionStatus? status, string text)
        {
            if (status is null || string.IsNullOrEmpty(text)) return Task.CompletedTask;

            // Nowhere safe to type. Without this the macOS path falls through to
            // SendTextMacKeystroke, which is an unconditional System Events
            // keystroke into whatever happens to be frontmost — so a dictated
            // sentence lands in an editor, a browser, or another session. That
            // is a latent hazard for any pane-less session; a gateway session
            // would make it the normal case.
            if (!status.IsLocalCli) return Task.CompletedTask;

            // And the other half of the same hazard, which the guard above only
            // covered by accident of who tends to have it. A session that
            // recorded no terminal coordinates at all is a local CLI, so it
            // passes that test, and then every branch below fails to find
            // anywhere to type until the last one sprays keystrokes at whatever
            // is frontmost — a browser, an editor, somebody else's session.
            //
            // The mic is offered on every orb, and this branch keeps a whole
            // class of terminal-less orbs on screen that used to be dropped, so
            // the number of orbs that could do this has gone up. Same predicate
            // the click path uses (ClickRouting.NoCoordinatesAtAll), which is
            // the point: one answer to "is there anywhere to aim this", not two
            // that can drift.
            //
            // Silently nothing, rather than an attach the way a *click* on the
            // same orb gets. A click asks to be taken somewhere and a new window
            // is a fair answer; dictation asks for words to arrive in a specific
            // prompt, and the honest failure is that they do not arrive at all.
            // Typing them into a terminal the user was not looking at, or
            // opening one and racing its startup, are both worse than nothing —
            // and unlike a click, nothing here is irreversible.
            if (ClickRouting.NoCoordinatesAtAll(status)) return Task.CompletedTask;

            return Task.Run(async () =>
            {
                // Reuses FocusCore as-is rather than a bespoke synchronous
                // variant: FocusCore's own osascript calls are fire-and-forget
                // (see RunOsaScript), so there's no return value to await
                // here — just the same fixed settle margin the rest of this
                // file already relies on for activation ordering (see
                // ActivateThenSettle), sized a bit larger because this also
                // has to cover FocusCore's own osascript process launch, not
                // just the `tell application to activate` inside it.
                FocusCore(status);
                await Task.Delay(500);

                if (OperatingSystem.IsWindows())
                {
                    SendUnicodeText(text);
                    return;
                }

                if (!OperatingSystem.IsMacOS()) return;

                if (!string.IsNullOrEmpty(status.TmuxPane) && SendTextTmux(status, text)) return;

                SendTextMacKeystroke(text);
            });
        }

        // send-keys writes directly into the pane's input buffer regardless
        // of whether its window is on screen, but FocusCore has already been
        // asked to bring it forward above, so the user sees it land the same
        // way a click would show them the pane.
        //
        // -l is literal: without it tmux tries to interpret the text as key
        // names ("Enter", "C-c", ...) instead of typing it verbatim, which is
        // exactly the gap between "type this" and "run arbitrary keys".
        private static bool SendTextTmux(SessionStatus status, string text)
        {
            var tmux = ResolveTmuxBinary(status.TmuxBin);
            if (tmux is null) return false;

            return TryRun(tmux, out _, TmuxArgs(status, "send-keys", "-t", status.TmuxPane, "-l", text));
        }

        // --- the chat panel's half ---
        //
        // Everything below sends without anything coming to the front. The
        // keystroke and SendInput fallbacks above type into whatever is
        // *frontmost*, which means focusing the terminal first; that is a fine
        // trade for dictation, which you started by reaching for the orb
        // anyway, and the wrong one for a chat panel whose entire point is not
        // making you leave what you are doing. A session Buddy cannot address
        // gets a read-only panel instead — see ClaudeCodeChatSession.CanType.
        //
        // **This used to mean tmux and nothing else**, which was a much
        // narrower rule than the name suggested: an ordinary `claude` in
        // iTerm2 was told there was "nowhere to type", and on Windows every
        // session was, permanently. TerminalTyping has the routing and the two
        // mechanisms that were tried and rejected first.

        // Whether this session can be typed into without anything coming to the
        // front. The one question the panel asks before enabling its composer.
        public static bool CanSendQuietly(SessionStatus? status) =>
            RouteFor(status) != TerminalTyping.Route.None;

        // The sibling question for a session CanSendQuietly has already said no
        // to: is there still somewhere a message can reach it, over its own
        // registry socket rather than a pane. sessionId is separate from
        // status because SessionStatus carries no session id of its own — it
        // is the status file's name, which every caller here already tracks
        // apart from the object (LocalCliChatSession.SessionId, the sessionId
        // parameter TerminalFocuser.Focus already takes) — so the spec this
        // was built against, which read it off status, named a field that
        // does not exist and this takes it as its own parameter instead.
        //
        // find is SessionMessenger's own shape (a session id in, a registry
        // Entry back) handed in rather than resolved here, so a caller with no
        // real registry to scan — a test, most often — can say so without a
        // live SessionMessenger behind it. The route itself still comes from
        // this file's own RouteFor, the same wrapper CanSendQuietly uses,
        // because TerminalTyping.ChannelFor needs to know Route has already
        // given up before Messaging is even worth asking about.
        public static bool CanDeliver(
            SessionStatus? status, string? sessionId, Func<string, SessionRegistry.Entry?> find)
        {
            if (string.IsNullOrEmpty(sessionId)) return false;

            var registryLive = find(sessionId) is { } entry && SessionRegistry.Speaks(entry);

            return TerminalTyping.ChannelFor(status, RouteFor(status), registryLive)
                   == TerminalTyping.Channel.Messaging;
        }

        // The platform and tmux's availability, resolved here so the rule
        // itself stays pure — and asked in this order because probing for a
        // tmux binary costs a PATH walk that a session with no pane recorded
        // has no reason to pay.
        private static TerminalTyping.Route RouteFor(SessionStatus? status) =>
            TerminalTyping.RouteFor(
                status,
                OperatingSystem.IsMacOS(),
                OperatingSystem.IsWindows(),
                ToolsFor(status));

        // Which of the tools a route needs this machine actually has.
        //
        // Each probe is skipped unless the session claims the terminal it
        // belongs to, because every one of them costs a PATH walk or a
        // subprocess and this is asked on every roster tick.
        [ExcludeFromCodeCoverage]
        private static TerminalTyping.Tools ToolsFor(SessionStatus? status)
        {
            if (status is null) return TerminalTyping.Tools.None;

            return new TerminalTyping.Tools(
                Tmux: !string.IsNullOrEmpty(status.TmuxPane)
                      && ResolveTmuxBinary(status.TmuxBin) is not null,
                Kitty: Named(status, TerminalTyping.KittyProgram) && KittyIsListening(),
                WezTerm: Named(status, TerminalTyping.WezTermProgram) && OnPath("wezterm") is not null);
        }

        private static bool Named(SessionStatus status, string program) =>
            string.Equals(status.TermProgram, program, StringComparison.OrdinalIgnoreCase);

        // kitty's remote control is **off unless the user turned it on**, so
        // the binary being present proves nothing. `kitty @ ls` is the cheapest
        // question that distinguishes the two: it answers with JSON when
        // remote control is allowed and fails outright when it is not.
        //
        // Cached, because a Send button asking every keystroke would be a
        // subprocess per keystroke, and whether kitty is listening changes only
        // when kitty is restarted.
        [ExcludeFromCodeCoverage]
        private static bool KittyIsListening()
        {
            if (_kittyListening is { } known) return known;

            var kitty = OnPath("kitty");
            var answer = kitty is not null && TryRun(kitty, 3000, out _, "@", "ls");

            _kittyListening = answer;
            return answer;
        }

        private static bool? _kittyListening;

        // Excluded from coverage: walks the real PATH.
        [ExcludeFromCodeCoverage]
        private static string? OnPath(string exe)
        {
            var name = OperatingSystem.IsWindows() ? exe + ".exe" : exe;

            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var full = Path.Combine(dir.Trim(), name);
                    if (File.Exists(full)) return full;
                }
                catch
                {
                    // A PATH entry that is not a usable path at all. Windows
                    // picks these up from installers often enough that
                    // throwing here would take out every route on the machine.
                }
            }

            return null;
        }

        // Types the text and presses Enter. The Enter is the whole difference
        // from SendText above, and it is why this is reached only from a Send
        // button behind a setting that is off by default: dictation is a typing
        // aid and doesn't get to decide you meant it, but a person clicking Send
        // has said exactly that.
        //
        // No FocusCore. Nothing comes forward, which is the feature.
        public static Task<bool> SendTextAndSubmit(SessionStatus? status, string text)
        {
            if (status is null || string.IsNullOrEmpty(text)) return Task.FromResult(false);

            return RouteFor(status) switch
            {
                TerminalTyping.Route.Tmux => SendViaTmux(status, text),
                TerminalTyping.Route.Kitty => Task.Run(() => SendViaKitty(status, text)),
                TerminalTyping.Route.WezTerm => Task.Run(() => SendViaWezTerm(status, text)),
                TerminalTyping.Route.ITerm2 => Task.Run(() => SendViaITerm2(status, text)),
                TerminalTyping.Route.TerminalApp => Task.Run(() => SendViaTerminalApp(status, text)),
                TerminalTyping.Route.WindowsConsole => Task.Run(() => SendViaConsole(status, text)),
                _ => Task.FromResult(false),
            };
        }

        // kitty, addressed by the window id it exports as KITTY_WINDOW_ID.
        //
        // Text goes over stdin rather than as an argument, which is what
        // `--stdin` is for: a message is arbitrary user text and a command line
        // is the wrong place for it on every platform, doubly so on Windows
        // where quoting is the caller's problem rather than the shell's.
        //
        // The trailing newline is the Enter. kitty sends exactly the bytes it
        // is given, so the submit has to be one of them.
        [ExcludeFromCodeCoverage]
        private static bool SendViaKitty(SessionStatus status, string text) =>
            Feed(
                OnPath("kitty"),
                TerminalTyping.ForPasting(text) + "\n",
                "@", "send-text", "--match", "id:" + status.TermId, "--stdin");

        // WezTerm, addressed by the pane id it exports as WEZTERM_PANE.
        //
        // `--no-paste` because ForPasting has already decided whether this
        // text needs bracketed-paste markers, and letting wezterm add its own
        // as well would deliver them twice.
        [ExcludeFromCodeCoverage]
        private static bool SendViaWezTerm(SessionStatus status, string text) =>
            Feed(
                OnPath("wezterm"),
                TerminalTyping.ForPasting(text) + "\n",
                "cli", "send-text", "--pane-id", status.TermId, "--no-paste");

        // Runs a tool and hands it the message on stdin.
        //
        // Separate from TryRun because that one does not write stdin, and this
        // is the difference between passing a user's message safely and
        // building a command line around it.
        [ExcludeFromCodeCoverage]
        private static bool Feed(string? exe, string stdin, params string[] args)
        {
            if (exe is null) return false;

            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                foreach (var a in args) psi.ArgumentList.Add(a);

                using var p = Process.Start(psi);
                if (p is null) return false;

                p.StandardInput.Write(stdin);
                p.StandardInput.Close();

                if (!p.WaitForExit(5000))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    return false;
                }

                return p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        // iTerm2, addressed by the session GUID the hook already records.
        //
        // `write text` puts the characters into the session as though they had
        // been typed, and — the property this whole file exists for — does not
        // activate the application. Nothing comes forward.
        //
        // Arguments go through `on run argv` rather than being interpolated
        // into the script. Not tidiness: a message is arbitrary user text, and
        // building AppleScript source around it means escaping quotes,
        // backslashes and newlines correctly every time or executing whatever
        // the user happened to type. argv has no such failure mode, and
        // carries multi-line text unaltered.
        [ExcludeFromCodeCoverage]
        private static bool SendViaITerm2(SessionStatus status, string text) =>
            TellTerminal(
                new[]
                {
                    "on run argv",
                    "set wanted to item 1 of argv",
                    "set body to item 2 of argv",
                    "tell application \"iTerm2\"",
                    "repeat with w in windows",
                    "repeat with t in tabs of w",
                    "repeat with s in sessions of t",
                    "if ((id of s) as string) is wanted then",
                    "tell s to write text body",
                    "return \"ok\"",
                    "end if",
                    "end repeat",
                    "end repeat",
                    "end repeat",
                    "end tell",
                    "return \"no\"",
                    "end run",
                },
                status.TermId,
                TerminalTyping.ForPasting(text));

        // Terminal.app, addressed by tty.
        //
        // Terminal exposes no stable per-tab identifier — a tab's index
        // changes when another is closed and its window's id changes when it
        // is merged — but `tty` is the one handle that follows the session
        // itself, and it is what the hook records.
        //
        // `do script … in <tab>` types the text into that tab and presses
        // Return, which is exactly the contract here, without activating
        // Terminal.
        [ExcludeFromCodeCoverage]
        private static bool SendViaTerminalApp(SessionStatus status, string text) =>
            TellTerminal(
                new[]
                {
                    "on run argv",
                    "set wanted to item 1 of argv",
                    "set body to item 2 of argv",
                    "tell application \"Terminal\"",
                    "repeat with w in windows",
                    "repeat with t in tabs of w",
                    "if ((tty of t) as string) is wanted then",
                    "do script body in t",
                    "return \"ok\"",
                    "end if",
                    "end repeat",
                    "end repeat",
                    "end tell",
                    "return \"no\"",
                    "end run",
                },
                TerminalTyping.DevicePath(status.Tty),
                TerminalTyping.ForPasting(text));

        // Runs one of the two scripts above and reports whether it found its
        // session.
        //
        // Not RunOsaScript, which is deliberately fire-and-forget: a Send
        // button has to know. The script answers "ok" or "no", and the
        // difference between them is the difference between a message
        // delivered and a message silently dropped — which is precisely the
        // failure this whole ticket started as.
        //
        // A longer timeout than TryRun's default because the first Apple Event
        // to an application this process has never talked to can raise a
        // consent prompt, and on a Mac nobody is looking at that prompt is
        // answered slowly or not at all. Failing is correct there; failing in
        // three seconds while the user is still reading the dialog is not.
        [ExcludeFromCodeCoverage]
        private static bool TellTerminal(string[] script, string wanted, string body)
        {
            if (string.IsNullOrEmpty(wanted)) return false;

            var args = new List<string>();
            foreach (var line in script)
            {
                args.Add("-e");
                args.Add(line);
            }

            args.Add("--");
            args.Add(wanted);
            args.Add(body);

            if (!TryRun("/usr/bin/osascript", 15000, out var answer, args.ToArray()))
                return false;

            return answer.Trim() == "ok";
        }

        // --- Windows: the console, not the terminal --------------------------------
        //
        // **The best-addressed route of the six, and the only one that needed
        // nothing taught to it.** `AttachConsole` takes a process id, so this
        // reaches the console of the `claude` process itself — Windows
        // Terminal, conhost and VS Code's integrated terminal are one case,
        // and a new terminal shipping next year will be too. On macOS every
        // emulator has to be taught separately; here none of them does.
        //
        // What it costs is that the console is *process-global state*.
        // `AttachConsole` rebinds this process's own standard handles, so two
        // sends at once would interleave into something neither of them meant.
        // Hence the lock, and hence the `FreeConsole` in a finally: leaving
        // this process attached to a user's terminal would redirect anything
        // Buddy later wrote into their session.
        private static readonly object ConsoleGate = new();

        [SupportedOSPlatform("windows")]
        [ExcludeFromCodeCoverage]
        private static bool SendViaConsole(SessionStatus status, string text)
        {
            // The Enter is a keystroke here rather than a character, so the
            // body and the submit are built separately.
            var body = TerminalTyping.ForPasting(text);

            lock (ConsoleGate)
            {
                // A GUI app normally has no console, so this is usually a
                // no-op — but "usually" is not "always" (a debugger, a
                // console-allocating dependency), and AttachConsole fails
                // outright if one is already attached.
                NativeMethods.FreeConsole();

                if (!NativeMethods.AttachConsole((uint)status.SessionPid)) return false;

                try
                {
                    using var input = NativeMethods.OpenConsoleInput();
                    if (input.IsInvalid) return false;

                    var records = new List<NativeMethods.InputRecord>(body.Length * 2 + 2);

                    foreach (var c in body) AddKeystroke(records, c);

                    // VK_RETURN, with '\r' as its character: a TUI reading
                    // characters sees the carriage return and one reading key
                    // events sees Enter, and both are correct.
                    AddKeystroke(records, '\r', virtualKey: 0x0D);

                    var all = records.ToArray();
                    return NativeMethods.WriteConsoleInput(
                        input, all, (uint)all.Length, out var written)
                        && written == all.Length;
                }
                finally
                {
                    NativeMethods.FreeConsole();
                }
            }
        }

        // One character, as the down-then-up pair a console expects.
        //
        // Both halves, because a TUI that reads key events rather than
        // characters counts them: sending only the key-down leaves every key
        // logically held, and the first one to be checked for a modifier
        // behaves as though it were.
        [SupportedOSPlatform("windows")]
        [ExcludeFromCodeCoverage]
        private static void AddKeystroke(
            List<NativeMethods.InputRecord> into, char c, ushort virtualKey = 0)
        {
            for (var down = 1; down >= 0; down--)
            {
                into.Add(new NativeMethods.InputRecord
                {
                    EventType = NativeMethods.KeyEvent,
                    KeyDown = down,
                    RepeatCount = 1,
                    VirtualKeyCode = virtualKey,
                    VirtualScanCode = 0,
                    Char = c,
                    ControlKeyState = 0,
                });
            }
        }

        [ExcludeFromCodeCoverage]
        private static Task<bool> SendViaTmux(SessionStatus status, string text)
        {
            return Task.Run(() =>
            {
                var tmux = ResolveTmuxBinary(status.TmuxBin);
                if (tmux is null) return false;

                // Through the paste buffer rather than send-keys -l, for
                // multi-line messages: a literal newline sent as a keystroke is
                // indistinguishable from pressing Return, so Shift+Enter in the
                // panel would submit half a sentence and leave the rest to
                // arrive as a second message. paste-buffer -p wraps it in
                // bracketed-paste markers, which the TUI reads as one paste.
                //
                // -p is safe when the pane's application never asked for
                // bracketed paste: tmux then sends the text unwrapped, which for
                // a single line is what send-keys -l would have done anyway.
                //
                // -- so a message starting with a dash isn't read as a flag.
                if (!TryRun(tmux, out _, TmuxArgs(status, "set-buffer", "-b", PasteBuffer, "--", text)))
                    return false;

                // -d deletes the buffer after pasting, so a half-typed message
                // isn't left sitting in tmux's paste stack for the next
                // middle-click anywhere else on the machine to find.
                if (!TryRun(tmux, out _, TmuxArgs(status,
                        "paste-buffer", "-b", PasteBuffer, "-t", status.TmuxPane, "-p", "-d")))
                    return false;

                return TryRun(tmux, out _, TmuxArgs(status, "send-keys", "-t", status.TmuxPane, "Enter"));
            });
        }

        private const string PasteBuffer = "claude-buddy";

        // A named key — "Enter", "Escape", or a bare digit for a numbered
        // dialog. Not -l: these are key names, which is the one case where
        // letting tmux interpret the argument is the point rather than the
        // hazard. Only ever called with a constant or with a digit this app
        // read off the pane itself, never with anything a person typed.
        public static Task<bool> SendPaneKey(SessionStatus? status, string key)
        {
            if (status is null || string.IsNullOrEmpty(key)) return Task.FromResult(false);
            if (!CanSendQuietly(status)) return Task.FromResult(false);

            return Task.Run(() =>
            {
                var tmux = ResolveTmuxBinary(status.TmuxBin);
                if (tmux is null) return false;

                return TryRun(tmux, out _, TmuxArgs(status, "send-keys", "-t", status.TmuxPane, key));
            });
        }

        // What the pane is showing right now, as text.
        //
        // This is how a permission prompt gets answered from the panel without
        // guessing. The dialog is drawn by the TUI and never reaches the
        // transcript, so the only place its wording exists is the screen —
        // capture-pane is reading the screen, which is exactly as much as is
        // needed and no more. Without -e, so no escape sequences come back.
        public static Task<string?> CapturePane(SessionStatus? status)
        {
            if (status is null || !CanSendQuietly(status)) return Task.FromResult<string?>(null);

            return Task.Run<string?>(() =>
            {
                var tmux = ResolveTmuxBinary(status.TmuxBin);
                if (tmux is null) return null;

                return TryRun(tmux, out var screen, TmuxArgs(status, "capture-pane", "-p", "-t", status.TmuxPane))
                    ? screen
                    : null;
            });
        }

        // Whether anything was actually brought forward. False means the click
        // had no effect at all, which is what the team-lead fallback above is
        // for — and what made two orbs on screen feel broken before it existed.
        private static bool FocusCore(SessionStatus status) => FocusCore(status, out _);

        // paneAliveButDetached is only ever set by the tmux branch, and only for
        // the one outcome no other rule can see from outside: the pane exists and
        // nothing is attached to its server. Threaded out as a second answer
        // rather than folded into the bool, because it is not a *kind* of failure
        // to focus — it is a fact about where the session is, which the caller
        // needs and which costs three subprocesses to establish.
        private static bool FocusCore(SessionStatus status, out bool paneAliveButDetached)
        {
            paneAliveButDetached = false;

            if (OperatingSystem.IsWindows())
            {
                FocusWindows(status);
                return true;
            }

            if (!OperatingSystem.IsMacOS()) return false;

            // tmux first: when a session is inside tmux, nothing else the hook
            // recorded points at a window you can actually see.
            if (!string.IsNullOrEmpty(status.TmuxPane)
                && FocusTmux(status, out paneAliveButDetached))
            {
                return true;
            }

            string? script;
            if (!string.IsNullOrEmpty(status.TermId))
            {
                script = TerminalScripts.ITermSelectScript("id", status.TermId);
            }
            else
            {
                script = status.TermProgram switch
                {
                    "Apple_Terminal" when !string.IsNullOrEmpty(status.Tty) => TerminalScripts.TerminalSelectScript(status.Tty),
                    "Apple_Terminal" => "tell application \"Terminal\" to activate",
                    "iTerm.app" => "tell application \"iTerm\" to activate",
                    "vscode" => "tell application \"Visual Studio Code\" to activate",
                    "ghostty" => "tell application \"Ghostty\" to activate",
                    "WezTerm" => "tell application \"WezTerm\" to activate",
                    _ => null
                };
            }

            // Nothing named a terminal program, or tmux couldn't be reached.
            // The tty is the one coordinate the hook always records, and the
            // process tree above it says which app owns it — enough to select
            // the exact iTerm2 session or Terminal.app tab, and failing that to
            // bring the owning app forward. Without this a session whose hook
            // recorded a tty but no TERM_PROGRAM — a background session started
            // by another tool, which is what a team lead often is — had an orb
            // that did nothing when clicked.
            if (script is null) return FocusByTty(status.Tty);

            RunOsaScript(script);
            return true;
        }

        private static bool FocusByTty(string tty)
        {
            if (string.IsNullOrEmpty(tty)) return false;

            var app = ResolveAppBundleForTty(tty);
            if (app is null) return false;

            // iTerm reports the full device path, so compare like with like.
            var device = tty.StartsWith("/dev/") ? tty : "/dev/" + tty;

            var script = Path.GetFileName(app) switch
            {
                "iTerm.app" => TerminalScripts.ITermSelectScript("tty", device),
                "Terminal.app" => TerminalScripts.TerminalSelectScript(device),
                _ => null
            };

            if (script is not null)
            {
                RunOsaScript(script);
                return true;
            }

            ActivateApp(app);
            return true;
        }

        // --- tmux ---
        //
        // Two separate jobs, and skipping either one leaves you looking at the
        // wrong thing:
        //   1. Make the session's pane current *inside* tmux — the attached
        //      client is very likely showing some other window/pane, so
        //      activating its terminal alone would land you somewhere else.
        //   2. Activate the terminal app that hosts a client attached to that
        //      session. Which terminal that is can't be recorded at hook time:
        //      you can detach and reattach a tmux session from a different app
        //      (or from none at all), so it's resolved from the live client's
        //      tty on every click.
        private static bool FocusTmux(SessionStatus status, out bool paneAliveButDetached)
        {
            paneAliveButDetached = false;

            var tmux = ResolveTmuxBinary(status.TmuxBin);
            if (tmux is null) return false;

            var pane = status.TmuxPane;

            // Also serves as the liveness check: a pane id from a server that
            // has since exited (or a pane that's been killed) fails here, and
            // we fall back to the non-tmux heuristics.
            if (!TryRun(tmux, out var sessionName, TmuxArgs(status, "display-message", "-p", "-t", pane, "#{session_name}")))
            {
                return false;
            }
            sessionName = sessionName.Trim();
            if (sessionName.Length == 0) return false;

            TryRun(tmux, out _, TmuxArgs(status, "select-window", "-t", pane));
            TryRun(tmux, out _, TmuxArgs(status, "select-pane", "-t", pane));

            var client = ResolveClient(tmux, status, sessionName);

            // No client attached anywhere: the pane is now selected, so the
            // session is waiting correctly for whenever it's next attached,
            // but there's no window to bring forward. Report that we didn't
            // activate anything so the caller can still try its own heuristics
            // rather than treating the click as handled.
            //
            // And say *why*, which is the new part. "Couldn't focus" and "there
            // is a live pane here with no screen on it" are different facts, and
            // only the second one has an answer: attach a terminal to this
            // server and the user is looking at the pane that was already
            // selected two lines up. Left as a bare false, this was the exact
            // path a click on an agent-team member in a detached swarm socket
            // took to doing nothing at all.
            if (client is null)
            {
                paneAliveButDetached = true;
                return false;
            }

            var (clientTty, controlMode) = client.Value;
            var app = ResolveAppBundleForTty(clientTty);

            // iTerm2 and Terminal.app can both select the exact tab the client
            // runs in, which matters when several tmux clients share one app.
            //
            // Except in control mode (iTerm2's native tmux integration,
            // `tmux -CC`), where that tty belongs to the hidden control tab
            // rather than to any window you'd want to look at — iTerm2 mirrors
            // tmux windows as native tabs and follows the select-pane above on
            // its own, so activating the app is both sufficient and correct.
            var script = controlMode ? null : Path.GetFileName(app) switch
            {
                "iTerm.app" => TerminalScripts.ITermSelectScript("tty", clientTty),
                "Terminal.app" => TerminalScripts.TerminalSelectScript(clientTty),
                _ => null
            };

            if (script is not null)
            {
                RunOsaScript(script);
                return true;
            }

            if (app is not null)
            {
                ActivateApp(app);
                return true;
            }

            // Couldn't work out which app owns the client's tty. The pane is
            // selected, but nothing was brought forward — say so, so the
            // caller falls through instead of swallowing the click.
            return false;
        }

        // Works for any terminal without a case per app: `open -a` on a running
        // app just brings it forward.
        private static void ActivateApp(string appBundlePath)
        {
            MacOSWindowExtensions.WaitForOwnActivation();

            try
            {
                var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
                psi.ArgumentList.Add("-a");
                psi.ArgumentList.Add(appBundlePath);
                Process.Start(psi);
            }
            catch { }
        }

        // Kept as a wrapper over TerminalScripts.TmuxArgs so its twelve call
        // sites read the same as before; the socket-pinning rule itself is tested
        // there.
        private static string[] TmuxArgs(SessionStatus status, params string[] args) =>
            TerminalScripts.TmuxArgs(status.TmuxSocket, args);

        // The app can't count on PATH: launched from Finder or Login Items it
        // gets the bare system PATH, with no Homebrew or MacPorts in it. The
        // hook records where tmux actually was, and these are the fallbacks
        // for status files written before it did.
        private static readonly string[] TmuxCandidates =
        {
            "/opt/homebrew/bin/tmux",
            "/usr/local/bin/tmux",
            "/opt/local/bin/tmux",
            "/usr/bin/tmux"
        };

        private static string? ResolveTmuxBinary(string recorded)
        {
            if (!string.IsNullOrEmpty(recorded) && File.Exists(recorded)) return recorded;
            return TmuxCandidates.FirstOrDefault(File.Exists);
        }

        // Prefer a client already looking at the session; otherwise commandeer
        // one — switching some client to it is the only way to get the session
        // on screen at all. Either way, ties break toward the most recently
        // active client: a session can be attached from several terminals at
        // once, and the one you touched last is the one you're sitting at.
        private static (string Tty, bool ControlMode)? ResolveClient(string tmux, SessionStatus status, string sessionName)
        {
            if (!TryRun(tmux, out var listing, TmuxArgs(
                    status, "list-clients", "-F", TerminalScripts.ClientListFormat)))
            {
                return null;
            }

            // One format string and one parse, shared with the viewer scan's own
            // client listing — a field added to one used to be read out of
            // position by the other.
            var clients = TerminalScripts.ParseClients(listing);

            // Which one, decided where it can be read and tested — see
            // TerminalScripts.ChooseClient. Everything below is aimed by the
            // client this returns: the switch, the app lookup, and the per-tty
            // window selection. With one client attached none of that mattered;
            // with two, choosing wrong brings the wrong window of the same
            // application to the front.
            if (TerminalScripts.ChooseClient(clients, sessionName) is not { } choice) return null;

            // Only when it is not already there. A client on the target session
            // needs no switch, and switching a *second* client onto it would drag
            // that one off whatever its own user was looking at.
            if (choice.NeedsSwitch)
            {
                TryRun(tmux, out _, TmuxArgs(status,
                    "switch-client", "-c", choice.Client.Tty, "-t", sessionName));
            }

            return (choice.Client.Tty, choice.Client.ControlMode);
        }

        // Walks up from whatever is running on a tty until it hits a process
        // living inside an .app bundle — that's the terminal emulator hosting
        // it. Covers Ghostty, WezTerm, kitty, Alacritty, VS Code and friends
        // without needing a case per app.
        private static string? ResolveAppBundleForTty(string tty)
        {
            var name = tty.StartsWith("/dev/") ? tty[5..] : tty;

            if (!TryRun("/bin/ps", out var listing, "-t", name, "-o", "pid=")) return null;

            var pid = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => int.TryParse(l.Trim(), out var p) ? p : 0)
                .FirstOrDefault(p => p > 0);
            if (pid == 0) return null;

            for (var hop = 0; hop < 12 && pid > 1; hop++)
            {
                if (!TryRun("/bin/ps", out var row, "-o", "ppid=,comm=", "-p", pid.ToString())) return null;

                row = row.Trim();
                var split = row.IndexOf(' ');
                if (split <= 0) return null;

                var command = row[(split + 1)..].Trim();
                var marker = command.IndexOf(".app/Contents/MacOS/", StringComparison.Ordinal);
                if (marker >= 0) return command[..(marker + 4)];

                if (!int.TryParse(row[..split].Trim(), out pid)) return null;
            }

            return null;
        }

        // --- process helpers ---

        private static bool TryRun(string exe, out string stdout, params string[] args) =>
            TryRun(exe, 3000, out stdout, args);

        private static bool TryRun(string exe, int timeoutMs, out string stdout, params string[] args)
        {
            stdout = "";
            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,

                    // Not cosmetic, and not implied by redirecting the pipes:
                    // this app is a WinExe, so a console child launched from it
                    // gets a console of its own allocated and *shown* unless
                    // CREATE_NO_WINDOW says otherwise. Measured from a WinExe
                    // parent with this exact ProcessStartInfo: without it the
                    // child owns a visible PseudoConsoleWindow, with it no
                    // window at all and identical stdout and exit code.
                    //
                    // On Windows that window is this file's whole reason for
                    // shelling out gone wrong twice over. It flashes on screen
                    // for the ~400ms the tab-selection helper runs (the "a
                    // terminal pops up and goes away" every orb click and every
                    // dictation produced), and while it exists it holds the
                    // foreground — so the terminal this was supposed to bring
                    // forward loses the race, and dictated text goes wherever
                    // Windows hands focus once the console dies rather than
                    // into the session.
                    //
                    // WslIntegration already sets this on its own launches for
                    // the same reason; this call site simply never did. Ignored
                    // on macOS, where every other TryRun caller lives.
                    CreateNoWindow = true
                };
                foreach (var arg in args) psi.ArgumentList.Add(arg);

                using var process = Process.Start(psi);
                if (process is null) return false;

                // Read both pipes concurrently and only then wait. Doing a
                // blocking ReadToEnd() first would make the timeout below
                // unreachable — it returns when the pipe closes, which a wedged
                // child never does — and leaving stderr undrained can deadlock
                // a chatty one once its pipe buffer fills.
                var outTask = process.StandardOutput.ReadToEndAsync();
                var errTask = process.StandardError.ReadToEndAsync();

                // A wedged tmux server (or, on Windows, a slow UIA broker)
                // would otherwise hang this click forever.
                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(true); } catch { }
                    return false;
                }

                stdout = outTask.GetAwaiter().GetResult();
                errTask.GetAwaiter().GetResult();

                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        // Fire-and-forget on purpose — a click must not wait on AppleScript — but
        // *not* blind. Process.Start succeeds whenever osascript merely launches,
        // so the previous version reported success for every outcome, including
        // the one that matters: error -1743, errAEEventNotPermitted, which is what
        // macOS returns when the app's Automation consent is missing or has been
        // invalidated (any change to the app's code identity does that — a
        // re-signed or replaced bundle counts).
        //
        // That failure is otherwise undetectable from the outside. It looks
        // exactly like a click landing on a terminal that is already frontmost,
        // so with a single terminal window it is invisible on the current Space
        // and only shows up as "clicking does nothing" from another one. It cost
        // a long hunt through a focus path that turned out to be correct.
        //
        // So drain stderr on a background task and say so once. Still never
        // throws into the caller: focusing is a convenience, not worth the app.
        private static void RunOsaScript(string? script)
        {
            if (script is null) return;

            // Let our own activation land first, or the terminal this script
            // brings forward is taken back the instant it arrives. See
            // WaitForOwnActivation.
            MacOSWindowExtensions.WaitForOwnActivation();

            try
            {
                var psi = new ProcessStartInfo("/usr/bin/osascript")
                {
                    UseShellExecute = false,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add(script);

                var process = Process.Start(psi);
                if (process is null) return;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Read before waiting: a full stderr pipe would otherwise
                        // block the child while we block on its exit.
                        var stderr = await process.StandardError.ReadToEndAsync();
                        await process.WaitForExitAsync();
                        if (process.ExitCode != 0) ReportFocusFailure(stderr);
                    }
                    catch { }
                    finally { process.Dispose(); }
                });
            }
            catch
            {
                // Focusing is a convenience; never let it take the app down.
            }
        }

        // Sends System Events a keystroke command for the frontmost app —
        // correct because SendText's caller (Task.Run above) has already
        // asked FocusCore to bring the right window/tab forward and waited
        // out the settle delay before reaching here.
        //
        // A dedicated run-and-report helper rather than reusing RunOsaScript:
        // that one attributes every failure to the Automation permission
        // (-1743), which is the *wrong* diagnosis here — keystroke injection
        // needs Accessibility permission, a separate TCC grant with its own
        // error text, and telling a user to check the wrong settings pane
        // over a permission failure is worse than not explaining it at all.
        private static void SendTextMacKeystroke(string text)
        {
            var script = $$"""
                tell application "System Events"
                    keystroke "{{TerminalScripts.EscapeForAppleScript(text)}}"
                end tell
                """;

            RunOsaScriptForSendText(script);
        }

        // AppleScript string literals only need their own quote and backslash
        // escaped — unlike the tab-selection scripts elsewhere in this file,
        // this text is never a hook-recorded value (a tty, a UUID); it's
        // whatever the user said, so it can contain anything a string can.


        // Mirrors RunOsaScript's fire-and-forget shape (a click, or here a
        // dictation, must not block on an external process) but reports
        // through ReportSendTextFailure instead — see the comment on
        // SendTextMacKeystroke for why the two can't share one reporter.
        private static void RunOsaScriptForSendText(string script)
        {
            try
            {
                var psi = new ProcessStartInfo("/usr/bin/osascript")
                {
                    UseShellExecute = false,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add(script);

                var process = Process.Start(psi);
                if (process is null) return;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var stderr = await process.StandardError.ReadToEndAsync();
                        await process.WaitForExitAsync();
                        if (process.ExitCode != 0) ReportSendTextFailure(stderr);
                    }
                    catch { }
                    finally { process.Dispose(); }
                });
            }
            catch
            {
                // Typing the transcription in is a convenience on top of a
                // convenience; never let it take the app down.
            }
        }

        private static int _sendTextFailureReported;

        private static void ReportSendTextFailure(string stderr)
        {
            if (Interlocked.Exchange(ref _sendTextFailureReported, 1) != 0) return;

            var detail = stderr.Trim();

            if (detail.Contains("not allowed to send keystrokes", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("assistive access", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine(
                    "Claude Buddy: the mic transcribed your speech, but typing it into the " +
                    "terminal failed — macOS has not granted Accessibility permission (this is " +
                    "separate from the Automation permission clicking an orb already uses).\n" +
                    "  Fix: System Settings > Privacy & Security > Accessibility, and enable the " +
                    "terminal app (or Claude Buddy, if System Events prompts for it there instead).\n" +
                    "  If it was granted before a rebuild, the grant may have been invalidated. Run:\n" +
                    "    tccutil reset Accessibility io.github.wtvamp.claudebuddy\n" +
                    "  then dictate again and approve the prompt.");
                return;
            }

            Console.Error.WriteLine($"Claude Buddy: typing the transcribed text failed: {detail}");
        }

        // Once per app run, not per click: a denied grant fails on every click,
        // and a message per click would bury everything else in the log.
        private static int _focusFailureReported;

        private static void ReportFocusFailure(string stderr)
        {
            if (Interlocked.Exchange(ref _focusFailureReported, 1) != 0) return;

            var detail = stderr.Trim();

            // -1743 is the one worth naming, because the user can actually fix it
            // and the wording macOS uses ("Not authorized to send Apple events")
            // does not say where to go.
            if (detail.Contains("-1743") || detail.Contains("Not authorized to send Apple events"))
            {
                Console.Error.WriteLine(
                    "Claude Buddy: clicking an orb can't focus your terminal — macOS has not " +
                    "granted Automation permission.\n" +
                    "  Fix: System Settings > Privacy & Security > Automation, and enable the " +
                    "terminal under Claude Buddy.\n" +
                    "  If Claude Buddy isn't listed, its permission was invalidated by a rebuild. Run:\n" +
                    "    tccutil reset AppleEvents io.github.wtvamp.claudebuddy\n" +
                    "  then click an orb again and approve the prompt.");
                return;
            }

            Console.Error.WriteLine($"Claude Buddy: focusing the terminal failed: {detail}");
        }

        // --- Windows keystroke injection ---
        //
        // SendInput rather than SendKeys.SendWait: SendKeys reads its string
        // as a small escaping language of its own (parentheses, braces, `+`
        // for shift...), and arbitrary dictated text is not written in that
        // language — every character that happens to collide with it would
        // need escaping, which is worse than not using SendKeys at all.
        //
        // KEYEVENTF_UNICODE sends a raw UTF-16 code unit per event and skips
        // virtual-key mapping entirely, so it doesn't care what's plugged in
        // or which keyboard layout is active — including a surrogate pair,
        // which arrives as two code units and reassembles correctly on the
        // receiving end, the same as typing an emoji normally would.
        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public ushort VirtualKey;
            public ushort ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        // MOUSEINPUT and HARDWAREINPUT are declared only to size the union
        // below, never sent. Leaving them out is not a harmless simplification:
        // MOUSEINPUT (32 bytes on x64) is the *largest* member, so a union
        // holding KEYBDINPUT alone is 24 bytes instead of 32, INPUT comes out
        // 32 bytes instead of 40, and SendInput — which validates its cbSize
        // against its own sizeof(INPUT) and accepts nothing else — rejects
        // every call with ERROR_INVALID_PARAMETER and inserts no events.
        //
        // That is exactly how this shipped: dictation recorded and transcribed
        // correctly, the terminal even came to the front, and then nothing was
        // typed, silently, because the return value went unchecked too (it is
        // checked now — see SendUnicodeText). Measured directly: cbSize 32
        // returns 0 / GetLastError 87; the same call at 40 types the text.
        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int X;
            public int Y;
            public uint Data;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HardwareInput
        {
            public uint Msg;
            public ushort ParamL;
            public ushort ParamH;
        }

        // INPUT is a C union of three keyboard/mouse/hardware shapes. All three
        // are declared so the union gets Win32's actual size and layout rather
        // than the size of whichever member this code happens to use.
        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MouseInput Mi;
            [FieldOffset(0)] public KeyboardInput Ki;
            [FieldOffset(0)] public HardwareInput Hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public uint Type;
            public InputUnion U;
        }

        private const uint InputKeyboard = 1;
        private const uint KeyEventFUnicode = 0x0004;
        private const uint KeyEventFKeyUp = 0x0002;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint numberOfInputs, Input[] inputs, int sizeOfInputStructure);

        // The console API, for typing into a session by its own process id.
        //
        // Grouped rather than scattered because these four only make sense
        // together: attach to a process's console, open its input buffer,
        // write key events, detach. See SendViaConsole for why the sequence
        // has to be exactly that and why it holds a lock while it runs.
        [ExcludeFromCodeCoverage]
        private static class NativeMethods
        {
            internal const ushort KeyEvent = 0x0001;

            private const uint GenericRead = 0x80000000;
            private const uint GenericWrite = 0x40000000;
            private const uint FileShareRead = 0x00000001;
            private const uint FileShareWrite = 0x00000002;
            private const uint OpenExisting = 3;

            // Laid out to match Windows' INPUT_RECORD with a KEY_EVENT_RECORD
            // in its union. The union's other members are all smaller, so the
            // explicit size is what a KEY_EVENT_RECORD needs and nothing here
            // reads the others.
            [StructLayout(LayoutKind.Sequential)]
            internal struct InputRecord
            {
                internal ushort EventType;
                internal ushort Padding;
                internal int KeyDown;
                internal ushort RepeatCount;
                internal ushort VirtualKeyCode;
                internal ushort VirtualScanCode;
                internal char Char;
                internal uint ControlKeyState;
            }

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool AttachConsole(uint processId);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool FreeConsole();

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern SafeFileHandle CreateFile(
                string fileName, uint access, uint share, IntPtr security,
                uint creation, uint flags, IntPtr template);

            // CONIN$ rather than GetStdHandle: after AttachConsole the standard
            // handles are the ones this process started with, which for a GUI
            // app are not console handles at all. CONIN$ names the attached
            // console's input buffer whatever the handles say.
            internal static SafeFileHandle OpenConsoleInput() =>
                CreateFile(
                    "CONIN$", GenericRead | GenericWrite,
                    FileShareRead | FileShareWrite, IntPtr.Zero,
                    OpenExisting, 0, IntPtr.Zero);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool WriteConsoleInput(
                SafeFileHandle input, InputRecord[] buffer, uint length, out uint written);
        }

        [SupportedOSPlatform("windows")]
        private static void SendUnicodeText(string text)
        {
            if (text.Length == 0) return;

            var inputs = new Input[text.Length * 2];
            for (var i = 0; i < text.Length; i++)
            {
                inputs[i * 2] = KeyEvent(text[i], keyUp: false);
                inputs[i * 2 + 1] = KeyEvent(text[i], keyUp: true);
            }

            // Checked, not fire-and-forget. SendInput has two failure modes
            // that both look identical to the user — nothing gets typed — and
            // neither throws: a cbSize Windows doesn't recognise (the bug the
            // union above exists to prevent, which is worth catching if the
            // layout ever regresses) and UIPI refusing to let this process
            // send input to a more privileged one, which is what an elevated
            // terminal looks like. Reported once per run, like the macOS
            // permission failures — see ReportSendTextFailure.
            var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
            if (sent == inputs.Length) return;

            var error = Marshal.GetLastWin32Error();
            ReportSendInputFailure(sent, inputs.Length, error);
        }

        private static int _sendInputFailureReported;

        private static void ReportSendInputFailure(uint sent, int expected, int error)
        {
            if (Interlocked.Exchange(ref _sendInputFailureReported, 1) != 0) return;

            // 5 is ERROR_ACCESS_DENIED, which for SendInput means UIPI: a
            // non-elevated process cannot send input to an elevated window.
            // The user can act on that one, so it's worth naming.
            if (error == 5)
            {
                Console.Error.WriteLine(
                    "Claude Buddy: the mic transcribed your speech, but Windows blocked typing it " +
                    "into the terminal — the terminal is running elevated (as Administrator) and " +
                    "Claude Buddy is not.\n" +
                    "  Fix: run the terminal without elevation, or start Claude Buddy elevated too.");
                return;
            }

            Console.Error.WriteLine(
                $"Claude Buddy: typing the transcribed text failed — SendInput accepted {sent} of " +
                $"{expected} events (GetLastError {error}).");
        }

        private static Input KeyEvent(char ch, bool keyUp) => new()
        {
            Type = InputKeyboard,
            U = new InputUnion
            {
                Ki = new KeyboardInput
                {
                    VirtualKey = 0,
                    ScanCode = ch,
                    Flags = KeyEventFUnicode | (keyUp ? KeyEventFKeyUp : 0),
                    Time = 0,
                    ExtraInfo = IntPtr.Zero
                }
            }
        };

        // --- Windows ---

        // OrbWindow sets ShowActivated="False" (it's a click-to-act overlay,
        // not something that should steal keyboard focus just by existing),
        // so clicking it never makes ClaudeBuddy.exe the foreground process —
        // hence WindowsForegroundWindow's AttachThreadInput dance below.
        private static void FocusWindows(SessionStatus status)
        {
            try
            {
                // Tab-exact beats window-exact: an unambiguous tab is the only
                // thing that identifies *which* session's terminal to show,
                // since every tab of a Windows Terminal window shares one
                // process and one MainWindowHandle.
                //
                // Selecting it is not enough on its own, though, and the
                // earlier reading of this (that selecting also raises the
                // window — docs/windows-wt-tabs-findings.md) was true only of
                // the case it was tested in: switching *away* from some other
                // tab. Selecting the tab that is already current is a no-op, so
                // it raises nothing — and clicking an orb or its mic has just
                // made Claude Buddy the foreground app, so "already on the
                // right tab" left the terminal behind us. Dictation into a
                // session you were already looking at typed into the flyout
                // instead, which is exactly the shape of "it only works if
                // you're on the wrong tab".
                //
                // So raise the window explicitly, and the tab's *own* window
                // rather than MainWindowHandle — with several Windows Terminal
                // windows in one process, that property names an arbitrary one.
                if (status.TermProgram == "WindowsTerminal"
                    && TrySelectWindowsTerminalTab(status, out var tabWindow))
                {
                    WindowsForegroundWindow.BringToFront(tabWindow);
                    return;
                }

                var hwnd = IntPtr.Zero;

                if (status.TermPid > 0)
                {
                    try
                    {
                        hwnd = Process.GetProcessById(status.TermPid).MainWindowHandle;
                    }
                    catch { } // terminal exited; fall through
                }

                if (hwnd == IntPtr.Zero)
                {
                    var processName = status.TermProgram switch
                    {
                        "WindowsTerminal" => "WindowsTerminal",
                        "vscode" => "Code",
                        _ => null
                    };
                    if (processName is null) return;

                    hwnd = Process.GetProcessesByName(processName)
                        .Select(p => p.MainWindowHandle)
                        .FirstOrDefault(h => h != IntPtr.Zero);
                }

                WindowsForegroundWindow.BringToFront(hwnd);
            }
            catch
            {
                // Same convenience-only rule as macOS.
            }
        }

        // The working directory's last segment, which is what a shell puts in a
        // Windows Terminal tab. Trailing separators are trimmed first so
        // "C:\src\fmn\" and "C:\src\fmn" give the same answer; a path that is
        // nothing but a root has no leaf and returns empty, which the caller
        // treats as "don't attempt tab selection".


        // WT puts every window of one launch context in a single process, so
        // Process.MainWindowHandle can't tell tabs apart — but UI Automation
        // enumerates the real TabItem elements of every window that process
        // owns, each with a live Name, and a TabItem's SelectionItemPattern
        // genuinely switches to it (confirmed against a real interactive
        // session; both the window and the tab change in one call — see
        // docs/windows-wt-tabs-findings.md). A titled session's tab Name is
        // "✳ " + the chat title.
        //
        // Deliberately NOT matching on a bare "claude" when status.Title is
        // empty, even though a single such tab would in principle be
        // unambiguous: measured live, a fresh session reads literally
        // "claude" for well under a second before Claude Code sets its own
        // "✳ Claude Code" placeholder title, and that placeholder (not
        // "claude") is what an untitled session sits at indefinitely
        // afterwards. So by the time a human actually clicks an orb, a bare
        // "claude" tab is never that session's own tab — it can only be some
        // other session caught mid-startup — and matching it would pick the
        // wrong window's tab with confidence. See findings doc for the
        // second-by-second trace. status.Title empty means: don't attempt
        // tab selection at all, just fall through to window activation.
        //
        // Shelling out to (Windows) PowerShell rather than adding a
        // System.Windows.Automation package reference keeps this file's
        // approach consistent with the macOS side (osascript) and avoids
        // pulling Windows Desktop framework assemblies into a project that
        // also builds for macOS.
        //
        // Never worse than today: this only returns true when exactly one
        // tab matched and Select() ran. Anything else — zero matches, more
        // than one, PowerShell missing, UIA slow, any exception — returns
        // false and FocusWindows falls through to its existing
        // window-activation path unchanged.
        // On success, tabWindow is the handle of the window that owns the matched
        // tab — the caller needs it to actually bring that window forward, which
        // selecting the tab does not reliably do (see FocusWindows).
        private static bool TrySelectWindowsTerminalTab(SessionStatus status, out IntPtr tabWindow)
        {
            tabWindow = IntPtr.Zero;

            // TermPid is no longer required. It is still the cheap path — one
            // process, its windows only — but a Codex session on Windows
            // routinely has none: Windows Terminal is not in its ancestry
            // (measured: powershell -> pwsh -> codex.exe -> node.exe -> sh.exe,
            // whose own parent has already exited), so the walk has nothing to
            // record. Refusing on that left every Codex orb falling through to
            // window activation, which raises the right *window* and shows
            // whatever tab was already in front — a click that visibly does
            // nothing when both sessions share one window, which is the normal
            // case. 0 means "look at every Windows Terminal", and the
            // one-unambiguous-match rule below is unchanged and is what keeps
            // the wider search honest.
            if (status.TermPid < 0) return false;

            // The title alone, with no glyph prefix — the script matches on the
            // tab name's *ending*. "✳ " + title was the original, and it is
            // wrong for exactly half the time an orb is worth clicking: Claude
            // Code swaps that ✳ for an animated braille spinner while it is
            // actually working, so a generating session's tab reads
            // "⠐ Check Claude Code status" (and "⠂ …", and every other frame)
            // rather than "✳ Check Claude Code status".
            //
            // Observed live with two sessions in one window, which is what made
            // it look intermittent — the idle one's tab matched and its orb
            // worked, the generating one's never matched and its orb didn't.
            // Failing that match doesn't fail safe, either: it falls through to
            // MainWindowHandle activation, and since every tab of a Windows
            // Terminal window shares one process, that raises the window with
            // whatever *other* tab was in front still showing.
            //
            // Matching the tail rather than adding the spinner frames to the
            // list of accepted prefixes is deliberate: the frames are an
            // implementation detail of somebody else's progress animation, and
            // the next status glyph Claude Code invents would break a list
            // again. The one-unambiguous-match rule below is what keeps this
            // honest, and it is unchanged.
            //
            // What a tab is actually *called* differs by CLI, so the string to
            // match on does too.
            //
            // Claude Code renames the tab to the chat title, so the title is
            // the identifying text and the tail match above is about its status
            // glyph. Codex renames nothing: its tab keeps whatever the shell
            // put there, which is the working directory's leaf — measured live,
            // a Codex session titled "what branch is this repo on" sat in a tab
            // named "fmn". Matching the title for Codex could therefore never
            // succeed, so that was a dead click by construction rather than an
            // intermittent one.
            //
            // The leaf is weaker evidence than a chat title, and it is worth
            // being clear about that: it names a directory, not a session. Two
            // Codex sessions in one directory, or a plain shell sitting in it,
            // produce tabs that read the same — which is exactly what the
            // exactly-one-match rule is for; two matches refuse and fall
            // through to window activation rather than guess between them. The
            // case it cannot see is a *single* non-Codex tab in that directory,
            // which would be selected; the cost is landing on a terminal in the
            // right directory rather than the right session, and it is why this
            // is a leaf match and not a substring one.
            var target = status.Source == SessionSource.Codex
                ? TerminalScripts.LeafOf(status.Cwd)
                : status.Title;

            if (string.IsNullOrEmpty(target)) return false;

            // The script has to reach powershell.exe as a *file* (-File), not
            // as -Command text with trailing arguments — verified the hard
            // way: powershell.exe's -Command greedily joins every remaining
            // argument onto the script text and reparses the lot as one
            // command line, so $args never receives them; the title just
            // gets spliced onto the end of the script and fails to parse.
            // -File is the only form where trailing arguments actually
            // arrive as $args.
            string scriptPath;
            try
            {
                scriptPath = Path.Combine(Path.GetTempPath(), $"cb-wt-tab-select-{Guid.NewGuid():N}.ps1");
                File.WriteAllText(scriptPath, SelectTabScript);
            }
            catch
            {
                return false;
            }

            try
            {
                // -NonInteractive: this must never pop a console of its own.
                // Bounded well under the "second or two" budget from
                // docs/windows-wt-tabs.md — a full round trip through a fresh
                // powershell.exe measured ~400ms for a handful of windows/tabs.
                var ok = TryRun("powershell.exe", 1500, out var stdout,
                    "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath,
                    status.TermPid.ToString(), target);

                if (!ok) return false;

                // "SELECTED:<hwnd of the tab's window>". A selection that can't
                // name its window is reported as a miss rather than a success:
                // the caller would have nothing to raise, and falling through to
                // window activation is a better outcome than stopping there.
                const string prefix = "SELECTED:";
                var reply = stdout.Trim();
                if (!reply.StartsWith(prefix, StringComparison.Ordinal)) return false;

                if (!long.TryParse(reply[prefix.Length..], out var handle) || handle == 0) return false;

                tabWindow = new IntPtr(handle);
                return true;
            }
            finally
            {
                try { File.Delete(scriptPath); } catch { }
            }
        }

        // $args[0] = target process id, $args[1] = the session title a tab name
        // must end with. Passed as process arguments rather than interpolated
        // into this script text — a session title is arbitrary user text (could
        // contain quotes, `$`, etc.) and splicing it into the script source
        // would be a PowerShell injection risk, not just an escaping
        // nuisance. The comparison is ordinal (case- and byte-exact) on the
        // title itself; only the status glyph ahead of it is allowed to vary.
        //
        // Two things here are about keyboard focus rather than about tabs, and
        // both were paid for. Selecting a tab through UIA puts focus on the tab
        // *header*, not in the terminal — measured with
        // AutomationElement.FocusedElement either side of the call: TermControl
        // before, TabItem/ListViewItem after. When the tab wasn't current that
        // never showed, because switching tabs moves focus into the newly shown
        // pane afterwards; when it was already current, Select() changes no
        // selection and the focus jolt is all that happens. Dictated text then
        // went to a focused tab header, which Windows Terminal takes as the
        // start of an inline rename — "it highlights the tab title and that's
        // it", and only ever on the tab you were already looking at.
        //
        // So: don't Select() a tab that is already selected, and then put focus
        // in the pane explicitly. The SetFocus() is what makes this recover
        // rather than merely stop breaking — a window left focused on its tab
        // header by an earlier run would otherwise stay that way, since nothing
        // else moves focus back. There is exactly one on-screen TermControl (WT
        // only exposes the active tab's) so "the one that isn't offscreen" is
        // unambiguous, and SetFocus on it was confirmed to pull focus back off
        // a tab header.
        private const string SelectTabScript = """
            $targetPid = [int]$args[0]
            $target = $args[1]
            Add-Type -AssemblyName UIAutomationClient
            Add-Type -AssemblyName UIAutomationTypes
            $root = [System.Windows.Automation.AutomationElement]::RootElement
            # A pid of 0 means the hook could not record one (see the caller).
            # Every Windows Terminal is then in scope, which widens what the
            # match has to be unambiguous across but does not weaken the rule
            # itself: still exactly one tab, or nothing happens.
            $targetPids = if ($targetPid -gt 0) { @($targetPid) }
                          else { @(Get-Process WindowsTerminal -ErrorAction SilentlyContinue |
                                   ForEach-Object { $_.Id }) }
            $tabCond = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::TabItem)
            $found = @()
            foreach ($somePid in $targetPids) {
                $procCond = New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $somePid)
                $windows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $procCond)
                foreach ($win in $windows) {
                    foreach ($tab in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tabCond)) {
                        if ($tab.Current.Name.EndsWith($target, [System.StringComparison]::Ordinal)) {
                            $found += [pscustomobject]@{ Tab = $tab; Window = $win; Hwnd = $win.Current.NativeWindowHandle }
                        }
                    }
                }
            }
            if ($found.Count -eq 1) {
                $pattern = $found[0].Tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
                if (-not $pattern.Current.IsSelected) {
                    $pattern.Select()
                    Start-Sleep -Milliseconds 120
                }
                $termCond = New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::ClassNameProperty, 'TermControl')
                foreach ($term in $found[0].Window.FindAll(
                        [System.Windows.Automation.TreeScope]::Descendants, $termCond)) {
                    if (-not $term.Current.IsOffscreen) {
                        try { $term.SetFocus() } catch { }
                        break
                    }
                }
                Write-Output "SELECTED:$($found[0].Hwnd)"
            } else {
                Write-Output "NOMATCH:$($found.Count)"
            }
            """;

    }
}
