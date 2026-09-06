using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeBuddy
{
    // The wire format two Claude Buddies use to show each other's sessions
    // verbatim, and the reason it exists rather than a nicer one.
    //
    // A remote session's panel used to be a *messaging channel*: what you typed
    // reached the far session's model as context, and what came back was a reply
    // that model composed for a peer — a second draft of its own conversation,
    // never the conversation itself. BridgeProtocol's own note records that being
    // watched side by side and losing. No amount of asking fixes it, because the
    // thing being asked is the thing that rewrites.
    //
    // So this stops asking a model for content at all. When the far machine is
    // also running Claude Buddy, that Buddy reads its session's transcript
    // **bytes off its own disk** and sends them here in framed pieces. The relay
    // model in the middle is reduced from an author to a courier: it pastes a
    // line of base64 it cannot read, and every piece carries a SHA-256 of what it
    // is supposed to be.
    //
    // **Verify or refuse.** That hash is the whole guarantee. A courier that
    // drops a character, reflows a line, helpfully "fixes" some base64, or
    // paraphrases the frame produces a payload whose digest does not match, and
    // an unverified payload is discarded rather than shown — see TryParseFrame,
    // which nulls it at the parse boundary so no later code can reach it by
    // accident. The failure mode is a visible error, never altered text
    // presented as a transcript. It cannot forge a matching digest for content
    // it changed, so what survives to the panel is provably the bytes the far
    // Buddy read.
    //
    // Pure on purpose, exactly like BridgeProtocol and ChatTranscript next door:
    // text in, records out. No process, no tmux, no files, no dispatcher.
    // RemoteMirrorServer and RemoteMirrorClient own all of that and hand the
    // strings here — which is what lets every rule below be tested without a
    // second machine, a relay, or a model.
    internal static class MirrorProtocol
    {
        // --- the frame -------------------------------------------------------

        // The marker follows CB-INFO's precedent next door, and for the same
        // reason: a frame arrives as an ordinary cross-session message,
        // indistinguishable from something a person is meant to read, so it
        // needs a prefix that says "this is plumbing" and gets it swallowed
        // before it can become a chat bubble.
        public const string Marker = "CB-MIRROR:";

        public const string Version = "v1";

        // Frame types. Deliberately short — every byte here is pasted through a
        // model's input line, and the whole line has to survive that trip.
        public const string Hello = "HELLO";       // client → server: are you a Buddy? send a roster
        public const string Roster = "ROSTER";     // server → client: what I can mirror and type into
        public const string Fetch = "FETCH";       // client → server: a byte range of a transcript
        public const string Chunk = "CHUNK";       // server → client: one piece of an answer
        public const string Watch = "WATCH";       // client → server: keep sending me what's new
        public const string Unwatch = "UNWATCH";   // client → server: stop
        public const string Input = "INPUT";       // client → server: type this into that session
        public const string Resend = "RESEND";     // client → server: that piece failed its hash
        public const string Ok = "OK";             // server → client: done
        public const string Err = "ERR";           // server → client: couldn't

        // Error codes. A code rather than a sentence because the client decides
        // what to do with it and the user reads wording chosen on this side of
        // the wire, not whatever the far machine happened to phrase.
        public const string ErrNoSession = "no-session";
        public const string ErrNoTranscript = "no-transcript";
        public const string ErrNoPane = "no-pane";

        // A terminal Buddy *can* address, which then refused the text.
        //
        // **Split from ErrNoPane because collapsing the two sent a user
        // looking for the wrong thing.** "There is nowhere to type" is a
        // statement about the session's terminal and it is what a user acts
        // on; saying it when the terminal was found and the delivery failed
        // is a wrong answer that reads like a right one. The two have
        // completely different fixes: one is "this terminal isn't supported",
        // the other is almost always macOS Automation consent not yet given,
        // or the window having been closed since the status file was written.
        public const string ErrTypeFailed = "type-failed";

        public const string ErrReplyOff = "reply-off";
        public const string ErrBadHash = "bad-hash";
        public const string ErrUnsupported = "unsupported";

        // The far session's own registry entry is gone — CB-105's messaging
        // path found no ~/.claude/sessions file for it at all, which almost
        // always means the background job it named has since stopped. There
        // is neither a pane nor a socket to reach it through, unlike
        // ErrNoPane below, where a pane genuinely does not exist but the
        // session itself is still there.
        public const string ErrNotRegistered = "not-registered";

        // A registry entry existed and looked reachable, but handing it the
        // text failed — the socket refused the connection, or the write
        // itself did. The same split as ErrNoPane/ErrTypeFailed, one layer
        // over: a route was found here too, and it declined it.
        public const string ErrDeliverFailed = "deliver-failed";

        // The value the INPUT reply's `via` field carries when a message was
        // handed to the far session's own messaging socket rather than typed
        // into a terminal. There is no equivalent constant for the typed
        // case — that was always the assumed shape of a successful send, so
        // this exists only to let the client tell the two apart.
        public const string ViaMessage = "msg";

        // How much raw payload goes in one frame.
        //
        // **6KB, because a model had to retype it. Now the size of a message.**
        //
        // The old value was a guess about how long a SendMessage body could be,
        // and its consequences ran through everything: a transcript arrived as
        // dozens of chunks, each one a model emitting ~8KB of base64 as tool
        // input at roughly two minutes a turn, each with its own hash so a
        // mistyped character could be asked for again.
        //
        // The wire is a TLS socket now and PeerProtocol carries a message up to
        // 32MB whole. Chunking at 6KB would cut a transcript into five thousand
        // pieces and reassemble them, to move bytes that fit in one write.
        //
        // Left as a constant rather than deleted outright, and matched to the
        // transport's own ceiling: Split still exists, still splits, and simply
        // never has anything to split — which is what makes the machinery
        // around it safe to remove in the next commit rather than the same one.
        public const int ChunkBytes = PeerProtocol.MaxMessageBytes;

        // The tail a panel opens on, and the page it walks back by.
        //
        // These used to be LocalCliChatSession's numbers — 512KB and 1MB — on
        // the reasoning that the mirror should show what that panel would have
        // shown. That reasoning is right about a local file read and does not
        // survive this wire. Every chunk here is a model emitting ~8KB of base64
        // as tool input, so throughput is one ChunkBytes per model turn, and a
        // turn was measured at close to two minutes on a real relay. A 512KB
        // tail came out as two chunks — a four-minute first paint against a
        // 180-second request timeout, so the window could not arrive before the
        // request that asked for it expired, and the panel showed nothing at
        // all. See CB-46.
        //
        // So they are chosen against what the wire can carry rather than what
        // the panel would like. These are only the *starting* size: the server
        // shrinks a tail further until it genuinely fits one chunk (see
        // RemoteMirrorServer.ReadFor), because how many turns a byte range
        // yields, and how well they compress, varies far too much between
        // transcripts to be settled by a constant.
        //
        // Paging back is what supplies the rest, which is what paging is for.
        public const int InitialBytes = 128 * 1024;
        public const int PageBytes = 128 * 1024;

        // How long a client waits for a fetch that carries a transcript back.
        //
        // **This was 600 seconds and is now 20, and the difference is the whole
        // point of the new transport.** The old number was measured, not
        // chosen: a fetch's reply was a chunk of base64 that a far model emitted
        // token by token, and a single-chunk window off the mini took 7m 15s on
        // 29 Aug — arriving intact and being thrown away, because the request
        // that asked for it had expired eight minutes earlier. Ten minutes was
        // the honest ceiling for a courier that slow.
        //
        // There is no model in the path now. A fetch is a read off a disk and a
        // write to a socket; measured between two real machines, a roster round
        // trip is ten milliseconds. Twenty seconds is not a target, it is a
        // ceiling generous enough for a large transcript on a busy machine and
        // short enough that a broken link says so while somebody is still
        // looking at the panel.
        //
        // **A too-long timeout is not a safe default.** Ten minutes of "no live
        // view" for a link that is simply down is a worse answer than an honest
        // failure in twenty seconds, and it was the single most confusing part
        // of the transport this replaces.
        public const int FetchTimeoutSeconds = 20;

        // Sending keeps a shorter wait still. An INPUT's reply is a bare OK, so
        // a slow one means the far machine is busy rather than that the answer
        // is long — and there is no longer any fallback if it does not come, so
        // saying so promptly is the only kindness available.
        public const int InputTimeoutSeconds = 10;

        // How long a subscription lives without being renewed, and how often a
        // client renews one it still wants. The gap between them is slack for a
        // relay having a slow turn: a watch that lapses costs a re-request, not
        // a wrong panel.
        public const int WatchTtlSeconds = 120;
        public const int WatchRenewSeconds = 90;

        // How many times a failed piece is asked for again before the transfer
        // is called off. Bounded because the failure this covers — a courier
        // that mangles text — is more likely to be systematic than transient,
        // and asking forever would spend a session's quota discovering that.
        public const int ResendAttempts = 2;

        // One frame, parsed. Payload is null unless it both arrived and matched
        // its hash; see PayloadVerified.
        public sealed record MirrorFrame(
            string Type,
            string Id,
            IReadOnlyDictionary<string, string> Fields,
            byte[]? Payload,
            bool PayloadVerified)
        {
            public string? Get(string key) => Fields.TryGetValue(key, out var v) ? v : null;

            public long Num(string key, long fallback = -1) =>
                Fields.TryGetValue(key, out var v) && long.TryParse(v, out var n) ? n : fallback;

            // A free-text field, which travels base64'd for the same reason the
            // payload does — see BuildFrame.
            public string? Text(string key)
            {
                var raw = Get(key);
                return raw is null ? null : Decode(raw);
            }
        }

        // True for anything that claims to be a frame, whether or not it parses.
        //
        // Separate from TryParseFrame and used before it, exactly the way
        // BridgeProtocol.IsInfoReply is: a frame that arrives malformed must
        // still be swallowed rather than shown. The person reading the panel did
        // not ask a question, so they should not see a fumbled answer to one —
        // and a wall of base64 in a chat bubble is the worst version of that.
        public static bool IsFrame(string? body) =>
            body is not null
            && body.TrimStart().StartsWith(Marker, StringComparison.Ordinal);

        // Builds one line.
        //
        // Two encoding rules, both load-bearing:
        //
        //  * **Payload and free text are standard base64.** Not base64url. The
        //    url alphabet's `_` would let a payload spell `msg_id`, which is the
        //    exact string RemoteControlBridge.AskAsync waits for to decide a
        //    send has been receipted — a frame that happened to contain it would
        //    satisfy somebody else's request and derail the relay. Standard
        //    base64 also cannot contain `<` or `>`, so a frame can never close
        //    the `</cross-session-message>` tag it is travelling inside, which
        //    is the other way this could have gone wrong. The cost is `=`
        //    padding, handled by splitting each pair at its *first* `=` only.
        //
        //  * **`;` separates pairs**, and is not in the base64 alphabet, so no
        //    value can introduce one.
        //
        // Everything else — types, ids, numbers, error codes — is a bare token
        // by construction and is asserted to be one rather than trusted.
        public static string BuildFrame(
            string type,
            string id,
            IReadOnlyDictionary<string, string>? fields = null,
            byte[]? payload = null)
        {
            var sb = new StringBuilder(Marker)
                .Append(Version)
                .Append(";t=").Append(type)
                .Append(";id=").Append(id);

            if (fields is not null)
            {
                foreach (var (key, value) in fields)
                {
                    // A field that could break the grammar is a bug here, not
                    // something to sanitise quietly at runtime: the caller
                    // either meant to send free text (and should have encoded
                    // it) or built a malformed token.
                    if (value.Contains(';') || key.Contains(';') || key.Contains('='))
                        throw new ArgumentException($"unencodable field {key}", nameof(fields));

                    sb.Append(';').Append(key).Append('=').Append(value);
                }
            }

            if (payload is not null)
            {
                sb.Append(";p=").Append(Convert.ToBase64String(payload));
                sb.Append(";h=").Append(Hash(payload));
            }

            return sb.ToString();
        }

        // Reads one line, or null if it is not a frame at all.
        //
        // Structural failures return null. A *hash* failure does not: the frame
        // still parses, because the client needs its `seq` to ask for that piece
        // again, but its payload is nulled and PayloadVerified is false. Nulling
        // rather than flagging is deliberate — it means no later code can use
        // unverified bytes by forgetting to check, which is the mistake this
        // whole design exists to make impossible.
        public static MirrorFrame? TryParseFrame(string? body)
        {
            if (!IsFrame(body)) return null;

            var line = body!.Trim();

            // A courier that wrapped the line, or quoted it inside a sentence,
            // leaves everything after the first newline out of the frame.
            var nl = line.IndexOf('\n');
            if (nl >= 0) line = line[..nl].TrimEnd();

            var rest = line[Marker.Length..];

            var parts = rest.Split(';');
            if (parts.Length < 3) return null;
            if (parts[0] != Version) return null;

            var fields = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var part in parts.Skip(1))
            {
                if (part.Length == 0) continue;

                var eq = part.IndexOf('=');
                if (eq <= 0) return null;

                var key = part[..eq];
                var value = part[(eq + 1)..];

                // Later wins rather than first, so a duplicated key is at least
                // deterministic; a frame with one is malformed either way.
                fields[key] = value;
            }

            if (!fields.TryGetValue("t", out var type) || type.Length == 0) return null;
            if (!fields.TryGetValue("id", out var id) || id.Length == 0) return null;

            fields.Remove("t");
            fields.Remove("id");

            byte[]? payload = null;
            var verified = true;

            if (fields.TryGetValue("p", out var encoded))
            {
                byte[] bytes;
                try { bytes = Convert.FromBase64String(encoded); }
                catch { return null; }

                // A payload with no digest beside it is unverifiable, which is
                // the same thing as wrong here.
                verified = fields.TryGetValue("h", out var expected)
                    && string.Equals(expected, Hash(bytes), StringComparison.OrdinalIgnoreCase);

                payload = verified ? bytes : null;
            }

            return new MirrorFrame(type, id, fields, payload, verified);
        }

        // --- putting a payload on the wire in pieces -------------------------

        // Splits a payload into the pieces one frame can carry.
        //
        // Always at least one piece, including for an empty payload: "nothing
        // new" is an answer the client has to be able to receive, and an empty
        // list would be indistinguishable from a transfer that never started.
        public static List<byte[]> Split(byte[] payload, int chunkBytes = ChunkBytes)
        {
            if (chunkBytes <= 0) throw new ArgumentOutOfRangeException(nameof(chunkBytes));

            var pieces = new List<byte[]>();

            for (var at = 0; at < payload.Length; at += chunkBytes)
            {
                var take = Math.Min(chunkBytes, payload.Length - at);
                var piece = new byte[take];
                Array.Copy(payload, at, piece, 0, take);
                pieces.Add(piece);
            }

            if (pieces.Count == 0) pieces.Add(Array.Empty<byte>());

            return pieces;
        }

        public enum AssemblyState
        {
            Incomplete,
            Complete,

            // One piece failed its own hash. The client asks for that seq again
            // rather than abandoning the whole transfer, which for a long
            // transcript is the difference between one re-request and thirty.
            BadChunk,

            // Every piece verified individually and the reassembled whole still
            // does not match. Nothing to re-request usefully, so this is fatal
            // for the transfer.
            Failed
        }

        public readonly record struct AssemblyResult(
            AssemblyState State, byte[]? Payload, int BadSeq, string? Reason);

        // Collects the pieces of one transfer and hands back the payload only
        // when every one of them, and their sum, has been proved.
        //
        // Order-independent because the courier is a model taking turns: pieces
        // can arrive out of order, twice, or interleaved with something else
        // entirely, and none of that should matter to whether the answer is
        // right.
        public sealed class MirrorAssembly
        {
            private readonly Dictionary<int, byte[]> _pieces = new();
            private int _of = -1;
            private string? _whole;

            public AssemblyResult Offer(MirrorFrame frame)
            {
                var seq = (int)frame.Num("seq", -1);
                var of = (int)frame.Num("of", -1);

                if (seq < 0 || of <= 0 || seq >= of)
                    return new AssemblyResult(AssemblyState.Failed, null, -1, "malformed chunk header");

                if (_of < 0) _of = of;
                else if (_of != of)
                    return new AssemblyResult(AssemblyState.Failed, null, -1, "chunk count changed mid-transfer");

                // The last piece carries the digest of the whole. Kept whenever
                // it arrives rather than only at the end, since it may well
                // arrive first.
                if (frame.Get("H") is { Length: > 0 } whole) _whole = whole;

                if (!frame.PayloadVerified)
                    return new AssemblyResult(AssemblyState.BadChunk, null, seq, "chunk failed its hash");

                _pieces[seq] = frame.Payload ?? Array.Empty<byte>();

                if (_pieces.Count < _of)
                    return new AssemblyResult(AssemblyState.Incomplete, null, -1, null);

                var total = 0;
                for (var i = 0; i < _of; i++) total += _pieces[i].Length;

                var payload = new byte[total];
                var at = 0;
                for (var i = 0; i < _of; i++)
                {
                    _pieces[i].CopyTo(payload, at);
                    at += _pieces[i].Length;
                }

                // Belt and braces over the per-piece hashes: those prove each
                // piece is what it said it was, this proves they are the right
                // pieces in the right order. A courier that delivered two
                // transfers' pieces under one id would pass the first check and
                // fail this one.
                if (_whole is null)
                    return new AssemblyResult(AssemblyState.Failed, null, -1, "no whole-payload hash");

                if (!string.Equals(_whole, Hash(payload), StringComparison.OrdinalIgnoreCase))
                    return new AssemblyResult(AssemblyState.Failed, null, -1, "reassembled payload failed its hash");

                return new AssemblyResult(AssemblyState.Complete, payload, -1, null);
            }
        }

        // --- what a far Buddy says it has ------------------------------------

        // One session the far Buddy is offering, and what can be done with it.
        //
        // `HasTranscript` and `HasPane` are answered rather than assumed because
        // they differ per session on one machine: a session Buddy can read but
        // not type into (no tmux pane) is common, and a panel that offered a
        // send it cannot deliver would be lying in the one place it matters.
        public sealed record MirrorRosterEntry(
            [property: JsonPropertyName("name")] string Name,
            [property: JsonPropertyName("cli")] string Cli,
            [property: JsonPropertyName("transcript")] bool HasTranscript,
            [property: JsonPropertyName("pane")] bool HasPane,
            [property: JsonPropertyName("color")] string? Color = null,
            [property: JsonPropertyName("commands")] IReadOnlyList<string>? Commands = null,

            // What that session is doing right now, in its own status file's
            // words — "idle", "working".
            //
            // **Added because the orb needs it and the relay used to supply it
            // from somewhere else.** Over the relay, status came from
            // `claude agents --json` parsed by the relay's model, and the roster
            // never had to carry it. A direct link has no such second channel,
            // so the one answer the far machine sends has to say everything an
            // orb needs — otherwise a session over the link can be found and
            // read and typed into, and still sits grey while it works.
            //
            // Optional, and absent reads as idle: an older Buddy on the far end
            // answers without it, and gets an orb that is right about everything
            // except its pulse rather than no orb at all.
            [property: JsonPropertyName("status")] string? Status = null,

            // Whether the far machine can hand this session text over its own
            // messaging socket when there is no pane to type into — CB-105's
            // second delivery path, for a background or agent-mode job that
            // never has a terminal at all.
            //
            // **Trailing and optional for the same reason Status is.** An
            // older Buddy's JSON never mentions delivery, and that has to
            // read as "unknown" rather than fail to parse or read as false —
            // the two are different claims, and a session offered as
            // undeliverable when the far machine simply never answered would
            // be a live-view session that quietly cannot be reached.
            [property: JsonPropertyName("deliver")] bool? CanDeliver = null);

        public const string CliClaudeCode = "claude";
        public const string CliCodex = "codex";
        public const string CliGrok = "grok";

        private static readonly JsonSerializerOptions RosterJson = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static byte[] EncodeRoster(IReadOnlyList<MirrorRosterEntry> entries) =>
            Gzip(JsonSerializer.SerializeToUtf8Bytes(entries, RosterJson));

        // Null rather than an exception for anything unreadable, which is this
        // repo's rule for a format it does not own: a roster that will not parse
        // means "no live view", and that is a state the panel already has
        // wording for.
        public static IReadOnlyList<MirrorRosterEntry>? DecodeRoster(byte[] payload)
        {
            try
            {
                var json = Gunzip(payload);
                var entries = JsonSerializer.Deserialize<List<MirrorRosterEntry>>(json, RosterJson);
                return entries?.Where(e => !string.IsNullOrWhiteSpace(e.Name)).ToList();
            }
            catch
            {
                return null;
            }
        }

        // --- transcript rows --------------------------------------------------

        // The rows worth sending, which is exactly the rows a local panel would
        // have shown.
        //
        // Both filters are the parsers' own, not a second opinion written here.
        // That is what makes the mirror equal to a local panel rather than
        // similar to one: anything Map would skip is skipped identically, so the
        // two cannot drift apart as those parsers learn new row types.
        //
        // It also does most of the work of making this affordable. Almost all of
        // a transcript's bytes are tool results and file-history snapshots —
        // LocalCliChatSession's own measurement, six real transcripts — and none
        // of them can produce a turn. Sending them would be paying a model to
        // paste megabytes nobody will ever see.
        public static List<string> SelectInterestingRows(IEnumerable<string> lines, string cli)
        {
            var grok = string.Equals(cli, CliGrok, StringComparison.OrdinalIgnoreCase);
            var codex = string.Equals(cli, CliCodex, StringComparison.OrdinalIgnoreCase);
            var kept = new List<string>();

            foreach (var line in lines)
            {
                if (line.Length == 0 || line[0] != '{') continue;
                var interesting = grok ? GrokTranscript.IsInteresting(line)
                    : codex ? CodexTranscript.IsInteresting(line)
                    : ChatTranscript.IsInteresting(line);
                if (!interesting) continue;

                kept.Add(line);
            }

            return kept;
        }

        public static string CliFor(SessionSource source) => source switch
        {
            SessionSource.Codex => CliCodex,
            SessionSource.Grok => CliGrok,
            _ => CliClaudeCode
        };

        // Rows in, one blob out. Newline-joined because that is how they arrived
        // and how the far side will split them, and gzipped because a
        // conversation is text and text is where gzip earns its keep — measured
        // at roughly five to ten times on real transcript rows, which is the
        // difference between one frame and ten.
        // --- what actually crosses the wire ----------------------------------

        // One turn, as small as it can be and still be the turn.
        //
        // **Turns rather than transcript rows, and the difference is the whole
        // difference between usable and not.** The first version shipped the raw
        // JSONL and let the far side parse it, which is a lovely property —
        // identical bytes, identical parser, nothing to argue about — and it was
        // measured costing 13 to 30 frames to open one panel. At roughly eight
        // thousand output tokens and a minute and a half per frame, that is
        // twenty minutes and a hundred thousand tokens to look at a
        // conversation. Measured, on this machine, against eight real
        // transcripts.
        //
        // The reason is that an assistant row is enormous and almost none of it
        // is shown: tool_use blocks, thinking, tool results. ChatTranscript.Map
        // renders a tool call as one summary line and drops thinking entirely,
        // so the row being paid for is ten to thirty times the size of the turn
        // it produces. Sending the same eight transcripts as turns: **one or two
        // frames**, worst case two.
        //
        // What is given up is smaller than it looks. The far Buddy still reads
        // the file off its own disk and still parses it with the *same*
        // ChatTranscript this app uses locally — the parse simply happens on the
        // side that has the file, which is the side that would have to be
        // trusted anyway. Nothing composed by a model is in the path, and the
        // hash still proves the turns arrive exactly as that Buddy produced
        // them. What is displayed is still, byte for byte, what a local panel
        // would display.
        public sealed record MirrorTurn(
            [property: JsonPropertyName("r")] string Role,
            [property: JsonPropertyName("t")] string Text,
            [property: JsonPropertyName("u")] string? Uuid = null,
            [property: JsonPropertyName("a")] long At = 0);

        private static readonly JsonSerializerOptions TurnJson = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static byte[] EncodeTurns(IReadOnlyList<MirrorTurn> turns) =>
            Gzip(JsonSerializer.SerializeToUtf8Bytes(turns, TurnJson));

        public static IReadOnlyList<MirrorTurn>? DecodeTurns(byte[] payload)
        {
            try
            {
                return JsonSerializer.Deserialize<List<MirrorTurn>>(Gunzip(payload), TurnJson);
            }
            catch
            {
                return null;
            }
        }

        // The mapped rows of a transcript, as turns ready to send.
        //
        // Goes through CliChatFormat so it is the same Map a local panel uses —
        // that is what keeps the two from drifting.
        public static List<MirrorTurn> TurnsFrom(IEnumerable<string> lines, string cli)
        {
            var source = string.Equals(cli, CliGrok, StringComparison.OrdinalIgnoreCase)
                ? SessionSource.Grok
                : string.Equals(cli, CliCodex, StringComparison.OrdinalIgnoreCase)
                    ? SessionSource.Codex
                    : SessionSource.ClaudeCode;

            var mapped = CliChatFormat.For(source).Map(SelectInterestingRows(lines, cli));

            return mapped
                .Select(r => new MirrorTurn(
                    r.Turn.Role switch
                    {
                        ChatRole.User => "u",
                        ChatRole.System => "s",
                        _ => "a"
                    },
                    r.Turn.Text,
                    r.Uuid,
                    r.Turn.At.ToUnixTimeSeconds()))
                .ToList();
        }

        public static ChatRole RoleOf(string role) => role switch
        {
            "u" => ChatRole.User,
            "s" => ChatRole.System,
            _ => ChatRole.Assistant
        };

        public static byte[] PackRows(IReadOnlyList<string> rows) =>
            Gzip(Encoding.UTF8.GetBytes(string.Join("\n", rows)));

        public static List<string>? UnpackRows(byte[] payload)
        {
            try
            {
                var text = Encoding.UTF8.GetString(Gunzip(payload));
                return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            catch
            {
                return null;
            }
        }

        // --- plumbing ---------------------------------------------------------

        public static string Hash(byte[] bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        public static string Encode(string text) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

        public static string? Decode(string encoded)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(encoded)); }
            catch { return null; }
        }

        public static byte[] Gzip(byte[] raw)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(raw, 0, raw.Length);
            }

            return output.ToArray();
        }

        public static byte[] Gunzip(byte[] packed)
        {
            using var input = new MemoryStream(packed);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }

        // Short, and short on purpose: it is only ever correlating requests
        // within one relay's conversation, where a handful are in flight at
        // once. Hex so it can never grow a character the grammar cares about.
        public static string NewId() =>
            Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
    }
}
