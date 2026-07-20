using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;

namespace ClaudeBuddy
{
    public partial class OrbWindow : Window
    {
        private static readonly Color IdleColor = Color.Parse("#5B7A94");       // calm slate blue
        private static readonly Color GeneratingColor = Color.Parse("#8B6FD1"); // violet
        private static readonly Color WaitingColor = Color.Parse("#E8983B");    // amber

        public string SessionId { get; }

        private string _lastState = "";

        private readonly SolidColorBrush _orbBrush = new(IdleColor);
        private readonly ColorTransition _colorTransition;
        private readonly ScaleTransform _orbScale = new();
        private CancellationTokenSource? _pulseCts;

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
            Glow.Fill = _orbBrush;
            Orb.RenderTransform = _orbScale;

            // Unlike WPF, Loaded fires *after* the first UpdateFrom here, so
            // honor any state that already arrived instead of stomping it.
            Loaded += (_, _) => ApplyState(string.IsNullOrEmpty(_lastState) ? "idle" : _lastState);

            Opened += (_, _) => this.ShowOnAllSpaces();
        }

        public void UpdateFrom(SessionStatus status)
        {
            var folder = string.IsNullOrEmpty(status.Cwd)
                ? ""
                : System.IO.Path.GetFileName(status.Cwd.TrimEnd('\\', '/'));

            ToolTip.SetTip(Root, string.IsNullOrEmpty(status.Cwd)
                ? SessionId
                : $"{folder}\n{status.Cwd}");

            Glyph.Text = string.IsNullOrEmpty(folder)
                ? "•"
                : folder[..1].ToUpperInvariant();

            SessionInfoItem.Header = string.IsNullOrEmpty(status.Cwd) ? SessionId : status.Cwd;

            if (status.State != _lastState)
            {
                _lastState = status.State;
                if (IsLoaded)
                {
                    ApplyState(status.State);
                }
                // else: Loaded handler applies _lastState once the window is up.
            }
        }

        private void ApplyState(string state)
        {
            switch (state)
            {
                case "waiting":
                    AnimateColor(WaitingColor, TimeSpan.FromMilliseconds(300));
                    StartPulse(1.22, TimeSpan.FromMilliseconds(500), new QuadraticEaseOut());
                    break;
                case "generating":
                    AnimateColor(GeneratingColor, TimeSpan.FromMilliseconds(300));
                    StartPulse(1.14, TimeSpan.FromMilliseconds(900), new SineEaseInOut());
                    break;
                default:
                    StopPulse();
                    AnimateColor(IdleColor, TimeSpan.FromMilliseconds(400));
                    StartPulse(1.06, TimeSpan.FromSeconds(2.2), new SineEaseInOut());
                    break;
            }
        }

        private void AnimateColor(Color to, TimeSpan duration)
        {
            _colorTransition.Duration = duration;
            _orbBrush.Color = to;
        }

        private void StartPulse(double to, TimeSpan duration, Easing easing)
        {
            _pulseCts?.Cancel();
            _pulseCts = new CancellationTokenSource();

            var animation = new Animation
            {
                Duration = duration,
                IterationCount = IterationCount.Infinite,
                PlaybackDirection = PlaybackDirection.Alternate,
                Easing = easing,
                Children =
                {
                    new KeyFrame
                    {
                        Cue = new Cue(0d),
                        Setters =
                        {
                            new Setter(ScaleTransform.ScaleXProperty, 1.0),
                            new Setter(ScaleTransform.ScaleYProperty, 1.0)
                        }
                    },
                    new KeyFrame
                    {
                        Cue = new Cue(1d),
                        Setters =
                        {
                            new Setter(ScaleTransform.ScaleXProperty, to),
                            new Setter(ScaleTransform.ScaleYProperty, to)
                        }
                    }
                }
            };

            // Run against the Orb visual; Avalonia's transform animator finds
            // the ScaleTransform via the visual's RenderTransform.
            _ = animation.RunAsync(Orb, _pulseCts.Token);
        }

        private void StopPulse()
        {
            _pulseCts?.Cancel();
            _pulseCts = null;
        }

        // --- Dragging & context menu ---
        // Dragged position is only honored until the next time the active
        // session set changes (add/remove), at which point SessionManager
        // reflows the whole stack. That's an intentional tradeoff to keep
        // the stack tidy as sessions come and go.

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        }

        private void ResetIdle_Click(object? sender, RoutedEventArgs e)
        {
            SessionManager.Instance?.ResetSessionToIdle(SessionId);
        }

        private void Exit_Click(object? sender, RoutedEventArgs e)
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        }
    }
}
