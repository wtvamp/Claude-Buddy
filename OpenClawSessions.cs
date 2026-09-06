using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Avalonia.Threading;

namespace ClaudeBuddy
{
    // The OpenClaw half of what SessionManager displays: one connection to the
    // gateway, kept alive in the background, publishing an immutable snapshot
    // the scan reads for free.
    //
    // Shaped like ClaudeDesktopManager rather than as an interface with two
    // implementations: every other second data source in this app is a static
    // class with a private cache and a gate (BackgroundJobs, AgentTeam,
    // AgentTeamViewer), and an interface whose second implementation is
    // permanently off would be ceremony. Off means Snapshot() returns an empty
    // array — no socket, no task, no key, nothing constructed at all.
    internal static class OpenClawSessions
    {
        private static readonly object Gate = new();

        // Published whole and replaced whole. The scan runs on the UI thread and
        // never locks anything else it reads; handing it a finished list keeps
        // that true.
        private static volatile IReadOnlyList<Session> _snapshot = Array.Empty<Session>();

        private static Task? _supervisor;
        private static CancellationTokenSource? _cts;

        // The live connection, so an opened chat can ask for its own backlog.
        // Null while disconnected, which is the only reason a history load
        // silently doesn't happen — the panel then fills from live events.
        private static OpenClawGateway? _gateway;

        private static string _state = "off";

        // Which sessions are mid-run, by session key. Maintained from the event
        // stream rather than from sessions.list, because the list is wrong about
        // it: `hasActiveRun` never once flipped across a complete observed run,
        // and a run's own key never appears in the list at all. Events are also
        // immediate, where a poll is up to a scan behind.
        private static readonly Dictionary<string, DateTime> Running =
            new(StringComparer.Ordinal);

        // When we last saw *any* activity on a session, from the event stream.
        //
        // Not the same thing as Running, and it exists because sessions.list
        // lies about this. Its lastActivityAt/updatedAt are hours stale for a
        // Discord conversation that is happening right now — measured: a chat
        // in progress reported 6640s since last activity while its agent was
        // mid-reply, where a cron session on the same gateway updated every five
        // minutes. Trusting the list alone made a Discord orb appear for the
        // twenty seconds of a reply and then vanish, which reads as the feature
        // not working rather than as a stale timestamp.
        private static readonly Dictionary<string, DateTime> LastSeen =
            new(StringComparer.Ordinal);

        // One chat session per gateway key, created the first time an orb is
        // clicked and kept afterwards so its transcript survives the panel being
        // dismissed and reopened. Only sessions someone has actually opened are
        // in here — a gateway with 59 sessions does not get 59 transcripts.
        private static readonly Dictionary<string, OpenClawChatSession> Chats =
            new(StringComparer.Ordinal);

        // Agent id -> the name its owner gave it: main is Lilibeth, comfyui is
        // Zara. The ids are what the session keys are built from, but they are
        // an implementation detail of somebody's config — the names are what
        // the agents are called in Discord, in conversation, and in the user's
        // head. An orb showing "M" for four different agents called main is a
        // worse answer than one showing "L", "Z", "A".
        private static readonly Dictionary<string, string> AgentNames =
            new(StringComparer.OrdinalIgnoreCase);

        // The rest of what an agent's owner gave it: an emoji, and a picture.
        // Both come down inside agents.list — the avatar as a base64 data URI,
        // which is generous of it and also about 8 MB across seven agents, so
        // it is asked for once per connection and the decoded result is what
        // gets kept.
        private static readonly Dictionary<string, AgentIdentity> Identities =
            new(StringComparer.OrdinalIgnoreCase);

        internal sealed record AgentIdentity(string Name, string? Emoji, byte[]? Avatar);

        // A test seam, matching SetSnapshotForTests: the only thing that fills the
        // identity table is LoadAgentNamesAsync, which is an agents.list request
        // over a live connection and is excluded. Without this, everything that
        // draws an agent's name or picture — the orb's avatar, the chat header —
        // is unreachable for a reason that has nothing to do with the code.
        // A test seam for the run tracker. The only thing that fills it is the
        // live event stream, and the rule worth checking — a session stops
        // counting as working once its events go quiet, whether or not a terminal
        // event ever arrived — needs a timestamp in the past to check at all.
        internal static void SetRunningForTests(string key, DateTime when)
        {
            lock (Gate) Running[key] = when;
        }

        internal static void ForgetRunningForTests()
        {
            lock (Gate) Running.Clear();
        }

        internal static void SetIdentitiesForTests(
            IReadOnlyDictionary<string, AgentIdentity> identities,
            IReadOnlyDictionary<string, string>? names = null)
        {
            lock (Gate)
            {
                Identities.Clear();
                foreach (var (id, identity) in identities) Identities[id] = identity;

                if (names is null) return;

                AgentNames.Clear();
                foreach (var (id, name) in names) AgentNames[id] = name;
            }
        }

        public static AgentIdentity? IdentityOf(string agentId)
        {
            lock (Gate) return Identities.GetValueOrDefault(agentId);
        }

        // The agent's picture for a session, already decoded and scaled. Shared
        // with the orb rather than decoded twice — the frames are immutable and
        // the cache is keyed by agent, so both surfaces draw the same objects.
        public static OpenClawAvatars.Avatar? AvatarForSession(string sessionId)
        {
            // A room is not an agent and has no picture of its own, so it wears
            // its members' — see RoomAvatar. Handled here rather than by each
            // caller so the orb and the chat panel's header cannot disagree
            // about what a room looks like; both already ask this one function.
            if (sessionId.StartsWith(RoomPrefix, StringComparison.Ordinal))
                return RoomAvatar(sessionId[RoomPrefix.Length..]);

            var identity = IdentityForSession(sessionId);
            if (identity is null) return null;

            var agent = AgentIdOf(sessionId);
            return agent is null ? null : OpenClawAvatars.For(agent, identity.Avatar);
        }

        // Minted by SessionManager.RoomId. Named here as well because this is
        // the other end of it — a room id is the one session id that names no
        // gateway session at all, and both halves have to agree on the shape.
        private const string RoomPrefix = "openclaw:room:";

        // The people in a channel, drawn as one picture: the orb cut into a
        // wedge each.
        //
        // A room orb used to be the only orb in its own cluster with nothing on
        // it — every agent pointing at it wore a face, and the thing they were
        // all pointing at wore two letters. The channel's initials say *which*
        // conversation, which the ring's colour also says; who is in it was said
        // nowhere.
        //
        // Cut from the channel's *participants* — the members with an orb — and
        // not from its membership, which is deliberately every agent the gateway
        // lists there however long ago it last spoke. Membership is what the
        // room's chat merges and it is right for that; using it here drew
        // #social-media as four faces while two agents were talking in it.
        //
        // The fallback to the full membership is for a room orb outliving its
        // sessions: "Keep orbs for" can hold one on screen after every member
        // has dropped out of the recency window, and the faces of who was in it
        // are still true. Reverting to the channel's initials at that moment
        // would be a change on screen with no event behind it.
        //
        // Ordering is the part worth being careful about. They arrive
        // most-recently-active first, which is how the four that get a wedge are
        // chosen — a channel with seven agents talking in it should show the
        // four most recent. They are then sorted by agent id, so the wedges stay
        // put: chosen *and* ordered by recency, two agents in a fast exchange
        // would swap halves of the orb every time either of them spoke.
        public static OpenClawAvatars.Avatar? RoomAvatar(string roomKey)
        {
            var agents = AgentsInRoom(roomKey);
            if (agents.Count == 0) agents = Distinct(MembersOfRoom(roomKey));

            if (agents.Count == 0) return null;

            if (agents.Count > AvatarPie.MaxParts)
                agents = agents.Take(AvatarPie.MaxParts).ToList();

            agents.Sort(StringComparer.OrdinalIgnoreCase);

            // One member is not a composite. Returning their avatar directly
            // rather than a one-wedge pie keeps the animation an animated
            // avatar has — a composite is a still — and means a channel only
            // one agent talks in looks exactly like that agent, which is true.
            if (agents.Count == 1)
            {
                var only = IdentityOf(agents[0]);
                return only is null ? null : OpenClawAvatars.For(agents[0], only.Avatar);
            }

            var colours = agents.Select(ColourForAgent).ToList();

            var parts = agents
                .Select((agent, i) => new OpenClawAvatars.Part(IdentityOf(agent)?.Avatar, colours[i]))
                .ToList();

            // The colours are in the cache key as well as the agents, because
            // the palette deals its colours across *every* agent the gateway
            // knows — so an agent joining a different channel can recolour a
            // wedge here without this room's membership changing at all.
            var cacheKey = RoomPrefix + roomKey + "|" + string.Join(
                '|', agents.Select((agent, i) => agent + "=" + colours[i]));

            return OpenClawAvatars.Composite(cacheKey, parts);
        }

        public static string? AgentIdOf(string sessionId)
        {
            const string Prefix = "openclaw:";
            var key = sessionId.StartsWith(Prefix, StringComparison.Ordinal)
                ? sessionId[Prefix.Length..]
                : sessionId;

            var parts = key.Split(':');
            return parts.Length >= 2 && parts[0] == "agent" ? parts[1] : null;
        }

        // The agent behind a session, from its key: "agent:<id>:<surface>…".
        public static AgentIdentity? IdentityForSession(string sessionId)
        {
            const string Prefix = "openclaw:";
            var key = sessionId.StartsWith(Prefix, StringComparison.Ordinal)
                ? sessionId[Prefix.Length..]
                : sessionId;

            var parts = key.Split(':');
            return parts.Length >= 2 && parts[0] == "agent" ? IdentityOf(parts[1]) : null;
        }

        // How long a session stays "working" after its last event. A turn emits
        // events continuously while it runs — thinking deltas, tool phases — so
        // silence for this long means it stopped, whether or not a terminal
        // event arrived. Long enough to bridge a slow tool call, short enough
        // that a finished orb doesn't keep pulsing at you.
        private static readonly TimeSpan RunIdle = TimeSpan.FromSeconds(20);

        // How far back a session counts as current at all.
        //
        // This is deliberately *not* the user's "Keep orbs for" setting, which
        // was the first design and is wrong. That setting answers "how long does
        // a session that has gone quiet stay on screen", and it is commonly set
        // to Forever — perfectly sensible for Claude Code, where the list only
        // ever holds sessions that are actually running. A gateway's list is not
        // that: it holds every conversation it has ever had. On the machine this
        // was built against that is 59, of which two had been touched in the last
        // five minutes, so "Forever" meant 59 permanent orbs.
        //
        // So the two questions are separated: this bounds which sessions exist
        // as far as Claude Buddy is concerned, and the lifetime setting still
        // decides how long one of those lingers after it goes quiet.
        // Read per scan rather than cached, so changing it in Settings takes
        // effect on the next poll rather than at the next launch.
        private static TimeSpan? ActiveWithin
        {
            get
            {
                var minutes = ClaudeBuddySettings.OpenClawActiveWithinMinutes;
                return minutes == ClaudeBuddySettings.OpenClawActiveWithinAll
                    ? null
                    : TimeSpan.FromMinutes(minutes);
            }
        }

        // Read per scan, for the same reason ActiveWithin above is: changing it
        // in Settings should take effect on the next poll.
        private static ClusterMode HeartbeatMode => ClaudeBuddySettings.OpenClawHeartbeatMode;
        private static ClusterMode CronMode => ClaudeBuddySettings.OpenClawCronMode;

        internal sealed record Session(
            string Key,
            string Title,
            string Channel,
            string State,
            DateTime LastActivity,
            Delivery? Delivery,
            SessionKind Kind,

            // Whether this is a session the gateway's heartbeat drives. Carried
            // beside Kind rather than folded into it: a heartbeat is how a
            // session gets *woken*, not what kind of conversation it is, and the
            // two are independent — an agent's main session is the heartbeat's
            // default target and is still the session you talk to it in. See
            // OpenClawHeartbeat.
            bool Heartbeat);

        // Where a reply in this session is supposed to end up. The gateway
        // resolves this itself when asked to deliver an agent's answer, but a
        // message *you* typed has to be posted to the channel explicitly, so
        // the client needs to know the address too.
        internal sealed record Delivery(string Channel, string To, string? AccountId);

        // An agent's colour, assigned across the whole set so no two are
        // confusable (see AgentPalette.Assign).
        //
        // Kept here rather than recomputed by each caller because the orb's ring
        // and a chat bubble from the same agent have to be the same colour or
        // the attribution means nothing — and Assign's answer depends on which
        // other agents exist, so two callers computing it from different sets
        // would quietly disagree.
        private static Dictionary<string, string> _agentColours = new(StringComparer.Ordinal);

        public static string ColourForAgent(string? agentId)
        {
            if (string.IsNullOrEmpty(agentId)) return "";
            lock (Gate) return _agentColours.GetValueOrDefault(agentId, "");
        }

        // A room's own colour.
        //
        // Rooms deliberately had none: the ring identifies an *agent*, and a
        // room is not one. That held while a room orb was the only one of its
        // kind on screen and stopped holding as soon as there were several —
        // "#cascadia-forensics" and "#arch" both arrive as a dark circle with a
        // # on it, and the badge says what they are while nothing says which.
        // The colour is the answer to *which*, exactly as it is for an agent.
        //
        // Dealt from AgentPalette.Assign over the rooms, not taken from
        // HexFor on each one separately. HexFor was the first attempt and is
        // the reason this comment exists: hashing each key independently put
        // two of four real room keys on #5FD79B and #5FD7A1, which are the same
        // colour to anyone looking at two orbs. Assign is the function that
        // exists to stop that, and it is what the agents already use.
        //
        // A separate pool from the agents, though, so a room's colour does not
        // move because an agent joined or left. It moves when the set of *rooms*
        // changes, which is rare and is the same bargain the agents make.
        public static string ColourForRoom(string? roomKey)
        {
            if (string.IsNullOrEmpty(roomKey)) return "";
            lock (Gate) return _roomColours.GetValueOrDefault(roomKey, "");
        }

        private static Dictionary<string, string> _roomColours = new(StringComparer.Ordinal);

        private static void AssignColours(IEnumerable<string> agentIds)
        {
            var colours = AgentPalette.Assign(agentIds);
            lock (Gate) _agentColours = colours;
        }

        // What the settings window shows on its status row.
        public static string StatusText
        {
            get { lock (Gate) return _state; }
        }

        // True when the last attempt failed *only* because the certificate no
        // longer matches the pinned one. The settings window offers to accept
        // the new one when this is set, and offers nothing of the sort
        // otherwise — see the button's own comment for why it is not automatic.
        public static bool CertificateRejected
        {
            get { lock (Gate) return _certificateRejected; }
        }

        private static bool _certificateRejected;

        // A test seam, matching SetIdentitiesForTests and SetSnapshotForTests:
        // the only thing that ever sets _certificateRejected is a real failed TLS
        // handshake against a gateway, which is exactly the kind of live socket
        // this suite has no business opening. Without it, the settings window's
        // "Trust the new certificate" row is unreachable for a reason that has
        // nothing to do with the row.
        internal static void SetCertificateRejectedForTests(bool value)
        {
            lock (Gate) _certificateRejected = value;
        }

        // A test seam, the same one and for the same reason as the four above:
        // the only thing that ever sets _gateway is RunAsync, which is the
        // supervisor loop and is excluded from coverage because it opens a
        // WebSocket to a real machine. Without this, everything a *send* does —
        // which of the two requests goes first, what is on each of them, and
        // what a failure of either one says — is unreachable for a reason that
        // has nothing to do with the code.
        //
        // Worth having rather than excluding SendToRoomAsync wholesale, which
        // was the first shape. The claim that fix rests on is that the mirror
        // carries the carrier's own accountId, and an excluded method is a claim
        // nothing checks. OpenClawGateway already takes its connector as an
        // argument for exactly this reason (see its own comment), so a gateway
        // over an in-memory socket costs nothing new.
        internal static void SetGatewayForTests(OpenClawGateway? gateway)
        {
            lock (Gate) _gateway = gateway;
        }

        // The conversation in a channel, as one thing. memberKeys are the
        // gateway keys of the sessions standing in it — see
        // OpenClawSessionKind.RoomOf for what decides that.
        //
        // The member chats are created here as a side effect, which is what
        // starts their backlogs loading. That is the same thing opening any one
        // of their orbs would do, so a room costs the same requests as reading
        // it agent by agent, made at once instead of one at a time.
        private static readonly Dictionary<string, OpenClawRoomChatSession> Rooms =
            new(StringComparer.Ordinal);

        // Everyone in a channel, whether or not their orb is on screen. Keyed
        // by OpenClawSessionKind.RoomOf.
        private static Dictionary<string, List<string>> _roomMembers = new(StringComparer.Ordinal);

        // Where every session the gateway listed delivers, by gateway key —
        // including the ones no orb is drawn for.
        //
        // Beside _roomMembers rather than folded into it, and recorded in the
        // same place for the same reason: the snapshot answers "which orbs are
        // worth showing" and this answers "where does this conversation
        // deliver", and a session filtered out for being quiet still has an
        // address. Reading the address off the snapshot is what CB-27 was —
        // a room whose members had all gone quiet had nowhere to post, so the
        // message went privately to one agent and nobody in the channel saw it.
        //
        // Two rejected alternatives, both of which look adequate:
        //
        //   * Deriving it from the room key. "discord:<id>" gives the channel
        //     and the recipient, and that is genuinely enough to post — but not
        //     the accountId, and the accountId is the whole reason a room send
        //     does not double up. The gateway suppresses a bot's own channel
        //     post from that bot's own sessions, so a mirror sent under the
        //     carrier's account is the one thing that reaches the carrier
        //     exactly once.
        //   * Taking whichever member happens to be in the snapshot. That is
        //     recency-dependent, which *is* the bug.
        //
        // Replaced whole per poll, like the snapshot: a session the gateway has
        // stopped listing has no address any more, and holding the last one
        // known would be inventing a destination.
        private static Dictionary<string, Delivery?> _deliveries = new(StringComparer.Ordinal);

        public static IReadOnlyList<string> MembersOfRoom(string roomKey)
        {
            lock (Gate)
                return _roomMembers.TryGetValue(roomKey, out var members)
                    ? members.ToList()
                    : Array.Empty<string>();
        }

        // Everyone in the channel who has an orb right now: the members that
        // came through the recency and cluster filters rather than every agent
        // the gateway has ever listed there.
        //
        // The narrower of the two answers, and the one the room's *picture*
        // wants. A conversation that is happening between two agents should not
        // be drawn as four because two more have a session in the channel and
        // nothing to say — which is what shipping the wide answer to the orb
        // did on #social-media.
        //
        // The wide one stays exactly as it was for the room's chat, which needs
        // a quiet agent's transcript to merge (CB-27). Neither is the "right"
        // list; they answer different questions.
        private static Dictionary<string, List<string>> _roomParticipants =
            new(StringComparer.Ordinal);

        public static IReadOnlyList<string> ParticipantsOfRoom(string roomKey)
        {
            lock (Gate)
                return _roomParticipants.TryGetValue(roomKey, out var standing)
                    ? standing.ToList()
                    : Array.Empty<string>();
        }

        // How many *people* are in a channel, rather than how many sessions —
        // still most-recently-active first.
        //
        // The two differ: one agent can hold more than one session in the same
        // channel, and counting sessions would call that a crowd. Which matters
        // now that the count decides whether a room orb exists at all, and it
        // already mattered to the picture, where the same agent twice would have
        // divided the orb between two copies of one face.
        public static List<string> AgentsInRoom(string roomKey) =>
            Distinct(ParticipantsOfRoom(roomKey));

        private static List<string> Distinct(IReadOnlyList<string> sessionKeys)
        {
            var agents = new List<string>();

            foreach (var key in sessionKeys)
            {
                var agent = AgentIdOf(key);
                if (agent is null || agents.Contains(agent, StringComparer.OrdinalIgnoreCase)) continue;

                agents.Add(agent);
            }

            return agents;
        }

        public static IRemoteChatSession? RoomChatFor(
            string sessionId, string displayName, IReadOnlyList<string> memberKeys)
        {
            if (!ClaudeBuddySettings.OpenClawEnabled) return null;
            if (memberKeys.Count == 0) return null;

            OpenClawRoomChatSession room;
            lock (Gate)
            {
                if (!Rooms.TryGetValue(sessionId, out var existing))
                {
                    existing = new OpenClawRoomChatSession(sessionId, displayName);
                    Rooms[sessionId] = existing;
                }

                existing.DisplayName = displayName;
                room = existing;
            }

            var members = new List<(OpenClawChatSession Chat, string Agent, string Colour)>();
            foreach (var key in memberKeys)
            {
                if (ChatFor("openclaw:" + key, displayName) is not OpenClawChatSession chat) continue;

                var agentId = AgentIdOf(key) ?? key;
                members.Add((chat, AgentNameOf(agentId), ColourForAgent(agentId)));
            }

            room.SetMembers(members);

            // Widening the window the merge can be trusted over, in the
            // background: the members' first pages rarely cover the same
            // stretch, and the room can only show where they overlap.
            _ = room.DeepenAsync();

            return room;
        }

        // The panel's view of one session. sessionId is the app's namespaced
        // id; the gateway knows it without the prefix.
        public static IRemoteChatSession? ChatFor(string sessionId, string displayName)
        {
            if (!ClaudeBuddySettings.OpenClawEnabled) return null;

            const string Prefix = "openclaw:";
            if (!sessionId.StartsWith(Prefix, StringComparison.Ordinal)) return null;

            var key = sessionId[Prefix.Length..];

            lock (Gate)
            {
                // The delivery map first, the snapshot second.
                //
                // The map holds every session the gateway listed; the snapshot
                // holds the ones whose orbs are worth drawing. A member of a
                // channel that has been quiet for longer than the window is in
                // the first and not the second, and reading only the second is
                // what left it with no address — the mirror silently skipped,
                // the message delivered privately to one agent, and nothing in
                // the channel to show for it.
                //
                // The snapshot is still consulted, because SetSnapshotForTests
                // is the seam a test publishes sessions through and a fallback
                // that reached nothing would make those tests pass by accident.
                var delivery = _deliveries.TryGetValue(key, out var known)
                    ? known
                    : _snapshot.FirstOrDefault(s => "openclaw:" + s.Key == sessionId)?.Delivery;

                if (!Chats.TryGetValue(key, out var chat))
                {
                    chat = new OpenClawChatSession(sessionId, key, displayName);
                    Chats[key] = chat;
                }

                // Assigned only when there is one, never cleared.
                //
                // The same rule, for the same reason, as ChatSpeaker.Resolve:
                // knowing an address and then not knowing it is a gap in what we
                // have been told — a poll that lost the race, a reconnect that
                // emptied the tables — and never news that the conversation
                // stopped living anywhere. A panel reopened in that window used
                // to have its mirror turned off for the rest of the run.
                if (delivery is not null) chat.Delivery = delivery;

                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    // Names arrive from agents.list a moment after the
                    // connection, so a session first opened in that window was
                    // created holding the raw id.
                    chat.DisplayName = displayName;
                }

                // Refreshed on every open, not just on the first: the gateway
                // records the same turns we see live, plus everything that
                // happened through Discord or the TUI while this panel was
                // closed. Re-reading is both simpler than merging and more
                // truthful than whatever we happened to catch.
                //
                // Fire and forget, so the panel opens now and fills in a moment
                // later rather than making the click wait on a round trip.
                _ = LoadHistoryAsync(chat, _cts?.Token ?? CancellationToken.None);

                return chat;
            }
        }

        public static IReadOnlyList<Session> Snapshot() =>
            ClaudeBuddySettings.OpenClawEnabled ? _snapshot : Array.Empty<Session>();

        // A test seam, in the same spirit as ClaudeBuddySettings.ReloadForTests
        // and OpenClawIdentity.ResetForTests: the poll loop above is the only
        // thing that publishes a snapshot, and it is excluded from coverage
        // because it needs a live gateway. Without this, everything downstream of
        // the snapshot — the gateway orbs, and the room orbs SessionManager
        // invents from them — is unreachable for a reason that has nothing to do
        // with the code being hard to test.
        //
        // Takes what Parse returns, so a test publishes exactly what a real poll
        // would rather than a shape of its own invention.
        internal static void SetSnapshotForTests(IReadOnlyList<Session> sessions)
        {
            _snapshot = sessions;
        }

        // Accept whatever certificate the gateway is now serving.
        //
        // Clearing the pin *and* the rejection together, rather than letting the
        // next successful connection clear the flag, because the flag means "the
        // pin is refusing this gateway" and after this call there is no pin to
        // refuse with. Waiting for the connection left the settings window still
        // offering to trust a certificate that had already been trusted — the
        // reconnect is asynchronous and the window redraws long before it
        // finishes, so the button sat there until something else redrew it.
        //
        // The status line moves too, for the same reason: leaving the old
        // sentence up under a button that has just gone would read as the click
        // having done nothing.
        // Excluded from coverage: records a pinned fingerprint and reconnects to
        // the gateway.
        [ExcludeFromCodeCoverage]
        public static void TrustNewCertificate()
        {
            ClaudeBuddySettings.OpenClawFingerprint = "";

            lock (Gate)
            {
                _certificateRejected = false;
                _state = "connecting…";
            }

            Restart();
        }

        // Called on launch and whenever the settings change. Idempotent: a
        // second call while running is a restart, which is what changing the
        // host or the token means.
        // Excluded from coverage: tears down the supervisor task and opens a new
        // gateway connection.
        [ExcludeFromCodeCoverage]
        public static void Restart()
        {
            lock (Gate)
            {
                _cts?.Cancel();
                _cts = null;
                _supervisor = null;
                _snapshot = Array.Empty<Session>();
                Running.Clear();

                // LastSeen deliberately survives a reconnect: a session that was
                // busy ten seconds before the socket dropped is still a session
                // worth showing when it comes back.

                // Transcripts are deliberately kept across a reconnect: the
                // conversation didn't stop happening because the socket did.
                foreach (var chat in Chats.Values) chat.SetState(RemoteChatState.Connecting);

                if (!ClaudeBuddySettings.OpenClawEnabled)
                {
                    _state = "off";
                    return;
                }

                var host = ClaudeBuddySettings.OpenClawHost;
                if (string.IsNullOrWhiteSpace(host))
                {
                    _state = "no gateway address set";
                    return;
                }

                _state = "connecting…";
                _cts = new CancellationTokenSource();
                _supervisor = Task.Run(() => RunAsync(host, ClaudeBuddySettings.OpenClawPort, _cts.Token));
            }
        }

        // Excluded from coverage: the supervisor loop: connects a WebSocket to a
        // live gateway and reconnects for as long as the app runs.
        [ExcludeFromCodeCoverage]
        private static async Task RunAsync(string host, int port, CancellationToken ct)
        {
            var backoff = TimeSpan.FromSeconds(2);

            while (!ct.IsCancellationRequested)
            {
                OpenClawGateway? gateway = null;

                try
                {
                    var token = OpenClawIdentity.GatewayTokenFor(host) ?? "";
                    gateway = new OpenClawGateway(host, port, token);
                    gateway.EventReceived += OnEvent;

                    var pinned = ClaudeBuddySettings.OpenClawFingerprint;
                    var result = await gateway.ConnectAsync(
                        string.IsNullOrEmpty(pinned) ? null : pinned, ct);

                    if (result.Outcome != OpenClawGateway.Outcome.Connected)
                    {
                        Report(Describe(result));

                        // Recorded as a flag as well as a sentence, because the
                        // settings window has to *offer something* for this one
                        // rather than only describe it — a changed certificate
                        // is otherwise a permanent dead end with no way through
                        // but editing settings.json.
                        lock (Gate)
                        {
                            _certificateRejected =
                                result.Outcome == OpenClawGateway.Outcome.CertificateMismatch;
                        }

                        // Terminal states get no retry. A gateway that refuses
                        // our credentials will refuse them again in two seconds,
                        // and again after that — the only thing a retry loop
                        // achieves is a connection attempt per second against a
                        // machine the user owns, forever.
                        if (result.Outcome is OpenClawGateway.Outcome.AuthRejected
                            or OpenClawGateway.Outcome.CertificateMismatch)
                        {
                            return;
                        }

                        // Falling through, not continuing.
                        //
                        // `continue` jumps to the loop condition, which is past
                        // the finally *and* past the backoff delay at the bottom
                        // — so a gateway waiting to be approved was re-attempted
                        // as fast as a TLS handshake can complete, forever. The
                        // comment here used to claim this reached "the retry
                        // below". It did not.
                        //
                        // Not throwing either: that put raw exception text
                        // through Report and wiped out the instructions just
                        // written, which is what left it saying "connecting…"
                        // instead of what to do.
                    }
                    else
                    {

                    // Remember what we agreed to trust, the first time only. A
                    // later mismatch is then a refusal rather than a silent
                    // re-pinning, which is the entire value of pinning.
                    if (string.IsNullOrEmpty(pinned)
                        && !string.IsNullOrEmpty(gateway.ObservedFingerprint))
                    {
                        var seen = gateway.ObservedFingerprint;
                        Dispatcher.UIThread.Post(() => ClaudeBuddySettings.OpenClawFingerprint = seen);
                    }

                    backoff = TimeSpan.FromSeconds(2);   // reset on a real connect, never before

                    // Whatever the certificate was, it is agreed now — so the
                    // offer to accept a new one goes away with the problem
                    // rather than lingering as a button that would clear a pin
                    // nothing is complaining about.
                    lock (Gate)
                    {
                        _gateway = gateway;
                        _certificateRejected = false;
                    }

                    // A panel opened while disconnected has an empty transcript
                    // and no way to know it should try again, so reconnecting
                    // refills whatever is already on screen.
                    foreach (var chat in OpenChats()) _ = LoadHistoryAsync(chat, ct);

                    await LoadAgentNamesAsync(gateway, ct);
                    await SubscribeAsync(gateway, ct);
                    await PollAsync(gateway, ct);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Ours: the feature was switched off or the app is closing.
                    break;
                }
                catch (OperationCanceledException)
                {
                    // Not ours — a request timed out. RequestAsync cancels its
                    // own task after twenty seconds, and TaskCanceledException
                    // derives from this, so an unguarded catch here treated a
                    // dead socket as "we were asked to stop" and left the
                    // supervisor broken until the app was restarted. That is
                    // exactly the case this loop exists for: a sleeping gateway
                    // is the most likely reason a request never comes back.
                    Report("The gateway stopped responding. Reconnecting…");
                }
                catch (Exception ex)
                {
                    Report(ex.Message);
                }
                finally
                {
                    // Only if it is still ours. Restart() cancels this loop and
                    // starts another without waiting for it, so a slow unwind
                    // here could otherwise null out the *new* connection's
                    // gateway — leaving a live socket whose orbs keep updating
                    // while every send and every history load fails with "not
                    // connected", until the next reconnect happened to fix it.
                    lock (Gate)
                    {
                        if (ReferenceEquals(_gateway, gateway)) _gateway = null;
                    }

                    gateway?.Dispose();
                }

                if (ct.IsCancellationRequested) break;

                // Orbs go the moment the connection does: an orb for a session
                // we can no longer see the state of is a lie that pulses.
                _snapshot = Array.Empty<Session>();

                try { await Task.Delay(backoff, ct); } catch { break; }
                backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
            }
        }

        internal static string Describe(OpenClawGateway.ConnectResult result) => result.Outcome switch
        {
            OpenClawGateway.Outcome.PairingPending =>
                "waiting to be approved on the gateway — run `openclaw devices approve --latest`",
            OpenClawGateway.Outcome.AuthRejected =>
                "the gateway refused these credentials: " + result.Detail,
            OpenClawGateway.Outcome.CertificateMismatch =>
                "the gateway is presenting a different certificate than the one this install trusts",
            OpenClawGateway.Outcome.Unreachable =>
                "can't reach the gateway: " + result.Detail,
            _ => result.Detail ?? "not connected"
        };

        // Excluded from coverage: an agents.list request over the live connection.
        [ExcludeFromCodeCoverage]
        private static async Task LoadAgentNamesAsync(OpenClawGateway gateway, CancellationToken ct)
        {
            try
            {
                var res = await gateway.RequestAsync("agents.list", new Dictionary<string, object>(), ct);
                if (!res.TryGetProperty("agents", out var agents)
                    || agents.ValueKind != JsonValueKind.Array)
                {
                    return;
                }

                // Built outside the lock. Base64-decoding seven avatars is
                // about 8 MB of work, and the UI thread takes this same lock on
                // every scan to read agent names — holding it through that would
                // stall the orbs for as long as the decode took.
                var parsed = new List<(string Id, AgentIdentity Identity)>();

                foreach (var agent in agents.EnumerateArray())
                {
                    var id = Str(agent, "id");
                    if (string.IsNullOrWhiteSpace(id)) continue;

                    var identity = agent.TryGetProperty("identity", out var block)
                        && block.ValueKind == JsonValueKind.Object
                            ? block
                            : default;

                    var name = Str(agent, "displayName");
                    if (string.IsNullOrWhiteSpace(name)) name = Str(agent, "name");
                    if (string.IsNullOrWhiteSpace(name) && identity.ValueKind == JsonValueKind.Object)
                    {
                        name = Str(identity, "name");
                    }

                    parsed.Add((id!, new AgentIdentity(
                        name?.Trim() ?? id!,
                        identity.ValueKind == JsonValueKind.Object ? Str(identity, "emoji") : null,
                        identity.ValueKind == JsonValueKind.Object
                            ? DecodeDataUri(Str(identity, "avatarUrl"))
                            : null)));
                }

                lock (Gate)
                {
                    AgentNames.Clear();
                    Identities.Clear();

                    foreach (var (id, identity) in parsed)
                    {
                        AgentNames[id] = identity.Name;
                        Identities[id] = identity;
                    }
                }

                // Decoded here, on this background task, rather than the first
                // time an orb asks for one. OpenClawAvatars.For runs SkiaSharp
                // over every frame — 24 of them for the animated avatar here —
                // and the orb asks for it from inside the scan, which is the UI
                // thread. Warming it costs nothing extra and moves that work off
                // the thread that draws.
                foreach (var (id, identity) in parsed)
                {
                    if (identity.Avatar is not null) OpenClawAvatars.For(id, identity.Avatar);
                }
            }
            catch
            {
                // Names are a courtesy; without them the ids still identify a
                // session perfectly well.
            }
        }

        // "data:image/png;base64,iVBOR…" -> the bytes. Anything else, including a
        // real URL, is declined rather than fetched: this app has one connection
        // to one machine the user pointed it at, and quietly reaching out to
        // some other host because a field said so is not a thing it should do.
        internal static byte[]? DecodeDataUri(string? uri)
        {
            if (string.IsNullOrEmpty(uri)) return null;
            if (!uri.StartsWith("data:", StringComparison.Ordinal)) return null;

            var comma = uri.IndexOf(',');
            if (comma < 0) return null;

            var header = uri[..comma];
            if (!header.Contains(";base64", StringComparison.OrdinalIgnoreCase)) return null;

            try { return Convert.FromBase64String(uri[(comma + 1)..]); }
            catch { return null; }
        }

        // Excluded from coverage: subscribes to the gateway event stream.
        [ExcludeFromCodeCoverage]
        private static async Task SubscribeAsync(OpenClawGateway gateway, CancellationToken ct)
        {
            try
            {
                await gateway.RequestAsync("sessions.subscribe", new Dictionary<string, object>(), ct);
            }
            catch (Exception ex)
            {
                // Not fatal: the poll below still produces orbs, they just take
                // a scan longer to notice a new session.
                Report("connected, but couldn't subscribe: " + ex.Message);
            }
        }

        // Excluded from coverage: a sessions.list request over the live
        // connection; what it does with the reply is Parse, which is tested.
        [ExcludeFromCodeCoverage]
        private static async Task PollAsync(OpenClawGateway gateway, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var res = await gateway.RequestAsync("sessions.list", new Dictionary<string, object>(), ct);
                var (sessions, total) = Parse(res);

                _snapshot = sessions;
                Report(total == sessions.Count
                    ? $"Connected — {sessions.Count} session{(sessions.Count == 1 ? "" : "s")}."
                    : $"Connected — showing {sessions.Count} of {total}. The rest have been quiet "
                      + "for longer than the window above.");

                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }

        // `now` is a parameter with the real clock as its default, so the recency
        // filter below is decidable. Without it a test could only ever exercise
        // the "recent enough" arm — and the interesting behaviour is what the
        // filter lets through anyway: a session mid-run, and a room's membership.
        internal static (IReadOnlyList<Session> Sessions, int Total) Parse(
            JsonElement payload, DateTime? now = null)
        {
            var list = payload;
            if (payload.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "sessions", "items", "rows", "list" })
                {
                    if (payload.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
                    {
                        list = v;
                        break;
                    }
                }
            }

            if (list.ValueKind != JsonValueKind.Array) return (Array.Empty<Session>(), 0);

            var asOf = now ?? DateTime.UtcNow;
            var result = new List<Session>();

            // Carried with each member's last activity, because the order this
            // ends up in is load-bearing: a room orb draws the four members it
            // is given first (see RoomAvatar), and "the four the gateway
            // happened to list first" is not an answer anyone can read. Sorted
            // once below rather than by each caller, so the room's chat and the
            // room's picture are built from the same order.
            var roomMembers = new Dictionary<string, List<(string Key, DateTime Activity)>>(
                StringComparer.Ordinal);

            // The subset of those that got an orb — see where this is filled,
            // below the filters rather than above them.
            var roomParticipants = new Dictionary<string, List<(string Key, DateTime Activity)>>(
                StringComparer.Ordinal);

            var deliveries = new Dictionary<string, Delivery?>(StringComparer.Ordinal);

            // Every agent the gateway knows of, filtered or not, so that a
            // colour is reserved for one whose orb isn't drawn — their messages
            // still appear in a room, and an uncoloured bubble in a coloured
            // conversation reads as a failure rather than as an absence.
            var everyAgent = new List<string>();

            foreach (var s in list.EnumerateArray())
            {
                var key = Str(s, "key") ?? Str(s, "sessionKey");
                if (string.IsNullOrEmpty(key)) continue;

                var origin = s.TryGetProperty("origin", out var o) && o.ValueKind == JsonValueKind.Object
                    ? o
                    : default;

                var channel = origin.ValueKind == JsonValueKind.Object
                    ? Str(origin, "provider") ?? Str(s, "lastChannel") ?? ""
                    : Str(s, "lastChannel") ?? "";

                var state = StateFor(key);
                var activity = Activity(s, key);

                // Room membership is recorded *before* the recency filter, and
                // deliberately ignores it.
                //
                // Those are two different questions. "Which orbs are worth
                // showing" is about what you are working with now; "who is in
                // this channel" is about the conversation, and an agent that
                // spoke an hour ago is still one of the people in the room. With
                // this after the filter, Amber's session was dropped, her
                // transcript never loaded, and the "Nodes loaded" she posted
                // survived only as input to the others — anonymous, unmatchable,
                // and drawn as though you had said it.
                var roomKey = OpenClawSessionKind.RoomOf(key);
                if (roomKey is not null)
                {
                    if (!roomMembers.TryGetValue(roomKey, out var members))
                        roomMembers[roomKey] = members = new List<(string, DateTime)>();

                    members.Add((key, activity));
                }

                // ...and where it delivers, on the same terms and for the same
                // reason. Every session, not only a channel's: a DM whose orb
                // the recency filter dropped is still a conversation with an
                // address, and the mirror on an ordinary send has been quietly
                // skipping exactly those.
                deliveries[key] = DeliveryFor(s);

                everyAgent.Add(AgentIdOf(key) ?? key);

                // A session mid-run is current whatever its timestamps say —
                // it is the one thing an orb is most worth showing.
                var within = ActiveWithin;
                if (state != "generating" && within is not null && asOf - activity > within) continue;

                // Timer-driven sessions the user has asked not to see —
                // heartbeats, scheduled jobs, or both. Deliberately below the
                // two blocks above rather than at the top of the loop: a hidden
                // session is still a member of any room it stands in, and its
                // agent still needs a colour reserved for the bubbles it posts
                // there. That is the same distinction the recency filter draws,
                // for the same reason — "which orbs are worth showing" and "who
                // is in this conversation" are different questions.
                //
                // Not exempted while generating, unlike the recency rule above.
                // A heartbeat session is *always* about to be mid-run — that is
                // what a heartbeat is — so exempting it would make the setting
                // do nothing for exactly the sessions it exists to hide. The
                // same goes for a cron that is running when the poll lands.
                //
                // Asked of OrbClusters rather than of the two settings directly,
                // so the scan and the arrangement agree about what a session is:
                // a cron labelled "Cron: Heartbeat (main)" is both by the two
                // detectors, and it has to count as *one* of them in both
                // places or an orb gets kept here and then drawn in a shape the
                // user hid — or the reverse.
                var heartbeat = OpenClawHeartbeat.Is(key, Str(s, "label"));
                var kind = KindFor(s, origin, key);

                if (!OrbClusters.Visible(
                        OrbClusters.Of(heartbeat, kind), HeartbeatMode, CronMode))
                    continue;

                // Recorded here, past every filter above, rather than beside
                // the membership at the top of the loop — which is the whole
                // difference between the two, and the point this file already
                // makes twice: "who is in this channel" and "who is worth
                // drawing" are different questions with different answers.
                //
                // The room's *picture* wants the second one. Membership is
                // deliberately generous, holding every agent the gateway lists
                // for the channel however long ago it last spoke, because the
                // room's chat needs their transcript to merge. Cutting the pie
                // from that list put four faces on #social-media while two
                // agents were talking in it — the other two had sessions there
                // and nothing to say.
                if (roomKey is not null)
                {
                    if (!roomParticipants.TryGetValue(roomKey, out var standing))
                        roomParticipants[roomKey] = standing = new List<(string, DateTime)>();

                    standing.Add((key, activity));
                }

                result.Add(new Session(
                    key,
                    TitleFor(s, origin, key),
                    channel,
                    state,
                    activity,
                    DeliveryFor(s),
                    kind,
                    heartbeat));
            }

            AssignColours(everyAgent);

            // Prefixed, so a room and an agent that happen to share a name are
            // still two different things to the palette.
            var roomColours = AgentPalette.Assign(roomMembers.Keys.Select(k => "room:" + k))
                .ToDictionary(pair => pair.Key["room:".Length..], pair => pair.Value,
                    StringComparer.Ordinal);

            // Most recently active first, and ties broken on the key so the
            // answer is the same twice running. A gateway lists its sessions in
            // whatever order it likes and that order does move between polls;
            // an unstable one here would reshuffle a room orb's wedges under a
            // conversation that had not changed at all.
            static Dictionary<string, List<string>> Ordered(
                Dictionary<string, List<(string Key, DateTime Activity)>> rooms) =>
                rooms.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value
                        .OrderByDescending(member => member.Activity)
                        .ThenBy(member => member.Key, StringComparer.Ordinal)
                        .Select(member => member.Key)
                        .ToList(),
                    StringComparer.Ordinal);

            lock (Gate)
            {
                _roomParticipants = Ordered(roomParticipants);
                _roomMembers = Ordered(roomMembers);
                _roomColours = roomColours;
                _deliveries = deliveries;
            }

            return (result, list.GetArrayLength());
        }

        // What kind of thing this session is: a scheduled job, a private
        // conversation, or one in a room with other people in it.
        //
        // Worth telling apart because they are not the same kind of object at
        // all — "Zara — general" and "Zara — wtvamp" read identically today,
        // and one of them is a channel anyone can see while the other is a DM.
        // A cron session is further still: nobody is on the other end of it.
        //
        // Two sources, deliberately in this order. The key is structural and
        // always present — `agent:<name>:cron:<uuid>` cannot be anything but a
        // cron job — while origin.chatType is the gateway's own word for a
        // conversation and is the only thing that separates a DM from a
        // channel. Where the key is uninformative (`agent:main:discord:…`),
        // chatType decides; where chatType is missing, the key's fourth segment
        // carries the same word.
        // chatType is on the session itself as well as inside origin, and the
        // top-level one is preferred: origin describes where a conversation came
        // from and is absent on 12 of the 70 sessions this was measured against,
        // while chatType is the gateway's own answer to the question being asked.
        internal static SessionKind KindFor(JsonElement session, JsonElement origin, string key) =>
            OpenClawSessionKind.From(key, Str(session, "chatType") ?? Str(origin, "chatType"));

        // What to call a session. Two halves: who is talking, and where.
        //
        // The session key is "agent:<id>:<surface>[:<type>:<id>]", and the id is
        // what somebody's config happens to call that agent — "main",
        // "comfyui". The names their owner actually uses for them live in
        // agents.list: Lilibeth, Zara. Four orbs showing "M" because four agents
        // have ids starting with main is worse than L, Z, A, so the name wins
        // whenever there is one.
        //
        // The second half is needed because one agent commonly has a DM with
        // you, a DM with somebody else and two channels at once, and repeating
        // "Lilibeth — discord" four times identifies nothing.
        internal static string TitleFor(JsonElement session, JsonElement origin, string key)
        {
            var label = Str(session, "label");
            var parts = key.Split(':');

            if (parts.Length >= 3 && parts[0] == "agent")
            {
                var agent = parts[1];
                var surface = parts[2];

                string name;
                lock (Gate) name = AgentNames.GetValueOrDefault(agent, agent);

                // A cron session is best identified by its job; everything else
                // by where the conversation is happening. "Cron: " is dropped
                // because the name after it already says that.
                var detail = !string.IsNullOrWhiteSpace(label)
                    ? label!.StartsWith("Cron: ", StringComparison.OrdinalIgnoreCase)
                        ? label![6..]
                        : label!
                    : Group(session) ?? Where(origin) ?? surface;

                return string.Equals(name, detail, StringComparison.OrdinalIgnoreCase)
                    ? name
                    : $"{name} — {detail}";
            }

            if (!string.IsNullOrWhiteSpace(label)) return label!;

            if (origin.ValueKind == JsonValueKind.Object)
            {
                var originLabel = Str(origin, "label");
                if (!string.IsNullOrWhiteSpace(originLabel)) return originLabel!;
            }

            return key;
        }

        // The channel's name, as the gateway already writes it: "#general".
        //
        // Where() below reconstructs the same thing out of origin.label by
        // cutting at " id:" and stripping the noun that introduces it, which was
        // necessary before anyone looked at what else sessions.list carries.
        // This field needs none of that and cannot be thrown off by a label
        // whose shape changes, so it is asked first and Where is the fallback.
        internal static string? Group(JsonElement session)
        {
            var group = Str(session, "groupChannel");
            return string.IsNullOrWhiteSpace(group) ? null : group!.Trim();
        }

        // origin.label is written for a log, not for a person: "#general channel
        // id:1474991965354463274", "wtvamp user id:246722755112861696",
        // "discord:amber". The useful part is always at the front, so cut at the
        // id and drop the noun that introduces it.
        internal static string? Where(JsonElement origin)
        {
            if (origin.ValueKind != JsonValueKind.Object) return null;

            var label = Str(origin, "label");
            if (string.IsNullOrWhiteSpace(label)) return null;

            var text = label!;

            var id = text.IndexOf(" id:", StringComparison.Ordinal);
            if (id > 0) text = text[..id];

            foreach (var noun in new[] { " channel", " user", " group" })
            {
                if (text.EndsWith(noun, StringComparison.OrdinalIgnoreCase))
                {
                    text = text[..^noun.Length];
                }
            }

            // "discord:amber" — the surface is already in the title if it is
            // going to be, so only the name after it is worth keeping.
            var colon = text.LastIndexOf(':');
            if (colon >= 0 && colon < text.Length - 1) text = text[(colon + 1)..];

            text = text.Trim();
            return text.Length == 0 ? null : text;
        }

        // deliveryContext is the authoritative answer; lastChannel/lastTo are
        // what the gateway itself falls back to, so this falls back the same
        // way rather than inventing its own rule.
        internal static Delivery? DeliveryFor(JsonElement session)
        {
            string? channel = null, to = null, account = null;

            if (session.TryGetProperty("deliveryContext", out var context)
                && context.ValueKind == JsonValueKind.Object)
            {
                channel = Str(context, "channel");
                to = Str(context, "to");
                account = Str(context, "accountId");
            }

            channel ??= Str(session, "lastChannel");
            to ??= Str(session, "lastTo");
            account ??= Str(session, "lastAccountId");

            return string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(to)
                ? null
                : new Delivery(channel!, to!, account);
        }

        internal static string StateFor(string key)
        {
            lock (Gate)
            {
                if (!Running.TryGetValue(key, out var last)) return "idle";
                if (DateTime.UtcNow - last > RunIdle)
                {
                    Running.Remove(key);
                    return "idle";
                }

                return "generating";
            }
        }

        // The later of what the gateway claims and what we have watched happen.
        // Ours wins whenever the two disagree, because ours came from an event
        // the session actually emitted.
        private static DateTime Activity(JsonElement session, string key)
        {
            var ms = Math.Max(Num(session, "lastActivityAt"), Num(session, "updatedAt"));
            var reported = ms <= 0
                ? DateTime.UtcNow
                : DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

            lock (Gate)
            {
                if (LastSeen.TryGetValue(key, out var seen) && seen > reported) return seen;
            }

            return reported;
        }

        // Every event that names a session is evidence that session is working.
        // The key on an event is run-scoped — "…:run:<runId>" appended to the
        // session's own key — so it has to be trimmed back before it means
        // anything to the list.
        // internal: this is the whole of how a gateway orb learns it is working.
        // The gateway's session list does not carry a running state, so an orb
        // pulses because an event named its session — and the effect is
        // observable through Parse, which reads what this records.
        internal static void OnEvent(string name, JsonElement payload)
        {
            if (name is "tick" or "health" or "presence" or "connect.challenge") return;
            if (payload.ValueKind != JsonValueKind.Object) return;

            var key = Str(payload, "sessionKey");
            if (string.IsNullOrEmpty(key)) return;

            var run = key.IndexOf(":run:", StringComparison.Ordinal);
            if (run > 0) key = key[..run];

            OpenClawChatSession? chat;

            lock (Gate)
            {
                Chats.TryGetValue(key, out chat);

                // A finished run stops counting immediately rather than waiting
                // out RunIdle — the gateway said so, which beats inferring it.
                // Seen is recorded for every event including the one that ends a
                // run: a conversation that just finished replying is exactly the
                // one worth keeping on screen.
                LastSeen[key] = DateTime.UtcNow;

                if (name is "cron" && Str(payload, "action") == "finished") Running.Remove(key);
                else Running[key] = DateTime.UtcNow;
            }

            // Only for a session someone has opened: building a transcript for
            // 59 sessions nobody is looking at would be work and memory spent on
            // nothing. Marshalled here so every implementation of
            // IRemoteChatSession can promise its events arrive on the UI thread.
            if (chat is not null)
            {
                Dispatcher.UIThread.Post(() => chat.OnAgentEvent(name, payload));
            }
        }

        // Sends a reply into a session. Fails loudly rather than silently: the
        // panel puts whatever comes back in front of the person who typed it,
        // because a message that didn't arrive and didn't say so is the worst
        // outcome a chat window can produce.
        // Excluded from coverage: sends a message to a real gateway.
        [ExcludeFromCodeCoverage]
        public static async Task SendAsync(OpenClawChatSession chat, string text, CancellationToken ct)
        {
            OpenClawGateway? gateway;
            lock (Gate) gateway = _gateway;

            if (gateway is null) throw new IOException("not connected to the gateway");

            // Post what you typed into the conversation first, then ask the
            // agent to answer it.
            //
            // The gateway only ever delivers the *agent's* side to a channel —
            // it assumes your side arrived from that channel in the first
            // place, which is true right up until you type it somewhere else.
            // Left alone, Discord shows an answer with no question above it.
            //
            // Ordering matters and is why this is awaited: the reply can come
            // back fast, and a mirror that lands after it puts the question
            // below its own answer.
            if (chat.Delivery is { } delivery)
            {
                try
                {
                    var mirror = new Dictionary<string, object>
                    {
                        ["to"] = delivery.To,
                        // The same constant the recognizer reads back, not a
                        // second literal saying the same thing. Two of them is
                        // how a mirror stops being recognisable as ours — which
                        // it silently was, for one release.
                        ["message"] = OpenClawSender.MirrorPrefix + text,
                        ["channel"] = delivery.Channel,
                        ["idempotencyKey"] = Guid.NewGuid().ToString()
                    };

                    if (!string.IsNullOrWhiteSpace(delivery.AccountId))
                    {
                        mirror["accountId"] = delivery.AccountId!;
                    }

                    await gateway.RequestAsync("send", mirror, ct);
                }
                catch
                {
                    // A mirror that fails must not eat the message. The reply
                    // still goes through and still gets delivered; the Discord
                    // log is just missing the prompt, which is where this
                    // started.
                }
            }

            await gateway.RequestAsync("chat.send", new Dictionary<string, object>
            {
                ["sessionKey"] = chat.GatewayKey,
                ["message"] = text,

                // Without this the gateway routes the reply to its internal
                // channel — the agent answers, the transcript records it, and
                // nothing ever reaches Discord. Its own routing reads:
                //
                //   if (!(params.deliver === true)) return { originatingChannel: INTERNAL_MESSAGE_CHANNEL, … }
                //   const sessionDeliveryContext = deliveryContextFromSession(entry)
                //
                // so `true` is what makes it look up where this conversation
                // actually lives and deliver there. We don't have to carry the
                // channel or recipient ourselves; the session already knows, and
                // the gateway is connected to Discord whether or not anything is
                // open on this machine.
                //
                // Always on rather than a choice: a reply typed into a Discord
                // conversation is a reply *to* that conversation. Anything else
                // would be a message that looked sent and wasn't.
                ["deliver"] = true,

                // Side-effecting methods want one of these; a fresh id per send
                // means a retry after a timeout can't post the message twice.
                ["idempotencyKey"] = Guid.NewGuid().ToString()
            }, ct);
        }

        // --- posting to a room -------------------------------------------

        // The three ways a room send can fail, as the sentence the room writes
        // into its own transcript.
        //
        // Three sentences rather than one, because they are three different
        // truths and the difference is exactly what the person needs: nothing
        // was sent, nothing was sent and here is why, or it went to the channel
        // and only the handoff to an agent failed. A single "couldn't send"
        // covering all three would leave someone re-typing a message that is
        // already in the channel.
        //
        // Pure and separate from the request that produces them for the reason
        // OpenClawChatSession.SendOrFailureAsync is: the network half needs a
        // live gateway and is excluded, and the wording is the half a person
        // reads and a test can check.
        //
        // `room` is the room's display name at runtime — "#general" — because
        // that is what the person typed into and what they will look for in
        // Discord. Passed rather than derived: the key is "discord:<id>", which
        // names nothing anybody recognises.
        internal static string NoAddressInRoom(string room) =>
            $"Couldn't post to {room}: no member of this channel carries a delivery address.";

        internal static string PostFailed(string room, string detail) =>
            $"Couldn't post to {room}: {detail}. Nothing was sent.";

        internal static string HandoffFailed(string room, string agent, string detail) =>
            $"Posted to {room}, but couldn't hand it to {agent}: {detail}.";

        // Posts a message to a channel and then asks one agent in it to answer.
        //
        // Both halves, always, in that order — which is the fix CB-27 asked for
        // and is worth the reasoning, because each half alone looks sufficient
        // and neither is:
        //
        // A channel post on its own would be read by every agent in the room
        // except one: the gateway suppresses a bot account's own channel post
        // from that account's own sessions, so the carrier — the very session we
        // are about to hand the message to — is the one member deaf to it.
        //
        // A `chat.send` on its own is what the bug was. The gateway delivers the
        // *agent's* side to the channel and assumes your side arrived from there
        // in the first place, so a message typed here reaches one agent
        // privately and nobody in the channel ever sees the question.
        //
        // The mirror goes under the **carrier's own** accountId, and that is the
        // load-bearing detail. Under any other account the carrier would receive
        // the post as an ordinary channel message *and* the chat.send, and
        // answer twice; under its own, the gateway's self-suppression is what
        // makes the pair arrive exactly once. Measured on a completed room send:
        // the carrier saw the chat.send input alone, three other members each
        // saw the prefixed mirror once, six agents woke and replied within
        // eleven seconds, and a message mentioning nobody woke one anyway.
        //
        // A failed mirror aborts the send, unlike the best-effort mirror on an
        // ordinary single-session send. There the mirror is a convenience — the
        // conversation already lives in that DM and the agent's reply is
        // delivered to it either way. Here it is the whole point: a chat.send
        // that goes through without it is precisely the silent private delivery
        // this ticket is about, and doing it anyway would reintroduce the bug on
        // the one path most likely to hit it.
        //
        // Writes to no transcript. The room owns what a room send looks like,
        // including its failures, and a note written into a member's transcript
        // is invisible in the merge — which drops System turns.
        //
        // Not excluded, unlike every other method here that talks to a gateway.
        // The connection is a constructor argument on OpenClawGateway and
        // SetGatewayForTests hands one in, so both requests, both failures and
        // the order between them are all reachable over an in-memory socket —
        // and the claim this whole fix rests on, that the mirror goes out under
        // the carrier's own account, is exactly the kind of claim that must not
        // sit behind an exclusion.
        internal static async Task<string?> SendToRoomAsync(
            OpenClawChatSession carrier, string room, string agent, string text,
            CancellationToken ct)
        {
            OpenClawGateway? gateway;
            lock (Gate) gateway = _gateway;

            if (gateway is null) return PostFailed(room, "not connected to the gateway");

            var delivery = carrier.Delivery;
            if (delivery is null) return NoAddressInRoom(room);

            try
            {
                var mirror = new Dictionary<string, object>
                {
                    ["to"] = delivery.To,
                    ["message"] = OpenClawSender.MirrorPrefix + text,
                    ["channel"] = delivery.Channel,
                    ["idempotencyKey"] = Guid.NewGuid().ToString()
                };

                if (!string.IsNullOrWhiteSpace(delivery.AccountId))
                {
                    mirror["accountId"] = delivery.AccountId!;
                }

                await gateway.RequestAsync("send", mirror, ct);
            }
            catch (Exception ex)
            {
                return PostFailed(room, ex.Message);
            }

            try
            {
                await gateway.RequestAsync("chat.send", new Dictionary<string, object>
                {
                    ["sessionKey"] = carrier.GatewayKey,
                    ["message"] = text,
                    ["deliver"] = true,
                    ["idempotencyKey"] = Guid.NewGuid().ToString()
                }, ct);
            }
            catch (Exception ex)
            {
                // The channel already has it, so this is not "nothing was
                // sent". Saying so matters: the alternative wording would have
                // someone post the same message a second time.
                return HandoffFailed(room, agent, ex.Message);
            }

            return null;
        }

        // One agent messaging another arrives as a user turn with a machine
        // header glued to the front:
        //
        //   [Inter-session message] sourceSession=agent:comfyui:discord:direct:2467…
        //   sourceChannel=discord sourceTool=sessions_send isUser=false <the actual message>
        //
        // Left as-is, a transcript in a multi-agent setup is mostly routing
        // metadata. It isn't noise to be dropped though — it is one of your
        // agents talking — so the header is replaced by the thing it was
        // actually saying, attributed to whoever said it.
        // One page of chat.history turned into turns the panel can draw.
        //
        // Extracted from the request that fetches it, which needs a live gateway
        // and is excluded for that. This is the half that reads a format nobody
        // here controls, so it is the half that has to be tested against
        // fixtures — the same reasoning that keeps ChatTranscript and
        // CodexTranscript pure.
        // The text of one message, whichever of the three shapes its content
        // arrived in. Factored out because the page has to be read twice: once
        // for the paths in it, before any turn is built, and then again to
        // build them. Two spellings of "the text of this message" would be two
        // things to keep in step.
        internal static string TextOf(JsonElement content) => content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? "",

            JsonValueKind.Array => string.Join("\n", content.EnumerateArray()
                .Where(b => Str(b, "type") == "text")
                .Select(b => Str(b, "text"))
                .Where(t => !string.IsNullOrWhiteSpace(t))),

            JsonValueKind.Object => Str(content, "text") ?? "",

            _ => ""
        };

        internal static List<HistoryTurn> TurnsFromHistory(JsonElement messages)
        {
            var turns = new List<HistoryTurn>();

            // Read the whole page for paths before building any turn. A
            // delivered picture's own record names only the file, and the
            // directory it lives in is somewhere else on the page — see
            // MediaPathsByFileName, including why this is each message's raw
            // JSON rather than the text this loop goes on to render.
            var mediaPaths = MediaPathsByFileName(
                messages.EnumerateArray().Select(m => m.GetRawText()));

            // Which sources each picture-drawing arm claimed, so a file drawn
            // by both can be collapsed once the page is read. See the end of
            // this method for why the mirror copy is the one that goes.
            //
            // Indices rather than turn values because a HistoryTurn is a
            // struct: two identical mirror turns would be equal by value and
            // there would be no way to say which one to remove.
            // Counted rather than a set, because one named turn cancels one
            // mirror and not every mirror of that file. A page carrying two
            // deliveries of a picture the agent also named by path has one
            // cross-arm pair and one genuine second delivery, and a set would
            // swallow both.
            var namedSources = new Dictionary<string, int>(StringComparer.Ordinal);
            var mirrorDrawn = new List<(int Index, string Source)>();

            foreach (var message in messages.EnumerateArray())
            {
                var role = Str(message, "role") == "user" ? ChatRole.User : ChatRole.Assistant;

                // content is a list of blocks; only the text ones are worth
                // showing. Tool calls arrive live as their own turns, and a
                // replayed tool_use block would be a wall of JSON.
                if (!message.TryGetProperty("content", out var content)) continue;

                // The two roles are shaped differently, which is easy to miss
                // and silently drops half the conversation: an assistant turn
                // carries `content` as a list of blocks, and a user turn
                // carries it as a plain string. Reading only the block form
                // showed an agent talking to nobody.
                // Pictures are their own turns rather than being folded into
                // the text of one. A message is commonly several images and
                // nothing else, and a bubble containing four of them stacked
                // reads worse than four bubbles.
                if (content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in content.EnumerateArray())
                    {
                        if (Str(block, "type") != "image") continue;

                        // Two shapes, and the inline one is the shape this
                        // gateway actually emits: every real image block in
                        // its own stored transcripts is
                        // `{type:"image", data:"<base64>", mimeType:...}`
                        // with no url whatsoever. Reading only the url form
                        // silently dropped every picture the gateway ever
                        // sent — CB-91, and the oldest of the picture bugs.
                        //
                        // The url form is kept rather than replaced: it costs
                        // one branch, other deployments (or a later gateway)
                        // may well send it, and nothing here can tell which
                        // it is going to get.
                        // Whitespace is not a url, and normalising it to null
                        // here rather than leaving it for the panel matters:
                        // the panel asks IsNullOrEmpty, so a "   " would send
                        // it fetching nothing and it would never look at the
                        // bytes sitting beside it. One spelling of "no url"
                        // for both this parser and everything downstream.
                        var url = Str(block, "url");
                        if (string.IsNullOrWhiteSpace(url)) url = null;

                        var bytes = url is null ? InlineImageBytes(block) : null;
                        if (url is null && bytes is null) continue;

                        var ms2 = Num(message, "timestamp");
                        turns.Add(new HistoryTurn(role, "", url, Str(block, "alt") ?? "",
                            ms2 <= 0
                                ? DateTimeOffset.Now
                                : DateTimeOffset.FromUnixTimeMilliseconds(ms2).ToLocalTime(),
                            null, null, false, bytes));
                    }
                }

                var text = TextOf(content);

                if (string.IsNullOrWhiteSpace(text)) continue;

                text = Readable(text, out var speakerId);
                if (string.IsNullOrWhiteSpace(text)) continue;

                var ms = Num(message, "timestamp");
                var at = ms > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime()
                    : DateTimeOffset.Now;

                var speaker = speakerId is null ? null : AgentNameOf(speakerId);
                var colour = speakerId is null ? null : ColourForAgent(speakerId);
                var mine = false;

                // Only the user role is in question. An assistant turn is the
                // agent whose transcript this is — that is the whole reason the
                // room merge works — and asking who sent it would be asking a
                // question that has already been answered.
                //
                // After Readable rather than before it, and on Readable's
                // result: an inter-session message reaches here with its machine
                // header already replaced by what it was actually saying and its
                // speaker already identified, and running the prefix test
                // against the raw header would only ever miss.
                if (role == ChatRole.User)
                {
                    var meta = message.TryGetProperty("__openclaw", out var oc)
                        && oc.ValueKind == JsonValueKind.Object
                            ? oc
                            : default;

                    var sender = OpenClawSender.Classify(
                        Bool(meta, "senderIsOwner"),
                        Str(meta, "senderName"),

                        // Both places carry it; the message's own copy is asked
                        // first because __openclaw is the undocumented half and
                        // the one likelier to move.
                        Str(message, "idempotencyKey") ?? Str(meta, "idempotencyKey"),
                        text);

                    text = sender.Text;
                    mine = sender.Kind == OpenClawSender.SenderKind.Mine;

                    // A name only where Readable did not already find a better
                    // one. Its answer is an agent *id*, which resolves to the
                    // name and colour this app draws that agent's orb in;
                    // senderName is a Discord display name, which does not.
                    //
                    // So the colour stays null for a Named sender, deliberately.
                    // The chip falls back to initials, which is the honest
                    // answer for somebody we cannot match to an agent — a
                    // borrowed colour would say two different speakers were the
                    // same one.
                    if (speaker is null && sender.Kind == OpenClawSender.SenderKind.Named)
                    {
                        speaker = sender.Name;
                    }
                }

                if (string.IsNullOrWhiteSpace(text)) continue;

                // A picture the gateway actually delivered somewhere. Its own
                // record of having done so is all a client ever sees of it —
                // see DeliveredPictureName for why that record, rather than
                // anything the agent wrote, is the signal worth trusting.
                var delivered = DeliveredPictureName(Str(message, "model"), text);
                if (delivered is not null)
                {
                    // Carried as the route rather than as a path, because that
                    // is what ImageUrl already means to everything downstream:
                    // FetchMediaAsync uses it as the GET path and the panel
                    // needs no new branch to draw it.
                    //
                    // The path off the page where the page has one, and the
                    // shared media directory only where it does not. The
                    // difference is not cosmetic: the mirror record's filename
                    // is a basename with the directory stripped, so the
                    // fallback is a guess that is right for a file an agent
                    // copied into the shared directory and wrong for one that
                    // lives anywhere else.
                    var source = mediaPaths.TryGetValue(delivered, out var known)
                        ? known
                        : SharedMediaDir + delivered;

                    // The filename is kept as the turn's text, not dropped for
                    // a cleaner picture-only bubble. Nothing here can know
                    // whether the fetch will succeed — the fallback may be
                    // wrong, a gateway under --profile has a different media
                    // root, and a file can be cleaned up — and a turn with an
                    // unresolvable url and no text is an empty bubble, which is
                    // worse than the bare filename this shows today. So it
                    // degrades to exactly today's appearance instead. Text
                    // beside a thumbnail is already what CB-88's MEDIA:
                    // pictures render as.
                    mirrorDrawn.Add((turns.Count, source));
                    turns.Add(new HistoryTurn(
                        role, delivered,
                        AssistantMediaRoute + Uri.EscapeDataString(source),
                        delivered, at, speaker, colour, mine));
                    continue;
                }

                // A picture the agent named by path — CB-101.
                //
                // CB-88 taught LocalMediaPathFrom to recognise this and wired
                // it into the live stream only, so the convention worked while
                // a reply was arriving and not when the same message was read
                // back. That is every reopen, every reconnect and every scroll
                // — which is to say, almost always.
                //
                // Warren's two screenshots caught the asymmetry three minutes
                // apart: a delivered picture drew a thumbnail (the branch
                // above) and a MEDIA: line drew its own text. The file was on
                // disk and the gateway served it on request; only this parser
                // never asked.
                //
                // After the delivery-mirror branch. The two arms cannot both
                // fire for one *message* — a mirror's text is a bare filename,
                // which LooksLikeAnImagePath refuses for not being rooted —
                // but they can each fire on a different message of the same
                // page and land on the same file. That is what namedSources
                // below is for.
                var namedCandidate = LocalMediaPathFrom(text);
                if (namedCandidate is not null)
                {
                    // A bare filename resolves against this page's own
                    // harvested paths (CB-94) before falling back to a guess
                    // — see ResolveLocalMediaPath's own comment.
                    var named = ResolveLocalMediaPath(namedCandidate, mediaPaths);
                    namedSources[named] = namedSources.GetValueOrDefault(named) + 1;

                    // Text kept and the picture beside it, which is the shape
                    // the live path already produces — TryResolveLocalMedia
                    // sets bytes on a turn that keeps its prose. So a fetch
                    // that cannot succeed degrades to exactly what this
                    // rendered before, rather than to an empty bubble.
                    turns.Add(new HistoryTurn(role, text.Trim(),
                        AssistantMediaRoute + Uri.EscapeDataString(named),
                        named[(named.LastIndexOf('/') + 1)..],
                        at, speaker, colour, mine));
                    continue;
                }

                turns.Add(new HistoryTurn(role, text.Trim(), null, "", at,
                    speaker, colour, mine));
            }

            // One delivery, two arms, one bubble — CB-98's cross-arm case,
            // which turned out to be live rather than latent.
            //
            // A page can carry both an agent's own message naming a file and
            // the gateway's mirror of having delivered it. CB-94 recovers the
            // mirror's directory from that very path, so the two arms resolve
            // to the *identical* source and draw the same picture twice, back
            // to back. QA measured two instances in the real corpus and both
            // load — this is not the refused-and-degraded case CB-98 first
            // described.
            //
            // The mirror copy is the one dropped, not the named one. In the
            // general shape the named turn carries the agent's prose — "here
            // you go", a question about the picture — where the mirror turn's
            // entire content is the filename the picture above already shows.
            // Dropping the richer bubble to keep the barer one would be the
            // wrong way round.
            //
            // Two *mirrors* for one file are deliberately left alone: those are
            // two separate deliveries with distinct records and timestamps
            // (36 seconds apart in one measured case, 47 minutes in another),
            // and collapsing them would hide an event the gateway recorded.
            // Only a cross-arm pair is one event seen twice.
            // One named turn cancels one mirror. Written as a budget rather
            // than a membership test because "drop every mirror of this file"
            // would silently eat a real second delivery on a page that has
            // both — the very thing the paragraph above preserves. QA found
            // that edge by reading the rule rather than the corpus, where it
            // does not occur.
            //
            // Chosen earliest-first, so the mirror that pairs with the named
            // turn is the one that goes and any later delivery keeps its own
            // timestamp. Removed highest-index-first afterwards so no earlier
            // index is invalidated on the way.
            if (namedSources.Count > 0)
            {
                var doomed = new List<int>();

                foreach (var (index, source) in mirrorDrawn)
                {
                    if (namedSources.GetValueOrDefault(source) == 0) continue;

                    namedSources[source]--;
                    doomed.Add(index);
                }

                for (var i = doomed.Count - 1; i >= 0; i--) turns.RemoveAt(doomed[i]);
            }

            return turns;
        }

        // The picture out of an image block that carried it inline. Real
        // blocks from this gateway put the whole thing in `data` as base64
        // with a `mimeType` beside it (CB-91).
        //
        // mimeType is deliberately not read: Avalonia's decoder sniffs the
        // format off the bytes themselves, so trusting a declared type would
        // only add a way to be wrong. Both the bare-base64 and the
        // `data:image/...;base64,` spellings are accepted, because this
        // gateway genuinely uses both — bare in these blocks, the data: form
        // in agents.list's avatarUrl, which DecodeDataUri already exists for.
        internal static byte[]? InlineImageBytes(JsonElement block)
        {
            var data = Str(block, "data");
            if (string.IsNullOrWhiteSpace(data)) return null;

            byte[]? bytes;
            try
            {
                bytes = DecodeDataUri(data) ?? Convert.FromBase64String(data!);
            }
            catch
            {
                // Not base64 at all. A block we cannot read is a picture that
                // does not show; the turn's text still does.
                return null;
            }

            // Zero bytes is not a picture, and the guard belongs here rather
            // than on the bare-base64 arm alone. `data:image/png;base64,` — an
            // empty payload — decodes through DecodeDataUri to a real,
            // zero-length array, and that is the *only* way this is reachable:
            // Convert.FromBase64String skips exactly ' ', tab, CR and LF, every
            // one of which IsNullOrWhiteSpace above already rejects, so the bare
            // path can never hand back an empty array. Guarding only that arm
            // therefore let the real case through as a turn with no text, no url
            // and no drawable picture — an empty bubble — while making the guard
            // itself unexecutable.
            return bytes.Length == 0 ? null : bytes;
        }

        // Which of a freshly-fetched page's turns is the picture a live reply
        // was talking about. There is nothing to join on but time: a live
        // "agent" event carries no id that also appears in a chat.history
        // message, so the turn whose timestamp is nearest the live one's is
        // the answer — the two are, at most, one round trip apart. Pure and
        // taking plain HistoryTurns rather than a page fetch, so
        // TryResolveLiveImage's actual gateway call is the only excluded half.
        //
        // Filtered to the live turn's own role first: a session's chat.history
        // mixes that agent's own replies with everyone else's messages
        // arriving as its input (a room's other agents, or a real person —
        // see OpenClawRoomChat's own header comment on that shape), and the
        // nearest picture in time is not necessarily the agent's own picture
        // once other traffic can land on the same page. Restricting to the
        // matching role is what keeps a busy room from occasionally handing
        // an agent's reply somebody else's attachment.
        // Either kind of picture counts as a match, not just a url-bearing
        // one: CB-87 was written filtering on ImageUrl alone, which on this
        // gateway can never match, because its blocks carry bytes inline and
        // no url at all (CB-91). A live reply reconciling against "the
        // nearest picture" has to mean either shape or it means nothing here.
        internal static HistoryTurn? BestImageMatch(
            IEnumerable<HistoryTurn> turns, ChatRole role, DateTimeOffset near) =>
            turns.Where(t => t.Role == role
                             && (!string.IsNullOrEmpty(t.ImageUrl) || t.ImageBytes is { Length: > 0 }))
                 .OrderBy(t => Math.Abs((t.At - near).Ticks))
                 // HistoryTurn is a struct, so a plain FirstOrDefault on an
                 // empty sequence returns a zeroed HistoryTurn — a real value,
                 // not null. Boxing into the nullable first is what makes "no
                 // match" actually come back as null.
                 .Select(t => (HistoryTurn?)t)
                 .FirstOrDefault();

        // A picture an agent generated itself and references by its own path
        // on the gateway host — a second convention alongside
        // MediaAttachedMarker, and a different one: that marker names
        // something a *person* attached, staged under the gateway's own
        // inbound directory and already reachable through FetchMediaAsync's
        // ordinary URL fetch. This is an agent's own local file, named by
        // "MEDIA:<path>" in CB-88's captured real traffic (confirmed via
        // tools/openclaw-probe against a live gateway, not assumed). It names
        // a file rather than a url, so FetchLocalMediaAsync reads it through
        // the gateway's own read-scoped media route — see that method for why
        // the admin-gated media.get RPC this used to call was the wrong
        // endpoint (CB-90).
        //
        // The second arm — the whole message being nothing but a path — is a
        // real observed shape too: the same automation's duplicate-post bug
        // (before Warren asked it fixed) left a bare path as an entire
        // assistant turn, with no MEDIA: prefix at all. Matched only when the
        // ENTIRE trimmed text is the path, so an ordinary sentence that
        // happens to mention a ".png" in passing is never mistaken for one.
        internal const string LocalMediaMarker = "MEDIA:";

        private static readonly string[] ImageExtensions =
            { ".png", ".jpg", ".jpeg", ".gif", ".webp" };

        // Returns a rooted path (starting with "/" or "~/") or, since CB-107,
        // a bare filename with no directory at all — the caller decides how
        // to turn either into something fetchable (ResolveLocalMediaPath).
        internal static string? LocalMediaPathFrom(string text)
        {
            // A line of its own, not necessarily the first line: the real
            // captured example (CB-88) has two paragraphs of in-character
            // reply before the MEDIA: line, so anchoring on the start of the
            // whole message would miss the one real case this exists for.
            //
            // Validated the same way as the bare-path arm below rather than
            // trusting anything after the prefix — QA (CB-88) found that an
            // ordinary sentence starting a line with "MEDIA:" ("MEDIA: is a
            // broad term...") would otherwise extract "is a broad term..." as
            // a "path" and fire a real request for it.
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (!line.StartsWith(LocalMediaMarker, StringComparison.Ordinal)) continue;

                var path = line[LocalMediaMarker.Length..].Trim();
                return LooksLikeAnImagePath(path) ? path : null;
            }

            var trimmed = text.Trim();
            if (LooksLikeAnImagePath(trimmed)) return trimmed;

            // CB-107: an agent's caption can pair descriptive prose with the
            // file rather than sending it alone — a caption line followed by
            // a bare filename on the next line, or a caption and a path
            // trailing on the same line. The two checks above only ever
            // matched a message that was *nothing but* the path, so neither
            // fired and the picture rendered as plain text.
            //
            // Scanning the trailing whitespace-separated token catches both
            // shapes (a bare filename on its own final line is also the last
            // token of the whole message) without loosening the checks
            // above: an ordinary sentence would have to happen to *end* with
            // something extension-shaped, which is a much narrower accident
            // than "mentions a .png anywhere".
            var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return null;

            var last = tokens[^1];
            if (LooksLikeAnImagePath(last)) return last;

            // A bare filename alone, with nothing else in the message, already
            // failed the whole-message check above and stays plain text — an
            // agent naming a file with no caption around it is exactly the
            // ambiguous case DeliveredPictureName exists for, trusted only
            // once the gateway's own delivery-mirror record confirms it, not
            // from prose alone (AnOrdinaryTurnNamingAFileStaysText). Paired
            // with a caption, it's the CB-107 shape and is returned
            // unresolved; ResolveLocalMediaPath is what turns this into
            // something fetchable, the same way the delivery-mirror branch
            // already does for its own bare filenames.
            return tokens.Length > 1 && LooksLikeABareImageFilename(last) ? last : null;
        }

        // No directory separator at all, as opposed to LooksLikeAnImagePath's
        // rooted paths. Deliberately narrower than "no slash": a filename
        // with a space in it would already have failed the whitespace-token
        // split above, so the checks here are about the extension and
        // nothing else being present.
        private static bool LooksLikeABareImageFilename(string text) =>
            text.Length > 0
            && !text.Contains('/')
            && !text.Contains('\\')
            && Array.Exists(ImageExtensions, ext => text.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

        // Turns whatever LocalMediaPathFrom found into something actually
        // fetchable. A rooted path is already that; a bare filename is the
        // same guess DeliveredPictureName's own branch makes — a known
        // directory from this page's mediaPaths where CB-94's JSON harvest
        // found one, the shared media directory otherwise. Callers with no
        // page to harvest from (the live stream — see OpenClawChatSession)
        // pass null and get the guess alone, which is right for the same
        // reason it is right there: a wrong guess costs nothing but an
        // "unavailable" fetch, and the filename stays as the turn's text.
        internal static string ResolveLocalMediaPath(
            string candidate, IReadOnlyDictionary<string, string>? mediaPaths)
        {
            if (candidate.StartsWith('/') || candidate.StartsWith("~/", StringComparison.Ordinal))
                return candidate;

            if (mediaPaths is not null && mediaPaths.TryGetValue(candidate, out var known))
                return known;

            return SharedMediaDir + candidate;
        }

        // `~/` as well as `/` (CB-97). The gateway expands a leading tilde
        // itself — `resolveLocalMediaPath` calls `resolveUserPath` on one —
        // and it is the form an agent naturally writes, so rejecting it threw
        // away pictures the gateway would have served happily. Confirmed by
        // probe: `~/.openclaw/media/browser/03a1be83-….png` answers
        // `available:true`.
        //
        // `..` refused outright (CB-89), and `//` with it. This builds a
        // gateway request out of a string an agent wrote into a transcript,
        // and refusing traversal is cheaper than reasoning about what it
        // resolves to on a host this process cannot see. `//host/a.png` is a
        // protocol-relative URL wearing a path's clothes.
        //
        // The gateway's own allowlist is the control that actually matters
        // here and these are defence in depth — but a client that sends a
        // traversal and waits to be told no is a client asking the wrong
        // question.
        private static bool LooksLikeAnImagePath(string text) =>
            (text.StartsWith('/') || text.StartsWith("~/", StringComparison.Ordinal))
            && !text.StartsWith("//", StringComparison.Ordinal)
            && !text.Contains("..", StringComparison.Ordinal)
            && !text.Contains(' ') && !text.Contains('\n')
            && Array.Exists(ImageExtensions, ext => text.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

        // The gateway writes one of these whenever it actually delivers a
        // message somewhere, and for a picture it is the only trace a client
        // gets: content is the bare filename, with no directory and no url
        // (CB-94, read out of the gateway's own stored transcript).
        //
        // Keyed on this record rather than on anything the agent said, and
        // that is the point. CB-88's MEDIA: convention works, but it depends
        // on the agent remembering to write it — the automation here skipped
        // it twice in a row while delivering pictures perfectly well. The
        // mirror is written by the gateway, so it cannot be forgotten.
        internal const string DeliveryMirrorModel = "delivery-mirror";

        // The last resort for a delivered picture whose real directory is
        // nowhere on the page — a guess, and named as one.
        //
        // It is a guess because the gateway builds the mirror text as
        // `mediaUrls.map(basename).join(", ")`
        // (`resolveMirroredTranscriptText`, read out of the running gateway's
        // own bundle): the directory is *deliberately* stripped, so the bare
        // filename says nothing whatsoever about where the file is. This was
        // originally written as though it did, and QA measured the cost —
        // right for the drop that prompted the ticket, wrong for a browser
        // capture living one directory deeper in
        // `~/.openclaw/media/browser/`, which 404s here.
        //
        // Kept as a fallback rather than deleted because it is right for the
        // common case an agent is told to arrange (copy the file into the
        // shared media directory, which is what Lilibeth's own runbook says),
        // and because a wrong guess costs nothing: the fetch comes back
        // unavailable and the filename stays as the turn's text. See
        // MediaPathsByFileName for the answer that is not a guess.
        //
        // `~` deliberately: resolveUserPath expands it on the *gateway* side,
        // so a client that only ever learns a filename never has to know the
        // host's absolute paths. It does assume the default state-directory
        // name, so a gateway under --profile or OPENCLAW_STATE_DIR falls
        // through to the same harmless unavailable.
        internal const string SharedMediaDir = "~/.openclaw/media/";

        // What separates one candidate path from the next. Whitespace, plus
        // the double quote, because the page is scanned as raw JSON and a path
        // in there is `"…":"/Users/…/a.png"` with no whitespace around it —
        // without the quote the whole object is one token and nothing is found.
        //
        // `:` is deliberately *not* a separator: splitting on it would turn
        // `https://example.com/a.png` into a token beginning `//example.com/…`,
        // which looks rooted. That token shape is refused explicitly instead
        // (see AbsoluteImagePathIn), because a *literal* `//host/…` can appear
        // in text without a scheme in front of it.
        //
        // A path containing a space is therefore missed. That is accepted:
        // nothing here can tell a spaced path from two tokens, and guessing
        // wrong would build a request for a file that does not exist. None
        // appears in the corpus this was measured against.
        private static readonly char[] TokenBreaks = { ' ', '\t', '\n', '\r', '"' };

        // The whitespace escapes, which have to be handled separately because
        // the page is scanned as raw JSON: in there a newline is the *two
        // characters* `\` and `n`, not a newline, so TokenBreaks never splits
        // on it however many real newlines it lists.
        //
        // That is not a hypothetical. The first version of the raw-JSON scan
        // lost the very picture this ticket was filed about: its path is on a
        // line of its own inside a text block, so the escape glued it to the
        // sentence in front of it and nothing matched. The fallback happened
        // to be right for that one file, which is exactly the kind of luck
        // this ticket exists to stop relying on.
        private static readonly string[] EscapedWhitespace = { "\\n", "\\r", "\\t" };

        // No real path is longer than this — PATH_MAX is 1024 on macOS and
        // 4096 on Linux. The guard is not about paths, though: a message
        // carrying an inline picture has a single base64 token megabytes long,
        // and there is no reason to trim and test that.
        private const int LongestPath = 4096;

        // Punctuation a path picks up from the prose around it — quoted,
        // parenthesised, or ending a sentence. Trimmed from both ends before
        // the shape test, since otherwise a perfectly good path fails it for
        // having been written inside a sentence.
        //
        // `.`, `!` and `?` are in here for the sentence-final case, and are
        // safe rather than merely convenient: a path's last character is part
        // of its extension, so trimming cannot damage one, and a *leading*
        // full stop cannot survive the rooted-prefix test below anyway. The
        // backslash is here for JSON's escaped quote, which otherwise leaves
        // one clinging to a token.
        private static readonly char[] PathWrappers =
        {
            '"', '\'', '`', '(', ')', '[', ']', '{', '}', '<', '>',
            ',', ';', ':', '.', '!', '?', '\\'
        };

        // The directory a delivered picture actually lives in, recovered from
        // the page it was delivered on rather than assumed.
        //
        // The mirror record names the file and nothing else (see
        // SharedMediaDir for why), but the real path is generally somewhere
        // else on the same page. So the page is read for paths first, and a
        // mirror's filename is matched against them by basename.
        //
        // Fed the **raw JSON** of each message rather than the text this parser
        // renders, and that is the difference between working and nearly not.
        // TurnsFromHistory deliberately skips tool_use blocks — a replayed one
        // is a wall of JSON — but those blocks are exactly where the paths are:
        // a `--media ~/.openclaw/media/browser/…png` argument, an `aggregated`
        // field. Measured over every delivery-mirror record on the gateway
        // host, harvesting the rendered text resolves 3 of 41; harvesting the
        // raw JSON resolves 27 of 41. Same rendering, nine times the pictures.
        //
        // A basename found under two different directories is dropped rather
        // than chosen between. Fetching the wrong one of two real files would
        // draw a picture that is not the delivered one — actively misleading,
        // and worse than drawing none — where dropping it falls back to the
        // guess and, at worst, to the text that is there today. (No genuine
        // ambiguity appears in the corpus: the nearest path is 1-2 records
        // from its mirror, min to p90. The rule is for the case that has not
        // happened yet.)
        internal static Dictionary<string, string> MediaPathsByFileName(IEnumerable<string> texts)
        {
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            var ambiguous = new List<string>();

            foreach (var text in texts)
            {
                if (string.IsNullOrEmpty(text)) continue;

                // See EscapedWhitespace: a raw-JSON newline is two characters
                // and would otherwise glue a line-initial path to the line
                // before it.
                var scannable = text;
                foreach (var escape in EscapedWhitespace)
                {
                    if (scannable.Contains(escape, StringComparison.Ordinal))
                        scannable = scannable.Replace(escape, " ", StringComparison.Ordinal);
                }

                foreach (var token in scannable.Split(TokenBreaks, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (token.Length > LongestPath) continue;

                    var path = AbsoluteImagePathIn(token);
                    if (path is null) continue;

                    var name = path[(path.LastIndexOf('/') + 1)..];

                    if (!found.TryGetValue(name, out var seen)) found[name] = path;
                    else if (!string.Equals(seen, path, StringComparison.Ordinal)) ambiguous.Add(name);
                }
            }

            foreach (var name in ambiguous) found.Remove(name);
            return found;
        }

        // What counts as a path worth fetching, and the answer is deliberately
        // narrow. Rooted at `/` or `~/`, a known image extension, and no `..`
        // anywhere.
        //
        // That last one is not decoration. This builds a gateway request out
        // of a string an agent wrote into a transcript, and traversal is the
        // open question on the sibling path (CB-89) — refusing `..` outright
        // is cheaper than reasoning about what it would resolve to on a host
        // this process cannot see. A relative path is refused for the related
        // reason that there is nothing here to resolve it against.
        internal static string? AbsoluteImagePathIn(string token)
        {
            var path = token.Trim(PathWrappers);

            if (!path.StartsWith('/') && !path.StartsWith("~/", StringComparison.Ordinal)) return null;

            // A protocol-relative URL — `//cdn.example.com/…/a.png`, which is
            // how a Discord attachment can appear in a transcript with the
            // scheme left off. It is rooted-looking and it is not a path.
            //
            // The gateway would refuse it anyway (`outside-allowed-folders`),
            // so the reason to reject it here is subtler: accepting it puts a
            // second directory under a basename, and the ambiguity rule then
            // *drops* the real path that was sitting next to it. A bogus
            // candidate does not just fail, it takes a good one with it.
            if (path.StartsWith("//", StringComparison.Ordinal)) return null;

            if (path.Contains("..", StringComparison.Ordinal)) return null;

            return Array.Exists(ImageExtensions, ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                ? path
                : null;
        }

        // Both conditions are load-bearing. delivery-mirror is *not* only used
        // for pictures — an ordinary text message sent through this app is
        // mirrored the same way ("**(via Claude Buddy)** try send me a
        // picture", observed live) — so the model alone would send perfectly
        // good prose off to be fetched as a file. And a bare filename alone is
        // no signal either: an agent can simply mention one mid-conversation.
        // Only the two together mean "a picture was delivered".
        internal static string? DeliveredPictureName(string? model, string text)
        {
            if (model != DeliveryMirrorModel) return null;

            // No emptiness guard, deliberately. The one caller has already
            // skipped a whitespace-only text, and an empty name is refused by
            // the extension test at the bottom anyway — "" ends with none of
            // them — so a check here would be a line no input can change the
            // answer of. Same reasoning that removed the third arm of the
            // live-image resolution rather than writing a test around it.
            var name = text.Trim();
            if (name.Contains('/') || name.Contains('\\')) return null;
            if (name.Contains(' ') || name.Contains('\n')) return null;

            return Array.Exists(ImageExtensions, ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                ? name
                : null;
        }

        // The gateway route that serves a file the agent named by path.
        //
        // CB-88 originally asked the `media.get` RPC for this, concluded from
        // its refusal that the feature needed `operator.admin`, and shipped
        // against a guessed response shape. All three were wrong. Read out of
        // the gateway's own descriptor table:
        //
        //     { name: "assistant.media.get", scope: "operator.read",
        //       advertise: false }
        //
        // It is an HTTP route rather than a WS method — asking for it over
        // RPC answers "unknown method" — and it needs only the scope this app
        // already pairs with. `advertise: false` is why it appears in neither
        // the docs nor `openclaw docs`, and why the admin-tier `media.get`
        // was the one that turned up first. That one is the read-any-path
        // version, which is exactly why it is gated the way it is.
        //
        // Verified against the live gateway with this device's own read
        // token: 200, 716535 bytes, image/png, for both a raw and a
        // percent-encoded source. Adding `&meta=1` asks whether a path is
        // servable at all and answers with a reason — a path outside the
        // gateway's hardcoded media allowlist comes back
        // `{"available":false,"code":"outside-allowed-folders"}`, which is
        // what an agent's picture written into *another* agent's workspace
        // does (CB-90).
        internal const string AssistantMediaRoute = "/__openclaw__/assistant-media?source=";

        // Delegating rather than opening a second fetch path: FetchMediaAsync
        // already speaks this exact transport and keys a bounded cache by the
        // url it is handed, so routing through it means a picture scrolled
        // past twice is fetched once.
        internal static Task<byte[]?> FetchLocalMediaAsync(string path, CancellationToken ct) =>
            FetchMediaAsync(AssistantMediaRoute + Uri.EscapeDataString(path), ct);

        internal static string Readable(string text) => Readable(text, out _);

        internal static string Readable(string text, out string? speakerId)
        {
            speakerId = null;

            // Not something a person said: OpenClaw writes this into the user
            // role when it restarts a CLI session under the covers. Dropped
            // rather than shortened, because there is nothing in it for the
            // person reading — an empty result is skipped by the caller.
            if (text.StartsWith("OpenClaw resumed this CLI session", StringComparison.Ordinal)) return "";

            text = WithoutTrailingInstruction(text);
            text = WithShortAttachments(text);

            const string Marker = "[Inter-session message]";
            if (!text.StartsWith(Marker, StringComparison.Ordinal)) return text;

            var rest = text[Marker.Length..].TrimStart();
            string? from = null;

            // The header is a run of key=value tokens; the message is whatever
            // follows the last of them. Parsed by shape rather than by a fixed
            // list of keys, so a new one appearing doesn't leak into the body.
            while (true)
            {
                var space = rest.IndexOf(' ');
                if (space <= 0) break;

                var token = rest[..space];
                var equals = token.IndexOf('=');
                if (equals <= 0) break;

                if (token.StartsWith("sourceSession=", StringComparison.Ordinal))
                {
                    // "agent:comfyui:discord:direct:…" — the agent's name is the
                    // one part of that a person recognises.
                    var value = token["sourceSession=".Length..].Split(':');
                    if (value.Length >= 2) from = value[1];
                }

                rest = rest[(space + 1)..].TrimStart();
            }

            if (string.IsNullOrWhiteSpace(rest)) return text;

            if (from is null) return rest;

            // Reported rather than glued to the front of the text. A name in the
            // string is a name the panel can only draw as part of the sentence;
            // as a field it can be a label above the bubble and can colour it.
            speakerId = from;
            return rest;
        }

        // The agent's name if we have it. The key carries the id, and the id is
        // what its owner's config calls it rather than what they do.
        public static string AgentNameOf(string agentId)
        {
            lock (Gate) return AgentNames.GetValueOrDefault(agentId, agentId);
        }

        // The picture for an agent named in a transcript, for the chip beside
        // their name in a room.
        //
        // By display name, which is the wrong way round and is what a merged
        // room view leaves us: a turn carries who said it, not which agent id
        // said it, because the panel's turn model is deliberately transport-
        // agnostic and an agent id is not a thing it knows about.
        //
        // Two agents sharing a display name therefore cannot be told apart, so
        // this refuses rather than guessing — the initials chip is a fine answer
        // and the wrong face is not. Not a hypothetical: agent ids are unique
        // and their names are whatever somebody typed.
        public static OpenClawAvatars.Avatar? AvatarForAgentName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            string? agentId = null;

            lock (Gate)
            {
                foreach (var (id, display) in AgentNames)
                {
                    if (!string.Equals(display, name, StringComparison.Ordinal)) continue;
                    if (agentId is not null) return null;   // ambiguous
                    agentId = id;
                }
            }

            if (agentId is null) return null;

            var identity = IdentityOf(agentId);
            return identity is null ? null : OpenClawAvatars.For(agentId, identity.Avatar);
        }

        // The last thing the agent said, for the speak button on the orb's own
        // flyout — which has no panel open and so no transcript to read from.
        // Loads the history if this session has never been opened.
        // Excluded from coverage: a history request over the live connection.
        [ExcludeFromCodeCoverage]
        public static async Task<string?> LastAssistantTextAsync(string sessionId, string displayName)
        {
            if (ChatFor(sessionId, displayName) is not OpenClawChatSession chat) return null;

            var existing = LastAssistantText(chat);
            if (existing is not null) return existing;

            // ChatFor kicks off a load; give it a moment rather than duplicating
            // the request. Speaking is a deliberate act, so a short wait is
            // better than saying nothing at all.
            for (var i = 0; i < 20; i++)
            {
                await Task.Delay(100);

                var text = LastAssistantText(chat);
                if (text is not null) return text;
            }

            return null;
        }

        internal static string? LastAssistantText(OpenClawChatSession chat)
        {
            for (var i = chat.History.Count - 1; i >= 0; i--)
            {
                var turn = chat.History[i];
                if (turn.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(turn.Text))
                {
                    return turn.Text;
                }
            }

            return null;
        }

        // Voice-mode messages arrive with an instruction stapled to the end:
        //
        //   What is the status of our OpenClaw install?
        //
        //   [Reply out loud in a natural speaking voice: 1-3 short sentences, …]
        //
        // That is OpenClaw's scaffolding rather than anything the person said,
        // and it is longer than most of their actual messages.
        //
        // The rule is deliberately narrow: a *final* paragraph that is entirely
        // wrapped in brackets, in a message that has something else in it. A
        // broader "strip bracketed text" would eat legitimate content, and a
        // check for this exact wording would rot the first time the prompt is
        // reworded.
        // An attachment arrives as its staging path:
        //
        //   [media attached: /Users/…/media/inbound/openclaw-staged-f71b696d-….png]
        //
        // The path is a detail of where the gateway put the file, and it is
        // longer than most messages. The filename is the only part worth
        // showing, and even that mostly to say something was attached at all.
        //
        // internal rather than private to this method: OpenClawChatSession's
        // live path (OnAgentText) needs to recognise the same marker to know
        // a streaming reply is worth resolving against history — see
        // TryResolveLiveImage.
        internal const string MediaAttachedMarker = "[media attached: ";

        private static string WithShortAttachments(string text)
        {
            const string Marker = MediaAttachedMarker;

            var start = text.IndexOf(Marker, StringComparison.Ordinal);
            while (start >= 0)
            {
                var end = text.IndexOf(']', start);
                if (end < 0) break;

                var path = text[(start + Marker.Length)..end];
                var name = path[(path.LastIndexOfAny(new[] { '/', '\\' }) + 1)..];

                text = text[..start] + "📎 " + (name.Length == 0 ? "attachment" : name) + text[(end + 1)..];
                start = text.IndexOf(Marker, StringComparison.Ordinal);
            }

            return text;
        }

        private static string WithoutTrailingInstruction(string text)
        {
            var trimmed = text.TrimEnd();
            if (!trimmed.EndsWith(']')) return text;

            var open = trimmed.LastIndexOf("\n\n[", StringComparison.Ordinal);
            if (open < 0) return text;

            var body = trimmed[..open].TrimEnd();
            if (body.Length == 0) return text;

            // Only when the bracket really does open that last paragraph — a
            // message whose final paragraph merely contains a bracket keeps it.
            var tail = trimmed[(open + 2)..];
            return tail.IndexOf(']') == tail.Length - 1 ? body : text;
        }

        // Pictures sent in a conversation. The gateway serves them from its own
        // HTTP endpoint, authorised with the same gateway token the socket
        // uses, and they arrive as ordinary bytes.
        //
        // Cached by url: a transcript is re-read every time its panel opens, and
        // refetching a megabyte per image per open would be wasteful and slow
        // in exactly the moment the user is waiting to see something.
        private static readonly Dictionary<string, byte[]?> Media = new(StringComparer.Ordinal);

        // Excluded from coverage: an HTTP GET against the gateway host.
        [ExcludeFromCodeCoverage]
        public static async Task<byte[]?> FetchMediaAsync(string url, CancellationToken ct)
        {
            lock (Gate)
            {
                if (Media.TryGetValue(url, out var cached)) return cached;
            }

            var host = ClaudeBuddySettings.OpenClawHost;
            var token = OpenClawIdentity.GatewayTokenFor(host);
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrEmpty(token)) return null;

            byte[]? bytes = null;

            try
            {
                var pinned = ClaudeBuddySettings.OpenClawFingerprint;

                bytes = await OpenClawSocket.GetAsync(
                    host, ClaudeBuddySettings.OpenClawPort, url, token!,
                    string.IsNullOrEmpty(pinned) ? null : pinned, ct);
            }
            catch
            {
                // A picture that won't load is a picture that won't load. The
                // message it belongs to still reads.
            }

            lock (Gate)
            {
                // Only successes are cached. Storing the failure meant one
                // hiccup — a gateway mid-restart — hid that picture for the life
                // of the process, however many times the panel was reopened.
                if (bytes is null || bytes.Length == 0) return null;

                Media[url] = bytes;

                // Bounded, because these are megabyte-sized and a long
                // conversation full of renders would otherwise grow the cache
                // for as long as the app runs. Oldest out first; a picture that
                // gets evicted and scrolled back to is simply fetched again.
                const int Keep = 24;
                while (Media.Count > Keep)
                {
                    Media.Remove(Media.Keys.First());
                }

                return bytes;
            }
        }

        internal static List<OpenClawChatSession> OpenChats()
        {
            lock (Gate) return Chats.Values.ToList();
        }

        // The conversation as it already stands. Without this a panel opens
        // blank and you are answering a question you cannot see — which is
        // exactly how it felt the first time one was opened for real.
        // How many of the gateway's messages a page asks for. Small enough that
        // opening a panel is quick, large enough that scrolling back doesn't
        // feel like it is fetching one line at a time.
        private const int PageSize = 40;

        // An older page, fetched when the panel is scrolled to the top.
        // chat.history counts its offset back from the newest message, so
        // walking backwards is simply an increasing offset — verified against
        // the gateway: consecutive pages do not overlap.
        // Excluded from coverage: pages history back over the live connection.
        [ExcludeFromCodeCoverage]
        public static async Task<bool> LoadOlderAsync(OpenClawChatSession chat, CancellationToken ct)
        {
            if (!chat.HasMore) return false;

            var page = await FetchPageAsync(chat, chat.LoadedMessages, ct);
            if (page is null) return false;

            var (turns, messages) = page.Value;

            // Nothing came back, so there is nothing behind this. Asked once and
            // remembered, rather than re-asking every time the user reaches the
            // top of a conversation that has no more to give.
            if (messages == 0)
            {
                chat.HasMore = false;
                return false;
            }

            // A short page is the last page. Without this the next scroll to the
            // top spends a round trip discovering the same thing again.
            if (messages < PageSize) chat.HasMore = false;

            chat.LoadedMessages += messages;
            chat.PrependHistory(turns);
            return turns.Count > 0;
        }

        // Excluded from coverage: a sessions.history request over the live
        // connection.
        [ExcludeFromCodeCoverage]
        private static async Task LoadHistoryAsync(OpenClawChatSession chat, CancellationToken ct)
        {
            var first = await FetchPageAsync(chat, 0, ct);
            if (first is null) return;

            var (initial, count) = first.Value;

            chat.LoadedMessages = count;
            chat.HasMore = count >= PageSize;

            Dispatcher.UIThread.Post(() => chat.SetHistory(initial));
        }

        // Excluded from coverage for its last line. With no gateway there is
        // nothing to fetch and it says so, which is the half a test can reach and
        // the half LoadOlderAsync's tests assert through — reaching the other
        // half means a chat.history request over a live socket, which is the
        // reason FetchHistoryPageAsync below is excluded too.
        //
        // internal rather than private: OpenClawChatSession.TryResolveLiveImage
        // reaches for the newest page (offset 0) the same way LoadOlderAsync
        // reaches for an older one, rather than opening a second request shape
        // for the same "one page of chat.history" idea.
        [ExcludeFromCodeCoverage]
        internal static async Task<(List<HistoryTurn> Turns, int Messages)?>
            FetchPageAsync(OpenClawChatSession chat, int offset, CancellationToken ct)
        {
            OpenClawGateway? gateway;
            lock (Gate) gateway = _gateway;

            if (gateway is null) return null;

            return await FetchHistoryPageAsync(gateway, chat, offset, ct);
        }

        // Excluded from coverage: a chat.history request over the live socket.
        // What comes back is turned into turns by TurnsFromHistory, which is pure
        // and covered against fixtures — this is only the asking, and the catch
        // for a gateway that will not answer.
        //
        // That catch is deliberate rather than defensive: a backlog that cannot be
        // fetched is not a reason to refuse the conversation, because the panel
        // still works forward from whatever happens next.
        [ExcludeFromCodeCoverage]
        private static async Task<(List<HistoryTurn> Turns, int Count)?> FetchHistoryPageAsync(
            OpenClawGateway gateway, OpenClawChatSession chat, int offset, CancellationToken ct)
        {
            try
            {
                var res = await gateway.RequestAsync("chat.history", new Dictionary<string, object>
                {
                    ["sessionKey"] = chat.GatewayKey,
                    ["limit"] = PageSize,
                    ["offset"] = offset
                }, ct);

                if (!res.TryGetProperty("messages", out var messages)
                    || messages.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var turns = TurnsFromHistory(messages);
                // The message count, not the turn count: it is what the next
                // page's offset is measured in, and one message can produce
                // several turns or none.
                return (turns, messages.GetArrayLength());
            }
            catch
            {
                // A gateway that won't tell us the backlog is not a reason to
                // refuse the conversation — the panel still works forward from
                // whatever happens next.
                return null;
            }
        }

        internal static void Report(string state)
        {
            lock (Gate) _state = state;
        }

        private static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

        // Null rather than false when absent, because the three answers are
        // genuinely different here: senderIsOwner true is the gateway saying the
        // operator sent it, false is the gateway saying somebody else did, and a
        // missing field is the gateway not saying — which is the case the whole
        // classification has to keep degrading gracefully to.
        private static bool? Bool(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty(name, out var v)
            && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? v.GetBoolean()
                : null;

        private static long Num(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number
                ? v.GetInt64()
                : 0;
    }
}
