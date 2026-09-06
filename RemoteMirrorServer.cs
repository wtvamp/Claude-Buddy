using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace ClaudeBuddy
{
    // This machine answering another machine's Buddy about the sessions running
    // here: what they are, what their transcripts actually say, and — when asked
    // — typing a line into one of them.
    //
    // The half that makes a mirror possible at all. A relay model can only ever
    // tell you what it *thinks* a session said, because that is all it has; this
    // opens the file and reads the bytes. Everything it sends is hashed on the
    // way out (MirrorProtocol), so the courier in the middle can lose or mangle
    // a frame but cannot quietly change one.
    //
    // Every seam that touches the operating system is a delegate rather than a
    // direct call — sending, listing sessions, reading the agent registry,
    // checking the reply setting, and typing. Not for elegance: it is what lets
    // the whole request/response contract be driven in a test with no tmux, no
    // relay, no second machine and no model, which is the only way any of this
    // could be covered at all. The defaults wire up the real ones.
    //
    // Two powers are gated here rather than trusted to the far side, because the
    // far side is exactly what cannot be trusted to gate them:
    //
    //  * **Typing** honours this machine's own "allow replying to sessions"
    //    setting, the same one a local panel obeys. A person who has turned
    //    replying off has said something about this machine, and a request
    //    arriving over a wire does not change it.
    //  * **Requests are only served to a Buddy relay**, matched on the name
    //    prefix RemoteControlBridge builds. It is a weak check on its own — the
    //    account is shared, so anything on it could wear the name — and it is
    //    named as such in the PR rather than presented as a boundary.
    internal sealed class RemoteMirrorServer
    {
        // Everything this needs from the world outside itself.
        internal sealed record Seams(
            Func<string, string, Task<bool>> SendFrame,
            Func<IReadOnlyList<(string SessionId, SessionStatus Status)>> LocalSessions,
            Func<IReadOnlyList<AgentRoster.Entry>> Agents,
            Func<SessionSource, bool> ReplyEnabled,
            Func<SessionStatus, bool> CanType,
            Func<SessionStatus, string, Task<bool>> TypeInto,

            // CB-105's second delivery path: whether this session's far end
            // can be handed text over its own messaging socket, for a
            // background or agent-mode job that has no pane at all. Null
            // (the default) means this server does not know how to ask, and
            // that has to behave exactly like "no": an older harness, or a
            // test built before this feature existed, gets the pre-CB-105
            // behaviour of ErrNoPane unchanged — see InputAsync's own note
            // and the regression test that pins it,
            // MirrorRoundTripTests.TypingIsRefusedWhenThereIsNoPaneToTypeInto.
            Func<SessionStatus, bool>? CanDeliver = null,

            // Actually attempts that delivery, once CanDeliver has said yes.
            // Kept as a separate delegate rather than folded into one call,
            // the same split CanType/TypeInto already make: the roster asks
            // the first question many times a minute and must never touch a
            // socket to answer it, while this one is the real send and runs
            // once, against a real INPUT.
            Func<SessionStatus, string, Task<DeliveryReceipt>>? Deliver = null,

            // Whether this peer is allowed to ask anything at all.
            //
            // **A seam because the answer depends on the transport, and the
            // hard-coded version was a second copy of a string.** Over the relay
            // it meant "the name starts with the prefix RemoteControlBridge
            // builds" — a guard rather than a boundary, since the account is
            // shared and anything on it could wear that name. Over a direct link
            // it means something much stronger: this peer completed a TLS
            // handshake presenting a certificate we pinned when a person typed a
            // pairing code.
            //
            // Null keeps the old prefix test, so nothing that has not been moved
            // across yet changes behaviour.
            Func<string, bool>? PeerAllowed = null);

        private readonly string _account;
        private readonly Seams _seams;
        private readonly object _gate = new();

        // One subscription: someone is watching a session and wants what's new.
        private sealed class Subscription
        {
            public required string Watcher;
            public required string Name;
            public required string Id;
            public required string Cli;
            public long Offset;
            public long Gen;
            public DateTime Expires;
        }

        private readonly Dictionary<string, Subscription> _watches = new(StringComparer.Ordinal);

        // The pieces of recent transfers, so a frame that failed its hash on the
        // way over can be sent again without rebuilding — and, more to the
        // point, without re-reading a file that has moved on since. A resend
        // must be the *same bytes*, or the whole-payload hash the client is
        // holding could never match.
        private readonly Dictionary<string, Transfer> _transfers = new(StringComparer.Ordinal);
        private readonly Queue<string> _transferOrder = new();
        private const int KeepTransfers = 8;

        private sealed class Transfer
        {
            public required string Watcher;
            public required List<byte[]> Pieces;
            public required Dictionary<string, string> Fields;
            public required string WholeHash;
        }

        public RemoteMirrorServer(string account, Seams seams)
        {
            _account = account;
            _seams = seams;
        }

        // Injectable only so a subscription lapsing can be tested without
        // sleeping through its TTL. Never replaced in the app — same pattern,
        // and same reason, as RemoteControlSessions.Now.
        internal Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

        // The real wiring. Split out so the constructor above stays free of it
        // and a test never has to opt out of anything.
        public static Seams RealSeams(
            string profileDir,
            Func<string, string, Task<bool>> sendFrame,
            Func<IReadOnlyList<(string SessionId, SessionStatus Status)>> localSessions) =>
            LiveSeams(profileDir, sendFrame, localSessions);

        // The same, for a transport that is not account-scoped.
        //
        // **A socket has no account, and reading one account's roster made that
        // comment untrue where it mattered.** A relay signs into one Anthropic
        // account and can only ever see that account's sessions, so
        // RealSeams taking one profile dir is not a limitation there — it is the
        // shape of the thing. A direct link has no such excuse: the machine has
        // whatever sessions it has, under whatever config dirs the user set up,
        // and answering for only the first is answering the wrong question.
        //
        // Caught on real hardware and nowhere else. The mini's sessions live
        // under `.claude-board`; the peer server read `.claude`; the two
        // machines connected in 22 milliseconds and exchanged a roster of
        // exactly zero entries, with nothing failing anywhere.
        public static Seams AllAccountSeams(
            IReadOnlyList<string> profileDirs,
            Func<string, string, Task<bool>> sendFrame,
            Func<IReadOnlyList<(string SessionId, SessionStatus Status)>> localSessions)
        {
            var one = LiveSeams(
                profileDirs.Count > 0 ? profileDirs[0] : ".claude", sendFrame, localSessions);

            return one with { Agents = () => AgentsAcross(profileDirs) };
        }

        // Every account's roster, as one list.
        //
        // Excluded from coverage: each call launches `claude agents`. What is
        // worth asserting is that duplicates collapse, which MergeRosters does
        // and is pure.
        [ExcludeFromCodeCoverage]
        private static IReadOnlyList<AgentRoster.Entry> AgentsAcross(
            IReadOnlyList<string> profileDirs)
        {
            var all = new List<IReadOnlyList<AgentRoster.Entry>>();

            foreach (var dir in profileDirs)
            {
                try
                {
                    all.Add(AgentRoster.Read(dir));
                }
                catch (Exception ex)
                {
                    // One account that cannot be read must not take the others
                    // with it — a profile dir removed from disk but left in
                    // settings is the ordinary way this happens.
                    MirrorLog.Say("roster-read-failed", $"dir={dir} {ex.GetType().Name}");
                }
            }

            return MergeRosters(all);
        }

        // Several accounts' rosters, deduplicated by session id.
        //
        // By session id rather than by name, because two accounts genuinely can
        // hold sessions with the same name — the same person naming things the
        // same way twice is the normal case — while a session id is unique. The
        // first account to claim an id wins, which makes the order of
        // profileDirs the tie-break and keeps the answer stable across ticks.
        internal static IReadOnlyList<AgentRoster.Entry> MergeRosters(
            IReadOnlyList<IReadOnlyList<AgentRoster.Entry>> rosters)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var merged = new List<AgentRoster.Entry>();

            foreach (var roster in rosters)
            foreach (var entry in roster)
            {
                if (!seen.Add(entry.SessionId)) continue;

                merged.Add(entry);
            }

            return merged;
        }

        // Excluded from coverage: this is the wiring that makes the seams real,
        // and every delegate in it is one a test must not run — launching
        // `claude agents`, asking tmux whether a pane can be typed into, and
        // typing into it. The tests build their own Seams instead, which is what
        // the record exists for; everything behind these four delegates is
        // covered on its own terms.
        [ExcludeFromCodeCoverage]
        private static Seams LiveSeams(
            string profileDir,
            Func<string, string, Task<bool>> sendFrame,
            Func<IReadOnlyList<(string SessionId, SessionStatus Status)>> localSessions)
        {
            // Built once per server rather than per call — SessionMessenger
            // itself is stateless, but constructing it here rather than
            // inside CanDeliver/Deliver keeps this the same shape as every
            // other seam above: LiveSeams runs once, at startup, and what it
            // wires up is what runs on every tick after.
            var messenger = new SessionMessenger(SessionMessenger.Live(ClaudeConfigRoots.All()));

            // MachineNames.Tag(), not .Mine(): the other CB-105 slice
            // (LocalCliChatSession.DeliverViaMessengerAsync) already settled
            // on Tag() for this same "from" identity, and there is no reason
            // for the two delivery paths to attribute a message differently
            // depending on which machine's Buddy happened to make the call.
            var fromName = SessionMessenger.FromName(MachineNames.Tag());

            return new(
                sendFrame,
                localSessions,
                () => AgentRoster.Read(profileDir),
                source => CliChatFormat.For(source).ReplyEnabled(),
                status => TerminalFocuser.CanSendQuietly(status),
                (status, text) => TerminalFocuser.SendTextAndSubmit(status, text),
                CanDeliver: status => RegistryEntryFor(status) is { } entry && SessionRegistry.Speaks(entry),
                Deliver: async (status, text) =>
                {
                    if (RegistryEntryFor(status) is not { } entry)
                        return new DeliveryReceipt(DeliveryResult.NoRegistryEntry, null);

                    return await messenger.DeliverAsync(entry.SessionId, fromName, text, CancellationToken.None)
                        .ConfigureAwait(false);
                });
        }

        // The one link back from a SessionStatus to a SessionRegistry.Entry.
        //
        // **SessionStatus carries the pid the hook wrote, never Claude
        // Code's own session id.** SessionMessenger.DeliverAsync needs that
        // id — it is how SessionRegistry.Find matches a registration — and
        // nothing upstream of here threads it through: Seams.CanDeliver and
        // Seams.Deliver both take a bare SessionStatus, the same shape
        // CanType and TypeInto already take, so this is the one place that
        // has to bridge the two. Matched on pid, which is the one thing a
        // registration and a status file agree on without either naming the
        // other, and confirmed alive through the same PidAlive check
        // SessionMessenger.Live uses internally, so a registration left
        // behind by a session that has since exited (its own file is not
        // always cleaned up on a crash) is not mistaken for a live one.
        //
        // Excluded from coverage along with the rest of LiveSeams: it scans
        // a real registry directory and checks a real process table.
        [ExcludeFromCodeCoverage]
        private static SessionRegistry.Entry? RegistryEntryFor(SessionStatus status)
        {
            if (status.SessionPid <= 0) return null;

            foreach (var entry in SessionRegistry.Scan(ClaudeConfigRoots.All()))
            {
                if (entry.Pid == status.SessionPid && ProcessLiveness.IsRunning(entry.Pid)) return entry;
            }

            return null;
        }

        // --- serving ---------------------------------------------------------

        public async Task HandleAsync(string fromPeer, MirrorProtocol.MirrorFrame frame)
        {
            // Only another Buddy's relay is answered. See the class note: this
            // is a guard rather than a boundary, and it is cheap enough to keep
            // for the one thing it does catch — a person on the same account
            // typing something that happens to look like a frame.
            if (!MayAsk(fromPeer))
            {
                MirrorLog.Say("serve-refused", $"t={frame.Type} from={fromPeer} not-a-relay-name");
                return;
            }

            MirrorLog.Say("serve", $"t={frame.Type} id={frame.Id} from={fromPeer}");

            switch (frame.Type)
            {
                case MirrorProtocol.Hello:
                    await HelloAsync(fromPeer, frame).ConfigureAwait(false);
                    break;

                case MirrorProtocol.Fetch:
                    await FetchAsync(fromPeer, frame).ConfigureAwait(false);
                    break;

                case MirrorProtocol.Watch:
                    await WatchAsync(fromPeer, frame).ConfigureAwait(false);
                    break;

                case MirrorProtocol.Unwatch:
                    lock (_gate) _watches.Remove(frame.Id);
                    break;

                case MirrorProtocol.Input:
                    await InputAsync(fromPeer, frame).ConfigureAwait(false);
                    break;

                case MirrorProtocol.Resend:
                    await ResendAsync(fromPeer, frame).ConfigureAwait(false);
                    break;
            }
        }

        // Who is allowed to ask.
        //
        // **The default is now "nobody", and that is the right way round.** It
        // used to fall back to a name test — anything called `claude-buddy-rc-…`
        // was another Buddy's relay — which was a guess dressed as a check: a
        // name is not a credential, and anyone on the account could pick one.
        // The transport answers this properly now, because a peer has completed
        // a TLS handshake with a certificate somebody pinned by typing a code.
        //
        // A server built with no PeerAllowed serves nothing, which is what a
        // half-wired server should do.
        private bool MayAsk(string fromPeer) =>
            _seams.PeerAllowed?.Invoke(fromPeer) ?? false;

        // What of this machine the asker can see.
        //
        // Answers only about the names it asked about, which is the reason HELLO
        // carries a payload at all. Listing everything running here would tell
        // the other machine about sessions its own peer list cannot see — a
        // session with Remote Control switched off is deliberately invisible
        // over there, and a roster is no place to undo that.
        private async Task HelloAsync(string fromPeer, MirrorProtocol.MirrorFrame frame)
        {
            var asked = frame.Payload is null
                ? null
                : MirrorProtocol.UnpackRows(frame.Payload);

            var agents = _seams.Agents();
            var sessions = _seams.LocalSessions();

            // Asking about nothing in particular means "what have you got?"
            //
            // **This used to be an error, and the reasoning behind that was
            // transport-specific.** Over the relay a peer already had its own
            // list of sessions from ListAgents, so a hello naming none of them
            // was a malformed question — and answering it would have told the
            // far machine about sessions its own peer list deliberately could
            // not see, which is a visibility rule this had no business undoing.
            //
            // A direct link has neither property. There is no prior list, so
            // without this there is nothing to put an orb on and no way to
            // learn a name to ask about — the question would have no first
            // answer. And the peer is not any process that happens to share an
            // account: it completed a TLS handshake presenting a certificate
            // pinned when a person typed a pairing code. Telling a machine the
            // user deliberately paired what sessions are here is the feature.
            //
            // Still only what this machine would show anyway: the same
            // IsLocalCli filter below applies either way, so nothing becomes
            // visible that was not already a session on this disk.
            var everything = asked is null || asked.Count == 0;

            var wanted = everything
                ? agents.Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : asked!;

            var entries = new List<MirrorProtocol.MirrorRosterEntry>();

            MirrorLog.Say("hello",
                $"asked={(everything ? "all" : wanted.Count.ToString())} "
                + $"agents={agents.Count} sessions={sessions.Count}");

            foreach (var (name, resolved) in Offer(wanted, agents, sessions))
            {
                if (resolved is null)
                {
                    // Says which of the two halves failed to line up. An agent
                    // the registry knows and Buddy has no status file for, and a
                    // status file Buddy has for a session the registry does not
                    // list, are different problems — and both arrive here as an
                    // empty roster and a panel with nothing in it.
                    // Keyed per name and said once: a peer asks every ten
                    // seconds, so a session the registry knows and Buddy has
                    // never seen a status file for would otherwise write a line
                    // per name per tick, forever, for a condition that is not
                    // changing.
                    MirrorLog.SayOnce($"hello-unresolved:{name}", $"name={name}");
                    continue;
                }

                var status = resolved.Value.Status;

                // A local CLI only. An OpenClaw conversation has no transcript
                // on this disk and no pane to type into, so offering it here
                // would be offering something that cannot be delivered.
                if (!status.IsLocalCli) continue;

                var hasTranscript = !string.IsNullOrEmpty(status.TranscriptPath)
                                    && File.Exists(status.TranscriptPath);

                // A conversation, not just a process.
                //
                // The registry lists every session whose window is still open,
                // including ones abandoned at a prompt days ago — and drawing
                // those puts an orb beside a live session with the same name
                // and no way to tell them apart. SessionLiveness has the full
                // account of why the obvious checks all say "alive" for a
                // session nobody has spoken to since Saturday.
                //
                // Said once per name: a peer asks every ten seconds, and a
                // session that has been abandoned stays abandoned.
                if (hasTranscript && !LivelyEnough(status))
                {
                    MirrorLog.SayOnce($"hello-abandoned:{name}", $"name={name}");
                    continue;
                }

                entries.Add(new MirrorProtocol.MirrorRosterEntry(
                    name,
                    MirrorProtocol.CliFor(status.Source),
                    hasTranscript,
                    _seams.CanType(status),
                    string.IsNullOrWhiteSpace(status.Color) ? null : status.Color,
                    Commands(status),
                    status.State,
                    _seams.CanDeliver?.Invoke(status)));
            }

            await SendTransferAsync(
                fromPeer, frame.Id, MirrorProtocol.EncodeRoster(entries),
                new Dictionary<string, string>(), sub: null)
                .ConfigureAwait(false);
        }

        // The commands that session can actually run, read off this machine's
        // disk rather than asked of a model.
        //
        // This is the honest version of a question Buddy has been answering
        // badly. It used to ask the far session to *list* its commands, which
        // meant trusting a model to enumerate and punctuate — and it could only
        // ever return the custom ones, because a peer message never reaches a
        // command handler and built-ins genuinely could not run. Over a mirror
        // the input is typed into the session's own input line, so every
        // built-in works exactly as it does locally; and the catalogue reads the
        // real ~/.claude on the machine the commands live on.
        private static IReadOnlyList<string> Commands(SessionStatus status)
        {
            return SafeCommandNames(status);
        }

        // Excluded from coverage: exists to be the try/catch. SlashCommandCatalog
        // already swallows the IO it does — a directory that vanished mid-scan is
        // its ordinary case rather than an exception — so nothing here has been
        // observed to throw.
        //
        // Kept because this runs while answering another machine, on a background
        // task, from a cwd belonging to a session this process does not own: an
        // exception would take the roster answer down and leave the far panel
        // with no live view and no explanation, which is a much worse outcome
        // than a session offering no commands.
        [ExcludeFromCodeCoverage]
        private static IReadOnlyList<string> SafeCommandNames(SessionStatus status)
        {
            try
            {
                return SlashCommandCatalog.For(status.Source, status.Cwd)
                    .Select(c => c.Name)
                    .Take(120)
                    .ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        // How much of a transcript to read looking for the last turn.
        //
        // Larger than the window used to *render* a conversation, because this
        // is asking a different question: a session that has been sitting at a
        // prompt accumulates bookkeeping rows — `bridge-session`,
        // `queue-operation`, `mode` — after its final turn, and the answer is
        // wrong if the last turn has been pushed out of the window by them.
        // A megabyte reaches past several hundred such rows, and the cost is a
        // bounded read on a path that already scans a commands directory per
        // entry.
        internal const int LivelinessTailBytes = 1024 * 1024;

        // Excluded from coverage: this is the disk. What it decides is
        // SessionLiveness, which is pure and covered from both sides of the
        // boundary; this reads a tail and asks.
        //
        // Through `Now()` rather than `DateTime.UtcNow` for the reason that
        // property already exists: a rule about how long ago something happened
        // is untestable against a wall clock. It also keeps the fixtures
        // honest — MirrorRoundTripTests pins every row at a fixed instant
        // deliberately (its own comment says why), and a roster that read the
        // real clock would quietly drop every one of those sessions and take
        // the whole suite with it.
        [ExcludeFromCodeCoverage]
        private bool LivelyEnough(SessionStatus status)
        {
            try
            {
                var lines = TranscriptReader.TailLines(
                    status.TranscriptPath!, LivelinessTailBytes);

                return SessionLiveness.WorthShowing(
                    status.State,
                    SessionLiveness.LastTurnAt(lines),
                    Now());
            }
            catch
            {
                // A transcript that cannot be read is shown rather than
                // hidden — see WorthShowing on why an unreadable file must not
                // look like an abandoned session.
                return true;
            }
        }

        // A peer name, resolved to a session on this machine — or nothing.
        //
        // Nothing is a perfectly good answer and the caller turns it into "no
        // live view". See AgentRoster.Resolve for why an ambiguous name refuses
        // rather than picks.
        private static (string SessionId, SessionStatus Status)? Resolve(
            string name,
            IReadOnlyList<AgentRoster.Entry> agents,
            IReadOnlyList<(string SessionId, SessionStatus Status)> sessions) =>
            Pick(name, agents, sessions);

        // Every session worth offering, and what to call each one.
        //
        // **Two live sessions can share a name, and refusing both was worse
        // than either answer.** One machine with the same session name under
        // two Claude accounts is ordinary — `.claude` and `.claude-board`, one
        // person, two logins — and Pick declines an ambiguous name on the sound
        // reasoning that typing into the wrong terminal is worse than typing
        // into none. For *typing* that is right. For a roster it is not: the
        // far user gets no live view of either session, and nothing anywhere
        // says why.
        //
        // Measured on the mini, which had exactly this: two sessions both called
        // `job-hunter-mac-mini`, both alive, and every roster it served came
        // back empty.
        //
        // So a shared name is disambiguated rather than refused, and only when
        // it is actually shared — a name belonging to one session is untouched,
        // which keeps the ordinary case free of noise and keeps an orb's
        // identity stable for as long as it is unambiguous.
        //
        // Pure, and returning the *name to publish* beside the session, because
        // the two are no longer the same thing.
        internal static List<(string Name, (string SessionId, SessionStatus Status)? Resolved)> Offer(
            IReadOnlyList<string> wanted,
            IReadOnlyList<AgentRoster.Entry> agents,
            IReadOnlyList<(string SessionId, SessionStatus Status)> sessions)
        {
            var offered = new List<(string, (string, SessionStatus)?)>();

            foreach (var name in wanted.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var candidates = Candidates(name, agents, sessions);

                if (candidates.Count == 0)
                {
                    offered.Add((name, null));
                    continue;
                }

                if (candidates.Count == 1)
                {
                    offered.Add((name, candidates[0]));
                    continue;
                }

                foreach (var candidate in candidates)
                    offered.Add((Qualified(name, candidate.SessionId), candidate));
            }

            return offered;
        }

        // A name with enough of the session id to tell two apart.
        //
        // Six characters, which is what git shows and for the same reason: long
        // enough to be unique in practice, short enough to read on an orb. The
        // separator is one a session name cannot contain, so splitting it back
        // apart is unambiguous.
        internal const char QualifierMark = '#';

        internal static string Qualified(string name, string sessionId) =>
            sessionId.Length >= 6 ? name + QualifierMark + sessionId[..6] : name;

        // The sessions a name could mean, in the order the roster lists them.
        internal static List<(string SessionId, SessionStatus Status)> Candidates(
            string name,
            IReadOnlyList<AgentRoster.Entry> agents,
            IReadOnlyList<(string SessionId, SessionStatus Status)> sessions)
        {
            var bare = Unqualified(name, out var wantedId);

            var named = agents
                .Where(a => a.Name.Equals(bare, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (named.Count == 0) return new List<(string, SessionStatus)>();

            var found = new List<(string SessionId, SessionStatus Status)>();

            foreach (var session in sessions)
            {
                var matches = named.Any(a =>
                    string.Equals(a.SessionId, session.SessionId, StringComparison.OrdinalIgnoreCase)
                    || (a.Pid > 0 && a.Pid == session.Status.SessionPid));

                if (!matches) continue;

                // A qualified name means exactly one of them.
                if (wantedId is not null
                    && !session.SessionId.StartsWith(wantedId, StringComparison.OrdinalIgnoreCase))
                    continue;

                found.Add(session);
            }

            return found;
        }

        // Splits a published name back into the name the far machine knows and
        // the session it was pointing at, if it carried one.
        internal static string Unqualified(string name, out string? sessionIdPrefix)
        {
            var mark = name.LastIndexOf(QualifierMark);

            if (mark <= 0 || mark == name.Length - 1)
            {
                sessionIdPrefix = null;
                return name;
            }

            sessionIdPrefix = name[(mark + 1)..];
            return name[..mark];
        }

        // Which local session a name refers to, across every account.
        //
        // **This used to defer to AgentRoster.Resolve, which refuses a name two
        // entries share — and that refusal is right for one account and wrong
        // the moment rosters from several are merged.** Within one account, two
        // sessions called the same thing genuinely are ambiguous and guessing
        // between them would type into the wrong terminal. Across accounts they
        // are usually the *same person's* two logins on one machine, and one of
        // them is the session Buddy actually holds a status file for. Refusing
        // there throws away the only answer there was.
        //
        // Measured, not imagined: the mini has `job-hunter-mac-mini` under both
        // `.claude` and `.claude-board`, with different session ids. Every
        // roster it sent came back empty, and the machine that asked showed no
        // orbs at all, with nothing in either log saying why until this ticket
        // added the line that named the dropped session.
        //
        // Still refuses when it is genuinely ambiguous — two *different* live
        // sessions both answering to one name is the case the original rule was
        // protecting, and it is still protected. What changed is that "one
        // candidate, one match" is now an answer rather than a tie.
        //
        // Pure, so both arms are a test rather than two machines and a log.
        internal static (string SessionId, SessionStatus Status)? Pick(
            string name,
            IReadOnlyList<AgentRoster.Entry> agents,
            IReadOnlyList<(string SessionId, SessionStatus Status)> sessions)
        {
            // Shares its candidate list with the roster, so a name the roster
            // published is a name this can resolve. That was the missing half:
            // a qualified name reaching a fetch would otherwise match no agent
            // at all and read as a session that had vanished.
            var matched = Candidates(name, agents, sessions);

            return matched.Count == 1 ? matched[0] : null;
        }

        // --- reading a transcript --------------------------------------------

        private async Task FetchAsync(string fromPeer, MirrorProtocol.MirrorFrame frame)
        {
            var name = frame.Text("n");
            if (name is null)
            {
                await ErrAsync(fromPeer, frame.Id, MirrorProtocol.ErrNoSession, "no name").ConfigureAwait(false);
                return;
            }

            var resolved = Resolve(name, _seams.Agents(), _seams.LocalSessions());
            if (resolved is null)
            {
                await ErrAsync(fromPeer, frame.Id, MirrorProtocol.ErrNoSession, name).ConfigureAwait(false);
                return;
            }

            var status = resolved.Value.Status;
            if (string.IsNullOrEmpty(status.TranscriptPath))
            {
                await ErrAsync(fromPeer, frame.Id, MirrorProtocol.ErrNoTranscript, name).ConfigureAwait(false);
                return;
            }

            var cli = MirrorProtocol.CliFor(status.Source);
            var tail = string.Equals(frame.Get("w"), "tail", StringComparison.Ordinal);

            Window window;
            try
            {
                window = ReadFor(status.TranscriptPath, tail, frame.Num("from", 0), frame.Num("to", 0), cli);
            }
            catch
            {
                await ErrAsync(fromPeer, frame.Id, MirrorProtocol.ErrNoTranscript, name).ConfigureAwait(false);
                return;
            }

            // A watch on this session starts where its opening read ended, so
            // the two cannot overlap or leave a gap between them.
            if (tail) SetWatchOffset(fromPeer, name, window.To);

            var fields = new Dictionary<string, string>
            {
                ["wfrom"] = window.From.ToString(),
                ["wto"] = window.To.ToString(),
                ["flen"] = window.Length.ToString(),
                ["gen"] = GenFor(name).ToString()
            };

            await SendTransferAsync(
                fromPeer, frame.Id, MirrorProtocol.EncodeTurns(window.Turns), fields, sub: null)
                .ConfigureAwait(false);
        }

        private readonly record struct Window(
            List<MirrorProtocol.MirrorTurn> Turns, long From, long To, long Length);

        private static Window ReadFor(string path, bool tail, long from, long to, string cli)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = fs.Length;

            if (tail) return ReadTailThatFits(fs, length, cli);

            from = Math.Max(0, from);
            to = Math.Min(to <= 0 ? length : to, length);

            var read = ReadRange(fs, from, to, alignStart: true);
            return new Window(
                MirrorProtocol.TurnsFrom(read.Lines, cli), read.From, read.To, length);
        }

        // The newest slice of a transcript that still fits in one chunk.
        //
        // A first paint has to arrive, and on this wire a chunk costs a model
        // turn — close to two minutes on the relay this was measured on. Two
        // chunks is therefore a four-minute first paint against a request
        // timeout of three, which is not a slow panel but an empty one: the
        // reply cannot exist before the request expires. CB-46 is that bug, and
        // the panel it was found on had shown nothing for hours.
        //
        // Measured rather than asserted, because a byte count cannot predict
        // this. What matters is how large the *encoded, compressed turns* are,
        // and the ratio between raw transcript bytes and that is wildly
        // variable: a chatty session compresses twenty to one, a session full of
        // hashes or base64 barely at all, and how many turns a byte range even
        // contains depends on how long its rows are. So this shrinks the window
        // and asks the real encoder, rather than picking a constant and hoping.
        //
        // Halving from the newest end, so what survives is always the most
        // recent conversation — the part somebody opening a panel is looking
        // for. Paging back supplies the rest on demand.
        //
        // The floor matters as much as the loop. A single turn can be bigger
        // than a chunk all by itself, and there is no window size that makes it
        // fit; when halving stops helping, the window is sent as-is and arrives
        // over several chunks. That is slow but correct, and it is why the
        // client's timeout extends while chunks are still coming rather than
        // being a flat deadline — the two changes are halves of one fix.
        private static Window ReadTailThatFits(FileStream fs, long length, string cli)
        {
            var span = (long)MirrorProtocol.InitialBytes;
            var best = ReadTail(fs, length, span, cli);

            // The binding constraint is the chunk, not the byte count, and the
            // two are only loosely related — so the window is searched for
            // rather than calculated, in whichever direction the first guess was
            // wrong.
            return FitsOneChunk(best.Turns)
                ? Grow(fs, length, span, best, cli)
                : Shrink(fs, length, span, best, cli);
        }

        // Bigger while it still fits, because a bigger window is more
        // conversation and the panel should show as much as one chunk can carry.
        //
        // This is not greed, it is the noise case. Most of a transcript is rows
        // no panel ever shows — tool results, and file-history snapshots that
        // run to hundreds of kilobytes each — and those contribute no turns, so
        // they contribute no payload either. A window that lands inside one is
        // spent entirely on something invisible: a fixed 128KB tail on a
        // transcript whose newest 200KB is a single snapshot row paints one
        // message where it could have painted the whole conversation. Growing
        // past such a row is free in the only currency that matters here.
        private static Window Grow(FileStream fs, long length, long span, Window best, string cli)
        {
            // **Stops when it has enough conversation, not when it stops
            // fitting — and the difference is 27 seconds per panel open.**
            //
            // The old condition was "grow while it still fits one chunk", which
            // terminated quickly only because a chunk was 6KB. A chunk is now
            // the whole 32MB message, so everything fits and this doubled all
            // the way to MaxTailBytes every time — six passes, each re-reading
            // and re-gzipping up to eight megabytes. Measured at 27s on a
            // transcript that does not compress, which is a panel that looks
            // hung.
            //
            // The reason for growing at all survives intact and is the noise
            // case: most of a transcript is rows no panel shows — tool results,
            // file-history snapshots running to hundreds of kilobytes — and a
            // window landing inside one is spent entirely on something
            // invisible. So it grows while the window is *short of
            // conversation*, which is what that fault actually looks like, and
            // stops as soon as it has some. On an ordinary transcript the first
            // read already has plenty and this does nothing at all.
            while (span < MaxTailBytes
                   && best.From > 0
                   && best.Turns.Count < EnoughTurnsToOpenOn)
            {
                span *= 2;

                var bigger = ReadTail(fs, length, span, cli);

                // Still bounded by what the wire can carry. It can no longer
                // bind in practice, and leaving it costs nothing and keeps the
                // guarantee true if the ceiling ever moves.
                if (!FitsOneChunk(bigger.Turns)) break;

                best = bigger;
            }

            return best;
        }

        // Enough to fill a panel and give somebody something to scroll, without
        // reaching for a whole conversation nobody asked for. Paging back
        // supplies the rest on demand, which is what paging is for.
        private const int EnoughTurnsToOpenOn = 40;

        // Smaller until it fits, halving from the newest end so what survives is
        // always the most recent conversation — the part somebody opening a
        // panel is looking for. Paging back supplies the rest on demand.
        private static Window Shrink(FileStream fs, long length, long span, Window best, string cli)
        {
            while (!FitsOneChunk(best.Turns))
            {
                span /= 2;

                // Nothing left to give up. Either the newest single row is
                // larger than a chunk all by itself — no window size makes that
                // fit — or the file is smaller than the floor and shrinking
                // further would start returning nothing at all. An empty panel
                // is the failure being fixed, not an acceptable way to fix it,
                // so the oversized window is sent and arrives over several
                // chunks: slow, but correct, and the client's timeout extends
                // while chunks are still coming precisely so this case lands.
                if (span < MinTailBytes) break;

                var smaller = ReadTail(fs, length, span, cli);

                // A smaller byte window that yields no turns has cut into the
                // middle of the newest row. Keep the larger one: too slow beats
                // nothing to show.
                if (smaller.Turns.Count == 0) break;

                best = smaller;
            }

            return best;
        }

        // A ceiling on the search rather than on the answer: each step re-reads
        // and re-parses, and past a few megabytes that is real CPU spent to
        // discover something a chunk was never going to hold anyway.
        // Internal so a paging test can size its fixture against the real cap.
        // It used to be sized against the chunk instead, which worked only while
        // a chunk was small — see the note in MirrorRoundTripTests.
        internal const int MaxTailBytes = 8 * 1024 * 1024;

        // Below this a window stops being a conversation. Four kilobytes is a
        // handful of turns, and if that still does not fit one chunk then no
        // window will, so the loop stops rather than shrinking towards zero.
        private const int MinTailBytes = 4 * 1024;

        private static Window ReadTail(FileStream fs, long length, long span, string cli)
        {
            var read = ReadRange(fs, Math.Max(0, length - span), length, alignStart: true);

            return new Window(
                MirrorProtocol.TurnsFrom(read.Lines, cli), read.From, read.To, length);
        }

        // Asked of the same encoder and splitter that will carry it, because a
        // prediction of that answer is exactly the thing that has been wrong.
        internal static bool FitsOneChunk(List<MirrorProtocol.MirrorTurn> turns) =>
            MirrorProtocol.Split(MirrorProtocol.EncodeTurns(turns)).Count <= 1;

        // A byte range as whole lines, and the two offsets that bound what was
        // actually read.
        //
        // **`alignStart` is the whole subtlety here, and getting it wrong drops
        // messages silently.** There are two different reads in this file and
        // they need opposite answers:
        //
        //  * An opening window or a page of backlog starts at a *computed*
        //    offset — "half a megabyte back from the end" — which almost
        //    certainly lands in the middle of a row. That partial row has to go,
        //    and `From` reports where the first whole row actually began, so the
        //    next page back stops there rather than reading it twice.
        //
        //  * Following a live transcript starts where the last read *finished*,
        //    which is exactly a row boundary. Dropping the first line there
        //    throws away a complete row — and it is always the newest one, so
        //    every single update loses its first message and nothing about the
        //    panel looks wrong. Caught by a test that appended one line and
        //    watched nothing arrive.
        //
        // The end is aligned in both cases: a transcript is being written to
        // while it is read, so the last line in a window is routinely a row the
        // writer has not finished. `To` reports the end of the last *complete*
        // row, which is where the next read must resume.
        //
        // The step-over rule is ported from LocalCliChatSession unchanged: a
        // window that lands entirely inside one row — which a megabyte-long
        // file-history snapshot manages — reports `from` rather than `to`, so
        // paging steps over it instead of asking for the same megabyte forever.
        internal static (List<string> Lines, long From, long To) ReadRange(
            FileStream fs, long from, long to, bool alignStart)
        {
            if (to <= from) return (new List<string>(), from, from);

            fs.Seek(from, SeekOrigin.Begin);
            var buffer = new byte[to - from];
            fs.ReadExactly(buffer);

            var start = 0;
            if (alignStart && from > 0)
            {
                var nl = Array.IndexOf(buffer, (byte)'\n');
                if (nl < 0) return (new List<string>(), from, from);

                start = nl + 1;
            }

            var last = Array.LastIndexOf(buffer, (byte)'\n');
            if (last < start) return (new List<string>(), from + start, from + start);

            var text = Encoding.UTF8.GetString(buffer, start, last + 1 - start);

            return (
                text.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList(),
                from + start,
                from + last + 1);
        }

        // --- watching ---------------------------------------------------------

        private async Task WatchAsync(string fromPeer, MirrorProtocol.MirrorFrame frame)
        {
            var name = frame.Text("n");
            if (name is null) return;

            var resolved = Resolve(name, _seams.Agents(), _seams.LocalSessions());
            if (resolved is null)
            {
                await ErrAsync(fromPeer, frame.Id, MirrorProtocol.ErrNoSession, name).ConfigureAwait(false);
                return;
            }

            var ttl = frame.Num("ttl", MirrorProtocol.WatchTtlSeconds);
            if (ttl <= 0 || ttl > MirrorProtocol.WatchTtlSeconds) ttl = MirrorProtocol.WatchTtlSeconds;

            var from = frame.Num("from", -1);

            lock (_gate)
            {
                // Keyed by the watch's own id so renewing one is an update
                // rather than a second subscription — a panel left open for an
                // afternoon renews every ninety seconds, and thirty stacked
                // subscriptions would send the same delta thirty times.
                _watches[frame.Id] = new Subscription
                {
                    Watcher = fromPeer,
                    Name = name,
                    Id = frame.Id,
                    Cli = MirrorProtocol.CliFor(resolved.Value.Status.Source),
                    Offset = from >= 0 ? from : PendingOffset(fromPeer, name),
                    Gen = GenFor(name),
                        Expires = Now().AddSeconds(ttl)
                };
            }

            await SendAsync(fromPeer, MirrorProtocol.BuildFrame(
                MirrorProtocol.Ok, frame.Id)).ConfigureAwait(false);
        }

        // Where a watch should start when the client didn't say: exactly where
        // its opening read finished.
        private readonly Dictionary<string, long> _pendingOffsets = new(StringComparer.OrdinalIgnoreCase);

        private void SetWatchOffset(string watcher, string name, long offset)
        {
            lock (_gate) _pendingOffsets[watcher + "\0" + name] = offset;
        }

        private long PendingOffset(string watcher, string name)
        {
            lock (_gate)
            {
                return _pendingOffsets.TryGetValue(watcher + "\0" + name, out var at) ? at : 0;
            }
        }

        // Which incarnation of a transcript we are reading.
        //
        // /clear starts a new file, and Claude Code can rewrite one wholesale.
        // Without something to say so, a client holding a byte offset into the
        // old file would go on asking for a position that now means something
        // else entirely. Bumping a counter lets it throw away what it has and
        // re-anchor, which is the only correct answer.
        private readonly Dictionary<string, long> _gens = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _lengths = new(StringComparer.OrdinalIgnoreCase);

        private long GenFor(string name)
        {
            lock (_gate) return _gens.TryGetValue(name, out var gen) ? gen : 0;
        }

        // Anything new on any watched session. Called on a timer by
        // RemoteControlSessions — a local file read, so it costs nothing and can
        // run far more often than anything that spends a model turn.
        public async Task TickAsync()
        {
            List<Subscription> due;
            var now = Now();

            lock (_gate)
            {
                foreach (var expired in _watches.Where(w => w.Value.Expires <= now).Select(w => w.Key).ToList())
                    _watches.Remove(expired);

                due = _watches.Values.ToList();
            }

            if (due.Count == 0) return;

            var agents = _seams.Agents();
            var sessions = _seams.LocalSessions();

            foreach (var watch in due)
            {
                var resolved = Resolve(watch.Name, agents, sessions);
                if (resolved is null) continue;

                var path = resolved.Value.Status.TranscriptPath;
                if (string.IsNullOrEmpty(path)) continue;

                Window window;
                long gen;

                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var length = fs.Length;

                    lock (_gate)
                    {
                        // Shorter than where this watch was reading means the
                        // file was replaced under it.
                        if (length < watch.Offset)
                        {
                            _gens[watch.Name] = GenFor(watch.Name) + 1;
                            watch.Offset = 0;
                        }

                        _lengths[watch.Name] = length;
                        gen = GenFor(watch.Name);
                    }

                    if (length <= watch.Offset) continue;

                    // alignStart false: this offset is where the last read
                    // finished, so it is already a row boundary and the first
                    // line here is a whole row. See ReadRange.
                    var read = ReadRange(fs, watch.Offset, length, alignStart: false);

                    window = new Window(
                        MirrorProtocol.TurnsFrom(read.Lines, watch.Cli),
                        read.From, read.To, length);

                    // Only over the complete rows. A row the writer had not
                    // finished is left for the next tick rather than half-sent
                    // and then skipped.
                    watch.Offset = read.To;
                }
                catch
                {
                    // Mid-write, or gone. The next tick tries again.
                    continue;
                }

                // Nothing displayable in the new bytes — a stretch of tool
                // results, which is most of a transcript. The offset still
                // moved, so this is silence rather than a gap.
                if (window.Turns.Count == 0) continue;

                var fields = new Dictionary<string, string>
                {
                    ["wfrom"] = window.From.ToString(),
                    ["wto"] = window.To.ToString(),
                    ["flen"] = window.Length.ToString(),
                    ["gen"] = gen.ToString()
                };

                await SendTransferAsync(
                    watch.Watcher, MirrorProtocol.NewId(),
                    MirrorProtocol.EncodeTurns(window.Turns), fields, sub: watch.Id)
                    .ConfigureAwait(false);
            }
        }

        // --- typing ------------------------------------------------------------

        private async Task InputAsync(string fromPeer, MirrorProtocol.MirrorFrame frame)
        {
            // An unverified payload is refused before anything is resolved. This
            // one is not a display concern: typing text that arrived corrupted
            // into somebody's terminal is the worst thing in this file.
            if (!frame.PayloadVerified || frame.Payload is null)
            {
                await ErrAsync(fromPeer, frame.Id, MirrorProtocol.ErrBadHash, "input failed its hash")
                    .ConfigureAwait(false);
                return;
            }

            // Decoded here rather than beside TypeInto below, where this used
            // to live: the messaging path below needs the same text, and it
            // is tried before CanType is even asked.
            var text = Encoding.UTF8.GetString(frame.Payload);

            var name = frame.Text("n");
            if (name is null)
            {
                await ErrAsync(fromPeer, frame.Id, MirrorProtocol.ErrNoSession, "no name").ConfigureAwait(false);
                return;
            }

            var resolved = Resolve(name, _seams.Agents(), _seams.LocalSessions());
            if (resolved is null)
            {
                await ErrAsync(fromPeer, frame.Id, MirrorProtocol.ErrNoSession, name).ConfigureAwait(false);
                return;
            }

            var status = resolved.Value.Status;

            // This machine's own setting, not the asker's. See the class note.
            if (!_seams.ReplyEnabled(status.Source))
            {
                await ErrAsync(fromPeer, frame.Id, MirrorProtocol.ErrReplyOff, name).ConfigureAwait(false);
                return;
            }

            if (!_seams.CanType(status))
            {
                // No pane to type into is no longer the end of it. A
                // background or agent-mode job never has a terminal at all,
                // and CB-105 gives it a second route: this machine's own
                // SessionMessenger hands the text to that session's own IPC
                // socket instead.
                //
                // Tried only when the seam is actually wired — CanDeliver and
                // Deliver default to null, and a harness or a build that
                // predates this feature must behave exactly as it always
                // has: straight to ErrNoPane, with nothing about this branch
                // observable. That back-compat guarantee is what
                // MirrorRoundTripTests.TypingIsRefusedWhenThereIsNoPaneToTypeInto
                // pins.
                if (_seams.CanDeliver?.Invoke(status) == true)
                {
                    DeliveryReceipt receipt;
                    try { receipt = await _seams.Deliver!(status, text).ConfigureAwait(false); }
                    catch
                    {
                        // The seam threw rather than answering — a registry
                        // scan mid-write, a socket that raised instead of
                        // just refusing. Read the same as SocketRefused:
                        // nothing reached the far side either way, and the
                        // caller sees a clean error rather than a connection
                        // torn down by an unhandled exception.
                        receipt = new DeliveryReceipt(DeliveryResult.SocketRefused, null);
                    }

                    if (receipt.Result == DeliveryResult.Accepted)
                    {
                        var okFields = new Dictionary<string, string> { ["via"] = MirrorProtocol.ViaMessage };
                        if (!string.IsNullOrEmpty(receipt.AgentStatus)) okFields["agent"] = receipt.AgentStatus;

                        await SendAsync(fromPeer, MirrorProtocol.BuildFrame(
                            MirrorProtocol.Ok, frame.Id, okFields)).ConfigureAwait(false);
                        return;
                    }

                    var code = receipt.Result == DeliveryResult.NoRegistryEntry
                        ? MirrorProtocol.ErrNotRegistered
                        : MirrorProtocol.ErrDeliverFailed;

                    await ErrAsync(fromPeer, frame.Id, code, name).ConfigureAwait(false);
                    return;
                }

                await ErrAsync(fromPeer, frame.Id, MirrorProtocol.ErrNoPane, name).ConfigureAwait(false);
                return;
            }

            bool typed;
            try { typed = await _seams.TypeInto(status, text).ConfigureAwait(false); }
            catch { typed = false; }

            await SendAsync(fromPeer, typed
                ? MirrorProtocol.BuildFrame(MirrorProtocol.Ok, frame.Id)
                : MirrorProtocol.BuildFrame(MirrorProtocol.Err, frame.Id, new Dictionary<string, string>
                {
                    // Not ErrNoPane. A route was found and it refused — see
                    // that constant's comment for why saying "there is nowhere
                    // to type" here is a wrong answer that reads like a right
                    // one.
                    ["code"] = MirrorProtocol.ErrTypeFailed,
                    ["msg"] = MirrorProtocol.Encode("the terminal refused the text")
                })).ConfigureAwait(false);
        }

        // --- sending ------------------------------------------------------------

        private async Task ResendAsync(string fromPeer, MirrorProtocol.MirrorFrame frame)
        {
            var seq = (int)frame.Num("seq", -1);

            Transfer? transfer;
            lock (_gate) _transfers.TryGetValue(frame.Id, out transfer);

            if (transfer is null || seq < 0 || seq >= transfer.Pieces.Count)
            {
                await ErrAsync(fromPeer, frame.Id, MirrorProtocol.ErrBadHash, "nothing to resend")
                    .ConfigureAwait(false);
                return;
            }

            await SendAsync(fromPeer, Piece(frame.Id, transfer, seq)).ConfigureAwait(false);
        }

        // Splits a payload, remembers the pieces, and sends them in order.
        private async Task SendTransferAsync(
            string toPeer, string id, byte[] payload,
            Dictionary<string, string> fields, string? sub)
        {
            var pieces = MirrorProtocol.Split(payload);

            var transfer = new Transfer
            {
                Watcher = toPeer,
                Pieces = pieces,
                Fields = fields,
                WholeHash = MirrorProtocol.Hash(payload)
            };

            if (sub is not null) transfer.Fields["sub"] = sub;

            lock (_gate)
            {
                _transfers[id] = transfer;
                _transferOrder.Enqueue(id);

                while (_transferOrder.Count > KeepTransfers)
                    _transfers.Remove(_transferOrder.Dequeue());
            }

            for (var seq = 0; seq < pieces.Count; seq++)
            {
                if (!await SendAsync(toPeer, Piece(id, transfer, seq)).ConfigureAwait(false))
                {
                    // The relay refused or timed out. Stopping is right: the
                    // client will time the transfer out and can ask again, and
                    // pushing the rest would spend turns filling in a transfer
                    // nobody can complete.
                    return;
                }
            }
        }

        private static string Piece(string id, Transfer transfer, int seq)
        {
            var fields = new Dictionary<string, string>(transfer.Fields, StringComparer.Ordinal)
            {
                ["seq"] = seq.ToString(),
                ["of"] = transfer.Pieces.Count.ToString()
            };

            // The digest of the whole rides on the last piece, where it is the
            // signal that there is nothing further to wait for as well as the
            // final check.
            if (seq == transfer.Pieces.Count - 1) fields["H"] = transfer.WholeHash;

            return MirrorProtocol.BuildFrame(MirrorProtocol.Chunk, id, fields, transfer.Pieces[seq]);
        }

        private Task ErrAsync(string toPeer, string id, string code, string detail) =>
            SendAsync(toPeer, MirrorProtocol.BuildFrame(
                MirrorProtocol.Err, id, new Dictionary<string, string>
                {
                    ["code"] = code,
                    ["msg"] = MirrorProtocol.Encode(detail)
                }));

        // Excluded from coverage: exists to be the try/catch around the relay.
        // A relay that has gone away throws rather than answering, and this side
        // simply stops talking to a peer it cannot reach — there is nobody to
        // tell, which is what separates this from the client's version, where
        // there is a panel waiting.
        //
        // That behaviour is asserted in MirrorRoundTripTests through a courier
        // that throws; what is not measured is only the swallow itself.
        [ExcludeFromCodeCoverage]
        private async Task<bool> SendAsync(string toPeer, string frame)
        {
            try { return await _seams.SendFrame(toPeer, frame).ConfigureAwait(false); }
            catch { return false; }
        }

        public string Account => _account;

        // True while anything is being watched, which is what tells
        // RemoteControlSessions to keep draining the relay quickly.
        public bool Busy
        {
            get { lock (_gate) return _watches.Count > 0; }
        }
    }
}
