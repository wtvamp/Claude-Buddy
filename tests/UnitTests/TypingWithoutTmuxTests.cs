using ClaudeBuddy;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// Which mechanism types into a session, and why one of them used to be the
// only answer.
//
// **Every case here takes the platform as a parameter** rather than calling
// `OperatingSystem.IsMacOS()`, so both arms run on both CI legs — the rule
// `OpenClawLocalNetworkHintTests` already follows. That matters more than
// usual for this file: the Windows arm is a refusal, and a refusal that only
// ever runs on the machine it describes is a refusal nobody has tested.
public class TypingWithoutTmuxTests
{
    private static SessionStatus Claude(
        string? tmuxPane = null,
        string? termProgram = null,
        string? termId = null,
        string? tty = null) =>
        new()
        {
            Cli = "claude",
            TmuxPane = tmuxPane ?? "",
            TermProgram = termProgram ?? "",
            TermId = termId ?? "",
            Tty = tty ?? "",
        };

    // --- what used to be the only answer ---------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TmuxWinsWhereverItIsAvailable(bool onMacOS)
    {
        // First even on a machine that could also address the emulator, and
        // the ordering is not arbitrary: a session inside tmux still reports
        // iTerm2 as its TERM_PROGRAM, because iTerm2 is what tmux is running
        // in. tmux owns the input, so answering iTerm2 would type the message
        // into the terminal *around* the session rather than into it.
        Assert.Equal(
            TerminalTyping.Route.Tmux,
            TerminalTyping.RouteFor(
                Claude(tmuxPane: "%0", termProgram: "iTerm.app", termId: "GUID"),
                onMacOS, onWindows: false, new TerminalTyping.Tools(Tmux: true, Kitty: true, WezTerm: true)));
    }

    [Fact]
    public void APaneWithNoTmuxBinaryIsNotARoute()
    {
        // The machine has the pane recorded but nothing to drive it with —
        // an upgrade that moved the binary, most often.
        Assert.Equal(
            TerminalTyping.Route.None,
            TerminalTyping.RouteFor(
                Claude(tmuxPane: "%0"), onMacOS: true, onWindows: false, TerminalTyping.Tools.None));
    }

    // --- the terminals this ticket added ---------------------------------------

    [Fact]
    public void AnOrdinaryITerm2SessionCanBeTypedInto()
    {
        // The case that started CB-79. A plain `claude` in iTerm2, refused
        // with "there is nowhere to type" — which was not true.
        Assert.Equal(
            TerminalTyping.Route.ITerm2,
            TerminalTyping.RouteFor(
                Claude(termProgram: "iTerm.app", termId: "DAE2A8B4-78AF-4C2A-B5A6-4803FD95331C"),
                onMacOS: true, onWindows: false, TerminalTyping.Tools.None));
    }

    [Fact]
    public void AnOrdinaryTerminalAppSessionCanToo()
    {
        Assert.Equal(
            TerminalTyping.Route.TerminalApp,
            TerminalTyping.RouteFor(
                Claude(termProgram: "Apple_Terminal", tty: "/dev/ttys003"),
                onMacOS: true, onWindows: false, TerminalTyping.Tools.None));
    }

    [Fact]
    public void TermProgramIsMatchedCaseInsensitively()
    {
        // It comes from a shell's environment, and this app does not own what
        // sets it.
        Assert.Equal(
            TerminalTyping.Route.ITerm2,
            TerminalTyping.RouteFor(
                Claude(termProgram: "iterm.app", termId: "GUID"),
                onMacOS: true, onWindows: false, TerminalTyping.Tools.None));
    }

    [Fact]
    public void ITerm2WithoutASessionIdIsNotARoute()
    {
        // The GUID is the only handle iTerm2 offers that survives a session
        // moving between tabs. Without it there is nothing to address, and
        // guessing would type into whichever session happened to be first.
        Assert.Equal(
            TerminalTyping.Route.None,
            TerminalTyping.RouteFor(
                Claude(termProgram: "iTerm.app"), onMacOS: true, onWindows: false, TerminalTyping.Tools.None));
    }

    [Fact]
    public void TerminalAppWithoutATtyIsNotARoute()
    {
        Assert.Equal(
            TerminalTyping.Route.None,
            TerminalTyping.RouteFor(
                Claude(termProgram: "Apple_Terminal"), onMacOS: true, onWindows: false, TerminalTyping.Tools.None));
    }

    [Fact]
    public void ATerminalNobodyHasTaughtItAboutIsRefused()
    {
        // Ghostty, WezTerm, Alacritty, kitty. Each has its own automation
        // surface or none at all, and pretending otherwise would be a Send
        // button that silently does nothing.
        Assert.Equal(
            TerminalTyping.Route.None,
            TerminalTyping.RouteFor(
                Claude(termProgram: "Ghostty", tty: "/dev/ttys004"),
                onMacOS: true, onWindows: false, TerminalTyping.Tools.None));
    }

    // --- the platform arm ------------------------------------------------------

    [Fact]
    public void OffMacOsThereIsNoEmulatorRoute()
    {
        // Apple Events are Apple's. Windows gets tmux — under WSL — or
        // nothing, which is a real gap named in CB-80 rather than a pretence
        // that the Send button works.
        Assert.Equal(
            TerminalTyping.Route.None,
            TerminalTyping.RouteFor(
                Claude(termProgram: "iTerm.app", termId: "GUID"),
                onMacOS: false, onWindows: true, TerminalTyping.Tools.None));

        Assert.Equal(
            TerminalTyping.Route.Tmux,
            TerminalTyping.RouteFor(
                Claude(tmuxPane: "%0"), onMacOS: false, onWindows: true, new TerminalTyping.Tools(Tmux: true, Kitty: false, WezTerm: false)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SomethingThatIsNotALocalCliIsNeverTypedInto(bool onMacOS)
    {
        // An OpenClaw conversation has no terminal on this machine at all,
        // whatever else it has.
        Assert.Equal(
            TerminalTyping.Route.None,
            TerminalTyping.RouteFor(
                new SessionStatus { Cli = "openclaw", Source = SessionSource.OpenClaw, TermProgram = "iTerm.app", TermId = "GUID" },
                onMacOS, onWindows: false, new TerminalTyping.Tools(Tmux: true, Kitty: true, WezTerm: true)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NoSessionAtAllIsNotARoute(bool onMacOS)
    {
        Assert.Equal(
            TerminalTyping.Route.None,
            TerminalTyping.RouteFor(null, onMacOS, onWindows: false, new TerminalTyping.Tools(Tmux: true, Kitty: true, WezTerm: true)));
    }

    // --- the terminals that ship their own CLI ---------------------------------

    private static readonly TerminalTyping.Tools Everything =
        new(Tmux: true, Kitty: true, WezTerm: true);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void KittyAndWezTermAreNotMacOsOnly(bool onMacOS, bool onWindows)
    {
        // The point of doing them at all. WezTerm on Windows is the one
        // terminal there that can be addressed without touching a console, and
        // both work on Linux, where none of the other five routes does.
        Assert.Equal(
            TerminalTyping.Route.Kitty,
            TerminalTyping.RouteFor(
                Claude(termProgram: "kitty", termId: "3"), onMacOS, onWindows, Everything));

        Assert.Equal(
            TerminalTyping.Route.WezTerm,
            TerminalTyping.RouteFor(
                Claude(termProgram: "WezTerm", termId: "17"), onMacOS, onWindows, Everything));
    }

    [Fact]
    public void KittyWithItsRemoteControlOffIsNotARoute()
    {
        // **The reason `Tools` exists rather than a PATH check.** kitty's
        // remote control is off unless the user turned it on, so the binary
        // being installed proves nothing — and a route that looks available
        // and then fails is the silent-nothing this whole ticket is about.
        Assert.Equal(
            TerminalTyping.Route.None,
            TerminalTyping.RouteFor(
                Claude(termProgram: "kitty", termId: "3"),
                onMacOS: true, onWindows: false,
                new TerminalTyping.Tools(Tmux: false, Kitty: false, WezTerm: true)));
    }

    [Fact]
    public void WezTermWithNoBinaryOnPathIsNotARoute()
    {
        Assert.Equal(
            TerminalTyping.Route.None,
            TerminalTyping.RouteFor(
                Claude(termProgram: "WezTerm", termId: "17"),
                onMacOS: true, onWindows: false,
                new TerminalTyping.Tools(Tmux: false, Kitty: true, WezTerm: false)));
    }

    [Theory]
    [InlineData("kitty")]
    [InlineData("WezTerm")]
    public void NeitherIsARouteWithoutAPaneId(string program)
    {
        // The id is the whole address. Without it there is nothing to send to,
        // and the CLI's own default — the *active* pane — would type into
        // whichever window the user was last looking at.
        Assert.Equal(
            TerminalTyping.Route.None,
            TerminalTyping.RouteFor(
                Claude(termProgram: program), onMacOS: true, onWindows: false, Everything));
    }

    // --- Windows: the console, addressed by pid ---------------------------------

    [Theory]
    [InlineData("WindowsTerminal")]
    [InlineData("vscode")]
    [InlineData("")]
    public void AnyWindowsTerminalIsTheSameCase(string program)
    {
        // **The route that needed nothing taught to it.** AttachConsole takes
        // a process id, so Windows Terminal, conhost and VS Code's integrated
        // terminal are one case — and a terminal shipping next year will be
        // too. TERM_PROGRAM is not consulted at all here, which is the point.
        Assert.Equal(
            TerminalTyping.Route.WindowsConsole,
            TerminalTyping.RouteFor(
                WithPid(Claude(termProgram: program), 4242),
                onMacOS: false, onWindows: true, TerminalTyping.Tools.None));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ABackgroundJobHasNoTerminalOnEitherPlatform(bool onMacOS, bool onWindows)
    {
        // **The regression CI caught, and only on the Windows leg.** A
        // background job is run by a daemon so that no terminal has to hold
        // it — and a pid is something it *has*, where a terminal handle is
        // something it conspicuously does not. Keying the console route on a
        // pid therefore claimed a daemon could be typed into, and the attach
        // affordance that is the only real way to answer one disappeared.
        var job = WithPid(Claude(termProgram: "WindowsTerminal"), 4242);
        job.Shape = LocalSessionShape.Background;

        Assert.Equal(
            TerminalTyping.Route.None,
            TerminalTyping.RouteFor(job, onMacOS, onWindows, Everything));
    }

    [Fact]
    public void ABackgroundJobInsideTmuxIsStillNotTypedInto()
    {
        // Belt and braces on the ordering: the tmux arm comes first, so the
        // shape check has to precede it rather than sit with the others.
        var job = WithPid(Claude(tmuxPane: "%0"), 4242);
        job.Shape = LocalSessionShape.Background;

        Assert.Equal(
            TerminalTyping.Route.None,
            TerminalTyping.RouteFor(job, onMacOS: true, onWindows: false, Everything));
    }

    [Fact]
    public void AWindowsSessionWithNoRecordedPidIsNot()
    {
        // A status file from a hook older than the field, or one written by a
        // session that had already gone. Both are "cannot tell", and typing
        // into pid 0 is not a thing to attempt.
        Assert.Equal(
            TerminalTyping.Route.None,
            TerminalTyping.RouteFor(
                Claude(termProgram: "WindowsTerminal"),
                onMacOS: false, onWindows: true, TerminalTyping.Tools.None));
    }

    [Fact]
    public void MacOsNeverFallsThroughToTheConsole()
    {
        // The console arm is after the macOS block and guarded by onWindows,
        // so a Mac session with a pid and an unknown terminal is refused
        // rather than routed somewhere that does not exist.
        Assert.Equal(
            TerminalTyping.Route.None,
            TerminalTyping.RouteFor(
                WithPid(Claude(termProgram: "Ghostty"), 4242),
                onMacOS: true, onWindows: false, Everything));
    }

    private static SessionStatus WithPid(SessionStatus status, int pid)
    {
        status.SessionPid = pid;
        return status;
    }

    // --- the tty, as Terminal.app reports it -----------------------------------

    [Fact]
    public void ABareTtyNameIsMadeIntoADevicePath()
    {
        // Terminal reports `/dev/ttys000`. A bare `ttys000` compared against
        // that matches nothing while looking entirely correct, which is the
        // most expensive kind of wrong.
        Assert.Equal("/dev/ttys000", TerminalTyping.DevicePath("ttys000"));
    }

    [Fact]
    public void AnAbsolutePathIsLeftAlone()
    {
        Assert.Equal("/dev/ttys000", TerminalTyping.DevicePath("/dev/ttys000"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoTtyIsNoPath(string? nothing)
    {
        Assert.Equal("", TerminalTyping.DevicePath(nothing));
    }

    // --- bracketed paste -------------------------------------------------------

    [Fact]
    public void ASingleLineIsSentWithNoEscapeSequencesAtAll()
    {
        // The common case, and the one where a wrapper would be an assumption
        // for no benefit: there is no second line to protect.
        Assert.Equal("ship it", TerminalTyping.ForPasting("ship it"));
    }

    [Theory]
    [InlineData("first\nsecond")]
    [InlineData("first\r\nsecond")]
    [InlineData("trailing\n")]
    public void AnythingWithANewlineIsWrappedAsOnePaste(string many)
    {
        // Without this a literal newline is indistinguishable from pressing
        // Return, so Shift+Enter in the panel would submit half a sentence and
        // leave the rest to arrive as a second message.
        var sent = TerminalTyping.ForPasting(many);

        Assert.StartsWith(TerminalTyping.PasteStart, sent, StringComparison.Ordinal);
        Assert.EndsWith(TerminalTyping.PasteEnd, sent, StringComparison.Ordinal);
        Assert.Contains(many, sent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMarkersAreTheRealEscapeSequences()
    {
        // Asserted rather than assumed, because a wrapper missing its ESC is
        // not a no-op — it types `[200~` into the conversation.
        Assert.Equal("\u001b[200~", TerminalTyping.PasteStart);
        Assert.Equal("\u001b[201~", TerminalTyping.PasteEnd);
    }

    [Fact]
    public void NothingToSendIsLeftAlone()
    {
        Assert.Equal("", TerminalTyping.ForPasting(""));
    }

    // --- what the user is told -------------------------------------------------

    [Fact]
    public void TheRefusalNamesTheTerminalItFound()
    {
        // The old sentence named tmux as though it were the only way to type,
        // which sent a user looking for a tmux setting they did not want — for
        // a session in an ordinary iTerm2 window all along.
        var why = TerminalTyping.WhyNot(Claude(termProgram: "Ghostty"), onMacOS: true, onWindows: false);

        Assert.Contains("Ghostty", why, StringComparison.Ordinal);
        Assert.Contains("iTerm2", why, StringComparison.Ordinal);
        Assert.DoesNotContain("isn't in a tmux pane", why, StringComparison.Ordinal);
    }

    [Fact]
    public void ATerminalThatDidNotSayWhatItWasStillGetsASentence()
    {
        var why = TerminalTyping.WhyNot(Claude(), onMacOS: true, onWindows: false);

        Assert.Contains("its terminal", why, StringComparison.Ordinal);
    }

    [Fact]
    public void OffMacOsTheRefusalSaysWhatIsActuallyTrue()
    {
        var why = TerminalTyping.WhyNot(Claude(termProgram: "Windows Terminal"), onMacOS: false, onWindows: true);

        Assert.Contains("Windows", why, StringComparison.Ordinal);
    }

    // Every platform's refusal says the same recognisable thing.
    //
    // **This is the test that would have caught the CI failure that produced
    // it.** The Windows arm was written as "Buddy couldn't find the console…"
    // and nothing else — true, and it broke two UI tests that match on the
    // shared phrase and run on both legs. On a Mac nothing looked wrong.
    //
    // Both platform flags, both directions, because a phrase that holds on one
    // leg and not the other is exactly the bug.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void EveryPlatformSaysTheSameRecognisableThing(bool onMacOS, bool onWindows)
    {
        var why = TerminalTyping.WhyNot(
            Claude(termProgram: "Ghostty"), onMacOS, onWindows);

        Assert.Contains(TerminalTyping.CantTypePhrase, why, StringComparison.Ordinal);
        Assert.Contains("Ghostty", why, StringComparison.Ordinal);
    }

    [Fact]
    public void SomethingWithNoTerminalHereSaysSo()
    {
        var why = TerminalTyping.WhyNot(
            new SessionStatus { Cli = "openclaw", Source = SessionSource.OpenClaw }, onMacOS: true, onWindows: false);

        Assert.Contains("isn't a CLI session", why, StringComparison.Ordinal);
    }

    // --- CB-105: is there somewhere a message can reach a session with no route --

    // A route always wins, whatever the registry says — Route means a TUI's
    // own input line, and a session that already has one is answered there.
    [Fact]
    public void ARouteAlwaysMeansTerminalRegardlessOfTheRegistry()
    {
        Assert.Equal(
            TerminalTyping.Channel.Terminal,
            TerminalTyping.ChannelFor(Claude(tmuxPane: "%0"), TerminalTyping.Route.Tmux, registryLive: false));

        Assert.Equal(
            TerminalTyping.Channel.Terminal,
            TerminalTyping.ChannelFor(Claude(tmuxPane: "%0"), TerminalTyping.Route.Tmux, registryLive: true));
    }

    // No route, a Claude Code session, and the registry says it can be
    // reached: a `claude bg-spare` worker or an `--agent` direct child, either
    // way the case this feature adds.
    [Fact]
    public void NoRouteWithALiveRegistryEntryIsMessaging()
    {
        Assert.Equal(
            TerminalTyping.Channel.Messaging,
            TerminalTyping.ChannelFor(Claude(), TerminalTyping.Route.None, registryLive: true));
    }

    // No route and nothing in the registry either: genuinely nowhere.
    [Fact]
    public void NoRouteWithNoRegistryEntryIsNone()
    {
        Assert.Equal(
            TerminalTyping.Channel.None,
            TerminalTyping.ChannelFor(Claude(), TerminalTyping.Route.None, registryLive: false));
    }

    // A registry entry belongs to a Claude Code session — Codex and Grok have
    // no such registry, and a status file naming one is not this feature's to
    // trust.
    [Fact]
    public void NoRouteWithALiveRegistryEntryButNotClaudeCodeIsNone()
    {
        var codex = Claude();
        codex.Cli = "codex";
        codex.Source = SessionSource.Codex;

        Assert.Equal(
            TerminalTyping.Channel.None,
            TerminalTyping.ChannelFor(codex, TerminalTyping.Route.None, registryLive: true));
    }

    // Not a local CLI at all — an OpenClaw conversation has no registry to ask
    // in the first place, whatever registryLive claims.
    [Fact]
    public void NoRouteAndNotALocalCliIsNoneEvenIfToldTheRegistryIsLive()
    {
        Assert.Equal(
            TerminalTyping.Channel.None,
            TerminalTyping.ChannelFor(
                new SessionStatus { Cli = "openclaw", Source = SessionSource.OpenClaw },
                TerminalTyping.Route.None, registryLive: true));
    }

    [Fact]
    public void NoStatusAtAllIsNone()
    {
        Assert.Equal(
            TerminalTyping.Channel.None,
            TerminalTyping.ChannelFor(null, TerminalTyping.Route.None, registryLive: true));
    }
}
