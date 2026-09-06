using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;

// Shapes.Path against System.IO.Path, which the project's implicit usings bring
// in. Aliased rather than fully qualified because this file names the type a
// dozen times and nothing here touches a file path.
using Path = Avalonia.Controls.Shapes.Path;

namespace ClaudeBuddy
{
    // One account's usage orb.
    //
    // A separate window from OrbWindow rather than a mode of it. Every one of
    // OrbWindow's channels — avatar, kind badge, heartbeat, presence, team role,
    // and the six context-menu items that act on a session — is meaningless for
    // an account, and threading a session-shaped nothing through UpdateFrom to
    // reach the two rings would be more code than this whole file, in the one
    // class nobody wants to make harder to read.
    //
    // What it does borrow is the shell recipe and the conventions that go with
    // it: transparent undecorated topmost window, a Root pinned to the top left
    // so Windows' minimum window size cannot become a layout input, ShowOnAllSpaces
    // and AcceptFirstClick on Opened, and OrbGlyph for the letters. The
    // arithmetic for the rings is in UsageRingGeometry, which has no window in
    // it and is tested on its own.
    internal partial class AccountOrbWindow : Window
    {
        // The ring radii, about a centre at (36,36). Ordered outside in, which
        // is also least-urgent to most-urgent: a week is a slower problem than
        // five hours, and money is the one that does not reset on its own.
        private const double WeeklyRadius = 32;
        private const double SessionRadius = 25;
        private const double ExtraRadius = 18;
        private static readonly Point Centre = new(36, 36);

        // Claude Code's own colour surface (S=0.558, V=0.843), which is what
        // AgentPalette generates from and what the /color names sit on. Rings in
        // these three read as native beside the session orbs instead of as a
        // second design that happened to land on the same screen.
        //
        // Deliberately not user settings. OrbColors exists because a session's
        // state colours are a matter of taste; "how much is left" is not, and
        // three more colour pickers would be three more things to get into a
        // state where a full ring looks fine.
        internal const string CalmHex = "#5FD79B";
        internal const string WarnHex = "#D7AF5F";
        internal const string DangerHex = "#D75F5F";

        // How far a stale orb fades. Enough to read as doubtful at a glance,
        // not so far that the ring it is still drawing becomes unreadable — a
        // stale 94% is exactly the reading someone needs to be able to see.
        private const double StaleOpacity = 0.45;

        // One cancellation per breathing ring. Without it a ring that drops out
        // of the danger band keeps breathing forever: RunAsync's only stop is
        // its token, and an animation left running on a shape whose reading has
        // since gone calm is a green ring pulsing like an emergency.
        private readonly Dictionary<Path, CancellationTokenSource> _breathing = new();

        private bool _pinned;

        // The same guard OrbWindow.UpdateFrom carries for its own tooltip
        // (CB-104): SetTip always builds a fresh Border, and doing that while
        // the pointer rests on the orb closes and reopens the popup on every
        // poll — a flicker for as long as the mouse stays still. This class
        // is a separate window from OrbWindow (see the class comment) and so
        // has its own copy of the same call, which round one of CB-104 never
        // touched — an account orb's poll is five minutes apart rather than
        // two seconds, so the same bug flickered too, just rarely enough to
        // be missed the first time.
        private string? _lastTipLabel;
        private string? _lastTipSummary;

        // Reference identity of the tooltip's current content, for the same
        // reason OrbWindow.CurrentThoughtBubble exists: a test can assert the
        // flicker fix without a real popup.
        internal Control? CurrentThoughtBubble => ToolTip.GetTip(Root) as Control;

        public AccountOrbWindow() : this(string.Empty)
        {
        }

        public AccountOrbWindow(string key)
        {
            AccountKey = key;
            InitializeComponent();

            Opened += (_, _) =>
            {
                this.ShowOnAllSpaces();

                // Without this the first click on the orb is swallowed
                // activating the app, which makes pinning feel broken exactly
                // once per launch — the occasion on which it most needs to
                // work.
                this.AcceptFirstClick();
            };

            Root.PointerEntered += (_, _) => HoverStarted?.Invoke(this);
            Root.PointerExited += (_, _) => HoverEnded?.Invoke(this);
            Root.PointerPressed += OnPressed;
            Root.PointerMoved += OnMoved;
            Root.PointerReleased += OnReleased;

            // Never had an explicit Placement, which defaults to Pointer —
            // the tooltip opened wherever the cursor already was, i.e.
            // inside its own 72x72 anchor, the same overlap-driven flicker
            // OrbWindow had under PlacementMode.Top. See the comment on
            // OrbWindow.ConfigureThoughtBubblePlacement for the full
            // mechanism (CB-104).
            OrbWindow.ConfigureThoughtBubblePlacement(Root);
        }

        // Which account this orb is for: the CLAUDE_CONFIG_DIR it was read
        // under, or the empty string for the account the app itself runs as.
        // Opaque to everything here — it exists so SessionManager can match an
        // orb to a reading without either of them keeping a second identity.
        internal string AccountKey { get; }

        internal event Action<AccountOrbWindow>? HoverStarted;
        internal event Action<AccountOrbWindow>? HoverEnded;
        internal event Action<AccountOrbWindow>? Clicked;

        // What the last update decided, kept so a headless test can assert on
        // what a person would have seen rather than on how it was drawn.
        internal string GlyphText => Glyph.Text ?? string.Empty;

        internal string? WeeklyColour { get; private set; }

        internal string? SessionColour { get; private set; }

        internal bool ExtraIsAbsent { get; private set; }

        internal bool IsDimmed { get; private set; }

        internal bool IsPinned => _pinned;

        internal string? CliMarkName { get; private set; }

        internal string? CliMarkFill { get; private set; }

        internal bool CliMarkVisible => CliBadge.IsVisible;

        internal void SetPinned(bool pinned)
        {
            _pinned = pinned;
            PinBadge.IsVisible = pinned;
        }

        // Everything the orb shows, from one reading.
        //
        // `now` is a parameter rather than DateTime.UtcNow so the expiry and
        // staleness rules can be driven to their boundaries in a test without
        // waiting a quarter of an hour, which is the same reason AccountUsage
        // takes it.
        internal void UpdateFrom(AccountUsage usage, DateTimeOffset now)
        {
            Glyph.Text = OrbGlyph.For(usage.Label, ClaudeBuddySettings.TwoLetterGlyphs);

            IsDimmed = usage.IsStale(now);
            Root.Opacity = IsDimmed ? StaleOpacity : 1;

            var weekly = usage.LiveWeekly(now);
            var session = usage.LiveSession(now);

            WeeklyColour = ApplyRing(WeeklyArc, WeeklyTrack, WeeklyRadius, weekly?.Percent);
            SessionColour = ApplyRing(SessionArc, SessionTrack, SessionRadius, session?.Percent);

            ApplyExtra(usage.Extra);
            ApplyCli(usage.Source);

            var summary = Summary(usage, now);
            if (usage.Label != _lastTipLabel || summary != _lastTipSummary)
            {
                ToolTip.SetTip(Root, OrbWindow.ThoughtBubble(usage.Label, summary, compact: true));
                _lastTipLabel = usage.Label;
                _lastTipSummary = summary;
            }
        }

        private void ApplyCli(AccountUsageSource source)
        {
            var mark = CliMark.For(source);
            CliBadge.Background = new SolidColorBrush(Color.Parse(mark.FillHex));
            CliGlyph.Data = StreamGeometry.Parse(mark.GlyphPath);
            CliBadge.IsVisible = true;
            CliMarkName = mark.Name;
            CliMarkFill = mark.FillHex;
        }

        // One ring: the arc, its colour, and whether it breathes.
        //
        // Returns the colour so a test can ask what a person would have seen
        // without reading a brush back off a shape.
        private string? ApplyRing(Path arc, Ellipse track, double radius, double? percent)
        {
            if (percent is not { } value)
            {
                // No reading for this window — expired, or never sent. The track
                // stays so the orb keeps its shape, but nothing claims a number.
                arc.Data = null;
                arc.Stroke = null;
                track.IsVisible = true;
                StopBreathing(arc);
                return null;
            }

            var colour = UsageRingGeometry.ColourFor(value, CalmHex, WarnHex, DangerHex);

            arc.Data = ArcGeometry(radius, value);
            arc.Stroke = new SolidColorBrush(Color.Parse(colour));
            track.IsVisible = true;

            if (UsageRingGeometry.ShouldBreathe(value)) StartBreathing(arc);
            else StopBreathing(arc);

            return colour;
        }

        // The inner ring, which is the one that is often not a gauge.
        //
        // Three states, not two, and the first version collapsed two of them.
        // An account with no extra usage has no cap to be a share of, so its
        // ring is a dotted outline saying "there is nothing here" rather than a
        // solid arc at zero. But an account whose *limit has been reached* is
        // the opposite of that — it is full — and drawing it as the same dotted
        // absence made a spent budget look like a budget that never existed.
        // RingPercent is where that distinction lives.
        private void ApplyExtra(ExtraUsage? extra)
        {
            var percent = extra?.RingPercent;

            ExtraIsAbsent = percent is null;

            if (percent is null)
            {
                ExtraArc.Data = null;
                ExtraArc.Stroke = null;
                StopBreathing(ExtraArc);

                ExtraTrack.StrokeThickness = 2;
                ExtraTrack.StrokeDashArray = new AvaloniaList<double> { 0.25, 2.5 };
                ExtraTrack.Stroke = new SolidColorBrush(Color.Parse("#22FFFFFF"));
                return;
            }

            ExtraTrack.StrokeThickness = 5;
            ExtraTrack.StrokeDashArray = null;
            ExtraTrack.Stroke = new SolidColorBrush(Color.Parse("#17FFFFFF"));

            var colour = UsageRingGeometry.ColourFor(
                percent.Value, CalmHex, WarnHex, DangerHex);

            ExtraArc.Data = ArcGeometry(ExtraRadius, percent.Value);
            ExtraArc.Stroke = new SolidColorBrush(Color.Parse(colour));

            if (UsageRingGeometry.ShouldBreathe(percent.Value)) StartBreathing(ExtraArc);
            else StopBreathing(ExtraArc);
        }

        // The tested arithmetic, turned into something Avalonia will draw.
        //
        // The full case is an ellipse and not an arc, and that is not tidiness:
        // an arc sweeping 360 degrees has coincident endpoints and renders as an
        // empty figure, so the account at 100% would be the one account showing
        // no ring at all. UsageRingGeometry flags it for exactly this reason.
        private static Geometry? ArcGeometry(double radius, double percent)
        {
            var arc = UsageRingGeometry.ArcFor(Centre, radius, percent);

            if (arc.IsEmpty) return null;

            if (arc.IsFull)
            {
                return new EllipseGeometry(
                    new Rect(Centre.X - radius, Centre.Y - radius, radius * 2, radius * 2));
            }

            var figure = new PathFigure
            {
                StartPoint = arc.Start,
                IsClosed = false,
                IsFilled = false
            };

            figure.Segments!.Add(new ArcSegment
            {
                Point = arc.End,
                Size = new Size(radius, radius),
                IsLargeArc = arc.LargeArc,
                SweepDirection = SweepDirection.Clockwise,
                RotationAngle = 0
            });

            var geometry = new PathGeometry();
            geometry.Figures!.Add(figure);
            return geometry;
        }

        // A ring in the danger band breathes.
        //
        // A per-shape opacity animation rather than joining OrbWindow's shared
        // 20fps ticker, which is the right instrument for dozens of session orbs
        // and the wrong one to reach into from here: its roster and its
        // _breathing flag are OrbWindow's private business, and there are at
        // most a handful of account orbs. The cost is a compositor animation on
        // a single Path, which is not measurable against the blur this app
        // already refused to use.
        private void StartBreathing(Path arc)
        {
            // Already breathing: leave it alone. Restarting on every poll would
            // reset the phase every five minutes, which is not visible as a
            // restart so much as a stutter nobody can explain.
            if (_breathing.ContainsKey(arc)) return;

            var cancel = new CancellationTokenSource();
            _breathing[arc] = cancel;

            var breath = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(2600),
                IterationCount = IterationCount.Infinite,
                PlaybackDirection = PlaybackDirection.Alternate,
                Easing = new SineEaseInOut(),
                Children =
                {
                    new KeyFrame
                    {
                        Cue = new Cue(0),
                        Setters = { new Setter(OpacityProperty, 1d) }
                    },
                    new KeyFrame
                    {
                        Cue = new Cue(1),
                        Setters = { new Setter(OpacityProperty, 0.55d) }
                    }
                }
            };

            breath.RunAsync(arc, cancel.Token);
        }

        private void StopBreathing(Path arc)
        {
            if (_breathing.Remove(arc, out var cancel))
            {
                cancel.Cancel();
                cancel.Dispose();
            }

            arc.Opacity = 1;
        }

        // The tooltip's second line: what the rings are saying, in words, for
        // the moment before anyone has learned to read them.
        internal static string Summary(AccountUsage usage, DateTimeOffset now)
        {
            if (!usage.Available) return "no subscription limits on this account";

            var weekly = usage.LiveWeekly(now);
            var session = usage.LiveSession(now);

            if (weekly is null && session is null) return "no reading yet";

            var parts = new System.Collections.Generic.List<string>();
            if (session is not null) parts.Add($"5h {Math.Floor(session.Percent)}%");
            if (weekly is not null) parts.Add($"7d {Math.Floor(weekly.Percent)}%");

            var text = string.Join(" · ", parts);
            return usage.IsStale(now) ? text + " · stale" : text;
        }

        // Press, move, release — because the orb has to be both a button and a
        // thing you can put somewhere else.
        //
        // The two gestures are told apart by distance, not by timing. A
        // press-and-hold that never moves is still a click here, which is the
        // forgiving reading: someone who rests on the orb before letting go
        // meant to press it, and a click that silently did nothing because it
        // lasted too long is the kind of unreliability that makes a control feel
        // broken without ever being reproducible.
        private const double DragThreshold = 4;

        private PixelPoint _pressedAt;
        private Point _grabOffset;
        private bool _pressing;
        private bool _dragging;

        private void OnPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            e.Handled = true;
            _pressing = true;
            _dragging = false;
            _pressedAt = Position;
            _grabOffset = e.GetPosition(this);
            e.Pointer.Capture(Root);
        }

        private void OnMoved(object? sender, PointerEventArgs e)
        {
            if (!_pressing) return;

            var here = e.GetPosition(this);
            var dx = here.X - _grabOffset.X;
            var dy = here.Y - _grabOffset.Y;

            if (!_dragging && Math.Sqrt(dx * dx + dy * dy) < DragThreshold) return;

            _dragging = true;

            // Position is in physical pixels while the pointer is in
            // device-independent ones, so the delta has to be scaled or the orb
            // travels at the wrong speed on any display that is not at 100%.
            var scale = DesktopScaling;
            Position = new PixelPoint(
                Position.X + (int)Math.Round(dx * scale),
                Position.Y + (int)Math.Round(dy * scale));
        }

        private void OnReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_pressing) return;

            _pressing = false;
            e.Pointer.Capture(null);

            if (_dragging)
            {
                _dragging = false;
                if (Position != _pressedAt) Moved?.Invoke(this);
                return;
            }

            Clicked?.Invoke(this);
        }

        internal event Action<AccountOrbWindow>? Moved;
    }
}
