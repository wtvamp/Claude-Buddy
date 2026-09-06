using System;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace ClaudeBuddy
{
    // A small conversation anchored to an orb: the last few turns, a line to
    // type in, and a mic. Opened by clicking an orb that represents a session
    // with no terminal to jump to — for those, this is where the click goes.
    //
    // One instance, reused. Two panels would be two windows competing to be the
    // key window, each one's dismiss-on-deactivate closing the other; a
    // singleton makes "opening B closes A" correct by construction rather than
    // emergent. What is worth keeping per session is the draft and the scroll
    // position, and those live in a dictionary — a window is not a storage
    // mechanism. The draft store isn't a nicety either: the panel hides whenever
    // you switch apps, so without it every alt-tab would eat a half-typed
    // sentence.
    public partial class ChatPanel : Window
    {
        private static ChatPanel? _instance;

        private static readonly Dictionary<string, string> Drafts = new(StringComparer.Ordinal);

        private IRemoteChatSession? _session;
        private OrbWindow? _owner;

        // Whether the header is currently wearing the orb's identity rather
        // than an agent's own. Only that case can be refreshed from the orb.
        private bool _borrowedIdentity;

        // Who is talking in this session when the transcript does not say.
        //
        // A box shared by reference with every TurnView, not a value copied
        // into each. The name is not always known when the panel binds — a
        // terminal session's title arrives with a later hook write, and the
        // panel opens on whatever the orb had at the time, which is often
        // nothing. Copied, that nothing was baked into every row already built
        // and the chips never appeared; boxed, filling it in later fills in the
        // rows that were waiting for it. Same one-shot-read mistake the header
        // made two commits ago, in a second place.
        private sealed class Speaker { public string? Name; }

        private readonly Speaker _soleSpeaker = new();

        // How much bigger or smaller than shipped this panel draws chat text.
        //
        // Boxed and shared with every TurnView for the same reason Speaker
        // above is: a bubble built before the last Cmd+ has to end up the same
        // size as one built after it. Copied into each row, only the rows built
        // since the keystroke would be right, and a transcript would fan out
        // into as many sizes as the user had pressed the key.
        private sealed class TextScale { public double Value = ChatZoom.Default; }

        private readonly TextScale _textScale = new();

        // The colour a reply is drawn in when the turn itself doesn't name one.
        //
        // A room's turns carry their own, because several agents are talking. In
        // every other panel exactly one agent is, and repeating its name on
        // every bubble would be noise — but its colour is not, so it comes from
        // the orb rather than from each message.
        private Color? _defaultBubble;

        private readonly ObservableCollection<TurnView> _turns = new();

        // Pictures pasted since the last send, waiting to go out with
        // whatever gets typed next. Not part of a session's own draft store
        // below — a paste is only ever meant for the message being composed
        // right now, so switching sessions clears it rather than saving it.
        private readonly ObservableCollection<PendingImage> _pendingImages = new();

        // What the bound session's CLI understands, so "/" in the box can
        // offer the same autocomplete the terminal itself would. Empty for a
        // session with no answer for IRemoteChatSlashCommands, which quietly
        // turns the whole feature off for it rather than needing a check at
        // every call site.
        //
        // Asked of the session on every keystroke rather than read once when
        // the panel binds. A local session knows its commands before the panel
        // opens, so caching looked free; a session on another machine has to be
        // *asked* what it can run, and the answer lands half a minute later. A
        // list captured at bind time was therefore permanently empty for the
        // one kind of session that most needed it — the panel had to be closed
        // and reopened to see anything, which nobody would guess.
        private IReadOnlyList<SlashCommand> SlashCommands =>
            (_session as IRemoteChatSlashCommands)?.SlashCommands ?? Array.Empty<SlashCommand>();

        // The suggestions currently shown, and which one Up/Down has landed
        // on. Kept as a plain list rather than something observable: the
        // popup is small and rebuilt wholesale on every keystroke or arrow
        // press anyway, so there is nothing an incremental update would save.
        private List<SlashCommand> _slashMatches = new();
        private int _slashSelected;

        // Distance from the orb's centre to the panel's near edge. Clears the
        // 56pt orb with a small gap, the same way OrbFlyout's ArcRadius does.
        private const int Gap = 34;

        // The size the XAML ships, captured before anything has been restored
        // over it. An agent with no saved size has to go *back* to this, not
        // keep whatever the previously bound agent was dragged to — one window
        // serves every session, so without this the first resize would silently
        // become the size of every panel opened after it.
        private readonly double _defaultWidth;
        private readonly double _defaultHeight;

        public ChatPanel()
        {
            InitializeComponent();

            _defaultWidth = Width;
            _defaultHeight = Height;

            _bubbleMenu = BuildBubbleMenu();

            Turns.ItemsSource = _turns;
            Attachments.ItemsSource = _pendingImages;

            // Bubbles size themselves off Scroll's actual width (see
            // TurnView.MaxBubbleWidth) rather than a fixed pixel cap, since
            // the panel is user-resizable now. Two hooks cover the two ways a
            // turn's width can go stale: the collection hook catches a turn
            // that didn't exist yet at the last resize, and SizeChanged
            // catches turns that were already on screen when the resize
            // happened.
            _turns.CollectionChanged += (_, e) =>
            {
                if (e.NewItems is null) return;

                var width = Scroll.Bounds.Width;
                if (width <= 0) return;

                foreach (TurnView turn in e.NewItems) turn.AvailableWidth = width;
            };
            Scroll.SizeChanged += (_, _) =>
            {
                var width = Scroll.Bounds.Width;
                if (width <= 0) return;

                foreach (var turn in _turns) turn.AvailableWidth = width;
            };

            CloseButton.PointerPressed += (_, e) => { e.Handled = true; HideNow(); };

            // The portrait opens at four times the size, centred on itself.
            // Handled so the click doesn't also travel on to anything behind it.
            AvatarBox.PointerPressed += (_, e) =>
            {
                if (_avatar is null) return;

                e.Handled = true;

                var centre = AvatarBox.Bounds.Center;
                AvatarPopup.Show(_avatar, this.PointToScreen(new Point(
                    AvatarBox.Bounds.X + centre.X,
                    AvatarBox.Bounds.Y + centre.Y)));
            };
            SendButton.PointerPressed += (_, e) => { e.Handled = true; Send(); };

            // Fire and forget, like the click on the orb it shares its
            // implementation with. Nothing is awaited and nothing about the panel
            // changes: what the attach produces arrives through the ordinary
            // scan, which is also what eventually gives this session a pane and
            // turns the composer into an ordinary one.
            AttachButton.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                (_session as IRemoteChatElsewhere)?.OpenElsewhere();
            };
            MicButton.PointerPressed += (_, e) => { e.Handled = true; _owner?.ToggleRecording(); };
            SpeakButton.PointerPressed += (_, e) => { e.Handled = true; SpeakLatest(); };

            // Tunnel, not bubble. TextBox handles Return in a class handler that
            // runs before any instance handler on the same element, so a plain
            // KeyDown += would fire after the newline had already been inserted.
            // Getting there first is the whole point.
            Input.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
            Input.TextChanged += (_, _) => UpdateSlashSuggestions();

            // On the window rather than on Input, and ahead of it: a selection
            // in a bubble has to be copyable no matter which control the
            // keystroke was aimed at, and the composer would otherwise eat the
            // gesture whether or not it had anything to copy. See OnCopyKeyDown.
            AddHandler(KeyDownEvent, OnCopyKeyDown, RoutingStrategies.Tunnel);

            KeyDown += OnPanelKeyDown;

            // Tunnel, and on the window rather than on the composer. The
            // composer holds focus almost the whole time this panel is open, so
            // a bubbling handler would be asking the TextBox's own key handling
            // for permission to run — and the one gesture that has to work
            // whatever is focused is the one that makes the text readable.
            AddHandler(KeyDownEvent, OnZoomKeyDown, RoutingStrategies.Tunnel);

            // The size is a setting, so a panel opens at whatever the last one
            // was left at. Done here rather than in Bind because it is about
            // the window, not about which session is in it.
            ApplyTextScale();

            Opened += (_, _) =>
            {
                // Orbs follow you across Spaces; a panel that didn't would be
                // stranded behind you mid-sentence.
                this.ShowOnAllSpaces();

                // A no-op in practice — the shared class patch is installed by
                // whichever orb you clicked to get here — but kept for symmetry
                // with OrbFlyout, and correct if that ever stops being true.
                this.AcceptFirstClick();
            };

            // Deferred and re-checked: clicking our own mic or close button can
            // deactivate the window for an instant, and a recording in progress
            // must not be orphaned by a click elsewhere — the same rule
            // ScheduleFlyoutHide already follows for the arc.
            Deactivated += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (IsActive) return;
                if (_owner?.IsRecording == true) return;

                // The enlarged portrait is this panel's own window: opening it
                // deactivates the panel, and closing the panel out from under it
                // would be a strange answer to "show me that picture".
                if (AvatarPopup.IsOpen) return;

                // Same argument, one control further out: a context menu opened
                // on a message bubble is its own window too, and closing the
                // panel behind it would take the menu with it.
                if (ContextMenuIsOpen) return;

                HideNow();
            }, DispatcherPriority.Background);

            // The window's Width/Height are the resize target now (see the
            // XAML comment), so they're known synchronously and Reposition()
            // is called explicitly wherever the size or the orb changes —
            // Bind and RepositionFor — rather than off SizeChanged. Doing it
            // off SizeChanged too would refire Reposition() on every pixel of
            // a user's own resize drag and recentre the window on the orb out
            // from under their cursor.
            ResizeN.PointerPressed += (_, e) => BeginResize(WindowEdge.North, e);
            ResizeS.PointerPressed += (_, e) => BeginResize(WindowEdge.South, e);
            ResizeE.PointerPressed += (_, e) => BeginResize(WindowEdge.East, e);
            ResizeW.PointerPressed += (_, e) => BeginResize(WindowEdge.West, e);
            ResizeNE.PointerPressed += (_, e) => BeginResize(WindowEdge.NorthEast, e);
            ResizeNW.PointerPressed += (_, e) => BeginResize(WindowEdge.NorthWest, e);
            ResizeSE.PointerPressed += (_, e) => BeginResize(WindowEdge.SouthEast, e);
            ResizeSW.PointerPressed += (_, e) => BeginResize(WindowEdge.SouthWest, e);
            PointerMoved += OnResizePointerMoved;
            PointerReleased += OnResizePointerReleased;

            // See NwSeCursor/NeSwCursor: no StandardCursorType member draws
            // as an actual diagonal on this platform, so these two corner
            // pairs get a cursor bitmap built by hand instead.
            ResizeNW.Cursor = NwSeCursor;
            ResizeSE.Cursor = NwSeCursor;
            ResizeNE.Cursor = NeSwCursor;
            ResizeSW.Cursor = NeSwCursor;

            // Reaching the top asks for the page before. A threshold rather than
            // exactly zero, because a trackpad flick lands a few pixels short of
            // the top as often as it lands on it.
            Scroll.ScrollChanged += (_, _) =>
            {
                // Only when there is something to scroll. ScrollChanged fires on
                // extent and viewport changes too, and a transcript shorter than
                // the panel sits at offset zero forever — so this asked for the
                // page before, which grew the extent, which fired it again, and
                // walked the entire backlog the instant the orb was clicked.
                if (Scroll.Extent.Height <= Scroll.Viewport.Height + 8) return;
                if (Scroll.Offset.Y > 24) return;

                _ = LoadOlderAsync();
            };
        }

        public static bool IsOpenFor(string sessionId) =>
            _instance is { IsVisible: true } panel
            && panel._session?.SessionId == sessionId;

        public static void OpenFor(OrbWindow orb, IRemoteChatSession session)
        {
            _instance ??= new ChatPanel();
            _instance.Bind(orb, session);
        }

        // Used when the orb goes away, or is about to move under the panel —
        // an arrangement animation, or the orb's own close.
        public static void HideFor(string sessionId)
        {
            if (_instance is null) return;
            if (_instance._session?.SessionId != sessionId) return;

            _instance.HideNow();
        }

        public static void RepositionFor(OrbWindow orb)
        {
            if (_instance is not { IsVisible: true } panel) return;
            if (!ReferenceEquals(panel._owner, orb)) return;

            panel.Reposition();
        }

        // Speech is global rather than per-orb, so the panel is told about it
        // the same way the flyout is, from one place.
        public static void SetSpeakState(TextToSpeech.SpeakState state) =>
            _instance?.ApplySpeakState(state);

        public static void SetRecording(OrbWindow orb, bool recording)
        {
            if (_instance is not { IsVisible: true } panel) return;
            if (!ReferenceEquals(panel._owner, orb)) return;

            panel.MicFill.Fill = recording ? RecordingFill : IdleFill;
        }

        // Dictation lands here rather than being sent. Same rule
        // TerminalFocuser.SendText has always followed and explains at its own
        // definition: transcription is a typing aid, and it does not get to
        // decide that you meant it.
        public static void AppendToInput(string text)
        {
            if (_instance is not { IsVisible: true } panel) return;

            var existing = panel.Input.Text ?? "";
            panel.Input.Text = existing.Length == 0 ? text : existing.TrimEnd() + " " + text;
            panel.Input.CaretIndex = panel.Input.Text.Length;
            panel.Input.Focus();
        }

        private static readonly IBrush IdleFill = new SolidColorBrush(Color.Parse("#E0202024"));
        private static readonly IBrush RecordingFill = new SolidColorBrush(Color.Parse("#E0D93B3B"));
        private static readonly IBrush SpeakActiveFill = new SolidColorBrush(Color.Parse("#E04A90D9"));
        private static readonly IBrush SpeakPreparingFill = new SolidColorBrush(Color.Parse("#E0B8860B"));

        // Connected is green rather than the speak button's blue. The two dots
        // sit inches apart and meant different things in the same colour: blue
        // on the button is "this is playing right now", blue on the portrait was
        // "this conversation is reachable". Green is what a presence dot is
        // everywhere else, so it needs no explanation, and it leaves blue to
        // mean one thing again.
        //
        // #00AF5F rather than a green picked by eye — it is the app's green
        // already, the value Claude Code's own /color green resolves to (see
        // OrbWindow's palette and ClaudeDesktopColors), so a connected dot and
        // a green orb are the same green.
        private static readonly IBrush ConnectedFill = new SolidColorBrush(Color.Parse("#E000AF5F"));

        private void Unbind()
        {
            if (_session is null) return;

            Drafts[_session.SessionId] = Input.Text ?? "";

            // Not carried to Drafts with the text: a picture pasted for one
            // conversation attached to whichever session happens to be open
            // next would be a silent misdirection rather than a convenience.
            _pendingImages.Clear();
            Attachments.IsVisible = false;

            _session.TurnAdded -= OnTurnAdded;
            _session.TurnUpdated -= OnTurnUpdated;
            _session.StateChanged -= OnStateChanged;

            if (_session is IRemoteChatBacklog previous)
            {
                previous.HistoryReplaced -= OnHistoryReplaced;
                previous.HistoryPrepended -= OnHistoryPrepended;
            }

            if (_session is IRemoteChatPrompts prompts) prompts.PromptChanged -= OnPromptChanged;

            if (_session is IRemoteChatFetchWait waited) waited.WaitChanged -= OnWaitChanged;
            HideWait();

            if (_session is IRemoteChatMachine wasNamed) wasNamed.MachineChanged -= OnMachineChanged;

            // A remote session can take a turn back — its "working…" line comes
            // off once the answer lands. Subscribed on the concrete type rather
            // than through an interface: nothing else in this app has ever needed
            // to remove a turn, and inventing IRemoteChatRemovable for one caller
            // would be ceremony. See RemoteControlChatSession.Removed.
            if (_session is RemoteControlChatSession previousRemote)
            {
                previousRemote.Removed -= OnTurnRemoved;

                // A live view is the one thing here that costs something while
                // nobody is looking: it holds a subscription on the other
                // machine's Buddy, which keeps that relay — a real Claude Code
                // session on the user's own account — from idling out. So the
                // panel closing says so, rather than leaving it to lapse.
                //
                // Told on the panel rather than in Dispose because these
                // sessions are deliberately never disposed: a remote
                // conversation outlives its orb, since there is no file on this
                // machine to rebuild it from. See _remoteChats in SessionManager.
                previousRemote.PanelClosed();
            }

            // The last good name is per session, not per panel. The panel is a
            // singleton and the box outlives a session, so leaving it set meant
            // the *next* conversation inherited it — and because "we already
            // knew a name" beats "we do not know one yet", a session whose
            // title had not arrived would wear the previous session's initials
            // on every bubble rather than none. Wrong is worse than absent
            // here: the chip is there to say who is talking.
            _soleSpeaker.Name = null;
        }

        private void Bind(OrbWindow orb, IRemoteChatSession session)
        {
            Unbind();

            // Tell the orb that is losing the panel. Only HideNow used to clear
            // this, and rebinding skips it — so clicking a second orb left the
            // first believing its panel was still open, and its hover flyout
            // never appeared again for the life of the process.
            if (_owner is not null && !ReferenceEquals(_owner, orb)) _owner.SetChatOpen(false);

            _owner = orb;
            _session = session;

            // Whatever this agent's panel was last dragged to. Before the
            // transcript is built and before Reposition(), because the height
            // decides whether the panel goes above or below the orb.
            ApplySavedSize(orb);

            session.TurnAdded += OnTurnAdded;
            session.TurnUpdated += OnTurnUpdated;
            session.StateChanged += OnStateChanged;

            if (session is RemoteControlChatSession remote)
            {
                remote.Removed += OnTurnRemoved;

                // Re-opens the live view if this panel closed it earlier. Cheap
                // when it is already open and nothing at all in messaging mode.
                remote.PanelOpened();
            }

            // The backlog usually lands a moment after the panel opens, so the
            // transcript has to be able to be replaced under it rather than only
            // appended to. Optional on purpose — a session that only ever
            // appends never raises it.
            if (session is IRemoteChatBacklog loader)
            {
                loader.HistoryReplaced += OnHistoryReplaced;
                loader.HistoryPrepended += OnHistoryPrepended;
            }

            if (session is IRemoteChatPrompts prompts) prompts.PromptChanged += OnPromptChanged;

            // Only the mirror has a wait worth drawing; everything else answers
            // fast enough that a spinner would flicker. Optional for exactly
            // that reason — see IRemoteChatFetchWait.
            if (session is IRemoteChatFetchWait waiting)
            {
                waiting.WaitChanged += OnWaitChanged;
                ShowWait(waiting);
            }
            else
            {
                HideWait();
            }

            // The roster usually answers after the panel is already open, so the
            // machine's name arrives late and the chip has to be redrawn when it
            // does.
            if (session is IRemoteChatMachine named) named.MachineChanged += OnMachineChanged;

            // "Zara — wtvamp" is built as name plus place, so it splits back
            // into the two lines the header now has. A name with no place (an
            // agent's own main session) simply leaves the second line empty.
            ApplyTitle();
            RefreshSoleSpeaker();

            // Read off the orb rather than from the session, so the panel and
            // the badge on the thing that was clicked cannot disagree — the
            // same reason the header takes its colour and letter from there.
            // The kind, and — for a session that is quiet in a way worth naming —
            // what it is waiting for. Read off the orb rather than re-derived, so
            // the chip and the thing that was clicked cannot disagree; the same
            // reason KindLabel is read from there rather than from the status.
            //
            // "needs input" is the daemon's own phrase, so the chip, the orb's
            // tooltip and `claude agents` all say the same words about the same
            // session.
            var kind = orb.KindLabel;
            var presence = orb.PresenceLabel;

            // Gated on the kind, not on either: a presence word without a kind
            // cannot happen — both marks belong to background sessions, and every
            // one of those is badged — and a chip that made room for the
            // impossible case would be a claim about this app that is not true.
            KindChip.IsVisible = kind is not null;
            KindChipText.Text = KindChipLabel(
                orb.KindGlyphText, kind, presence,
                (_session as IRemoteChatMachine)?.MachineName);

            ApplyHeartbeat(orb.IsHeartbeat);

            _defaultBubble = orb.AccentColor;

            ApplyAvatar(session.SessionId);
            OnStateChanged(session.State);

            _turns.Clear();
            foreach (var turn in session.History) _turns.Add(new TurnView(turn, _defaultBubble, _soleSpeaker, Adopt, _textScale));

            HideSlashSuggestions();

            Input.Text = Drafts.GetValueOrDefault(session.SessionId, "");
            MicButton.IsVisible = ClaudeBuddySettings.VoiceInputEnabled;
            ApplySpeakState(TextToSpeech.State);

            // The box stays enabled even when sending won't work, and says why
            // on itself instead. A disabled box can't be pasted into or drafted
            // in, and SendAsync explains itself in the transcript anyway.
            ApplyComposerAffordances(session);
            ApplyPrompt();

            Reposition();

            if (!IsVisible) Show();

            // Show() then Activate(): an accessory app can be activated
            // programmatically or by a click on one of its windows, and this one
            // is opened by exactly such a click. Focus is taken on Activated
            // rather than by WaitForOwnActivation, which sleeps the UI thread up
            // to 600ms — fine at the tail of a TerminalFocuser call, not between
            // a click and a window appearing.
            Activate();
            Dispatcher.UIThread.Post(() => Input.Focus(), DispatcherPriority.Input);

            // Unconditionally, not the pinned-only rule — this used to be
            // ScrollToEndIfPinned and that is why a panel sometimes opened
            // halfway up a conversation.
            //
            // There is one ChatPanel for every orb (its own comment above says
            // why: two of them would fight over being the key window), so the
            // scroll position this instance is carrying belongs to whichever
            // session you had open last. Asking whether *that* offset is at the
            // bottom is asking a question about a transcript that is no longer
            // on screen: scroll up in one chat, click a different orb, and the
            // answer is "no", so the new chat opens at the old offset with the
            // newest message somewhere below the fold.
            //
            // Same reasoning as OnHistoryReplaced: a transcript that was just
            // loaded wholesale has no read position worth preserving, and the
            // newest turn is the one you clicked the orb to read.
            ScrollToEndAfterLayout();
        }

        // What the box says, and whether there is a button beside it — one method
        // because they are two halves of one answer, and reading them from two
        // places is how they came to disagree in the first place.
        //
        // Both are re-read wherever the other was: at bind, and when a panel's
        // history is replaced wholesale (which is what a remote session
        // upgrading to a live view looks like). A local session's own transition
        // — parked, then attached, then typeable — arrives through the scan
        // rather than through the panel, and shows up here the next time the
        // panel is opened. Worth stating rather than papering over with a timer:
        // the attach opens a terminal the user is looking at, so the panel is not
        // where they are waiting for the answer.
        private void ApplyComposerAffordances(IRemoteChatSession? session)
        {
            Input.Watermark = (session as IRemoteChatComposer)?.ComposerHint ?? "Message…";
            AttachButton.IsVisible = (session as IRemoteChatElsewhere)?.CanOpenElsewhere ?? false;
        }

        // The same decoded frames the orb draws, at a size worth looking at.
        // Animated here too: this is the one place you are actually looking at
        // the picture rather than glancing at it, so a still frame of an
        // animated avatar would be the wrong half of the trade.
        private OpenClawAvatars.Avatar? _avatar;
        private ImageBrush? _avatarBrush;
        private int _avatarFrame;
        private DispatcherTimer? _avatarTimer;

        // The ring is the one part of the portrait that is always visible,
        // whether the circle holds a photo, a colour or nothing, so it is where
        // an identity colour belongs. Default is the flat white the XAML ships
        // with, which is what a session with no colour of its own keeps.
        private static readonly IBrush DefaultRing = new SolidColorBrush(Color.Parse("#40FFFFFF"));

        private void RingFor(Color? color)
        {
            if (color is not { } c)
            {
                Avatar.Stroke = DefaultRing;
                Avatar.StrokeThickness = 1;
                return;
            }

            Avatar.Stroke = new SolidColorBrush(c);
            // Thicker than the default hairline: a coloured ring is carrying
            // information now, and at 1px against a dark panel it reads as an
            // antialiasing artefact rather than a deliberate mark.
            Avatar.StrokeThickness = 2.5;
        }

        // An agent's colour, keyed on its id so the same agent is the same
        // colour everywhere — the orb, the team view and now this header.
        //
        // Asked of OpenClawSessions rather than of AgentPalette directly, which
        // is the difference between that sentence being true and being nearly
        // true. HexFor gives an agent the colour its id hashes to; two agents
        // can hash close enough to be indistinguishable, so the assignment is
        // made across the whole set and moves whichever of them collided.
        // Calling HexFor here would hand this header the pre-collision answer
        // and quietly disagree with the ring on the orb it opened from.
        private static Color? AgentColorFor(string sessionId)
        {
            var agent = OpenClawSessions.AgentIdOf(sessionId);
            if (string.IsNullOrEmpty(agent)) return null;

            var hex = OpenClawSessions.ColourForAgent(agent);
            return Color.TryParse(hex, out var colour) ? colour : null;
        }

        // The header borrows the orb's letters and colours, and borrowed them
        // exactly once — at Bind. Anything that changed the orb afterwards left
        // the panel showing what the orb used to say: a /rename, a /color, a
        // title arriving after the first hook write, or the two-letter setting
        // being toggled while a panel was open. Worse than stale, at open time
        // it could be empty — an orb clicked before its first status write has
        // no glyph yet, and the header copied the nothing and kept it.
        //
        // Same shape as RepositionFor and SetRecording above: the orb tells the
        // panel, the panel checks the message is from the orb it is showing.
        public static void RefreshIdentityFor(OrbWindow orb)
        {
            if (_instance is not { IsVisible: true } panel) return;
            if (!ReferenceEquals(panel._owner, orb)) return;

            panel.ApplyBorrowedIdentity();
            panel.RefreshSoleSpeaker();
        }

        // Only the case that borrows from the orb. An agent with a portrait or
        // an OpenClaw identity has its own, and re-running the whole of
        // ApplyAvatar here would restart an animated avatar on every hook
        // write — which is several a second while a session is working.
        // The agent whose messages these are.
        //
        // For a gateway session that is the agent in the session key, not the
        // panel's title: "#openclaw-management" is where the conversation is
        // and Lilibeth is who is talking in it, and a chip reading "Op" would
        // be naming the room as its own speaker. Only a terminal session, whose
        // title *is* its agent, falls back to the title.
        // "Zara — wtvamp" is built as name plus place, so it splits back into
        // the two lines the header has. A name with no place — an agent's own
        // main session — leaves the second line empty.
        //
        // Read from the session every time rather than once at Bind. A terminal
        // session is usually nameless when its panel opens and gets its title
        // from a later hook write; the header used to keep the empty string it
        // was born with.
        private void ApplyTitle()
        {
            var parts = (_session?.DisplayName ?? "").Split(" — ", 2);

            TitleText.Text = parts[0];
            SubtitleText.Text = parts.Length > 1 ? parts[1] : "";
            SubtitleText.IsVisible = parts.Length > 1;
        }

        private void RefreshSoleSpeaker()
        {
            ApplyTitle();

            var was = _soleSpeaker.Name;

            var identity = _session is null
                ? null
                : OpenClawSessions.IdentityForSession(_session.SessionId);

            // The rule itself is in ChatSpeaker, pure and tested — including
            // the part that matters here, that a name we already knew is never
            // replaced by not knowing it. That is what made the chips vanish
            // after a while rather than simply never appear.
            var name = ChatSpeaker.Resolve(identity?.Name, TitleText.Text, was);

            if (name == was) return;

            _soleSpeaker.Name = name;

            foreach (var view in _turns) view.SpeakerChanged();
        }

        private void ApplyBorrowedIdentity()
        {
            if (!_borrowedIdentity || _owner is null) return;

            Avatar.Fill = new SolidColorBrush(_owner.OrbColor);
            AvatarEmoji.Foreground = InkOn(_owner.OrbColor);
            RingFor(_owner.AccentColor);

            var letters = BorrowedLetters();

            // Never blank what is already there. Same rule as ChatSpeaker and
            // the last place in the panel that still lacked it: both of this
            // one's sources can be momentarily empty for reasons that are about
            // us rather than about the session — the orb clears its glyph while
            // an avatar loads, and a title is empty until a hook write brings
            // one — and either used to wipe a circle that was reading fine.
            //
            // There is no case where going from letters to nothing is the truth
            // about a conversation. Nothing to say yet is the empty circle at
            // the start; nothing to say any more does not happen.
            if (string.IsNullOrEmpty(letters)) return;
            if (AvatarEmoji.Text == letters) return;

            AvatarEmoji.Text = letters;
            AvatarEmoji.IsVisible = true;
        }

        // What the orb is drawing, or what it would draw if it had got round to
        // it. The fallback matters because the panel can be bound before the
        // orb's first status write, and an empty circle beside a perfectly good
        // title is the one outcome that is never right. It derives them the way
        // the orb would rather than with Initials(), so the two agree on case
        // as well as on letters — "Cb" here and on the orb, not "CB" here.
        private string BorrowedLetters()
        {
            var letters = _owner?.GlyphText ?? "";
            if (!string.IsNullOrEmpty(letters)) return letters;

            var name = TitleText.Text;
            return string.IsNullOrWhiteSpace(name)
                ? ""
                : OrbGlyph.For(name, ClaudeBuddySettings.TwoLetterGlyphs);
        }

        // Ink that can be read on a given circle.
        //
        // AvatarEmoji had no Foreground at all and inherited the panel's, which
        // is near-black — fine on nothing, because the circle it sits in was
        // invisible until an identity was drawn behind it. Once the fill became
        // the orb's *state* colour it was black on black, and idle is near-black
        // by default. That is the whole of the "initials keep disappearing"
        // report: they were there the entire time, and the letters went from
        // legible to invisible when a session stopped working, because
        // generating and waiting are bright and idle is not.
        //
        // Chosen by luminance rather than fixed at white, which is what the orb
        // does. The orb only ever draws on a state colour and white suits all
        // of them; this circle is also filled with an agent's own colour, and
        // several of those are light enough that white letters vanish the same
        // way black ones just did.
        private static readonly IBrush LightInk = new SolidColorBrush(Color.Parse("#EEFFFFFF"));
        private static readonly IBrush DarkInk = new SolidColorBrush(Color.Parse("#E6000000"));

        private static IBrush InkOn(Color fill)
        {
            // Rec. 709 luminance: the eye is far more sensitive to green than
            // to blue, so a plain average calls mid-blue light and gets it
            // backwards.
            var luminance = (0.2126 * fill.R + 0.7152 * fill.G + 0.0722 * fill.B) / 255.0;

            return luminance > 0.55 ? DarkInk : LightInk;
        }

        private void ApplyAvatar(string sessionId)
        {
            StopAvatarAnimation();

            var avatar = OpenClawSessions.AvatarForSession(sessionId);
            var identity = OpenClawSessions.IdentityForSession(sessionId);

            // Neither a portrait nor an emoji, which is every local session and
            // a gateway one whose agent list hasn't landed yet. Its orb already
            // carries both halves of an identity — a letter and a colour, the
            // ones just clicked — so the header borrows them. Better than an
            // empty circle, and better than a second scheme invented for this
            // window: the panel ends up looking like the orb it came out of.
            //
            // Keyed on there being no OpenClaw identity rather than on the
            // session's type, because the panel deliberately doesn't know what
            // kinds of session exist.
            if (avatar is null && identity is null && _owner is not null)
            {
                _avatar = null;
                _avatarFrame = 0;

                // Fill from the state, ring from the identity — which is
                // exactly how the orb itself is drawn, so the header reads as
                // the same object rather than as a second scheme.
                //
                // Both from OrbColor is what this said first, and that made the
                // ring the state colour twice over: idle is a user setting and
                // is commonly near black, so the "identity ring" was an
                // invisible ring around a circle of its own colour.
                Avatar.Fill = new SolidColorBrush(_owner.OrbColor);
                Avatar.IsVisible = true;
                AvatarEmoji.Foreground = InkOn(_owner.OrbColor);
                RingFor(_owner.AccentColor);

                // An initial wants less room than an emoji does.
                // Set outright rather than through ApplyBorrowedIdentity,
                // which refuses to blank: this is a new conversation and the
                // letters on screen are the last one's. The never-blank rule is
                // about refreshing what is already right, not about carrying
                // one session's identity onto another — the same distinction
                // Unbind draws for the speaker.
                _borrowedIdentity = true;
                AvatarEmoji.Text = BorrowedLetters();
                AvatarEmoji.FontSize = 26;
                AvatarEmoji.IsVisible = !string.IsNullOrEmpty(AvatarEmoji.Text);

                StateDot.HorizontalAlignment = HorizontalAlignment.Right;
                StateDot.VerticalAlignment = VerticalAlignment.Bottom;
                return;
            }

            _avatar = avatar;
            _avatarFrame = 0;
            _borrowedIdentity = false;

            // Reset from whatever the branch above may have left behind.
            AvatarEmoji.FontSize = 38;

            if (avatar is null)
            {
                // No portrait. This used to leave a hollow circle — no fill, no
                // ring, and nothing inside unless the agent happened to have an
                // emoji, which reads as a picture that failed to load rather
                // than as a person.
                //
                // An agent already has both halves of an identity elsewhere in
                // the app: a colour from AgentPalette, keyed on its id so it is
                // stable, and a name. So the header shows what the orb shows —
                // that colour as the fill and ring, and the initials of the
                // name when there is no emoji to use instead.
                var agentColor = AgentColorFor(sessionId);

                AvatarEmoji.Text = !string.IsNullOrEmpty(identity?.Emoji)
                    ? identity!.Emoji!
                    : OrbGlyph.Initials(identity?.Name);
                AvatarEmoji.IsVisible = !string.IsNullOrEmpty(AvatarEmoji.Text);

                // Initials are letterforms, not a pictograph, so they want the
                // smaller size an emoji would overflow at.
                if (string.IsNullOrEmpty(identity?.Emoji)) AvatarEmoji.FontSize = 26;

                if (agentColor is { } c)
                {
                    Avatar.Fill = new SolidColorBrush(c);
                    Avatar.IsVisible = true;
                    AvatarEmoji.Foreground = InkOn(c);
                    RingFor(c);
                }
                else
                {
                    Avatar.Fill = null;
                    Avatar.IsVisible = false;
                }

                // With a filled circle the badge has somewhere to sit, so it
                // keeps its corner. Only a genuinely empty circle centres it.
                var filled = Avatar.IsVisible || AvatarEmoji.IsVisible;
                StateDot.HorizontalAlignment = filled
                    ? HorizontalAlignment.Right
                    : HorizontalAlignment.Center;
                StateDot.VerticalAlignment = filled
                    ? VerticalAlignment.Bottom
                    : VerticalAlignment.Center;

                return;
            }

            AvatarEmoji.IsVisible = false;
            StateDot.HorizontalAlignment = HorizontalAlignment.Right;
            StateDot.VerticalAlignment = VerticalAlignment.Bottom;

            _avatarBrush ??= new ImageBrush { Stretch = Stretch.UniformToFill };
            _avatarBrush.Source = avatar.Frames[0];
            Avatar.Fill = _avatarBrush;
            Avatar.IsVisible = true;
            // A portrait gets the ring too. Without it the one avatar with a
            // picture is the only one in the app not wearing its own colour.
            RingFor(AgentColorFor(sessionId));

            if (!avatar.IsAnimated) return;

            _avatarTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(avatar.DelaysMs[0])
            };

            // The tick closes over its own timer and its own avatar rather than
            // reading the fields. A tick already queued on the dispatcher when
            // the panel rebinds to another agent would otherwise fire against
            // whatever is there now: a null timer if that agent's picture is
            // static, or the previous picture's frame delay if it isn't.
            var timer = _avatarTimer;
            var frames = avatar;
            var frame = 0;

            timer.Tick += (_, _) =>
            {
                if (!ReferenceEquals(_avatar, frames) || _avatarBrush is null) return;

                frame = (frame + 1) % frames.Frames.Count;
                _avatarBrush.Source = frames.Frames[frame];
                timer.Interval = TimeSpan.FromMilliseconds(frames.DelaysMs[frame]);
            };

            _avatarTimer.Start();
        }

        // Stopped when the panel goes away: a hidden window animating a GIF is
        // work nobody asked for, and the panel is hidden far more than it is up.
        private void StopAvatarAnimation()
        {
            _avatarTimer?.Stop();
            _avatarTimer = null;
        }

        // --- the heartbeat chip's beat ---------------------------------------
        // Same curve as the orb badge's (OpenClawHeartbeat.Beat), driven by a
        // timer of this panel's own.
        //
        // A timer rather than an Avalonia animation for the reason the orb's
        // pulse gives, and rather than *sharing* the orb's ticker because that
        // one's roster is orb windows: there is at most one chat panel, so a
        // second 20Hz timer that only runs while a heartbeat chat is open costs
        // nothing worth pooling. It stops the moment the chip goes away, which
        // is what keeps that true.

        private DispatcherTimer? _heartTimer;
        private long _heartStartedAt;
        private readonly ScaleTransform _heartScale = new();

        private void ApplyHeartbeat(bool heartbeat)
        {
            HeartChip.IsVisible = heartbeat;

            if (!heartbeat)
            {
                StopHeartAnimation();
                return;
            }

            HeartChipText.RenderTransform = _heartScale;
            HeartChipText.RenderTransformOrigin = RelativePoint.Center;
            _heartStartedAt = Environment.TickCount64;

            if (_heartTimer is not null) return;

            _heartTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1000.0 / 20)
            };
            _heartTimer.Tick += (_, _) =>
            {
                var beat = OpenClawHeartbeat.Beat(
                    (Environment.TickCount64 - _heartStartedAt) / OpenClawHeartbeat.PeriodMs);

                // Smaller swell than the orb's: this heart sits on a line of
                // text, and one that grew by a third would shift the chip's
                // neighbours every beat.
                _heartScale.ScaleX = _heartScale.ScaleY = 1.0 + (0.14 * beat);
                HeartChipText.Opacity = 0.55 + (0.45 * beat);
            };
            _heartTimer.Start();
        }

        private void StopHeartAnimation()
        {
            _heartTimer?.Stop();
            _heartTimer = null;

            _heartScale.ScaleX = _heartScale.ScaleY = 1.0;
            HeartChipText.Opacity = 1.0;
        }

        // Clicking a picture opens it full size in whatever this machine views
        // pictures with. Handled so the click doesn't travel on to the panel
        // behind it, and so it can't be mistaken for a click-away dismiss.
        private void Image_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;

            if ((sender as Control)?.DataContext is TurnView turn) turn.OpenFullSize();
        }

        // Avalonia.Native's macOS cursor factory (libAvaloniaNative.dylib)
        // only ever asks AppKit for arrow, crosshair, hand, I-beam, and the
        // two straight resize cursors (up/down, left/right) — nothing in it
        // answers to a diagonal, because AppKit itself has no public diagonal
        // resize NSCursor to hand back. TopLeftCorner and friends fall
        // through to plain crosshairCursor there, and Hand doesn't read as
        // "resize" at all. So the corners get a real double-headed arrow,
        // drawn once here and shared by both ends of each diagonal — NwSeCursor
        // for the ↖↘ corners, NeSwCursor (the same arrow mirrored) for ↗↙.
        private static readonly Cursor NwSeCursor = BuildDiagonalCursor(mirrored: false);
        private static readonly Cursor NeSwCursor = BuildDiagonalCursor(mirrored: true);

        private static Cursor BuildDiagonalCursor(bool mirrored)
        {
            // Avalonia's macOS cursor images came out at the bitmap's raw
            // pixel count, not that divided by dpiScale — so the first version
            // of this, drawn on a 22-unit canvas at 2x for retina crispness,
            // rendered as a 44px cursor and looked about double the size it
            // should have. Sizes below are fractions of `size` rather than
            // the original design's absolute numbers, so this can be tuned
            // again without redoing the arithmetic by hand.
            const double size = 11;
            const double dpiScale = 2;

            double margin = size / 11; // corner inset for a head's right-angle vertex
            double headEnd = size * 9 / 22; // how far a head's short legs reach
            double shaftA = size * 3 / 11; // shaft's near end
            double shaftB = size * 8 / 11; // shaft's far end
            double farHeadStart = size - headEnd;
            double far = size - margin;

            double X(double x) => mirrored ? size - x : x;

            var target = new RenderTargetBitmap(
                new PixelSize((int)(size * dpiScale), (int)(size * dpiScale)),
                new Vector(96 * dpiScale, 96 * dpiScale));

            using (var ctx = target.CreateDrawingContext())
            {
                // The shaft, drawn as a black line with a white one on top of
                // it rather than a single stroke, so it reads against either
                // a light or a dark background — the same reason this panel's
                // own bubbles carry an outline color at all.
                var outline = new Pen(Brushes.Black, size * 4.5 / 22, lineCap: PenLineCap.Round);
                ctx.DrawLine(outline, new Point(X(shaftA), shaftA), new Point(X(shaftB), shaftB));
                var inner = new Pen(Brushes.White, size * 2 / 22, lineCap: PenLineCap.Round);
                ctx.DrawLine(inner, new Point(X(shaftA), shaftA), new Point(X(shaftB), shaftB));

                // Two right-triangle heads, one at each end of the shaft, each
                // with its right angle at the corner it points into — the
                // same shape Windows' own SIZENWSE/SIZENESW cursors use.
                var heads = new StreamGeometry();
                using (var g = heads.Open())
                {
                    g.BeginFigure(new Point(X(margin), margin), true);
                    g.LineTo(new Point(X(headEnd), margin), true);
                    g.LineTo(new Point(X(margin), headEnd), true);
                    g.EndFigure(true);

                    g.BeginFigure(new Point(X(far), far), true);
                    g.LineTo(new Point(X(farHeadStart), far), true);
                    g.LineTo(new Point(X(far), farHeadStart), true);
                    g.EndFigure(true);
                }

                ctx.DrawGeometry(Brushes.White, new Pen(Brushes.Black, size * 1.25 / 22), heads);
            }

            return new Cursor(target, new PixelPoint(target.PixelSize.Width / 2, target.PixelSize.Height / 2));
        }

        // BeginResizeDrag hands the drag to the platform's window manager, which
        // is where Win32 and X11 implement it — but Avalonia.Native carries no
        // such hook on macOS (nothing in libAvaloniaNative.dylib answers to it),
        // so the call is a silent no-op there: the cursor still swaps, because
        // that part is pure managed code, but nothing ever moves. Tracked by
        // hand instead, uniformly on every platform, rather than branching on
        // OS to use the native call where it happens to exist.
        private WindowEdge? _resizeEdge;
        private PixelPoint _resizeStartPos;
        private double _resizeStartWidth;
        private double _resizeStartHeight;
        private PixelPoint _resizeStartScreen;

        private void BeginResize(WindowEdge edge, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            e.Handled = true;
            _resizeEdge = edge;
            _resizeStartPos = Position;
            _resizeStartWidth = Width;
            _resizeStartHeight = Height;
            _resizeStartScreen = this.PointToScreen(e.GetPosition(this));

            // Captured on the window rather than the strip under the pointer:
            // the drag routinely carries the pointer off a 6px strip, and
            // capture is what keeps the events coming anyway.
            e.Pointer.Capture(this);
        }

        private void OnResizePointerMoved(object? sender, PointerEventArgs e)
        {
            if (_resizeEdge is not { } edge) return;

            var nowScreen = this.PointToScreen(e.GetPosition(this));
            var scale = RenderScaling;
            var dx = (nowScreen.X - _resizeStartScreen.X) / scale;
            var dy = (nowScreen.Y - _resizeStartScreen.Y) / scale;

            var west = edge is WindowEdge.West or WindowEdge.NorthWest or WindowEdge.SouthWest;
            var east = edge is WindowEdge.East or WindowEdge.NorthEast or WindowEdge.SouthEast;
            var north = edge is WindowEdge.North or WindowEdge.NorthWest or WindowEdge.NorthEast;
            var south = edge is WindowEdge.South or WindowEdge.SouthWest or WindowEdge.SouthEast;

            var pos = _resizeStartPos;
            var width = _resizeStartWidth;
            var height = _resizeStartHeight;

            if (east) width = Math.Clamp(_resizeStartWidth + dx, MinWidth, MaxWidth);
            if (west)
            {
                width = Math.Clamp(_resizeStartWidth - dx, MinWidth, MaxWidth);
                pos = pos.WithX(_resizeStartPos.X - (int)Math.Round((width - _resizeStartWidth) * scale));
            }

            if (south) height = Math.Clamp(_resizeStartHeight + dy, MinHeight, MaxHeight);
            if (north)
            {
                height = Math.Clamp(_resizeStartHeight - dy, MinHeight, MaxHeight);
                pos = pos.WithY(_resizeStartPos.Y - (int)Math.Round((height - _resizeStartHeight) * scale));
            }

            Width = width;
            Height = height;
            Position = pos;
        }

        private void OnResizePointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_resizeEdge is null) return;

            _resizeEdge = null;
            e.Pointer.Capture(null);

            // Once per drag, not once per pixel: OnResizePointerMoved fires
            // continuously and this writes a file. Saved against the orb rather
            // than the session so the size follows the agent across runs — see
            // ChatPanelSizes in ClaudeBuddySettings for why a session id would
            // not have survived one.
            if (_owner is { } owner)
            {
                ClaudeBuddySettings.SetChatPanelSize(owner.PositionKey, Width, Height);
            }
        }

        // The size this agent's panel should open at: what it was last dragged
        // to, or the shipped default if it never has been.
        //
        // Clamped against this build's own Min/Max rather than trusted: the
        // bounds are in the XAML and could tighten in a later version, and a
        // hand-edited settings.json is a normal thing to find. An out-of-range
        // Width set on a Window is not politely ignored — it is honoured, and a
        // panel wider than any screen has no visible way back.
        private void ApplySavedSize(OrbWindow orb)
        {
            var saved = ClaudeBuddySettings.ChatPanelSizeFor(orb.PositionKey);

            Width = saved is null
                ? _defaultWidth
                : Math.Clamp(saved.Width, MinWidth, MaxWidth);
            Height = saved is null
                ? _defaultHeight
                : Math.Clamp(saved.Height, MinHeight, MaxHeight);
        }

        private void Reposition()
        {
            if (_owner is null) return;

            // Anchor on the orb's centre, the same constant EnsureFlyoutShown
            // uses. PointToScreen because Position is physical pixels and these
            // are DIPs, and the two only agree at 100% scaling.
            var anchor = _owner.PointToScreen(new Point(28, 28));
            var screen = Screens.ScreenFromPoint(anchor) ?? Screens.Primary;
            if (screen is null) return;

            var scale = screen.Scaling;
            var work = screen.WorkingArea;

            // Width and Height are the resize target (see the XAML comment),
            // so unlike the old SizeToContent world these are already final —
            // no need to wait on Root's laid-out bounds.
            var width = (int)(Width * scale);
            var height = (int)(Height * scale);
            var gap = (int)(Gap * scale);

            // Below by default, flipped above when it would run off the bottom.
            // Flipped rather than clamped upward: a clamped panel ends up
            // covering the orb you just clicked.
            var y = anchor.Y + gap;
            if (y + height > work.Bottom) y = anchor.Y - gap - height;

            var x = Math.Clamp(anchor.X - width / 2, work.X, Math.Max(work.X, work.Right - width));
            y = Math.Clamp(y, work.Y, Math.Max(work.Y, work.Bottom - height));

            Position = new PixelPoint(x, y);
        }

        private void OnInputKeyDown(object? sender, KeyEventArgs e)
        {
            // Only a session that has somewhere to put a picture gets its
            // paste intercepted at all — see IRemoteChatImages. Anything
            // else falls straight through to the TextBox's own paste, which
            // is exactly what happened here before this feature existed.
            if (_session is IRemoteChatImages && IsPasteGesture(e))
            {
                e.Handled = true;
                _ = HandlePasteAsync();
                return;
            }

            // While suggestions are up, the keys that would otherwise send or
            // insert a newline instead drive the popup — the same keys a
            // terminal's own "/" autocomplete would claim.
            if (_slashMatches.Count > 0)
            {
                if (e.Key == Key.Down)
                {
                    _slashSelected = (_slashSelected + 1) % _slashMatches.Count;
                    RenderSlashSuggestions();
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Up)
                {
                    _slashSelected = (_slashSelected - 1 + _slashMatches.Count) % _slashMatches.Count;
                    RenderSlashSuggestions();
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Escape)
                {
                    // Dismisses the popup only. A second Escape then reaches
                    // OnPanelKeyDown and closes the panel — the same
                    // two-step precedent recording already sets below.
                    HideSlashSuggestions();
                    e.Handled = true;
                    return;
                }

                var accepting = e.Key == Key.Tab
                    || (e.Key is Key.Enter or Key.Return && !e.KeyModifiers.HasFlag(KeyModifiers.Shift));

                if (accepting)
                {
                    AcceptSlashSuggestion(_slashMatches[_slashSelected]);
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key != Key.Enter && e.Key != Key.Return) return;

            // Shift+Enter is left entirely alone so the TextBox inserts the
            // newline itself, with its own caret handling and undo entry.
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;

            e.Handled = true;
            Send();
        }

        // TextBox's own PasteGesture rather than a hard-coded Ctrl/Cmd+V:
        // it is already the platform-correct chord (Cmd on macOS, Ctrl
        // elsewhere), and asking Input for its own answer means this never
        // drifts from whatever the TextBox itself would have matched.
        private static bool IsPasteGesture(KeyEventArgs e)
        {
            var gesture = TextBox.PasteGesture;
            return gesture is not null && e.Key == gesture.Key && e.KeyModifiers == gesture.KeyModifiers;
        }

        // Read off TextBox rather than spelled out, exactly as IsPasteGesture
        // above does, which is also what makes this right on both platforms
        // without a single OS check: Avalonia resolves the gesture to Cmd+C on
        // macOS and Ctrl+C on Windows.
        private static bool IsCopyGesture(KeyEventArgs e)
        {
            var gesture = TextBox.CopyGesture;
            return gesture is not null && e.Key == gesture.Key && e.KeyModifiers == gesture.KeyModifiers;
        }

        // The copy keystroke, claimed before the composer can swallow it.
        //
        // Tunnel and registered on the window, for a reason worth spelling out
        // because the bug it avoids is silent: Avalonia's TextBox handles the
        // copy gesture in OnKeyDown and marks it handled *unconditionally* —
        // even with nothing selected, where it copies nothing at all. The
        // composer is always the focused control here, so an ordinary bubbling
        // handler would never run, and a message selection would look copyable
        // while the clipboard never changed. Getting there first is the only
        // way. Same reasoning, and the same routing strategy, as the Enter
        // interception in OnInputKeyDown.
        private void OnCopyKeyDown(object? sender, KeyEventArgs e)
        {
            if (!IsCopyGesture(e)) return;

            var selected = SelectedMessageText();
            var composerHasSelection = !string.IsNullOrEmpty(Input.SelectedText);

            if (ChatCopy.Decide(composerHasSelection, selected is not null) != ChatCopy.Target.Message)
                return;

            // Handled only in the one case the panel is actually answering for.
            // Left alone otherwise, so the composer's own copy — and its own
            // no-op on an empty selection — behave exactly as they always have.
            e.Handled = true;
            _ = CopyToClipboardAsync(selected!);
        }

        // The selection a person can see in the transcript, if there is one.
        //
        // First rather than joined: SelectOnly keeps exactly one bubble
        // selected at a time, so "first" and "only" are the same block, and
        // asking for the first is how that invariant gets stated rather than
        // assumed.
        private string? SelectedMessageText()
        {
            foreach (var turn in _turns)
            {
                foreach (var block in turn.Blocks)
                {
                    if (!string.IsNullOrEmpty(block.SelectedText)) return block.SelectedText;
                }
            }

            return null;
        }

        // Every rule that spans more than one bubble, applied to a block as
        // TurnView builds it.
        private void Adopt(SelectableTextBlock block)
        {
            // Only one bubble may hold a selection at a time, so that the copy
            // gesture is never ambiguous about which of two highlighted
            // passages it meant. Tunnel, so the previous selection is gone
            // before SelectableTextBlock's own handler starts the new one.
            block.AddHandler(
                InputElement.PointerPressedEvent,
                (_, _) => SelectOnly(block),
                RoutingStrategies.Tunnel);

            // One menu shared by every bubble rather than one built per
            // paragraph: a long transcript is hundreds of paragraphs, and they
            // would be hundreds of identical two-item menus. Avalonia sets
            // Target to whichever control opened it, which is the only thing
            // the two items need to know.
            block.ContextFlyout = _bubbleMenu;
        }

        // Right-click on a message: copy what is selected, or the message
        // whole.
        //
        // The second item is not a convenience — it is the answer to the one
        // real limit of doing this with SelectableTextBlock. A rendered reply
        // is a stack of controls (a paragraph here, a code block there) and
        // Avalonia has no selection that spans two of them, so a drag can only
        // ever take one of those pieces. "Copy message" is how a person gets
        // the whole thing, and it hands back the original Markdown rather than
        // the rendering, which is what anyone pasting it into a terminal or an
        // editor actually wants.
        // Built once with the panel rather than on first use. Lazy would save
        // a two-item menu on a panel nobody right-clicks, and cost a null to
        // reason about on every path that asks whether it is open — including
        // the deactivate guard, which runs whether or not a bubble was ever
        // drawn.
        private readonly MenuFlyout _bubbleMenu;

        private MenuFlyout BuildBubbleMenu()
        {
            var copy = new MenuItem { Header = "Copy" };
            copy.Click += (_, _) =>
            {
                var text = SelectedMessageText();
                if (text is not null) _ = CopyToClipboardAsync(text);
            };

            var copyAll = new MenuItem { Header = "Copy message" };
            copyAll.Click += (_, _) =>
            {
                var text = MessageTextOf(_bubbleMenu.Target as SelectableTextBlock);
                if (!string.IsNullOrEmpty(text)) _ = CopyToClipboardAsync(text!);
            };

            var menu = new MenuFlyout { ItemsSource = new[] { copy, copyAll } };

            // Greyed rather than hidden when there is no selection, so the menu
            // is the same shape every time it opens and "Copy" is where it was
            // last time.
            menu.Opening += (_, _) => copy.IsEnabled = SelectedMessageText() is not null;

            return menu;
        }

        // The Markdown a bubble was drawn from, found by asking which turn
        // owns the block the menu opened on.
        private string? MessageTextOf(SelectableTextBlock? block)
        {
            if (block is null) return null;

            foreach (var turn in _turns)
            {
                foreach (var candidate in turn.Blocks)
                {
                    if (ReferenceEquals(candidate, block)) return turn.Text;
                }
            }

            return null;
        }

        // Whether a bubble's context menu is standing open.
        //
        // Asked by the deactivate handler for the same reason it already asks
        // about the enlarged portrait: a menu is its own window on this
        // platform, so opening one deactivates the panel, and hiding the panel
        // out from under a menu the user just opened would be a strange answer
        // to a right-click.
        private bool ContextMenuIsOpen => _bubbleMenu.IsOpen;

        // Everything except the block a drag has just started in.
        //
        // Avalonia selections are per-control and know nothing about each
        // other, so without this every paragraph a person had ever dragged
        // across would stay highlighted and the copy gesture would have to pick
        // between them.
        private void SelectOnly(SelectableTextBlock pressed)
        {
            foreach (var turn in _turns)
            {
                foreach (var block in turn.Blocks)
                {
                    if (!ReferenceEquals(block, pressed)) block.ClearSelection();
                }
            }
        }

        // Excluded from coverage for the same reason as TryClipboardText above:
        // the catch is the platform's clipboard refusing, which a headless
        // clipboard never does. The decision in front of it — whether this text
        // is copied at all — is the half worth covering, and is covered.
        [ExcludeFromCodeCoverage]
        private async Task CopyToClipboardAsync(string text)
        {
            var clipboard = Clipboard;
            if (clipboard is null) return;

            try { await clipboard.SetTextAsync(text); }
            catch { }
        }

        // Whether the paste this preempted turns out to be a picture can
        // only be known asynchronously, so the keystroke is always taken
        // first and one of two things is done with it here: a picture
        // becomes a pending attachment, and anything else — plain text, or
        // a clipboard with nothing this app can read — is pasted by hand,
        // since the TextBox's own paste handler never got the chance to.
        private async Task HandlePasteAsync()
        {
            var clipboard = Clipboard;
            if (clipboard is null) return;

            var bitmap = await TryClipboardBitmap(clipboard);

            if (bitmap is not null)
            {
                await AttachImageAsync(bitmap);
                return;
            }

            var text = await TryClipboardText(clipboard);

            if (!string.IsNullOrEmpty(text)) PasteText(text);
        }

        // Excluded from coverage: both exist to be a try/catch around another
        // process's clipboard, and neither catch is arrangeable here. Avalonia's
        // headless clipboard is a real implementation rather than a fake that can
        // be told to fail, so there is no way to make TryGetBitmapAsync or
        // TryGetTextAsync throw on cue — and what they throw for is a clipboard
        // owner that has gone away or is handing over a format it cannot actually
        // produce, which belongs to whatever app was copied from.
        //
        // The paste paths themselves are covered: a bitmap arriving is
        // AttachImageAsync, plain text is PasteText, and an empty clipboard is the
        // null both of these return.
        [ExcludeFromCodeCoverage]
        private static async Task<Bitmap?> TryClipboardBitmap(IClipboard clipboard)
        {
            try { return await clipboard.TryGetBitmapAsync(); }
            catch { return null; }
        }

        [ExcludeFromCodeCoverage]
        private static async Task<string?> TryClipboardText(IClipboard clipboard)
        {
            try { return await clipboard.TryGetTextAsync(); }
            catch { return null; }
        }

        // What TextBox.Paste() would have done with the same string: replace
        // the selection, or insert at the caret when there isn't one.
        private void PasteText(string text)
        {
            var current = Input.Text ?? "";
            var start = Math.Clamp(Math.Min(Input.SelectionStart, Input.SelectionEnd), 0, current.Length);
            var end = Math.Clamp(Math.Max(Input.SelectionStart, Input.SelectionEnd), 0, current.Length);

            Input.Text = current[..start] + text + current[end..];
            Input.CaretIndex = start + text.Length;
        }

        // Saved to disk immediately rather than held as a bitmap until Send:
        // Send needs a path to type into the terminal, and writing it once
        // here means a picture that sits pasted for an hour is written once
        // rather than re-encoded at the moment it is finally needed.
        //
        // The encode itself runs off the UI thread — the same reasoning
        // TurnView.LoadImage gives for decoding a received picture there:
        // a full-screen screenshot is large enough that PNG-encoding it is a
        // visible hitch on the thread that draws, and nothing here needs the
        // result before the next frame.
        private async Task AttachImageAsync(Bitmap bitmap)
        {
            var path = await Task.Run(() => ChatAttachments.Save(bitmap));

            _pendingImages.Add(new PendingImage(path, bitmap));
            Attachments.IsVisible = true;
        }

        private void Attachment_Remove_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;

            if ((sender as Control)?.DataContext is not PendingImage image) return;

            _pendingImages.Remove(image);
            Attachments.IsVisible = _pendingImages.Count > 0;
        }

        // What a pasted picture looks like before it has been sent: a path
        // already on disk (see AttachImage), and the same decoded bitmap the
        // clipboard handed over, small enough at 40pt that reusing it as
        // its own thumbnail costs nothing worth a second decode.
        private sealed record PendingImage(string Path, Bitmap Thumbnail);
        // Only while the input's first word is still being typed and starts
        // with "/" — a slash command is the whole message, not something
        // that can appear after other text, so anything past the first space
        // isn't a command being completed any more.
        private void UpdateSlashSuggestions()
        {
            var commands = SlashCommands;
            if (commands.Count == 0) { HideSlashSuggestions(); return; }

            var text = Input.Text ?? "";
            var caret = Math.Clamp(Input.CaretIndex, 0, text.Length);
            var token = text[..caret];

            if (token.Length == 0 || token[0] != '/' || token.Contains(' ') || token.Contains('\n'))
            {
                HideSlashSuggestions();
                return;
            }

            _slashMatches = commands
                .Where(c => c.Name.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Also closes once the only match is exactly what's already
            // typed — otherwise finishing a command by hand and pressing
            // Enter would "accept" it into itself instead of sending.
            if (_slashMatches.Count == 0
                || (_slashMatches.Count == 1 && string.Equals(_slashMatches[0].Name, token, StringComparison.OrdinalIgnoreCase)))
            {
                HideSlashSuggestions();
                return;
            }

            _slashSelected = 0;
            RenderSlashSuggestions();
            SlashBox.IsVisible = true;
        }

        private static readonly IBrush SlashRowFill = new SolidColorBrush(Colors.Transparent);
        private static readonly IBrush SlashRowSelected = new SolidColorBrush(Color.Parse("#33FFFFFF"));

        private void RenderSlashSuggestions()
        {
            SlashList.ItemsSource = _slashMatches
                .Select((c, i) => new SlashSuggestionView(c, i == _slashSelected ? SlashRowSelected : SlashRowFill))
                .ToList();
        }

        private void HideSlashSuggestions()
        {
            if (_slashMatches.Count == 0 && !SlashBox.IsVisible) return;

            _slashMatches = new List<SlashCommand>();
            _slashSelected = 0;
            SlashBox.IsVisible = false;
            SlashList.ItemsSource = null;
        }

        // Replaces the token being completed with the chosen command, the
        // way every other editor's autocomplete does — not sent outright.
        // Deciding a bare "/rename" is done and should go is the same
        // judgement call Send() already leaves to whoever is typing.
        private void AcceptSlashSuggestion(SlashCommand command)
        {
            var text = Input.Text ?? "";
            var caret = Math.Clamp(Input.CaretIndex, 0, text.Length);
            var rest = text[caret..];
            var replacement = command.Name + " ";

            Input.Text = replacement + rest;
            Input.CaretIndex = replacement.Length;

            // Last, not first: setting Text above already re-ran this via
            // TextChanged, and calling it again here is what makes the
            // outcome "closed" regardless of what that intermediate pass
            // computed.
            HideSlashSuggestions();
            Input.Focus();
        }

        private void SlashSuggestion_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;

            if ((sender as Control)?.DataContext is SlashSuggestionView view) AcceptSlashSuggestion(view.Command);
        }

        private sealed record SlashSuggestionView(SlashCommand Command, IBrush RowFill)
        {
            public string Name => Command.Name;
            public string Description => Command.Description;
        }

        // Cmd+= / Cmd+- / Cmd+0, Ctrl on Windows — see ChatZoom, which owns
        // both the key mapping and the ladder of sizes.
        private void OnZoomKeyDown(object? sender, KeyEventArgs e)
        {
            var command = ChatZoom.Gesture(e.Key, e.KeyModifiers);
            if (command == ChatZoom.Command.None) return;

            // Handled even when the size does not change — at either end of the
            // ladder the gesture was still understood, and letting it travel on
            // would put a "0" in the composer for someone pressing Cmd+0 twice.
            e.Handled = true;

            var next = ChatZoom.Apply(command, ClaudeBuddySettings.ChatTextScale);
            if (next.Equals(_textScale.Value)) return;

            ClaudeBuddySettings.ChatTextScale = next;
            ApplyTextScale();
        }

        // Pushes the saved scale into everything that draws chat text. Called
        // when the panel is built, after a zoom keystroke, and from the
        // settings slider through ReapplyTextScale below.
        private void ApplyTextScale()
        {
            var scale = ClaudeBuddySettings.ChatTextScale;

            _textScale.Value = scale;

            // The composer is chat text too — you read back what you typed. Its
            // heights go with it, or a doubled font size would be drawn into a
            // box still sized for one line of the old one.
            Input.FontSize = 11.5 * scale;
            Input.MinHeight = 26 * scale;
            Input.MaxHeight = 66 * scale;

            // The permission dialog sits between the transcript and the
            // composer and is the most important text in the window when it is
            // there at all — it is the thing being agreed to. PromptOptions
            // carries the size for its items, which have no handle of their own
            // (see the template's comment).
            PromptTitle.FontSize = 11 * scale;
            PromptOptions.FontSize = 11 * scale;
            PromptElsewhere.FontSize = 10.5 * scale;

            foreach (var turn in _turns) turn.Rescaled();
        }

        // The settings slider changes the same number this window's keyboard
        // does, so an open panel has to hear about it. Null-safe and
        // visibility-blind on purpose: a panel that exists but is hidden still
        // holds rows that will be shown again without being rebuilt.
        internal static void ReapplyTextScale() => _instance?.ApplyTextScale();

        private void OnPanelKeyDown(object? sender, KeyEventArgs e)
        {
            var isClose = e.Key == Key.Escape
                || (e.Key == Key.W && e.KeyModifiers.HasFlag(KeyModifiers.Meta));

            if (!isClose) return;

            e.Handled = true;

            // Escape while recording stops the recording and leaves the panel
            // up. Not a new rule: ScheduleFlyoutHide already refuses to hide the
            // arc while recording, because the control that stops it is on it. A
            // second Escape then dismisses.
            if (_owner?.IsRecording == true)
            {
                _owner.ToggleRecording();
                return;
            }

            HideNow();
        }

        private void Send()
        {
            var text = (Input.Text ?? "").Trim();
            if ((text.Length == 0 && _pendingImages.Count == 0) || _session is null) return;

            Input.Text = "";

            var images = _pendingImages.Select(p => p.Path).ToList();
            _pendingImages.Clear();
            Attachments.IsVisible = false;

            // Sending is the one time the view should jump to the bottom
            // regardless. The autoscroll rule elsewhere deliberately leaves you
            // where you are reading, but a message you just sent landing
            // somewhere off screen reads as it not having sent at all.
            ScrollToEndAfterLayout();

            // Deliberately not inserting the user's turn here: the session
            // raises TurnAdded for it, so one thing owns the transcript and a
            // failed send leaves nothing behind to clean up.
            if (images.Count > 0 && _session is IRemoteChatImages withImages)
            {
                _ = withImages.SendWithImagesAsync(text, images);
            }
            else
            {
                _ = _session.SendAsync(text);
            }
        }

        // Excluded from coverage: the one line a test cannot reach is Speak(),
        // which makes the machine make a noise — and an exclusion stops that line
        // being counted, not being run, so reaching it is not an option either.
        //
        // Everything this method decides is covered around it: already speaking
        // cancels instead of starting a second voice, and a conversation with no
        // assistant reply, or a blank one, speaks nothing. Those two arms return
        // before the call and are exercised in ChatPanelInteractionTests.
        [ExcludeFromCodeCoverage]
        private void SpeakLatest()
        {
            if (TextToSpeech.IsSpeaking)
            {
                TextToSpeech.Cancel();
                return;
            }

            var last = _turns.LastOrDefault(t => t.Role == ChatRole.Assistant);
            if (last is null || string.IsNullOrWhiteSpace(last.Text)) return;

            Speak(last.Text);
        }

        // TextToSpeech.Speak is itself excluded from coverage ("starts a speech
        // engine and makes the machine make a noise" — see its own comment) —
        // pulled out here so that exclusion covers only this one call and not
        // the decision above it, which a headless test can and does exercise
        // (IsSpeaking -> cancel, no eligible reply -> do nothing). This one
        // line — actually reaching a real utterance — has no headless seam and
        // is deliberately left uncovered rather than exercised for real.
        [ExcludeFromCodeCoverage]
        private static void Speak(string text) => TextToSpeech.Speak(text, ClaudeBuddySettings.SpeakVoice);

        private void ApplySpeakState(TextToSpeech.SpeakState state)
        {
            SpeakFill.Fill = state switch
            {
                TextToSpeech.SpeakState.Speaking => SpeakActiveFill,
                TextToSpeech.SpeakState.Preparing => SpeakPreparingFill,
                _ => IdleFill
            };

            SpeakGlyph.Text = state switch
            {
                TextToSpeech.SpeakState.Speaking => "⏹",
                TextToSpeech.SpeakState.Preparing => "⏳",
                _ => "\U0001F508"
            };
        }

        private bool _loadingOlder;

        // Older messages, fetched when the transcript is scrolled to its top.
        //
        // The awkward part is not the fetch, it is that content appearing above
        // where you are reading pushes what you were reading down the screen.
        // So the extent is measured before and after, and the offset is moved by
        // the difference — which leaves the same words under the pointer and the
        // new ones above, the way every message app that does this behaves.
        private async Task LoadOlderAsync()
        {
            if (_loadingOlder) return;
            if (_session is not IRemoteChatBacklog chat || !chat.HasMore) return;

            _loadingOlder = true;

            try
            {
                var before = Scroll.Extent.Height;

                if (!await chat.LoadOlderAsync(CancellationToken.None)) return;

                // The prepend itself happens on the event below; this only has
                // to restore the position once layout has caught up with it.
                //
                // Twice, at two priorities: one yield gets the items into the
                // tree, and the measure that gives them height happens after
                // that. Measuring too early reads the old extent, and the
                // correction is then silently zero — the failure looks like the
                // scroll jumping rather than like a missing yield.
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                var grew = Scroll.Extent.Height - before;
                if (grew > 0) Scroll.Offset = Scroll.Offset.WithY(Scroll.Offset.Y + grew);
            }
            finally
            {
                _loadingOlder = false;
            }
        }

        private void OnHistoryPrepended(int count)
        {
            if (_session is null) return;

            // Inserted at the front in order, rather than rebuilding the whole
            // list: rebuilding would discard every already-fetched picture and
            // start them downloading again.
            for (var i = 0; i < count && i < _session.History.Count; i++)
            {
                _turns.Insert(i, new TurnView(_session.History[i], _defaultBubble, _soleSpeaker, Adopt, _textScale));
            }
        }

        // Drops the row for a turn the session has retracted. Matched by
        // reference through the view wrapper, because the text is not unique —
        // two "working…" lines would be identical strings.
        private void OnTurnRemoved(ChatTurn turn)
        {
            for (var i = 0; i < _turns.Count; i++)
            {
                if (!ReferenceEquals(_turns[i].Source, turn)) continue;

                _turns.RemoveAt(i);
                return;
            }
        }

        private void OnHistoryReplaced()
        {
            if (_session is null) return;

            _turns.Clear();
            foreach (var turn in _session.History) _turns.Add(new TurnView(turn, _defaultBubble, _soleSpeaker, Adopt, _textScale));

            // Re-read rather than left at what Bind found.
            //
            // A remote panel can change what it *is* while open: it starts as a
            // messaging channel and upgrades to a live view of the far session
            // the moment that machine's Buddy answers, and the box's own
            // description of where a message goes changes with it — from
            // "message this session" to "type into its terminal". Read once at
            // bind, the box would go on describing the panel it used to be.
            ApplyComposerAffordances(_session);

            // Straight to the bottom rather than the pinned-only rule: a
            // transcript that has just been replaced wholesale has no scroll
            // position worth preserving, and the newest turn is the one you
            // opened the panel to read.
            ScrollToEndAfterLayout();
        }

        private void OnTurnAdded(ChatTurn turn)
        {
            _turns.Add(new TurnView(turn, _defaultBubble, _soleSpeaker, Adopt, _textScale));

            // Your own turn always brings the view with it; everything else
            // respects where you were reading.
            if (turn.Role == ChatRole.User)
            {
                ScrollToEndAfterLayout();
                return;
            }

            ScrollToEndIfPinned();
        }

        private void OnTurnUpdated(ChatTurn turn)
        {
            // Nothing to do to the collection: the view wraps the same object
            // and forwards its own change notification, so no row is recreated
            // and nothing can steal focus by being re-templated.
            ScrollToEndIfPinned();
        }

        private void OnStateChanged(RemoteChatState state)
        {
            StateDot.Fill = state switch
            {
                RemoteChatState.Connected => ConnectedFill,
                RemoteChatState.Connecting => SpeakPreparingFill,
                RemoteChatState.Error => RecordingFill,
                _ => IdleFill
            };
        }

        private void OnPromptChanged() => ApplyPrompt();

        // The far machine's name arrives on the roster, which normally lands
        // after the panel is already open — so the chip is redrawn rather than
        // only being set once at Bind.
        private void OnMachineChanged()
        {
            if (_owner is null) return;

            KindChipText.Text = KindChipLabel(
                _owner.KindGlyphText, _owner.KindLabel, _owner.PresenceLabel,
                (_session as IRemoteChatMachine)?.MachineName);
        }

        // What the chip says, including which machine when that is known.
        //
        // "another machine" is true and is not an answer: somebody with two of
        // them cannot act on it, which is what prompted this. The machine name
        // replaces the generic word rather than being appended to it — "⇄
        // another machine · avatar" says the same thing twice and is longer than
        // the header has room for.
        //
        // Falls back the moment the name is unknown, which is the ordinary case
        // for the first second a panel is open: the roster has not answered yet,
        // and naming no machine beats naming a guessed one.
        //
        // Pure and static so the wording is a unit test rather than a
        // screenshot. See CB-59.
        internal static string KindChipLabel(
            string glyph, string? kind, string? presence, string? machine)
        {
            if (kind is null) return "";

            var what = string.IsNullOrWhiteSpace(machine) ? kind : machine;

            return presence is null
                ? $"{glyph}  {what}"
                : $"{glyph}  {what} · {presence}";
        }


        // --- the wait, while it is happening --------------------------------

        // Ticks the elapsed figure while a fetch is in flight.
        //
        // A timer rather than a binding because the thing that changes is the
        // clock, not the session: nothing raises an event once a second, and
        // asking the session to would put a timer in the transport instead of
        // in the one place that draws it.
        private DispatcherTimer? _waitTick;

        private void OnWaitChanged()
        {
            if (_session is IRemoteChatFetchWait waiting) ShowWait(waiting);
            else HideWait();
        }

        private void ShowWait(IRemoteChatFetchWait waiting)
        {
            if (waiting.WaitingSince is not { } since)
            {
                HideWait();
                return;
            }

            FetchWaitBox.IsVisible = true;
            FetchWaitHint.Text = RemoteControlChatSession.WaitHint;

            void Paint() => FetchWaitText.Text = RemoteControlChatSession.WaitLabel(
                DateTimeOffset.Now - since, waiting.WaitingFor);

            Paint();

            // A second, because that is the resolution of what it says. Half of
            // one would redraw twice for every change and a longer one would
            // make a counter that is supposed to prove liveness look stopped.
            _waitTick ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _waitTick.Tick -= OnWaitTick;
            _waitTick.Tick += OnWaitTick;
            _waitTick.Start();
        }

        private void OnWaitTick(object? sender, EventArgs e)
        {
            if (_session is IRemoteChatFetchWait waiting
                && waiting.WaitingSince is { } since)
            {
                FetchWaitText.Text = RemoteControlChatSession.WaitLabel(
                    DateTimeOffset.Now - since, waiting.WaitingFor);
                return;
            }

            HideWait();
        }

        private void HideWait()
        {
            _waitTick?.Stop();
            FetchWaitBox.IsVisible = false;
        }


        // A dialog the session has stopped on, or nothing.
        //
        // The options are shown whether or not replying is switched on, and
        // clicking one while it is off produces the same explanation in the
        // transcript that sending a message would. Same reasoning as the
        // composer: the panel doesn't hide what is happening because you can't
        // act on it yet, and the session — which owns the rule — is the thing
        // that states it.
        private void ApplyPrompt()
        {
            var prompt = (_session as IRemoteChatPrompts)?.Prompt;

            if (prompt is null)
            {
                PromptBox.IsVisible = false;
                PromptOptions.ItemsSource = null;
                return;
            }

            PromptTitle.Text = prompt.Title;

            // No options means the screen couldn't be read. The box still
            // appears — something is waiting and the transcript won't say so —
            // but the only thing offered is the terminal.
            PromptOptions.ItemsSource = prompt.Options.Count > 0 ? prompt.Options : null;
            PromptOptions.IsVisible = prompt.Options.Count > 0;

            PromptBox.IsVisible = true;
        }

        private void PromptOption_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;

            if ((sender as Control)?.DataContext is not ChatPromptOption option) return;
            if (_session is not IRemoteChatPrompts prompts) return;

            _ = prompts.AnswerAsync(option);
        }

        private void PromptElsewhere_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;

            if (_session is not IRemoteChatPrompts prompts) return;

            prompts.AnswerElsewhere();

            // Dismissed, because this asked to be somewhere else. Leaving the
            // panel up over the terminal it just brought forward would be
            // covering the dialog it sent you to answer.
            HideNow();
        }

        // Whether the view is sitting at the bottom — read *now*, on the same
        // tick as the turn that prompted the question, rather than after a
        // yield.
        //
        // The row that was just added or grown has not been measured yet, so the
        // extent still describes the transcript you were reading and an offset
        // at the bottom of it still reads as the bottom. A yield later the new
        // content has height, the extent has grown past where you are sitting,
        // and the same position answers "no" — so autoscroll switches itself off
        // partway through a streaming reply and the panel is left in the middle
        // of it. That is the intermittent half of the same complaint the bind
        // path above fixes: it depended on whether measure had happened yet.
        private bool IsPinnedToBottom =>
            Scroll.Offset.Y >= Scroll.Extent.Height - Scroll.Viewport.Height - 8;

        // Only when already at the bottom, so reading back through a long reply
        // isn't yanked forward as it grows.
        private void ScrollToEndIfPinned()
        {
            if (IsPinnedToBottom) ScrollToEndAfterLayout();
        }

        // To the newest message, after layout has caught up with the rows that
        // put it there.
        //
        // Twice, at two priorities, for the reason LoadOlderAsync spells out at
        // length: one yield gets the rows into the visual tree, and the measure
        // that gives them height happens after that. A single ScrollToEnd() at
        // Loaded priority — which is what every one of these call sites used to
        // be — reads the extent the transcript had *before* those rows existed
        // and lands short of the bottom by however much was added. Loading a
        // whole transcript at once adds a lot, so "short of the bottom" is not a
        // few pixels; it is the middle of the conversation.
        //
        // The first call is kept rather than only doing the late one: it puts
        // the view roughly right on the frame the panel appears, so the
        // correction is a settle rather than a visible jump.
        private void ScrollToEndAfterLayout()
        {
            Dispatcher.UIThread.Post(() =>
            {
                Scroll.ScrollToEnd();
                Dispatcher.UIThread.Post(() => Scroll.ScrollToEnd(), DispatcherPriority.Background);
            }, DispatcherPriority.Loaded);
        }

        private void HideNow()
        {
            if (_session is not null) Drafts[_session.SessionId] = Input.Text ?? "";

            StopAvatarAnimation();

            // Nothing on screen to beat for. Without this the timer outlives the
            // panel and goes on scaling a hidden TextBlock 20 times a second for
            // as long as the app runs.
            StopHeartAnimation();

            AvatarPopup.Close();

            // Cleared with the panel, not left standing: the next session bound
            // here is very unlikely to be waiting on the same dialog, and a
            // stale one would offer buttons that answer nothing.
            PromptBox.IsVisible = false;
            PromptOptions.ItemsSource = null;

            // Detached while hidden. The panel is a singleton that stays alive
            // between openings, and a hidden panel left subscribed goes on
            // appending a row per event for a conversation nobody is watching —
            // the session's own history is bounded, this collection was not.
            // Bind rebuilds from History anyway, so there is nothing to keep.
            Unbind();

            _owner?.SetChatOpen(false);
            Hide();
        }

        // The row's own view. Exists so the template can bind colour, shape and
        // alignment per role without the transport's ChatTurn knowing what a
        // brush is — and so the three roles share one template instead of three.
        private sealed class TurnView : System.ComponentModel.INotifyPropertyChanged
        {
            private readonly ChatTurn _turn;
            private readonly Speaker? _soleSpeaker;
            private readonly Color? _defaultBubble;
            private readonly Action<SelectableTextBlock> _adopt;
            private readonly TextScale _scale;

            // soleSpeaker is who is talking when the transcript does not say.
            // A room stamps every turn with its speaker because there are
            // several; a one-to-one session — a Claude Code or Codex terminal,
            // or a single agent — stamps none, because there is only one and it
            // was obvious to whoever wrote the transport. It is not obvious in
            // the bubbles, which is the whole point of the chip.
            // adopt is how each selectable run of text gets handed to the
            // panel as it is built, for the rules that span more than one
            // bubble. Passed in rather than reached for through the singleton,
            // so a TurnView built by a test is a TurnView and not half a window.
            public TurnView(ChatTurn turn, Color? defaultBubble, Speaker? soleSpeaker,
                Action<SelectableTextBlock> adopt, TextScale scale)
            {
                _turn = turn;
                _defaultBubble = defaultBubble;
                _soleSpeaker = soleSpeaker;
                _adopt = adopt;
                _scale = scale;

                turn.PropertyChanged += (_, e) =>
                {
                    // A streaming turn replaces its whole text, so the rendered
                    // Markdown has to be thrown away with it. Without this the
                    // first snapshot of a reply is the only one ever drawn.
                    if (e.PropertyName == nameof(ChatTurn.Text))
                    {
                        _body = null;
                        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Body)));
                    }

                    // A live turn can start with no picture and gain one once
                    // OpenClawChatSession.TryResolveLiveImage resolves it
                    // against the gateway's own history — this row already
                    // exists by then, so it has to notice rather than being
                    // recreated. !HasImage guards against loading twice were
                    // ImageUrl to change again after already resolving once.
                    if (e.PropertyName == nameof(ChatTurn.ImageUrl) && !HasImage
                        && !string.IsNullOrEmpty(turn.ImageUrl))
                    {
                        LoadImage();
                    }

                    // Same shape, for a picture resolved via
                    // OpenClawChatSession.TryResolveLocalMedia (CB-88) rather
                    // than a URL — an agent's own generated file, fetched as
                    // bytes rather than fetched from one.
                    if (e.PropertyName == nameof(ChatTurn.ImageBytes) && !HasImage
                        && turn.ImageBytes is { Length: > 0 } lateBytes)
                    {
                        LoadImageBytes(lateBytes);
                    }

                    // CB-93: ImageNote and ImageNoteDetail are set together
                    // (see LoadImage below), but only ImageNote's own setter
                    // raises a change — ImageNoteDetail is plain, the same
                    // pairing ChatTurn.ImageAlt already has with
                    // ImageBytes/ImageUrl. So the one event that does fire
                    // has to stand in for both bindings, HasImageNote (the
                    // row's own IsVisible) included.
                    if (e.PropertyName == nameof(ChatTurn.ImageNote))
                    {
                        PropertyChanged?.Invoke(this,
                            new System.ComponentModel.PropertyChangedEventArgs(nameof(HasImageNote)));
                        PropertyChanged?.Invoke(this,
                            new System.ComponentModel.PropertyChangedEventArgs(nameof(ImageNoteDetail)));
                    }

                    PropertyChanged?.Invoke(this, e);
                };

                if (!string.IsNullOrEmpty(turn.ImageUrl)) LoadImage();
                else if (turn.ImageBytes is { Length: > 0 } bytes) LoadImageBytes(bytes);
            }

            // The turn this row was built from, so the panel can find a row
            // again by identity. Text is not unique enough — two "working…"
            // lines would be the same string.
            public ChatTurn Source => _turn;

            public ChatRole Role => _turn.Role;
            public string Text => _turn.Text;

            public bool HasText => !string.IsNullOrWhiteSpace(_turn.Text);

            // The rendered Markdown, rebuilt when the text changes.
            //
            // A control rather than a bound string because a reply has
            // structure — code blocks want a monospace box, list items want a
            // bullet and a hanging indent, and neither is expressible as one
            // TextBlock. Cached because OpenClaw streams: a snapshot arrives per
            // delta, and reparsing on read would reparse per layout pass too.
            private Control? _body;

            public Control Body => _body ??= BuildBody();

            private Control BuildBody()
            {
                // Thrown away with the body they belong to. A streaming turn
                // rebuilds its body per delta (see the PropertyChanged hook
                // above), and a list that kept growing across those rebuilds
                // would have the panel searching controls that are no longer
                // on screen for a selection the user cannot see.
                _blocks.Clear();

                var stack = new StackPanel { Spacing = 4 };

                foreach (var block in ChatMarkdown.Parse(_turn.Text))
                    stack.Children.Add(BuildBlock(block));

                // A turn whose text is only whitespace still needs something to
                // hand back; HasText hides it either way.
                if (stack.Children.Count == 0)
                    stack.Children.Add(Line(_turn.Text));

                return stack;
            }

            // Every selectable run of text in this bubble, in the order it is
            // drawn. The panel asks for these rather than walking the visual
            // tree: a turn knows what it built, whereas a tree walk would only
            // find what has been realised and laid out, which is a different
            // question with a different answer under a headless renderer.
            //
            // Empty until Body has been read, which is correct rather than
            // merely convenient — a bubble nobody has drawn cannot be holding
            // a selection.
            private readonly List<SelectableTextBlock> _blocks = new();

            public IReadOnlyList<SelectableTextBlock> Blocks => _blocks;

            // A paragraph, heading, list item, quote or code block that a
            // person can drag across and copy out.
            //
            // Focusable is off, which is the whole trick and worth stating
            // plainly: SelectableTextBlock ships Focusable=true, and taking it
            // at face value would break the rule ChatPanel.axaml sets out —
            // that the composer is the only focusable thing in the window, so
            // nothing can pull focus out of it mid-reply. Selection does not
            // need focus: Avalonia drives it entirely from the pointer handlers
            // and never calls Focus() itself. What focus *is* needed for is the
            // copy keystroke, which is why the panel claims that gesture on the
            // tunnel route instead — see OnCopyKeyDown.
            private SelectableTextBlock Selectable(
                string? text = null, FontFamily? font = null, double? size = null, IBrush? ink = null)
            {
                var block = new SelectableTextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Focusable = false,
                    Cursor = TextCursor,
                    SelectionBrush = SelectionFill
                };

                if (text is not null) block.Text = text;
                if (font is not null) block.FontFamily = font;
                if (size is not null) block.FontSize = size.Value;
                if (ink is not null) block.Foreground = ink;

                _blocks.Add(block);

                // Handed to the panel to finish: it owns the rules that span
                // more than one bubble — that only one may hold a selection,
                // and that they share a single context menu — and neither is a
                // TurnView's to enforce from inside one row.
                _adopt(block);

                return block;
            }

            private Control BuildBlock(ChatMarkdown.MdBlock block)
            {
                switch (block.Kind)
                {
                    case ChatMarkdown.MdKind.Code:
                        // Wrapped rather than scrolled: the bubble is 244pt and
                        // a horizontal scrollbar per code block in a column of
                        // them is worse than a wrapped line.
                        return new Border
                        {
                            Background = CodeBackground,
                            CornerRadius = new CornerRadius(4),
                            Padding = new Thickness(6, 4),
                            Margin = new Thickness(0, 1),
                            Child = Selectable(block.Text, Mono, Size - Points(1), CodeInk)
                        };

                    case ChatMarkdown.MdKind.Heading:
                    {
                        var heading = Line(block.Text);
                        heading.FontWeight = FontWeight.SemiBold;

                        // Only two sizes. Six levels of heading inside a bubble
                        // this size would be a distinction nobody could see.
                        heading.FontSize = block.Depth <= 2 ? Size + Points(1) : Size;
                        heading.Margin = new Thickness(0, 2, 0, 0);
                        return heading;
                    }

                    case ChatMarkdown.MdKind.Quote:
                    {
                        var quote = Line(block.Text);
                        quote.Opacity = 0.75;
                        return new Border
                        {
                            BorderBrush = QuoteEdge,
                            BorderThickness = new Thickness(2, 0, 0, 0),
                            Padding = new Thickness(6, 0, 0, 0),
                            Child = quote
                        };
                    }

                    case ChatMarkdown.MdKind.Bullet:
                    case ChatMarkdown.MdKind.Ordered:
                    {
                        // Two columns so the text hangs under itself rather than
                        // wrapping back beneath the bullet.
                        var row = new Grid
                        {
                            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                            Margin = new Thickness(Points(block.Depth * 10), 0, 0, 0)
                        };

                        var marker = Line(block.Marker);
                        marker.Margin = new Thickness(0, 0, 5, 0);
                        marker.Opacity = 0.7;

                        var text = Line(block.Text);
                        Grid.SetColumn(text, 1);

                        row.Children.Add(marker);
                        row.Children.Add(text);
                        return row;
                    }

                    default:
                        return Line(block.Text);
                }
            }

            // One line of inline Markdown as a TextBlock of styled runs.
            private SelectableTextBlock Line(string text)
            {
                var block = Selectable();
                block.FontSize = Size;
                block.FontStyle = Style;
                block.Foreground = Ink;

                var spans = ChatMarkdown.Inline(text);

                // No markup at all is the common case, and setting Text avoids
                // building an Inlines collection for every plain line.
                if (spans.Count == 1 && spans[0].Style == ChatMarkdown.MdStyle.Normal)
                {
                    block.Text = spans[0].Text;
                    return block;
                }

                foreach (var span in spans)
                {
                    var run = new Run(span.Text);

                    switch (span.Style)
                    {
                        case ChatMarkdown.MdStyle.Bold:
                            run.FontWeight = FontWeight.SemiBold;
                            break;

                        case ChatMarkdown.MdStyle.Italic:
                            run.FontStyle = FontStyle.Italic;
                            break;

                        case ChatMarkdown.MdStyle.BoldItalic:
                            run.FontWeight = FontWeight.SemiBold;
                            run.FontStyle = FontStyle.Italic;
                            break;

                        // Avalonia's Run has no background, so inline code is
                        // told apart by face and colour rather than by a chip.
                        case ChatMarkdown.MdStyle.Code:
                            run.FontFamily = Mono;
                            run.FontSize = Size - Points(0.5);
                            run.Foreground = CodeInk;
                            break;

                        case ChatMarkdown.MdStyle.Link:
                            run.Foreground = LinkInk;
                            run.TextDecorations = TextDecorations.Underline;
                            break;
                    }

                    block.Inlines?.Add(run);
                }

                return block;
            }

            // An I-beam over text a person can drag across, because nothing else
            // in this window can be dragged across and the cursor is the only
            // thing that says so before they try.
            private static readonly Cursor TextCursor = new(StandardCursorType.Ibeam);

            // Light enough to read the ink through on a coloured bubble, which
            // a solid system highlight is not — bubbles here are tinted per
            // agent, so the one selection colour has to sit on any of them.
            private static readonly IBrush SelectionFill =
                new SolidColorBrush(Color.Parse("#59FFFFFF"));

            private static readonly FontFamily Mono = new("Menlo,SF Mono,Consolas,monospace");
            private static readonly IBrush CodeBackground = new SolidColorBrush(Color.Parse("#33000000"));
            private static readonly IBrush CodeInk = new SolidColorBrush(Color.Parse("#FFD9A0"));
            private static readonly IBrush LinkInk = new SolidColorBrush(Color.Parse("#9FD0FF"));
            private static readonly IBrush QuoteEdge = new SolidColorBrush(Color.Parse("#4DFFFFFF"));
            public bool HasImage => _image is not null;

            // CB-93: why a picture that should have shown didn't, drawn in
            // the slot it would have occupied. See ChatTurn.ImageNote's own
            // header for why this is a line rather than a tooltip on the
            // 📎 marker or a System-turn note appended to the end of the
            // transcript.
            public string? ImageNote => _turn.ImageNote;

            public bool HasImageNote => !string.IsNullOrEmpty(_turn.ImageNote);

            public string? ImageNoteDetail => _turn.ImageNoteDetail;

            private Bitmap? _image;
            private byte[]? _bytes;

            // Excluded from coverage: the catch around it cannot be reached with a
            // picture, and the picture is the only thing this is ever handed.
            // Avalonia's Bitmap.DecodeToWidth does not throw on rubbish — five
            // random bytes and a truncated PNG both come back as an ordinary
            // 456x456 bitmap, which is the finding recorded in
            // ChatPanelMarkdownTests. So the fallback this catch promises never
            // happens, and a test cannot make it happen either.
            [ExcludeFromCodeCoverage]
            private static Bitmap DecodeAtDrawWidth(byte[] bytes)
            {
                using var stream = new MemoryStream(bytes);
                return Bitmap.DecodeToWidth(stream, 456);
            }

            // Full size, in the OS's own viewer — see OpenClawMedia for why this
            // isn't a window of ours.
            // Excluded from coverage: ends in OpenClawMedia.Open, which writes the
            // picture to a temporary file and hands it to the OS — Preview.app on
            // macOS. A test that reached it would open a window on the machine
            // running the suite. The guard in front of it, a turn with no bytes to
            // open, is the half worth covering and is covered.
            [ExcludeFromCodeCoverage]
            public void OpenFullSize()
            {
                if (_bytes is null) return;

                OpenInViewer(_bytes, _turn.ImageAlt);
            }

            // OpenClawMedia.Open is itself excluded from coverage — it writes a
            // real file and hands it to the OS's own viewer (/usr/bin/open on
            // macOS). Wrapped here so that exclusion covers only this one call
            // rather than the guard above it; a headless test proves the guard
            // without ever actually launching a viewer, and this one line is
            // left uncovered rather than exercised for real.
            [ExcludeFromCodeCoverage]
            private static void OpenInViewer(byte[] bytes, string alt) => OpenClawMedia.Open(bytes, alt);

            public Bitmap? Image
            {
                get => _image;
                private set
                {
                    _image = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Image)));
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(HasImage)));
                }
            }

            // Fetched when the row is created rather than when it scrolls into
            // view. A transcript holds thirty turns at most and only a few carry
            // pictures, so the simpler thing is also cheap enough — and the
            // bytes are cached by url, so reopening the panel costs nothing.
            public async void LoadImage()
            {
                if (string.IsNullOrEmpty(_turn.ImageUrl)) return;

                var url = _turn.ImageUrl!;
                var bytes = await OpenClawSessions.FetchMediaAsync(url, CancellationToken.None);
                if (bytes is { Length: > 0 })
                {
                    await DecodeAndShowAsync(bytes);
                    return;
                }

                // CB-93: this is the dominant failure site — every reopen,
                // reconnect and scroll reads history back through here, where
                // the live path above only fires once per streamed reply. A
                // path outside the gateway's media allowlist used to leave
                // this exact row with nothing in it and no explanation.
                if (!OpenClawMediaRefusal.ShouldAskWhy(bytes, url)) return;

                // Never null here: ShouldAskWhy already confirmed url starts
                // with AssistantMediaRoute, which is the one thing
                // PathFromUrl checks before unescaping the rest.
                var path = OpenClawMediaRefusal.PathFromUrl(url)!;

                var json = await OpenClawSessions.FetchLocalMediaMetaAsync(path, CancellationToken.None);
                _turn.ImageNoteDetail = OpenClawMediaRefusal.Detail(json, path);
                _turn.ImageNote = OpenClawMediaRefusal.Explain(json);
            }

            // The bytes are already in hand — decoded from a local CLI's own
            // transcript (ChatTranscript's image handling), or read straight
            // back off a picture the panel itself just wrote to disk — so
            // there is nothing to fetch, only to decode.
            public async void LoadImageBytes(byte[] bytes) => await DecodeAndShowAsync(bytes);

            private async Task DecodeAndShowAsync(byte[] bytes)
            {
                // Kept as they arrived, not as they were decoded: opening the
                // picture full size should hand over the original rather than
                // the 456px copy the bubble draws.
                _bytes = bytes;

                try
                {
                    // Decoded on a worker: this awaits a network fetch that
                    // usually starts on the UI thread, so the continuation lands
                    // back there, and decoding an 840x1024 PNG on the thread
                    // that draws is a visible hitch per picture.
                    //
                    // Decoded to the width it is drawn at, twice over for
                    // Retina: keeping them at full size to show them at 228
                    // would be most of a megabyte of pixels each, held for as
                    // long as the panel is open.
                    var bitmap = await Task.Run(() => DecodeAtDrawWidth(bytes));

                    Dispatcher.UIThread.Post(() => Image = bitmap);
                }
                catch
                {
                    // Not an image, or not one we can decode. The message keeps
                    // whatever text it had.
                }
            }

            // The name was filled in after this row was built. Everything
            // drawn from it has to be asked again — the chip is bound to five
            // separate properties and a stale one leaves half a chip.
            public void SpeakerChanged()
            {
                foreach (var name in new[]
                {
                    nameof(HasSpeaker), nameof(SpeakerName), nameof(ShowSpeakerName),
                    nameof(SpeakerInitials), nameof(SpeakerAvatar),
                    nameof(HasSpeakerAvatar), nameof(HasSpeakerInitials),
                    nameof(SpeakerChip), nameof(SpeakerChipInk)
                })
                {
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
                }
            }

            private bool IsSystem => _turn.Role == ChatRole.System;

            // A named speaker is never *you*, whatever role the transport gave
            // it. Another agent's message in a channel arrives with role "user"
            // — it is user-role input as far as this agent is concerned — and
            // taking that at face value drew it right-aligned in your own blue,
            // so a room full of agents looked like you talking to yourself.
            private bool IsUser => _turn.Role == ChatRole.User && !HasSpeaker;

            public bool HasSpeaker => !string.IsNullOrEmpty(SpeakerName);

            // Falls back to the session's one agent, but only on the agent's
            // own turns. Your messages are yours whoever else is in the room,
            // and a system note is about the conversation rather than in it —
            // stamping either with the agent's name would say it spoke them.
            public string SpeakerName =>
                !string.IsNullOrEmpty(_turn.Speaker) ? _turn.Speaker!
                : _turn.Role == ChatRole.Assistant ? _soleSpeaker?.Name ?? ""
                : "";

            // The name in words, beside the chip, only when the transcript
            // named the speaker itself. In a room that is worth the line: eight
            // agents talk and the names are how you follow who. In a one-to-one
            // it is the panel's own title repeated down the whole transcript,
            // which says nothing the header has not already said — so the chip
            // goes on alone, the way a messaging app shows a face and not a
            // name against every message from one person.
            public bool ShowSpeakerName => !string.IsNullOrEmpty(_turn.Speaker);

            // The speaker's own picture, when the gateway has one for them.
            //
            // The first frame only, even for an animated avatar. The header
            // animates its portrait with a timer; a room is a scrolling list of
            // dozens of turns, and one timer per row to animate a 16-pixel
            // circle is a lot of machinery for something too small to read a
            // motion in. The portrait is the place you look, and it still moves.
            public Bitmap? SpeakerAvatar =>
                OpenClawSessions.AvatarForAgentName(SpeakerName)?.Frames.FirstOrDefault();

            public bool HasSpeakerAvatar => SpeakerAvatar is not null;

            // Initials are the fallback, not the design: an agent with a face
            // shows the face, and the letters are for the ones without one and
            // for a name this cannot resolve to a single agent.
            public bool HasSpeakerInitials => !HasSpeakerAvatar;

            // The speaker's initials, for the chip beside their name.
            //
            // A name alone in a colour was enough while a room had two or three
            // agents in it. With eight it is a column of similar words in
            // similar hues, and the eye has to read each one — a shape it can
            // recognise without reading is what a room view is for. Same
            // letters the agent's own orb shows, so the chip and the orb are
            // recognisably the same agent.
            public string SpeakerInitials => OrbGlyph.Initials(SpeakerName);

            // Filled in the speaker's own colour, with the panel's own
            // background punched through it for the letters. Ink on a tinted
            // chip was the alternative and reads as a third bubble; a solid
            // dot reads as a person.
            public IBrush SpeakerChip =>
                SpeakerColor is { } c ? new SolidColorBrush(c) : SystemInk;

            public IBrush SpeakerChipInk =>
                SpeakerColor is not null ? ChipInk : SystemInk;

            // Near-black rather than the window's background brush: the chip is
            // a solid colour whatever is behind it, so the letters only have to
            // read against the chip.
            private static readonly IBrush ChipInk =
                new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));

            // The agent's own colour, the one their orb's ring is drawn in.
            private Color? SpeakerColor
            {
                get
                {
                    if (!string.IsNullOrEmpty(_turn.SpeakerColor)
                        && Color.TryParse(_turn.SpeakerColor, out var named))
                    {
                        return named;
                    }

                    // Only what the session said. Your own bubbles keep their
                    // blue, and a system note keeps none — the fallback is the
                    // agent's colour, and neither of those is the agent.
                    return IsSystem || IsUser ? null : _defaultBubble;
                }
            }

            public IBrush SpeakerInk =>
                SpeakerColor is { } c ? new SolidColorBrush(c) : SystemInk;

            public HorizontalAlignment Side => IsSystem
                ? HorizontalAlignment.Center
                : IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;

            // Blue for you, grey for the agent — the arrangement every messaging
            // app has trained everyone to read without being told. The blue is
            // the same #4A90D9 the speak button already uses for "live", so the
            // app keeps one accent rather than acquiring a second.
            // A speaker's own colour, at low alpha. Full strength would be a
            // wall of saturated colour in a busy room — the name above it is
            // drawn in the same hue at full strength, which is enough to tie the
            // two together and to the orb.
            public IBrush Bubble => IsSystem
                ? Transparent
                : SpeakerColor is { } c
                    ? new SolidColorBrush(Color.FromArgb(0x3D, c.R, c.G, c.B))
                    : IsUser ? UserBubble : AgentBubble;

            public IBrush Ink => IsSystem ? SystemInk : BubbleInk;

            // The corner nearest the speaker is squared off. It is the one
            // detail that makes a column of bubbles read as two people talking
            // rather than as a list.
            public CornerRadius Corners => IsSystem
                ? new CornerRadius(0)
                : IsUser ? new CornerRadius(11, 11, 3, 11) : new CornerRadius(11, 11, 11, 3);

            public Thickness Pad => IsSystem
                ? new Thickness(0, 1)
                : HasImage && !HasText ? new Thickness(5) : new Thickness(9, 6);

            // Bubbles sit closer to their own side's previous bubble than to the
            // other side's, which is what gives a conversation its rhythm.
            public Thickness Gap => IsSystem
                ? new Thickness(0, 2, 0, 2)
                : IsUser ? new Thickness(40, 2, 0, 3) : new Thickness(0, 2, 40, 3);

            // Set from outside — see ChatPanel's Scroll.SizeChanged handler and
            // the _turns.CollectionChanged hook that seeds it on every new
            // turn — rather than read some ambient static, so a TurnView stays
            // a plain value holder that answers what it's told.
            private double _availableWidth = 306;

            public double AvailableWidth
            {
                get => _availableWidth;
                set
                {
                    if (_availableWidth.Equals(value)) return;

                    _availableWidth = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MaxBubbleWidth)));
                }
            }

            // A fraction of what's actually available rather than a fixed
            // 244px, now that the panel is user-resizable: a message keeps a
            // comfortable line length instead of either hugging the old cap
            // forever on a wide window or never using the room a narrow one
            // freed up. System lines run almost the full width because they
            // read as a note about the conversation, not one side of it.
            public double MaxBubbleWidth => Math.Max(140, AvailableWidth * (IsSystem ? 0.95 : 0.8));

            // Every size in a bubble, multiplied. Four different numbers on
            // purpose — a system note is quieter than a message, a name is
            // quieter than what it said, a timestamp quieter still — and the
            // multiplier keeps that hierarchy instead of flattening it, which
            // is what a single "chat font size" setting would have done.
            private double Scale => _scale.Value;

            public double Size => (IsSystem ? 10 : 11.5) * Scale;

            // A measurement written in shipped points, in the size it should be
            // at the current scale.
            //
            // The differences inside a bubble are what make it readable — a
            // heading a point above its prose, code a point below it, a nested
            // list indented from its parent. Left as raw constants those gaps
            // stay the same absolute size while everything around them grows,
            // so the hierarchy quietly flattens exactly when someone has said
            // they are having trouble reading it. Caught by a test asserting
            // that every part of a turn grows by the same factor.
            private double Points(double shipped) => shipped * Scale;

            public double SpeakerNameSize => 9.5 * Scale;

            public double TimeSize => 8.5 * Scale;

            // The chip grows with the name beside it. Left at 16pt it would sit
            // next to 20pt text looking like a bullet point, and the initials
            // inside it would spill out of their own circle.
            public double ChipSize => 16 * Scale;

            public CornerRadius ChipCorners => new(8 * Scale);

            public double ChipTextSize => 8 * Scale;

            // Called when the scale changes under a row that already exists.
            // The body is rendered Markdown with the sizes baked into each
            // TextBlock, so it is thrown away and rebuilt rather than restyled
            // — the same thing a streaming turn's text change already does.
            public void Rescaled()
            {
                _body = null;

                foreach (var name in new[]
                         {
                             nameof(Body), nameof(Size), nameof(SpeakerNameSize), nameof(TimeSize),
                             nameof(ChipSize), nameof(ChipCorners), nameof(ChipTextSize)
                         })
                {
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
                }
            }

            public FontStyle Style => IsSystem ? FontStyle.Italic : FontStyle.Normal;

            public bool ShowTime => !IsSystem;

            // Time alone for today, date and time for anything older — a
            // conversation that has been running since yesterday should say so,
            // and one from this afternoon shouldn't waste the width.
            public string TimeText
            {
                get
                {
                    var at = _turn.At.ToLocalTime();
                    return at.Date == DateTimeOffset.Now.Date
                        ? at.ToString("HH:mm")
                        : at.ToString("d MMM HH:mm");
                }
            }

            private static readonly IBrush UserBubble = new SolidColorBrush(Color.Parse("#E04A90D9"));
            private static readonly IBrush AgentBubble = new SolidColorBrush(Color.Parse("#26FFFFFF"));
            private static readonly IBrush Transparent = new SolidColorBrush(Colors.Transparent);
            private static readonly IBrush BubbleInk = new SolidColorBrush(Color.Parse("#F2FFFFFF"));
            private static readonly IBrush SystemInk = new SolidColorBrush(Color.Parse("#8CFFFFFF"));

            public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        }
    }
}
