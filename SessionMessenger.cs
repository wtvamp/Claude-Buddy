using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeBuddy
{
    // How a delivery attempt against a headless session's registry socket
    // came out.
    //
    // WriteFailed exists in the shape of this enum but is never produced.
    // Seams.Write reports success as a bool, and the one failure that bool can
    // carry — connect refused, timed out, or the write itself threw — reads
    // identically to the caller either way: nothing reached the far side. It
    // stays in the enum because a future Seams.Write that distinguishes "never
    // connected" from "connected, then the write itself failed" should not need
    // a new enum member to say so; today, both fold into SocketRefused.
    internal enum DeliveryResult { Accepted, NoRegistryEntry, UnsupportedProtocol, SocketRefused, WriteFailed }

    internal readonly record struct DeliveryReceipt(DeliveryResult Result, string? AgentStatus);

    // The wire format one delivery is encoded as, with no I/O in it at all —
    // the part CLAUDE.md's fixture rule means for this branch: the shape below
    // is not invented, it's transcribed from reverse-engineering the real
    // Claude Code binary, and it is kept pure precisely so that transcription
    // can be checked against golden bytes without a socket anywhere nearby.
    internal static class SessionMessageFrame
    {
        private const int MaxFromNameLength = 64;

        // The body Claude Code expects to see wrapped around a cross-session
        // delivery, so the receiving session can tell this message arrived
        // from another agent rather than from the person at its own keyboard.
        //
        // fromName is sanitized here rather than trusted from the caller —
        // "defensively even though callers should already pass something
        // safe", per the spec this was built against — because this is the
        // one place a stray '"' or '<' in a machine or agent name would
        // otherwise break out of the attribute it's placed in.
        internal static string Wrap(string fromName, string text) =>
            $"<cross-session-message from=\"{SanitizeFromName(fromName)}\" from-mode=\"prompting\">\n{text}\n</cross-session-message>";

        private static string SanitizeFromName(string fromName)
        {
            var builder = new StringBuilder(fromName.Length);
            foreach (var c in fromName)
            {
                if (c is '"' or '<' or '>') continue;
                builder.Append(c);
            }

            return builder.Length > MaxFromNameLength
                ? builder.ToString(0, MaxFromNameLength)
                : builder.ToString();
        }

        // The full byte sequence to write to the socket: an auth line first
        // when a token is available, then the deliver line, each one compact
        // JSON terminated with a bare '\n'. Every line goes through
        // JsonSerializer rather than string concatenation — the one field that
        // is not just handed to the serializer verbatim is `content`, which
        // must be exactly Wrap's string, JSON-string-escaped like any other
        // string value and not escaped a second time by hand.
        internal static byte[] Encode(string? peerToken, string fromName, string text, Guid msgId)
        {
            using var buffer = new MemoryStream();

            if (!string.IsNullOrEmpty(peerToken))
            {
                WriteLine(buffer, new { type = "auth", token = peerToken });
            }

            WriteLine(buffer, new
            {
                msgV = 1,
                msg_id = msgId.ToString(),
                type = "user",
                message = new { role = "user", content = Wrap(fromName, text) },
                priority = "next",
                from = fromName
            });

            return buffer.ToArray();
        }

        private static void WriteLine(Stream stream, object payload)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload) + "\n");
            stream.Write(bytes, 0, bytes.Length);
        }
    }

    // Delivers one message to a headless/background Claude Code session over
    // the IPC socket it registers for itself — the alternative to typing into
    // a terminal a background session doesn't have.
    //
    // Split into pure decision logic plus a Seams record wrapping the actual
    // I/O, the same shape UsagePoller.RunOne uses around its subprocess
    // boundary: DeliverAsync is covered against a fake Seams with no socket in
    // sight, and Live is the only part excluded from coverage, because it is
    // the only part that touches a real one.
    internal sealed class SessionMessenger
    {
        // ReadKey takes the whole Entry rather than a bare socket-path string:
        // the key file's location is Entry.KeyPath, already resolved by
        // SessionRegistry.Scan, and threading the path back out as a separate
        // string here would just be re-deriving what Scan already worked out.
        internal sealed record Seams(
            Func<IReadOnlyList<SessionRegistry.Entry>> Registry,
            Func<int, bool> PidAlive,
            Func<SessionRegistry.Entry, string?> ReadKey,
            Func<string, byte[], CancellationToken, Task<bool>> Write);

        private readonly Seams _seams;

        internal SessionMessenger(Seams seams) => _seams = seams;

        // Excluded from coverage: scans real config roots, checks real pids,
        // reads a real key file and opens a real Unix domain socket. None of
        // that is decision logic — DeliverAsync is what decides what to do
        // with what these seams report, and it is covered against a fake Seams
        // in tests/UnitTests/SessionMessengerTests.cs. The socket half is
        // covered separately, against a real listener, in
        // tests/IntegrationTests/SessionMessengerSocketTests.cs — through the
        // same Write delegate shape, not through Live itself.
        [ExcludeFromCodeCoverage]
        internal static Seams Live(IReadOnlyList<string> configRoots) => new(
            Registry: () => SessionRegistry.Scan(configRoots),
            PidAlive: ProcessLiveness.IsRunning,
            ReadKey: ReadKeyFile,
            Write: WriteToSocket);

        [ExcludeFromCodeCoverage]
        private static string? ReadKeyFile(SessionRegistry.Entry entry)
        {
            if (entry.KeyPath is null) return null;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(entry.KeyPath));
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

                return doc.RootElement.TryGetProperty("peerToken", out var token)
                    && token.ValueKind == JsonValueKind.String
                    ? token.GetString()
                    : null;
            }
            catch
            {
                // Missing, unreadable, or not JSON — no token, same as a
                // session with no key file at all.
                return null;
            }
        }

        // The only real socket I/O in this file: connect, write, close. There
        // is no acknowledgement to read back — a successful connect and write
        // is all "accepted" ever means for this protocol — so this never
        // attempts a read, and a five-second ceiling keeps a listener that
        // accepts and then goes silent from hanging the caller forever.
        [ExcludeFromCodeCoverage]
        private static async Task<bool> WriteToSocket(string socketPath, byte[] bytes, CancellationToken ct)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), linked.Token);
                await socket.SendAsync(bytes, SocketFlags.None, linked.Token);
                return true;
            }
            catch
            {
                // Nothing listening, connection refused, timed out, or any
                // other I/O failure along the way — all read the same to a
                // caller: the message did not reach the far side.
                return false;
            }
        }

        // The name of the counterpart everything else here needs is
        // `<fromName>`. This is the only shape Buddy ever constructs one from —
        // there is exactly one machine tag per delivery, and building the
        // string here rather than at each call site is what keeps that
        // literal in one place.
        internal static string FromName(string machineTag) => $"Claude Buddy on {machineTag}";

        // Looks up the session, checks it can actually be reached, and hands
        // it one message. Each early return is a distinct DeliveryResult
        // rather than a bool plus an out-parameter, so a caller deciding how
        // to tell a user "that didn't work" can say why without re-deriving
        // it.
        internal async Task<DeliveryReceipt> DeliverAsync(
            string sessionId, string fromName, string text, CancellationToken ct)
        {
            var entries = _seams.Registry();
            var entry = SessionRegistry.Find(entries, sessionId, _seams.PidAlive);

            if (entry is null) return new DeliveryReceipt(DeliveryResult.NoRegistryEntry, null);

            if (!SessionRegistry.Speaks(entry.Value))
                return new DeliveryReceipt(DeliveryResult.UnsupportedProtocol, entry.Value.Status);

            var peerToken = _seams.ReadKey(entry.Value);
            var bytes = SessionMessageFrame.Encode(peerToken, fromName, text, Guid.NewGuid());

            var ok = await _seams.Write(entry.Value.SocketPath, bytes, ct);

            return new DeliveryReceipt(
                ok ? DeliveryResult.Accepted : DeliveryResult.SocketRefused, entry.Value.Status);
        }
    }
}
