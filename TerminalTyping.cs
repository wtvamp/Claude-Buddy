using System;

namespace ClaudeBuddy
{
    // How a line of text gets into a session that is already running.
    //
    // **Buddy could only ever type into tmux, and the gate did not say so.**
    // `CanSendQuietly` read as a general "can this session be typed into"
    // while testing one thing: whether a tmux pane was recorded. A perfectly
    // ordinary `claude` in iTerm2 was refused with "there is nowhere to type",
    // which is not true — and on Windows, where there is no tmux at all,
    // *every* session was refused, silently and permanently.
    //
    // Two mechanisms were investigated and rejected before these, both worth
    // recording so nobody spends the afternoon again:
    //
    //  * **Writing into the tty from outside.** `TIOCSTI` pushes a character
    //    into a terminal's input queue, and it is exactly the primitive this
    //    wants — a `/dev/ttysNNN` is already in every status file. On Darwin
    //    27 it fails `EPERM` even against a pty the calling process created
    //    itself, so there is no generic path and there is not going to be one.
    //  * **Asking the CLI to deliver it.** `claude`'s subcommands are all
    //    about *background* sessions — `attach`, `logs`, `stop`, `respawn`.
    //    Nothing hands a line to a running interactive session.
    //
    // What is left is per-terminal, so this is a routing problem. Six routes,
    // which between them cover every terminal anyone here has been able to
    // name, and each addresses **this session** rather than "whatever is
    // focused" — the property that makes typing safe to do without looking:
    //
    // | Route | Addressed by | Where |
    // | --- | --- | --- |
    // | tmux | pane id | anywhere tmux runs |
    // | kitty | window id | macOS, Linux |
    // | WezTerm | pane id | macOS, Linux, Windows |
    // | iTerm2 | session GUID | macOS |
    // | Terminal.app | tty | macOS |
    // | the console | the session's own pid | Windows |
    //
    // The Windows one is the odd one and the best of them: `AttachConsole`
    // takes a *process id*, so it reaches the console of the `claude` process
    // itself. Windows Terminal, conhost and VS Code's terminal are all the
    // same case and none of them needs its own integration — where on macOS
    // each emulator has to be taught separately.
    //
    // The decision is pure and lives here; the delivery lives in
    // TerminalFocuser beside the tmux one it joins.
    internal static class TerminalTyping
    {
        internal enum Route
        {
            // Nothing can be typed into this session without bringing its
            // window forward, which is the one thing Send must never do.
            None,

            // tmux `paste-buffer`, the original and still the best: tmux knows
            // whether the pane's application asked for bracketed paste, so it
            // is the only route that never has to guess.
            Tmux,

            // `kitty @ send-text --match id:<n>`.
            Kitty,

            // `wezterm cli send-text --pane-id <n>`.
            WezTerm,

            // iTerm2's `write text`, addressed by the session GUID.
            ITerm2,

            // Terminal.app's `do script … in <tab>`, addressed by tty, which
            // is the only handle Terminal exposes that survives a tab moving
            // between windows.
            TerminalApp,

            // `AttachConsole` + `WriteConsoleInput`, addressed by the
            // session's own pid.
            WindowsConsole,
        }

        // What each terminal calls itself in TERM_PROGRAM.
        //
        // The values a shell exports, not the application names — Terminal
        // sets `Apple_Terminal` and is called "Terminal", and matching the
        // wrong one of those is a bug that only shows up on somebody else's
        // machine. kitty sets nothing at all, so the hook fills this one in
        // rather than leaving the one terminal that declines to say
        // unaddressable over a missing variable.
        internal const string ITerm2Program = "iTerm.app";
        internal const string TerminalAppProgram = "Apple_Terminal";
        internal const string KittyProgram = "kitty";
        internal const string WezTermProgram = "WezTerm";

        // Which of the tools a route needs is actually on this machine.
        //
        // A parameter rather than a probe inside the rule, for the reason the
        // platform is: a machine has one answer and the interesting cases are
        // the ones it does not have. kitty is here as well as on PATH because
        // its remote control is **off unless the user turned it on** — so the
        // binary existing proves nothing, and a route that looks available and
        // then fails is the silent-nothing this ticket is about.
        internal readonly record struct Tools(bool Tmux, bool Kitty, bool WezTerm)
        {
            internal static readonly Tools None = new(false, false, false);
        }

        // Which mechanism applies to this session.
        //
        // Takes the platform as parameters rather than calling
        // `OperatingSystem.IsMacOS()` — the rule
        // `OpenClawLocalNetworkHintTests` already follows, so that every arm
        // runs on both CI legs instead of only the one it happens to describe.
        internal static Route RouteFor(
            SessionStatus? status, bool onMacOS, bool onWindows, Tools tools)
        {
            // Not a CLI on this disk: an OpenClaw conversation has no terminal
            // here at all, whatever else it has.
            if (status is not { IsLocalCli: true }) return Route.None;

            // **A background job is run by a daemon precisely so that no
            // terminal has to hold it.** There is no window and no console, so
            // there is nothing to address — which is why the panel offers
            // `claude attach` there instead of a composer.
            //
            // Caught by CI on the Windows leg and nowhere else, which is the
            // whole argument for the console route being keyed on a pid: a pid
            // is something a background job *has*, where a terminal handle is
            // something it conspicuously does not. Without this the Windows
            // arm claimed it could type into a daemon, and the attach
            // affordance — the only way to actually answer one — went away.
            if (status.Shape == LocalSessionShape.Background) return Route.None;

            // First, because it is the only route that does not have to guess
            // about bracketed paste, and because a session inside tmux may
            // *also* report iTerm2 or kitty as its TERM_PROGRAM — tmux is what
            // owns the input, and answering the emulator there would type into
            // the terminal around the session rather than into it.
            if (tools.Tmux && !string.IsNullOrEmpty(status.TmuxPane)) return Route.Tmux;

            // Neither of these is macOS-only, which is the point of doing them
            // at all: WezTerm on Windows is the one terminal there that can be
            // addressed without touching a console.
            if (tools.Kitty && Is(status.TermProgram, KittyProgram)
                && !string.IsNullOrEmpty(status.TermId))
                return Route.Kitty;

            if (tools.WezTerm && Is(status.TermProgram, WezTermProgram)
                && !string.IsNullOrEmpty(status.TermId))
                return Route.WezTerm;

            if (onMacOS)
            {
                if (Is(status.TermProgram, ITerm2Program) && !string.IsNullOrEmpty(status.TermId))
                    return Route.ITerm2;

                if (Is(status.TermProgram, TerminalAppProgram) && !string.IsNullOrEmpty(status.Tty))
                    return Route.TerminalApp;

                return Route.None;
            }

            // Last, and general: a console belongs to a *process*, so this
            // needs nothing from the terminal at all. Windows Terminal,
            // conhost and VS Code's terminal are one case here.
            //
            // The pid is the session's own, recorded by the hook for a
            // different reason (telling a live session from a status file left
            // behind) and exactly the handle this wants.
            if (onWindows && status.SessionPid > 0) return Route.WindowsConsole;

            return Route.None;
        }

        // A narrower question than Route above, and deliberately a separate
        // concept rather than a seventh arm of it: Route means "a TUI's own
        // input line, addressed the way bracketed paste and SendPaneKey
        // already assume" — and a delivered message never goes through that
        // pipe. Messaging is the answer for a session Route has already given
        // up on: a `claude bg-spare` pooled worker (Shape == Background) and an
        // `--agent` direct child the daemon doesn't list (SessionPresence and
        // ClickRouting.NoCoordinatesAtAll both treat that one specially
        // already) land on Route.None for different reasons, and both are
        // exactly "no terminal, so ask the registry instead" — which is why
        // this function does not need to tell the two apart itself.
        //
        // registryLive is a plain bool rather than a lookup run in here, the
        // same reason Tools above is a parameter instead of a probe: the
        // caller already knows how to ask SessionRegistry, and folding the
        // scan into every route decision would charge it to sessions that
        // never reach Route.None at all.
        internal enum Channel { None, Terminal, Messaging }

        internal static Channel ChannelFor(SessionStatus? status, Route route, bool registryLive)
        {
            if (route != Route.None) return Channel.Terminal;

            if (status is { IsLocalCli: true, Source: SessionSource.ClaudeCode } && registryLive)
                return Channel.Messaging;

            return Channel.None;
        }

        private static bool Is(string? program, string name) =>
            string.Equals(program, name, StringComparison.OrdinalIgnoreCase);

        // The tty as Terminal.app reports it.
        //
        // The hook records what `tty` printed, which on macOS is already an
        // absolute path — but a bare `ttys000` has been seen from at least one
        // shell, and comparing that against Terminal's `/dev/ttys000` matches
        // nothing while looking entirely correct.
        internal static string DevicePath(string? tty)
        {
            if (string.IsNullOrWhiteSpace(tty)) return "";

            return tty.StartsWith('/') ? tty : "/dev/" + tty;
        }

        // Bracketed-paste markers, or not.
        //
        // **The one thing tmux does for free and this has to decide.** A
        // literal newline sent as a keystroke is indistinguishable from
        // pressing Return, so a multi-line message typed straight in would
        // submit its first line and leave the rest to arrive as separate
        // messages. Wrapping it tells the TUI to take the whole thing as one
        // paste.
        //
        // Only for text that needs it. tmux can ask the pane's application
        // whether it wants bracketed paste and send the text unwrapped when it
        // does not; through an emulator's scripting interface there is nobody
        // to ask, so a wrapper is an assumption — a safe one for the two TUIs
        // this ever types into, both of which accept pasted input, but an
        // assumption all the same. A single line does not need it and so does
        // not take it, which leaves the common case with no escape sequences
        // in it at all.
        internal const string PasteStart = "\u001b[200~";
        internal const string PasteEnd = "\u001b[201~";

        internal static string ForPasting(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            return text.Contains('\n') || text.Contains('\r')
                ? PasteStart + text + PasteEnd
                : text;
        }

        // Why a session cannot be typed into, in words a person can act on.
        //
        // The old sentence named tmux as though it were the only way to type,
        // which sent at least one user looking for a tmux setting they did not
        // want and did not need. What is true is narrower: this terminal is
        // not one Buddy can address.
        // The phrase every refusal shares, whatever the platform.
        //
        // **A contract, and it was broken the moment it was written.** The
        // Windows arm originally said "Buddy couldn't find the console…" and
        // nothing else — perfectly true, and it cost a green CI run: two UI
        // tests assert the panel explains itself, they match on this phrase,
        // and they run on both legs. Nobody would have noticed on a Mac.
        //
        // Keeping it constant is also the honest thing for a reader. Every one
        // of these sentences answers the same question — *why can't I type
        // here* — and a user who has learned to recognise one answer should
        // not have to learn a second because they moved machines.
        internal const string CantTypePhrase = "isn't a terminal Buddy can type into";

        internal static string WhyNot(SessionStatus? status, bool onMacOS, bool onWindows)
        {
            if (status is not { IsLocalCli: true })
                return "this isn't a CLI session on that machine, so there is nowhere to type.";

            var program = string.IsNullOrWhiteSpace(status.TermProgram)
                ? "its terminal"
                : status.TermProgram;

            // **Every arm carries the same phrase**, and that is a contract
            // rather than a stylistic preference — see CantTypePhrase.
            //
            // On Windows every session has a console, so the only way to land
            // here is a status file with no pid: one written by a hook older
            // than the field, or by a session that had already gone.
            if (onWindows)
                return $"{program} {CantTypePhrase} — Buddy couldn't find the console this "
                       + "session is running in.";

            if (!onMacOS)
                return $"{program} {CantTypePhrase} on this platform.";

            return $"{program} {CantTypePhrase} without bringing it forward. "
                   + "tmux, iTerm2, Terminal.app, kitty and WezTerm are the ones it can.";
        }
    }
}
