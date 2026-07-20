using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;

namespace ClaudeBuddy
{
    public class SessionStatus
    {
        [JsonPropertyName("state")]
        public string State { get; set; } = "idle";

        [JsonPropertyName("cwd")]
        public string Cwd { get; set; } = "";
    }

    // Watches %TEMP%\claude_buddy\<session_id>.txt (one per running Claude
    // Code session, written by ClaudeBuddyHook.ps1) and keeps one OrbWindow
    // per session in sync. A session is considered gone once its file is
    // deleted (SessionEnd hook, on graceful exit) or hasn't been touched in
    // StaleAfter (fallback for Ctrl+C and other ungraceful termination,
    // which SessionEnd isn't reliably delivered for).
    public class SessionManager
    {
        public static SessionManager? Instance { get; private set; }

        private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

        private readonly string _statusDir =
            Path.Combine(Path.GetTempPath(), "claude_buddy");

        private readonly Dictionary<string, OrbWindow> _windows = new();
        private readonly List<string> _order = new(); // stable stacking order

        private FileSystemWatcher? _watcher;
        private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(2) };
        private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };

        public void Start()
        {
            Instance = this;
            Directory.CreateDirectory(_statusDir);

            StartWatching();

            _pollTimer.Tick += (_, _) => ScanAndUpdate();
            _pollTimer.Start();

            _debounce.Tick += (_, _) =>
            {
                _debounce.Stop();
                ScanAndUpdate();
            };

            ScanAndUpdate();
        }

        private void StartWatching()
        {
            try
            {
                _watcher = new FileSystemWatcher(_statusDir, "*.txt")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName,
                    EnableRaisingEvents = true
                };
                _watcher.Changed += (_, _) => Application.Current.Dispatcher.Invoke(RestartDebounce);
                _watcher.Created += (_, _) => Application.Current.Dispatcher.Invoke(RestartDebounce);
                _watcher.Deleted += (_, _) => Application.Current.Dispatcher.Invoke(RestartDebounce);
            }
            catch
            {
                // If the watcher can't be set up for some reason, the poll timer still covers us.
            }
        }

        private void RestartDebounce()
        {
            _debounce.Stop();
            _debounce.Start();
        }

        private void ScanAndUpdate()
        {
            var seen = new HashSet<string>();
            var now = DateTime.UtcNow;
            bool setChanged = false;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(_statusDir, "*.txt");
            }
            catch
            {
                files = Enumerable.Empty<string>();
            }

            foreach (var file in files)
            {
                var sessionId = Path.GetFileNameWithoutExtension(file);

                SessionStatus? status;
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    status = System.Text.Json.JsonSerializer.Deserialize<SessionStatus>(stream);
                }
                catch
                {
                    continue; // mid-write; retry next tick
                }

                if (status is null) continue;

                // A session waiting on you (permission prompt / question) never
                // goes stale on its own — no further hook fires until you
                // respond, so the file's mtime is frozen for as long as you're
                // away. Pruning it would hide the orb exactly when it matters
                // most. Use "Reset this session to idle" to clear a genuinely
                // abandoned one manually.
                if (status.State != "waiting")
                {
                    DateTime lastWrite;
                    try
                    {
                        lastWrite = File.GetLastWriteTimeUtc(file);
                    }
                    catch
                    {
                        continue; // file vanished mid-scan
                    }

                    if (now - lastWrite > StaleAfter)
                    {
                        continue; // treat as gone; cleaned up in the removal pass below
                    }
                }

                seen.Add(sessionId);

                if (!_windows.TryGetValue(sessionId, out var window))
                {
                    window = new OrbWindow(sessionId);
                    _windows[sessionId] = window;
                    _order.Add(sessionId);
                    window.Show();
                    setChanged = true;
                }

                window.UpdateFrom(status);
            }

            var gone = _windows.Keys.Where(id => !seen.Contains(id)).ToList();
            foreach (var id in gone)
            {
                _windows[id].Close();
                _windows.Remove(id);
                _order.Remove(id);
                setChanged = true;
            }

            if (setChanged)
            {
                ReflowPositions();
            }
        }

        private void ReflowPositions()
        {
            var workArea = SystemParameters.WorkArea;
            const double size = 56;
            const double spacing = 12;
            const double margin = 24;

            for (int i = 0; i < _order.Count; i++)
            {
                var window = _windows[_order[i]];
                window.Left = workArea.Right - size - margin;
                window.Top = workArea.Top + margin + i * (size + spacing);
            }
        }

        public void ResetSessionToIdle(string sessionId)
        {
            var file = Path.Combine(_statusDir, sessionId + ".txt");
            var cwd = "";
            try
            {
                var existing = System.Text.Json.JsonSerializer.Deserialize<SessionStatus>(File.ReadAllText(file));
                if (existing is not null) cwd = existing.Cwd;
            }
            catch { }

            var reset = new SessionStatus { State = "idle", Cwd = cwd };
            try
            {
                File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(reset));
            }
            catch { }

            if (_windows.TryGetValue(sessionId, out var window))
            {
                window.UpdateFrom(reset);
            }
        }
    }
}
