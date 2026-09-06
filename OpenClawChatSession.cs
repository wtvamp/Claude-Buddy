using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Avalonia.Threading;

namespace ClaudeBuddy
{
    // One turn as the history parser hands it over: what TurnsFromHistory reads
    // out of a page of chat.history, and what SetHistory and PrependHistory put
    // into a transcript.
    //
    // A record rather than the seven-field tuple this was. The tuple was
    // tolerable while it was three fields and stopped being so at seven — every
    // producer and consumer restated the whole shape in its own signature, so
    // the shape appeared eight times in two files plus once in each test's
    // helper, and adding a field meant editing all of them before anything
    // compiled again. Named members also read at the call site: `turn.Speaker`
    // says what `t.Item6` does not.
    //
    // A struct because it is a value and is treated as one — a page is a few
    // hundred of these, each copied into a ChatTurn immediately, and nothing
    // ever holds on to one.
    internal readonly record struct HistoryTurn(
        ChatRole Role,
        string Text,
        string? ImageUrl,
        string ImageAlt,
        DateTimeOffset At,
        string? Speaker,
        string? SpeakerColor,

        // Whether the person at this keyboard said it — see ChatTurn.Mine, whose
        // value this becomes. Defaulted, because only the OpenClaw history
        // parser is in a position to answer it and every other producer of a
        // HistoryTurn would otherwise have to write `false` to say nothing.
        bool Mine = false,

        // The picture itself, when the block carried it inline rather than
        // naming somewhere to fetch it from. This is the shape this gateway
        // actually emits — `{type:"image", data:"<base64>", mimeType:...}`
        // with no url at all (CB-91) — so a turn generally has *either* this
        // or ImageUrl, never both. Defaulted for the same reason Mine is:
        // only TurnsFromHistory is in a position to fill it in.
        byte[]? ImageBytes = null);

    // One OpenClaw session, as something the chat panel can talk to.
    //
    // Reading works today. **Sending does not**, and says so rather than
    // failing quietly: the app pairs itself with `operator.read` and nothing
    // else, so `chat.send` would be refused by the gateway. Widening that is a
    // deliberate act — it means re-pairing this device with `operator.write`,
    // which a person has to approve on the gateway — and it is not something
    // opening a window should do on their behalf. Until then this is a reader
    // with an input box that explains itself.
    internal sealed class OpenClawChatSession : IRemoteChatSession, IRemoteChatBacklog, IRemoteChatComposer
    {
        private readonly List<ChatTurn> _history = new();

        // The turn currently being streamed, if any. Held so an `agent` event
        // can update it in place rather than appending a row per delta.
        private ChatTurn? _streaming;

        // Which stream the turn above belongs to. An agent emits "thinking"
        // and then "assistant", each as its own growing snapshot, so they have
        // to become two turns — appending one to the other would produce a
        // paragraph that says the same thing twice in different voices.
        private string? _streamingKind;

        // Turns already asked about, or being asked about — so a streaming
        // snapshot that still carries the marker on its next delta doesn't
        // fire a second gateway round trip for the same picture.
        private readonly HashSet<ChatTurn> _pendingImageChecks = new();

        public OpenClawChatSession(string sessionId, string gatewayKey, string displayName)
        {
            SessionId = sessionId;
            GatewayKey = gatewayKey;
            DisplayName = displayName;
        }

        public string SessionId { get; }

        // The gateway's own key, without the "openclaw:" prefix the app adds to
        // keep session ids in one namespace.
        public string GatewayKey { get; }

        // Settable, because the name can improve after the session was created:
        // agents.list arrives moments after the connection does, so a panel
        // opened in that window would otherwise keep the raw id ("main") in its
        // header for as long as the app runs.
        public string DisplayName { get; set; }

        public RemoteChatState State { get; private set; } = RemoteChatState.Connected;

        // Where this conversation lives, when it lives somewhere — a Discord DM,
        // a channel. Null for a session with no channel behind it, which is the
        // signal not to mirror anything anywhere.
        public OpenClawSessions.Delivery? Delivery { get; set; }

        public IReadOnlyList<ChatTurn> History => _history;

        public event Action<ChatTurn>? TurnAdded;
        public event Action<ChatTurn>? TurnUpdated;
        public event Action<RemoteChatState>? StateChanged;

        public async Task SendAsync(string text)
        {
            if (!ClaudeBuddySettings.OpenClawReplyEnabled)
            {
                // A System turn rather than an exception: the person has just
                // typed a sentence, and losing it behind a dialog would be a
                // poor answer to "why didn't that send".
                Note("Replying is off. Turn on \"Allow replying to agents\" in Settings — "
                   + "it asks the gateway for permission to write, which you approve there.");
                return;
            }

            // The user's own turn is added here rather than by the panel, so one
            // thing owns the transcript and a send that fails leaves a message
            // on screen with an explanation under it rather than a ghost.
            // Mine, said here rather than inferred: this is the one turn in the
            // app whose author is not in doubt, and marking it keeps it matching
            // the copy that comes back from the gateway a moment later — which
            // is what lets a room dedupe the two instead of drawing both.
            var mine = new ChatTurn
            {
                Role = ChatRole.User, Text = text, IsComplete = true, Mine = true
            };
            Add(mine);

            var failure = await SendOrFailureAsync(text);
            if (failure is not null) Note("Couldn't send: " + failure);
        }

        // The request, and the catch around it, moved behind a method that
        // returns the failure instead of throwing it.
        //
        // Excluded from coverage because it is the gateway call, but the shape is
        // what matters: an await that always faults never resumes, so the line
        // that awaited it is reported unhit even though the catch beside it runs.
        // Returning the message rather than throwing it means the caller's await
        // completes, and the decision that reads it — say so in the transcript —
        // is measured where it belongs.
        [ExcludeFromCodeCoverage]
        private async Task<string?> SendOrFailureAsync(string text)
        {
            try
            {
                await OpenClawSessions.SendAsync(this, text, CancellationToken.None);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private void Note(string text) => Add(new ChatTurn
        {
            Role = ChatRole.System,
            IsComplete = true,
            Text = text
        });

        public void Cancel()
        {
            // Nothing to cancel while this is read-only. Stopping someone else's
            // run — one started from Discord or a cron schedule — is not
            // something a viewer should be able to do by accident anyway.
        }

        // Fed from OpenClawSessions' event stream. Everything here is already on
        // the UI thread, which is the contract IRemoteChatSession states and the
        // panel relies on.
        public void OnAgentEvent(string name, JsonElement payload)
        {
            switch (name)
            {
                case "agent":
                    OnAgentText(payload);
                    break;

                case "session.tool":
                    OnTool(payload);
                    break;

                case "cron" when Str(payload, "action") == "finished":
                case "task" when Str(payload, "action") == "upserted":
                    Complete();
                    break;
            }
        }

        private void OnAgentText(JsonElement payload)
        {
            if (!payload.TryGetProperty("data", out var data)) return;

            // data.text is a full snapshot of the turn so far, alongside a
            // data.delta. Using the snapshot means a dropped or coalesced event
            // costs nothing — which is exactly the property the panel is written
            // against, so it is worth taking even though the delta is right
            // there and looks cheaper.
            var text = Str(data, "text");
            if (string.IsNullOrEmpty(text)) return;

            // Thinking is shown — watching an agent think is most of the value
            // of an orb that pulses — but kept as its own turn rather than
            // mixed into the reply it will eventually give.
            var kind = Str(payload, "stream") ?? "assistant";

            if (_streaming is null || _streaming.IsComplete || _streamingKind != kind)
            {
                _streaming = new ChatTurn
                {
                    Role = kind == "thinking" ? ChatRole.System : ChatRole.Assistant,
                    Text = text
                };
                _streamingKind = kind;
                Add(_streaming);
            }
            else
            {
                _streaming.Text = text;
                TurnUpdated?.Invoke(_streaming);
            }

            // A live snapshot never carries the picture itself — only a
            // TurnsFromHistory read of chat.history does (see
            // OpenClawSessions.BestImageMatch) — so a reply that mentions an
            // attachment is worth asking the gateway about, once, rather than
            // leaving it as text-only until the panel happens to reload.
            if (text.Contains(OpenClawSessions.MediaAttachedMarker, StringComparison.Ordinal))
            {
                TryResolveLiveImage(_streaming);
                return;
            }

            // A picture the agent generated itself and named by its own path
            // on the gateway host — see LocalMediaPathFrom's own comment for
            // why this is a second, distinct convention from the marker
            // above rather than the same one.
            var localPath = OpenClawSessions.LocalMediaPathFrom(text);
            if (localPath is not null)
            {
                TryResolveLocalMedia(_streaming, localPath);
            }
        }

        // Best-effort and one-shot per turn, the same shape as TurnView's own
        // LoadImage: a picture that doesn't resolve is a picture that doesn't
        // resolve, and the text beside it still reads. Fetching the newest
        // page rather than matching on anything in the event itself, because
        // nothing in a live "agent" event ties back to a chat.history message
        // — see BestImageMatch's own comment.
        //
        // No try/catch around the fetch: FetchPageAsync already swallows a
        // gateway that will not answer and returns null, the same contract
        // LoadOlderAsync already trusts without one of its own — adding a
        // second catch here would only ever guard against nothing.
        //
        // No explicit Dispatcher.Post after the await: this runs under the
        // app's own Avalonia SynchronizationContext, which already resumes an
        // await's continuation on the UI thread — the same reason TurnView's
        // LoadImage sets its own bound property directly.
        private async void TryResolveLiveImage(ChatTurn turn)
        {
            if (!_pendingImageChecks.Add(turn)) return;

            var page = await OpenClawSessions.FetchPageAsync(this, 0, CancellationToken.None);
            if (page is null) return;

            // Restricted to this turn's own role: a session's own
            // chat.history mixes the agent's replies with everyone else's
            // messages arriving as input to it (see OpenClawRoomChat's own
            // header comment), and a picture someone else posted moments
            // before or after the agent's reply is not the agent's picture —
            // matching across roles would occasionally attribute the wrong
            // one in a busy room.
            var match = OpenClawSessions.BestImageMatch(page.Value.Turns, turn.Role, turn.At);
            if (match is null) return;

            // Whichever form the matched turn actually carries. On this
            // gateway it is always the inline bytes (CB-91); the url arm is
            // kept for a deployment that sends one instead. Setting only
            // ImageUrl, as this did before, resolved to nothing at all here.
            //
            // No third arm for "neither": BestImageMatch only returns a turn
            // that has one or the other, so restating that here would be a
            // branch nothing could ever take.
            if (match.Value.ImageBytes is { Length: > 0 } matchedBytes) turn.ImageBytes = matchedBytes;
            else turn.ImageUrl = match.Value.ImageUrl;

            turn.ImageAlt = match.Value.ImageAlt;

            // OpenClawRoomChat only rebuilds a room's merged view on this
            // event (see its chat.TurnUpdated subscription) — the
            // PropertyChanged the setters above already raised reaches a
            // direct (non-room) panel through TurnView's own subscription,
            // but a room's Rebuild() has to be asked separately.
            TurnUpdated?.Invoke(turn);
        }

        // CB-88: an agent's own generated picture, named by its own path on
        // the gateway host rather than fetchable by URL — fetched through the
        // gateway's own read-scoped media route (see
        // OpenClawSessions.FetchLocalMediaAsync), with LocalMediaPathFrom
        // deciding what counts as such a reference. Same one-shot-per-turn
        // guard as TryResolveLiveImage; the two never fire for the same turn
        // since OnAgentText only ever detects one marker or the other.
        private async void TryResolveLocalMedia(ChatTurn turn, string path)
        {
            if (!_pendingImageChecks.Add(turn)) return;

            var bytes = await OpenClawSessions.FetchLocalMediaAsync(path, CancellationToken.None);
            if (bytes is { Length: > 0 })
            {
                turn.ImageBytes = bytes;

                // See TryResolveLiveImage's identical comment: OpenClawRoomChat
                // needs its own nudge to rebuild, beyond the PropertyChanged the
                // setter above already raised.
                TurnUpdated?.Invoke(turn);
                return;
            }

            // CB-93: the fetch above came back empty, which used to leave the
            // turn as bare "MEDIA:<path>" text with no explanation. No
            // ShouldAskWhy guard needed here, unlike TurnView.LoadImage's
            // history-path twin: this method is only ever reached with a
            // path LocalMediaPathFrom already matched to the gateway's
            // read-scoped media route, so there is no ordinary attachment
            // url to protect against asking a meta question that has no
            // answer.
            var json = await OpenClawSessions.FetchLocalMediaMetaAsync(path, CancellationToken.None);
            turn.ImageNoteDetail = OpenClawMediaRefusal.Detail(json, path);
            turn.ImageNote = OpenClawMediaRefusal.Explain(json);

            TurnUpdated?.Invoke(turn);
        }

        private void OnTool(JsonElement payload)
        {
            if (!payload.TryGetProperty("data", out var data)) return;
            if (Str(data, "phase") != "start") return;

            var tool = Str(data, "name");
            if (string.IsNullOrEmpty(tool)) return;

            // One line per tool call, in the transcript rather than in a status
            // area: what an agent reached for is part of what it said.
            Add(new ChatTurn
            {
                Role = ChatRole.System,
                IsComplete = true,
                Text = "· " + tool
            });
        }

        private void Complete()
        {
            if (_streaming is null) return;

            _streaming.IsComplete = true;
            TurnUpdated?.Invoke(_streaming);
            _streaming = null;
            _streamingKind = null;
        }

        private void Add(ChatTurn turn)
        {
            _history.Add(turn);

            // Generous, because this is now the only thing that discards
            // anything: at 60 a busy conversation dropped its own beginning
            // while you were reading it, which is what "stuff disappears"
            // looked like. Scrolling back can load more than this, so the cap
            // is high enough that reaching it means a genuinely enormous
            // scrollback rather than an ordinary afternoon.
            const int Keep = 500;
            if (_history.Count > Keep) _history.RemoveRange(0, _history.Count - Keep);

            TurnAdded?.Invoke(turn);
        }

        // The backlog, once the gateway has told us what it is. Replaces
        // whatever is there rather than merging: this arrives moments after the
        // panel opens, and the alternative is reconciling two orderings of the
        // same conversation for the sake of a turn or two that might have
        // landed in between.
        //
        // Historical turns are marked complete so a live reply that arrives
        // next starts its own row instead of appending to the last thing
        // somebody said an hour ago.
        // How many of the gateway's own messages this transcript has consumed,
        // which is the offset the next page back starts at. Not the same as the
        // number of turns: one message can be text plus three pictures, and
        // some are dropped as scaffolding.
        public int LoadedMessages { get; set; }

        // False once the gateway answers a page with nothing left to give.
        public bool HasMore { get; set; } = true;

        // The fetch itself lives on OpenClawSessions, which owns the connection;
        // this is only the seam the panel reaches it through, so the panel does
        // not have to know that a page comes from a gateway rather than a file.
        public Task<bool> LoadOlderAsync(CancellationToken ct) =>
            OpenClawSessions.LoadOlderAsync(this, ct);

        public string ComposerHint => ClaudeBuddySettings.OpenClawReplyEnabled
            ? "Message…"
            : "Replying is off";

        // Older turns, from scrolling back. Prepended rather than replacing, and
        // raising its own event, because the panel has to put the scroll
        // position back afterwards — content appearing above where you are
        // reading would otherwise throw you down the page.
        public void PrependHistory(IReadOnlyList<HistoryTurn> turns)
        {
            if (turns.Count == 0) return;

            var older = turns.Select(t => new ChatTurn
            {
                Role = t.Role,
                Text = t.Text,
                ImageUrl = t.ImageUrl,
                ImageBytes = t.ImageBytes,
                ImageAlt = t.ImageAlt,
                At = t.At,
                Speaker = t.Speaker,
                SpeakerColor = t.SpeakerColor,
                Mine = t.Mine,
                IsComplete = true
            }).ToList();

            _history.InsertRange(0, older);
            HistoryPrepended?.Invoke(older.Count);
        }

        public event Action<int>? HistoryPrepended;

        public void SetHistory(IReadOnlyList<HistoryTurn> turns)
        {
            if (turns.Count == 0) return;

            _history.Clear();
            _streaming = null;
            _streamingKind = null;
            HasMore = true;

            foreach (var turn in turns)
            {
                _history.Add(new ChatTurn
                {
                    Role = turn.Role,
                    Text = turn.Text,
                    ImageUrl = turn.ImageUrl,
                    ImageBytes = turn.ImageBytes,
                    ImageAlt = turn.ImageAlt,
                    At = turn.At,
                    Speaker = turn.Speaker,
                    SpeakerColor = turn.SpeakerColor,
                    Mine = turn.Mine,
                    IsComplete = true
                });
            }

            HistoryReplaced?.Invoke();
        }

        // Raised when the whole transcript changes underneath the panel, which
        // TurnAdded can't express. Deliberately not on IRemoteChatSession: the
        // panel treats it as an optional extra, so an implementation that only
        // ever appends — the fake this was developed against — needs nothing.
        public event Action? HistoryReplaced;

        public void SetState(RemoteChatState state)
        {
            if (State == state) return;

            State = state;
            StateChanged?.Invoke(state);
        }

        private static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
    }
}
