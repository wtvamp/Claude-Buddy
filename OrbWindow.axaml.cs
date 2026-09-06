using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Controls.Shapes;
using Avalonia.Styling;
using Avalonia.Threading;

namespace ClaudeBuddy
{
    public partial class OrbWindow : Window
    {
        // The three state colours live in OrbColors now — they're settable, and
        // the tray icon reads the same three, so a static field here would have
        // been a second copy for both reasons.
        //
        // A session's /color goes on the orb's border and letter, leaving the
        // fill to mean what it always has.
        //
        // These are Claude Code's own accent colors, which it renders as
        // xterm-256 indices (index = 16 + 36r + 6g + b over the levels
        // 0/95/135/175/215/255). Three are confirmed from what Claude Code
        // actually emitted in a terminal — green is index 35, and the two
        // auto-assigned accents seen in other sessions were 37 and 175. The
        // rest are the same-band cube colors for their hue, i.e. educated
        // guesses; correct one by reading the escape sequence Claude Code
        // writes for that color (`tmux capture-pane -p -e`, look for
        // `38;5;<n>`) if one ever looks off.
        private static readonly Dictionary<string, Color> AgentColors = new(StringComparer.OrdinalIgnoreCase)
        {
            ["red"] = Color.Parse("#D75F5F"),     // 167
            ["orange"] = Color.Parse("#D7875F"),  // 173
            ["yellow"] = Color.Parse("#D7AF5F"),  // 179
            ["green"] = Color.Parse("#00AF5F"),   // 35  — confirmed
            ["teal"] = Color.Parse("#00AFAF"),    // 37  — confirmed (auto-assigned)
            ["cyan"] = Color.Parse("#00AFAF"),    // 37
            ["blue"] = Color.Parse("#5F87D7"),    // 68
            ["purple"] = Color.Parse("#875FD7"),  // 98
            ["violet"] = Color.Parse("#875FD7"),  // 98
            ["magenta"] = Color.Parse("#D787AF"), // 175 — confirmed (auto-assigned)
            ["pink"] = Color.Parse("#D787AF"),    // 175
            ["gray"] = Color.Parse("#808080"),    // 244
            ["grey"] = Color.Parse("#808080"),    // 244
            ["white"] = Color.Parse("#FFFFFF")
        };

        // What an orb looks like with no /color set: the original faint hairline
        // and near-white letter. PlainLink is the same idea for the team arrow,
        // but brighter — the hairline works because it sits on the orb's own
        // fill, and an arrow has nothing behind it but the desktop.
        private static readonly Color PlainStroke = Color.Parse("#22FFFFFF");
        private static readonly Color PlainGlyph = Color.Parse("#DDFFFFFF");
        private static readonly Color PlainLink = Color.Parse("#FFCCCCCC");

        public string SessionId { get; }

        private string _lastState = "";
        private string _lastColor = "";
        private Color? _accentColor;
        private string _lastGlyphName = "";
        private string? _lastTipTitle;
        private string? _lastTipPath;

        // Colour for the team arrow leaving this orb, when it has one. Follows
        // /color so several members pointing at one lead stay apart; sessions
        // without a colour share the neutral. See TeamLinks.
        public Color LinkColor { get; private set; } = PlainLink;

        // Seeded from the settings-backed colour at field-init time, the same way
        // SessionManager seeds OrbsVisible from ClaudeBuddySettings.ShowOrbs.
        private readonly SolidColorBrush _orbBrush = new(OrbColors.Idle);

        // The two halves of this orb's identity, for the chat panel's header.
        // A local session has no portrait and no emoji to draw there, and these
        // are what it has instead — read from the orb rather than re-derived, so
        // the header cannot disagree with the thing that was clicked.
        public string GlyphText => Glyph.Text ?? "";
        public Color OrbColor => _orbBrush.Color;

        // This session's own colour — /color for a Claude Code session, the
        // derived one for a gateway agent — or null where it has none. Distinct
        // from OrbColor, which is the *state* and changes as the session works.
        public Color? AccentColor => _accentColor;

        private readonly RadialGradientBrush _glowBrush = new()
        {
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientStops = GlowStops(OrbColors.Idle)
        };

        private readonly ColorTransition _colorTransition;
        private readonly ScaleTransform _orbScale = new();

        // The heart badge's own beat, separate from the orb's breath so the two
        // can run at different rates without fighting over one transform.
        private readonly ScaleTransform _heartScale = new();

        // The halo's own transform, used by nothing but Acknowledge below. Its own
        // rather than shared with _orbScale because the two say different things:
        // one is the session's breath, the other is this app answering a click.
        private readonly ScaleTransform _glowScale = new();

        // Flat red rather than a fourth entry in OrbColors: this isn't a
        // session state Claude Code reports, it's purely local UI feedback
        // for "the mic is listening", so it has no reason to be user
        // configurable the way idle/generating/waiting are.
        private static readonly Color RecordingColor = Color.Parse("#D93B3B");

        private bool _recording;
        private VoiceRecorder? _recorder;
        private DispatcherTimer? _recordingCap;

        // Created lazily on first hover (see EnsureFlyoutShown), not here —
        // most orbs are never hovered in a given run, and none of them should
        // pay for a second window until one actually is.
        private OrbFlyout? _flyout;

        // Test-only window into the lazily-created flyout: a test that drives
        // EnsureFlyoutShown directly (see that method's own comment) has no other
        // way to reach the window it just built to click one of its own buttons.
        internal OrbFlyout? Flyout => _flyout;

        // Bridges hover between two separate OS windows (the orb and its
        // flyout): a bare PointerExited on either one would hide the flyout
        // the instant the cursor crosses from one window into the other,
        // before it ever reaches the second window. Scheduling the hide and
        // cancelling it if either window reports the pointer back within the
        // grace period turns that into a single smooth handoff instead.
        private DispatcherTimer? _hideFlyoutTimer;

        // True while this orb's chat panel is open. The arc and the panel want
        // the same piece of screen — ArcRadius is 56, directly below the orb —
        // and the arc's mic and speak duplicate what the panel already offers,
        // so one hides for the other rather than both being there.
        private bool _chatOpen;

        // The flyout used to open the instant the pointer touched an orb,
        // which made orbs hostile to each other: the arc one orb throws out
        // covers its neighbours, so a cursor travelling toward a second orb
        // summoned a menu that then sat in the way of the click it was on its
        // way to make. Requiring the pointer to *rest* on the orb separates
        // "I want this orb's menu" from "I am passing over this orb", and
        // costs a deliberate hover nothing it would notice.
        private DispatcherTimer? _showFlyoutTimer;
        private static readonly TimeSpan FlyoutHoverDelay = TimeSpan.FromMilliseconds(450);

        public OrbWindow(string sessionId)
        {
            SessionId = sessionId;
            InitializeComponent();

            _colorTransition = new ColorTransition
            {
                Property = SolidColorBrush.ColorProperty,
                Duration = TimeSpan.FromMilliseconds(300),
                Easing = new QuadraticEaseOut()
            };
            _orbBrush.Transitions = new Transitions { _colorTransition };

            Orb.Fill = _orbBrush;

            Glow.Fill = _glowBrush;
            Orb.RenderTransform = _orbScale;

            // Centred, so the acknowledgment halo expands evenly out of the orb
            // rather than growing towards one corner.
            Glow.RenderTransform = _glowScale;
            Glow.RenderTransformOrigin = RelativePoint.Center;

            // Centred, so the beat grows the heart in place rather than pushing
            // it towards the orb's rim.
            HeartGlyph.RenderTransform = _heartScale;
            HeartGlyph.RenderTransformOrigin = RelativePoint.Center;

            Root.PointerEntered += (_, _) =>
            {
                CancelFlyoutHide();
                ScheduleFlyoutShow();
            };
            Root.PointerExited += (_, _) =>
            {
                CancelFlyoutShow();
                ScheduleFlyoutHide();
            };

            ConfigureThoughtBubblePlacement(Root);

            // Unlike WPF, Loaded fires *after* the first UpdateFrom here, so
            // honor any state that already arrived instead of stomping it.
            Loaded += (_, _) =>
            {
                ApplyState(string.IsNullOrEmpty(_lastState) ? "idle" : _lastState);

                // ...and then take the breath straight back off a parked orb.
                //
                // Every arm of ApplyState calls StartPulse, unconditionally, and
                // it has no idea about presence — so an orb whose *first* status
                // was already parked was dimmed by ApplyPresence and then set
                // breathing again by this handler a moment later. That is the
                // shape the whole ticket started from ("fifteen breathing orbs on
                // a machine with nothing running on it"), surviving in the one
                // window where the order is guaranteed to produce it: the scan
                // creates the orb, calls UpdateFrom, and shows it, so a session
                // that is parked when its orb first appears took this path every
                // single time.
                //
                // Re-asserted here rather than by teaching ApplyState about
                // presence, and the same way StopRecording already hands the orb
                // back: ApplyState answers "what is this session doing", which is
                // a different question from "is anyone on the other end of it",
                // and the two channels are kept separate everywhere else in this
                // file.
                if (_presence != OrbPresence.Present) ApplyPresence(_presence, force: true);
            };

            Opened += (_, _) =>
            {
                this.ShowOnAllSpaces();

                // Otherwise the first click on an orb is spent activating the
                // app and never reaches it — see AcceptFirstClick.
                this.AcceptFirstClick();
            };

            // A closed orb must leave the shared ticker or it keeps being ticked.
            Closed += (_, _) =>
            {
                Pulsing.Remove(this);
                StopAvatarAnimation();
                ChatPanel.HideFor(SessionId);
            };

            // A session going away mid-dictation (the window closing) must
            // not leave a capture thread or a native mic handle running.
            Closed += (_, _) => CancelRecording();

            // Same reason for the other half of the voice feature: speech
            // outlives the window that started it unless it's cancelled.
            Closed += (_, _) => TextToSpeech.Cancel();

            // The flyout is a second, independent top-level window — it
            // outlives this one unless told otherwise. Stopping the hide
            // timer first, not just closing the flyout, matters because a
            // tick already queued on the dispatcher would otherwise run
            // after this and touch a window that no longer exists.
            Closed += (_, _) =>
            {
                _hideFlyoutTimer?.Stop();
                _flyout?.Close();
            };
        }

        public void UpdateFrom(SessionStatus status)
        {
            _lastStatus = status;

            var folder = string.IsNullOrEmpty(status.Cwd)
                ? ""
                : System.IO.Path.GetFileName(status.Cwd.TrimEnd('\\', '/'));

            // The chat's own name is the better label — it says what the
            // session is *doing*, and two sessions in one repo no longer look
            // identical. Falls back to the folder until Claude Code names it.
            var label = string.IsNullOrEmpty(status.Title) ? folder : status.Title;

            // An agent's own name beats all of it. Every member of a team
            // inherits the team session's title, so a team of four drew the
            // same letter four times and said nothing about which agent was
            // which — while the terminal had been calling them MenuUX,
            // Narrative and HitReactSpec the whole time. The title still gets
            // said, in the tooltip, because "which team" is worth knowing too.
            var name = string.IsNullOrEmpty(status.Agent) ? label : status.Agent;

            var described = string.IsNullOrEmpty(status.Agent) || string.IsNullOrEmpty(label)
                ? name
                : $"{status.Agent} · {label}";

            // The presence word goes in the tooltip as well as on the badge,
            // because a mark says *that* there is something and only a word says
            // what. "needs input" is the daemon's own phrase for it, so the
            // tooltip and `claude agents` agree rather than inventing a synonym.
            var presenceWord = PresenceMarkFor(status.Presence)?.Label;

            var tipTitle = string.IsNullOrEmpty(described) ? SessionId : described;
            if (presenceWord is not null)
            {
                tipTitle = tipTitle + " · " + presenceWord;
            }

            var tipPath = string.IsNullOrEmpty(status.Cwd) ? null : status.Cwd;

            // UpdateFrom runs on every session poll, but SetTip always builds
            // a fresh Border. Doing that while the pointer is resting on the
            // orb and the tooltip is already open made Avalonia close and
            // reopen the popup on every poll tick — a flicker, not a redraw,
            // for as long as the mouse stayed still. Content is small and
            // cheap to compare, so skip the rebuild when nothing changed.
            if (tipTitle != _lastTipTitle || tipPath != _lastTipPath)
            {
                ToolTip.SetTip(Root, ThoughtBubble(tipTitle, tipPath));
                _lastTipTitle = tipTitle;
                _lastTipPath = tipPath;
            }

            _lastGlyphName = name;
            ApplyAvatar(status);
            if (!_hasAvatar) Glyph.Text = _agentEmoji ?? GlyphFor(name);

            // An open chat panel wears this orb's letters and colours. It used
            // to copy them once, when it opened, so a title that arrived after
            // the panel did — or a /rename, or a /color — left the header
            // showing the old ones. Cheap to call: the panel ignores it unless
            // it is showing this orb, and does nothing unless something moved.
            ChatPanel.RefreshIdentityFor(this);
            ApplyAccent(status.Color);
            // Rooms wear the badge too, now that their glyph is the channel's
            // initials rather than a hash. It was suppressed when the two would
            // have said the same thing; with the glyph saying *which* channel,
            // the badge saying *that* it is one is the other half rather than a
            // repeat — and without it a room orb is indistinguishable from an
            // ordinary one.
            ApplyKind(status.Kind);
            ApplyCli(status.Source);
            ApplyHeartbeat(status.Heartbeat);

            // A room orb is named for its channel, like every other orb is named
            // for its session: "#arch" draws "Ar". The hash it used to draw
            // instead said only "this is a channel", which is true of the badge
            // on every member orb too and so distinguished one room from
            // another not at all.
            // ...unless it has a picture, which it does as soon as anyone in
            // the channel has one: the composite is the better answer to both
            // "which channel" and "who is in it", and letters drawn over it
            // would be unreadable anyway.
            if (status.IsRoom && !_hasAvatar)
            {
                Glyph.IsVisible = true;
            }
            SetTeamRole(!string.IsNullOrEmpty(status.Lead));

            SessionInfoItem.Header = string.IsNullOrEmpty(described) ? SessionId : described;
            SessionPathItem.Header = status.Cwd;
            SessionPathItem.IsVisible = !string.IsNullOrEmpty(status.Title)
                                        && !string.IsNullOrEmpty(status.Cwd);

            // Which of the two lifecycle actions this session can be offered.
            // Asked of SessionPresence rather than decided here, so the menu and
            // the manager's own guards cannot disagree about what is possible —
            // an item that is offered and then silently does nothing is worse
            // than one that is not there.
            AgentsViewItem.IsVisible = ClickRouting.OffersTheAgentsView(status);
            DismissItem.IsVisible = SessionPresence.CanDismiss(status);
            EndSessionItem.IsVisible = SessionPresence.CanEndSession(status);

            if (status.State != _lastState)
            {
                _lastState = status.State;
                if (IsLoaded && !_recording)
                {
                    ApplyState(status.State);
                }
                // else if !IsLoaded: Loaded handler applies _lastState once the
                // window is up. Else (_recording): the mic's red pulse owns
                // the orb's colour/motion right now — StopRecording restores
                // whatever _lastState ends up being once dictation finishes,
                // so a state change mid-recording isn't lost, just deferred.
            }

            // Last, and after the state block above deliberately: parking is
            // about stillness, and the state block is the thing that starts
            // motion. Applied in the other order, an orb that parked and
            // changed state in one update would be left breathing.
            ApplyPresence(status.Presence);
        }

        // --- presence ---------------------------------------------------------
        // A third axis, beside identity and state. The orb's colour says which
        // session this is, its fill says what that session is doing, and its
        // opacity and stillness say whether anything is on the other end of it
        // at all — a background job parked between turns, or a team member whose
        // lead has gone. See SessionPresence for what counts as either.
        //
        // Dimmed and kept, rather than hidden. A parked job is real, resumable
        // and worth clicking, and the user's own "Keep orbs for" setting is what
        // decides how long a quiet session stays on screen; hiding one would
        // trade "why are fifteen orbs breathing" for "where did my job go",
        // which is the worse of the two questions because nothing on screen
        // would answer it.

        // Dim enough to read as background at a glance across a screen, light
        // enough that the letter and the ring are still legible — the orb has to
        // stay identifiable, because the whole point is that the user can find
        // the parked job again.
        private const double ParkedOpacity = 0.45;

        private OrbPresence _presence = OrbPresence.Present;

        // The two things a dimmed orb can be saying, and nothing for the two that
        // have nothing to add.
        //
        // "?" for a job the daemon is holding for you: several of the ones this
        // was written for are literally questions waiting on an answer, and a
        // question mark is the one glyph nobody has to be taught. "✓" for a job
        // that is over — the opposite instruction, and worth being unmistakable
        // from across a screen, because the difference between "this wants you"
        // and "this wants nothing ever again" is the whole of why they are two
        // states rather than one dim one.
        //
        // Neither is a *kind*: the gear says what the session is and does not
        // change while it runs. These are presence, they change under it, and
        // they live in their own corner for exactly that reason.
        private static (string Glyph, string Label)? PresenceMarkFor(OrbPresence presence) =>
            presence switch
            {
                OrbPresence.NeedsInput => ("?", "needs input"),
                OrbPresence.Finished => ("\u2713", "finished"),
                _ => null
            };

        // What the chat panel and the tooltip say about presence, or null when
        // there is nothing to say. Read the same way KindLabel is, so the two
        // cannot disagree about one orb.
        public string? PresenceLabel => PresenceMarkFor(_lastStatus?.Presence ?? OrbPresence.Present)?.Label;

        // internal: driven directly by the UI suite, the same trade ApplyState
        // documents — and worth asserting on its own, because the arms are not
        // symmetrical.
        //
        // Deliberately not folded into ApplyState. That method is gated on the
        // *state* having changed, and presence does not change it: a parked job's
        // state is a truthful "idle" both before and after, so the gate would
        // never fire and nothing would dim. Presence needs its own gate for
        // exactly that reason.
        //
        // force is for a re-assertion the presence itself did not ask for — see
        // StopRecording, which hands the orb's motion back after dictation and
        // would otherwise leave a dimmed orb breathing. Same shape as
        // ApplyAccent's force above and there for the same kind of reason.
        internal void ApplyPresence(OrbPresence presence, bool force = false)
        {
            if (!force && _presence == presence) return;
            _presence = presence;

            var mark = PresenceMarkFor(presence);
            if (mark is null)
            {
                PresenceBadge.IsVisible = false;
            }
            else
            {
                PresenceGlyph.Text = mark.Value.Glyph;
                PresenceBadge.IsVisible = true;
            }

            if (presence != OrbPresence.Present)
            {
                Root.Opacity = ParkedOpacity;

                // Off the shared roster as well as stopped. StopPulse alone
                // leaves an orb on it when a heart is beating, which no local
                // session has — but "no local orb has a heartbeat badge" is a
                // fact about another feature, and a dimmed orb that kept being
                // ticked would breathe again the moment that changed.
                StopPulse();
                Pulsing.Remove(this);
                return;
            }

            Root.Opacity = 1.0;

            // Back to whatever it was doing. Guarded exactly as UpdateFrom's
            // state block is, and for the same two reasons: before Loaded there
            // is no point (the Loaded handler applies _lastState anyway), and
            // during dictation the mic owns the orb's colour and motion —
            // StopRecording is what restores it, and this must not race that.
            if (IsLoaded && !_recording)
            {
                ApplyState(string.IsNullOrEmpty(_lastState) ? "idle" : _lastState);
            }
        }

        // How this orb is currently drawn. Read by the UI suite, and by nothing in
        // the app — the answer lives in the status. There used to be an IsParked
        // bool beside this; it went with the bool it was derived from, since
        // "dimmed" is now three different statements and a test that only asked
        // whether an orb was dim could not tell which one it was making.
        internal OrbPresence Presence => _presence;

        // /color identifies *which* session; the fill keeps saying what it's
        // doing. An unknown or missing color name leaves the orb looking the
        // way it always has, so a future addition to Claude Code's palette
        // degrades quietly instead of throwing.
        //
        // A "#RRGGBB" is accepted as well as a name, which is how a gateway
        // agent gets an accent: it has no /color to give, so one is derived
        // from its id (see AgentPalette). Taking it through the same field
        // rather than adding a second one means the ring, the glyph and the
        // team arrow all pick it up with no further wiring.
        private void ApplyAccent(string colorName, bool force = false)
        {
            // force is for a redraw the colour itself did not ask for — a
            // picture arriving or going changes how the ring is drawn while
            // leaving the colour alone, and the early return would swallow it.
            if (!force && colorName == _lastColor) return;
            _lastColor = colorName;

            Color accent = default;
            var known = !string.IsNullOrEmpty(colorName)
                        && (AgentColors.TryGetValue(colorName, out accent)
                            || (colorName[0] == '#' && Color.TryParse(colorName, out accent)));

            _accentColor = known ? accent : null;

            // The ring says *who*, including over a picture.
            //
            // It used to carry the state there instead, on the reasoning that a
            // picture takes the fill and leaves the ring as the only solid
            // colour. That was wrong in practice for a reason the reasoning
            // couldn't see: the idle colour is a user setting, and set near
            // black — which most installs are, since idle is meant to be quiet —
            // the "state ring" is a **black band** around the picture for the
            // 95% of the time an agent is idle. It reads as a rendering fault,
            // not as a status.
            //
            // Nothing is lost by giving it up. State on these orbs is carried by
            // the glow, which appears only for the states worth noticing
            // (GlowsFor) and pulses while they last — so "working" still
            // announces itself, and "idle" correctly says nothing at all.
            Orb.Stroke = new SolidColorBrush(known ? accent : _hasAvatar ? _orbBrush.Color : PlainStroke);

            // Thicker over a picture: it is a ring around a photograph rather
            // than an outline on a flat circle, and at 2px it reads as an edge.
            Orb.StrokeThickness = _hasAvatar ? 3 : known ? 2 : 1;

            Glyph.Foreground = new SolidColorBrush(known ? accent : PlainGlyph);
            LinkColor = known ? accent : PlainLink;

            if (Glow.IsVisible)
                _glowBrush.GradientStops = GlowStops(_accentColor ?? _orbBrush.Color);
        }

        // Re-runs ApplyAccent when something other than the colour has changed —
        // a picture arriving or going, which changes how thick the ring is and
        // what it falls back to. ApplyAccent returns early when the colour is
        // the same, and here it is: where it is *drawn* is what moved.
        private void RefreshAccent() => ApplyAccent(_lastColor, force: true);

        // Same 22 DIP as the CLI mark. The kind/heart/presence discs used to
        // be 16 against a 22px lobster, which is the size the user pointed at
        // and said the right-hand icons looked tiny.
        private const double BadgeSize = 22;

        private const double BadgeGlyphSize = 13;

        // What the CLI mark shows, so a test can assert on the disc a person
        // would have seen without reading a brush back off the control.
        internal string? CliMarkName { get; private set; }

        internal string? CliMarkFill { get; private set; }

        internal bool CliMarkVisible => CliBadge.IsVisible;

        // Reference identity of the tooltip's current content, so a test can
        // assert the flicker fix without a real popup: UpdateFrom must reuse
        // this instance across polls whose title/path didn't change, and
        // swap it for a new one when they did.
        internal Control? CurrentThoughtBubble => ToolTip.GetTip(Root) as Control;

        private void ApplyCli(SessionSource source)
        {
            var mark = CliMark.For(source);
            if (mark is null)
            {
                CliBadge.IsVisible = false;
                CliMarkName = null;
                CliMarkFill = null;
                return;
            }

            CliBadge.Background = new SolidColorBrush(Color.Parse(mark.Value.FillHex));
            CliGlyph.Data = StreamGeometry.Parse(mark.Value.GlyphPath);
            CliBadge.IsVisible = true;
            CliMarkName = mark.Value.Name;
            CliMarkFill = mark.Value.FillHex;
        }

        // A scheduled job, a private message, or a room with other people in
        // it. Nothing at all for a local session or for an agent's own main
        // session: every agent has a main, so badging it would put a mark on
        // almost every orb and distinguish nothing.
        //
        // @ and # are the symbols the surfaces themselves use for these two
        // things, so they need no learning. The clock is the odd one out and
        // has to be: a cron session is the one kind with nobody on the other
        // end, which is the distinction most worth seeing from across a screen.
        private static (string Glyph, string Label)? BadgeFor(SessionKind kind) => kind switch
        {
            SessionKind.Cron => ("\u23F1", "cron"),
            SessionKind.Direct => ("@", "direct message"),
            SessionKind.Channel => ("#", "channel"),

            // Two arrows going opposite ways: the same "this is somewhere else,
            // and it answers" that every sync icon means, which is as close to
            // learned-without-explaining as this one gets. The label says the
            // machine part, since the glyph can't.
            SessionKind.Remote => ("\u21C4", "another machine"),

            // A gear, for a job the daemon runs with nobody on the other end.
            // Machinery is the idea, which is as close as this gets to
            // learned-without-explaining, and it is the one badge that says
            // something about *this* machine rather than somewhere else.
            //
            // Worn by a working background job as well as a parked one. The
            // badge channel says what a session is, which does not change while
            // it runs; whether anything is happening in it rides the orb's
            // opacity — see ApplyPresence. Badging only the parked ones would
            // smuggle a state into the kind channel, and then two orbs of the
            // same kind would carry different marks depending on what they were
            // doing, which is what every other badge here avoids.
            SessionKind.Background => ("\u2699", "background job"),
            _ => null
        };

        // What the chat panel puts in its header. Null where there is no badge,
        // so the panel shows nothing rather than the word "unknown".
        public string? KindLabel => BadgeFor(_lastStatus?.Kind ?? SessionKind.Unknown)?.Label;

        public string? KindGlyphText => BadgeFor(_lastStatus?.Kind ?? SessionKind.Unknown)?.Glyph;

        private void ApplyKind(SessionKind kind)
        {
            var badge = BadgeFor(kind);

            if (badge is null)
            {
                KindBadge.IsVisible = false;
                return;
            }

            KindGlyph.Text = badge.Value.Glyph;
            KindBadge.IsVisible = true;
        }

        // Whether the chat panel should say this conversation is heartbeat-driven.
        // Read the same way KindLabel is, so the panel asks the orb rather than
        // re-deriving it from a status it does not hold.
        public bool IsHeartbeat => _lastStatus?.Heartbeat ?? false;

        private void ApplyHeartbeat(bool heartbeat)
        {
            if (HeartBadge.IsVisible == heartbeat) return;

            HeartBadge.IsVisible = heartbeat;

            if (heartbeat)
            {
                _heartStartedAt = Environment.TickCount64;

                // Joins the pulse ticker on its own account. Every branch of
                // ApplyState currently ends in a StartPulse, so an orb is
                // normally on the roster already and this is redundant — but the
                // heart's motion is the whole signal, and having it depend on
                // that staying true of a switch statement elsewhere is the kind
                // of coupling that breaks quietly.
                if (!Pulsing.Contains(this)) Pulsing.Add(this);
                EnsureTicker();
            }
            else
            {
                _heartScale.ScaleX = _heartScale.ScaleY = 1.0;
                HeartGlyph.Opacity = 1.0;
            }
        }

        // --- agent teams ------------------------------------------------------
        // A team member is drawn smaller than the session that leads it, so a
        // team reads as one lead with its agents rather than as several equal
        // sessions that happen to be next to each other. Deliberately only the
        // *drawing* shrinks: the window stays 56x56, so the stack spacing, the
        // drag target, and every remembered position keep working unchanged,
        // and a member that later loses its team grows back with no relayout.

        private const double MemberScale = 0.72;

        // Half the orb's drawn width, in DIPs — where TeamLinks stops the arrow
        // so it doesn't run under the orb.
        public double OrbRadius { get; private set; } = 18;

        private bool _isTeamMember;

        public void SetTeamRole(bool isTeamMember)
        {
            if (_isTeamMember == isTeamMember) return;
            _isTeamMember = isTeamMember;

            var scale = isTeamMember ? MemberScale : 1.0;

            Orb.Width = Orb.Height = 36 * scale;
            Glow.Width = Glow.Height = 56 * scale;

            // Kept on the orb's edge rather than in the window's corner. The
            // orb is a circle of radius 18*scale centred at (28,28), so its
            // lower-right edge is at 28 + 18*scale*sin45. Solving for the
            // margin that puts the badge's centre there is what keeps it
            // touching the rim at both sizes instead of drifting off a team
            // member's smaller circle.
            KindBadge.Width = KindBadge.Height = BadgeSize * scale;
            KindBadge.CornerRadius = new CornerRadius(BadgeSize * scale / 2);
            KindGlyph.FontSize = BadgeGlyphSize * scale;

            var inset = 28 - (18 * scale * 0.7071) - (BadgeSize * scale / 2);
            KindBadge.Margin = new Thickness(0, 0, Math.Max(0, inset), Math.Max(0, inset));

            // The same sum mirrored into the opposite corner: the heart rides the
            // orb's upper-right rim, so it has to move with the circle for the
            // same reason the kind badge does.
            HeartBadge.Width = HeartBadge.Height = BadgeSize * scale;
            HeartBadge.CornerRadius = new CornerRadius(BadgeSize * scale / 2);
            HeartGlyph.FontSize = BadgeGlyphSize * scale;
            HeartBadge.Margin = new Thickness(0, Math.Max(0, inset), Math.Max(0, inset), 0);

            // And mirrored once more into the corner this one lives in. Same sum
            // because it is the same circle: a team member's orb is smaller, and
            // a mark left at the full-size margin would float off its rim.
            PresenceBadge.Width = PresenceBadge.Height = BadgeSize * scale;
            PresenceBadge.CornerRadius = new CornerRadius(BadgeSize * scale / 2);
            PresenceGlyph.FontSize = BadgeGlyphSize * scale;
            PresenceBadge.Margin = new Thickness(Math.Max(0, inset), Math.Max(0, inset), 0, 0);

            CliBadge.Width = CliBadge.Height = CliMark.Size * scale;
            CliBadge.CornerRadius = new CornerRadius(CliMark.Size * scale / 2);
            CliGlyph.Width = CliGlyph.Height = CliMark.GlyphSize * scale;
            CliBadge.Margin = new Thickness(Math.Max(0, inset), 0, 0, Math.Max(0, inset));

            Glyph.FontSize = BaseGlyphFontSize * scale;
            OrbRadius = 18 * scale;
        }

        // Smaller with two letters than with one, so the wider glyph still
        // fits inside the same 36px circle rather than crowding its edge.
        private static double BaseGlyphFontSize => ClaudeBuddySettings.TwoLetterGlyphs ? 12.0 : 16.0;

        // Settings' "Two-letter initials" toggle changes how every already-
        // open orb's glyph reads without waiting for that session's next
        // hook update — see SessionManager.ReapplyGlyphs, which calls this
        // on each one. Re-derives from _lastGlyphName rather than the full
        // SessionStatus: nothing else about the orb needs to change, just
        // the text and the font size sitting under it.
        public void ReapplyGlyph()
        {
            Glyph.Text = GlyphFor(_lastGlyphName);
            Glyph.FontSize = BaseGlyphFontSize * (_isTeamMember ? MemberScale : 1.0);
            ChatPanel.RefreshIdentityFor(this);
        }

        // An agent's own picture, drawn as the orb itself.
        //
        // This is the one place the app's usual rule bends: normally the fill is
        // the state and the letter is which session. A face says which session
        // far better than a letter can, so the state moves outward to the ring —
        // which still carries the colour, and the pulse and halo were always
        // doing most of that work anyway. Sessions with no picture keep the
        // ordinary filled orb and get their agent's emoji instead of a letter,
        // which is why both paths stay.
        private bool _hasAvatar;
        private string? _agentEmoji;
        private ImageBrush? _avatarBrush;

        // The state ring on an avatar orb. One brush with its own transition,
        // rather than a fresh SolidColorBrush per state change: the fill it
        // replaced faded over 300ms and a ring that snaps instead reads as a
        // different, cruder thing. Also stops allocating a brush every time a
        // session changes state, which for a busy gateway is often.
        private SolidColorBrush? _ringBrush;
        private OpenClawAvatars.Avatar? _avatar;
        private int _avatarFrame;
        private DispatcherTimer? _avatarTimer;

        private void ApplyAvatar(SessionStatus status)
        {
            if (status.Source != SessionSource.OpenClaw)
            {
                ClearAvatar();
                return;
            }

            var identity = OpenClawSessions.IdentityForSession(SessionId);
            _agentEmoji = identity?.Emoji;

            // Asked of OpenClawSessions rather than assembled here, because a
            // room's picture is not an agent's picture — it is a composite of
            // everyone in the channel, and that is a question about who is in
            // the room, which this window has no business knowing. The chat
            // panel's header asks the same function, so the two cannot end up
            // wearing different faces for the same session.
            var avatar = OpenClawSessions.AvatarForSession(SessionId);
            if (avatar is null)
            {
                ClearAvatar();
                return;
            }

            if (ReferenceEquals(avatar, _avatar)) return;

            _avatar = avatar;
            _avatarFrame = 0;
            _hasAvatar = true;

            Glyph.IsVisible = false;

            _avatarBrush ??= new ImageBrush { Stretch = Stretch.UniformToFill };
            _avatarBrush.Source = avatar.Frames[0];
            Orb.Fill = _avatarBrush;

            _ringBrush ??= new SolidColorBrush(_orbBrush.Color)
            {
                Transitions = new Transitions
                {
                    new ColorTransition
                    {
                        Property = SolidColorBrush.ColorProperty,
                        Duration = TimeSpan.FromMilliseconds(300),
                        Easing = new QuadraticEaseOut()
                    }
                }
            };

            _ringBrush.Color = _orbBrush.Color;

            // The picture lands long after the accent did, and it is what
            // decides how the ring is drawn — so the accent is applied again
            // rather than assumed to have got there first. An agent with no
            // colour at all falls back to the state ring inside ApplyAccent,
            // which is what _ringBrush is still here for.
            Orb.Stroke = _ringBrush;
            RefreshAccent();

            StartAvatarAnimation();
        }

        private void ClearAvatar()
        {
            if (!_hasAvatar)
            {
                Glyph.IsVisible = true;
                return;
            }

            _hasAvatar = false;
            _avatar = null;
            StopAvatarAnimation();

            Orb.Fill = _orbBrush;
            Orb.Stroke = new SolidColorBrush(Color.Parse("#22FFFFFF"));
            Orb.StrokeThickness = 1;
            Glyph.IsVisible = true;

            // Thinner ring, and the accent back on a flat circle.
            RefreshAccent();
        }

        // Its own timer rather than the shared pulse ticker: frame delays are
        // whatever each GIF's author chose, and are neither 60fps nor the same
        // between two agents.
        private void StartAvatarAnimation()
        {
            StopAvatarAnimation();

            if (_avatar is null || !_avatar.IsAnimated) return;

            _avatarTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(_avatar.DelaysMs[0])
            };

            _avatarTimer.Tick += (_, _) =>
            {
                if (_avatar is null || _avatarBrush is null) return;

                _avatarFrame = (_avatarFrame + 1) % _avatar.Frames.Count;
                _avatarBrush.Source = _avatar.Frames[_avatarFrame];
                _avatarTimer!.Interval = TimeSpan.FromMilliseconds(_avatar.DelaysMs[_avatarFrame]);
            };

            _avatarTimer.Start();
        }

        private void StopAvatarAnimation()
        {
            _avatarTimer?.Stop();
            _avatarTimer = null;
        }

        // The letters themselves live in OrbGlyph, which is pure and tested;
        // this only supplies the one thing that is not — the user's setting.
        private static string GlyphFor(string label) =>
            OrbGlyph.For(label, ClaudeBuddySettings.TwoLetterGlyphs);

        // The colour comes from OrbColors so this switch is about *motion* only —
        // one state-to-colour mapping in the app, not two that can drift apart.
        // internal: the state-to-motion mapping is a rule worth asserting, and
        // UpdateFrom deliberately does not apply it directly — it stores the
        // state and lets Loaded/Opened apply it, because Avalonia fires Loaded
        // *after* the first UpdateFrom. A window that is never shown therefore
        // never applies anything, so a test drives this instead of the lifecycle.
        internal void ApplyState(string state)
        {
            var color = OrbColors.For(state);

            switch (state)
            {
                case "waiting":
                    AnimateColor(color, TimeSpan.FromMilliseconds(300), state);
                    StartPulse(1.22, TimeSpan.FromMilliseconds(500), new QuadraticEaseOut());
                    break;
                case "generating":
                    AnimateColor(color, TimeSpan.FromMilliseconds(300), state);
                    StartPulse(1.14, TimeSpan.FromMilliseconds(900), new SineEaseInOut());
                    break;
                default:
                    StopPulse();
                    AnimateColor(color, TimeSpan.FromMilliseconds(400), state);
                    StartPulse(1.06, TimeSpan.FromSeconds(2.2), new SineEaseInOut());
                    break;
            }
        }

        // The halo is a claim on your attention, so only the two states that
        // have something to say make it. Idle is what most orbs are in most of
        // the time, and glowing about it spends the screen's whole attention
        // budget on the one state that wants none of it — the slow breath is
        // enough to say the session is still there, and the fill and hairline
        // still say where it is.
        //
        // A custom idle colour makes the point sharply: a dark one (the default
        // is already nearly black) renders as a smudge that darkens whatever
        // sits under it rather than as light.
        //
        // Asked in one place, from the state alone, because it's read both by
        // ApplyState and by ReapplyStateColors and the two must not drift —
        // the same reason the colours themselves live in OrbColors.
        private static bool GlowsFor(string state) => state is "waiting" or "generating";

        // Changing a colour in settings is not a state change, and UpdateFrom only
        // calls ApplyState when status.State actually differs — so without this an
        // orb would keep its old fill until its session next did something, which
        // for a quiet session is never.
        //
        // Two things it deliberately doesn't do. It doesn't re-run ApplyState:
        // StartPulse resets the breath's phase, so every orb on screen would jerk
        // in step with the pointer. And it barely fades — 60ms, not the 300-400ms
        // a real state change gets — because the picker raises its change event on
        // every pointer move, and a third of a second of easing leaves the orb
        // trailing the cursor, reading as lag rather than as a live preview. At
        // 60ms each frame lands most of the way there and the orb tracks the
        // spectrum. The glow already snaps, since GlowStops is assigned rather
        // than animated, so this also stops the two disagreeing mid-drag.
        private static readonly TimeSpan SettingsColorFade = TimeSpan.FromMilliseconds(60);

        public void ReapplyStateColors()
        {
            // Not up yet: the Loaded handler applies _lastState with the new
            // colours anyway, which also covers an orb created while orbs were
            // hidden.
            if (!IsLoaded) return;

            var state = string.IsNullOrEmpty(_lastState) ? "idle" : _lastState;
            AnimateColor(OrbColors.For(state), SettingsColorFade, state);
        }

        private void AnimateColor(Color to, TimeSpan duration, string state)
        {
            _colorTransition.Duration = duration;
            _orbBrush.Color = to;

            // Hidden rather than made transparent: an invisible ellipse isn't
            // rendered at all, and there's no point rebuilding four gradient
            // stops for something nobody can see.
            Glow.IsVisible = GlowsFor(state);
            if (Glow.IsVisible) _glowBrush.GradientStops = GlowStops(_accentColor ?? to);

            // With a picture in the fill, the ring is the only thing left
            // carrying the colour, so it has to follow the same changes — and
            // fade rather than snap, the way the fill did.
            if (_hasAvatar && _ringBrush is not null) _ringBrush.Color = to;
        }

        // Opaque at the centre, gone by the edge — the same falloff a blur gave,
        // without re-blurring 56x56 pixels sixty times a second.
        // The glow's gradient offsets are fractions of the *radius* (28px), and
        // the orb covers the inner 18px — so anything before offset 0.64 is hidden
        // behind the orb and contributes nothing. Hold the colour flat out to
        // there and fade over the visible ring, which is where the blur used to
        // put its bloom.
        private static GradientStops GlowStops(Color color) => new()
        {
            new GradientStop(Color.FromArgb(150, color.R, color.G, color.B), 0.0),
            new GradientStop(Color.FromArgb(150, color.R, color.G, color.B), 0.64),
            new GradientStop(Color.FromArgb(95, color.R, color.G, color.B), 0.82),
            new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1.0)
        };

        // Shared with the flyout's buttons, which are a different window but the
        // same app: a second tooltip look would be a second answer to a question
        // this already answered. The global ToolTip style is stripped to nothing
        // precisely so this control *is* the tooltip, which also means anything
        // handing ToolTip a bare string gets unstyled text floating on the
        // desktop — how the flyout's first set of tips shipped.
        // `compact` drops the tail and tightens the type: a caption for a
        // button, rather than a thought belonging to an orb.
        //
        // The tail is two dots *below* the bubble, so it reads as a thought
        // rising from whatever is underneath — right for an orb's own tooltip,
        // which sits above the orb and points back down at it. A button's label
        // sits below the button, where the same dots point at nothing and read
        // as a rendering fault. Sharing the palette and dropping the tail keeps
        // one look without claiming a button is thinking.
        // Both orb windows are tiny (56x56 here, 72x72 for an account orb) and
        // Topmost, so their thought bubble has to render outside the window's
        // own bounds — Avalonia backs that with a real, separate native popup
        // window rather than drawing inside Root. `PlacementMode.Top`'s own
        // math put that popup's bounds overlapping Root's: caught live off
        // the window server (CGWindowListCopyWindowInfo), a fresh popup
        // window id opening and closing every ~20-25ms for as long as the
        // hover lasted. The mechanism: the popup's bounds overlap Root's
        // pixel under the cursor, the OS hands "topmost under the cursor" to
        // the newly-opened popup, Avalonia reads that as the pointer leaving
        // Root and closes the tooltip, the OS hands the cursor back to Root,
        // Avalonia reopens it — a self-sustaining ~40Hz loop, which reads as
        // flicker rather than as discrete blinks. AccountOrbWindow never set
        // a Placement at all, which defaults to Pointer — the popup opens
        // wherever the cursor already is, i.e. inside its own anchor, the
        // same failure by a different route.
        //
        // Two earlier rounds (CB-104) each fixed a real bug in the tooltip's
        // *content* churning open/closed on every session poll while the
        // pointer sat still. Both fixes stay; this is a separate mechanism —
        // geometry, not content — so it needed a separate fix.
        //
        // PlacementMode.Custom sidesteps whatever Top's default math is
        // actually doing (undocumented, and this Avalonia version's source
        // wasn't available locally to read it): the callback below computes
        // the box itself, anchored to Root's own top edge and grown upward,
        // so it cannot land on top of Root regardless of platform quirks.
        // Call once, from the constructor — not every poll tick — since the
        // callback never changes and there is no reason to touch a popup's
        // placement while it may be open.
        internal static void ConfigureThoughtBubblePlacement(Control anchor)
        {
            ToolTip.SetPlacement(anchor, PlacementMode.Custom);
            ToolTip.SetCustomPopupPlacementCallback(anchor, PlaceThoughtBubbleAboveAnchor);
        }

        // Daylight above the anchor's own top edge, past whatever rounding
        // either side of the popup positioner does. Not a tuned magic
        // number: the overlap this replaces measured 78px deep into a 56px-
        // tall window, so a few px here is a floor against rounding, not an
        // offset chosen to clear one captured case.
        private const double ThoughtBubbleClearance = 6;

        private static void PlaceThoughtBubbleAboveAnchor(CustomPopupPlacement placement)
        {
            // Anchor = the target's own top-center point; Gravity = the
            // popup grows in the "up" direction from that point, so its
            // bottom-center lands on the anchor point before the offset
            // pushes it clear. Both fully determined by us, not by
            // PlacementMode.Top's own (evidently unreliable) math.
            placement.Anchor = PopupAnchor.Top;
            placement.Gravity = PopupGravity.Top;
            placement.Offset = new Point(0, -ThoughtBubbleClearance);

            // Never let a screen-edge constraint flip this to Bottom or
            // Center — flipping back onto the anchor is exactly the bug
            // being fixed here. An orb pinned at the very top of a display
            // draws its tooltip partly off-screen instead, which is a far
            // smaller problem than reopening the flicker loop.
            placement.ConstraintAdjustment = PopupPositionerConstraintAdjustment.SlideX;
        }

        internal static Control ThoughtBubble(string title, string? path, bool compact = false)
        {
            var bg = Color.Parse("#E6EAECF0");
            var fg = Color.Parse("#FF2A2A35");
            var font = new FontFamily(
                "SF Pro Rounded, .AppleSystemUIFontRounded, Segoe UI, sans-serif");

            var content = new StackPanel { Spacing = 2 };
            content.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = compact ? 11 : 12.5,
                FontFamily = font,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(fg),
                TextWrapping = TextWrapping.NoWrap,
                LineHeight = 17
            });

            if (path is not null)
            {
                content.Children.Add(new TextBlock
                {
                    Text = path,
                    FontSize = 11.5,
                    FontFamily = font,
                    Foreground = new SolidColorBrush(Color.FromArgb(180, fg.R, fg.G, fg.B)),
                    TextWrapping = TextWrapping.NoWrap,
                    LineHeight = 15
                });
            }

            var bubble = new Border
            {
                Background = new SolidColorBrush(bg),
                CornerRadius = new CornerRadius(compact ? 9 : 14),
                Padding = compact ? new Thickness(9, 5) : new Thickness(14, 9),
                BoxShadow = BoxShadows.Parse("0 2 8 0 #30000000"),
                Child = content
            };

            if (compact) return bubble;

            var canvas = new Canvas
            {
                Width = 16, Height = 16,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Margin = new Thickness(0, -2, 0, 0)
            };

            var dot1 = new Ellipse
            {
                Width = 8, Height = 8,
                Fill = new SolidColorBrush(Color.FromArgb(180, bg.R, bg.G, bg.B))
            };
            Canvas.SetLeft(dot1, 4);
            Canvas.SetTop(dot1, 0);

            var dot2 = new Ellipse
            {
                Width = 5, Height = 5,
                Fill = new SolidColorBrush(Color.FromArgb(120, bg.R, bg.G, bg.B))
            };
            Canvas.SetLeft(dot2, 6);
            Canvas.SetTop(dot2, 10);

            canvas.Children.Add(dot1);
            canvas.Children.Add(dot2);

            var stack = new StackPanel();
            stack.Children.Add(bubble);
            stack.Children.Add(canvas);
            return stack;
        }

        // One shared ticker drives every orb's pulse instead of an Avalonia
        // Animation per window. Avalonia animations run at the display's frame
        // rate, and each frame re-renders the whole (transparent, topmost) orb
        // window — measured at roughly 8% of a core per orb at 60Hz. The pulse is
        // a slow breath, so a much lower rate is indistinguishable and costs a
        // third as much. Hidden orbs are skipped entirely, which the old
        // animation never did: Hide() left it running.
        private const double PulseFps = 20;

        private static readonly List<OrbWindow> Pulsing = new();
        private static DispatcherTimer? _ticker;

        private double _pulseTo = 1.0;
        private double _pulsePeriodMs = 2200;
        private long _pulseStartedAt;

        // Whether the *breath* is running, as distinct from whether this orb is on
        // the shared roster. They used to be the same thing and are not any more:
        // the click acknowledgment below borrows the roster for a quarter of a
        // second on orbs that are deliberately held still, and a parked orb whose
        // scale started swelling because it had been added to the ticker would be
        // saying something about its state that is not true.
        private bool _breathing;

        // Its own clock rather than the orb's, because the orb's period changes
        // with the session's state — and a heart that sped up when the agent
        // started working would be saying something this badge does not know.
        private const double HeartPeriodMs = OpenClawHeartbeat.PeriodMs;
        private long _heartStartedAt;

        private void StartPulse(double to, TimeSpan duration, Easing easing)
        {
            // Duration is a half-cycle in the old alternating animation, so a full
            // breath is twice it. Easing is implied by the cosine below.
            _pulseTo = to;
            _pulsePeriodMs = duration.TotalMilliseconds * 2;
            _pulseStartedAt = Environment.TickCount64;
            _breathing = true;

            if (!Pulsing.Contains(this)) Pulsing.Add(this);
            EnsureTicker();
        }

        private static void EnsureTicker()
        {
            // Restart as well as create: the tick handler stops the timer once the
            // last orb stops pulsing, so a returning session has to be able to
            // wake it again.
            if (_ticker is not null)
            {
                if (!_ticker.IsEnabled) _ticker.Start();
                return;
            }

            _ticker = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1000.0 / PulseFps)
            };
            _ticker.Tick += (_, _) => TickAllPulses();
            _ticker.Start();
        }

        // internal: the shared ticker's Tick handler calls this on every
        // pulsing orb once a real frame elapses, which a headless test only
        // gets by pumping the dispatcher against wall time. Driving it directly
        // is the same trade ApplyState already makes above.
        internal void TickPulse()
        {
            // Nothing on screen, nothing to animate — the whole point of the
            // "Show orbs" toggle was to stop this work, and it never did.
            if (!IsVisible)
            {
                _orbScale.ScaleX = _orbScale.ScaleY = 1.0;
                return;
            }

            if (_ackStartedAt != 0) TickAcknowledgement();

            // Only when the breath is actually running. An orb that is on the
            // roster solely to finish an acknowledgment must not start swelling:
            // scale is the state channel, and a parked orb that breathed for a
            // quarter of a second would be claiming to have come back to life.
            if (_breathing)
            {
                var phase = (Environment.TickCount64 - _pulseStartedAt) % _pulsePeriodMs / _pulsePeriodMs;
                var eased = (1 - Math.Cos(phase * 2 * Math.PI)) / 2;   // 0 -> 1 -> 0, smooth at both ends
                var scale = 1.0 + (_pulseTo - 1.0) * eased;

                _orbScale.ScaleX = scale;
                _orbScale.ScaleY = scale;
            }

            if (HeartBadge.IsVisible) TickHeart();
        }

        // The heart badge's beat, on the same ticker as the breath above. Two
        // thumps close together and then a rest, rather than the orb's cosine:
        // the orb is already breathing, and a second smooth swell next to it
        // reads as one thing wobbling rather than as a pulse. Lub-dub is the
        // shape everyone recognises without being told, which is the only reason
        // it is worth the extra few lines.
        //
        // Opacity moves with the scale because 15px of heart cannot get much
        // bigger before it leaves its own badge — most of the visible motion is
        // the fade, and the scale is what stops the fade reading as a blink.
        internal void TickHeart()
        {
            var beat = OpenClawHeartbeat.Beat(
                (Environment.TickCount64 - _heartStartedAt) / HeartPeriodMs);

            _heartScale.ScaleX = _heartScale.ScaleY = 1.0 + (0.28 * beat);
            HeartGlyph.Opacity = 0.5 + (0.5 * beat);
        }

        private void StopPulse()
        {
            _breathing = false;

            // Stays on the roster while the heart is beating. Every caller
            // currently follows this immediately with a StartPulse, so leaving
            // would be momentary — but a momentary stop is exactly what a
            // heartbeat badge should not do, and the alternative is that this
            // stays correct only by luck of the call order.
            if (!HeartBadge.IsVisible) Pulsing.Remove(this);

            _orbScale.ScaleX = _orbScale.ScaleY = 1.0;
        }

        // --- "handled": the click that deliberately did nothing --------------

        // A single outward halo, once, when a click resolved without creating
        // anything.
        //
        // Round eight exists because round seven worked and nobody could tell.
        // The click on an orb whose session was already on screen correctly did
        // nothing at all — verified from outside the app: dispatch fired, the
        // pane scan matched, no window was created — and the user's verdict was
        // "still not working", because a silent success is indistinguishable from
        // the broken clicks the whole ticket is about. Invisible success is
        // failure, so the orb says it handled the gesture.
        //
        // Deliberately *not* the scale channel. Scale is the breath, which is
        // this app's word for what a session is doing, and a swell on click would
        // read as the session changing state — the one thing an acknowledgment
        // must not imply, since nothing about the session changed. The glow's
        // *size* is otherwise unused (its colour carries state, its scale carries
        // nothing), so a halo that expands and fades is a free channel and reads
        // as a ripple leaving the orb rather than as the orb doing something.
        //
        // On the shared ticker rather than an Avalonia animation or a timer of its
        // own, for the reason the breath is: one ticker for every orb, and this
        // borrows it for a quarter of a second. It is also the only reason
        // _breathing exists — a parked orb is deliberately off the roster and has
        // to be able to come back onto it for an acknowledgment without its held
        // stillness being mistaken for a state change.
        private const double AckMs = 280;
        private const double AckHaloTo = 1.45;

        private long _ackStartedAt;
        private bool _ackBorrowedTheRoster;

        // internal: reached in production from GoToSession's callback, off the
        // click path, and driven directly by the UI suite for the same reason
        // ApplyState and ApplyPresence are.
        internal void Acknowledge()
        {
            // The mic owns the orb's colour and motion while it is recording, and
            // StopRecording is what hands it back. Same guard as ApplyPresence's,
            // and for the same reason: an acknowledgment that fought that would
            // leave the orb mid-halo when dictation ended.
            if (_recording) return;

            _ackStartedAt = Environment.TickCount64;

            if (!Pulsing.Contains(this))
            {
                Pulsing.Add(this);
                _ackBorrowedTheRoster = true;
            }

            EnsureTicker();
        }

        // What GoToSession hands the focuser as its acknowledgment callback.
        //
        // A named method rather than a lambda at the call site, because the
        // marshalling is the whole content of it and a lambda body there is
        // reachable only by a real click resolving through TerminalFocuser — which
        // this suite must never do, so it would be an uncovered line that looks
        // like an oversight rather than the seam it is.
        //
        // Marshalled because Focus answers from a pool thread and this touches the
        // orb's own visual tree.
        internal void AcknowledgeFromAnyThread() => Dispatcher.UIThread.Post(Acknowledge);

        // internal for the same reason TickPulse is: a headless test cannot wait
        // for real frames, and driving the tick directly is how every other
        // animation in this file is asserted.
        internal void TickAcknowledgement()
        {
            var progress = (Environment.TickCount64 - _ackStartedAt) / AckMs;

            if (progress >= 1.0)
            {
                EndAcknowledgement();
                return;
            }

            // Out and away: the halo grows the whole time and fades the whole
            // time, so the motion has one direction. A swell-and-return would be
            // the breath's shape at a different speed, which is the confusion this
            // whole channel exists to avoid.
            _glowScale.ScaleX = _glowScale.ScaleY = 1.0 + (AckHaloTo - 1.0) * progress;
            Glow.Opacity = 1.0 - progress;
        }

        private void EndAcknowledgement()
        {
            _ackStartedAt = 0;
            _glowScale.ScaleX = _glowScale.ScaleY = 1.0;
            Glow.Opacity = 1.0;

            // Off the roster again if this put it there, following StopPulse's own
            // rule about the heart: a beating badge keeps the orb on the ticker
            // whatever else stops.
            if (_ackBorrowedTheRoster && !_breathing && !HeartBadge.IsVisible)
            {
                Pulsing.Remove(this);
            }

            _ackBorrowedTheRoster = false;
        }

        // How the acknowledgment is drawn right now, for the UI suite. Nothing in
        // the app reads it.
        internal bool IsAcknowledging => _ackStartedAt != 0;

        // --- Voice dictation mic ---
        // Hover shows a small flyout window below the orb with action
        // buttons in a semicircular arc (see OrbFlyout — its own window,
        // not a control drawn inside this one's 56x56 bounds). The mic
        // button records, transcribes locally via Whisper, and types the
        // result into this session's terminal. See VoiceRecorder,
        // SpeechTranscriber and TerminalFocuser.SendText.

        // Created on first hover, not in the constructor — see the field's
        // own comment for why. A no-op when the feature is off, so nothing
        // here ever constructs a VoiceRecorder — and triggers macOS's
        // mic-permission prompt — for someone who hasn't opted in.
        // internal: only reachable in production via a real hover (see
        // ScheduleFlyoutShow/OnFlyoutShowTick), which needs a pointer genuinely
        // resting on the orb. Driven directly for the same reason ApplyState is.
        internal void EnsureFlyoutShown()
        {
            if (_flyout is null)
            {
                _flyout = new OrbFlyout();
                _flyout.MicClicked += ToggleRecording;
                _flyout.ArrangeClicked += ArrangeAllOrbs;
                _flyout.SettingsClicked += OpenSettings;
                _flyout.SpeakClicked += OnSpeakClicked;
                _flyout.ChatClicked += OpenChat;

                // The other half of the hover bridge described on
                // _hideFlyoutTimer: entering the flyout must cancel a hide
                // that Root.PointerExited already scheduled, and leaving it
                // must schedule one of its own in case the pointer doesn't
                // land back on the orb either.
                _flyout.PointerEntered += (_, _) => CancelFlyoutHide();
                _flyout.PointerExited += (_, _) => ScheduleFlyoutHide();
            }

            // Shown for both kinds now. It used to be hidden on gateway orbs
            // because dictation had nowhere to go for them; the chat panel is
            // that somewhere, and StopRecording opens it with the words in its
            // input box rather than sending them.
            bool micOn = ClaudeBuddySettings.VoiceInputEnabled;
            _flyout.SetMicVisible(micOn);

            // Only on local sessions. A gateway orb opens its panel when you
            // click it, so a button that did the same thing one ring further out
            // would be a second way to do the thing the orb already does.
            // Any local CLI, each behind its own setting. A gateway orb still
            // doesn't get the button: clicking it already opens its panel, so a
            // second way to do the same thing one ring further out is noise.
            _flyout.SetChatVisible(
                (_lastStatus?.IsLocalCli ?? false)
                && CliChatFormat.For(_lastStatus!.Source).ChatEnabled());

            _flyout.SetArranged(SessionManager.Instance?.IsArranged ?? false);

            // Speech is global rather than per-orb, so a flyout opening
            // while something is already being read has to show the stop
            // glyph rather than offer to start a second one. Reads the real
            // state now rather than a flag some click left behind.
            _flyout.SetSpeakState(TextToSpeech.State);

            // The arc's virtual centre (ArcOrigin) aligns with the orb's
            // centre so the semicircle sits concentric with the orb. The
            // animation starts with the flyout centred on the orb so the
            // buttons are hidden behind it and emerge downward.
            //
            // PointToScreen, not raw arithmetic: Position is physical screen
            // pixels, these are DIP measurements, and the two only line up
            // at 100% display scaling.
            var target = new Point(
                OrbCentre - _flyout.ArcOriginX,
                OrbCentre - _flyout.ArcOriginY);
            var from = new Point(
                OrbCentre - _flyout.Width / 2,
                OrbCentre - _flyout.Height / 2);

            _flyout.ShowNear(
                from: this.PointToScreen(from),
                to: this.PointToScreen(target),
                owner: this);
        }

        // Centre of the orb in its own window's DIPs — half of Root's pinned
        // 56x56. Unchanged by MemberScale: a team member is drawn smaller
        // around this same point, never moved off it.
        private const double OrbCentre = 28;

        // --- Speak latest turn --------------------------------------------------

        // Deliberately tells the flyout nothing. It used to push the glyph itself
        // either side of the call, which was a guess that happened to be right on
        // the way in and always wrong on the way out — speech that ended by itself
        // left the stop glyph up until the flyout was reopened, and the neural
        // engine's several seconds of preparation looked identical to playing.
        // TextToSpeech.StateChanged is the single source now; see SessionManager,
        // which broadcasts it to every orb because speech is global rather than
        // per-orb.
        internal void OnSpeakClicked()
        {
            if (TextToSpeech.IsSpeaking)
            {
                TextToSpeech.Cancel();
                return;
            }

            // A gateway session has no transcript on this machine, so the text
            // comes from the conversation itself — fetched if this session has
            // never been opened, which is why this branch is async where the
            // local one isn't.
            if (_lastStatus?.Source == SessionSource.OpenClaw)
            {
                _ = SpeakRemoteAsync();
                return;
            }

            SpeakIfThereIsAnything(FindSpeakableText());
        }

        // Excluded from coverage: both of its lines. Reaching SpeakNow means a
        // transcript with something in it was found, and what happens next is the
        // machine running the tests reading it out loud — so a test that covered
        // this line would be one nobody could run with other people in the room.
        // Which text is found is FindSpeakableText, which is measured.
        [ExcludeFromCodeCoverage]
        private void SpeakIfThereIsAnything(string? text)
        {
            if (text is null) return;
            SpeakNow(text);
        }

        // Safe to call: SessionManager.Instance is null outside the running app, so
        // this is a no-op there — which is what an arrange should be when there
        // are no orbs to arrange.
        internal static void ArrangeAllOrbs() => SessionManager.Instance?.ArrangeOrbsInPattern();

        // Excluded from coverage: SettingsWindow.Toggle puts the app in the Dock,
        // shows a window and takes it key, then starts a status ticker. Its own
        // exclusion says the same; this is the call site, and calling it would do
        // all of that for real.
        [ExcludeFromCodeCoverage]
        private static void OpenSettings() => SettingsWindow.Toggle();

        // Excluded from coverage: makes the machine make a noise. TextToSpeech.Speak
        // is itself already excluded for that, and scoping the exclusion to this one
        // call keeps OnSpeakClicked's decisions — already speaking, nothing to say —
        // measured. An earlier attempt at testing the caller end to end actually
        // spoke out loud on a developer's machine, which is how narrow this needs
        // to be.
        [ExcludeFromCodeCoverage]
        private static void SpeakNow(string text) =>
            TextToSpeech.Speak(text, ClaudeBuddySettings.SpeakVoice);

        internal async Task SpeakRemoteAsync()
        {
            var title = _lastStatus?.Title ?? "";
            var text = await OpenClawSessions.LastAssistantTextAsync(SessionId, title);

            if (string.IsNullOrWhiteSpace(text)) return;

            Dispatcher.UIThread.Post(() => TextToSpeech.Speak(text, ClaudeBuddySettings.SpeakVoice));
        }

        // Called by SessionManager when speech starts, changes phase or stops.
        public void SetFlyoutSpeakState(TextToSpeech.SpeakState state) =>
            _flyout?.SetSpeakState(state);

        // This session's own transcript first, then a search by directory.
        //
        // The fallback is for a session that dispatches work rather than
        // doing it: a controller has no transcript of its own, but the
        // background jobs it launched write theirs into project dirs named
        // for the same cwd, and the most recent of those is what "read the
        // last turn" means when you click its orb.
        // `home` exists so the cwd fallback below is reachable without a
        // transcript in the developer's real ~/.claude. TranscriptReader's search
        // already takes one for the same reason; this just passes it through.
        // One frame of the shared pulse, for every orb currently pulsing.
        //
        // A named method rather than the Tick lambda it used to be, because the
        // ticker is process-wide and accumulates every orb ever shown for the life
        // of the process — nothing closes a window to remove one. A test that
        // waited on the real timer passed alone and failed once the rest of the
        // suite loaded the machine, even with a ten-second budget. Driving it is
        // the fix, and it also covers the arm that matters: the ticker stops
        // itself when the last orb finishes, rather than spinning at 30fps
        // forever.
        internal static void TickAllPulses()
        {
            for (var i = Pulsing.Count - 1; i >= 0; i--) Pulsing[i].TickPulse();
            if (Pulsing.Count == 0) _ticker?.Stop();
        }

        internal string? FindSpeakableText(string? home = null)
        {
            // Not for a gateway session. It has no transcript on this machine,
            // and the cwd fallback below would match a *local* project directory
            // with the same path and speak an unrelated local session's last
            // turn as though it were the remote agent's. The lookup also walks
            // every project directory recursively, on the UI thread, before
            // getting there.
            //
            if (!(_lastStatus?.IsLocalCli ?? false)) return null;

            // Codex reads through its own entry point, which understands a
            // rollout and — the part that matters — has no cwd fallback. Sharing
            // the path below would have found no Claude Code rows in a rollout
            // and then searched ~/.claude/projects for the same directory,
            // speaking an unrelated Claude Code session's last turn out of a
            // Codex orb.
            if (_lastStatus?.Source == SessionSource.Codex)
                return TranscriptReader.LatestCodexAgentText(_lastStatus?.TranscriptPath);

            if (_lastStatus?.Source == SessionSource.Grok)
                return TranscriptReader.LatestGrokAgentText(_lastStatus?.TranscriptPath);

            var path = _lastStatus?.TranscriptPath;
            var text = TranscriptReader.LatestAssistantText(path, SessionId);
            if (text is not null) return text;

            var cwd = _lastStatus?.Cwd;
            if (string.IsNullOrEmpty(cwd)) return null;

            var fallback = TranscriptReader.LatestTranscriptForCwd(cwd, home);
            if (fallback is not null)
            {
                text = TranscriptReader.LatestAssistantText(fallback);
                if (text is not null) return text;
            }

            return null;
        }

        // Called by SessionManager when the arrangement state changes, so
        // every orb's flyout (if it exists) reflects whether clicking the
        // arrange button would arrange or restore.
        public void SetFlyoutArranged(bool arranged) => _flyout?.SetArranged(arranged);

        // Called by ChatPanel when it closes itself, so the arc becomes
        // available again without the orb having to watch the window.
        public void SetChatOpen(bool open) => _chatOpen = open;

        // The flyout's keyboard button. Same destination a gateway orb's click
        // reaches, arrived at differently because for a local session the click
        // is already spoken for.
        // Excluded from coverage: needs SessionManager.Instance to hand back a
        // session, and this suite deliberately never sets it — making one current
        // starts the status-directory watcher, the two-second scan timer and a
        // tray icon, none of which a test should own. RemoteScanTests,
        // SessionScanTests and GatewayScanTests all make the same choice and say
        // so.
        //
        // What the chat panel does once opened is covered directly in the
        // ChatPanel suites, which construct one against a FakeChatSession rather
        // than going through an orb.
        [ExcludeFromCodeCoverage]
        internal void OpenChat()
        {
            var chat = SessionManager.Instance?.RemoteChatFor(SessionId);
            if (chat is null) return;

            _chatOpen = true;
            HideFlyoutNow();
            ChatPanel.OpenFor(this, chat);
        }

        // Whether a dictation capture is in progress. The panel mirrors it on
        // its own mic button and refuses to be dismissed while it is true.
        public bool IsRecording => _recording;

        // The panel's mic drives this orb's recorder rather than constructing a
        // second one — that keeps one recorder per session, along with the red
        // pulse and the 30-second cap, all working exactly as they already do.
        // Excluded from coverage: both arms reach a live VoiceRecorder — one to
        // construct and start it (PvRecorder.Create opens the microphone), the
        // other to stop it and push captured audio through Whisper.net. There is
        // no third path, so there is nothing here a headless runner can execute.
        // The panel's own mirroring of IsRecording is covered where it lives.
        [ExcludeFromCodeCoverage]
        public void ToggleRecording()
        {
            if (_recording) StopRecording();
            else StartRecording();
        }

        // Hides the flyout unconditionally — used by SessionManager before
        // starting an arrangement animation, since a flyout anchored to a
        // moving orb would look broken.
        public void HideFlyout() => HideFlyoutNow();

        // Immediate, not scheduled — dragging moves the orb every pointer
        // move, and a flyout animating toward a stale position underneath a
        // moving orb would read as broken rather than as a hover effect.
        private void HideFlyoutNow()
        {
            CancelFlyoutShow();
            _hideFlyoutTimer?.Stop();
            _flyout?.Hide();
        }

        internal void CancelFlyoutHide() => _hideFlyoutTimer?.Stop();

        internal void CancelFlyoutShow() => _showFlyoutTimer?.Stop();

        // The delay is only for *opening* the flyout from nothing. Coming back
        // onto the orb from its own open flyout is the other half of the hover
        // bridge, not a new request, and pausing there would be a stutter in
        // the middle of an interaction the user is already having.
        internal void ScheduleFlyoutShow()
        {
            // On the method rather than on PointerEntered, the same way
            // ScheduleFlyoutHide carries its own _recording guard: the rule is
            // "the arc does not open while the chat panel has that space", and a
            // rule about the arc belongs where the arc is scheduled. Left on the
            // handler it is one caller's business, and the next caller has to
            // remember it.
            if (_chatOpen) return;

            if (_flyout?.IsVisible == true)
            {
                EnsureFlyoutShown();
                return;
            }

            _showFlyoutTimer ??= new DispatcherTimer { Interval = FlyoutHoverDelay };
            _showFlyoutTimer.Stop();

            // One handler, however many hovers — same reason as the hide timer.
            _showFlyoutTimer.Tick -= OnFlyoutShowTick;
            _showFlyoutTimer.Tick += OnFlyoutShowTick;
            _showFlyoutTimer.Start();
        }

        internal void OnFlyoutShowTick(object? sender, EventArgs e)
        {
            _showFlyoutTimer!.Stop();

            // PointerExited cancels this timer, but a drag that carries the orb
            // out from under a stationary cursor, or an orb closing mid-wait,
            // doesn't necessarily raise one — so confirm rather than assume.
            if (!Root.IsPointerOver) return;

            EnsureFlyoutShown();
        }

        // A no-op while recording: the flyout is the only way to stop, so it
        // must stay up regardless of where the pointer wanders.
        internal void ScheduleFlyoutHide()
        {
            if (_recording) return;

            _hideFlyoutTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _hideFlyoutTimer.Stop();

            // Re-subscribing on every schedule would stack a duplicate Tick
            // handler per hover; there both is and only ever needs to be one.
            _hideFlyoutTimer.Tick -= OnFlyoutHideTick;
            _hideFlyoutTimer.Tick += OnFlyoutHideTick;
            _hideFlyoutTimer.Start();
        }

        internal void OnFlyoutHideTick(object? sender, EventArgs e)
        {
            _hideFlyoutTimer!.Stop();

            // The grace period ends with the pointer having genuinely landed
            // on one of the two windows after all (a slow, deliberate move
            // across the gap) — nothing to hide in that case.
            if (Root.IsPointerOver || (_flyout?.IsPointerOverFlyout ?? false)) return;

            _flyout?.Hide();
        }

        // Excluded from coverage: constructs and starts a real VoiceRecorder,
        // which is itself excluded wholesale for the same reason (it opens an
        // actual microphone). There is no seam to test the surrounding
        // dispatch logic without either a working input device (which a CI
        // runner may or may not have, making the catch-vs-success branch
        // nondeterministic across machines — the exact kind of platform
        // dependence this ticket's rules forbid relying on) or a real
        // recording actually starting and running for up to 30 seconds on a
        // background capture thread, which is not something a headless suite
        // should leave running.
        [ExcludeFromCodeCoverage]
        private void StartRecording()
        {
            if (_recording) return;

            try
            {
                _recorder = new VoiceRecorder();

                // Fired from VoiceRecorder's own capture thread, so this has
                // to hop back to the UI thread before touching anything here
                // — StopRecording ends up updating Avalonia controls and
                // awaiting the transcription pipeline, none of which is safe
                // to do from off the dispatcher.
                _recorder.SilenceDetected += () => Dispatcher.UIThread.Post(StopRecording);

                _recorder.Start();
            }
            catch (Exception ex)
            {
                // No input device, permission denied, device busy — a
                // convenience feature failing to start is not worth a crash.
                _recorder = null;
                Console.Error.WriteLine($"Claude Buddy: couldn't start recording: {ex.Message}");
                return;
            }

            _recording = true;
            ChatPanel.SetRecording(this, true);

            // Flat red, fast — visibly distinct from the waiting/generating
            // pulses, so "listening" reads as its own thing rather than as
            // the session itself having changed state.
            AnimateColor(RecordingColor, TimeSpan.FromMilliseconds(150), _lastState);
            StartPulse(1.18, TimeSpan.FromMilliseconds(350), new SineEaseInOut());

            // A hard cap, not just a courtesy: this runs whether or not the
            // user remembers to click again, so a missed second click can't
            // leave the mic — and VoiceRecorder's own capture thread — running
            // indefinitely.
            _recordingCap = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _recordingCap.Tick += (_, _) => StopRecording();
            _recordingCap.Start();
        }

        // Excluded from coverage along with StartRecording: only reachable
        // after a real recording actually started (a live VoiceRecorder,
        // itself excluded), and its meaningful body pipes real captured audio
        // through SpeechTranscriber — a local Whisper.net model load and
        // inference, not something a headless CI runner should be doing
        // either. The early-return guard is inseparable from that same
        // feature rather than a decision worth a seam of its own.
        [ExcludeFromCodeCoverage]
        private async void StopRecording()
        {
            if (!_recording || _recorder is null) return;

            _recording = false;
            ChatPanel.SetRecording(this, false);
            _recordingCap?.Stop();
            _recordingCap = null;

            // Back to whatever the session's own state actually is —
            // StartRecording never changed _lastState, only the pulse and
            // colour drawn over it.
            ApplyState(string.IsNullOrEmpty(_lastState) ? "idle" : _lastState);

            // ...and back to still, if this orb is parked. The line above puts
            // it on the pulse roster, which is right for every orb except a
            // parked one — and ApplyPresence's own gate would decline, since
            // nothing about the presence changed. Forced rather than reasoned
            // around: the mic owns the orb's motion while it is recording, and
            // this is where that ownership is handed back.
            if (_presence != OrbPresence.Present) ApplyPresence(_presence, force: true);

            // The pointer is very likely still over the mic right after a
            // click, but the recording that was forcing the flyout to stay
            // up just ended — re-derive from where the pointer actually is
            // now rather than assuming either way.
            if (Root.IsPointerOver || (_flyout?.IsPointerOverFlyout ?? false))
            {
                CancelFlyoutHide();
            }
            else
            {
                HideFlyoutNow();
            }

            var recorder = _recorder;
            _recorder = null;

            float[] pcm;
            try
            {
                pcm = recorder.Stop();
            }
            finally
            {
                recorder.Dispose();
            }

            if (pcm.Length == 0) return;

            var text = await SpeechTranscriber.TranscribeAsync(pcm);
            if (string.IsNullOrWhiteSpace(text)) return;

            // With a chat panel open for this session, the words belong in its
            // input box — unsent, for the reason TerminalFocuser.SendText gives
            // at its own definition: transcription is a typing aid and doesn't
            // get to decide you meant it. Reviewing before Enter is the whole
            // contract, and it is the same one either way.
            if (ChatPanel.IsOpenFor(SessionId))
            {
                ChatPanel.AppendToInput(text);
                return;
            }

            // Dictated at a gateway orb with no panel up: open one and put the
            // words in it. Still unsent — the panel is what makes "review before
            // Enter" possible for a session that has no terminal to review in.
            if (_lastStatus?.Source == SessionSource.OpenClaw)
            {
                var chat = SessionManager.Instance?.RemoteChatFor(SessionId);
                if (chat is not null)
                {
                    _chatOpen = true;
                    HideFlyoutNow();
                    ChatPanel.OpenFor(this, chat);
                    ChatPanel.AppendToInput(text);
                }

                return;
            }

            var status = _lastStatus;
            if (status is null) return;

            await TerminalFocuser.SendText(status, text);
        }

        // Ends an in-progress recording without transcribing or sending
        // anything — only reachable from Closed, where the orb (and the
        // session it belongs to) is going away regardless.
        //
        // Excluded from coverage: its only caller is the Closed event
        // (rule of this suite: never Close() a headless orb, which corrupts a
        // process-wide font cache — see OrbWindowStateTests), and its
        // meaningful body needs the same live VoiceRecorder StartRecording
        // does.
        [ExcludeFromCodeCoverage]
        private void CancelRecording()
        {
            _recordingCap?.Stop();
            _recordingCap = null;

            if (!_recording || _recorder is null) return;

            _recording = false;
            ChatPanel.SetRecording(this, false);
            try { _recorder.Stop(); } catch { }
            _recorder.Dispose();
            _recorder = null;
        }

        // --- Click, dragging & context menu ---
        // Left-press starts as a potential click; it becomes a drag once the
        // pointer moves past a small threshold. A clean click jumps to the
        // session's terminal (macOS, best-effort — see TerminalFocuser).
        //
        // Dragging an orb pins it: it keeps that spot as sessions come and go
        // (SessionManager.ReflowPositions steps over pinned orbs) and the spot
        // is remembered across restarts, keyed by the session's directory. The
        // context menu's "Return this orb to the stack" undoes both.

        // Where the user dragged this orb is remembered against this key — the
        // session's cwd, set by SessionManager. Empty for a session with no cwd
        // reported, which pins for this run only since there's nothing stable
        // to remember it against.
        public string PositionKey { get; set; } = "";

        // True once the user has placed this orb by hand, whether in this run or
        // in an earlier one.
        public bool IsPinned { get; private set; }

        private SessionStatus? _lastStatus;
        private bool _pressed;
        private bool _dragging;
        private PixelPoint _windowStart;
        private PixelPoint _pointerStart;

        // A team lead drags its members along with it, so a team can be moved
        // out of the way as one thing — which is the whole point of drawing it
        // as one thing. Captured on press, because membership can change
        // mid-drag and an orb that joins the team while you're moving it should
        // not jump.
        private readonly List<(OrbWindow Orb, PixelPoint Start)> _followers = new();

        // Excluded from coverage: a synthesized press on an orb ends in a real
        // click being resolved, and OnClicked's default action is
        // TerminalFocuser.Focus — which fires tmux, ps and osascript as real
        // processes off-thread, with no OS guard at its own entry point. On a CI
        // runner that is an unpredictable side effect rather than a test, and on
        // a developer's machine it moves their windows.
        //
        // The drag arithmetic these three share is not lost with them: the
        // 6-pixel threshold, the follower offsets and the arranged-anchor nudge
        // are all OrbArrangement's, which is pure and swept by
        // tests/ArrangementTests across 20736 cases. What is excluded here is the
        // plumbing from a pointer to those answers, and the click resolution
        // itself is covered directly in OrbWindowClickResolutionTests via
        // OnClicked/ActionFor rather than through a pointer.
        [ExcludeFromCodeCoverage]
        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _pressed = true;
                _dragging = false;

                // Recorded here because only the press carries it; the release
                // that acts on it does not.
                _clickCount = e.ClickCount;

                _windowStart = Position;
                _pointerStart = this.PointToScreen(e.GetPosition(this));

                _followers.Clear();
                foreach (var member in SessionManager.Instance?.MembersOf(SessionId)
                                       ?? Enumerable.Empty<OrbWindow>())
                {
                    _followers.Add((member, member.Position));
                }

                // When arranged, the whole cluster moves as one — every
                // orb in the pattern that isn't already a team follower
                // tags along so the shape stays intact.
                if (SessionManager.Instance?.IsArranged == true)
                {
                    var existing = new HashSet<string>(_followers.Select(f => f.Orb.SessionId));
                    foreach (var sibling in SessionManager.Instance.ArrangedSiblings(SessionId))
                    {
                        if (!existing.Contains(sibling.SessionId))
                            _followers.Add((sibling, sibling.Position));
                    }
                }

                e.Pointer.Capture(this);
            }
        }

        // Excluded from coverage: same reason as OnPointerPressed — this only runs
        // between a real press and a real release, and it moves live windows via
        // Position while TeamLinks.Refresh() redraws native arrow windows over
        // them.
        [ExcludeFromCodeCoverage]
        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_pressed) return;

            var current = this.PointToScreen(e.GetPosition(this));
            var dx = current.X - _pointerStart.X;
            var dy = current.Y - _pointerStart.Y;

            if (!_dragging && Math.Abs(dx) < 6 && Math.Abs(dy) < 6) return;

            // Only on the transition into dragging, not every move after —
            // a flyout animating toward a stale position underneath a moving
            // orb would read as broken, so it's simplest to just take it off
            // screen the instant a drag actually starts.
            if (!_dragging) HideFlyoutNow();

            _dragging = true;
            Position = new PixelPoint(_windowStart.X + dx, _windowStart.Y + dy);

            // The team travels with its lead, keeping the shape the user
            // arranged it in rather than being re-stacked around the new spot.
            foreach (var (member, start) in _followers)
            {
                member.Position = new PixelPoint(start.X + dx, start.Y + dy);
            }

            // Drag a member away and its arrow to the lead stretches with it —
            // which is the point of the arrow, since a dragged orb is exactly
            // the one that no longer sits next to the team it belongs to. Cheap
            // enough to do per pointer move: a few windows repositioned, no
            // scan of anything.
            TeamLinks.Refresh();
            ChatPanel.RepositionFor(this);
        }

        // Excluded from coverage: the end of the same gesture. Either it commits a
        // drag — writing every dragged orb's position through SessionManager and
        // re-anchoring the arrangement — or it resolves a click, which is
        // TerminalFocuser again. Both halves are covered where they are decided:
        // OrbArrangement for the geometry, OrbWindowClickResolutionTests for the
        // click.
        [ExcludeFromCodeCoverage]
        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!_pressed) return;

            _pressed = false;
            e.Pointer.Capture(null);

            if (_dragging)
            {
                SetPinned(true);
                SessionManager.Instance?.RememberOrbPosition(this);

                // Members carried along are pinned too, or the next reflow
                // would pull them back into the stack and leave the lead on its
                // own with three long arrows.
                //
                // Their positions are only *remembered* when they don't share
                // the lead's key. A team usually runs in one directory, and
                // positions are keyed by directory, so writing each member's
                // spot would overwrite the lead's with an offset copy of
                // itself — the group would come back scattered rather than not
                // come back at all. See RestoreOrbPosition.
                foreach (var (member, _) in _followers)
                {
                    member.SetPinned(true);
                    if (member.PositionKey != PositionKey)
                    {
                        SessionManager.Instance?.RememberOrbPosition(member);
                    }
                }

                // When arranged, every orb in the pattern moved by this same
                // delta (see OnPointerPressed's ArrangedSiblings) — the shape's
                // saved centre needs to move with it, or absorbing the next
                // orb to join or leave would snap it back to where it was.
                if (SessionManager.Instance?.IsArranged == true)
                {
                    SessionManager.Instance.ShiftArrangementAnchor(
                        Position.X - _windowStart.X, Position.Y - _windowStart.Y);
                }
            }
            else
            {
                // A team member has no window of its own — its tmux server has
                // no client attached anywhere — so a click that finds nothing
                // falls through to the session leading it. See
                // TerminalFocuser.Focus.
                // A click has always meant "take me to this session". For a
                // Claude Code session that is its terminal; for a gateway
                // session there is no terminal anywhere, and the honest answer
                // is a place to read and reply — so the panel *is* the
                // destination rather than an extra affordance.
                //
                // Guarded on the source rather than on RemoteChatFor answering,
                // which it now does for local sessions as well: that is what the
                // flyout's keyboard button opens, and it must not quietly become
                // what a click does instead. Going to the terminal is the oldest
                // behaviour this app has and people reach for it without looking.
                OnClicked(_clickCount);
            }

            _followers.Clear();
        }

        // --- what a click does ------------------------------------------------

        // One, two or three clicks, each bound to whatever the user chose.
        //
        // The awkward part is that these gestures are prefixes of each other: a
        // double click is a single click that turns out to have company. So a
        // single click can only be acted on immediately if nothing longer is
        // bound to something different — otherwise it has to wait long enough to
        // find out, and going to a terminal is the most common thing this app
        // does. The wait is therefore paid only by people who asked for it by
        // binding a second gesture, and never by anyone leaving the defaults
        // alone, where two and three clicks do nothing at all.
        private const int MultiClickMs = 300;

        private DispatcherTimer? _clickTimer;
        private int _pendingClicks;
        private int _clickCount = 1;

        // How many clicks this orb last resolved, and how many gestures have
        // reached that point at all. Written here and read by the UI suite, by
        // nothing in the app.
        //
        // It exists because the two ways a click can come to nothing are
        // indistinguishable from outside, and telling them apart is the whole of
        // the team-orb bug this was added for: a gesture that was *eaten* before
        // it ever became a click, and a gesture that arrived here and found
        // "none" bound to it, both look exactly like an orb that ignored you.
        // Every other observable is downstream of RunClickAction, so a test that
        // asserts on one of those is asserting the destination and cannot say
        // whether the journey started.
        internal int ResolvedGestures { get; private set; }

        internal int LastResolvedClicks { get; private set; }

        internal void OnClicked(int clicks)
        {
            // Beyond three there is nothing to bind, and treating a fourth click
            // as a fresh single would fire the single-click action in the middle
            // of somebody drumming on the orb.
            if (clicks > 3) return;

            ResolvedGestures++;
            LastResolvedClicks = clicks;

            _clickTimer?.Stop();
            _clickTimer = null;
            _pendingClicks = clicks;

            if (!AwaitsMoreClicks(clicks))
            {
                RunClickAction(clicks);
                return;
            }

            _clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(MultiClickMs) };
            _clickTimer.Tick += (_, _) =>
            {
                _clickTimer?.Stop();
                _clickTimer = null;
                RunClickAction(_pendingClicks);
            };
            _clickTimer.Start();
        }

        // Whether a longer gesture is bound to something this one isn't already
        // going to do. Same action on two and three as on one means the wait
        // would change nothing, so there is no reason to pay it.
        internal static bool AwaitsMoreClicks(int clicks)
        {
            var mine = ActionFor(clicks);

            for (var longer = clicks + 1; longer <= 3; longer++)
            {
                var other = ActionFor(longer);
                if (other != "none" && other != mine) return true;
            }

            return false;
        }

        internal static string ActionFor(int clicks) => clicks switch
        {
            1 => ClaudeBuddySettings.ClickAction,
            2 => ClaudeBuddySettings.DoubleClickAction,
            _ => ClaudeBuddySettings.TripleClickAction
        };

        internal void RunClickAction(int clicks)
        {
            switch (ActionFor(clicks))
            {
                case "chat":
                    OpenChat();
                    break;

                case "speak":
                    OnSpeakClicked();
                    break;

                case "none":
                    break;

                default:
                    GoToSession();
                    break;
            }
        }

        // "Take me to this session", which for a local CLI is its terminal and
        // for a gateway agent is the only place it exists — the panel. That
        // second case is not a fallback bolted on for this feature; it is what a
        // click on a gateway orb has always done, because there is no terminal
        // anywhere to go to.
        internal void GoToSession()
        {
            if (!(_lastStatus?.IsLocalCli ?? false) && TryOpenRemoteChat()) return;

            TerminalFocuser.Focus(
                _lastStatus,
                SessionManager.Instance?.StatusFor(_lastStatus?.Lead),
                SessionId,
                SessionManager.Instance?.PaneClaimsByOthers(SessionId),
                acknowledge: AcknowledgeFromAnyThread);
        }

        // Put the orb at a position it was dragged to in an earlier run, without
        // treating it as a fresh drag (nothing to write back).
        public void PinAt(PixelPoint position)
        {
            Position = position;
            SetPinned(true);
        }

        public void Unpin() => SetPinned(false);

        private void SetPinned(bool pinned)
        {
            IsPinned = pinned;
            // Only worth offering once there's something to undo.
            ResetPositionItem.IsVisible = pinned;
        }

        // The roster, for a background orb that wants it. Delegates the same way
        // the two lifecycle handlers below do: the orb knows which session was
        // right-clicked and nothing else, and every rule about where a gesture
        // goes lives in ClickRouting with the subprocesses in TerminalFocuser.
        //
        // Safe with no status yet — TerminalFocuser.OpenAgentsView returns on
        // null — which matters because the menu is built before the first
        // UpdateFrom, and a right-click during that window would otherwise be
        // the one gesture that could throw.
        internal void AgentsView_Click(object? sender, RoutedEventArgs e)
        {
            TerminalFocuser.OpenAgentsView(_lastStatus);
        }

        internal void ResetIdle_Click(object? sender, RoutedEventArgs e)
        {
            SessionManager.Instance?.ResetSessionToIdle(SessionId);
        }

        internal void ResetPosition_Click(object? sender, RoutedEventArgs e)
        {
            SessionManager.Instance?.ReturnOrbToStack(SessionId);
        }

        // Both of these delegate rather than acting, the same way ResetIdle_Click
        // does: the orb knows which session was right-clicked and nothing else,
        // and every rule about what may be done to a session lives with the
        // manager that owns its file and its pid.
        internal void Dismiss_Click(object? sender, RoutedEventArgs e)
        {
            SessionManager.Instance?.DismissSession(SessionId);
        }

        internal void EndSession_Click(object? sender, RoutedEventArgs e)
        {
            SessionManager.Instance?.EndSession(SessionId);
        }

        // Excluded from coverage: needs SessionManager.Instance to hand back a
        // session, and this suite never sets it — making one current starts the
        // status-directory watcher, the scan timer and a tray icon. Same reason
        // OpenChat above carries the attribute, and the panel it would open is
        // covered directly in the ChatPanel suites against a FakeChatSession.
        [ExcludeFromCodeCoverage]
        private bool TryOpenRemoteChat()
        {
            var chat = SessionManager.Instance?.RemoteChatFor(SessionId);
            if (chat is null) return false;

            _chatOpen = true;
            HideFlyoutNow();
            ChatPanel.OpenFor(this, chat);
            return true;
        }

        internal void Exit_Click(object? sender, RoutedEventArgs e) =>
            ShutdownIfDesktop(Application.Current?.ApplicationLifetime);

        // Excluded from coverage: its guard is the only reachable half under a
        // headless lifetime, and the half behind it ends the process. Splitting
        // the two so the guard could be measured would leave the more interesting
        // question — is this lifetime a desktop one? — measured in a method that
        // does nothing with the answer.
        //
        // IsDesktopLifetime is that question asked where a test can ask it, and
        // ItRefusesToQuitAHostThatIsNotADesktopApp asserts the answer that keeps
        // this harmless.
        [ExcludeFromCodeCoverage]
        private static void ShutdownIfDesktop(IApplicationLifetime? lifetime)
        {
            if (lifetime is IClassicDesktopStyleApplicationLifetime desktop) Shutdown(desktop);
        }

        internal static bool IsDesktopLifetime(IApplicationLifetime? lifetime) =>
            lifetime is IClassicDesktopStyleApplicationLifetime;

        // Excluded from coverage: ends the process. Scoped to this one call so the
        // guard stays measured — under the headless lifetime it is false, which is
        // what keeps this harmless, and that is worth asserting rather than
        // excluding along with it.
        [ExcludeFromCodeCoverage]
        private static void Shutdown(IClassicDesktopStyleApplicationLifetime desktop) =>
            desktop.Shutdown();
    }
}
