using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClaudeBuddy
{
    // What the chat panel needs from a session it can talk to, and nothing more.
    //
    // An interface here rather than the panel reaching into OpenClawSessions
    // directly, for one practical reason: it lets the whole panel — layout,
    // keyboard, streaming, autoscroll, the mic — be built and watched against an
    // in-memory fake before the gateway can send a word. The transport is then
    // swapping one implementation for another rather than the moment everything
    // is first tried at once.
    //
    // Four requirements on any implementation, each of which the panel relies on
    // and none of which it can check:
    //
    //  1. Every event is raised on the UI thread. The implementation does its
    //     own Dispatcher.Post. The alternative is every consumer hopping threads
    //     by hand and a comment on each explaining why.
    //  2. TurnUpdated carries the whole turn, already mutated — not a delta. A
    //     dropped or coalesced event then costs nothing, because the panel
    //     re-reads Text. Deltas make the view desyncable with no way to notice.
    //     (The gateway obliges: its `agent` events carry data.text as a full
    //     snapshot alongside data.delta.)
    //  3. SendAsync raises TurnAdded for the user's own turn. The panel never
    //     inserts optimistically, so exactly one thing owns the transcript and a
    //     failed send leaves no ghost behind.
    //  4. History is already bounded and ordered oldest to newest. The panel
    //     shows what it is given and never trims or pages.
    public enum ChatRole { User, Assistant, System }

    public enum RemoteChatState { Disconnected, Connecting, Connected, Error }

    // Mutable on purpose: a streaming reply updates Text in place and raises
    // TurnUpdated, so the list never recreates the item. Recreating it would
    // re-template the row, which is the one thing that could steal focus from
    // the input mid-sentence.
    public sealed class ChatTurn : INotifyPropertyChanged
    {
        private string _text = "";

        public ChatRole Role { get; init; }

        public string Text
        {
            get => _text;
            set
            {
                if (_text == value) return;
                _text = value;
                Raise();
            }
        }

        public bool IsComplete { get; set; }

        // When this turn happened, in local time. The gateway records a
        // timestamp per message and the panel shows it, so a conversation that
        // has been going on across Discord and a terminal all day reads as
        // something with a shape rather than a flat wall.
        //
        // Defaulted to now, which is right for a turn created live and is
        // overwritten from the backlog for one that wasn't.
        public DateTimeOffset At { get; init; } = DateTimeOffset.Now;

        private string? _imageUrl;

        // A picture sent in the conversation, as a path on the gateway rather
        // than bytes: a transcript can hold a dozen of them and only the ones
        // actually scrolled to are worth a megabyte each. The panel resolves it.
        //
        // Settable rather than init-only: a live turn can start with no
        // picture and gain one once OpenClawChatSession resolves a
        // "[media attached: ...]" marker against the gateway's own history
        // (see TryResolveLiveImage) — the same reason Text is mutable rather
        // than the row being recreated.
        public string? ImageUrl
        {
            get => _imageUrl;
            set
            {
                if (_imageUrl == value) return;
                _imageUrl = value;
                Raise();
            }
        }

        public string ImageAlt { get; set; } = "";

        private string? _imageNote;

        // Why a picture that should have shown didn't — CB-93. Set once a
        // resolution attempt comes back empty and the gateway's own &meta=1
        // answer explains the refusal (see OpenClawMediaRefusal). Null in the
        // ordinary case where nothing failed, which draws nothing.
        //
        // Mutable rather than init-only for the same reason ImageUrl and
        // ImageBytes are: a turn starts with neither a picture nor a reason,
        // and gains one once the async resolution — on the live path or the
        // history path — finishes.
        public string? ImageNote
        {
            get => _imageNote;
            set
            {
                if (_imageNote == value) return;
                _imageNote = value;
                Raise();
            }
        }

        // The tooltip for ImageNote: the path and the gateway's own code,
        // kept out of the line itself so the bubble doesn't grow past one
        // sentence. Plain rather than notifying on its own — always set
        // immediately before ImageNote, whose Raise() is what tells the view
        // to look again, the same pairing ImageAlt already has with
        // ImageBytes/ImageUrl above.
        public string? ImageNoteDetail { get; set; }

        private byte[]? _imageBytes;

        // A picture already decoded, for the two cases where there is no
        // gateway to resolve ImageUrl against: a local CLI's own transcript
        // carries an attached picture inline as base64 (see ChatTranscript's
        // image handling), and a picture the panel itself just pasted and
        // wrote to disk is read straight back rather than round-tripped
        // through a fetch it would only fail. Never both this and ImageUrl
        // on the same turn.
        public byte[]? ImageBytes
        {
            get => _imageBytes;
            set
            {
                if (_imageBytes == value) return;
                _imageBytes = value;
                Raise();
            }
        }

        // Who said this, when that is someone other than the two ends of the
        // conversation. In a channel an agent's transcript carries messages from
        // the other agents in the room, and "Zara" and "Lilibeth" arriving in
        // identical bubbles is a transcript you have to read twice to follow.
        //
        // Null for an ordinary two-party conversation, where the side of the
        // bubble already says who is talking and a name on every row would be
        // noise.
        public string? Speaker { get; init; }

        // Whether the person at this keyboard said it.
        //
        // Separate from the role, because the role cannot carry it. A message in
        // a channel arrives in every member agent's transcript in the *user*
        // role whoever sent it, so user-role means "not the agent this
        // transcript belongs to" and nothing more — which is why a room used to
        // draw everybody's messages as the room's own neutral voice rather than
        // asserting one of them was yours.
        //
        // The panel needs no rule of its own for this: a turn kept at
        // ChatRole.User with no Speaker is already what it draws blue and to the
        // right, so a Mine turn is one and a turn from somebody else carries the
        // Speaker that takes it back to the left. What this flag adds is
        // something the *transcript* can be built from — a room deciding which
        // of several identical-looking user turns to keep in your voice.
        //
        // Defaults false, which is the honest answer for every transport that
        // does not know: a local CLI's transcript, where user-role really does
        // mean you, sets it nowhere and loses nothing.
        public bool Mine { get; init; }

        // That speaker's colour, as "#RRGGBB" — the same one their orb's ring
        // is drawn in, so the two are recognisably the same agent. Carried
        // rather than looked up because the panel deliberately knows nothing
        // about agents or gateways.
        public string? SpeakerColor { get; init; }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public interface IRemoteChatSession
    {
        string SessionId { get; }
        string DisplayName { get; }
        RemoteChatState State { get; }
        IReadOnlyList<ChatTurn> History { get; }

        event Action<ChatTurn>? TurnAdded;
        event Action<ChatTurn>? TurnUpdated;
        event Action<RemoteChatState>? StateChanged;

        Task SendAsync(string text);

        // Stops the reply in flight. Separate from dismissing the panel: closing
        // a window should never cancel work someone asked for.
        void Cancel();
    }

    // Everything below is optional. The panel tests for each one and does
    // without when it isn't there, which is what keeps the in-memory fake — and
    // any future third transport — to the nine members above.
    //
    // They are separate interfaces rather than more members on
    // IRemoteChatSession because they are separate powers: a transport can page
    // backwards without being able to answer a dialog, and both were true of
    // exactly one implementation each when they were written.

    // Scrolling back past what the panel was given.
    //
    // This started life as four members on OpenClawChatSession that the panel
    // reached for by type test. Lifting it happened when the second transport
    // turned out to want the same thing for the same reason — a transcript is
    // thousands of rows and the panel is given forty.
    public interface IRemoteChatBacklog
    {
        // False once there is nothing older left to fetch. The panel asks before
        // every fetch, so an implementation that can't page says false forever.
        bool HasMore { get; }

        // True when this call actually prepended something. False is not a
        // failure — it is "that was the end" — and the panel uses it to avoid
        // measuring a scroll correction that isn't coming.
        Task<bool> LoadOlderAsync(CancellationToken ct);

        // The whole transcript changed underneath the panel, which TurnAdded
        // can't express. Raised when a backlog lands a moment after the panel
        // opened and replaces the little it had.
        event Action? HistoryReplaced;

        // Older turns went on the front. Carries how many, because the panel has
        // to put the scroll position back afterwards — content appearing above
        // where you are reading would otherwise throw you down the page.
        event Action<int>? HistoryPrepended;
    }

    // What the empty input box should say.
    //
    // Both transports can be in a state where typing is pointless — replying
    // switched off, or a session with no pane to type into — and both would
    // rather say so on the box than let you write a paragraph first. The box
    // stays enabled either way: SendAsync still explains itself in the
    // transcript, and a disabled box you can't paste a draft into is a worse
    // answer than one that tells you what will happen.
    public interface IRemoteChatComposer
    {
        string ComposerHint { get; }
    }

    // A session that cannot be typed into where it is, but can be *opened*
    // somewhere it can be dealt with.
    //
    // One implementation, and the same reason the interfaces above are separate
    // from IRemoteChatSession rather than on it: a transport with no such answer
    // simply does not implement this, and the panel offers nothing. A gateway
    // session has nowhere to be attached to; a background job has exactly one
    // place, and until this existed the panel's advice for one was "reply in the
    // terminal instead" — a terminal that does not exist, which is the whole
    // reason a daemon runs the session.
    public interface IRemoteChatElsewhere
    {
        // Whether the panel should offer it. Decided by the session rather than
        // by the panel, because the rule is the click path's
        // (ClickRouting.AttachWouldReach) and the panel has no business holding
        // a second copy of it.
        bool CanOpenElsewhere { get; }

        // Fire and forget, like the click it shares its implementation with:
        // opening a window is not something the panel waits on, and what arrives
        // afterwards arrives through the ordinary scan.
        //
        // Deliberately the same destination as that click rather than a second
        // one of its own — for a background session, the `claude agents` roster.
        // One verb, one place: a panel that sent you somewhere else from where
        // the orb sends you would be two answers to "where is this session".
        void OpenElsewhere();
    }

    // A conversation that is somewhere else, and can say where.
    //
    // The panel used to badge these "another machine", which is true and is not
    // an answer: somebody with two of them cannot act on it. The name is already
    // on the wire — a relay is called `claude-buddy-rc--claude-board-avatar` and
    // the tail is the machine — so this costs nothing to send and works against
    // a far Buddy of any version.
    //
    // Optional like the rest, and for the same reason: a local session's machine
    // is *this* one, which is not worth a chip, and a gateway conversation has
    // no machine at all.
    public interface IRemoteChatMachine
    {
        // Null until the roster has answered, which a panel can open before. The
        // caller keeps the vaguer wording rather than guessing — naming the
        // wrong machine is worse than not naming one.
        string? MachineName { get; }

        // Raised when it becomes known, since the panel is usually open first.
        event Action? MachineChanged;
    }

    // A session that can be waiting on something slow enough to need saying so.
    //
    // Optional for the same reason as the interfaces above: a local CLI session
    // reads a file on this machine and has nothing to wait for, and a gateway
    // session is one websocket round trip away. Only the mirror has a wait worth
    // drawing — its opening window is carried by a model retyping base64 by
    // hand, measured on real machines at 222, 231, 234, 247 and 192 seconds.
    //
    // **What it deliberately does not offer is a percentage.** A window that
    // fits in one chunk is a single round trip: there is no intermediate signal
    // to report, so a bar that filled at a guessed rate would be inventing
    // progress rather than showing it, and would then sit at 99% for a minute —
    // which is worse than an honest spinner, because it makes a working transfer
    // look stuck. What is real is *elapsed*, and how long these usually take, so
    // that is what this carries. See CB-58.
    public interface IRemoteChatFetchWait
    {
        // When the current wait began, or null when nothing is in flight. A
        // clock rather than a bool so the panel can say how long it has been
        // without keeping its own copy of the answer.
        DateTimeOffset? WaitingSince { get; }

        // What is being waited for, in the panel's own words.
        string WaitingFor { get; }

        // Raised when either of the two above changes. Not a property-changed
        // event: this is the whole of the surface, and one signal for two
        // properties read together is less to get wrong than two signals.
        event Action? WaitChanged;
    }

    // A session where a pasted picture can actually go somewhere.
    //
    // Not on IRemoteChatSession itself, for the same reason the three
    // interfaces above aren't: only one implementation exists yet, and a
    // session that doesn't implement this simply leaves a paste of a
    // picture as the ordinary text paste it would otherwise have been —
    // which is the only honest answer for a transport with nowhere to put
    // a file. A local CLI session has somewhere, because its own reader is
    // on this machine; a gateway session's is on the other end of a
    // websocket, and handing it a path on this machine would name a file it
    // cannot open.
    public interface IRemoteChatImages
    {
        // Sends text together with pictures already saved to disk. Called
        // instead of SendAsync exactly when the panel is holding at least
        // one pasted picture; a message with none still goes through
        // SendAsync alone.
        Task SendWithImagesAsync(string text, IReadOnlyList<string> imagePaths);
    }

    // One option in a dialog the session is blocked on. Key is what gets sent —
    // a digit, for the numbered lists Claude Code puts up — and Label is the
    // dialog's own wording, read off the screen rather than guessed at.
    public sealed record ChatPromptOption(string Key, string Label);

    public sealed record ChatPrompt(string Title, IReadOnlyList<ChatPromptOption> Options);

    // One slash command a CLI understands, offered the same way typing "/" in
    // its own terminal would. Name includes the leading "/" so the panel can
    // compare it against the input box's text directly.
    public sealed record SlashCommand(string Name, string Description);

    // The slash commands a session's underlying CLI understands, so the panel
    // can offer autocomplete for a message that is about to be typed straight
    // into that CLI's own input line. Only a local CLI session has an answer:
    // an OpenClaw conversation isn't parsed by a command grammar on the far
    // end, so there is nothing to suggest.
    public interface IRemoteChatSlashCommands
    {
        IReadOnlyList<SlashCommand> SlashCommands { get; }
    }

    // A session that has stopped and is waiting to be answered.
    //
    // The case this exists for is a permission prompt: the session is doing
    // nothing, the transcript says nothing about why, and the panel would
    // otherwise show a conversation that had simply gone quiet. Answering is a
    // real power and is gated like sending is.
    public interface IRemoteChatPrompts
    {
        // Null when nothing is waiting. Non-null means the panel should show the
        // options instead of pretending the silence is normal.
        ChatPrompt? Prompt { get; }

        event Action? PromptChanged;

        // Take this option. Only ever called with an option out of Prompt, so an
        // implementation never has to invent a key it didn't publish.
        Task AnswerAsync(ChatPromptOption option);

        // "I'll deal with it myself" — go to wherever the dialog actually is.
        // The fall back for when the dialog could not be read, which is the only
        // honest answer at that point.
        void AnswerElsewhere();
    }
}
