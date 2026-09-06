using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace ClaudeBuddy.Tests;

// A local CLI session as the chat panel talks to it: a transcript file on disk,
// tailed.
//
// In UiTests because the loads finish on the Avalonia dispatcher — the read
// happens on a worker and the result is posted back — so a test has to pump the
// loop to see it. That is the only reason this is not a unit test; nothing here
// starts a CLI, and the sending half (which goes through tmux) is not touched.
//
// The thing worth understanding before reading any of it, quoting the source:
// there is only one conversation and this is not a copy of it. The file *is* the
// conversation, so what these tests assert is that the panel reads the same thing
// the terminal is writing — including the parts of that file it must not show.
[Collection("Settings")]
public class LocalCliChatSessionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cb-localcli-" + Guid.NewGuid().ToString("N"));

    public LocalCliChatSessionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // Real Claude Code row shapes, trimmed of the fields none of this reads.
    private static string User(string uuid, string text) =>
        "{\"type\":\"user\",\"uuid\":\"" + uuid + "\",\"timestamp\":\"2026-08-24T12:00:00Z\","
        + "\"message\":{\"role\":\"user\",\"content\":" + Json(text) + "}}";

    private static string Assistant(string uuid, string text) =>
        "{\"type\":\"assistant\",\"uuid\":\"" + uuid + "\",\"timestamp\":\"2026-08-24T12:00:01Z\","
        + "\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":"
        + Json(text) + "}]}}";

    private static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    private string Transcript(params string[] rows)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(path, string.Join('\n', rows) + "\n");
        return path;
    }

    private static LocalCliChatSession Session(string transcriptPath, string state = "idle") =>
        new("s1", new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            TranscriptPath = transcriptPath,
            State = state,
            Title = "",
        });

    // --- the attach affordance ----------------------------------------------

    // Whether the panel offers to put this session in a terminal. The rule is
    // ClickRouting's and is covered per case there; what this pins is that the
    // session asks it, with its *own* status — which is the wiring that would
    // fail silently, since a session that always answered false would simply
    // show no button and look like a session that could be typed into.
    [AvaloniaFact]
    public void ABackgroundSessionOffersAnAttachAndSaysWhatItWants()
    {
        var session = new LocalCliChatSession("s1", new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            Shape = LocalSessionShape.Background,
            Presence = OrbPresence.NeedsInput,
            SessionPid = Environment.ProcessId,
            State = "idle",
        });

        Assert.True(session.CanOpenElsewhere);
        Assert.Equal("Needs input — attach to reply", session.ComposerHint);
    }

    // An ordinary session in a tmux pane: nothing to attach, and the box says
    // what it has always said. A button on every panel would be a mark that
    // distinguishes nothing, which is the argument the orb's badges are held to.
    [AvaloniaFact]
    public void AnOrdinarySessionOffersNothingElsewhere()
    {
        var session = new LocalCliChatSession("s1", new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            Shape = LocalSessionShape.Terminal,
            SessionPid = Environment.ProcessId,
            TmuxPane = "%7",
            TmuxSocket = "/tmp/tmux-501/default",
            State = "idle",
        });

        Assert.False(session.CanOpenElsewhere);
    }

    // The read runs on a worker and posts its result back, so the loop has to be
    // pumped until it lands. Bounded rather than a bare spin: a test that hangs
    // tells you far less than one that fails.
    private static void PumpUntil(Func<bool> done, string what)
    {
        for (var i = 0; i < 400 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.True(done(), $"timed out waiting for {what}");
    }

    private static LocalCliChatSession Started(string transcriptPath, string state = "idle")
    {
        var session = Session(transcriptPath, state);
        session.Start();
        PumpUntil(() => session.History.Count > 0 || session.State == RemoteChatState.Connected,
            "the initial load");
        return session;
    }

    // --- reflection into the private plumbing ---
    //
    // Same reasoning as SessionScanTests' OrbIds/DisplayOrder: Watch, Nudge,
    // Pump and Add are private because nothing outside this class should call
    // them directly in production, but the debounce-and-tail machinery they
    // make up is the largest untested surface in this file and none of it
    // needs a dispatcher, a watcher or a CLI to exercise — only the private
    // entry points a real watcher event or timer tick would otherwise reach.
    private static void Invoke(LocalCliChatSession session, string method, params object?[] args) =>
        typeof(LocalCliChatSession)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(session, args);

    private static void SetField(LocalCliChatSession session, string field, object? value) =>
        typeof(LocalCliChatSession)
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(session, value);

    private static T GetField<T>(LocalCliChatSession session, string field) =>
        (T)typeof(LocalCliChatSession)
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(session)!;

    // --- Start ---

    [AvaloniaFact]
    public void ATranscriptIsReadIntoHistory()
    {
        var session = Started(Transcript(
            User("u1", "fix the arrangement test"),
            Assistant("a1", "Fixed the nested-team case.")));

        Assert.Equal(2, session.History.Count);
        Assert.Equal(ChatRole.User, session.History[0].Role);
        Assert.Equal("fix the arrangement test", session.History[0].Text);
        Assert.Equal(ChatRole.Assistant, session.History[1].Role);
    }

    [AvaloniaFact]
    public void LoadingAnnouncesThatTheWholeTranscriptChanged()
    {
        var session = Session(Transcript(User("u1", "hello")));
        var replaced = 0;
        session.HistoryReplaced += () => replaced++;

        session.Start();
        PumpUntil(() => replaced > 0, "HistoryReplaced");

        Assert.Equal(1, replaced);
    }

    [AvaloniaFact]
    public void ASessionWithATranscriptReportsItselfConnected()
    {
        var session = Started(Transcript(User("u1", "hello")));

        Assert.Equal(RemoteChatState.Connected, session.State);
    }

    // The transcript path is the one field the hook can record later than the
    // rest, so a session whose first status file predates its first message has
    // none — and Start has to be a no-op rather than an error.
    [AvaloniaFact]
    public void ASessionWithNoTranscriptStaysQuiet()
    {
        var session = Session("");

        session.Start();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(session.History);
        Assert.False(session.HasMore);
        Assert.Equal(RemoteChatState.Connecting, session.State);
    }

    [AvaloniaFact]
    public void APathThatDoesNotExistIsAlsoQuiet()
    {
        var session = Session(Path.Combine(_root, "never-written.jsonl"));

        session.Start();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(session.History);
    }

    // Idempotent, because it is called from both construction-time binding and
    // every status update. A second Start must not re-read the file and double
    // the history.
    [AvaloniaFact]
    public void StartingTwiceReadsOnce()
    {
        var path = Transcript(User("u1", "one"), Assistant("a1", "two"));
        var session = Started(path);

        session.Start();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, session.History.Count);
    }

    // Rows the panel must not show. These are in every real transcript and each
    // one has its own reason — a tool result is not conversation, a sidechain is
    // a subagent's own transcript, a system reminder is not something anybody
    // said. The parsing is tested exhaustively elsewhere; what this asserts is
    // that the session hands the file to that parser rather than showing raw
    // rows.
    [AvaloniaFact]
    public void RowsThatAreNotConversationDoNotAppear()
    {
        var session = Started(Transcript(
            User("u1", "fix it"),
            "{\"type\":\"user\",\"uuid\":\"m1\",\"isMeta\":true,\"timestamp\":\"2026-08-24T12:00:00Z\","
                + "\"message\":{\"role\":\"user\",\"content\":\"hook output\"}}",
            "{\"type\":\"user\",\"uuid\":\"s1\",\"isSidechain\":true,\"timestamp\":\"2026-08-24T12:00:00Z\","
                + "\"message\":{\"role\":\"user\",\"content\":\"subagent progress\"}}",
            Assistant("a1", "done")));

        Assert.Equal(2, session.History.Count);
        Assert.DoesNotContain(session.History, t => t.Text.Contains("hook output"));
        Assert.DoesNotContain(session.History, t => t.Text.Contains("subagent progress"));
    }

    // A row repeated in the file — which happens, because the tail window can be
    // re-read — is one turn. The uuid is what makes that possible.
    [AvaloniaFact]
    public void ARepeatedRowBecomesOneTurn()
    {
        var row = User("u1", "said once");
        var session = Started(Transcript(row, row, row));

        Assert.Single(session.History);
    }

    // --- paging backwards ---

    [AvaloniaFact]
    public void AShortTranscriptHasNothingOlderToLoad()
    {
        var session = Started(Transcript(User("u1", "hello")));

        Assert.False(session.HasMore);
    }

    [AvaloniaFact]
    public async Task AskingForOlderTurnsWhenThereAreNoneAnswersFalse()
    {
        var session = Started(Transcript(User("u1", "hello")));

        Assert.False(await session.LoadOlderAsync(CancellationToken.None));
    }

    // A transcript longer than the opening window opens on its tail, and paging
    // back reaches the rest. The rows are padded so the file genuinely exceeds
    // the 512KB the session opens with, because the whole point of the window is
    // that a real transcript is far larger than what it shows.
    [AvaloniaFact]
    public async Task ALongTranscriptOpensOnItsTailAndPagesBack()
    {
        var padding = new string('x', 4000);
        var rows = Enumerable.Range(0, 200)
            .Select(i => User("u" + i, $"message {i} {padding}"))
            .ToArray();

        var session = Started(Transcript(rows));

        var opened = session.History.Count;
        Assert.True(opened > 0, "the tail should hold something");
        Assert.True(opened < rows.Length, $"the whole file should not fit: {opened} of {rows.Length}");
        Assert.True(session.HasMore, "there should be more to page back to");

        var prepended = 0;
        session.HistoryPrepended += n => prepended = n;

        Assert.True(await session.LoadOlderAsync(CancellationToken.None));

        Assert.True(prepended > 0, "paging back should report how many arrived");
        Assert.True(session.History.Count > opened, "paging back should add turns");

        // ...and the oldest turn on screen is older than it was, which is the
        // property a user actually notices.
        Assert.StartsWith("message ", session.History[0].Text);
    }

    // Paging back repeatedly reaches the beginning and then stops claiming there
    // is more — otherwise the panel offers a "load older" that never ends.
    [AvaloniaFact]
    public async Task PagingBackEventuallyReachesTheBeginning()
    {
        var padding = new string('x', 4000);
        var rows = Enumerable.Range(0, 200)
            .Select(i => User("u" + i, $"message {i} {padding}"))
            .ToArray();

        var session = Started(Transcript(rows));

        for (var i = 0; i < 40 && session.HasMore; i++)
        {
            await session.LoadOlderAsync(CancellationToken.None);
        }

        Assert.False(session.HasMore);
        Assert.Contains(session.History, t => t.Text.StartsWith("message 0 ", StringComparison.Ordinal));
    }

    // --- status updates ---

    // The title can improve after the panel opened: Claude Code writes an
    // ai-title for a conversation that did not have one yet, and a panel opened
    // before that would otherwise keep the folder name in its header for as long
    // as the app runs.
    [AvaloniaFact]
    public void ALaterTitleImprovesTheDisplayName()
    {
        var path = Transcript(User("u1", "hello"));
        var session = Started(path);

        session.UpdateStatus(new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            TranscriptPath = path,
            Title = "Fixing the arrangement",
        });

        Assert.Equal("Fixing the arrangement", session.DisplayName);
    }

    // ...and a status that has lost its title does not blank the one already
    // shown. An empty title is "not known yet", not "called nothing".
    [AvaloniaFact]
    public void AStatusWithNoTitleDoesNotBlankTheOneAlreadyKnown()
    {
        var path = Transcript(User("u1", "hello"));
        var session = Started(path);

        session.UpdateStatus(new SessionStatus
        {
            Source = SessionSource.ClaudeCode, TranscriptPath = path, Title = "Named",
        });
        session.UpdateStatus(new SessionStatus
        {
            Source = SessionSource.ClaudeCode, TranscriptPath = path, Title = "",
        });

        Assert.Equal("Named", session.DisplayName);
    }

    // A transcript path arriving late is the case Start is idempotent for: the
    // session was bound before the hook had written one, and the next status
    // update is what gets it reading.
    [AvaloniaFact]
    public void ATranscriptPathArrivingLateStartsTheSession()
    {
        var session = Session("");
        session.Start();
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(session.History);

        var path = Transcript(User("u1", "arrived late"));
        session.UpdateStatus(new SessionStatus
        {
            Source = SessionSource.ClaudeCode, TranscriptPath = path,
        });

        PumpUntil(() => session.History.Count > 0, "the late load");

        Assert.Single(session.History);
    }

    // --- the composer ---

    // "No terminal to type into" wins over the reply setting, and the precedence is
    // the interesting half: a session with nowhere to send has to say *that*
    // rather than "Replying is off", because the two have different answers —
    // one is a setting the user can change and the other is not.
    //
    // The reply-setting branch below it is deliberately not asserted here. It is
    // only reachable once the session can send quietly, which needs a real tmux
    // binary on the machine — so a test of it would pass on a developer's Mac and
    // do nothing on a runner without tmux, which is the same test passing for two
    // different reasons. The setting's own behaviour is covered where it is
    // decided, in the settings suite.
    [AvaloniaFact]
    public void ASessionWithNoPaneSaysSoRatherThanBlamingTheSetting()
    {
        var session = Session(Transcript(User("u1", "hello")));

        ClaudeBuddySettings.ClaudeCodeReplyEnabled = true;
        Assert.Equal("No terminal to type into", session.ComposerHint);

        ClaudeBuddySettings.ClaudeCodeReplyEnabled = false;
        Assert.Equal("No terminal to type into", session.ComposerHint);
    }

    // Scanned once per session rather than per keystroke, since the commands a
    // running CLI understands do not change while it runs — so asking twice must
    // give the same list rather than re-reading the disk.
    [AvaloniaFact]
    public void SlashCommandsAreScannedOnce()
    {
        var session = Session(Transcript(User("u1", "hello")));

        var first = session.SlashCommands;

        Assert.Same(first, session.SlashCommands);
    }

    [AvaloniaFact]
    public void DisposingASessionThatNeverStartedIsHarmless()
    {
        var session = Session("");

        session.Dispose();

        Assert.Empty(session.History);
    }

    [AvaloniaFact]
    public void DisposingAStartedSessionStopsItCleanly()
    {
        var session = Started(Transcript(User("u1", "hello")));

        session.Dispose();
        Dispatcher.UIThread.RunJobs();

        // The history it already read stays readable; disposing stops the
        // watcher, it does not empty the panel.
        Assert.Single(session.History);
    }

    // --- the permission prompt ---
    //
    // A prompt is the panel offering buttons that send keystrokes into a live
    // session, so a stale one is not a cosmetic problem: it is a button that
    // presses something for a dialog that has already been answered. Finding a
    // prompt means capturing a tmux pane and is excluded; the transitions around
    // one are decided here and are not.

    // Leaving "waiting" clears the prompt. Without this the buttons stay on
    // screen after the dialog is gone.
    [AvaloniaFact]
    public void LeavingTheWaitingStateClearsThePrompt()
    {
        var path = Transcript(User("u1", "hello"));
        var session = Started(path, state: "waiting");

        session.SetPrompt(new ChatPrompt("Do you want to proceed?", new[]
        {
            new ChatPromptOption("1", "Yes"),
            new ChatPromptOption("2", "No"),
        }));
        Assert.NotNull(session.Prompt);

        session.UpdateStatus(new SessionStatus
        {
            Source = SessionSource.ClaudeCode, TranscriptPath = path, State = "idle",
        });

        Assert.Null(session.Prompt);
    }

    // ...and says so, because the panel has to take the buttons down rather than
    // wait for something else to happen.
    [AvaloniaFact]
    public void ClearingThePromptIsAnnounced()
    {
        var path = Transcript(User("u1", "hello"));
        var session = Started(path, state: "waiting");

        var changes = 0;
        session.PromptChanged += () => changes++;

        session.SetPrompt(new ChatPrompt("Proceed?", Array.Empty<ChatPromptOption>()));
        Dispatcher.UIThread.RunJobs();
        var afterSetting = changes;

        session.UpdateStatus(new SessionStatus
        {
            Source = SessionSource.ClaudeCode, TranscriptPath = path, State = "idle",
        });
        Dispatcher.UIThread.RunJobs();

        Assert.True(changes > afterSetting, "clearing a prompt has to be announced");
    }

    // A status update that was not waiting and still is not raises nothing. The
    // scan runs a couple of times a second, so an update per tick would be an
    // event per tick for a panel with nothing to change.
    [AvaloniaFact]
    public void StayingNotWaitingRaisesNothing()
    {
        var path = Transcript(User("u1", "hello"));
        var session = Started(path);

        Dispatcher.UIThread.RunJobs();
        var changes = 0;
        session.PromptChanged += () => changes++;

        for (var i = 0; i < 3; i++)
        {
            session.UpdateStatus(new SessionStatus
            {
                Source = SessionSource.ClaudeCode, TranscriptPath = path, State = "idle",
            });
        }

        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, changes);
    }

    // A prompt already on screen survives another "waiting" update. Claude Code
    // commonly asks two or three permissions in a row and the state never leaves
    // "waiting" between them, so an update that cleared or re-read on every tick
    // would flicker the buttons under the pointer.
    [AvaloniaFact]
    public void APromptSurvivesAnotherWaitingUpdate()
    {
        var path = Transcript(User("u1", "hello"));
        var session = Started(path, state: "waiting");

        var prompt = new ChatPrompt("Do you want to proceed?", new[]
        {
            new ChatPromptOption("1", "Yes"),
        });
        session.SetPrompt(prompt);

        session.UpdateStatus(new SessionStatus
        {
            Source = SessionSource.ClaudeCode, TranscriptPath = path, State = "waiting",
        });

        Assert.Same(prompt, session.Prompt);
    }

    // Answering is refused when replying is off, and refused *out loud* — the
    // panel says so in the transcript rather than a button doing nothing. A send
    // that silently fails is the worst outcome a chat window can produce.
    [AvaloniaFact]
    public async Task AnsweringWithReplyingOffSaysSoInsteadOfDoingNothing()
    {
        var path = Transcript(User("u1", "hello"));
        var session = Started(path, state: "waiting");

        ClaudeBuddySettings.ClaudeCodeReplyEnabled = false;

        var before = session.History.Count;
        await session.AnswerAsync(new ChatPromptOption("1", "Yes"));
        Dispatcher.UIThread.RunJobs();

        Assert.True(session.History.Count > before, "a refusal has to be visible");
        Assert.Contains(
            session.History,
            turn => turn.Text.Contains("Replying is off", StringComparison.OrdinalIgnoreCase));
    }

    // --- watching and the live tail ---
    //
    // Nudge, OnDebounce and Pump are private because nothing outside this class
    // should call them directly — a real FileSystemWatcher event or the 2s poll
    // timer is what calls them in production — but the debounce-and-tail
    // machinery they make up needs none of that to exercise: it is bytes on
    // disk and a dispatcher, same as LoadInitialAsync above. Invoked here
    // through reflection for the same reason OrbIds and DisplayOrder are
    // reflected into in SessionScanTests.

    // Watch() is only ever reached from Start() with a path already confirmed
    // to exist, so provoking the FileSystemWatcher constructor to throw for
    // real needs a lower-level nudge than Start offers: point _transcriptPath
    // at a directory that was never created and call the private method
    // directly. "A watcher is an optimisation over the poll below, not a
    // requirement" is the claim the source comment makes; this proves the
    // catch does not also take the poll timer down with it.
    [AvaloniaFact]
    public void AWatcherThatCannotBeCreatedStillLeavesThePollRunning()
    {
        var session = Session("");
        SetField(session, "_transcriptPath", Path.Combine(_root, "never-created", "gone.jsonl"));

        Invoke(session, "Watch");

        Assert.NotNull(GetField<object?>(session, "_poll"));
    }

    // A real watcher's Changed event does exactly this: post Nudge to the UI
    // thread, which (re)starts a 150ms debounce that calls Pump once it
    // fires. The real DispatcherTimer that (re)start drives is not something
    // a headless test can wait out reliably — there is no virtual clock to
    // advance — so Nudge and the timer's own Tick handler are proved
    // separately: this covers what Nudge itself does (create, or reuse, a
    // stopped-then-restarted timer), and OnDebounce below covers what firing
    // it does.
    [AvaloniaFact]
    public void NudgeStartsADebounceTimerReadyToFire()
    {
        var session = Session(Transcript(User("u1", "hello")));

        Invoke(session, "Nudge");

        var debounce = GetField<DispatcherTimer?>(session, "_debounce");
        Assert.NotNull(debounce);
        Assert.True(debounce!.IsEnabled);
    }

    // Nudged again before it fires, the same timer is reused rather than a
    // second one being created — `_debounce ??=` is the whole of that — and
    // it stays enabled rather than compounding into several pending fires.
    [AvaloniaFact]
    public void RepeatedNudgesReuseTheSameTimerInstance()
    {
        var session = Session(Transcript(User("u1", "hello")));

        Invoke(session, "Nudge");
        var first = GetField<DispatcherTimer?>(session, "_debounce");

        Invoke(session, "Nudge");
        Invoke(session, "Nudge");
        var second = GetField<DispatcherTimer?>(session, "_debounce");

        Assert.Same(first, second);
        Assert.True(second!.IsEnabled);
    }

    // OnDebounce is the timer's own Tick handler — invoked here exactly as a
    // real fire would call it, with the sender and event args a DispatcherTimer
    // hands its subscribers — and what it does is stop the timer and pump
    // whatever Nudge was raised for.
    [AvaloniaFact]
    public void OnDebounceStopsTheTimerAndPumpsWhatWasAppended()
    {
        var path = Transcript(User("u1", "hello"));
        var session = Started(path);

        File.AppendAllText(path, Assistant("a2", "appended live") + "\n");

        Invoke(session, "Nudge");
        Invoke(session, "OnDebounce", null, EventArgs.Empty);

        Assert.False(GetField<DispatcherTimer?>(session, "_debounce")!.IsEnabled);

        PumpUntil(() => session.History.Count > 1, "the pump OnDebounce kicked off");
        Assert.Equal("appended live", session.History[^1].Text);
    }

    // The file replaced case: /clear starts a new transcript, which can be
    // shorter than wherever the tail had already read to. Carrying the old
    // offset would read from the middle of an unrelated row forever, so Pump
    // notices the file is now shorter than its offset and starts over from
    // zero.
    [AvaloniaFact]
    public void ATranscriptShorterThanTheLastOffsetIsReadFromTheStart()
    {
        var path = Transcript(Assistant("u1", "before clear " + new string('y', 200)));
        var session = Started(path);

        File.WriteAllText(path, User("u2", "after clear") + "\n");

        Invoke(session, "Pump");
        PumpUntil(() => session.History.Any(t => t.Text == "after clear"), "the restarted read");
    }

    // Mid-write or gone: Pump's own catch, provoked here by holding the file
    // open with no sharing at all so the FileStream it tries to open throws.
    // The offset must stay exactly where it was — "the poll comes back in two
    // seconds" is the design, and moving the offset on a failed read would
    // skip whatever that read was supposed to cover.
    [AvaloniaFact]
    public void APumpThatCannotOpenTheFileLeavesTheOffsetUntouched()
    {
        var path = Transcript(User("u1", "hello"));
        var session = Started(path);
        var offsetBefore = GetField<long>(session, "_offset");

        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Invoke(session, "Pump");
            PumpUntil(() => !GetField<bool>(session, "_pumping"), "the failed pump to finish");
        }

        Assert.Equal(offsetBefore, GetField<long>(session, "_offset"));
        Assert.Single(session.History);
    }

    // LoadInitialAsync's own catch: the transcript exists but cannot be
    // opened, which reports a session with nothing in it rather than leaving
    // the panel stuck on "Connecting…" forever.
    [AvaloniaFact]
    public void ATranscriptThatCannotBeOpenedForTheInitialReadStaysConnectedWithNoHistory()
    {
        var path = Transcript(User("u1", "hello"));

        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var session = Session(path);
            session.Start();
            PumpUntil(() => session.State == RemoteChatState.Connected, "the failed initial load");

            Assert.Empty(session.History);
            Assert.False(session.HasMore);
        }
    }

    // LoadOlderAsync's own catch, the other side of the same guard: a page
    // that cannot be read reports no progress rather than throwing, so the
    // panel's "load older" can be tried again instead of crashing the click.
    [AvaloniaFact]
    public async Task PagingOlderWhenTheFileCannotBeOpenedReportsNoProgress()
    {
        var padding = new string('x', 4000);
        var rows = Enumerable.Range(0, 200).Select(i => User("u" + i, $"message {i} {padding}")).ToArray();
        var path = Transcript(rows);
        var session = Started(path);
        Assert.True(session.HasMore);

        var before = session.History.Count;

        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(await session.LoadOlderAsync(CancellationToken.None));
        }

        Assert.Equal(before, session.History.Count);
    }

    // --- reconciling a sent message against the row it produces ---
    //
    // SendCoreAsync itself needs a real tmux pane to reach — CanSendQuietly is
    // false without one, which is the same environment dependency
    // ASessionWithNoPaneSaysSoRatherThanBlamingTheSetting declines to test for
    // above. Reconcile's own branches do not: they only need _pending and its
    // two candidate texts set, which is what a successful send would have left
    // behind, set here directly.

    private static void SetPending(
        LocalCliChatSession session, ChatTurn? turn, string raw, string caption, DateTimeOffset at)
    {
        SetField(session, "_pending", turn);
        SetField(session, "_pendingRaw", raw);
        SetField(session, "_pendingCaption", caption);
        SetField(session, "_pendingAt", at);
    }

    [AvaloniaFact]
    public void APendingSendOlderThanTwoMinutesIsNotReconciledAgainst()
    {
        var session = Session(Transcript(User("seed", "seed")));
        var pending = new ChatTurn { Role = ChatRole.User, Text = "typed this" };
        Invoke(session, "Add", pending);
        SetPending(session, pending, "typed this", "typed this", DateTimeOffset.Now - TimeSpan.FromMinutes(3));

        var incoming = new ChatTurn { Role = ChatRole.User, Text = "typed this" };
        Invoke(session, "Add", incoming);

        // Neither turn is discarded: a stale pending send must not swallow an
        // identical message sent again later.
        Assert.Equal(2, session.History.Count(t => t.Text == "typed this"));
        Assert.Null(GetField<ChatTurn?>(session, "_pending"));
    }

    // The pending turn is itself passed through Add on the way in for a plain
    // send, and must not reconcile against itself — "SendAsync orders things
    // so this cannot happen" is the claim in the source comment; this is the
    // belt to that braces, tested by breaking the ordering on purpose.
    [AvaloniaFact]
    public void APendingTurnDoesNotReconcileAgainstItself()
    {
        var session = Session(Transcript(User("seed", "seed")));
        var turn = new ChatTurn { Role = ChatRole.User, Text = "typed this" };
        SetPending(session, turn, "typed this", "typed this", DateTimeOffset.Now);

        Invoke(session, "Add", turn);

        Assert.Single(session.History);
        Assert.NotNull(GetField<ChatTurn?>(session, "_pending"));
    }

    [AvaloniaFact]
    public void AnAssistantTurnNeverSettlesAUsersPendingSend()
    {
        var session = Session(Transcript(User("seed", "seed")));
        var pending = new ChatTurn { Role = ChatRole.User, Text = "typed this" };
        Invoke(session, "Add", pending);
        SetPending(session, pending, "typed this", "typed this", DateTimeOffset.Now);

        var incoming = new ChatTurn { Role = ChatRole.Assistant, Text = "typed this" };
        Invoke(session, "Add", incoming);

        Assert.NotNull(GetField<ChatTurn?>(session, "_pending"));
        Assert.Contains(session.History, t => ReferenceEquals(t, incoming));
    }

    [AvaloniaFact]
    public void AUserTurnWithDifferentTextDoesNotSettleThePendingSend()
    {
        var session = Session(Transcript(User("seed", "seed")));
        var pending = new ChatTurn { Role = ChatRole.User, Text = "typed this" };
        Invoke(session, "Add", pending);
        SetPending(session, pending, "typed this", "typed this", DateTimeOffset.Now);

        var incoming = new ChatTurn { Role = ChatRole.User, Text = "something else entirely" };
        Invoke(session, "Add", incoming);

        Assert.NotNull(GetField<ChatTurn?>(session, "_pending"));
        Assert.Contains(session.History, t => ReferenceEquals(t, incoming));
    }

    // The row that comes back adopts the turn already on screen rather than
    // adding a second — matched here on the raw typed text, which is what
    // comes back verbatim when the CLI never noticed a pasted image path.
    [AvaloniaFact]
    public void ARowMatchingTheRawTypedTextSettlesThePendingSend()
    {
        var session = Session(Transcript(User("seed", "seed")));
        var pending = new ChatTurn { Role = ChatRole.User, Text = "caption only" };
        Invoke(session, "Add", pending);
        SetPending(session, pending, "caption only /tmp/pic.png", "caption only", DateTimeOffset.Now);

        var updated = 0;
        session.TurnUpdated += _ => updated++;

        var incoming = new ChatTurn { Role = ChatRole.User, Text = "caption only /tmp/pic.png" };
        Invoke(session, "Add", incoming);

        Assert.Equal(1, updated);
        Assert.Equal("caption only /tmp/pic.png", pending.Text);
        Assert.DoesNotContain(session.History, t => ReferenceEquals(t, incoming));
        Assert.Null(GetField<ChatTurn?>(session, "_pending"));
    }

    // ...and a row matching the caption alone settles it too — the other of
    // the two shapes the transcript can hand back, when the CLI did notice the
    // path and swapped it for a real picture.
    [AvaloniaFact]
    public void ARowMatchingTheCaptionAloneAlsoSettlesThePendingSend()
    {
        var session = Session(Transcript(User("seed", "seed")));
        var pending = new ChatTurn { Role = ChatRole.User, Text = "caption only" };
        Invoke(session, "Add", pending);
        SetPending(session, pending, "caption only /tmp/pic.png", "caption only", DateTimeOffset.Now);

        var updated = 0;
        session.TurnUpdated += _ => updated++;

        var incoming = new ChatTurn { Role = ChatRole.User, Text = "caption only" };
        Invoke(session, "Add", incoming);

        Assert.Equal(1, updated);
        Assert.DoesNotContain(session.History, t => ReferenceEquals(t, incoming));
        Assert.Null(GetField<ChatTurn?>(session, "_pending"));
    }

    // --- sending: the two early refusals that need no tmux at all ---
    //
    // The success path below them needs a real tmux pane, for the same
    // environment-dependency reason ComposerHint's "Message…" branch is left
    // untested — see the comment on ASessionWithNoPaneSaysSoRatherThanBlamingTheSetting.

    [AvaloniaFact]
    public async Task SendingWhileReplyingIsOffLeavesANoteInsteadOfTyping()
    {
        var before = ClaudeBuddySettings.ClaudeCodeReplyEnabled;
        try
        {
            var session = Session(Transcript(User("u1", "hi")));
            ClaudeBuddySettings.ClaudeCodeReplyEnabled = false;

            await session.SendAsync("hello there");

            Assert.Contains(
                session.History,
                t => t.Text.Contains("Replying is off", StringComparison.Ordinal));
        }
        finally
        {
            ClaudeBuddySettings.ClaudeCodeReplyEnabled = before;
        }
    }

    [AvaloniaFact]
    public async Task SendingWithNoTmuxPaneExplainsThereIsNowhereToTypeQuietly()
    {
        var before = ClaudeBuddySettings.ClaudeCodeReplyEnabled;
        try
        {
            var session = Session(Transcript(User("u1", "hi"))); // TmuxPane empty by construction
            ClaudeBuddySettings.ClaudeCodeReplyEnabled = true;

            await session.SendAsync("hello there");

            Assert.Contains(
                session.History,
                t => t.Text.Contains(TerminalTyping.CantTypePhrase, StringComparison.Ordinal));
        }
        finally
        {
            ClaudeBuddySettings.ClaudeCodeReplyEnabled = before;
        }
    }

    // SendWithImagesAsync with no images at all is just SendAsync — the same
    // refusal, reached through the other entry point.
    [AvaloniaFact]
    public async Task SendingWithNoImagesFallsBackToThePlainSend()
    {
        var before = ClaudeBuddySettings.ClaudeCodeReplyEnabled;
        try
        {
            var session = Session(Transcript(User("u1", "hi")));
            ClaudeBuddySettings.ClaudeCodeReplyEnabled = false;

            await session.SendWithImagesAsync("hello", Array.Empty<string>());

            Assert.Contains(
                session.History,
                t => t.Text.Contains("Replying is off", StringComparison.Ordinal));
        }
        finally
        {
            ClaudeBuddySettings.ClaudeCodeReplyEnabled = before;
        }
    }

    // With an image path, the caption is what SendCoreAsync sees as the
    // display text even though replying is off and it never gets past the
    // first check — proving the caption/path assembly happens before that
    // gate is asked, not that anything reaches tmux.
    [AvaloniaFact]
    public async Task SendingWithAnImagePathStillRefusesQuietlyWhenReplyingIsOff()
    {
        var before = ClaudeBuddySettings.ClaudeCodeReplyEnabled;
        try
        {
            var imagePath = Path.Combine(_root, "pic.png");
            File.WriteAllBytes(imagePath, new byte[] { 1, 2, 3 });

            var session = Session(Transcript(User("u1", "hi")));
            ClaudeBuddySettings.ClaudeCodeReplyEnabled = false;

            await session.SendWithImagesAsync("a caption", new[] { imagePath });

            Assert.Contains(
                session.History,
                t => t.Text.Contains("Replying is off", StringComparison.Ordinal));
        }
        finally
        {
            ClaudeBuddySettings.ClaudeCodeReplyEnabled = before;
        }
    }

    // A picture that vanished between being dropped and being sent — deleted,
    // or never really there — must not fail the send. "The file is still on
    // disk and the terminal still gets its path" is the claim for the normal
    // case; this is the one where that is not even true, and the send still
    // has to proceed rather than throwing out of SendWithImagesAsync.
    [AvaloniaFact]
    public async Task AMissingImageFileDoesNotStopTheSendFromProceeding()
    {
        var before = ClaudeBuddySettings.ClaudeCodeReplyEnabled;
        try
        {
            var missingPath = Path.Combine(_root, "never-written.png");

            var session = Session(Transcript(User("u1", "hi")));
            ClaudeBuddySettings.ClaudeCodeReplyEnabled = false;

            await session.SendWithImagesAsync("a caption", new[] { missingPath });

            // Still reaches SendCoreAsync and reports the same refusal a plain
            // send would — the missing thumbnail cost nothing but itself.
            Assert.Contains(
                session.History,
                t => t.Text.Contains("Replying is off", StringComparison.Ordinal));
        }
        finally
        {
            ClaudeBuddySettings.ClaudeCodeReplyEnabled = before;
        }
    }

    // --- cancelling and stepping away to the terminal ---

    [AvaloniaFact]
    public void CancellingDoesNothingWhenReplyingIsOff()
    {
        var before = ClaudeBuddySettings.ClaudeCodeReplyEnabled;
        try
        {
            var session = Session(Transcript(User("u1", "hi")));
            ClaudeBuddySettings.ClaudeCodeReplyEnabled = false;

            session.Cancel(); // must not throw, and must not touch TerminalFocuser at all
        }
        finally
        {
            ClaudeBuddySettings.ClaudeCodeReplyEnabled = before;
        }
    }

    [AvaloniaFact]
    public void CancellingWithReplyingOnButNoPaneSendsNothing()
    {
        var before = ClaudeBuddySettings.ClaudeCodeReplyEnabled;
        try
        {
            var session = Session(Transcript(User("u1", "hi"))); // TmuxPane empty
            ClaudeBuddySettings.ClaudeCodeReplyEnabled = true;

            // SendPaneKey short-circuits on CanSendQuietly before it would
            // otherwise touch tmux, exactly like the composer's own checks.
            session.Cancel();
        }
        finally
        {
            ClaudeBuddySettings.ClaudeCodeReplyEnabled = before;
        }
    }

    // Answering with nowhere to send the keystroke reports the failure and,
    // since the session is still waiting, asks the terminal again for what is
    // on screen — which, with no tmux pane to capture, comes back as an honest
    // "something is waiting and I can't say what" rather than silence.
    [AvaloniaFact]
    public async Task AnsweringWithNoPaneExplainsAndAsksAgainWhileStillWaiting()
    {
        var before = ClaudeBuddySettings.ClaudeCodeReplyEnabled;
        try
        {
            var path = Transcript(User("u1", "hello"));
            var session = Started(path, state: "waiting");
            ClaudeBuddySettings.ClaudeCodeReplyEnabled = true;

            session.SetPrompt(new ChatPrompt("Proceed?", new[] { new ChatPromptOption("1", "Yes") }));

            await session.AnswerAsync(new ChatPromptOption("1", "Yes"));
            Dispatcher.UIThread.RunJobs();

            Assert.Contains(
                session.History,
                t => t.Text.Contains("Couldn't answer that in the terminal", StringComparison.Ordinal));
        }
        finally
        {
            ClaudeBuddySettings.ClaudeCodeReplyEnabled = before;
        }
    }

    // AnswerElsewhere hands off to TerminalFocuser.Focus, which is out of
    // bounds for a headless test to actually reach — so this uses a
    // non-local-CLI status, which makes Focus's own first check return before
    // anything resembling tmux, ps or osascript runs. What this proves is that
    // AnswerElsewhere wires the call through at all, not what Focus then does.
    [AvaloniaFact]
    public void AnswerElsewhereCallsFocusWithoutReachingTerminalMachinery()
    {
        var session = new LocalCliChatSession("s1", new SessionStatus { Source = SessionSource.OpenClaw });

        session.AnswerElsewhere();
    }

    // --- a recorded transcript path that is wrong, not just late ---------------

    // The daemon respawns a finished job's worker from the job's original
    // directory, so the hook records a transcript_path computed from that
    // directory while the conversation lives in the projects directory keyed by
    // where the session actually ran. Job b0633b77 on a real machine: 3.6MB of
    // transcript, and a panel that opened blank forever, because Start treated
    // "recorded but missing" as final and never hunted.
    [AvaloniaFact]
    public void AMissingRecordedPathFallsBackToTheHunt()
    {
        var real = Transcript(User("u1", "why is this orb chat blank?"),
                              Assistant("a1", "Found it — the recorded path was wrong."));
        var recorded = Path.Combine(_root, "never-written-here.jsonl");

        var session = new LocalCliChatSession("s1", new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            TranscriptPath = recorded,
            State = "idle",
            Title = "",
        }, findTranscript: id => id == "s1" ? real : null);

        session.Start();
        PumpUntil(() => session.History.Count > 0, "the hunted transcript to load");

        Assert.Contains(session.History,
            t => t.Text.Contains("the recorded path was wrong", StringComparison.Ordinal));
    }

    // The other direction: a path that exists is never second-guessed. A hunt
    // that ran anyway could shadow the hook's own record with a stale sibling's
    // file, which is a worse wrong than the one being fixed.
    [AvaloniaFact]
    public void ARecordedPathThatExistsIsNeverHuntedPast()
    {
        var real = Transcript(User("u1", "hello"), Assistant("a1", "hi"));
        var hunted = false;

        var session = new LocalCliChatSession("s1", new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            TranscriptPath = real,
            State = "idle",
            Title = "",
        }, findTranscript: _ => { hunted = true; return null; });

        session.Start();
        PumpUntil(() => session.History.Count > 0, "the recorded transcript to load");

        Assert.False(hunted);
    }

    // And when the hunt finds nothing either, Start stays unstarted rather than
    // wedging: the next status update asks again, which is how a transcript
    // that appears late has always been picked up.
    [AvaloniaFact]
    public void AHuntThatFindsNothingLeavesStartRetryable()
    {
        var recorded = Path.Combine(_root, "never-written-here.jsonl");
        var hunts = 0;

        var session = new LocalCliChatSession("s1", new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            TranscriptPath = recorded,
            State = "idle",
            Title = "",
        }, findTranscript: _ => { hunts++; return null; });

        session.Start();
        session.Start();

        Assert.Equal(2, hunts);
        Assert.Empty(session.History);
    }

    // --- CB-105: delivering to a session with no pane, over its own registry socket ---
    //
    // SendCoreAsync's tmux path needs a real tmux binary and a real pane and
    // cannot be faked — see the comment on TypeIntoTerminalAsync. The
    // messenger path is different by design: both the SessionMessenger and
    // the registry lookup are seams on the constructor, so the whole path can
    // be driven for real against a fake Seams, the same pattern
    // SessionMessengerTests already uses for the messenger alone.
    //
    // findRegistryEntry and messengerEntries are deliberately separate rather
    // than one list handed to both: CanDeliver's own lookup (findRegistry) and
    // DeliverAsync's (the messenger's own Seams.Registry) are two different
    // reads of the same real registry in production, taken a moment apart —
    // the far session can exit, or stop speaking a protocol this build knows,
    // between the composer offering to deliver and the send actually landing.
    // Letting the two diverge in a test is what makes NoRegistryEntry and
    // UnsupportedProtocol reachable at all: both need CanDeliver to have said
    // yes a moment ago, and the messenger's own read to disagree now.
    private static SessionRegistry.Entry RegistryEntry(
        string sessionId, int peerProtocol = 1, string? status = "idle") =>
        SessionRegistry.Parse(
            $$"""
            {"pid":4242,"sessionId":"{{sessionId}}","messagingSocketPath":"/tmp/a.sock",
             "peerProtocol":{{peerProtocol}},"status":{{(status is null ? "null" : $"\"{status}\"")}}}
            """, keyPath: null)!.Value;

    private static (SessionMessenger Messenger, Func<string, SessionRegistry.Entry?> FindRegistry) FakeMessaging(
        SessionRegistry.Entry? findRegistryEntry,
        SessionRegistry.Entry[]? messengerEntries = null,
        bool write = true)
    {
        var forMessenger = messengerEntries
            ?? (findRegistryEntry is { } e ? new[] { e } : Array.Empty<SessionRegistry.Entry>());

        var messenger = new SessionMessenger(new SessionMessenger.Seams(
            Registry: () => forMessenger,
            PidAlive: _ => true,
            ReadKey: _ => null,
            Write: (_, _, _) => Task.FromResult(write)));

        Func<string, SessionRegistry.Entry?> find = id =>
            findRegistryEntry is { } found && found.SessionId == id ? found : null;

        return (messenger, find);
    }

    private static LocalCliChatSession BackgroundSession(
        string transcriptPath, SessionMessenger messenger, Func<string, SessionRegistry.Entry?> findRegistry,
        OrbPresence presence = OrbPresence.NeedsInput) =>
        new("s1", new SessionStatus
        {
            Source = SessionSource.ClaudeCode,
            TranscriptPath = transcriptPath,
            Shape = LocalSessionShape.Background,
            Presence = presence,
            State = "idle",
            Title = "job-hunter",
        }, messenger: messenger, findRegistry: findRegistry);

    // The composer hint before anything is typed: a `claude bg-spare` worker
    // with a live, speaking registry entry gets the new wording rather than
    // the daemon-attach one, because there is now somewhere for a message to
    // go even though there is still no pane.
    [AvaloniaFact]
    public void ADeliverableBackgroundSessionOffersToBeMessaged()
    {
        var (messenger, find) = FakeMessaging(RegistryEntry("s1"));
        var session = BackgroundSession(Transcript(User("seed", "seed")), messenger, find);

        Assert.Equal("Message it — it reads this at its next turn", session.ComposerHint);
    }

    // Slash commands don't survive a delivered message — see the property's
    // own comment — so the catalogue is empty rather than offering commands
    // that would be sent as plain text to a CLI that never sees them as one.
    [AvaloniaFact]
    public void SlashCommandsAreEmptyWhenTheChannelIsMessaging()
    {
        var (messenger, find) = FakeMessaging(RegistryEntry("s1"));
        var session = BackgroundSession(Transcript(User("seed", "seed")), messenger, find);

        Assert.Empty(session.SlashCommands);
    }

    // Sending it for real: the note left afterwards is DeliveryNote's own
    // Accepted wording, naming the session by its display name.
    //
    // Replying has to be turned on for any of the send tests below — the
    // first check SendCoreAsync makes, ahead of CanSendQuietly or CanDeliver —
    // and is restored afterwards, the same pattern every other send test in
    // this file already follows.
    [AvaloniaFact]
    public async Task SendingToADeliverableBackgroundSessionNotesItWasHandedOver()
    {
        var before = ClaudeBuddySettings.ClaudeCodeReplyEnabled;
        try
        {
            ClaudeBuddySettings.ClaudeCodeReplyEnabled = true;
            var (messenger, find) = FakeMessaging(RegistryEntry("s1"));
            var session = BackgroundSession(Transcript(User("seed", "seed")), messenger, find);

            await session.SendAsync("check the deploy");

            Assert.Contains(session.History,
                t => t.Text.Contains("Handed to job-hunter", StringComparison.Ordinal));
        }
        finally
        {
            ClaudeBuddySettings.ClaudeCodeReplyEnabled = before;
        }
    }

    // The row Claude Code eventually writes back is the whole
    // <cross-session-message> tag it was handed, not the bare text — so this
    // proves the send settles against that row rather than leaving the
    // message pending forever and showing it a second time when the row
    // arrives.
    [AvaloniaFact]
    public async Task ADeliveredMessageSettlesWhenTheWrappedRowArrivesInTheTranscript()
    {
        var before = ClaudeBuddySettings.ClaudeCodeReplyEnabled;
        try
        {
            ClaudeBuddySettings.ClaudeCodeReplyEnabled = true;
            var (messenger, find) = FakeMessaging(RegistryEntry("s1"));
            var session = BackgroundSession(Transcript(User("seed", "seed")), messenger, find);

            await session.SendAsync("check the deploy");
            var beforeEcho = session.History.Count;

            var updated = 0;
            session.TurnUpdated += _ => updated++;

            var wrapped = "<cross-session-message from=\"Claude Buddy on mini\" from-mode=\"prompting\">\n"
                          + "check the deploy\n</cross-session-message>";
            var incoming = new ChatTurn { Role = ChatRole.User, Text = wrapped };
            Invoke(session, "Add", incoming);

            Assert.Equal(1, updated);
            Assert.Equal(beforeEcho, session.History.Count);
            Assert.DoesNotContain(session.History, t => ReferenceEquals(t, incoming));
        }
        finally
        {
            ClaudeBuddySettings.ClaudeCodeReplyEnabled = before;
        }
    }

    // The far session is no longer in the registry at all by the time the
    // send actually runs — it may have exited a moment after the composer
    // offered to deliver — and the note points at the one thing that still
    // works: attaching it.
    [AvaloniaFact]
    public async Task SendingWhenTheFarSessionHasLeftTheRegistrySaysSo()
    {
        var before = ClaudeBuddySettings.ClaudeCodeReplyEnabled;
        try
        {
            ClaudeBuddySettings.ClaudeCodeReplyEnabled = true;
            var (messenger, find) = FakeMessaging(
                findRegistryEntry: RegistryEntry("s1"), messengerEntries: Array.Empty<SessionRegistry.Entry>());
            var session = BackgroundSession(Transcript(User("seed", "seed")), messenger, find);

            await session.SendAsync("check the deploy");

            Assert.Contains(session.History,
                t => t.Text.Contains("isn't registered", StringComparison.Ordinal));
        }
        finally
        {
            ClaudeBuddySettings.ClaudeCodeReplyEnabled = before;
        }
    }

    // The far session now speaks a peer protocol this build doesn't
    // recognize — newer than what CanDeliver saw a moment ago.
    [AvaloniaFact]
    public async Task SendingWhenTheFarSessionSpeaksAnUnsupportedProtocolSaysSo()
    {
        var before = ClaudeBuddySettings.ClaudeCodeReplyEnabled;
        try
        {
            ClaudeBuddySettings.ClaudeCodeReplyEnabled = true;
            var (messenger, find) = FakeMessaging(
                findRegistryEntry: RegistryEntry("s1"),
                messengerEntries: new[] { RegistryEntry("s1", peerProtocol: 2) });
            var session = BackgroundSession(Transcript(User("seed", "seed")), messenger, find);

            await session.SendAsync("check the deploy");

            Assert.Contains(session.History,
                t => t.Text.Contains("peer protocol", StringComparison.Ordinal));
        }
        finally
        {
            ClaudeBuddySettings.ClaudeCodeReplyEnabled = before;
        }
    }

    // The socket itself refuses the connection — nothing session-specific
    // learned, so the note is the generic one rather than naming the session.
    [AvaloniaFact]
    public async Task SendingWhenTheSocketRefusesSaysNothingWasSent()
    {
        var before = ClaudeBuddySettings.ClaudeCodeReplyEnabled;
        try
        {
            ClaudeBuddySettings.ClaudeCodeReplyEnabled = true;
            var (messenger, find) = FakeMessaging(RegistryEntry("s1"), write: false);
            var session = BackgroundSession(Transcript(User("seed", "seed")), messenger, find);

            await session.SendAsync("check the deploy");

            Assert.Contains(session.History,
                t => t.Text.Contains("refused the connection", StringComparison.Ordinal));
        }
        finally
        {
            ClaudeBuddySettings.ClaudeCodeReplyEnabled = before;
        }
    }
}
