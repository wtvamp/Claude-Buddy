using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeBuddy
{
    // Claude Code's own per-session registry at ~/.claude/sessions/<pid>.json —
    // NOT a Buddy invention, and not the same file AgentRoster reads. AgentRoster
    // asks a running `claude agents --json` for an in-memory list; this reads
    // what each session (including a headless one with no terminal to ask
    // anything of) writes to disk on its own, unprompted. A real, redacted
    // example:
    //
    //     {"pid":40957,"sessionId":"01991a2c-...","cwd":"/some/path",
    //      "version":"2.1.261","peerProtocol":1,
    //      "peerFeatures":["notify_idle","reply_across_default_dirs","artifact_yield"],
    //      "kind":"bg","entrypoint":"cli",
    //      "messagingSocketPath":"/tmp/cc-socks/40957.sock",
    //      "name":"job-hunter","jobId":"94f106","status":"idle",
    //      "bridgeSessionId":"..."}
    //
    // A sibling file in the same directory, `<pid>.<sha256hex(socketPath)>.key`,
    // holds the bearer token a caller needs to write to that socket —
    // `{"peerToken":"<32 lowercase hex>", ...}`. It is named off the socket
    // path rather than the pid alone because the pid is reused across restarts
    // and the socket is not; hashing the path is what SessionMessenger's caller
    // needs to find the right key without this file naming the pid twice.
    //
    // Kept pure and read-only, the same split AgentRoster and every registry
    // reader in this repo makes: this file only turns bytes already on disk
    // into an Entry, and never opens the socket it describes — that is
    // SessionMessenger's job, against a real Seams.
    internal static class SessionRegistry
    {
        // Only the fields a caller here can act on. PeerProtocol defaults to 0
        // when the JSON omits it, so an old session that predates the field
        // reads as "does not speak it" rather than accidentally matching
        // SupportedPeerProtocol by coincidence of a shared default.
        internal readonly record struct Entry(
            int Pid,
            string SessionId,
            string? Name,
            string Kind,
            int PeerProtocol,
            string SocketPath,
            string? Status,
            string? KeyPath);

        // The messaging protocol version this build of Buddy knows how to
        // speak. A registry entry naming a higher number is a newer Claude
        // Code than this app has been taught, and Speaks below refuses it
        // rather than guessing at a frame shape that may have changed.
        internal const int SupportedPeerProtocol = 1;

        // One registration, or null for anything that isn't a complete one.
        // Never throws — a corrupt or half-written status file (this one is
        // rewritten on every status change, so a reader can catch it mid-write)
        // is exactly as absent as a file that was never there.
        //
        // keyPath is not read from the JSON — the registry file doesn't name
        // its own key file — it is handed in by the caller, which for Scan
        // below means deriving it from the socket path first via KeyFileName.
        internal static Entry? Parse(string json, string? keyPath)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;

                if (!root.TryGetProperty("pid", out var pidEl)
                    || pidEl.ValueKind != JsonValueKind.Number
                    || !pidEl.TryGetInt32(out var pid))
                {
                    return null;
                }

                var sessionId = Str(root, "sessionId");
                if (string.IsNullOrWhiteSpace(sessionId)) return null;

                var socketPath = Str(root, "messagingSocketPath");
                if (string.IsNullOrWhiteSpace(socketPath)) return null;

                var peerProtocol = 0;
                if (root.TryGetProperty("peerProtocol", out var ppEl)
                    && ppEl.ValueKind == JsonValueKind.Number)
                {
                    ppEl.TryGetInt32(out peerProtocol);
                }

                return new Entry(
                    pid,
                    sessionId!,
                    Str(root, "name"),
                    Str(root, "kind") ?? "",
                    peerProtocol,
                    socketPath!,
                    Str(root, "status"),
                    keyPath);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // Named off the socket path rather than the pid, for the reason the
        // header comment gives: the pid is reused, the socket path is not, and
        // a caller matching a key to an entry must match on the thing that is
        // actually unique to this run.
        internal static string KeyFileName(int pid, string socketPath) =>
            $"{pid}.{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(socketPath)))}.key";

        // Every registration under every config root — one Claude Code account
        // can be running several background sessions at once, and a machine
        // with more than one account (ClaudeConfigRoots.All) has one registry
        // per account, none of which know about the others.
        //
        // One bad file must not stop the scan of the rest, the same rule
        // AgentRoster.ParseAgentsJson follows for one bad row: a session
        // mid-write to its own status file is not a reason to lose every
        // other session's entry.
        internal static IReadOnlyList<Entry> Scan(IReadOnlyList<string> configRoots)
        {
            var found = new List<Entry>();

            foreach (var root in configRoots)
            {
                var sessionsDir = Path.Combine(root, "sessions");
                if (!Directory.Exists(sessionsDir)) continue;

                foreach (var file in Directory.GetFiles(sessionsDir, "*.json"))
                {
                    try
                    {
                        var json = File.ReadAllText(file);

                        // Parsed once without a key to learn the socket path,
                        // then re-parsed — cheaper than threading the socket
                        // path back out separately — with whichever key file
                        // actually exists on disk for it.
                        var withoutKey = Parse(json, null);
                        if (withoutKey is null) continue;

                        var keyPath = Path.Combine(
                            sessionsDir, KeyFileName(withoutKey.Value.Pid, withoutKey.Value.SocketPath));

                        found.Add(File.Exists(keyPath)
                            ? withoutKey.Value with { KeyPath = keyPath }
                            : withoutKey.Value);
                    }
                    catch
                    {
                        // Not readable, not JSON, or gone between the listing
                        // and the read — same as any other file this app polls
                        // on a live, changing directory.
                    }
                }
            }

            return found;
        }

        // The one live entry for a session id, or null.
        //
        // Ordinal, not ignore-case: session ids are GUIDs Claude Code assigns,
        // never something a person typed, so there is no casing mismatch to be
        // lenient about — unlike AgentRoster.Resolve's peer *names*.
        //
        // pidAlive is a caller-supplied delegate rather than ProcessLiveness
        // called directly, the same seam AgentRoster and every other liveness
        // check in this repo takes, so a stale registry entry left behind by a
        // session that has since exited (its own status file is not always
        // cleaned up on a crash) is never mistaken for a live one.
        internal static Entry? Find(IReadOnlyList<Entry> entries, string sessionId, Func<int, bool> pidAlive)
        {
            foreach (var entry in entries)
            {
                if (!string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal)) continue;
                if (pidAlive(entry.Pid)) return entry;
            }

            return null;
        }

        // Whether this entry is something SessionMessenger can actually write
        // to: a protocol version this build understands, a socket path that
        // is present, and not a Windows named pipe. Claude Code on Windows
        // names its socket `\\.\pipe\...`, which .NET's UnixDomainSocketEndPoint
        // cannot dial — a different transport, not merely a different path
        // string — so it is refused here rather than failing later inside a
        // real connect attempt.
        internal static bool Speaks(Entry e) =>
            e.PeerProtocol == SupportedPeerProtocol
            && !string.IsNullOrEmpty(e.SocketPath)
            && !e.SocketPath.StartsWith(@"\\.\pipe\", StringComparison.Ordinal);

        private static string? Str(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
