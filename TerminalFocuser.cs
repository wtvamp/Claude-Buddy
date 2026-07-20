using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClaudeBuddy
{
    // Best-effort "take me to that session's terminal" for a left-click on
    // an orb. Silently does nothing when the status file predates the hook
    // scripts that record terminal info.
    //
    // Precision degrades gracefully. macOS: exact iTerm2 pane (via its
    // session UUID, which survives into tmux environments), exact
    // Terminal.app tab (via tty), otherwise just activate the terminal app;
    // the first click triggers a macOS Automation permission prompt for
    // controlling the terminal — that's expected; approve it once.
    // Windows: the terminal window whose PID the hook recorded, otherwise
    // any window of the app named by term_program (the WSL case, where the
    // Windows-side parent chain dead-ends in an interop bridge). Selecting
    // the exact Windows Terminal *tab* isn't possible — WT doesn't expose
    // its tabs to other processes.
    internal static class TerminalFocuser
    {
        public static void Focus(SessionStatus? status)
        {
            if (status is null) return;

            if (OperatingSystem.IsWindows())
            {
                FocusWindows(status);
                return;
            }

            if (!OperatingSystem.IsMacOS()) return;

            string? script;
            if (!string.IsNullOrEmpty(status.TermId))
            {
                script = ITermSelectScript(status.TermId);
            }
            else
            {
                script = status.TermProgram switch
                {
                    "Apple_Terminal" when !string.IsNullOrEmpty(status.Tty) => TerminalSelectScript(status.Tty),
                    "Apple_Terminal" => "tell application \"Terminal\" to activate",
                    "iTerm.app" => "tell application \"iTerm\" to activate",
                    "vscode" => "tell application \"Visual Studio Code\" to activate",
                    "ghostty" => "tell application \"Ghostty\" to activate",
                    "WezTerm" => "tell application \"WezTerm\" to activate",
                    _ => null
                };
            }

            if (script is null) return;

            try
            {
                var psi = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false };
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add(script);
                Process.Start(psi);
            }
            catch
            {
                // Focusing is a convenience; never let it take the app down.
            }
        }

        // --- Windows ---

        private const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        private static void FocusWindows(SessionStatus status)
        {
            try
            {
                var hwnd = IntPtr.Zero;

                if (status.TermPid > 0)
                {
                    try
                    {
                        hwnd = Process.GetProcessById(status.TermPid).MainWindowHandle;
                    }
                    catch { } // terminal exited; fall through
                }

                if (hwnd == IntPtr.Zero)
                {
                    var processName = status.TermProgram switch
                    {
                        "WindowsTerminal" => "WindowsTerminal",
                        "vscode" => "Code",
                        _ => null
                    };
                    if (processName is null) return;

                    hwnd = Process.GetProcessesByName(processName)
                        .Select(p => p.MainWindowHandle)
                        .FirstOrDefault(h => h != IntPtr.Zero);
                }

                if (hwnd == IntPtr.Zero) return;

                if (IsIconic(hwnd)) ShowWindowAsync(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }
            catch
            {
                // Same convenience-only rule as macOS.
            }
        }

        private static string ITermSelectScript(string sessionUuid) => $$"""
            tell application "iTerm"
                activate
                repeat with w in windows
                    repeat with t in tabs of w
                        repeat with s in sessions of t
                            if id of s is "{{sessionUuid}}" then
                                select w
                                select t
                                select s
                                return
                            end if
                        end repeat
                    end repeat
                end repeat
            end tell
            """;

        private static string TerminalSelectScript(string tty) => $$"""
            tell application "Terminal"
                activate
                repeat with w in windows
                    repeat with t in tabs of w
                        if tty of t is "/dev/{{tty}}" then
                            set selected of t to true
                            set index of w to 1
                            return
                        end if
                    end repeat
                end repeat
            end tell
            """;
    }
}
