using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClaudeBuddy
{
    public partial class OrbWindow : Window
    {
        private static readonly Color IdleColor = (Color)ColorConverter.ConvertFromString("#5B7A94");       // calm slate blue
        private static readonly Color GeneratingColor = (Color)ColorConverter.ConvertFromString("#8B6FD1"); // violet
        private static readonly Color WaitingColor = (Color)ColorConverter.ConvertFromString("#E8983B");    // amber

        public string SessionId { get; }

        private string _lastState = "";

        public OrbWindow(string sessionId)
        {
            SessionId = sessionId;
            InitializeComponent();
            Loaded += (_, _) => ApplyState("idle");
        }

        public void UpdateFrom(SessionStatus status)
        {
            ToolTip = string.IsNullOrEmpty(status.Cwd)
                ? SessionId
                : $"{System.IO.Path.GetFileName(status.Cwd.TrimEnd('\\', '/'))}\n{status.Cwd}";

            if (status.State != _lastState)
            {
                _lastState = status.State;
                ApplyState(status.State);
            }
        }

        private void ApplyState(string state)
        {
            switch (state)
            {
                case "waiting":
                    AnimateColor(WaitingColor, TimeSpan.FromMilliseconds(300));
                    StartWaitingPulse();
                    break;
                case "generating":
                    AnimateColor(GeneratingColor, TimeSpan.FromMilliseconds(300));
                    StartGeneratingPulse();
                    break;
                default:
                    StopPulse();
                    AnimateColor(IdleColor, TimeSpan.FromMilliseconds(400));
                    StartIdleBreathing();
                    break;
            }
        }

        private void AnimateColor(Color to, TimeSpan duration)
        {
            var anim = new ColorAnimation(to, duration) { EasingFunction = new QuadraticEase() };
            OrbBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }

        private void StartIdleBreathing()
        {
            var scale = new DoubleAnimation
            {
                From = 1.0,
                To = 1.06,
                Duration = TimeSpan.FromSeconds(2.2),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase()
            };
            OrbScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
            OrbScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        }

        private void StartGeneratingPulse()
        {
            var scale = new DoubleAnimation
            {
                From = 1.0,
                To = 1.14,
                Duration = TimeSpan.FromMilliseconds(900),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase()
            };
            OrbScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
            OrbScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        }

        private void StartWaitingPulse()
        {
            var scale = new DoubleAnimation
            {
                From = 1.0,
                To = 1.22,
                Duration = TimeSpan.FromMilliseconds(500),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new QuadraticEase()
            };
            OrbScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
            OrbScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        }

        private void StopPulse()
        {
            OrbScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            OrbScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }

        // --- Dragging & context menu ---
        // Dragged position is only honored until the next time the active
        // session set changes (add/remove), at which point SessionManager
        // reflows the whole stack. That's an intentional tradeoff to keep
        // the stack tidy as sessions come and go.

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            DragMove();
        }

        private void ResetIdle_Click(object sender, RoutedEventArgs e)
        {
            SessionManager.Instance?.ResetSessionToIdle(SessionId);
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }
    }
}
