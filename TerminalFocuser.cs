using System.Diagnostics;

namespace ClaudeBuddy
{
    // Best-effort "take me to that session's terminal" for a left-click on
    // an orb. macOS only for now; silently does nothing elsewhere or when
    // the status file predates the hook script that records terminal info.
    //
    // Precision degrades gracefully: exact iTerm2 pane (via its session
    // UUID, which survives into tmux environments), exact Terminal.app tab
    // (via tty), otherwise just activate the terminal app. The first click
    // triggers a macOS Automation permission prompt for controlling the
    // terminal — that's expected; approve it once.
    internal static class TerminalFocuser
    {
        public static void Focus(SessionStatus? status)
        {
            if (status is null || !OperatingSystem.IsMacOS()) return;

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
