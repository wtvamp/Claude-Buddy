using Avalonia.Headless.XUnit;

namespace ClaudeBuddy.Tests;

// What a remote session's panel actually looks like in each of its two modes.
//
// Worth capturing rather than asserting on strings alone, because the thing
// being fixed here is a *reading* problem: the old panel showed a model's
// summary of a conversation in a window that looked exactly like the one
// showing a real conversation, and nobody could tell which they were looking
// at. Whether the two now read differently at a glance is a question about
// pixels, so it belongs in the suite that renders them.
//
// The far Buddy is the real RemoteMirrorServer reading a real file in a temp
// directory, reached through the real client and protocol — only the relay is
// faked, same seam as MirrorRoundTripTests.
public class RemoteMirrorPanelScreenshots : IDisposable
{
    private const string Account = ".claude-board";
    private const string Name = "job-hunter";
    private const string FarRelay = "claude-buddy-rc--claude-mini";
    private const string NearRelay = "claude-buddy-rc--claude-laptop";

    private readonly string _dir;
    private readonly List<string> _sessionIdsToClean = new();
    private readonly bool _remoteWasEnabled;

    private readonly List<(string SessionId, SessionStatus Status)> _sessions = new();
    private readonly List<AgentRoster.Entry> _agents = new();

    private RemoteMirrorServer _server = null!;
    private RemoteMirrorClient _client = null!;

    public RemoteMirrorPanelScreenshots()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cb-shot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        _remoteWasEnabled = ClaudeBuddySettings.RemoteControlEnabled;
        ClaudeBuddySettings.RemoteControlEnabled = true;
    }

    public void Dispose()
    {
        foreach (var id in _sessionIdsToClean) ChatPanel.HideFor(id);

        ClaudeBuddySettings.RemoteControlEnabled = _remoteWasEnabled;
        RemoteControlSessions.ResetForTests();

        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // Deliberately never closed — same reason as ChatPanelScreenshots: closing a
    // headless Window corrupts a process-wide FontManager cache for every window
    // built afterward in this run.
    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    // The fix, on screen: the far session's own conversation, with a line at the
    // top saying that is what it is and that typing goes into its terminal.
    [AvaloniaFact]
    public async Task ALiveViewShowsTheFarSessionsOwnConversation()
    {
        // Deliberately short enough that the panel does not scroll.
        //
        // The first version of this was a four-turn conversation, and it cost
        // the screenshot both of the things it exists to show: the banner
        // explaining what a live view *is* scrolled off the top, and on the
        // Windows runner the last bubble captured empty — laid out but not yet
        // measured, because ScrollToEnd posts at Loaded priority and the capture
        // does not wait for the text to arrive. A conversation that fits needs
        // no scroll, so there is no race and the banner stays on screen.
        //
        // A slash command is the conversation on purpose: it is the half of this
        // bug a picture can actually show.
        Wire(
            ("user", "/color green"),
            ("assistant", "Set — this session's orb is green now."));

        var session = Open();
        await _client.DiscoverAsync(Peers, new[] { Name });

        ChatPanel.OpenFor(NewOrb(), session);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-remote-live-view.png");
    }

    // The fallback, and the reason it has to look different: there is no Buddy on
    // the other machine, so what arrives here is written *for* the reader by a
    // model rather than read off a disk. The panel says so instead of letting it
    // pass for a transcript.
    [AvaloniaFact]
    public async Task WithNoBuddyOverThereThePanelSaysItIsOnlyAMessagingChannel()
    {
        WireEmpty();

        var session = Open();

        await _client.DiscoverAsync(
            Array.Empty<string>(),
            new[] { Name });

        ChatPanel.OpenFor(NewOrb(), session);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-remote-no-live-view.png");
    }

    // A transfer that failed its integrity check. Nothing of it is shown — which
    // is the point, and is also why this is worth a picture: an empty panel with
    // an explanation is what "we refused to show you something we could not
    // verify" has to look like.
    [AvaloniaFact]
    public async Task AMirrorThatFailedItsIntegrityCheckShowsTheRefusal()
    {
        Wire(
            ("user", "what did it say?"),
            ("assistant", "something that will not survive the trip"));

        _mangle = true;

        var session = Open();
        await _client.DiscoverAsync(Peers, new[] { Name });

        ChatPanel.OpenFor(NewOrb(), session);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-remote-integrity-refusal.png");
    }

    // The four minutes nobody could see.
    //
    // A mirrored panel's opening window is carried by a model retyping base64 by
    // hand — 222, 231, 234, 247 and 192 seconds, measured on real machines — and
    // until CB-58 the panel showed nothing moving for the whole of it. Reported
    // twice as a hang that was not one.
    //
    // Captured at the start of the wait, so the elapsed figure reads near zero:
    // that is the honest first frame rather than a staged one, and what the
    // picture is for is the surface — an indeterminate bar, what is being
    // fetched, and the line saying how long these usually take. There is no
    // percentage on purpose; a single-chunk transfer has no progress to report,
    // and an invented one would park at 99% and make a healthy fetch look stuck.
    [AvaloniaFact]
    public async Task AMirrorStillFetchingShowsHowLongItHasBeenWaiting()
    {
        Wire(
            ("user", "what is it working on?"),
            ("assistant", "still reading the transcript across"));

        _swallowFetch = true;

        var session = Open();
        await _client.DiscoverAsync(Peers, new[] { Name });

        ChatPanel.OpenFor(NewOrb(), session);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-remote-fetching.png");
    }

    // The other shape a live view can be in: a background or agent-mode job
    // with no pane at all, where this machine can still hand it text over its
    // own messaging socket. The composer has to say something different from
    // both "type into…" and plain "message…" here, or a headless session
    // reads as unreachable when it is not — CB-105.
    [AvaloniaFact]
    public async Task ALiveViewWithNoPaneOffersToMessageTheBackgroundJob()
    {
        Wire(canType: false, canDeliver: true,
            ("user", "still going?"),
            ("assistant", "yes — deploying now"));

        var session = Open();
        await _client.DiscoverAsync(Peers, new[] { Name });

        ChatPanel.OpenFor(NewOrb(), session);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-remote-live-view-deliverable.png");
    }

    // --- wiring -------------------------------------------------------------------

    private bool _mangle;

    // Swallows the FETCH so the request stays pending and the panel keeps
    // drawing its wait. The real wait is three or four minutes of a model
    // retyping base64; a screenshot cannot sit through one, and holding the
    // frame at the door is the same trick MirrorEdgeCaseTests uses to make
    // "in flight" a fact rather than a race.
    private bool _swallowFetch;
    private string _path = "";

    private RemoteControlChatSession Open()
    {
        var id = $"rc:{Account}:{Name}";
        _sessionIdsToClean.Add(id);

        var session = new RemoteControlChatSession(id, Account, Name);
        session.PanelOpened();
        return session;
    }

    // The machines to ask, by name. The relay-shaped list this replaced had
    // to be filtered down to the same answer a direct link simply knows.
    private static IReadOnlyList<string> Peers => new[] { FarRelay };
    private void Wire(params (string Role, string Text)[] turns) => Wire(true, false, turns);

    // CB-105's shape: a session with no pane to type into but a machine that
    // can hand it text over its own messaging socket instead.
    private void Wire(bool canType, bool canDeliver, params (string Role, string Text)[] turns)
    {
        _path = Path.Combine(_dir, "session.jsonl");

        var rows = turns.Select((t, i) => t.Role == "user"
            ? $"{{\"type\":\"user\",\"uuid\":\"u{i}\",\"message\":{{\"role\":\"user\",\"content\":\"{t.Text}\"}}}}"
            : $"{{\"type\":\"assistant\",\"uuid\":\"a{i}\",\"message\":{{\"role\":\"assistant\",\"content\":[{{\"type\":\"text\",\"text\":\"{t.Text}\"}}]}}}}");

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

        Build(canType, canDeliver);
    }

    private void WireEmpty() => Build(true, false);

    private void Build(bool canType = true, bool canDeliver = false)
    {
        _server = new RemoteMirrorServer(Account, new RemoteMirrorServer.Seams(
            SendToClientAsync,
            () => _sessions,
            () => _agents,
            _ => true,
            _ => canType,
            (_, _) => Task.FromResult(true),
            CanDeliver: status => canDeliver,
            Deliver: (status, text) =>
                Task.FromResult(new DeliveryReceipt(DeliveryResult.Accepted, "idle"))));

        _client = new RemoteMirrorClient(Account, new RemoteMirrorClient.Seams(SendToServerAsync));
        RemoteControlSessions.UseMirrorClientForTests(Account, _client);
    }

    private async Task<bool> SendToServerAsync(string peer, string line)
    {
        var frame = MirrorProtocol.TryParseFrame(line);
        if (frame is null) return false;

        // Accepted onto the wire and never answered, which is exactly what a
        // transfer in progress looks like from this side.
        if (_swallowFetch && frame.Type == MirrorProtocol.Fetch) return true;

        await _server.HandleAsync(NearRelay, frame);
        return true;
    }

    private async Task<bool> SendToClientAsync(string peer, string line)
    {
        var frame = MirrorProtocol.TryParseFrame(line);
        if (frame is null) return false;

        // Windows only, so the handshake still succeeds and the failure lands
        // where this is about.
        if (_mangle && frame.Type == MirrorProtocol.Chunk && frame.Get("wfrom") is not null)
        {
            var start = line.IndexOf(";p=", StringComparison.Ordinal);
            var end = line.IndexOf(";h=", StringComparison.Ordinal);

            if (start >= 0 && end >= 0)
            {
                var swapped = Convert.ToBase64String(
                    System.Text.Encoding.UTF8.GetBytes("a tidier version of that"));

                frame = MirrorProtocol.TryParseFrame(line[..(start + 3)] + swapped + line[end..]) ?? frame;
            }
        }

        await _client.OnFrameAsync(FarRelay, frame);
        return true;
    }
}
