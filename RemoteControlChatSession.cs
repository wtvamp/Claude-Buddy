using System.Diagnostics.CodeAnalysis;
using Avalonia.Threading;

namespace ClaudeBuddy
{
    // One Claude Code session on another machine, as something the chat panel
    // can talk to — in one of two modes, and the difference between them is the
    // whole point of this class.
    //
    // **Live view** is what you get when the other machine is also running
    // Claude Buddy. That Buddy reads the session's transcript off its own disk
    // and sends it here in hashed pieces, so this panel shows the same
    // conversation the person sitting in front of that machine sees — verbatim,
    // byte for byte, parsed by the same ChatTranscript a local panel uses. What
    // you type is typed into that session's own input line, which means slash
    // commands work: /color, /rename, all of them, because the CLI's own command
    // handler is what runs them.
    //
    // **Messaging** is the fallback, and it is what this class used to be
    // always. Without a Buddy on the far side there is no way to read a file
    // there — the only channel is peer messaging, which reaches the far
    // session's *model*, not its terminal. So what comes back is a reply that
    // model wrote for a peer: its own words about its conversation rather than
    // its conversation. That is not a bug in the transport and cannot be fixed
    // by asking nicely; it was measured being asked nicely and it still
    // paraphrased. It is simply the most that channel can carry, and the panel
    // says so in as many words rather than letting a summary pass for a
    // transcript.
    //
    // A panel opens in messaging mode and **upgrades in place** when the far
    // Buddy answers, which is why this is one class with a mode rather than two
    // classes: the handshake costs a round trip through a model and can take
    // half a minute, and nobody should have to close a panel and reopen it to
    // find out it could have been a live view all along. HistoryReplaced is what
    // makes that free — the panel already redraws from History when it fires.
    internal sealed class RemoteControlChatSession :
        IRemoteChatSession, IRemoteChatComposer, IRemoteChatSlashCommands, IRemoteChatBacklog,
        IRemoteChatFetchWait, IRemoteChatMachine, IDisposable
    {
        private readonly List<ChatTurn> _history = new();

        // The peer's name on the other machine — the correlation key that
        // matches an inbound message back to this conversation. Not a display
        // nicety: it is the only link, because replies arrive on some later turn
        // of the bridge's conversation with nothing tying them to the send.
        private readonly string _remoteName;

        // Which account's relay this conversation goes through. Needed because
        // there is one relay per account now, and a name alone no longer says
        // which machine — or which login — a message should leave by.
        private readonly string _account;

        // Rows already turned into turns, by transcript uuid. Only meaningful in
        // live view, where the same row can legitimately arrive twice: the
        // opening window and the first delta can overlap if the file grew
        // between the two reads.
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        private bool _mirroring;

        // Whether the mirror has ever actually put the far transcript on screen.
        //
        // Distinct from _mirroring, which only says a live view was *agreed*.
        // Between the two there is a real interval — the first window has to
        // cross a wire that moves one chunk per model turn — and CB-46 found a
        // panel that sat in it indefinitely with all three of its sources
        // silent at once: the window had not arrived, the delta subscription
        // does not start until one has, and OnInbound was dropping the far
        // session's messages because _mirroring was true. The user sent "test",
        // the session replied "Received — connectivity confirmed.", that reply
        // reached this machine, and the panel threw it away.
        //
        // So the rules that go quiet in favour of the transcript key on this
        // instead. A panel that has upgraded but never painted keeps behaving
        // like the messaging channel it was a moment ago, which is the honest
        // answer: it degrades to something rather than to nothing, and it does
        // so for a stalled mirror later just as much as for a slow first paint.
        private bool _painted;
        private CliChatFormat _format = CliChatFormat.ClaudeCode;
        private bool _saidNoLiveView;
        private bool _disposed;

        public RemoteControlChatSession(string sessionId, string account, string remoteName)
        {
            SessionId = sessionId;
            _account = account;
            _remoteName = remoteName;
            DisplayName = remoteName;

            // Opens with a line saying what this panel is, because otherwise it
            // opens empty and an empty panel reads as broken.
            //
            // It also survives the one case that surprised me in testing: the
            // history is in memory, so restarting Claude Buddy empties it. With
            // this line the panel still explains itself after a restart instead
            // of being a blank box.
            //
            // Deliberately does not promise a live view yet. Saying "mirroring…"
            // and then falling back would be worse than saying "checking" and
            // then succeeding.
            Note($"Messages you send {remoteName} appear here, with its replies. "
                + "Checking whether a live view of its conversation is available…");

            RemoteControlSessions.MirrorChanged += OnMirrorChanged;

            // The answer may already be in — a second panel on a machine that
            // handshook minutes ago upgrades before it is ever drawn.
            TryUpgrade();
        }

        public string SessionId { get; }

        public string DisplayName { get; set; }

        public RemoteChatState State { get; private set; } = RemoteChatState.Connected;

        public IReadOnlyList<ChatTurn> History => _history;

        public bool IsMirroring => _mirroring;

        public event Action<ChatTurn>? TurnAdded;

        // Raised in live view, where the transcript settles a turn that was
        // already on screen: the message you typed comes back as a real row once
        // the far CLI has read it, and adopts the bubble rather than adding a
        // second. Never raised in messaging mode, which has no echo to
        // reconcile — see the note on Reconcile.
        public event Action<ChatTurn>? TurnUpdated;

        public event Action<RemoteChatState>? StateChanged;

        // Said in the input box itself. "Message…" would be a lie by omission
        // here: this one leaves the machine, and in live view it is typed into
        // somebody else's terminal, which is worth being even plainer about.
        public string ComposerHint => ComposerHintFor(_mirroring, _canType, _canDeliver, _remoteName);

        // Pure and static so the three-way choice is a unit test rather than
        // a screenshot — the same arrangement WaitLabel and FetchingNote are
        // already under.
        //
        // **The third arm is CB-105's.** A live view used to mean exactly one
        // of two things: type into a terminal, or nothing — a pane-less
        // session got the same "Message…" wording as no live view at all,
        // which was honest before this feature existed and stopped being
        // honest the moment there was a second way to reach it. A background
        // or agent-mode job has no terminal and never will, so
        // `mirroring && !canType` no longer automatically means "can't
        // send" — canDeliver says whether this machine can hand the text to
        // that session's own messaging socket instead.
        internal static string ComposerHintFor(bool mirroring, bool canType, bool canDeliver, string name) =>
            !mirroring
                ? $"Message {name} on the other machine…"
                : canType
                    ? $"Type into {name}'s terminal on the other machine…"
                    : canDeliver
                        ? $"Message {name}'s background job on the other machine — it reads this at its next turn…"
                        : $"Message {name} on the other machine…";

        // Whether the far session actually has an input line to type into.
        //
        // **The roster has always answered this and the hint ignored it.**
        // MirrorRosterEntry carries HasPane, and its own comment says why: "a
        // panel that offered a send it cannot deliver would be lying in the one
        // place it matters." Keying the hint on _mirroring alone did exactly
        // that — a live view was taken to mean a typable one, and on a headless
        // machine it never is.
        //
        // Seen on a real pair: the composer said "Type into
        // job-hunter-mac-mini's terminal" while the line directly above it read
        // "Sent as a message rather than typed … there is no input line to type
        // into." Both were on screen at once, and only one of them was true.
        private bool _canType;

        // Whether this machine can hand text to the far session's own
        // messaging socket when there is no pane to type into — CB-105's
        // second delivery path, for a background or agent-mode job that
        // never has a terminal at all. Read off the same roster entry
        // _canType is, and for the same reason _canType's own comment gives:
        // a hint that offered a send it cannot deliver would be lying in the
        // one place it matters.
        private bool _canDeliver;

        // In live view: every command that session can run, read off the far
        // machine's own disk by the Buddy sitting next to it — built-ins
        // included, because a mirrored send is typed into that CLI's input line
        // and its own handler runs it.
        //
        // In messaging mode: only what the far session said it can run, and
        // nothing until it has said so. A built-in genuinely cannot run over
        // that channel — measured, with /color coming back "I can't run /color
        // ... only the harness's own command handler can set" it — so offering
        // one would be offering something that quietly does nothing when
        // accepted. RemoteControlSessions.CommandsFor knows which of the two
        // answers it has.
        public IReadOnlyList<SlashCommand> SlashCommands =>
            RemoteControlSessions.CommandsFor(_account, _remoteName);

        // --- live view ---------------------------------------------------------

        private void OnMirrorChanged(string account)
        {
            if (!account.Equals(_account, StringComparison.Ordinal)) return;

            // The roster landing is what makes the far machine knowable, so this
            // is the moment a panel opened before it can be told.
            OnUi(() =>
            {
                MachineChanged?.Invoke();
                TryUpgrade();
            });
        }

        private void TryUpgrade()
        {
            if (_disposed || _mirroring) return;

            var state = RemoteControlSessions.MirrorStateFor(_account, _remoteName);

            if (state.Availability == RemoteMirrorClient.MirrorAvailability.Unavailable)
            {
                SayNoLiveView();
                return;
            }

            if (state.Availability != RemoteMirrorClient.MirrorAvailability.Available) return;
            if (state.Entry is not { } entry) return;

            var client = RemoteControlSessions.MirrorClientFor(_account);
            if (client is null) return;

            _mirroring = true;
            _canType = entry.HasPane;
            _canDeliver = entry.CanDeliver ?? false;
            _format = CliChatFormat.For(
                entry.Cli.Equals(MirrorProtocol.CliCodex, StringComparison.OrdinalIgnoreCase)
                    ? SessionSource.Codex
                    : SessionSource.ClaudeCode);

            client.Delivered += OnDelivered;
            client.Failed += OnMirrorFailed;

            // Only if somebody is actually looking. A handshake that lands while
            // every panel is closed switches the mode and stops there; the next
            // PanelOpened reads the tail, which by then is the current one
            // anyway.
            if (!_panelOpen) return;

            // Said before the fetch rather than after, because the fetch is the
            // part that takes time. The opening line is "Checking whether a live
            // view … is available", and leaving that on screen for the whole
            // transfer showed a user the exact sentence that meant failure an
            // hour earlier — they reported a working transfer as "no live view"
            // twice, which is a wording bug rather than a mirror one.
            //
            // Not a problem to clean up afterwards: the window that ends this
            // wait clears the history outright and replaces it with the far
            // conversation, so this line goes with it.
            if (!_painted) Note(FetchingNote(_remoteName));

            // Started here rather than inside OpenAsync so the indicator covers
            // the whole wait the user experiences, including the part where the
            // frame is still queued on this machine's own relay. That queueing
            // was eight minutes once (CB-56), and a spinner that only began when
            // the frame finally went out would have shown nothing for the part
            // that most needed explaining.
            BeginWait();

            _ = client.OpenAsync(_remoteName);
        }

        // --- where this conversation actually is ----------------------------

        // See IRemoteChatMachine. Read through on demand rather than cached,
        // because the roster usually answers after the panel is already open and
        // a cached null would stay null until something else happened to
        // refresh it.
        // **Straight off the roster now, where it used to be parsed back out of
        // a relay's tmux session name.** That parse was the third of the three
        // couplings this transport had to break, and it was the fiddliest: one
        // account's name is a prefix of another's the moment somebody has
        // `.claude` and `.claude-board`, so it could answer confidently and
        // wrongly. A direct link records who served each session, and who served
        // it *is* the machine it is on.
        public string? MachineName =>
            RemoteControlSessions.MirrorClientFor(_account)?.RelayFor(_remoteName);

        public event Action? MachineChanged;

        // --- the wait, while it is happening --------------------------------

        // See IRemoteChatFetchWait. Set immediately before the fetch and cleared
        // however it ends, including the failure paths, because a spinner left
        // running after a transfer gave up is a worse lie than no spinner.
        private DateTimeOffset? _waitingSince;

        public DateTimeOffset? WaitingSince => _waitingSince;

        public string WaitingFor => $"{_remoteName}'s conversation";

        public event Action? WaitChanged;

        private void BeginWait()
        {
            _waitingSince = DateTimeOffset.Now;
            WaitChanged?.Invoke();
        }

        private void EndWait()
        {
            if (_waitingSince is null) return;

            _waitingSince = null;
            WaitChanged?.Invoke();
        }

        // How long it has been, in words, for the indicator above the turns.
        //
        // Pure and static so the wording is a unit test rather than a
        // screenshot, the same arrangement FetchingNote is under.
        //
        // **Seconds all the way to a minute, then minutes and seconds.** The
        // measured waits are 3–4 minutes, so a counter that only showed whole
        // minutes would sit unchanged for sixty seconds at exactly the moment
        // somebody is deciding whether it has hung — which is the entire failure
        // this indicator exists to prevent.
        internal static string WaitLabel(TimeSpan elapsed, string what)
        {
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

            var when = elapsed.TotalSeconds < 60
                ? $"{(int)elapsed.TotalSeconds}s"
                : $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s";

            return $"Fetching {what} — {when}";
        }

        // The second line, which is the one that stops a normal wait reading as
        // a fault. Fixed rather than counting down: an estimate that ran out
        // while the transfer was still going would be the same broken promise
        // as the "about a minute" this project has already had to correct once.
        internal const string WaitHint = "these usually take three or four minutes";

        // Named for the same reason the refusals are: a line a user reads while
        // nothing appears to be happening has to say that something is.
        // **"A minute" was wrong, and understating it is the same bug in a
        // quieter form.** The first version said the wait could take a minute,
        // which was a guess; a single window off the mini was then timed at
        // `7m 15s`, because each piece is base64 the far relay's *model* has to
        // retype exactly. A user told to expect one minute and left waiting
        // seven concludes it has hung — which is how a transfer that was working
        // perfectly got reported as broken, twice, on the strength of a wording.
        // So it says minutes, and says why, rather than promising a number this
        // wire has never met. See CB-54.
        internal static string FetchingNote(string remoteName) =>
            $"Found a live view of {remoteName} — fetching its conversation from the other machine. "
          + "This can take several minutes: the transcript comes across in pieces, and each one "
          + "waits its turn on the relay, which retypes it by hand.";

        private void SayNoLiveView()
        {
            if (_saidNoLiveView) return;
            _saidNoLiveView = true;

            Note($"No live view: the other machine isn't running Claude Buddy's Remote Control for "
               + $"this session, so this stays a messaging channel — a way to talk to {_remoteName}, "
               + "not a view of it. Its replies here are written for you, and may summarise what it "
               + "actually did.");
        }

        private void OnDelivered(RemoteMirrorClient.MirrorRows rows)
        {
            if (!rows.Name.Equals(_remoteName, StringComparison.OrdinalIgnoreCase)) return;

            EndWait();

            // Already parsed, on the machine that had the file.
            //
            // The far Buddy runs the same ChatTranscript this app uses locally
            // and sends the turns rather than the rows — see MirrorProtocol's
            // note on why, which is that the rows cost ten to thirty times as
            // much to relay as the turns they produce. So there is nothing to
            // parse here, only turns to adopt.
            var mapped = rows.Turns
                .Select(t => new
                {
                    t.Uuid,
                    Turn = new ChatTurn
                    {
                        Role = MirrorProtocol.RoleOf(t.Role),
                        Text = t.Text,
                        IsComplete = true,
                        At = t.At > 0
                            ? DateTimeOffset.FromUnixTimeSeconds(t.At).ToLocalTime()
                            : DateTimeOffset.Now
                    }
                })
                .ToList();

            OnUi(() =>
            {
                if (_disposed) return;

                if (rows.Mode == RemoteMirrorClient.MirrorDelivery.Window)
                {
                    _history.Clear();
                    _seen.Clear();
                    _pending = null;

                    foreach (var row in mapped)
                    {
                        if (row.Uuid is not null && !_seen.Add(row.Uuid)) continue;
                        _history.Add(row.Turn);
                    }

                    Trim();

                    // Said once, at the top of the real conversation, so nobody
                    // mistakes a mirror for a chat thread and wonders why their
                    // half of it is missing.
                    _history.Insert(0, new ChatTurn
                    {
                        Role = ChatRole.System,
                        IsComplete = true,
                        Text = $"Live view: this panel mirrors {_remoteName}'s own conversation from "
                             + "the other machine, a few seconds behind. Messages you type are typed "
                             + "into its terminal."
                    });

                    _painted = true;

                    HistoryReplaced?.Invoke();
                    return;
                }

                foreach (var row in mapped)
                {
                    if (row.Uuid is not null && !_seen.Add(row.Uuid)) continue;
                    if (Reconcile(row.Turn)) continue;

                    Add(row.Turn);
                }
            });
        }

        private void OnMirrorFailed(string name, string why)
        {
            if (!name.Equals(_remoteName, StringComparison.OrdinalIgnoreCase)) return;

            EndWait();

            OnUi(() =>
            {
                if (_disposed) return;

                // Nothing of the failed transfer is shown — not a partial
                // window, not the messaging-channel version of it. The whole
                // reason this feature exists is that a plausible-looking second
                // draft is indistinguishable from the real thing once it is on
                // screen, and quietly substituting one at the exact moment
                // integrity failed would be the worst possible time to do it.
                // If the relay is stuck on a prompt, that is the real answer and
                // it names what to do about it. Saying "try again" to somebody
                // whose relay is waiting on a keypress sends them round the loop
                // that produced this.
                var stall = RemoteControlSessions.StallFor(_account);

                Note(stall is null
                    ? $"Couldn't verify {_remoteName}'s transcript — {why}. Showing nothing rather "
                      + "than something altered; close and reopen the panel to try again."
                    : $"Couldn't reach {_remoteName}: this machine's relay session is {stall}");
            });
        }

        // --- backlog -------------------------------------------------------------

        // Claimed in both modes, answered honestly in each. In messaging mode
        // there is nothing older to fetch and this is false forever, which is
        // what keeps a "loading older messages" spinner off a conversation that
        // has no history to load — the panel asks before every fetch.
        public bool HasMore =>
            _mirroring && RemoteControlSessions.MirrorClientFor(_account)?.HasMore(_remoteName) == true;

        public async Task<bool> LoadOlderAsync(CancellationToken ct)
        {
            if (!HasMore) return false;

            var client = RemoteControlSessions.MirrorClientFor(_account);
            if (client is null) return false;

            var turns = await client.LoadOlderAsync(_remoteName).ConfigureAwait(true);
            if (turns is null) return false;

            var mapped = turns
                .Select(t => new
                {
                    t.Uuid,
                    Turn = new ChatTurn
                    {
                        Role = MirrorProtocol.RoleOf(t.Role),
                        Text = t.Text,
                        IsComplete = true,
                        At = t.At > 0
                            ? DateTimeOffset.FromUnixTimeSeconds(t.At).ToLocalTime()
                            : DateTimeOffset.Now
                    }
                })
                .ToList();

            var older = new List<ChatTurn>();

            foreach (var row in mapped)
            {
                if (row.Uuid is not null && !_seen.Add(row.Uuid)) continue;
                older.Add(row.Turn);
            }

            if (older.Count == 0)
            {
                // A page that parsed to nothing but moved the offset is not the
                // end — the window can be entirely tool results. Same rule as
                // LocalCliChatSession: the answer is whether there is more to
                // ask for, not whether this page had anything in it.
                return HasMore;
            }

            // After the live-view banner, which is the one thing that stays at
            // the top: it describes the panel, not a moment in the conversation.
            var at = _history.Count > 0 && _history[0].Role == ChatRole.System ? 1 : 0;
            _history.InsertRange(at, older);

            HistoryPrepended?.Invoke(older.Count);
            return true;
        }

        public event Action? HistoryReplaced;

        public event Action<int>? HistoryPrepended;

        // --- sending -------------------------------------------------------------

        // Excluded from coverage for its last line, which nothing may execute:
        // reaching it means remote control is on, and SendThroughRelayAsync then
        // starts a live Claude Code session on another machine and types into it.
        // There is no arrangement of settings that makes that inert, so the only
        // honest way to leave this line unrun is not to measure it.
        //
        // Everything it decides first is still asserted — see
        // RemoteControlChatSessionTurnTests, which drives this method with remote
        // control off and checks that the typed turn stays on screen with the
        // refusal underneath. Those assertions run; they are simply not counted.
        [ExcludeFromCodeCoverage]
        public async Task SendAsync(string text)
        {
            // The user's own turn is added here rather than by the panel, so one
            // thing owns the transcript and a send that fails leaves the message
            // on screen with an explanation under it rather than a ghost. Same
            // reasoning as OpenClawChatSession's.
            var mine = new ChatTurn { Role = ChatRole.User, Text = text, IsComplete = true };
            Add(mine);

            if (!ClaudeBuddySettings.PeerLinkEnabled)
            {
                Note(RemoteControlOffNote);
                return;
            }

            if (_mirroring)
            {
                await SendTypedAsync(mine, text).ConfigureAwait(true);
                return;
            }

            // **No live view means no way to send, and that is a real loss
            // rather than an oversight.** The relay used to answer here: a
            // session it could see but not mirror still took a message, handed
            // to its model as text. That channel went with the relay, and
            // nothing on a direct link replaces it — the link types into the far
            // session's own input line, which needs a pane to type into.
            //
            // Said plainly rather than left as a dead composer. A message that
            // vanishes with no explanation is the failure this panel has spent
            // six tickets learning not to produce.
            Note(NoWayToSendNote(_remoteName));
        }

        // Said when there is a session on screen and no way to reach it.
        //
        // Names the actual condition — no live view — rather than blaming the
        // network, because the fix is on the far machine and this is the only
        // place that will ever say so.
        internal static string NoWayToSendNote(string remoteName) =>
            $"No live view of {remoteName}, so there is nothing to type into. Claude Buddy can "
          + "show and reply to a session running under tmux on the other machine; this one "
          + "isn't, so it can be listed but not written to.";

        // Named so the wording is reachable from a test even though the method
        // that says it is not measured: a refusal that does not name the setting
        // to turn on is a dead end for whoever reads it.
        internal const string RemoteControlOffNote =
            "Remote sessions are switched off. Turn on \"Show sessions from other machines\" in Settings.";

        // The live-view send: typed into the far session's terminal by the Buddy
        // beside it. The far transcript will produce this message back, because
        // it went in through the input line — which is exactly what makes slash
        // commands work, and why the echo has to be reconciled rather than shown
        // twice.
        private async Task SendTypedAsync(ChatTurn mine, string text)
        {
            var client = RemoteControlSessions.MirrorClientFor(_account);
            if (client is null)
            {
                Note("The relay session isn't running. Try again to start it back up.");
                return;
            }

            // Marked pending after Add, never before: Add runs every turn
            // through Reconcile, and setting this first would make the message
            // match itself and vanish on the spot.
            _pending = mine;
            _pendingText = text.Trim();
            _pendingAt = DateTimeOffset.Now;

            RemoteControlSessions.Touch();

            var outcome = await client.SendInputDetailedAsync(_remoteName, text).ConfigureAwait(true);

            if (outcome.Error is null)
            {
                // Delivered over the messaging channel rather than typed.
                // _pending stays set exactly as it does for a typed send —
                // there is no ack on this wire either way, only the far
                // transcript's own next turn, and Echoes already knows how
                // to recognise this message coming back through it, the same
                // way it already does for the old relay fallback's
                // cross-session-message tag.
                if (string.Equals(outcome.Via, MirrorProtocol.ViaMessage, StringComparison.Ordinal))
                    Note(DeliveredRemotelyNote(_remoteName, outcome.AgentStatus));

                return;
            }

            // No terminal to type into is a missing mechanism, not a refusal, and
            // the messaging channel this panel used before it upgraded still
            // works. Refusing here made a live view *cost* the user the ability
            // to send — strictly worse than not having mirrored at all — and on
            // a headless machine, where a session runs in a plain tty rather
            // than under tmux, that is the ordinary case and not an edge one.
            //
            // **There used to be a fallback here and there is not any more.**
            // ErrNoPane — the far session is not in a tmux pane — was answered
            // by handing the text to that session as a message through the
            // relay. The relay is gone, and nothing on a direct link replaces
            // it: typing needs an input line to type into.
            //
            // The refusal below names which of the four codes came back, which
            // is the whole of what is left to say. Three of them are things to
            // change on the other machine and one is a locked door: ErrReplyOff
            // is that machine's owner having switched replying off, and a
            // request arriving over a wire does not change it.
            _pending = null;

            Note(TypingRefusal(outcome.Error, _remoteName));
        }

        // What the panel says when a message was handed to a background
        // job's own messaging socket rather than typed into a terminal —
        // CB-105's second delivery path.
        //
        // There is no ack on the wire this rides — MirrorProtocol's own note
        // says why: a successful hand-off only ever means "accepted for
        // delivery". So this is exactly as provisional as the ordinary
        // typed-echo wait: the transcript itself is still what settles the
        // pending turn, once that session's own next turn writes the row.
        internal static string DeliveredRemotelyNote(string remoteName, string? agentStatus) =>
            agentStatus == "working"
                ? $"Handed to {remoteName}. It's mid-turn and will read this when that turn ends — "
                  + "the message shows here once it has."
                : $"Handed to {remoteName} for its next turn. It arrives as a message from Claude "
                  + "Buddy, not keystrokes, so built-in slash commands won't run.";

        // What a refused keystroke says, as a function of the code that came
        // back rather than as a switch buried in the send.
        //
        // Worth having on its own because each of these is about a different
        // machine's state and only the person reading it can act: three of them
        // are things to change *over there*, and the last one is the arm that
        // runs when the far machine is newer than this one. A blank or a code
        // number on screen would be the worst outcome of the four, and it is the
        // only arm nothing else can produce.
        internal static string TypingRefusal(string? errCode, string remoteName) =>
            errCode switch
            {
                MirrorProtocol.ErrReplyOff =>
                    $"{remoteName}'s machine has replying to sessions switched off, so nothing was typed. "
                    + "That is its own setting, and it has to be turned on over there.",

                // No longer reached from a mirrored send: CB-43 routes this code
                // to the messaging channel instead (see FallsBackToMessaging),
                // so what a user sees for a pane-less session is
                // SentAsMessageNote. Kept because this is a general mapping from
                // code to wording rather than one call site's switch, and the
                // generic arm below would be a worse answer for a caller that
                // does hand it this code.
                // Says "a terminal Buddy can type into" rather than naming
                // tmux. Since CB-79 that is iTerm2 and Terminal.app as well,
                // and the old wording sent at least one user looking for a
                // tmux setting they did not want and did not need — for a
                // session that was in an ordinary iTerm2 window all along.
                //
                // The far machine's own reason cannot be read from here: this
                // maps a code, and the code is all that crosses the wire.
                // Built from the same phrase every local refusal uses, so a
                // user who has learned to recognise one answer does not have
                // to learn a second because the session is on another machine.
                MirrorProtocol.ErrNoPane =>
                    $"{remoteName}'s terminal {TerminalTyping.CantTypePhrase} on the other "
                    + "machine, so there is nowhere to type without bringing its window forward.",

                // The terminal was found and refused. Almost always one of two
                // things, and both are worth naming because neither is
                // guessable from "couldn't type that": macOS asks for
                // Automation consent the first time one app drives another and
                // that prompt appears on the *far* machine's screen, which on
                // a headless Mac nobody is looking at; or the window has been
                // closed since the status file was written.
                MirrorProtocol.ErrTypeFailed =>
                    $"{remoteName}'s terminal refused the text. On macOS the other machine may be "
                    + "waiting for you to allow Claude Buddy to control it — check for a prompt "
                    + "there — or that terminal window may have been closed.",

                MirrorProtocol.ErrNoSession =>
                    $"The other machine's Claude Buddy no longer has a session called {remoteName}.",

                // The messaging fallback tried, and Claude Code's own
                // registry no longer has an entry for this session — almost
                // always because the background job it named has since
                // stopped. Unlike ErrNoPane, there is no terminal *or* socket
                // to reach it through any more.
                MirrorProtocol.ErrNotRegistered =>
                    $"{remoteName} isn't registered with Claude Code any more — the job may have "
                    + "stopped.",

                // The messaging fallback found a registration and the socket
                // still refused it. A route was found here too, same as
                // ErrTypeFailed above, and it declined it.
                MirrorProtocol.ErrDeliverFailed =>
                    $"{remoteName}'s machine found the session but its messaging socket refused the "
                    + "connection; nothing was sent.",

                MirrorProtocol.ErrBadHash =>
                    "That message didn't survive the trip intact and was refused rather than typed "
                    + "in a form you didn't write. Try sending it again.",

                _ => $"Couldn't type that into {remoteName}."
            };

        private ChatTurn? _pending;
        private string _pendingText = "";
        private DateTimeOffset _pendingAt;

        // The mirrored transcript will produce the message just sent, because it
        // went through the terminal. So the row that comes back adopts the turn
        // already on screen rather than adding a second.
        //
        // Matched on text and bounded by time, the same way LocalCliChatSession
        // does it and for the same reason: an identical message sent twice an
        // hour apart must not have the second swallowed by a stale pending turn
        // that never arrived.
        // Excluded from coverage: reaching it means a message sent from this
        // panel was still unmatched two minutes later, which needs either two
        // minutes of a test's life or a clock this class does not take. The
        // decision itself is PendingHasGoneStale, which does take one and is
        // covered at the boundary in both directions.
        [ExcludeFromCodeCoverage]
        private bool ForgetPending()
        {
            _pending = null;
            return false;
        }

        // Bounded by time as well as by text, and the bound is the point: an
        // identical message sent twice an hour apart must not have the second
        // swallowed by a stale pending turn that never arrived. Taking "now"
        // rather than reading it is what makes that assertable — the alternative
        // is a test that waits two minutes.
        internal static bool PendingHasGoneStale(DateTimeOffset pendingAt, DateTimeOffset now) =>
            now - pendingAt > TimeSpan.FromMinutes(2);

        private bool Reconcile(ChatTurn incoming)
        {
            if (_pending is null) return false;

            if (PendingHasGoneStale(_pendingAt, DateTimeOffset.Now)) return ForgetPending();

            if (ReferenceEquals(incoming, _pending)) return false;
            if (incoming.Role != ChatRole.User) return false;
            if (!Echoes(incoming.Text)) return false;

            // Keep the transcript's own text: it is what that session actually
            // received, which is the thing this panel exists to show.
            var settled = _pending;
            _pending = null;

            settled.Text = incoming.Text;
            TurnUpdated?.Invoke(settled);
            return true;
        }

        // Whether a turn arriving from the far transcript is the message this
        // panel just sent, coming back.
        //
        // Two shapes, because there are two ways it can have got there. A typed
        // message went in through the session's own input line, so the
        // transcript holds exactly what was typed. A message sent through the
        // relay channel — the CB-43 fallback — was *handed* to that session, so
        // its transcript holds the whole `<cross-session-message …>` tag with
        // the text inside it, and an exact match would miss it and render the
        // message twice.
        //
        // Matched on the tag's parsed body rather than by looking for the text
        // anywhere in the row: a far session that merely quoted the same
        // sentence back would otherwise be swallowed as an echo, which is the
        // failure mode worth avoiding here — a message silently disappearing
        // reads as a bug in the panel, not in the matching.
        private bool Echoes(string incomingText)
        {
            if (string.Equals(incomingText.Trim(), _pendingText, StringComparison.Ordinal))
                return true;

            foreach (var carried in BridgeProtocol.ParseInboundMessages(incomingText))
            {
                if (string.Equals(carried.Body.Trim(), _pendingText, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        // --- inbound (messaging mode) ---------------------------------------------

        // A message from the other machine. Called on the UI thread by
        // RemoteControlSessions, which is the contract IRemoteChatSession states.
        public void OnInbound(BridgeProtocol.InboundMessage message)
        {
            // Both halves must match. The name says which session and the
            // account says whose — and with two accounts in play, a name on its
            // own can be true of two different machines at once.
            if (!message.FromName.Equals(_remoteName, StringComparison.OrdinalIgnoreCase)) return;
            if (message.Account.Length > 0
                && !message.Account.Equals(_account, StringComparison.Ordinal))
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(message.Body)) return;

            // In live view the transcript is the source of truth and a peer
            // message would be a second, differently-worded account of
            // something already shown. Dropped rather than appended: showing
            // both is precisely the confusion this feature was built to end.
            //
            // Only once it really is showing that transcript, though. Before the
            // first window lands there is no second account to be confused with
            // — there is nothing on screen at all — so dropping the message here
            // makes the panel strictly worse than the messaging channel it
            // replaced. See _painted.
            if (_mirroring && _painted) return;

            // The answer supersedes the "working" line, so that comes off first —
            // leaving it above the reply would read as though it were still going.
            ClearWorkingNote();

            Add(new ChatTurn
            {
                Role = ChatRole.Assistant,
                Text = message.Body,
                IsComplete = true
            });
        }

        // The waiting indicator, and the reason it is not decorative.
        //
        // A reply can be minutes away — the remote session may be running a
        // whole command — and until it lands the panel is a message you typed
        // and nothing else. That is indistinguishable from a send that silently
        // failed, which is the wrong thing to leave someone guessing about.
        //
        // Only in messaging mode. A live view shows the work itself: the far
        // session's own turns arrive as it makes them, so a line claiming it is
        // working would sit under the evidence that it is.
        private ChatTurn? _workingNote;

        public void SetWorking(bool working)
        {
            // Same rule as OnInbound, for the same reason: the live view only
            // supersedes the working line once it is actually showing the work.
            if (_mirroring && _painted) return;

            if (working)
            {
                if (_workingNote is not null) return;

                // IsComplete false rather than true: this is a turn still in
                // progress, which is what the flag means everywhere else, and it
                // keeps the row from reading as a finished statement.
                _workingNote = new ChatTurn
                {
                    Role = ChatRole.System,
                    Text = $"{_remoteName} is working…",
                    IsComplete = false
                };

                Add(_workingNote);
                return;
            }

            // Went idle without answering. The note still comes off — a stale
            // "working…" is worse than no indicator, because it is a claim rather
            // than an absence.
            ClearWorkingNote();
        }

        private void ClearWorkingNote()
        {
            if (_workingNote is null) return;

            var note = _workingNote;
            _workingNote = null;

            // Removed rather than rewritten. Turning it into "finished" would
            // leave a line nobody needs in a transcript that is only ever a
            // handful of turns long.
            if (_history.Remove(note)) Removed?.Invoke(note);
        }

        // The panel rebuilds its list from History when this fires. There is no
        // TurnRemoved on IRemoteChatSession — nothing else has ever needed to
        // take a turn back — so this is deliberately local to this class and the
        // panel subscribes only when it recognises the type.
        public event Action<ChatTurn>? Removed;

        // Said out loud rather than silently dropping the conversation, because
        // an idle shutdown is invisible from the panel: nothing on screen
        // changes, and the next message would otherwise be the first hint.
        public void OnBridgeStopped(string why)
        {
            if (State == RemoteChatState.Error) return;

            Note($"The relay session stopped ({why}). Sending again will start it back up.");
        }

        public void Cancel()
        {
            // Nothing to cancel, in either mode, and for two different reasons.
            //
            // Messaging: stopping work on another machine is not something that
            // channel can do — SendMessage delivers a message, it does not
            // interrupt a run.
            //
            // Live view: it could, in principle — Escape is what interrupts the
            // TUI and the far Buddy can send a key as easily as a line — but the
            // protocol carries no key frame yet, and a Cancel that silently did
            // nothing while looking like it worked would be worse than none.
        }

        // --- the panel coming and going --------------------------------------

        // Whether anyone is actually looking. A live view is the one part of
        // this that costs something while unwatched — it holds a subscription on
        // the other machine's Buddy, and that keeps a real session on the user's
        // account awake — so it follows the panel rather than this object, which
        // deliberately outlives every panel that shows it.
        private bool _panelOpen;

        public void PanelOpened()
        {
            _panelOpen = true;

            // The handshake may have finished while nothing was open.
            if (!_mirroring) TryUpgrade();
            else _ = RemoteControlSessions.MirrorClientFor(_account)?.ReopenAsync(_remoteName);
        }

        public void PanelClosed()
        {
            _panelOpen = false;

            // Whatever was in flight is no longer being waited for by anybody.
            // Reopening starts a fresh fetch (CloseAsync drops the feed), so a
            // wait carried across the gap would be timing something that had
            // already been abandoned.
            EndWait();

            if (!_mirroring) return;

            // The history stays exactly as it is. Only the subscription goes:
            // reopening re-reads the tail, which is cheap and is also the right
            // thing, since the conversation will have moved on.
            _ = RemoteControlSessions.MirrorClientFor(_account)?.CloseAsync(_remoteName);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            RemoteControlSessions.MirrorChanged -= OnMirrorChanged;

            var client = RemoteControlSessions.MirrorClientFor(_account);
            if (client is null) return;

            client.Delivered -= OnDelivered;
            client.Failed -= OnMirrorFailed;

            if (_mirroring) _ = client.CloseAsync(_remoteName);
        }

        // Everything that touches the history has to land on the UI thread,
        // which is the contract IRemoteChatSession states. Run inline when
        // already there rather than always posting: a mirror delivered from the
        // relay's pump is on a background thread, but one delivered inside a
        // test — or by a reopen from a click — is not, and posting there would
        // defer the update behind a dispatcher turn nobody pumps.
        internal static void OnUi(Action work)
        {
            if (Dispatcher.UIThread.CheckAccess()) work();
            else Dispatcher.UIThread.Post(work);
        }

        private void Note(string text) => Add(new ChatTurn
        {
            Role = ChatRole.System,
            IsComplete = true,
            Text = text
        });

        private void Add(ChatTurn turn)
        {
            _history.Add(turn);
            Trim();

            if (Dispatcher.UIThread.CheckAccess()) TurnAdded?.Invoke(turn);
            else Dispatcher.UIThread.Post(() => TurnAdded?.Invoke(turn));
        }

        private void Trim()
        {
            // Bounded for the same reason the local sessions' history is: a
            // panel left open on a chatty conversation should not grow without
            // limit.
            //
            // Live view keeps as much as a local panel does, because it is one:
            // it is showing a real transcript and scrolling back through it.
            // Messaging keeps less because that channel is low-volume by nature
            // — every turn in it is something a person typed or a machine
            // answered.
            var keep = _mirroring ? 500 : 200;
            if (_history.Count > keep) _history.RemoveRange(0, _history.Count - keep);
        }

        internal void SetState(RemoteChatState state)
        {
            if (State == state) return;

            State = state;

            if (Dispatcher.UIThread.CheckAccess()) StateChanged?.Invoke(state);
            else Dispatcher.UIThread.Post(() => StateChanged?.Invoke(state));
        }
    }
}
