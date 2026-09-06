using System.Text.Json;

namespace ClaudeBuddy
{
    // CB-93: when the gateway refuses to serve a picture named by
    // `MEDIA:<path>`, it will say why if asked — `&meta=1` on the same
    // assistant-media route answers a capability question instead of bytes:
    //
    //   {"available":true,"mediaTicket":"v1.…","mediaTicketExpiresAt":"…"}
    //   {"available":false,"code":"outside-allowed-folders","reason":"…"}
    //
    // This turns that answer into what the panel shows: a one-line reason in
    // the slot the picture would have occupied, and a tooltip carrying the
    // path and the raw code. Pure, no window, no settings — the same habit
    // as OrbGlyph and OrbArrangement — so the messages are one function call
    // away from a test rather than something only visible on a real gateway.
    //
    // Kept out of OpenClawMedia.cs deliberately: that file is the OS-viewer
    // call (Preview.app), excluded from coverage whole. Nothing here opens
    // anything.
    internal static class OpenClawMediaRefusal
    {
        // The happy-path guard. A meta round trip doubles the request count
        // for a fetch, so it is only worth asking once the plain fetch has
        // already come back empty — never before or instead of it — and only
        // against a route that can actually answer `&meta=1` at all. An
        // ordinary `[media attached: ...]` marker resolves through the same
        // TurnView.LoadImage path but has no meta variant; asking it would be
        // a second wasted request against a route that was never going to
        // explain itself.
        internal static bool ShouldAskWhy(byte[]? bytes, string? url) =>
            (bytes is null || bytes.Length == 0)
            && url is not null
            && url.StartsWith(OpenClawSessions.AssistantMediaRoute, StringComparison.Ordinal);

        // The route a meta question asks, built the same way
        // OpenClawSessions.FetchLocalMediaAsync builds the ordinary fetch —
        // same prefix, same escaping, `&meta=1` appended.
        internal static string MetaRoute(string path) =>
            OpenClawSessions.AssistantMediaRoute + Uri.EscapeDataString(path) + "&meta=1";

        // The reverse of MetaRoute's escaping, for the one call site that
        // only has the built url (TurnView.LoadImage, via ChatTurn.ImageUrl)
        // rather than the raw path a MEDIA: marker was matched against
        // (OpenClawChatSession.TryResolveLocalMedia, which already has it).
        // Null for a url this route didn't build, which ShouldAskWhy already
        // refuses to ask meta about.
        internal static string? PathFromUrl(string? url) =>
            url is not null
            && url.StartsWith(OpenClawSessions.AssistantMediaRoute, StringComparison.Ordinal)
                ? Uri.UnescapeDataString(url[OpenClawSessions.AssistantMediaRoute.Length..])
                : null;

        private const string Prefix = "Picture not shown — ";

        // Cap on a gateway-supplied reason, so a verbose message from the
        // other end doesn't blow out the bubble's width. 200 is generous for
        // a sentence and short of anything that would wrap more than a line
        // or two at the note's own small font size.
        private const int MaxReasonLength = 200;

        // The gateway's meta answer, as one sentence. json is the raw HTTP
        // body — null when the meta request itself never got an answer
        // (gateway down, no token, TLS refused), which gets the honest "don't
        // know" line rather than a fabricated cause; this project already
        // keeps that distinction between confirmed and assumed everywhere
        // else, and inventing a reason here would break it.
        internal static string Explain(string? json)
        {
            if (!TryParse(json, out var root)) return Prefix + "couldn't ask the gateway why.";

            if (root.TryGetProperty("available", out var a) && a.ValueKind == JsonValueKind.True)
                return Prefix + "the gateway has the file but the fetch didn't finish.";

            var code = StringOrNull(root, "code");
            var reason = StringOrNull(root, "reason")?.Trim();

            if (string.Equals(code, "outside-allowed-folders", StringComparison.Ordinal))
            {
                return Prefix + "the gateway won't serve files from that folder. Ask the agent to "
                    + "write it to ~/.openclaw/media/, which is allowed for every agent.";
            }

            if (!string.IsNullOrEmpty(reason))
            {
                var trimmed = reason!.Length > MaxReasonLength ? reason[..MaxReasonLength] : reason;
                return Prefix + "the gateway refused it: " + trimmed;
            }

            return string.IsNullOrEmpty(code)
                ? Prefix + "the gateway refused it."
                : Prefix + $"the gateway refused it ({code}).";
        }

        // The tooltip on that line: the path the agent named, plus the
        // gateway's own code when the meta answer had one. Never the reason —
        // that is already in the line above, and repeating it in the tooltip
        // says nothing new.
        internal static string? Detail(string? json, string path)
        {
            if (!TryParse(json, out var root)) return path;

            var code = StringOrNull(root, "code");
            return string.IsNullOrEmpty(code) ? path : $"{path} — {code}";
        }

        private static bool TryParse(string? json, out JsonElement root)
        {
            root = default;
            if (string.IsNullOrWhiteSpace(json)) return false;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

                root = doc.RootElement.Clone();
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string? StringOrNull(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
