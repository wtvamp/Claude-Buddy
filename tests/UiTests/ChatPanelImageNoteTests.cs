using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-93: the note line a refused picture leaves in the slot the image would
// have occupied, and its tooltip. Same singleton-cleanup rule as
// ChatPanelTests: ChatPanel is one window shared by every test in the
// process, so each test here HideFor's its own session id when done rather
// than relying on process isolation.
[Collection("Settings")]
public class ChatPanelImageNoteTests : IDisposable
{
    private readonly List<string> _sessionIdsToClean = new();

    private FakeChatSession NewFake(IEnumerable<ChatTurn>? history = null)
    {
        var id = "note-" + Guid.NewGuid();
        _sessionIdsToClean.Add(id);
        return new FakeChatSession(history) { SessionId = id, DisplayName = "Fake Session" };
    }

    // Never closed, for the same reason as ChatPanelTests.NewOrb: closing a
    // headless OrbWindow corrupts a process-wide FontManager cache shared
    // with every window built afterward in this run.
    private static OrbWindow NewOrb() => new(Guid.NewGuid().ToString());

    public void Dispose()
    {
        foreach (var id in _sessionIdsToClean) ChatPanel.HideFor(id);
    }

    private static void Flush() => Dispatcher.UIThread.RunJobs();

    // The note TextBlock among a row's controls, found by the one Opacity
    // value ChatPanel.axaml gives only this element — 0.6, where TimeText is
    // 0.5 and everything else in the per-turn template leaves Opacity at its
    // default 1. There is no per-row x:Name to search by instead: the
    // template is instantiated once per turn, and ChatPanelTests' own
    // ATurnWithImageBytesRendersAsAThumbnail already matches this way for the
    // same reason — there it is an Image's fixed width, here it is this.
    private static TextBlock? NoteTextBlockIn(Avalonia.Controls.Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(tb => Math.Abs(tb.Opacity - 0.6) < 0.001);

    [AvaloniaFact]
    public void ATurnConstructedWithAnImageNoteRendersTheLine()
    {
        const string note = "Picture not shown — the gateway refused it.";

        var turn = new ChatTurn { Role = ChatRole.Assistant, Text = "MEDIA:/x/y.png", ImageNote = note };
        var fake = NewFake(new[] { turn });

        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var block = NoteTextBlockIn(ChatPanelTestAccess.Instance!);

        Assert.NotNull(block);
        Assert.True(block!.IsVisible);
        Assert.Equal(note, block.Text);
    }

    // The live case, and the exact bug shape that already bit ImageUrl and
    // ImageBytes: OpenClawChatSession.TryResolveLocalMedia and
    // TurnView.LoadImage both resolve asynchronously, well after the row for
    // that turn already exists on screen — see the !HasImage guards in
    // ChatPanel.axaml.cs's own PropertyChanged hook, which exist because of
    // exactly this ordering. ImageNote has to notice the same way rather than
    // only being read once at construction.
    [AvaloniaFact]
    public void ATurnThatGainsANoteAfterItsRowExistsShowsIt()
    {
        var turn = new ChatTurn { Role = ChatRole.Assistant, Text = "MEDIA:/x/y.png" };
        var fake = NewFake(new[] { turn });

        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        // The element exists from the moment the row is built — Opacity is a
        // literal in the template, not bound — but starts collapsed, since
        // IsVisible is HasImageNote and nothing has failed yet.
        var before = NoteTextBlockIn(ChatPanelTestAccess.Instance!);
        Assert.NotNull(before);
        Assert.False(before!.IsVisible);

        // Set in the same order LoadImage/TryResolveLocalMedia set them —
        // detail before the note, whose own Raise() is what the view
        // actually notices (see ChatTurn.ImageNoteDetail's own header).
        turn.ImageNoteDetail = "/x/y.png — outside-allowed-folders";
        turn.ImageNote = "Picture not shown — the gateway won't serve files from that folder. "
            + "Ask the agent to write it to ~/.openclaw/media/, which is allowed for every agent.";
        Flush();

        var block = NoteTextBlockIn(ChatPanelTestAccess.Instance!);

        Assert.NotNull(block);
        Assert.True(block!.IsVisible);
        Assert.Equal(turn.ImageNote, block.Text);
    }

    [AvaloniaFact]
    public void ATurnWithNoNoteLeavesTheLineCollapsed()
    {
        var turn = new ChatTurn { Role = ChatRole.Assistant, Text = "an ordinary reply" };
        var fake = NewFake(new[] { turn });

        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var block = NoteTextBlockIn(ChatPanelTestAccess.Instance!);

        Assert.NotNull(block);
        Assert.False(block!.IsVisible);
    }

    // A turn with a decoded picture and a note set never actually happens:
    // both LoadImage and TryResolveLocalMedia return on a successful fetch
    // before either ever asks the gateway why. Pinned here so a later change
    // to that ordering doesn't quietly start setting both at once.
    [AvaloniaFact]
    public void ImageNoteStaysNullWhenAPictureActuallyArrives()
    {
        var bytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg==");

        var turn = new ChatTurn
        {
            Role = ChatRole.User,
            Text = "a screenshot",
            IsComplete = true,
            ImageBytes = bytes
        };

        var fake = NewFake(new[] { turn });
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        Assert.Null(turn.ImageNote);

        var block = NoteTextBlockIn(ChatPanelTestAccess.Instance!);
        Assert.NotNull(block);
        Assert.False(block!.IsVisible);
    }

    [AvaloniaFact]
    public void ImageNoteDetailReachesTheTooltip()
    {
        const string detail = "/x/y.png — outside-allowed-folders";

        var turn = new ChatTurn
        {
            Role = ChatRole.Assistant,
            Text = "MEDIA:/x/y.png",
            ImageNote = "Picture not shown — the gateway won't serve files from that folder.",
            ImageNoteDetail = detail
        };

        var fake = NewFake(new[] { turn });
        ChatPanel.OpenFor(NewOrb(), fake);
        Flush();

        var block = NoteTextBlockIn(ChatPanelTestAccess.Instance!);

        Assert.NotNull(block);
        Assert.Equal(detail, ToolTip.GetTip(block!));
    }
}
