using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace ClaudeBuddy.Tests;

// One capture per scenario in tests/UiTests/ChatPanelTests.cs. Same
// singleton-cleanup rule as that suite: ChatPanel is one window shared by
// every test in the process, so each test here calls HideFor its own
// session id when done rather than relying on process isolation.
public class ChatPanelScreenshots : IDisposable
{
    private readonly List<string> _sessionIdsToClean = new();

    // displayName defaults to the placeholder every capture before this one
    // used, so none of them changes. It is worth overriding wherever the
    // *picture* names the conversation somewhere else: a header reading "Fake
    // Session" above a note about "#lobby" is two different answers to "what am
    // I looking at" inside the one artifact reviewers actually open, and the
    // fake's name costs nothing to set.
    private FakeChatSession NewFake(
        IEnumerable<ChatTurn>? history = null, string displayName = "Fake Session")
    {
        var id = "screenshot-" + Guid.NewGuid();
        _sessionIdsToClean.Add(id);
        return new FakeChatSession(history) { SessionId = id, DisplayName = displayName };
    }

    // Deliberately never closed — same reason as tests/UiTests's ChatPanelTests:
    // closing a headless Window here corrupts a process-wide FontManager
    // cache for every window built afterward in this run.
    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    // An orb whose session the gateway's heartbeat drives. The panel reads the
    // flag off the orb rather than the session (see ChatPanel.Bind), so the
    // status has to go through UpdateFrom to reach the chip.
    private static OrbWindow NewHeartbeatOrb()
    {
        var orb = NewOrb();
        orb.UpdateFrom(new SessionStatus
        {
            State = "idle",
            Cwd = "/Users/test/project",
            Title = "",
            Color = "",
            Cli = "",
            Kind = SessionKind.Channel,
            Heartbeat = true,
        });

        return orb;
    }

    [AvaloniaFact]
    public void AHeartbeatChatWearsABeatingHeartChipInItsHeader()
    {
        var fake = NewFake(new[]
        {
            new ChatTurn { Role = ChatRole.Assistant, Text = "No response requested." },
        });

        ChatPanel.OpenFor(NewHeartbeatOrb(), fake);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-heartbeat-chip.png");
    }

    // The panel for a session there is nowhere to type into: the box says what it
    // is waiting for, and there is a gear beside it that opens the roster where
    // it can be answered. Captured
    // because it is a new visible surface, and because the thing worth reviewing
    // is a judgement — whether the box's wording and one small button are enough
    // for someone who clicked a grey orb expecting to be able to talk to it.
    [AvaloniaFact]
    public void AParkedSessionsPanelSaysSoAndOffersTheView()
    {
        var fake = NewFake(new[]
        {
            new ChatTurn { Role = ChatRole.User, Text = "merge the open PRs" },
            new ChatTurn { Role = ChatRole.Assistant, Text = "Done — three merged, one had conflicts." },
        });

        fake.ComposerHint = "Needs input — attach to reply";
        fake.CanOpenElsewhere = true;

        ChatPanel.OpenFor(NewOrb(), fake);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-parked-attach.png");
    }

    public void Dispose()
    {
        foreach (var id in _sessionIdsToClean) ChatPanel.HideFor(id);
    }

    [AvaloniaFact]
    public void OpenForRendersOneRowPerHistoryTurnWithMatchingText()
    {
        var fake = NewFake(new[]
        {
            new ChatTurn { Role = ChatRole.User, Text = "hi there" },
            new ChatTurn { Role = ChatRole.Assistant, Text = "hello back" },
            new ChatTurn
            {
                Role = ChatRole.Assistant,
                Text = "room message",
                Speaker = "Zara",
                SpeakerColor = "#00AF5F"
            },
        });

        ChatPanel.OpenFor(NewOrb(), fake);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-three-turns.png");
    }

    [AvaloniaFact]
    public void MarkdownTurnRendersAsStyledRunsNotLiteralMarkup()
    {
        var fake = NewFake(new[]
        {
            new ChatTurn { Role = ChatRole.Assistant, Text = "**bold** and `code`" },
        });

        ChatPanel.OpenFor(NewOrb(), fake);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-markdown-turn.png");
    }

    [AvaloniaFact]
    public void TurnWithSpeakerShowsTheSpeakersNameOnItsRow()
    {
        var fake = NewFake(new[]
        {
            new ChatTurn
            {
                Role = ChatRole.Assistant,
                Text = "room message",
                Speaker = "Zara",
                SpeakerColor = "#00AF5F"
            },
        });

        ChatPanel.OpenFor(NewOrb(), fake);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-speaker-turn.png");
    }

    [AvaloniaFact]
    public void TypingAndPressingEnterSendsTheTypedTextAndClearsTheBox()
    {
        var fake = NewFake();
        ChatPanel.OpenFor(NewOrb(), fake);
        ScreenshotHelper.Flush();
        var panel = ChatPanelTestAccess.Instance!;
        var input = panel.FindControl<TextBox>("Input")!;

        input.Focus();
        ScreenshotHelper.Flush();
        input.RaiseEvent(new TextInputEventArgs
        {
            RoutedEvent = InputElement.TextInputEvent,
            Text = "hello from a test"
        });
        ScreenshotHelper.Flush();

        // Captured before Enter, deliberately: this is the one moment the
        // source test's own assertions distinguish (typed-but-not-sent vs.
        // sent-and-cleared) that a screenshot can actually show — after
        // Enter, the box is empty and the panel looks identical to the
        // three-turns capture above plus one more row.
        ScreenshotHelper.CaptureAlreadyShown(panel, "chat-panel-typed-input-before-enter.png");
    }

    // Bytes that are not a picture, which is the one thing this suite can test
    // that tests/UiTests cannot: Avalonia's headless render interface answers
    // DecodeToWidth with a stub of the requested size for any input at all, so
    // the panel's "not an image" path is unreachable under the null renderer
    // and reachable here, where Skia is real and throws.
    //
    // Worth having because a local CLI's transcript is a file this app does not
    // write, and a half-flushed image block is a normal thing to read out of
    // one. The message keeps its text; only the picture is missing.
    [AvaloniaFact]
    public void ATurnWhoseImageBytesDoNotDecodeStillShowsItsText()
    {
        var fake = NewFake(new[]
        {
            new ChatTurn
            {
                Role = ChatRole.User,
                Text = "a screenshot",
                IsComplete = true,
                ImageBytes = new byte[] { 0x4E, 0x4F, 0x50, 0x45, 0x21, 0x21, 0x21, 0x21 }
            }
        });

        ChatPanel.OpenFor(NewOrb(), fake);

        // The decode runs on a worker and its failure is swallowed on the way
        // back, so the frames are what prove the panel survived it rather than
        // stopped drawing partway through.
        for (var i = 0; i < 40; i++) ScreenshotHelper.Flush();

        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-undecodable-image.png");
    }

    // CB-93: a picture the gateway refused, with the reason shown rather than
    // a bare MEDIA: line and no explanation. One ordinary turn above it for
    // contrast, so the judgement worth reviewing is legible — does the note
    // read as an aside about this one message, or does it compete with the
    // conversation around it.
    [AvaloniaFact]
    public void APictureTheGatewayRefusedShowsWhy()
    {
        var fake = NewFake(new[]
        {
            new ChatTurn { Role = ChatRole.User, Text = "can you drop the render in here?" },
            new ChatTurn
            {
                Role = ChatRole.Assistant,
                Text = "here you go\n\nMEDIA:/Users/warrenthompson/.openclaw/workspace-comfyui-zara/outputs/render.png",
                ImageNote = "Picture not shown — the gateway won't serve files from that folder. "
                          + "Ask the agent to write it to ~/.openclaw/media/, which is allowed for every agent.",
                ImageNoteDetail = "/Users/warrenthompson/.openclaw/workspace-comfyui-zara/outputs/render.png"
                                + " — outside-allowed-folders",
            },
        });

        ChatPanel.OpenFor(NewOrb(), fake);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-picture-refused.png");
    }

    // A room with everyone in it drawn as themselves — the whole of CB-27 in one
    // picture, and the reason it is a capture rather than only an assertion.
    //
    // Four kinds of turn, and the judgement worth reviewing is whether they read
    // as four different people at a glance rather than as four grey bubbles:
    //
    //   * Yours, in your own blue on the right. Before this, a message you sent
    //     to a channel came back as an anonymous grey bubble on the left,
    //     because the copies in the members' transcripts are user-role like
    //     everybody else's.
    //   * An agent in the room, in its own colour, matched to the ring on its
    //     orb.
    //   * Somebody the gateway named but this app cannot match to an agent — a
    //     relayed bot, or another person in the channel. Named, and deliberately
    //     uncoloured: a Discord display name is not an agent id, and a borrowed
    //     colour would say two speakers were one. The initials chip is what that
    //     honesty looks like, and whether it reads as deliberate rather than as
    //     a missing colour is exactly the thing a screenshot settles and a test
    //     cannot.
    //   * The room's own anonymous voice, drawn when the gateway said nothing
    //     about who sent a message. This is the *degraded* rendering and it is
    //     deliberately still here: the whole attribution rule falls back to it
    //     rather than guessing. In the capture because it is the arm most likely
    //     to regress without anyone noticing — nothing else in any suite draws
    //     it, and a change that started attributing these would look like an
    //     improvement in every test and like the app asserting something false
    //     on screen.
    //
    //     It wears the room's own name on its chip — "#lobby" — which looks
    //     wrong and is what a real room genuinely draws. Verified against one
    //     rather than assumed: the panel falls back to the session's sole
    //     speaker for an unattributed assistant turn, and for a room that
    //     resolves to the title, because a room has no agent identity behind
    //     its session key. ChatSpeaker's own comment already admits the title is
    //     "the wrong one for a room". It predates this branch — ChatSpeaker.cs
    //     and ChatPanel.axaml.cs are untouched here — and this branch makes it
    //     rarer rather than worse, since the turns it now attributes properly
    //     are ones that used to land in exactly this bucket. Captured as it is,
    //     rather than staged to look better than the app does.
    //   * A failure note, which is what a send with nowhere to go now leaves
    //     behind instead of silence.
    [AvaloniaFact]
    public void ARoomDrawsEveryoneInItAsThemselves()
    {
        var fake = NewFake(new[]
        {
            new ChatTurn
            {
                Role = ChatRole.User,
                Text = "anyone free to look at the build?",
                IsComplete = true,
                Mine = true
            },
            new ChatTurn
            {
                Role = ChatRole.Assistant,
                Text = "Taking it now — the arm64 leg is the slow one.",
                IsComplete = true,
                Speaker = "Quill",
                SpeakerColor = "#00AF5F"
            },
            new ChatTurn
            {
                Role = ChatRole.Assistant,
                Text = "Nodes are loaded, so it should be quick.",
                IsComplete = true,
                Speaker = "Thistle"
            },
            new ChatTurn
            {
                Role = ChatRole.Assistant,
                Text = "Anyone know if the runner picked that up?",
                IsComplete = true
            },
            new ChatTurn
            {
                Role = ChatRole.System,
                Text = "Couldn't post to #lobby: no member of this channel carries "
                     + "a delivery address.",
                IsComplete = true
            },
        }, displayName: "#lobby");

        ChatPanel.OpenFor(NewOrb(), fake);
        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-room-attribution.png");
    }

    // A passage held selected in a reply.
    //
    // Captured because the selection is the whole feature and the only part of
    // it a reviewer cannot check any other way: the tests can prove the right
    // characters end up on the clipboard, but not that the highlight can
    // actually be read. That is a judgement about one colour sitting on a
    // tinted bubble, and it is the reason SelectionFill is a translucent white
    // rather than the solid system highlight — which is exactly the kind of
    // choice a picture settles and a passing assertion does not.
    [AvaloniaFact]
    public void ASelectedPassageIsHighlightedInTheReply()
    {
        var fake = NewFake(new[]
        {
            new ChatTurn
            {
                Role = ChatRole.User,
                Text = "where does the hook write its status files?",
                IsComplete = true,
            },
            new ChatTurn
            {
                Role = ChatRole.Assistant,
                Text = "They go to `$TMPDIR/claude-buddy`, one file per session.\n\n"
                     + "Drag across any of this to select it, then copy.",
                IsComplete = true,
            },
        });

        ChatPanel.OpenFor(NewOrb(), fake);
        ScreenshotHelper.Flush();

        // The selection a person would have made by dragging. Set directly
        // rather than synthesized, because a headless drag would be testing
        // Avalonia's hit-testing rather than this app's rendering — and it is
        // the rendering the capture exists to show.
        var panel = ChatPanelTestAccess.Instance!;
        var line = panel.FindControl<ItemsControl>("Turns")!
            .GetVisualDescendants()
            .OfType<SelectableTextBlock>()
            .First(b => Text(b).StartsWith("They go to", StringComparison.Ordinal));

        line.SelectionStart = "They go to ".Length;
        line.SelectionEnd = "They go to $TMPDIR/claude-buddy".Length;

        ScreenshotHelper.Flush();
        ScreenshotHelper.CaptureAlreadyShown(panel, "chat-panel-selected-text.png");
    }

    // A styled line keeps its words in Inlines and leaves Text null.
    private static string Text(TextBlock block)
    {
        if (!string.IsNullOrEmpty(block.Text)) return block.Text!;
        if (block.Inlines is null) return "";

        return string.Concat(
            block.Inlines.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));
    }
    // Where a long transcript sits when you open it, which every capture above
    // is silent about — they all hold a handful of turns and so never overflow
    // the panel at all.
    //
    // Worth a picture rather than only the assertions in tests/UiTests'
    // ChatPanelScrollTests, because the fault it covers was reported by eye and
    // is read back by eye: the numbers in an offset-versus-extent assertion say
    // the scroll viewer is at its end, and the image says the newest message is
    // the one you are looking at. It is also the one part of this fix whose
    // behaviour is worth confirming per platform — the two-priority yield it
    // depends on is dispatcher and layout timing, and the capture runs on both
    // runners.
    //
    // Staged the way the report describes rather than on a fresh panel: a first
    // session read part way up, then a second one opened over it. The panel is a
    // process-wide singleton, so the offset left behind by the first is exactly
    // what the second used to inherit.
    [AvaloniaFact]
    public void ALongTranscriptOpensOnItsNewestTurn()
    {
        var read = NewFake(LongTranscript("an earlier conversation"));
        ChatPanel.OpenFor(NewOrb(), read);
        ScreenshotHelper.Flush();

        // Part way up the first transcript, where reading back through it
        // leaves you.
        ChatPanelTestAccess.Instance!.Scroll.Offset = new Vector(0, 300);
        ScreenshotHelper.Flush();

        var opened = NewFake(LongTranscript("this conversation"), displayName: "Long Session");
        ChatPanel.OpenFor(NewOrb(), opened);

        // Several, because the scroll deliberately settles across two dispatcher
        // priorities with a measure between them, and a capture taken before it
        // has finished is a picture of the bug rather than of the fix.
        for (var i = 0; i < 8; i++) ScreenshotHelper.Flush();

        ScreenshotHelper.CaptureAlreadyShown(
            ChatPanelTestAccess.Instance!, "chat-panel-opens-at-newest-turn.png");
    }

    // Numbered, and with the last turn saying so, because the whole point of the
    // image is which end of the transcript is on screen — and "some chat
    // bubbles" looks the same at either end.
    private static List<ChatTurn> LongTranscript(string what) =>
        Enumerable.Range(0, 60).Select(i => new ChatTurn
        {
            Role = i % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
            Text = i == 59
                ? $"Message 60 of {what} — the newest turn, and the one you should be looking at."
                : $"Message {i + 1} of {what}, somewhere above the fold.",
            IsComplete = true,
        }).ToList();

    // The panel after Cmd+ has been pressed a few times.
    //
    // The tests assert the numbers; this is the half a number cannot answer.
    // Whether an enlarged conversation is still a conversation — whether the
    // bubbles still read as two people talking, whether a heading is still a
    // heading beside its prose, whether a wrapped code block at 1.75x has
    // anything left of its line — is a judgement, and it is made by looking.
    // Both runners capture it, so a font that falls back differently on
    // Windows shows up here rather than on someone's machine.
    [AvaloniaFact]
    public void AnEnlargedPanelIsStillAReadableConversation()
    {
        var was = ClaudeBuddySettings.ChatTextScale;

        try
        {
            ClaudeBuddySettings.ChatTextScale = 1.75;

            var fake = NewFake(new[]
            {
                new ChatTurn { Role = ChatRole.User, Text = "why is the build red?" },
                new ChatTurn
                {
                    Role = ChatRole.Assistant,
                    Text = "## One test\n\n`ArrangementSweep` fails at the widest spacing:\n\n"
                         + "```\nExpected: 1.15\nActual:   1.0\n```\n\n"
                         + "- the ladder is uneven\n- the tick was not",
                    IsComplete = true
                },
                new ChatTurn { Role = ChatRole.System, Text = "Session went idle." },
            }, displayName: "Build");

            ChatPanel.OpenFor(NewOrb(), fake);

            // The panel is a singleton and may already have been built by an
            // earlier capture, in which case its constructor's ApplyTextScale
            // ran against the old size. This is the same hook the settings
            // slider uses.
            ChatPanel.ReapplyTextScale();
            ScreenshotHelper.Flush();
            ScreenshotHelper.CaptureAlreadyShown(
                ChatPanelTestAccess.Instance!, "chat-panel-text-enlarged.png");
        }
        finally
        {
            // Every other capture in this assembly draws at the shipped size,
            // and the suite shares one process.
            ClaudeBuddySettings.ChatTextScale = was;
            ChatPanel.ReapplyTextScale();
        }
    }
}
