using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.Tests;

// RemoteControlChatSession in **live view** — the mode where the panel shows the
// far session's own transcript rather than a reply a model wrote about it.
//
// Separate from RemoteControlChatSessionTests, which covers the messaging
// fallback, because the two modes are genuinely different behaviours sharing a
// class and mixing them would make each file's setup lie about the other's.
//
// The far Buddy here is the real RemoteMirrorServer reading real files in a temp
// directory, reached through the real RemoteMirrorClient and the real protocol.
// Only the relay is faked, and only because the real one is a live Claude Code
// session on somebody's account. Same seam as MirrorRoundTripTests, and for the
// same reason.
public class RemoteMirrorChatSessionTests : IDisposable
{
    private const string Account = ".claude-board";
    private const string Name = "job-hunter";

    private readonly string _dir;
    private readonly List<(string Name, string Text)> _typed = new();

    // Messages that went the long way round, through the relay's messaging
    // channel rather than into a terminal — the CB-43 fallback.
    //
    // Installed for every test in the class, not only the ones that exercise it.
    // The real RemoteControlSessions.SendToAsync calls EnsureStarted, which
    // starts a live Claude Code session on somebody's account, so a test that
    // reached the fallback without this would spend real money and hang; making
    // it the default means no future test can do that by accident. Cleared by
    // ResetForTests in Dispose.
    private readonly List<(string Name, string Text)> _messaged = new();

    private bool _relayAccepts = true;
    private bool _relayThrows;

    // Swallow FETCH frames, so a panel upgrades to a live view and then waits
    // for a window that never comes. That is not a contrived state: on the wire
    // this runs over, a window costs a model turn per chunk, so the interval
    // between "mirroring" and "painted" is real and was measured in minutes.
    private bool _stallFetch;

    private RemoteMirrorServer _server = null!;
    private RemoteMirrorClient _client = null!;
    private readonly List<(string SessionId, SessionStatus Status)> _sessions = new();
    private readonly List<AgentRoster.Entry> _agents = new();

    private bool _replyEnabled = true;
    private bool _canType = true;
    private bool _mangle;
    private bool _mangleInput;

    // CB-105's messaging fallback. Defaults keep every test written before it
    // seeing exactly what it saw before: _canDeliver false means
    // `_seams.CanDeliver?.Invoke(status) == true` is false, which falls
    // straight to ErrNoPane the same as it always has.
    private bool _canDeliver;
    private DeliveryResult _deliverResult = DeliveryResult.Accepted;
    private string? _deliverAgentStatus;
    private bool _deliverThrows;
    private readonly List<(string Name, string Text)> _delivered = new();

    private readonly bool _remoteWasEnabled;

    public RemoteMirrorChatSessionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cb-mirror-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        // A send checks this before doing anything, so with it off every test
        // below would assert against "remote sessions are switched off" instead
        // of the thing it is about. Put back in Dispose rather than left on: a
        // real setter that writes settings.json and leaks into every test after
        // it is exactly what bugfix/rc-tests-leak-remote-setting had to fix once
        // already.
        _remoteWasEnabled = ClaudeBuddySettings.RemoteControlEnabled;
        ClaudeBuddySettings.PeerLinkEnabled = true;

        RemoteControlSessions.SendOverrideForTests = (_, name, text) =>
        {
            if (_relayThrows) throw new InvalidOperationException("the relay died mid-send");
            if (!_relayAccepts) return Task.FromResult<string?>(null);

            _messaged.Add((name, text));
            return Task.FromResult<string?>("msg_01FAKE");
        };
    }

    public void Dispose()
    {
        ClaudeBuddySettings.RemoteControlEnabled = _remoteWasEnabled;
        RemoteControlSessions.ResetForTests();

        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // --- the point of the whole thing -----------------------------------------

    // What the panel ends up holding is the far session's own conversation,
    // parsed by the same ChatTranscript a local panel uses — not a reply
    // composed about it.
    [AvaloniaFact]
    public async Task ALiveViewShowsTheFarSessionsOwnTranscript()
    {
        Wire("what did the build say?", "it passed on both runners");

        var session = await OpenAsync();

        Assert.True(session.IsMirroring);

        var said = Turns(session);

        Assert.Equal(2, said.Count);
        Assert.Equal(ChatRole.User, said[0].Role);
        Assert.Equal("what did the build say?", said[0].Text);
        Assert.Equal(ChatRole.Assistant, said[1].Role);
        Assert.Equal("it passed on both runners", said[1].Text);
    }

    // The banner replaces the "checking…" line rather than joining it, and says
    // plainly what the panel now is — including that typing goes into somebody
    // else's terminal, which is worth saying out loud.
    [AvaloniaFact]
    public async Task UpgradingReplacesTheOpeningLineWithOneThatSaysItIsLive()
    {
        Wire("hello", "hi");

        var replaced = 0;
        var session = await OpenAsync(s => ((IRemoteChatBacklog)s).HistoryReplaced += () => replaced++);

        Assert.Equal(1, replaced);

        var banner = session.History[0];

        Assert.Equal(ChatRole.System, banner.Role);
        Assert.Contains("Live view", banner.Text);
        Assert.Contains(Name, banner.Text);
        Assert.DoesNotContain(session.History, t => t.Text.Contains("Checking whether"));
    }

    // The box has to stop saying "message this session" once messages are being
    // typed into a terminal instead.
    [AvaloniaFact]
    public async Task TheComposerSaysTypingRatherThanMessaging()
    {
        Wire("a", "b");

        var session = await OpenAsync();

        Assert.Contains("terminal", session.ComposerHint);
        Assert.Contains(Name, session.ComposerHint);
    }

    // And it has to stop saying "terminal" when there is no terminal.
    //
    // **A live view is not a typable one, and keying the hint on mirroring alone
    // conflated them.** The roster has always carried HasPane — its own comment
    // says "a panel that offered a send it cannot deliver would be lying in the
    // one place it matters" — and the hint ignored it.
    //
    // Seen on a real pair: the composer read "Type into job-hunter-mac-mini's
    // terminal" with the line directly above it reading "Sent as a message
    // rather than typed … there is no input line to type into." Both on screen
    // at once, one of them false. On a headless machine, where sessions run in a
    // plain tty rather than under tmux, this is the ordinary case.
    [AvaloniaFact]
    public async Task TheComposerSaysMessagingWhenThereIsNoPaneToTypeInto()
    {
        _canType = false;

        Wire("a", "b");

        var session = await OpenAsync();

        Assert.Contains("Message", session.ComposerHint);
        Assert.DoesNotContain("terminal", session.ComposerHint);
        Assert.Contains(Name, session.ComposerHint);
    }

    // CB-105's third arm: no pane, but this machine can hand the text to the
    // far session's own messaging socket instead — a live view that is real
    // and a composer that says something more useful than the plain
    // "Message…" a headless session got before this feature existed.
    [AvaloniaFact]
    public async Task TheComposerOffersDeliveryWhenThereIsNoPaneButThereIsASocket()
    {
        _canType = false;
        _canDeliver = true;

        Wire("a", "b");

        var session = await OpenAsync();

        Assert.Contains(Name, session.ComposerHint);
        Assert.Contains("background job", session.ComposerHint);
        Assert.DoesNotContain("terminal", session.ComposerHint);
    }

    // A live view is a real transcript, so it can be paged back into — which is
    // exactly what the messaging channel could not do.
    [AvaloniaFact]
    public async Task ALiveViewCanBePagedBackInto()
    {
        // Comfortably more than one opening window — and since CB-46 that is no
        // longer the same as more than InitialBytes. The server grows the
        // opening window while the turns still fit one chunk, so a fixture sized
        // against the byte constant now arrives whole with nothing behind it.
        // Sized against the constraint that actually binds instead.
        var rows = new List<string>();
        var bytes = 0;

        for (var i = 0; bytes < MirrorProtocol.InitialBytes * 8; i++)
        {
            var row = UserRow($"r{i}", $"message {i} " + new string('y', 300));
            rows.Add(row);
            bytes += row.Length + 1;
        }

        WireRows(rows);

        var session = await OpenAsync();
        var backlog = (IRemoteChatBacklog)session;

        Assert.True(backlog.HasMore);

        var prepended = 0;
        backlog.HistoryPrepended += n => prepended += n;

        Assert.True(await backlog.LoadOlderAsync(CancellationToken.None));
        Assert.True(prepended > 0);

        // The banner stays at the top: it describes the panel, not a moment in
        // the conversation, so older turns go in under it.
        Assert.Contains("Live view", session.History[0].Text);
    }

    // --- typing -----------------------------------------------------------------

    // The half of this bug that was about slash commands. The message reaches
    // the far session's own input line, which is the only place /color can
    // possibly work.
    [AvaloniaFact]
    public async Task AMessageIsTypedIntoTheFarTerminalRatherThanDescribedToAModel()
    {
        Wire("a", "b");

        var session = await OpenAsync();

        await session.SendAsync("/color green");

        var typed = Assert.Single(_typed);
        Assert.Equal(Name, typed.Name);
        Assert.Equal("/color green", typed.Text);

        // On screen once, after the transcript's own opening turn.
        Assert.Equal(
            new[] { "a", "/color green" },
            Turns(session).Where(t => t.Role == ChatRole.User).Select(t => t.Text));
    }

    // The far transcript will produce the message back, because it went in
    // through the terminal — that is the whole design. So the row that returns
    // adopts the bubble already on screen instead of adding a second.
    [AvaloniaFact]
    public async Task TheEchoOfATypedMessageSettlesTheTurnRatherThanDuplicatingIt()
    {
        Wire("a", "b");

        var session = await OpenAsync();

        var updated = 0;
        session.TurnUpdated += _ => updated++;

        await session.SendAsync("run the tests");

        // The far CLI reads it and writes it into its own transcript.
        Append(UserRow("echo", "run the tests"));
        await _server.TickAsync();

        // Once, not twice: the echo adopted the bubble that was already there.
        Assert.Equal(
            new[] { "a", "run the tests" },
            Turns(session).Where(t => t.Role == ChatRole.User).Select(t => t.Text));

        Assert.Equal(1, updated);
    }

    // An unrelated message that happens to arrive after a send must not be
    // swallowed by the pending turn.
    [AvaloniaFact]
    public async Task ADifferentMessageArrivingAfterASendIsNotMistakenForTheEcho()
    {
        Wire("a", "b");

        var session = await OpenAsync();
        await session.SendAsync("run the tests");

        Append(UserRow("other", "something else entirely"));
        await _server.TickAsync();

        Assert.Equal(
            new[] { "a", "run the tests", "something else entirely" },
            Turns(session).Where(t => t.Role == ChatRole.User).Select(t => t.Text));
    }

    [AvaloniaFact]
    public async Task TheFarMachinesOwnReplySettingIsWhatDecidesWhetherAnythingIsTyped()
    {
        Wire("a", "b");
        _replyEnabled = false;

        var session = await OpenAsync();
        await session.SendAsync("hello");

        Assert.Empty(_typed);

        var last = session.History[^1];
        Assert.Equal(ChatRole.System, last.Role);
        Assert.Contains("switched off", last.Text);
        Assert.Contains("over there", last.Text);
    }

    // --- CB-52: a relay waiting on a prompt nobody will answer -----------------

    // The panel is where the person most in need of this answer is sitting:
    // staring at a view that will never paint, because the relay on *their own*
    // machine is stuck on a keypress. "Close and reopen to try again" sends them
    // round the loop that produced it.
    [AvaloniaFact]
    public async Task AMirrorThatFailedBecauseTheRelayIsStuckSaysSoAndWhatToPress()
    {
        Wire("a", "b");
        _mangle = true;

        RemoteControlSessions.SetRelayForTests(
            Account, "1 remote session",
            stall: "waiting for an answer (a prompt this app does not recognise) — "
                 + "press Escape in that relay's terminal to clear it");

        var session = await OpenAsync(expectMirror: false);

        var last = session.History[^1];

        Assert.Equal(ChatRole.System, last.Role);
        Assert.Contains("press Escape", last.Text);
        Assert.DoesNotContain("try again", last.Text);
    }

    // And when nothing says the relay is stuck, the integrity wording stays
    // exactly as it was — a stall that is merely unknown must not be reported as
    // one, or every ordinary failure starts telling people to press keys.
    [AvaloniaFact]
    public async Task AMirrorThatFailedForSomeOtherReasonStillSaysWhatItAlwaysDid()
    {
        Wire("a", "b");
        _mangle = true;

        var session = await OpenAsync(expectMirror: false);

        var last = session.History[^1];

        Assert.Contains("Couldn't verify", last.Text);
        Assert.DoesNotContain("press Escape", last.Text);
    }

    // The other surface, and the one a user checks when nothing is working at
    // all. The count and the stall are both true and both survive.
    [AvaloniaFact]
    public void TheSettingsStatusLineSaysWhenARelayIsWaiting()
    {
        RemoteControlSessions.SetRelayForTests(
            Account, "3 remote sessions",
            stall: "waiting for an answer (a tool-permission prompt) — press Escape");

        var said = RemoteControlSessions.StatusText;

        Assert.Contains("3 remote sessions", said);
        Assert.Contains("press Escape", said);
    }

    // --- CB-46: upgraded, but nothing painted yet ------------------------------

    // The state the user was actually left in, and the reason his panel showed
    // nothing at all. The mirror had been agreed — the composer had already
    // switched to "type into its terminal" — but the first window had not
    // arrived, so the deltas had not started either, and OnInbound was throwing
    // his messages away on the strength of _mirroring alone. Three sources,
    // all silent, and a panel strictly worse than the messaging channel it
    // replaced.
    //
    // He sent "test"; the far session replied "Received — connectivity
    // confirmed."; it reached this machine and was discarded here.
    private async Task<RemoteControlChatSession> StalledMirrorAsync()
    {
        Wire("a", "b");
        _stallFetch = true;
        _client.TimeoutOverrideForTests = TimeSpan.FromMilliseconds(50);

        var session = NewSession();
        session.PanelOpened();

        await _client.DiscoverAsync(Peers, new[] { Name });

        Assert.True(session.IsMirroring, "the roster arrived, so the panel should have upgraded");
        Assert.DoesNotContain(Turns(session), t => t.Text == "a");

        return session;
    }

    private static BridgeProtocol.InboundMessage FromFarSession(string body) =>
        new(Name, "bridge:session_1", "prompting", body, Account);

    [AvaloniaFact]
    public async Task AMirrorThatHasNeverPaintedStillShowsWhatTheFarSessionSays()
    {
        var session = await StalledMirrorAsync();

        session.OnInbound(FromFarSession("Received — connectivity confirmed."));

        Assert.Contains(
            session.History,
            t => t.Role == ChatRole.Assistant && t.Text.Contains("connectivity confirmed"));
    }

    // The working line follows the same rule, and for the same reason: it is
    // suppressed because a live view shows the work itself, which is only true
    // once there is a live view on screen.
    [AvaloniaFact]
    public async Task AMirrorThatHasNeverPaintedStillSaysWhenTheFarSessionIsWorking()
    {
        var session = await StalledMirrorAsync();

        session.SetWorking(true);

        Assert.Contains(session.History, t => !t.IsComplete);
    }

    // The other half, unchanged and still load-bearing: once the transcript is
    // actually on screen, a peer message would be a second, differently-worded
    // account of something already shown, and showing both is the confusion this
    // whole feature exists to end.
    [AvaloniaFact]
    public async Task OnceItHasPaintedTheTranscriptIsTheOnlySource()
    {
        Wire("a", "b");

        var session = await OpenAsync();

        var before = Turns(session).Count;
        session.OnInbound(FromFarSession("Summary for you: the build passed."));

        Assert.Equal(before, Turns(session).Count);
        Assert.DoesNotContain(session.History, t => t.Text.Contains("Summary for you"));
    }

    // What the panel says while the transfer is running. It used to sit on the
    // opening "Checking whether a live view … is available" line for the whole
    // wait, which is the exact sentence that meant failure an hour earlier — the
    // user reported a working transfer as "no live view" twice on the strength
    // of it.
    [AvaloniaFact]
    public async Task ThePanelSaysItIsFetchingRatherThanStillChecking()
    {
        var session = await StalledMirrorAsync();

        Assert.Contains(session.History, t => t.Text.Contains("fetching its conversation"));
    }

    // And it does not outlive the wait: the window that ends it clears the
    // history outright, so the line goes with it rather than sitting above a
    // conversation it no longer describes.
    [AvaloniaFact]
    public async Task TheFetchingLineIsGoneOnceTheConversationArrives()
    {
        Wire("a", "b");

        var session = await OpenAsync();

        Assert.DoesNotContain(session.History, t => t.Text.Contains("fetching its conversation"));
        Assert.DoesNotContain(session.History, t => t.Text.Contains("Checking whether"));
    }

    // --- CB-43, and what became of it -------------------------------------------

    // **The capability these tested is gone, and it went deliberately with the
    // relay.** CB-43 fixed a real bug: a session running in a plain tty rather
    // than under tmux has a transcript to mirror but no input line to type
    // into, and the panel used to refuse — which made upgrading to a live view
    // strictly worse than staying on the messaging channel it had been using
    // happily. The fix was to fall back to that channel, which handed the text
    // to the far session's model as a cross-session message.
    //
    // That channel was the relay, and the relay is deleted. A direct link types
    // into the session's own input line and has nothing else to offer, so a
    // session with no pane can now be read and not written to.
    //
    // Rewritten rather than removed, because a capability that disappears
    // quietly is how a regression gets shipped. What follows asserts the new
    // behaviour *and* that the user is told plainly, which is the part that
    // keeps this from being the bug CB-43 was about.
    [AvaloniaFact]
    public async Task ASessionWithNoPaneIsRefusedAndToldWhy()
    {
        _canType = false;

        Wire("a", "b");
        var session = await OpenAsync();
        var before = session.History.Count;

        await session.SendAsync("are you there?");

        var added = session.History.Skip(before).ToList();

        // The typed message stays on screen with the explanation under it. A
        // message that vanishes silently is the failure this panel has spent
        // six tickets learning not to produce.
        Assert.Contains(added, t => t.Role == ChatRole.User && t.Text == "are you there?");
        // Says "a terminal Buddy can type into" rather than naming tmux: since
        // CB-79 that is five different mechanisms, and the far machine's own
        // reason never crosses the wire — a code is all that does.
        Assert.Contains(added, t => t.Role == ChatRole.System
            && t.Text.Contains(TerminalTyping.CantTypePhrase, StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task TheRefusalNamesTheSessionRatherThanBlamingTheNetwork()
    {
        // The fix is on the other machine, and this is the only place that will
        // ever say so.
        _canType = false;

        Wire("a", "b");
        var session = await OpenAsync();
        await session.SendAsync("hello?");

        Assert.Contains(
            session.History,
            t => t.Role == ChatRole.System && t.Text.Contains(Name));
    }

    // --- CB-105: delivering to a session with no pane at all -------------------

    // The mirrored transcript is what actually settles the pending turn — same
    // as a typed send — so this only asserts the note that appears while it is
    // still waiting for that echo, plus the fact that a delivery was actually
    // attempted rather than typed.
    [AvaloniaFact]
    public async Task SendingWithNoPaneButASocketHandsItOverAndSaysSo()
    {
        _canType = false;
        _canDeliver = true;
        _deliverAgentStatus = "working";

        Wire("a", "b");
        var session = await OpenAsync();

        await session.SendAsync("still there?");

        Assert.Equal("still there?", Assert.Single(_delivered).Text);
        Assert.Empty(_typed);

        Assert.Contains(session.History,
            t => t.Role == ChatRole.System && t.Text.Contains("Handed to") && t.Text.Contains("mid-turn"));

        // The typed message stays on screen, still pending — nothing here
        // settles it early. Once the far transcript's own next turn produces
        // the row, Echoes settles it exactly as it already does for a typed
        // send.
        Assert.Contains(session.History, t => t.Role == ChatRole.User && t.Text == "still there?");
    }

    // The mirrored transcript settling the pending turn once the far session's
    // own next turn writes the row — the same mechanism a typed send already
    // relies on, exercised here for the messaging path.
    [AvaloniaFact]
    public async Task ADeliveredMessageIsSettledByTheEchoJustLikeATypedOne()
    {
        _canType = false;
        _canDeliver = true;

        Wire("a", "b");
        var session = await OpenAsync();

        var updated = 0;
        session.TurnUpdated += _ => updated++;

        await session.SendAsync("still there?");

        Append(UserRow("u2", "still there?"));
        await _server.TickAsync();

        Assert.Equal(1, updated);
        // Adopted the turn already on screen rather than adding a second one.
        Assert.Single(Turns(session), t => t.Text == "still there?");
    }

    [AvaloniaFact]
    public async Task ASessionTheRegistryNoLongerKnowsAboutSaysTheJobMayHaveStopped()
    {
        _canType = false;
        _canDeliver = true;
        _deliverResult = DeliveryResult.NoRegistryEntry;

        Wire("a", "b");
        var session = await OpenAsync();

        await session.SendAsync("hello?");

        Assert.Contains(session.History,
            t => t.Role == ChatRole.System && t.Text.Contains("the job may have stopped"));
    }

    [AvaloniaFact]
    public async Task ASocketThatRefusesTheDeliverySaysNothingWasSent()
    {
        _canType = false;
        _canDeliver = true;
        _deliverResult = DeliveryResult.SocketRefused;

        Wire("a", "b");
        var session = await OpenAsync();

        await session.SendAsync("hello?");

        Assert.Contains(session.History,
            t => t.Role == ChatRole.System && t.Text.Contains("nothing was sent"));
    }

    // --- keeping up ---------------------------------------------------------------

    [AvaloniaFact]
    public async Task WhatTheFarSessionDoesNextArrivesWithoutBeingAskedFor()
    {
        Wire("a", "b");

        var session = await OpenAsync();
        var before = Turns(session).Count;

        Append(AssistantRow("new", "and then it deployed"));
        await _server.TickAsync();

        var said = Turns(session);

        Assert.Equal(before + 1, said.Count);
        Assert.Equal("and then it deployed", said[^1].Text);
    }

    // A live view shows the work itself, so a line claiming the session is
    // working would sit directly under the evidence that it is.
    [AvaloniaFact]
    public async Task TheWorkingNoteIsSuppressedOnceThereIsALiveView()
    {
        Wire("a", "b");

        var session = await OpenAsync();
        var before = session.History.Count;

        session.SetWorking(true);

        Assert.Equal(before, session.History.Count);
    }

    // In live view the transcript is the source of truth. A peer message would
    // be a second, differently-worded account of something already shown —
    // which is precisely the confusion this feature exists to end.
    [AvaloniaFact]
    public async Task APeerMessageIsNotAppendedBesideTheTranscriptItParaphrases()
    {
        Wire("a", "b");

        var session = await OpenAsync();
        var before = Turns(session).Count;

        session.OnInbound(new BridgeProtocol.InboundMessage(
            Name, "bridge:session_1", "prompting",
            "Summary for you: the build passed and I deployed it.", Account));

        Assert.Equal(before, Turns(session).Count);
    }

    // --- refusing what did not survive ------------------------------------------

    // The guarantee, at the panel. A mangled transfer produces an error and an
    // empty conversation — never a partial window, and never a quiet fallback to
    // the model-written version, which would substitute a summary at the exact
    // moment integrity failed.
    [AvaloniaFact]
    public async Task AMirrorThatFailsItsIntegrityCheckShowsNothingRatherThanSomethingElse()
    {
        Wire("what did the build say?", "it passed on both runners");
        _mangle = true;

        var session = await OpenAsync(expectMirror: false);

        var last = session.History[^1];

        Assert.Equal(ChatRole.System, last.Role);
        Assert.Contains("Couldn't verify", last.Text);
        Assert.Contains("rather than something altered", last.Text);

        // Nothing from the far transcript reached the screen.
        Assert.DoesNotContain(session.History, t => t.Text.Contains("it passed on both runners"));
        Assert.DoesNotContain(session.History, t => t.Text.Contains("what did the build say?"));
    }

    // --- no live view --------------------------------------------------------------

    // A bare peer — no Buddy on the other machine — keeps the messaging channel
    // and says so, including the part people need to know: that the replies are
    // written for them and may summarise.
    [AvaloniaFact]
    public async Task WithoutABuddyOverThereThePanelSaysWhyItIsNotALiveView()
    {
        WireClientOnly();

        var session = NewSession();
        session.PanelOpened();

        await _client.DiscoverAsync(
            Array.Empty<string>(), new[] { Name });

        Assert.False(session.IsMirroring);

        var last = session.History[^1];

        Assert.Contains("No live view", last.Text);
        Assert.Contains("may summarise", last.Text);
        Assert.Contains("Message", session.ComposerHint);
    }

    // The sequence a user actually hit, and the one that must be asserted rather
    // than reasoned about: the panel is OPEN and already showing "no live view",
    // and the roster arrives afterwards.
    //
    // The claim being pinned is that the stale line does not survive. It is a
    // statement about the *other machine* — "isn't running Remote Control" —
    // so a reader has no way to tell it has gone out of date, and leaving it
    // above a working live view would be its own bug. What removes it is the
    // Window delivery that follows the upgrade, which rebuilds the history from
    // the far transcript; this test exists so that stays true rather than being
    // an inference about code that could change.
    [AvaloniaFact]
    public async Task ARosterArrivingAfterThePanelGaveUpReplacesTheNoLiveViewLine()
    {
        WireClientOnly();

        var session = NewSession();
        session.PanelOpened();

        // No Buddy over there yet: the name settles as unavailable and says so.
        await _client.DiscoverAsync(
            Array.Empty<string>(), new[] { Name });

        Assert.False(session.IsMirroring);
        Assert.Contains(session.History, t => t.Text.Contains("No live view"));

        // Now the far Buddy shows up and can answer for the session. Wired by
        // hand rather than through WireRows, which would build a second client
        // and throw away the state this test is about.
        _path = Path.Combine(_dir, "session.jsonl");
        File.WriteAllText(_path, UserRow("u1", "what did the build say?") + "\n"
                               + AssistantRow("a1", "it passed on both runners") + "\n");

        var sessionId = Guid.NewGuid().ToString();
        _agents.Add(new AgentRoster.Entry(Name, sessionId, 4242));
        _sessions.Add((sessionId, new SessionStatus
        {
            Title = Name,
            Cwd = _dir,
            Source = SessionSource.ClaudeCode,
            TranscriptPath = _path,
            TmuxPane = "%1",
            SessionPid = 4242
        }));

        await _client.DiscoverAsync(Peers, new[] { Name });

        Assert.True(session.IsMirroring, "the panel should upgrade when the roster finally arrives");

        // The stale line is gone, not merely outvoted by a newer one.
        Assert.DoesNotContain(session.History, t => t.Text.Contains("No live view"));
        Assert.Contains(session.History, t => t.Text.Contains("Live view"));

        // And the far session's real conversation is what is on screen.
        Assert.Equal(
            new[] { "what did the build say?", "it passed on both runners" },
            Turns(session).Select(t => t.Text));
    }

    [AvaloniaFact]
    public async Task ThePanelOnlySaysItOnce()
    {
        WireClientOnly();

        var session = NewSession();
        session.PanelOpened();

        // Nobody to ask, twice. Over the relay this was a peer list with the
        // session but no Buddy on it; a link says the same thing by being
        // connected to no machines.
        var peers = Array.Empty<string>();

        await _client.DiscoverAsync(peers, new[] { Name });
        await _client.DiscoverAsync(peers, new[] { Name });

        Assert.Single(session.History.Where(t => t.Text.Contains("No live view")));
    }

    // --- the rest of what a panel can be told --------------------------------------

    // Every arm of the error switch, because each is a different thing to do
    // about it and a generic "couldn't send" would tell nobody which.
    [AvaloniaFact]
    public async Task ASessionTheFarBuddyHasForgottenSaysSo()
    {
        Wire("a", "b");
        var session = await OpenAsync();

        _agents.Clear();
        _sessions.Clear();

        await session.SendAsync("hello");

        Assert.Contains("no longer has a session", session.History[^1].Text);
    }

    [AvaloniaFact]
    public async Task AMessageThatDidNotSurviveTheTripIsNotTypedAndSaysSo()
    {
        Wire("a", "b");
        var session = await OpenAsync();

        // A courier that alters the message on its way to the terminal. Refused
        // rather than typed in a form the person did not write.
        _mangleInput = true;

        await session.SendAsync("hello");

        Assert.Empty(_typed);
        Assert.Contains("didn't survive the trip", session.History[^1].Text);
    }

    // The relay going away mid-conversation is invisible from the panel —
    // nothing on screen changes — so it is said out loud.
    [AvaloniaFact]
    public void ARelayStoppingIsSaidOutLoud()
    {
        var session = NewSession();

        session.OnBridgeStopped("idle");

        Assert.Contains("relay session stopped (idle)", session.History[^1].Text);
    }

    // Cancel is a no-op in both modes and must stay a quiet one: a Cancel that
    // looked like it worked and did nothing would be worse than none.
    [AvaloniaFact]
    public async Task CancellingDoesNothingAndSaysNothing()
    {
        Wire("a", "b");
        var session = await OpenAsync();
        var before = session.History.Count;

        session.Cancel();

        Assert.Equal(before, session.History.Count);
    }

    // Closing keeps the conversation and drops only the subscription, so a panel
    // reopened later still has what it had.
    [AvaloniaFact]
    public async Task ClosingThePanelKeepsTheConversationAndDropsTheSubscription()
    {
        Wire("a", "b");
        var session = await OpenAsync();

        var before = session.History.Count;
        session.PanelClosed();

        Assert.Equal(before, session.History.Count);
        Assert.False(_server.Busy);

        session.PanelOpened();
        Assert.True(_server.Busy);
    }

    [AvaloniaFact]
    public void ClosingAPanelThatNeverBecameALiveViewIsHarmless()
    {
        var session = NewSession();
        var before = session.History.Count;

        session.PanelClosed();

        Assert.Equal(before, session.History.Count);
    }

    // Disposing is not part of the app's normal path — these sessions outlive
    // every panel that shows them — but it exists, and it must not throw or take
    // the conversation with it.
    [AvaloniaFact]
    public async Task DisposingUnsubscribesWithoutLosingTheConversation()
    {
        Wire("a", "b");
        var session = await OpenAsync();
        var before = session.History.Count;

        session.Dispose();
        session.Dispose();

        Assert.Equal(before, session.History.Count);
    }

    // Raised once per change, not per set, so a poll that re-asserts the same
    // state does not redraw anything.
    [AvaloniaFact]
    public void StateOnlyChangesWhenItChanges()
    {
        var session = NewSession();
        var changes = 0;
        session.StateChanged += _ => changes++;

        session.SetState(RemoteChatState.Connected);
        Assert.Equal(0, changes);

        session.SetState(RemoteChatState.Error);
        session.SetState(RemoteChatState.Error);

        Assert.Equal(1, changes);
        Assert.Equal(RemoteChatState.Error, session.State);
    }

    // A page that parsed to nothing but moved the offset is not the end — the
    // window can be entirely tool results — so paging must not stop at the first
    // quiet stretch.
    [AvaloniaFact]
    public async Task PagingPastAQuietStretchKeepsGoing()
    {
        var rows = new List<string>();
        var bytes = 0;

        for (var i = 0; bytes < MirrorProtocol.InitialBytes + 200_000; i++)
        {
            var row = i % 8 == 0 ? UserRow($"u{i}", $"said {i}") : Snapshot(i);

            rows.Add(row);
            bytes += row.Length + 1;
        }

        WireRows(rows);

        var session = await OpenAsync();
        var backlog = (IRemoteChatBacklog)session;

        var pages = 0;
        while (backlog.HasMore && pages < 20)
        {
            await backlog.LoadOlderAsync(CancellationToken.None);
            pages++;
        }

        Assert.False(backlog.HasMore);
    }

    private static string Snapshot(int i) =>
        "{\"type\":\"file-history-snapshot\",\"uuid\":\"h" + i + "\",\"blob\":\""
        + new string('x', 400) + "\"}";

    // --- through the panel itself ----------------------------------------------------

    // The panel is a singleton that outlives every session it shows, so what it
    // says about *where a message goes* has to follow the session it is bound
    // to — and follow it when that session changes underneath.
    [AvaloniaFact]
    public async Task ThePanelsInputBoxFollowsThePanelItHasBecome()
    {
        Wire("a", "b");

        var session = NewSession();
        var orb = new OrbWindow(Guid.NewGuid().ToString());

        // Bound while it is still a messaging channel.
        ChatPanel.OpenFor(orb, session);

        var input = ChatPanelTestAccess.Instance!.FindControl<TextBox>("Input")!;
        Assert.Contains("Message", input.Watermark!);

        // ...and upgraded underneath it. Read once at bind, the box would go on
        // describing the panel it used to be.
        await _client.DiscoverAsync(Peers, new[] { Name });
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("terminal", input.Watermark!);

        ChatPanel.HideFor(session.SessionId);
    }

    // Binding away from a live view has to stop the far side serving it: a relay
    // kept awake by a panel nobody is looking at is somebody's quota. Rebinding
    // to it starts again.
    [AvaloniaFact]
    public async Task BindingAwayFromALiveViewStopsItAndComingBackResumesIt()
    {
        Wire("a", "b");

        var session = await OpenAsync();
        var orb = new OrbWindow(Guid.NewGuid().ToString());

        ChatPanel.OpenFor(orb, session);
        Assert.True(_server.Busy);

        // A different session takes the panel, which unbinds the first.
        var other = new FakeChatSession { SessionId = "other-" + Guid.NewGuid(), DisplayName = "Other" };
        ChatPanel.OpenFor(new OrbWindow(Guid.NewGuid().ToString()), other);

        Assert.False(_server.Busy);

        ChatPanel.OpenFor(orb, session);
        Assert.True(_server.Busy);

        ChatPanel.HideFor(session.SessionId);
        ChatPanel.HideFor(other.SessionId);
    }

    // --- the frame door ------------------------------------------------------------

    // The one guarantee that protects a person from the plumbing: a mirror frame
    // is swallowed before it can reach a chat bubble, whether or not it parses.
    // A screenful of base64 in somebody's conversation is the failure this
    // prevents, and it is one line of code away at all times.
    //
    // Here rather than in IntegrationTests because a message that is *not*
    // swallowed goes out through the dispatcher, so proving the difference needs
    // one running.
    [AvaloniaFact]
    public void AFrameNeverReachesAChatPanelButARealMessageStillDoes()
    {
        var delivered = new List<BridgeProtocol.InboundMessage>();
        void Collect(BridgeProtocol.InboundMessage m) => delivered.Add(m);

        RemoteControlSessions.MessageReceived += Collect;

        try
        {
            foreach (var body in new[]
            {
                MirrorProtocol.BuildFrame(MirrorProtocol.Ok, "abcd1234"),
                MirrorProtocol.BuildFrame(MirrorProtocol.Chunk, "abcd1234",
                    new Dictionary<string, string> { ["seq"] = "0", ["of"] = "1" },
                    System.Text.Encoding.UTF8.GetBytes("payload")),
                "CB-MIRROR:this one does not even parse",
                BridgeProtocol.InfoMarker + " color=green; commands=none"
            })
            {
                RemoteControlSessions.OnMessage(Account,
                    new BridgeProtocol.InboundMessage(FarRelay, "bridge:x", "prompting", body));
            }

            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Empty(delivered);

            // ...and something a person actually said still comes through, so
            // this is a filter rather than a wall.
            RemoteControlSessions.OnMessage(Account,
                new BridgeProtocol.InboundMessage(Name, "bridge:x", "prompting", "the build passed"));

            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.Equal("the build passed", Assert.Single(delivered).Body);
        }
        finally
        {
            RemoteControlSessions.MessageReceived -= Collect;
        }
    }

    // --- wiring ----------------------------------------------------------------------

    // --- a live view whose relay has gone -----------------------------------

    // Typing into a live view goes through the mirror client for the account,
    // and there is a window where the panel is mirroring and the client has been
    // torn down — an idle shutdown, or the relay being restarted under it. The
    // message has to come back with something a person can act on rather than
    // disappearing.
    [AvaloniaFact]
    public async Task TypingWithNoClientLeftSaysTheRelayIsNotRunning()
    {
        Wire("what is left?", "one thing");
        var session = await OpenAsync();

        RemoteControlSessions.UseMirrorClientForTests(Account, null);

        await session.SendAsync("still there?");
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(session.History,
            t => t.Role == ChatRole.System && t.Text.Contains("relay session isn't running"));
    }

    // And the typed message stays on screen above that note rather than being
    // discarded, which is the same contract the messaging mode has.
    [AvaloniaFact]
    public async Task AMessageThatCouldNotBeTypedIsStillOnScreen()
    {
        Wire("what is left?", "one thing");
        var session = await OpenAsync();

        RemoteControlSessions.UseMirrorClientForTests(Account, null);

        await session.SendAsync("still there?");
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(session.History,
            t => t.Role == ChatRole.User && t.Text == "still there?");
    }

    // --- scrolling back over a stretch nobody sees ---------------------------

    // A page that parsed to nothing but moved the offset is not the end of the
    // conversation. A window can be entirely file-history snapshots — on a real
    // transcript most of the bytes are — and treating "no turns in this page" as
    // "no more conversation" would stop the scroll dead in the middle of one.
    //
    // Same rule LocalCliChatSession follows for a local session: the answer is
    // whether there is more to *ask for*, not whether this page had anything in
    // it.
    [AvaloniaFact]
    public async Task APageOfNothingButSnapshotsIsNotTheTopOfTheConversation()
    {
        // A long stretch of rows no panel shows, with a page back landing
        // entirely inside it.
        //
        // **Three layers, and the middle one is the subject.** The opening
        // window grows until it has enough conversation to fill a panel, and a
        // stretch of snapshots yields no turns at all — so it grows straight
        // past them, which is exactly the point of growing.
        //
        // This used to end with a single turn, which was enough while growth
        // stopped at what fit one chunk. Growth stops at enough *turns* now, so
        // one turn at the end sends it hunting all the way through the
        // snapshots and into the conversation behind them, swallowing the whole
        // file and leaving nothing to page into.
        //
        // A real run of recent conversation stops the growth where a real
        // transcript would, which puts the snapshots back in the middle — the
        // only place they can be for this test to mean anything.
        var rows = new List<string>();

        for (var i = 0; i < 3000; i++)
            rows.Add(UserRow($"u{i}", $"the first thing said {i} " + new string('q', 200)));

        for (var i = 0; i < 4000; i++)
            rows.Add("{\"type\":\"file-history-snapshot\",\"uuid\":\"h" + i + "\",\"blob\":\""
                     + new string('z', 600) + "\"}");

        for (var i = 0; i < 60; i++)
            rows.Add(AssistantRow($"a{i}", $"the last thing said {i}"));

        WireRows(rows);
        var session = await OpenAsync();

        // The first page back is all snapshots, so it yields no turns — and the
        // session must still report that there is more behind it.
        var more = await session.LoadOlderAsync(default);

        Assert.True(more, "a page of snapshots must not read as the top of the conversation");
    }

    private static RemoteControlChatSession NewSession() =>
        new($"rc:{Account}:{Name}", Account, Name);

    // Everything the far session said, with the panel's own banner left out.
    private static IReadOnlyList<ChatTurn> Turns(RemoteControlChatSession session) =>
        session.History.Where(t => t.Role != ChatRole.System).ToList();

    private async Task<RemoteControlChatSession> OpenAsync(
        Action<RemoteControlChatSession>? before = null, bool expectMirror = true)
    {
        var session = NewSession();
        before?.Invoke(session);

        session.PanelOpened();

        // Discovering is all it takes: the roster landing raises MirrorChanged,
        // the session upgrades itself and reads the tail. Opening the feed by
        // hand here would be a second window and would hide whether the session
        // does that for itself.
        await _client.DiscoverAsync(Peers, new[] { Name });

        if (expectMirror) Assert.True(session.IsMirroring, "the panel should have upgraded to a live view");

        return session;
    }

    // The machines to ask, by name.
    //
    // **This used to be a list of BridgeProtocol.RemoteAgent that the client
    // filtered down to relays** — IsOwnRelay, IsOffline, IsRemoteControl — which
    // is why the second entry below was the session itself and was expected to
    // be dropped. A direct link has no such list and needs no such filter: it
    // knows the machines it is connected to, which is the answer that filtering
    // was working towards.
    private static IReadOnlyList<string> Peers => new[] { FarRelay };
    private const string FarRelay = "claude-buddy-rc--claude-mini";
    private const string NearRelay = "claude-buddy-rc--claude-laptop";

    private string _path = "";

    private void Wire(string question, string answer) =>
        WireRows(new List<string> { UserRow("u1", question), AssistantRow("a1", answer) });

    private void WireRows(List<string> rows)
    {
        _path = Path.Combine(_dir, "session.jsonl");
        File.WriteAllText(_path, string.Join("\n", rows) + "\n");

        var sessionId = Guid.NewGuid().ToString();
        _agents.Add(new AgentRoster.Entry(Name, sessionId, 4242));
        _sessions.Add((sessionId, new SessionStatus
        {
            Title = Name,
            Cwd = _dir,
            Source = SessionSource.ClaudeCode,
            TranscriptPath = _path,
            TmuxPane = "%1",
            SessionPid = 4242
        }));

        BuildServer();
        BuildClient();
    }

    // No far Buddy at all: a client with nothing on the other end of it.
    private void WireClientOnly()
    {
        BuildServer();
        BuildClient();
    }

    private void Append(string row) => File.AppendAllText(_path, row + "\n");

    private void BuildServer() =>
        _server = new RemoteMirrorServer(Account, new RemoteMirrorServer.Seams(
            SendToClientAsync,
            () => _sessions,
            () => _agents,
            _ => _replyEnabled,
            _ => _canType,
            (status, text) =>
            {
                _typed.Add((status.Title, text));
                return Task.FromResult(true);
            },
            CanDeliver: status => _canDeliver,
            Deliver: async (status, text) =>
            {
                if (_deliverThrows) throw new InvalidOperationException("messaging socket went away");

                _delivered.Add((status.Title, text));
                return new DeliveryReceipt(_deliverResult, _deliverAgentStatus);
            },
            // Who may ask, which the server no longer guesses. It used to fall
            // back to a name test — anything called `claude-buddy-rc-…` was
            // taken for another Buddy's relay — and a name is not a credential.
            // It refuses by default now; the real transport answers properly,
            // because a peer has completed a TLS handshake with a certificate
            // somebody pinned by typing a code. A harness says yes explicitly.
            PeerAllowed: _ => true));

    private void BuildClient()
    {
        _client = new RemoteMirrorClient(Account, new RemoteMirrorClient.Seams(SendToServerAsync));
        RemoteControlSessions.UseMirrorClientForTests(Account, _client);
    }

    private async Task<bool> SendToServerAsync(string peer, string line)
    {
        var frame = MirrorProtocol.TryParseFrame(line);
        if (frame is null) return false;

        if (_stallFetch && frame.Type == MirrorProtocol.Fetch) return true;

        // A courier that alters a message on its way to somebody's terminal.
        if (_mangleInput && frame.Type == MirrorProtocol.Input) frame = Mangle(line) ?? frame;

        await _server.HandleAsync(NearRelay, frame);
        return true;
    }

    private async Task<bool> SendToClientAsync(string peer, string line)
    {
        var frame = MirrorProtocol.TryParseFrame(line);
        if (frame is null) return false;

        // Only the transcript windows, not the roster: a handshake that failed
        // would leave the panel in messaging mode and never reach the code this
        // test is about. A window carries wfrom; a roster does not.
        if (_mangle && frame.Type == MirrorProtocol.Chunk && frame.Get("wfrom") is not null)
            frame = Mangle(line) ?? frame;

        await _client.OnFrameAsync(FarRelay, frame);
        return true;
    }

    // A courier that quietly rewords what it was asked to relay.
    private static MirrorProtocol.MirrorFrame? Mangle(string line)
    {
        var start = line.IndexOf(";p=", StringComparison.Ordinal);
        var end = line.IndexOf(";h=", StringComparison.Ordinal);
        if (start < 0 || end < 0) return null;

        var swapped = Convert.ToBase64String(Encoding.UTF8.GetBytes("a tidier version of that"));
        return MirrorProtocol.TryParseFrame(line[..(start + 3)] + swapped + line[end..]);
    }

    private static string UserRow(string uuid, string text) =>
        $"{{\"type\":\"user\",\"uuid\":\"{uuid}\",\"message\":{{\"role\":\"user\",\"content\":\"{text}\"}}}}";

    // The same row with its content properly encoded.
    //
    // UserRow interpolates raw, which is fine for the one-word bodies every
    // other case uses and silently wrong for anything carrying a quote or a
    // newline: the row becomes invalid JSON split across several lines, the
    // parser skips it, and a test asserting that something did NOT appear twice
    // then passes because nothing appeared at all. That is exactly how the
    // cross-session echo case below first passed while covering none of the
    // code it was written for.
    private static string UserRowEncoded(string uuid, string text) =>
        "{\"type\":\"user\",\"uuid\":\"" + uuid + "\",\"message\":{\"role\":\"user\",\"content\":"
        + System.Text.Json.JsonSerializer.Serialize(text) + "}}";

    private static string AssistantRow(string uuid, string text) =>
        $"{{\"type\":\"assistant\",\"uuid\":\"{uuid}\",\"message\":{{\"role\":\"assistant\",\"content\":[{{\"type\":\"text\",\"text\":\"{text}\"}}]}}}}";
}
