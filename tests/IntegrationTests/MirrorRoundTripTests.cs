using System.Text;
using Xunit;

namespace ClaudeBuddy.Tests;

// Drives a whole mirror — client, protocol and server — over real transcript
// files on disk, with the relay replaced by a delegate that hands one side's
// frames straight to the other.
//
// The seam being cut is deliberately narrow. Everything below is the real
// RemoteMirrorClient talking to the real RemoteMirrorServer through the real
// MirrorProtocol, reading real FileStreams; the only thing faked is the pair of
// `SendFrame` delegates, which in production paste a line into a tmux pane and
// wait for a model to relay it. That is the one part a test cannot have —
// it needs two machines, a live Claude Code session on each, and somebody's
// quota — and it is also the part that carries no logic. Everything it *would*
// have carried is asserted here.
//
// What these prove that the unit tests cannot: that the bytes coming out the far
// end are the same bytes that were on the far disk. A hash agreeing with itself
// is easy; a transcript arriving intact through chunking, gzip, base64, framing,
// reassembly and window alignment is the actual claim this feature makes.
public class MirrorRoundTripTests : IDisposable
{
    private readonly string _dir;

    public MirrorRoundTripTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cb-mirror-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // --- the claim ------------------------------------------------------------

    // The whole feature in one test: what the panel is handed is exactly the
    // rows on the other machine's disk, byte for byte.
    [Fact]
    public async Task ATailArrivesAsTheSameBytesThatAreOnTheFarDisk()
    {
        var rows = Conversation(60);
        var path = WriteTranscript("session.jsonl", rows);

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", path);

        Assert.True(await harness.HandshakeAsync("job-hunter"));
        Assert.True(await harness.Client.OpenAsync("job-hunter"));

        var delivered = Assert.Single(harness.Windows);

        // Not "looks right" — identical to what this machine's own parser
        // produces from those same bytes. The far Buddy runs the same
        // ChatTranscript, so equality here is the whole verbatim claim: the
        // panel is handed exactly the turns a local panel would have built.
        Assert.Equal(
            MirrorProtocol.TurnsFrom(rows, MirrorProtocol.CliClaudeCode),
            delivered.Turns);
    }

    // A conversation big enough to need many frames, which is where chunking,
    // ordering and the whole-payload hash all have to hold at once.
    [Fact]
    public async Task ALongTranscriptArrivesWholeAndUnbroken()
    {
        // Large enough that the opening window cannot cover the file, so what
        // is asserted below — an exact, unbroken suffix — is a real claim about
        // where the window starts rather than about a small fixture.
        var rows = Conversation(120_000);
        var path = WriteTranscript("long.jsonl", rows);

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", path);

        await harness.HandshakeAsync("job-hunter");

        // Counted from after the handshake, because the roster is a transfer
        // too — it goes through the same SendTransferAsync as a window does,
        // which is why a total would say two and mean one.
        var beforeOpen = harness.ChunkFrames;

        Assert.True(await harness.Client.OpenAsync("job-hunter"));

        var delivered = Assert.Single(harness.Windows);
        var all = MirrorProtocol.TurnsFrom(rows, MirrorProtocol.CliClaudeCode);

        // **This used to assert `> 1`, and the transcript really did arrive in
        // dozens of frames.** Each was ~8KB of base64 a far model retyped as
        // tool input at roughly two minutes a turn, which is why chunking,
        // per-chunk hashes and resends existed at all.
        //
        // The wire carries a message whole now, so the claim worth making is
        // the opposite one — and the half that mattered is unchanged and
        // asserted below: what arrives is an exact, unbroken suffix of the
        // file, with no row dropped, duplicated or reworded.
        Assert.Equal(1, harness.ChunkFrames - beforeOpen);

        // A tail, so the end of the file rather than all of it — the same
        // 512KB window a local panel opens on. What matters is that it is an
        // exact, unbroken suffix: no row dropped at a frame boundary, none
        // duplicated, none reworded.
        Assert.NotEmpty(delivered.Turns);
        Assert.True(delivered.Turns.Count < all.Count, "this file should be bigger than one window");
        Assert.Equal(all.Skip(all.Count - delivered.Turns.Count).ToList(), delivered.Turns);
    }

    // The bulk of a transcript is tool results and file-history snapshots that
    // no panel ever shows. Sending them would be paying a model to paste
    // megabytes into the void, so they are dropped on the far side — and the
    // rows that *are* shown must be untouched by that.
    [Fact]
    public async Task TheRowsNobodySeesNeverCrossTheWire()
    {
        var rows = new List<string>
        {
            Row("user", "u1", "a question"),
            "{\"type\":\"file-history-snapshot\",\"uuid\":\"h1\",\"blob\":\"" + new string('x', 200_000) + "\"}",
            Row("assistant", "a1", "an answer")
        };

        var path = WriteTranscript("noisy.jsonl", rows);

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", path);

        await harness.HandshakeAsync("job-hunter");
        await harness.Client.OpenAsync("job-hunter");

        var delivered = Assert.Single(harness.Windows);

        Assert.Equal(2, delivered.Turns.Count);
        Assert.DoesNotContain(delivered.Turns, t => t.Text.Contains("file-history-snapshot"));
        Assert.Contains("a question", delivered.Turns[0].Text);
        Assert.Contains("an answer", delivered.Turns[1].Text);

        // And the snapshot's 200KB did not become frames.
        Assert.True(harness.ChunkFrames <= 2, $"the big row was relayed anyway ({harness.ChunkFrames} frames)");
    }

    // --- serving with no dispatcher (CB-39) -------------------------------------

    // The regression itself: a machine whose screen never unlocks has no
    // dispatcher, so the two DispatcherTimers that drive a relay never run, and
    // for two hours the relay answered nothing while looking perfectly alive
    // from every other machine.
    //
    // This suite has no Avalonia lifetime in it at all, which is what makes it
    // the right place to assert the fix: if ServeOneAsync needed the UI thread
    // for anything, there is nothing here to give it one.
    [Fact]
    public async Task AServeTickDeliversAnUpdateWithNoDispatcherAnywhere()
    {
        var path = WriteTranscript("headless.jsonl", Conversation(6));

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", path);

        await harness.HandshakeAsync("job-hunter");
        await harness.Client.OpenAsync("job-hunter");

        Assert.Single(harness.Windows);

        File.AppendAllText(path, Row("assistant", "later", "said while the screen was locked") + "\n");

        // Exactly what the pump's timer calls, and the only thing standing
        // between an unattended machine and serving nothing.
        await RemoteControlSessions.ServeOneAsync(
            harness.Server, harness.Client);

        var delta = Assert.Single(harness.Deltas);
        Assert.Contains("said while the screen was locked", Assert.Single(delta.Turns).Text);
    }

    // The handover window, from the side that has to stand down (CB-28).
    //
    // For one round at the moment the dispatcher arrives, the serve pump and the
    // mirror DispatcherTimer both exist: EnsureTimer disposes the pump before it
    // creates the timers, but disposing a timer does not reach inside a round
    // already running. Two rounds on one relay would both read from the same
    // transcript offset and route the same lines twice, so a message could reach
    // a panel twice. The shared gate is what stops it, and this is where "the
    // caller actually asks" can be proved rather than read.
    [Fact]
    public async Task AServeTickStandsDownWhileTheOtherPumpHoldsTheGate()
    {
        var path = WriteTranscript("handover.jsonl", Conversation(6));

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", path);

        await harness.HandshakeAsync("job-hunter");
        await harness.Client.OpenAsync("job-hunter");
        Assert.Single(harness.Windows);

        File.AppendAllText(path, Row("assistant", "later", "said mid-handover") + "\n");

        Assert.True(RemoteControlSessions.PumpGate.TryEnter());
        try
        {
            // The mirror timer's round is in flight. This round is declined
            // rather than queued or run late — the pump's own timer is what
            // brings it back.
            Assert.False(await RemoteControlSessions.ServeOneAsync(
                harness.Server, harness.Client));

            Assert.Empty(harness.Deltas);
        }
        finally
        {
            RemoteControlSessions.PumpGate.Exit();
        }

        // ...and nothing was lost by standing down: the next round delivers the
        // rows the declined one would have.
        Assert.True(await RemoteControlSessions.ServeOneAsync(
            harness.Server, harness.Client));

        var delta = Assert.Single(harness.Deltas);
        Assert.Contains("said mid-handover", Assert.Single(delta.Turns).Text);
    }

    // A gate that is not released is worse than no gate: it stops the machine
    // serving permanently, silently, on the one machine nobody is watching. So
    // the `finally` is asserted through the failure that would exercise it.
    [Fact]
    public async Task AServeTickLeavesTheGateFreeEvenWhenAHalfThrows()
    {
        var path = WriteTranscript("throwing-gate.jsonl", Conversation(4));

        var harness = new Harness(_dir) { AgentsThrow = true };
        harness.AddSession("job-hunter", path);

        Assert.True(await RemoteControlSessions.ServeOneAsync(
            harness.Server, harness.Client));

        Assert.False(RemoteControlSessions.PumpGate.Busy);

        // And the next round can still get in, which is the thing that actually
        // matters about the previous line.
        Assert.True(await RemoteControlSessions.ServeOneAsync(
            harness.Server, harness.Client));
    }

    // A relay with neither half built yet — the window between the bridge
    // starting and StartAsync putting a server and client on it. Nothing to
    // tick, and specifically not a null dereference in the loop that is meant to
    // be keeping the machine alive.
    [Fact]
    public async Task AServeTickOverARelayWithNoMirrorHalvesDoesNothing()
    {
        await RemoteControlSessions.ServeOneAsync(null, null);
    }

    // One half throwing must cost that half's round and nothing else. The
    // client is still ticked afterwards, and the next tick still delivers —
    // because on a serving machine there is nobody to notice a loop that quietly
    // stopped.
    [Fact]
    public async Task AServeTickSurvivesAMirrorHalfThatThrows()
    {
        var path = WriteTranscript("throwing.jsonl", Conversation(4));

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", path);

        await harness.HandshakeAsync("job-hunter");
        await harness.Client.OpenAsync("job-hunter");

        File.AppendAllText(path, Row("assistant", "later", "arrived during the outage") + "\n");

        harness.AgentsThrow = true;
        await RemoteControlSessions.ServeOneAsync(
            harness.Server, harness.Client);

        Assert.Empty(harness.Deltas);

        harness.AgentsThrow = false;
        await RemoteControlSessions.ServeOneAsync(
            harness.Server, harness.Client);

        var delta = Assert.Single(harness.Deltas);
        Assert.Contains("arrived during the outage", Assert.Single(delta.Turns).Text);
    }

    // --- keeping up ------------------------------------------------------------

    [Fact]
    public async Task WhatIsAppendedAfterwardsArrivesAsAnUpdate()
    {
        var rows = Conversation(10);
        var path = WriteTranscript("live.jsonl", rows);

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", path);

        await harness.HandshakeAsync("job-hunter");
        await harness.Client.OpenAsync("job-hunter");

        Assert.Single(harness.Windows);

        File.AppendAllText(path, Row("assistant", "later", "said after the panel opened") + "\n");
        await harness.Server.TickAsync();

        var delta = Assert.Single(harness.Deltas);
        Assert.Contains("said after the panel opened", Assert.Single(delta.Turns).Text);
    }

    // An update that is nothing but tool results moves the offset without
    // sending anything, which is silence rather than a gap — and specifically
    // must not deliver an empty update that the panel would render as a blank
    // turn.
    [Fact]
    public async Task AnUpdateWithNothingWorthShowingSaysNothing()
    {
        var path = WriteTranscript("quiet.jsonl", Conversation(4));

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", path);

        await harness.HandshakeAsync("job-hunter");
        await harness.Client.OpenAsync("job-hunter");

        File.AppendAllText(path, "{\"type\":\"file-history-snapshot\",\"uuid\":\"h9\"}\n");
        await harness.Server.TickAsync();

        Assert.Empty(harness.Deltas);
    }

    // /clear starts a new transcript. A client holding a byte offset into the
    // old one would be asking for a position that now means something else, so
    // the generation counter tells it to throw away what it has.
    [Fact]
    public async Task ATranscriptReplacedUnderneathBumpsItsGenerationAndReAnchors()
    {
        var path = WriteTranscript("cleared.jsonl", Conversation(40));

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", path);

        await harness.HandshakeAsync("job-hunter");
        await harness.Client.OpenAsync("job-hunter");

        var first = Assert.Single(harness.Windows);

        // Shorter than where the watch was reading: the /clear case.
        File.WriteAllText(path, Row("user", "fresh", "starting over") + "\n");
        await harness.Server.TickAsync();

        // The client re-anchors by itself: a delta whose generation does not
        // match what the feed is holding means the file underneath changed, so
        // it re-reads the tail rather than appending onto a file that no longer
        // exists. Nothing here asks it to.
        Assert.Equal(2, harness.Windows.Count);

        var second = harness.Windows[^1];
        Assert.Contains("starting over", Assert.Single(second.Turns).Text);
        Assert.NotEqual(first.Gen, second.Gen);
    }

    // --- CB-46: a first paint that can actually arrive --------------------------

    // The headline claim, and the one the user's empty panel came down to.
    //
    // Every chunk is a model emitting ~8KB of base64 as tool input, so
    // throughput is one chunk per model turn — measured at close to two minutes
    // on a real relay. A 512KB opening window came out as two chunks, which is a
    // four-minute first paint against a three-minute request timeout: the reply
    // could not exist before the request expired, and the panel showed nothing
    // for hours.
    //
    // So an opening window is one chunk, and it is asserted on a transcript far
    // bigger than one — because a rule that only holds for small files is not
    // the rule that was needed.
    [Fact]
    public async Task ALargeTranscriptOpensInASingleChunk()
    {
        // Bigger than one opening window, so there is a backlog to page.
        //
        // Briefly sized to eight megabytes while the window grew until it
        // stopped fitting a chunk — which, once a chunk became the whole 32MB
        // message, meant growing to the cap every time. The window stops at
        // enough conversation now rather than at what fits, so a megabyte is
        // once again comfortably more than one window.
        var path = WriteTranscript("huge.jsonl", Rows(MirrorProtocol.InitialBytes * 8));

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", path);

        await harness.HandshakeAsync("job-hunter");
        harness.ForgetFramesSoFar();

        Assert.True(await harness.Client.OpenAsync("job-hunter"));

        Assert.Equal(1, harness.ChunkFrames);

        // And it is a real conversation, not an empty window bought by cutting
        // until nothing was left — which would satisfy the frame count while
        // reproducing the bug.
        Assert.NotEmpty(Assert.Single(harness.Windows).Turns);
    }

    // The newest end, specifically. Someone opening a panel is looking for what
    // just happened, so what survives the shrink is the tail rather than an
    // arbitrary slice.
    [Fact]
    public async Task WhatSurvivesIsTheNewestPartOfTheConversation()
    {
        // Bigger than one opening window, so there is a backlog to page.
        //
        // Briefly sized to eight megabytes while the window grew until it
        // stopped fitting a chunk — which, once a chunk became the whole 32MB
        // message, meant growing to the cap every time. The window stops at
        // enough conversation now rather than at what fits, so a megabyte is
        // once again comfortably more than one window.
        var rows = Rows(MirrorProtocol.InitialBytes * 8);
        rows.Add(Row("assistant", "last", "the most recent thing said"));

        var path = WriteTranscript("newest.jsonl", rows);

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", path);

        await harness.HandshakeAsync("job-hunter");
        Assert.True(await harness.Client.OpenAsync("job-hunter"));

        var window = Assert.Single(harness.Windows);

        Assert.Contains("the most recent thing said", window.Turns[^1].Text);
    }

    // Growing is what makes the noise case work, and the noise case is most of a
    // real transcript. A stretch of rows no panel shows contributes no turns and
    // therefore no payload, so a window can cover it for free — where a fixed
    // byte window that landed inside one would paint a single message and call
    // it the conversation.
    [Fact]
    public async Task AStretchOfRowsNobodySeesDoesNotCostTheConversation()
    {
        var rows = new List<string> { Row("user", "u1", "the question") };

        // Comfortably more than the old fixed window, all of it invisible.
        for (var i = 0; i < 40; i++)
            rows.Add("{\"type\":\"file-history-snapshot\",\"uuid\":\"h" + i + "\",\"blob\":\""
                     + new string('x', 20_000) + "\"}");

        rows.Add(Row("assistant", "a1", "the answer"));

        var path = WriteTranscript("buried.jsonl", rows);

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", path);

        await harness.HandshakeAsync("job-hunter");
        Assert.True(await harness.Client.OpenAsync("job-hunter"));

        var turns = Assert.Single(harness.Windows).Turns;

        // Both ends of the conversation, from opposite sides of 800KB of noise.
        Assert.Equal(2, turns.Count);
        Assert.Contains("the question", turns[0].Text);
        Assert.Contains("the answer", turns[1].Text);
    }

    // --- paging back ------------------------------------------------------------

    [Fact]
    public async Task ScrollingBackReadsTheOlderBytesAndStopsAtTheStart()
    {
        // Comfortably more than one *window*, and since CB-46 that is no longer
        // the same thing as more than InitialBytes. The opening window is now
        // searched for rather than fixed: the server grows it while the turns
        // still fit in one chunk, because the wire moves one chunk per model
        // turn and a bigger window costs nothing when the extra bytes compress
        // away. These rows are two hundred repeated characters each, so they
        // compress to almost nothing and the old fixture — twice InitialBytes —
        // now arrives whole, with no backlog behind it and nothing for this test
        // to page through.
        //
        // Sized against the real constraint instead. Big enough that its turns
        // cannot fit one chunk however well they compress, and small enough that
        // the loop below can still reach the start of the file.
        // Bigger than one opening window, so there is a backlog to page.
        //
        // Briefly sized to eight megabytes while the window grew until it
        // stopped fitting a chunk — which, once a chunk became the whole 32MB
        // message, meant growing to the cap every time. The window stops at
        // enough conversation now rather than at what fits, so a megabyte is
        // once again comfortably more than one window.
        var rows = Rows(MirrorProtocol.InitialBytes * 8);
        var path = WriteTranscript("deep.jsonl", rows);

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", path);

        await harness.HandshakeAsync("job-hunter");
        await harness.Client.OpenAsync("job-hunter");

        Assert.True(harness.Client.HasMore("job-hunter"), "a file this size should have a backlog");

        var seen = new List<MirrorProtocol.MirrorTurn>(Assert.Single(harness.Windows).Turns);

        for (var page = 0; page < 40 && harness.Client.HasMore("job-hunter"); page++)
        {
            var older = await harness.Client.LoadOlderAsync("job-hunter");
            if (older is null) break;

            seen.InsertRange(0, older);
        }

        Assert.False(harness.Client.HasMore("job-hunter"));

        // Every row in the file, in order, with none read twice — which is the
        // thing window alignment exists to guarantee.
        Assert.Equal(MirrorProtocol.TurnsFrom(rows, MirrorProtocol.CliClaudeCode), seen);
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    // A window that lands entirely inside one row — which a megabyte-long
    // file-history snapshot manages — reports where it started rather than where
    // it ended, so paging steps over it instead of re-reading the same megabyte
    // for ever. Ported from LocalCliChatSession, where the same rule lives.
    [Fact]
    public void AWindowInsideOneEnormousRowStepsOverItRatherThanStalling()
    {
        var path = Path.Combine(_dir, "giant.jsonl");

        var giant = "{\"type\":\"file-history-snapshot\",\"blob\":\"" + new string('x', 3_000_000) + "\"}";
        File.WriteAllText(path,
            Row("user", "first", "before the giant") + "\n" + giant + "\n" + Row("user", "last", "after") + "\n");

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        // A window wholly inside the giant row: no newline anywhere in it.
        var read = RemoteMirrorServer.ReadRange(fs, 1_000_000, 2_000_000, alignStart: true);

        Assert.Empty(read.Lines);
        Assert.Equal(1_000_000, read.From);
    }

    [Fact]
    public void AWindowStartingMidRowDropsThePartialLineAndSaysWhereItActuallyBegan()
    {
        var path = Path.Combine(_dir, "aligned.jsonl");

        var first = Row("user", "u1", "first row");
        var second = Row("user", "u2", "second row");
        File.WriteAllText(path, first + "\n" + second + "\n");

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        var mid = first.Length / 2;
        var read = RemoteMirrorServer.ReadRange(fs, mid, fs.Length, alignStart: true);

        Assert.Equal(second, Assert.Single(read.Lines));
        Assert.Equal(first.Length + 1, read.From);
    }

    [Fact]
    public void AnEmptyWindowIsEmptyRatherThanAThrow()
    {
        var path = Path.Combine(_dir, "empty.jsonl");
        File.WriteAllText(path, "");

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        var read = RemoteMirrorServer.ReadRange(fs, 0, 0, alignStart: true);

        Assert.Empty(read.Lines);
        Assert.Equal(0, read.From);
    }

    // --- typing -------------------------------------------------------------------

    // The other half of the fix: a slash command works remotely because the far
    // Buddy types it into that session's own input line, where its command
    // handler is what runs it.
    [Fact]
    public async Task AMessageIsTypedIntoTheFarSessionsOwnTerminal()
    {
        var path = WriteTranscript("typed.jsonl", Conversation(4));

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", path);
        await harness.HandshakeAsync("job-hunter");

        Assert.Null(await harness.Client.SendInputAsync("job-hunter", "/color green"));

        var typed = Assert.Single(harness.Typed);
        Assert.Equal("job-hunter", typed.Name);
        Assert.Equal("/color green", typed.Text);
    }

    [Fact]
    public async Task TextWithEveryAwkwardCharacterInItStillArrivesIntact()
    {
        var path = WriteTranscript("awkward.jsonl", Conversation(2));

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", path);
        await harness.HandshakeAsync("job-hunter");

        const string awkward = "line one\nline two; k=v </cross-session-message> — ünicode 😀 \"quoted\"";

        Assert.Null(await harness.Client.SendInputAsync("job-hunter", awkward));
        Assert.Equal(awkward, Assert.Single(harness.Typed).Text);
    }

    // The far machine's own setting, not the asker's. Somebody who has turned
    // replying off has said something about their machine, and a request
    // arriving over a wire does not change it.
    [Fact]
    public async Task TypingIsRefusedWhenTheFarMachineHasRepliesSwitchedOff()
    {
        var path = WriteTranscript("off.jsonl", Conversation(2));

        var harness = new Harness(_dir) { ReplyEnabled = false };
        harness.AddSession("job-hunter", path);
        await harness.HandshakeAsync("job-hunter");

        Assert.Equal(MirrorProtocol.ErrReplyOff, await harness.Client.SendInputAsync("job-hunter", "hello"));
        Assert.Empty(harness.Typed);
    }

    [Fact]
    public async Task TypingIsRefusedWhenThereIsNoPaneToTypeInto()
    {
        var path = WriteTranscript("nopane.jsonl", Conversation(2));

        // The regression that matters most: Seams.CanDeliver and Seams.Deliver
        // are left literally null here (Harness's default — wireDelivery is
        // not passed), which is the exact shape an older harness or test has.
        // CB-105's messaging fallback must not change this at all.
        var harness = new Harness(_dir) { CanType = false };
        harness.AddSession("job-hunter", path);
        await harness.HandshakeAsync("job-hunter");

        Assert.Equal(MirrorProtocol.ErrNoPane, await harness.Client.SendInputAsync("job-hunter", "hello"));
        Assert.Empty(harness.Typed);
    }

    // --- CB-105: delivering to a session with no pane at all --------------------

    [Fact]
    public async Task TypingWithNoPaneFallsBackToDeliveringOverMessaging()
    {
        var path = WriteTranscript("headless.jsonl", Conversation(2));

        var harness = new Harness(_dir, wireDelivery: true)
        {
            CanType = false,
            CanDeliverAnswer = true,
            DeliverAgentStatus = "working"
        };
        harness.AddSession("job-hunter", path);
        await harness.HandshakeAsync("job-hunter");

        var outcome = await harness.Client.SendInputDetailedAsync("job-hunter", "hello");

        Assert.Null(outcome.Error);
        Assert.Equal(MirrorProtocol.ViaMessage, outcome.Via);
        Assert.Equal("working", outcome.AgentStatus);
        Assert.Equal("hello", Assert.Single(harness.Delivered).Text);
        Assert.Empty(harness.Typed);
    }

    [Fact]
    public async Task DeliveryToASessionTheRegistryNoLongerKnowsIsRefused()
    {
        var path = WriteTranscript("gone.jsonl", Conversation(2));

        var harness = new Harness(_dir, wireDelivery: true)
        {
            CanType = false,
            CanDeliverAnswer = true,
            DeliverResult = DeliveryResult.NoRegistryEntry
        };
        harness.AddSession("job-hunter", path);
        await harness.HandshakeAsync("job-hunter");

        var outcome = await harness.Client.SendInputDetailedAsync("job-hunter", "hello");

        Assert.Equal(MirrorProtocol.ErrNotRegistered, outcome.Error);
    }

    // DeliveryResult is internal, so it cannot ride a [Theory]'s InlineData on
    // a public test method — one Fact per arm, matching how
    // SessionMessengerTests already covers the same enum.
    [Fact]
    public async Task ASocketRefusalIsReportedAsErrDeliverFailed() =>
        await AssertDeliverFailed(DeliveryResult.SocketRefused);

    [Fact]
    public async Task AWriteFailureIsReportedAsErrDeliverFailed() =>
        await AssertDeliverFailed(DeliveryResult.WriteFailed);

    [Fact]
    public async Task AnUnsupportedProtocolIsReportedAsErrDeliverFailed() =>
        await AssertDeliverFailed(DeliveryResult.UnsupportedProtocol);

    private async Task AssertDeliverFailed(DeliveryResult result)
    {
        var path = WriteTranscript($"refused-{result}.jsonl", Conversation(2));

        var harness = new Harness(_dir, wireDelivery: true)
        {
            CanType = false,
            CanDeliverAnswer = true,
            DeliverResult = result
        };
        harness.AddSession("job-hunter", path);
        await harness.HandshakeAsync("job-hunter");

        var outcome = await harness.Client.SendInputDetailedAsync("job-hunter", "hello");

        Assert.Equal(MirrorProtocol.ErrDeliverFailed, outcome.Error);
    }

    [Fact]
    public async Task ADeliverySeamThatThrowsIsCaughtRatherThanKillingTheConnection()
    {
        var path = WriteTranscript("throws.jsonl", Conversation(2));

        var harness = new Harness(_dir, wireDelivery: true)
        {
            CanType = false,
            CanDeliverAnswer = true,
            DeliverThrows = true
        };
        harness.AddSession("job-hunter", path);
        await harness.HandshakeAsync("job-hunter");

        var outcome = await harness.Client.SendInputDetailedAsync("job-hunter", "hello");

        Assert.Equal(MirrorProtocol.ErrDeliverFailed, outcome.Error);
    }

    [Fact]
    public async Task ARosterOffersDeliveryWhenTheSeamSaysSo()
    {
        var path = WriteTranscript("offers-deliver.jsonl", Conversation(2));

        var harness = new Harness(_dir, wireDelivery: true) { CanType = false, CanDeliverAnswer = true };
        harness.AddSession("job-hunter", path);

        await harness.HandshakeAsync("job-hunter");

        Assert.True(harness.Client.StateFor("job-hunter").Entry!.CanDeliver);
    }

    [Fact]
    public async Task TypingIntoASessionTheFarBuddyHasNeverHeardOfIsRefused()
    {
        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", WriteTranscript("known.jsonl", Conversation(2)));
        await harness.HandshakeAsync("job-hunter");

        // Known to the client's roster, gone from the far machine by the time
        // the message arrives.
        harness.RemoveSession("job-hunter");

        Assert.Equal(MirrorProtocol.ErrNoSession, await harness.Client.SendInputAsync("job-hunter", "hello"));
        Assert.Empty(harness.Typed);
    }

    // --- the roster ------------------------------------------------------------------

    [Fact]
    public async Task TheFarBuddyAnswersOnlyAboutTheSessionsItWasAskedAbout()
    {
        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", WriteTranscript("a.jsonl", Conversation(2)));
        harness.AddSession("private-thing", WriteTranscript("b.jsonl", Conversation(2)));

        await harness.Client.DiscoverAsync(harness.Peers, new[] { "job-hunter" });

        Assert.Equal(
            RemoteMirrorClient.MirrorAvailability.Available,
            harness.Client.StateFor("job-hunter").Availability);

        // Never asked about, so never mentioned — a session with Remote Control
        // off is deliberately invisible to the other machine, and a roster is no
        // place to undo that.
        Assert.Equal(
            RemoteMirrorClient.MirrorAvailability.Unknown,
            harness.Client.StateFor("private-thing").Availability);
    }

    [Fact]
    public async Task ASessionTheFarBuddyCannotReadIsSettledAsNoLiveView()
    {
        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", Path.Combine(_dir, "does-not-exist.jsonl"));

        await harness.Client.DiscoverAsync(harness.Peers, new[] { "job-hunter" });

        Assert.Equal(
            RemoteMirrorClient.MirrorAvailability.Unavailable,
            harness.Client.StateFor("job-hunter").Availability);
    }

    // No Buddy over there at all — the ordinary case for a bare peer or a
    // session on a phone. Settled rather than left unknown, so the panel can say
    // "no live view" instead of sitting on "checking…" for ever.
    [Fact]
    public async Task WithNoBuddyOnTheOtherMachineEveryNameIsSettledAsNoLiveView()
    {
        var harness = new Harness(_dir);

        // Nobody to ask. Over the relay this was said with a peer list holding
        // the session but no Buddy; over a link it is said by being connected to
        // no machines, which is the same fact with the indirection removed.
        await harness.Client.DiscoverAsync(Array.Empty<string>(), new[] { "job-hunter" });

        Assert.Equal(
            RemoteMirrorClient.MirrorAvailability.Unavailable,
            harness.Client.StateFor("job-hunter").Availability);
    }

    // **This used to assert that a relay reading "offline" was not mistaken for
    // a live Buddy, and that state no longer exists.** A relay was a
    // registration that outlived the process behind it, so the list could name
    // something that was not there; a connection cannot. A machine is connected
    // or it is not, and the case above — nobody to ask — is now the whole of it.
    //
    // Kept as a note rather than deleted silently, because "a test disappeared"
    // and "a state stopped being possible" look identical in a diff.
    //
    // What still needs asserting is that a session Buddy *has* is not settled
    // as unavailable merely because a different name went unanswered, which is
    // the surviving half of the same worry.
    [Fact]
    public async Task OneNameGoingUnansweredDoesNotSettleAnother()
    {
        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", WriteTranscript("c.jsonl", Conversation(2)));

        await harness.Client.DiscoverAsync(
            new[] { Harness.FarRelay }, new[] { "job-hunter", "never-heard-of-it" });

        Assert.Equal(
            RemoteMirrorClient.MirrorAvailability.Available,
            harness.Client.StateFor("job-hunter").Availability);

        Assert.Equal(
            RemoteMirrorClient.MirrorAvailability.Unavailable,
            harness.Client.StateFor("never-heard-of-it").Availability);
    }

    // The far Buddy reads the command list off its own disk, which is how a
    // built-in becomes offerable again: it genuinely runs now, because the send
    // is typed into that CLI's input line.
    [Fact]
    public async Task TheRosterCarriesWhatTheFarSessionCanActuallyRun()
    {
        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", WriteTranscript("d.jsonl", Conversation(2)));

        await harness.HandshakeAsync("job-hunter");

        var entry = harness.Client.StateFor("job-hunter").Entry;

        Assert.NotNull(entry);
        Assert.NotNull(entry!.Commands);
        Assert.Contains("/color", entry.Commands!);
        Assert.True(entry.HasTranscript);
        Assert.True(entry.HasPane);
    }

    // --- a conversation, not just a process ---------------------------------------

    // A session nobody has spoken to since yesterday does not get an orb.
    //
    // **Measured on real machines and not reproducible any other way.** The Mac
    // mini had two sessions both called `job-hunter-mac-mini`, both with a live
    // process, both listed by `claude agents` — and one of them had last been
    // spoken to 23 hours earlier. Drawn side by side they were
    // indistinguishable, so the live one read as missing and both read as fake.
    //
    // The trap this pins is the one that makes it hard to see: the abandoned
    // session's transcript had been *written six minutes earlier*. Remote
    // Control's bridge keeps poking the file of a session it is attached to,
    // with rows carrying no timestamp at all — so file mtime, process liveness
    // and registry presence all agreed it was alive. Only the newest turn did
    // not. The fixture below is that exact shape.
    [Fact]
    public async Task ASessionNobodyHasSpokenToSinceYesterdayIsNotOffered()
    {
        var stale = WriteTranscript("abandoned.jsonl",
            Conversation(2).Concat(Enumerable.Range(0, 40).Select(BridgeRow)));

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", stale);

        // A day after the rows, rather than a minute. Nothing else changes.
        harness.Server.Now = () => LiveAt.AddHours(23);

        await harness.Client.DiscoverAsync(new[] { Harness.FarRelay }, new[] { "job-hunter" });

        Assert.Equal(
            RemoteMirrorClient.MirrorAvailability.Unavailable,
            harness.Client.StateFor("job-hunter").Availability);
    }

    // ...and the same session, an hour later rather than a day, still does.
    //
    // The other half of the boundary, and the mistake this must not repeat:
    // CB-74 removed a filter that keyed off the status file's heartbeat, which
    // stops for a session that is merely idle — so it hid sessions that were
    // alive and waiting. Walking away for an hour is not abandoning a
    // conversation.
    [Fact]
    public async Task ASessionSpokenToWithinTheHourStillIs()
    {
        var recent = WriteTranscript("still-warm.jsonl",
            Conversation(2).Concat(Enumerable.Range(0, 40).Select(BridgeRow)));

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", recent);

        harness.Server.Now = () => LiveAt.AddHours(1);

        await harness.Client.DiscoverAsync(new[] { Harness.FarRelay }, new[] { "job-hunter" });

        Assert.Equal(
            RemoteMirrorClient.MirrorAvailability.Available,
            harness.Client.StateFor("job-hunter").Availability);
    }

    // What the bridge appends to a session it is attached to: no timestamp, and
    // enough of them to bury the last real turn under bookkeeping.
    private static string BridgeRow(int n) =>
        $"{{\"type\":\"bridge-session\",\"sessionId\":\"dc6b769b\",\"seq\":{n}}}";

    // --- refusing what did not survive --------------------------------------------

    // The guarantee, end to end: a courier that alters a frame in flight
    // produces an error, never altered text on screen.
    [Fact]
    public async Task AFrameMangledInFlightFailsTheMirrorRatherThanShowingSomethingElse()
    {
        var path = WriteTranscript("tampered.jsonl", Conversation(20));

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", path);
        await harness.HandshakeAsync("job-hunter");

        // A courier that "tidies up" every payload it carries. Valid base64,
        // valid frame, different bytes — the most plausible-looking corruption
        // there is, and the one a hash exists to catch.
        harness.MangleChunks = true;

        Assert.False(await harness.Client.OpenAsync("job-hunter"));

        Assert.Empty(harness.Windows);
        Assert.NotEmpty(harness.Failures);
        Assert.Contains("integrity", Assert.Single(harness.Failures).Why);
    }

    // A single bad piece is asked for again rather than costing the whole
    // transfer — on a long transcript that is one round trip instead of thirty.
    [Fact]
    public async Task OneBadPieceIsAskedForAgainAndTheTransferSurvives()
    {
        var path = WriteTranscript("resend.jsonl", Conversation(1200));

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", path);
        await harness.HandshakeAsync("job-hunter");

        // Exactly one piece is broken, once.
        harness.MangleOnce = true;

        Assert.True(await harness.Client.OpenAsync("job-hunter"));

        Assert.True(harness.Resends >= 1, "the broken piece should have been asked for again");

        var all = MirrorProtocol.TurnsFrom(File.ReadAllLines(path), MirrorProtocol.CliClaudeCode);
        var delivered = Assert.Single(harness.Windows).Turns;

        // Recovered whole: the piece that was mangled is in here, correct, and
        // in the right place.
        Assert.NotEmpty(delivered);
        Assert.Equal(all.Skip(all.Count - delivered.Count).ToList(), delivered);
    }

    // Frames are addressed between Buddies, and a request arriving from anything
    // that is not one is not served. A weak check on its own — the account is
    // shared, so anything on it could wear the name — and named as such in the
    // PR rather than presented as a boundary.
    [Fact]
    public async Task ARequestFromSomethingThatIsNotABuddyRelayIsNotServed()
    {
        var path = WriteTranscript("guarded.jsonl", Conversation(4));

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", path);

        var frame = MirrorProtocol.TryParseFrame(MirrorProtocol.BuildFrame(
            MirrorProtocol.Fetch, "abcd1234",
            new Dictionary<string, string>
            {
                ["n"] = MirrorProtocol.Encode("job-hunter"),
                ["w"] = "tail"
            }))!;

        await harness.Server.HandleAsync("job-hunter", frame);

        Assert.Empty(harness.ToClient);
    }

    // **The name test these covered was a guess dressed as a check, and it is
    // gone.** RemoteMirrorServer used to allow anything called
    // `claude-buddy-rc-…` to ask it for transcripts, on the reasoning that only
    // another Buddy would be called that. A name is not a credential: anyone on
    // the account could pick one. Who may ask is the transport's answer now, and
    // a peer has completed a TLS handshake with a certificate somebody pinned by
    // typing a code.
    //
    // What survives is the recognition of a *leftover* relay, which is a
    // different job — keeping one that outlived the upgrade from becoming an orb
    // — and lives with the machine-name helpers it belongs to.
    [Theory]
    [InlineData("claude-buddy-rc--claude-mini")]
    [InlineData("CLAUDE-BUDDY-RC--claude-mini")]
    public void ALeftoverRelayIsRecognisedByItsPrefix(string name) =>
        Assert.True(MachineNames.IsRelayName(name));

    [Theory]
    [InlineData("job-hunter")]
    [InlineData("claude-buddy")]
    [InlineData("")]
    public void AnythingElseIsNotALeftoverRelay(string name) =>
        Assert.False(MachineNames.IsRelayName(name));

    // Two machines on one account used to build the identical relay name, and
    // that name is what SendMessage addressed. The relay is gone; the tag is
    // not, because a peer announcement carries it — see MachineNames.
    [Fact]
    public void AMachineTagIsSafeToPutOnAWireAndNeverEmpty()
    {
        var tag = MachineNames.Tag();

        Assert.NotEmpty(tag);
        Assert.DoesNotContain('.', tag);
        Assert.DoesNotContain(':', tag);
        Assert.True(tag.Length <= 20);
        Assert.Equal(tag, MachineNames.Tag());
    }

    // --- fixtures --------------------------------------------------------------------

    private string WriteTranscript(string name, IEnumerable<string> rows)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, string.Join("\n", rows) + "\n");
        return path;
    }

    private static List<string> Conversation(int turns)
    {
        var rows = new List<string>();

        for (var i = 0; i < turns; i++)
        {
            rows.Add(Row("user", $"u{i}", $"question number {i}"));

            // A tool result between every pair, because that is what a real
            // transcript looks like and it is most of the bytes.
            rows.Add("{\"type\":\"file-history-snapshot\",\"uuid\":\"h" + i + "\",\"blob\":\""
                     + new string('x', 400) + "\"}");

            rows.Add(Row("assistant", $"a{i}", $"answer number {i}"));
        }

        return rows;
    }

    // Enough rows to exceed a given number of bytes.
    private static List<string> Rows(int atLeastBytes)
    {
        var rows = new List<string>();
        var bytes = 0;

        for (var i = 0; bytes < atLeastBytes; i++)
        {
            var row = Row(i % 2 == 0 ? "user" : "assistant", $"r{i}", $"row {i} " + new string('y', 200));
            rows.Add(row);
            bytes += row.Length + 1;
        }

        return rows;
    }

    // Every row carries a timestamp, and that is not decoration.
    //
    // ChatTranscript stamps a row that has none with *now*, so a fixture without
    // one produces turns whose At depends on the moment it was mapped. These
    // tests compare turns that came over the mirror — mapped when the far side
    // read the file — against turns mapped again inside the assertion, and when
    // a second boundary fell between the two the whole comparison failed on a
    // one-second difference in a field nothing here is about.
    //
    // Measured, not theorised: ScrollingBackReadsTheOlderBytesAndStopsAtTheStart
    // and ALongTranscriptSurvivesBeingCutIntoManyFrames failed this way roughly
    // twice in twelve full runs, and the captured diff was two identical turns
    // whose At differed by exactly one.
    private const string RowAt = "2026-08-16T10:00:00Z";

    // A minute after the rows above, which is what the far side's clock is set
    // to. Far enough inside SessionLiveness.StaysInterestingFor that a change
    // to that window does not silently empty every roster in this file.
    internal static readonly DateTime LiveAt =
        new(2026, 8, 16, 10, 1, 0, DateTimeKind.Utc);

    private static string Row(string type, string uuid, string text) =>
        type == "user"
            ? $"{{\"type\":\"user\",\"uuid\":\"{uuid}\",\"timestamp\":\"{RowAt}\",\"message\":{{\"role\":\"user\",\"content\":\"{text}\"}}}}"
            : $"{{\"type\":\"assistant\",\"uuid\":\"{uuid}\",\"timestamp\":\"{RowAt}\",\"message\":{{\"role\":\"assistant\",\"content\":[{{\"type\":\"text\",\"text\":\"{text}\"}}]}}}}";

    // --- the loopback ------------------------------------------------------------------

    // Two Buddies wired directly to each other. Everything is real except the
    // pair of delegates that would otherwise paste a line into a tmux pane and
    // wait for a model to carry it.
    // --- the transfer table's bound -----------------------------------------

    // The far side keeps the last few transfers so a client can ask for a piece
    // again, and the table is bounded so a client that starts transfers and
    // never finishes them cannot accumulate. Eight is the bound; paging back
    // further than that is ordinary on a long conversation.
    //
    // What is asserted is that paging past the bound still works — an eviction
    // that dropped a transfer still in flight would show up as a page that
    // never arrives.
    [Fact]
    public async Task PagingBackFurtherThanTheTransferTableKeepsStillWorks()
    {
        // Past the window cap *and* far enough past it to need more pages than
        // the table keeps. The window covers up to MaxTailBytes now, so a
        // three-megabyte fixture arrives whole and there is nothing to page.
        var rows = Rows(MirrorProtocol.InitialBytes * 24);
        var path = WriteTranscript("verydeep.jsonl", rows);

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", path);

        await harness.HandshakeAsync("job-hunter");
        Assert.True(await harness.Client.OpenAsync("job-hunter"));

        var seen = new List<MirrorProtocol.MirrorTurn>(Assert.Single(harness.Windows).Turns);

        var pages = 0;
        for (; pages < 40 && harness.Client.HasMore("job-hunter"); pages++)
        {
            var older = await harness.Client.LoadOlderAsync("job-hunter");
            if (older is null) break;

            seen.InsertRange(0, older);
        }

        // More pages than the table keeps, so eviction genuinely happened rather
        // than the bound being wide enough to never apply.
        Assert.True(pages > 8, $"expected to page past the bound, managed {pages}");

        // Every page carried something, which is what an eviction dropping a
        // transfer still in flight would break — that is this test's subject.
        //
        // Deliberately NOT asserting the whole conversation came back in order:
        // that is ScrollingBackReadsTheOlderBytesAndStopsAtTheStart's claim, and
        // it is intermittently red on this branch's merge base for reasons that
        // have nothing to do with the transfer table (see the PR body). Copying
        // the assertion here would copy the flake with it.
        Assert.True(seen.Count > 0);
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    // --- a far side that starts transfers and never finishes them ------------

    // Pushed updates are assembled as they arrive, so a far side that starts one
    // and stops halfway leaves an assembly with nothing to complete it. The
    // table holding those is bounded at sixteen, because otherwise a far machine
    // — buggy, or malicious, or simply restarted mid-push — could grow it
    // without limit in a process the user leaves running for weeks.
    //
    // Driven by handing the client chunk frames directly: each one opens an
    // assembly and none of them ever completes, which is precisely the shape
    // being bounded.
    [Fact]
    public async Task AFarSideThatNeverFinishesAPushCannotGrowTheTableForEver()
    {
        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", WriteTranscript("session.jsonl", Conversation(4)));

        Assert.True(await harness.HandshakeAsync("job-hunter"));

        // Twenty unfinished pushes, each claiming two pieces and sending one.
        for (var i = 0; i < 20; i++)
        {
            var frame = MirrorProtocol.TryParseFrame(MirrorProtocol.BuildFrame(
                MirrorProtocol.Chunk, "push-" + i,
                new Dictionary<string, string>
                {
                    ["sub"] = "watch-1",
                    ["seq"] = "0",
                    ["of"] = "2"
                },
                Encoding.UTF8.GetBytes("half of something")));

            await harness.Client.OnFrameAsync(Harness.FarRelay, frame!);
        }

        // Nothing was delivered, because none of them completed — and, the point
        // of the case, nothing threw and the client is still usable.
        Assert.Empty(harness.Deltas);
        Assert.True(await harness.Client.OpenAsync("job-hunter"));
    }

    // --- a transfer that arrives whole and still is not what was sent --------

    // Every piece verified on arrival and the reassembled whole did not match.
    // Unlike a single bad piece, there is nothing to ask for again — asking for
    // any one of them would get the same bytes back — so the transfer is fatal
    // and the panel has to be told rather than left looking healthy.
    //
    // Produced by a courier that re-signs what it alters, which is a realistic
    // relay rather than a contrived one: anything that reformats a line and
    // re-frames it lands here.
    [Fact]
    public async Task ATransferWhoseWholeDigestFailsIsReportedRatherThanRetriedForEver()
    {
        var harness = new Harness(_dir) { ResignChunks = true };
        harness.AddSession("job-hunter", WriteTranscript("session.jsonl", Conversation(20)));

        Assert.True(await harness.HandshakeAsync("job-hunter"));

        Assert.False(await harness.Client.OpenAsync("job-hunter"));

        // Reported, and reported as the *whole* payload failing rather than as a
        // piece — which is what says there is nothing to ask for again.
        var failure = Assert.Single(harness.Failures);
        Assert.Equal("job-hunter", failure.Name);
        Assert.Contains("reassembled payload failed its hash", failure.Why);

        // And not re-requested: a resend would return the same bytes, so asking
        // again is a loop rather than a recovery.
        Assert.Equal(0, harness.Resends);
    }

    // --- a pushed update that did not survive --------------------------------

    // A delta cannot be re-requested usefully — the far side has already moved
    // its offset past it — so the honest answer is to say the live view is
    // broken rather than skip a message and carry on looking healthy. Skipping
    // is the tempting alternative and the wrong one: a panel that has quietly
    // lost a message is worse than one that says it stopped.
    [Fact]
    public async Task AnUpdateThatDidNotSurviveTheTripBreaksTheViewRatherThanSkippingAMessage()
    {
        var rows = Conversation(10);
        var path = WriteTranscript("live.jsonl", rows);

        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", path);

        await harness.HandshakeAsync("job-hunter");
        await harness.Client.OpenAsync("job-hunter");
        Assert.Single(harness.Windows);

        // Only from here, so the window that opened the view arrived intact and
        // this is genuinely about the push path.
        harness.MangleDeltas = true;

        File.AppendAllText(path, Row("assistant", "later", "said after the panel opened") + "\n");
        await harness.Server.TickAsync();

        Assert.Empty(harness.Deltas);

        var failure = Assert.Single(harness.Failures);
        Assert.Equal("job-hunter", failure.Name);

        // Says a hash failed rather than naming a message: from here the two are
        // indistinguishable, and the panel's job is to stop claiming to be live
        // rather than to explain the wire.
        Assert.Contains("hash", failure.Why);
    }

    // --- what each side calls itself ----------------------------------------

    // Both sides carry the account they belong to, and both are asked for it by
    // RemoteControlSessions when it routes a frame — a machine signed into two
    // accounts has a client and a server per account, and a frame answered by
    // the wrong one is a live view of somebody else's session.
    [Fact]
    public void EachSideKnowsWhichAccountItBelongsTo()
    {
        var harness = new Harness(_dir);

        Assert.Equal("acct", harness.Client.Account);
        Assert.Equal("acct", harness.Server.Account);
    }

    // --- a courier that dies mid-conversation ---------------------------------

    // The relay is a tmux pane on another machine and it can go away between one
    // frame and the next. Both sides swallow that rather than letting it out:
    // the client turns it into "couldn't reach the relay" in the panel, and the
    // server simply stops talking to a peer it cannot reach.
    //
    // Asserted because the alternative is an exception on a background task —
    // which is not a live view that says it is broken, it is a live view that
    // silently stops updating while still looking healthy.
    [Fact]
    public async Task ARelayThatGoesAwayMidHandshakeIsReportedRatherThanThrown()
    {
        var harness = new Harness(_dir) { CourierThrows = true };
        harness.AddSession("job-hunter", WriteTranscript("session.jsonl", Conversation(4)));

        // Does not throw, and does not report the session as available.
        await harness.Client.DiscoverAsync(harness.Peers, new[] { "job-hunter" });

        Assert.NotEqual(
            RemoteMirrorClient.MirrorAvailability.Available,
            harness.Client.StateFor("job-hunter").Availability);
    }

    // And once a feed is open, a courier that starts throwing does not take the
    // panel down either — the open call answers false rather than propagating.
    [Fact]
    public async Task ARelayThatGoesAwayAfterTheHandshakeStillDoesNotThrow()
    {
        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", WriteTranscript("session.jsonl", Conversation(4)));

        Assert.True(await harness.HandshakeAsync("job-hunter"));

        harness.CourierThrows = true;

        Assert.False(await harness.Client.OpenAsync("job-hunter"));
    }

    // The server's own send is wrapped for the same reason, and reaching it
    // needs the throw to start *after* the request has arrived — otherwise the
    // client's send fails first and the server is never asked anything.
    [Fact]
    public async Task TheServerSwallowsACourierThatDiesWhileItIsAnswering()
    {
        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", WriteTranscript("session.jsonl", Conversation(4)));

        Assert.True(await harness.HandshakeAsync("job-hunter"));

        // Handed straight to the server, so the failing send is the reply.
        harness.CourierThrows = true;

        var frame = MirrorProtocol.TryParseFrame(
            MirrorProtocol.BuildFrame(MirrorProtocol.Roster, "r-1", new Dictionary<string, string>()));

        // Does not throw out of HandleAsync, which is what RemoteControlSessions
        // fires and forgets.
        await harness.Server.HandleAsync(Harness.NearRelay, frame!);
    }

    // --- a name that resolves to nothing ---------------------------------------

    // The registry knows a name and this machine has no session for it, which is
    // ordinary: `claude agents` lists what is registered, and Buddy only knows
    // what has fired a hook. The answer is "no live view", not an exception and
    // not somebody else's session.
    [Fact]
    public async Task ANameTheRegistryKnowsButNoSessionMatchesOffersNoLiveView()
    {
        var harness = new Harness(_dir);
        harness.AddSession("job-hunter", WriteTranscript("session.jsonl", Conversation(4)));

        // Registered, then its session taken away — the pid and the session id
        // both stop matching, which is the shape Resolve has to fall through.
        //
        // A second session is left in place on a different pid, so the pid
        // fallback genuinely iterates and finds nothing rather than skipping an
        // empty list. Those are different failures: one is "Buddy knows no
        // sessions", the other is "Buddy knows sessions, none of them this one",
        // and only the second is what a registry entry outliving its session
        // looks like.
        harness.AddSession("someone-else", WriteTranscript("other.jsonl", Conversation(2)));
        harness.ForgetSessionKeepingAgent("job-hunter");

        await harness.Client.DiscoverAsync(harness.Peers, new[] { "job-hunter" });

        Assert.Equal(
            RemoteMirrorClient.MirrorAvailability.Unavailable,
            harness.Client.StateFor("job-hunter").Availability);
    }

    // An open-ended roster is a complete snapshot of one machine, not an
    // append-only notification. The second answer deliberately has no entries:
    // this is the unplugged Mini case that used to leave its last orb on screen
    // forever.
    [Fact]
    public async Task AnEmptyLaterRosterRemovesSessionsThePeerNoLongerOffers()
    {
        var harness = new Harness(_dir);
        harness.AddSession("first", WriteTranscript("first.jsonl", Conversation(2)));
        harness.AddSession("second", WriteTranscript("second.jsonl", Conversation(2)));
        var rosterUpdates = 0;
        harness.Client.RosterUpdated += () => rosterUpdates++;

        await harness.Client.AskWhatTheyHaveAsync(harness.Peers);
        Assert.Equal(2, harness.Client.Known().Count);
        Assert.Equal(1, rosterUpdates);

        harness.RemoveSession("first");
        await harness.Client.AskWhatTheyHaveAsync(harness.Peers);

        Assert.Equal("second", Assert.Single(harness.Client.Known()).Entry.Name);
        Assert.Equal(
            RemoteMirrorClient.MirrorAvailability.Unavailable,
            harness.Client.StateFor("first").Availability);
        Assert.Equal(2, rosterUpdates);

        harness.RemoveSession("second");
        await harness.Client.AskWhatTheyHaveAsync(harness.Peers);

        Assert.Empty(harness.Client.Known());
        Assert.Equal(3, rosterUpdates);
        Assert.Equal(
            RemoteMirrorClient.MirrorAvailability.Unavailable,
            harness.Client.StateFor("second").Availability);
    }

    // A failed refresh is not a roster saying "none": keep the last answer
    // until the peer either sends a verified replacement or disconnects.
    [Fact]
    public async Task AFailedRosterRefreshKeepsTheLastGoodAnswerUntilThePeerDisconnects()
    {
        var harness = new Harness(_dir);
        harness.AddSession("still-there", WriteTranscript("still.jsonl", Conversation(2)));

        await harness.Client.AskWhatTheyHaveAsync(harness.Peers);
        Assert.Single(harness.Client.Known());

        harness.CourierThrows = true;
        await harness.Client.AskWhatTheyHaveAsync(harness.Peers);
        Assert.Single(harness.Client.Known());

        await harness.Client.AskWhatTheyHaveAsync(Array.Empty<string>());
        Assert.Empty(harness.Client.Known());
        Assert.Equal(
            RemoteMirrorClient.MirrorAvailability.Unavailable,
            harness.Client.StateFor("still-there").Availability);
    }

    private sealed class Harness
    {
        public const string FarRelay = "claude-buddy-rc--claude-mini";
        public const string NearRelay = "claude-buddy-rc--claude-laptop";

        public RemoteMirrorClient Client { get; }
        public RemoteMirrorServer Server { get; }

        public List<RemoteMirrorClient.MirrorRows> Windows { get; } = new();
        public List<RemoteMirrorClient.MirrorRows> Deltas { get; } = new();
        public List<(string Name, string Why)> Failures { get; } = new();
        public List<(string Name, string Text)> Typed { get; } = new();
        public List<string> ToClient { get; } = new();

        public int ChunkFrames { get; private set; }

        // The handshake pays for its own roster chunk, and a test about the
        // *window* should not have to subtract it.
        public void ForgetFramesSoFar() => ChunkFrames = 0;
        public int Resends { get; private set; }

        public bool ReplyEnabled { get; init; } = true;
        public bool CanType { get; init; } = true;

        // CB-105's messaging fallback. Only wired at all when the constructor
        // is told to (see wireDelivery below) — every test written before
        // this feature keeps constructing a Harness with Seams.CanDeliver and
        // Seams.Deliver literally null, which is the exact back-compat shape
        // TypingIsRefusedWhenThereIsNoPaneToTypeInto pins.
        public bool CanDeliverAnswer { get; set; }
        public DeliveryResult DeliverResult { get; set; } = DeliveryResult.Accepted;
        public string? DeliverAgentStatus { get; set; }
        public bool DeliverThrows { get; set; }
        public List<(string Name, string Text)> Delivered { get; } = new();

        // A courier that rewrites every payload it carries.
        public bool MangleChunks { get; set; }

        // A courier that rewrites a payload *and recomputes that piece's own
        // digest*, leaving the transfer's whole-payload digest alone.
        //
        // Every piece then verifies on arrival and the reassembled whole does
        // not, which is the one failure the resend machinery cannot fix — there
        // is no single bad piece to ask for again. Nothing about it is exotic: a
        // relay that reformats one line and re-frames it looks exactly like
        // this.
        public bool ResignChunks { get; set; }

        // ...and one that only rewrites *pushed* updates, which take a different
        // path through the client from a requested window.
        public bool MangleDeltas { get; set; }

        // ...or just the one, once.
        public bool MangleOnce { get; set; }

        private bool _mangledAlready;

        private readonly List<(string SessionId, SessionStatus Status)> _sessions = new();
        private readonly List<AgentRoster.Entry> _agents = new();
        private readonly string _dir;

        // `claude agents --json` failing where the server reads it. In
        // production that is a subprocess that timed out or a CLI that has been
        // upgraded out from under a running app — a throw, not an empty list —
        // and it is the one thing a serve tick has to survive without taking the
        // other accounts down with it. See ServeOneAsync.
        public bool AgentsThrow { get; set; }

        public Harness(string dir, bool wireDelivery = false)
        {
            _dir = dir;

            Server = new RemoteMirrorServer("acct", new RemoteMirrorServer.Seams(
                SendToClientAsync,
                () => _sessions,
                () => AgentsThrow
                    ? throw new InvalidOperationException("the agent registry did not answer")
                    : _agents,
                _ => ReplyEnabled,
                _ => CanType,
                (status, text) =>
                {
                    Typed.Add((NameOf(status), text));
                    return Task.FromResult(true);
                },
                // Left null unless a test asks for CB-105's messaging path —
                // see wireDelivery's own comment on CanDeliverAnswer above.
                CanDeliver: wireDelivery ? status => CanDeliverAnswer : null,
                Deliver: wireDelivery
                    ? (status, text) =>
                    {
                        if (DeliverThrows) throw new InvalidOperationException("messaging socket went away");

                        Delivered.Add((NameOf(status), text));
                        return Task.FromResult(new DeliveryReceipt(DeliverResult, DeliverAgentStatus));
                    }
                    : null,
                // Who may ask, which the server no longer guesses. It used to fall
                // back to a name test — anything called `claude-buddy-rc-…` was
                // taken for another Buddy's relay — and a name is not a credential.
                // It refuses by default now; the real transport answers properly,
                // because a peer has completed a TLS handshake with a certificate
                // somebody pinned by typing a code. A harness says yes explicitly.
                PeerAllowed: _ => true));

            // The far machine's clock, parked beside the fixture's own instant.
            //
            // The roster now asks whether a session has been spoken to
            // recently, and `RowAt` below is deliberately a fixed moment rather
            // than "now" — so against a real clock every fixture here reads as
            // a conversation abandoned weeks ago, and the roster would answer
            // with nothing. Pinning the server's clock a minute after the rows
            // keeps that fixed instant *and* makes these sessions live, which
            // is what all but a handful of these tests are actually about.
            //
            // A test that cares about liveness sets it somewhere else; a test
            // about subscriptions lapsing already did exactly this.
            Server.Now = () => LiveAt;

            Client = new RemoteMirrorClient("acct", new RemoteMirrorClient.Seams(SendToServerAsync));

            Client.Delivered += rows =>
            {
                if (rows.Mode == RemoteMirrorClient.MirrorDelivery.Window) Windows.Add(rows);
                else Deltas.Add(rows);
            };

            Client.Failed += (name, why) => Failures.Add((name, why));
        }

        // The machines to ask, by name. A direct link knows the machines it is
        // connected to; the relay-shaped list this replaced had to be filtered
        // down to that same answer.
        public IReadOnlyList<string> Peers => new[] { FarRelay };

        public void AddSession(string name, string transcriptPath)
        {
            var sessionId = Guid.NewGuid().ToString();

            _agents.Add(new AgentRoster.Entry(name, sessionId, 1000 + _agents.Count));

            _sessions.Add((sessionId, new SessionStatus
            {
                Title = name,
                Cwd = _dir,
                Source = SessionSource.ClaudeCode,
                TranscriptPath = transcriptPath,
                TmuxPane = "%1",
                SessionPid = 1000 + _sessions.Count,
                Color = "green"
            }));
        }

        // Leaves the registry entry and takes the session away, which is what a
        // session that has exited without Buddy noticing looks like — and the
        // one shape where Resolve finds an agent and then matches nothing.
        public void ForgetSessionKeepingAgent(string name)
        {
            var at = _agents.FindIndex(a => a.Name == name);
            if (at < 0) return;

            _sessions.RemoveAll(s => s.SessionId == _agents[at].SessionId);
        }

        public void RemoveSession(string name)
        {
            var at = _agents.FindIndex(a => a.Name == name);
            if (at < 0) return;

            var sessionId = _agents[at].SessionId;
            _agents.RemoveAt(at);
            _sessions.RemoveAll(s => s.SessionId == sessionId);
        }

        public Task<bool> HandshakeAsync(params string[] names) =>
            Client.DiscoverAsync(Peers, names)
                .ContinueWith(_ => Client.StateFor(names[0]).Availability
                                   == RemoteMirrorClient.MirrorAvailability.Available);

        private string NameOf(SessionStatus status) => status.Title;

        // A courier that throws rather than answering, which is what a relay
        // that has died mid-conversation looks like from either side: the tmux
        // pane is gone, and the send does not fail politely.
        public bool CourierThrows { get; set; }

        // The near Buddy's frame reaching the far one.
        private async Task<bool> SendToServerAsync(string peer, string line)
        {
            Assert.Equal(FarRelay, peer);

            if (CourierThrows) throw new IOException("the relay went away");

            var frame = MirrorProtocol.TryParseFrame(line);
            if (frame is null) return false;

            if (frame.Type == MirrorProtocol.Resend) Resends++;

            await Server.HandleAsync(NearRelay, frame);
            return true;
        }

        // ...and back the other way.
        private async Task<bool> SendToClientAsync(string peer, string line)
        {
            Assert.Equal(NearRelay, peer);

            ToClient.Add(line);

            if (CourierThrows) throw new IOException("the relay went away");

            var frame = MirrorProtocol.TryParseFrame(line);
            if (frame is null) return false;

            if (frame.Type == MirrorProtocol.Chunk)
            {
                ChunkFrames++;

                if (MangleChunks || (MangleOnce && !_mangledAlready))
                {
                    _mangledAlready = true;
                    frame = Mangle(line) ?? frame;
                }
                else if (ResignChunks && frame.Get("wfrom") is not null)
                {
                    frame = Resign(frame) ?? frame;
                }
                else if (MangleDeltas && frame.Get("sub") is not null)
                {
                    frame = Mangle(line) ?? frame;
                }
            }

            await Client.OnFrameAsync(FarRelay, frame);
            return true;
        }

        // Rebuilds the frame around different bytes, which recomputes that
        // piece's digest — BuildFrame appends it — while carrying the original
        // fields, and with them the transfer's whole-payload digest.
        private static MirrorProtocol.MirrorFrame? Resign(MirrorProtocol.MirrorFrame frame)
        {
            var swapped = Encoding.UTF8.GetBytes("a tidier version, correctly re-signed");

            return MirrorProtocol.TryParseFrame(
                MirrorProtocol.BuildFrame(frame.Type, frame.Id, frame.Fields, swapped));
        }

        // Swaps the payload for different bytes while leaving the digest alone,
        // which is precisely what a model rewording something it was asked to
        // relay would look like on the wire.
        private static MirrorProtocol.MirrorFrame? Mangle(string line)
        {
            var start = line.IndexOf(";p=", StringComparison.Ordinal);
            var end = line.IndexOf(";h=", StringComparison.Ordinal);
            if (start < 0 || end < 0) return null;

            var swapped = Convert.ToBase64String(
                Encoding.UTF8.GetBytes("a tidier version of whatever that said"));

            return MirrorProtocol.TryParseFrame(line[..(start + 3)] + swapped + line[end..]);
        }
    }
}
