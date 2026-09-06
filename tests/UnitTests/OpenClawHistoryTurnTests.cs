using System;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// One page of the gateway's chat.history, turned into turns the panel can draw.
//
// A format nobody here controls, which is the whole reason this is worth testing
// against fixtures rather than reading — the same argument that keeps
// ChatTranscript and CodexTranscript pure and separately covered.
//
// The bug this parser's own comment records is the one to hold onto: the two roles
// are shaped differently. An assistant turn carries `content` as a list of
// blocks; a user turn carries it as a plain string. Reading only the block form
// showed an agent talking to nobody — half the conversation silently gone, with
// nothing on screen to suggest anything was missing.
//
// Serialised because the parser resolves speaker names and colours through the
// process-wide identity table.
[Collection("Settings")]
public class OpenClawHistoryTurnTests
{
    private static JsonElement Messages(string json) =>
        JsonDocument.Parse(json).RootElement;

    private static System.Collections.Generic.List<HistoryTurn> Turns(string json) =>
        OpenClawSessions.TurnsFromHistory(Messages(json));

    // ---- the two role shapes --------------------------------------------

    // A user turn's content is a plain string. This is the half that was being
    // dropped.
    [Fact]
    public void AUserTurnCarriesItsContentAsAString()
    {
        var turns = Turns("""[{"role":"user","content":"fix the arrangement test"}]""");

        var turn = Assert.Single(turns);
        Assert.Equal(ChatRole.User, turn.Role);
        Assert.Equal("fix the arrangement test", turn.Text);
    }

    // An assistant turn's content is a list of blocks, and only the text ones are
    // worth showing — a replayed tool_use block would be a wall of JSON, and tool
    // calls arrive live as their own turns anyway.
    [Fact]
    public void AnAssistantTurnCarriesBlocksAndOnlyTheTextOnesAreShown()
    {
        var turns = Turns("""
        [{"role":"assistant","content":[
            {"type":"text","text":"Fixed the nested-team case."},
            {"type":"tool_use","name":"Edit","input":{"file":"a.cs"}}]}]
        """);

        var turn = Assert.Single(turns);
        Assert.Equal(ChatRole.Assistant, turn.Role);
        Assert.Equal("Fixed the nested-team case.", turn.Text);
        Assert.DoesNotContain("tool_use", turn.Text);
    }

    // Both shapes in one page, which is what a real conversation is.
    [Fact]
    public void BothShapesInOnePageAreBothRead()
    {
        var turns = Turns("""
        [{"role":"user","content":"is it green?"},
         {"role":"assistant","content":[{"type":"text","text":"yes"}]}]
        """);

        Assert.Equal(2, turns.Count);
        Assert.Equal(ChatRole.User, turns[0].Role);
        Assert.Equal(ChatRole.Assistant, turns[1].Role);
    }

    // Several text blocks in one message are joined rather than becoming several
    // bubbles — they are one thing the agent said.
    [Fact]
    public void SeveralTextBlocksAreJoinedIntoOneTurn()
    {
        var turns = Turns("""
        [{"role":"assistant","content":[
            {"type":"text","text":"first line"},
            {"type":"text","text":"second line"}]}]
        """);

        var turn = Assert.Single(turns);
        Assert.Contains("first line", turn.Text);
        Assert.Contains("second line", turn.Text);
    }

    // A third shape, tolerated: content as a single object with a text field.
    [Fact]
    public void ContentAsASingleObjectIsRead()
    {
        var turns = Turns("""[{"role":"assistant","content":{"text":"just this"}}]""");

        Assert.Equal("just this", Assert.Single(turns).Text);
    }

    // Any role that is not "user" is treated as the assistant, so a role this
    // version has not seen still shows up rather than vanishing.
    [Fact]
    public void AnUnknownRoleIsTreatedAsTheAssistant()
    {
        var turns = Turns("""[{"role":"tool","content":"something"}]""");

        Assert.Equal(ChatRole.Assistant, Assert.Single(turns).Role);
    }

    // ---- what is dropped -------------------------------------------------

    [Fact]
    public void AMessageWithNoContentAtAllIsSkipped()
    {
        Assert.Empty(Turns("""[{"role":"user"}]"""));
    }

    [Fact]
    public void AMessageWhoseTextIsBlankIsSkipped()
    {
        Assert.Empty(Turns("""[{"role":"user","content":"   "}]"""));
        Assert.Empty(Turns("""[{"role":"assistant","content":[{"type":"text","text":""}]}]"""));
    }

    // A message with only non-text blocks produces nothing rather than an empty
    // bubble.
    [Fact]
    public void AMessageOfOnlyToolBlocksIsSkipped()
    {
        Assert.Empty(Turns("""
        [{"role":"assistant","content":[{"type":"tool_use","name":"Edit"}]}]
        """));
    }

    [Fact]
    public void AnEmptyPageProducesNoTurns()
    {
        Assert.Empty(Turns("[]"));
    }

    // The resumed-session notice goes through Readable, which drops it — so it is
    // not drawn even though it arrives as an ordinary user-role message.
    [Fact]
    public void TheResumedSessionNoticeDoesNotBecomeATurn()
    {
        Assert.Empty(Turns(
            """[{"role":"user","content":"OpenClaw resumed this CLI session after a restart"}]"""));
    }

    // ---- pictures --------------------------------------------------------

    // A picture is its own turn rather than being folded into the text of one. A
    // message is commonly several images and nothing else, and one bubble holding
    // four of them stacked reads worse than four bubbles.
    [Fact]
    public void AnImageBlockBecomesItsOwnTurn()
    {
        var turns = Turns("""
        [{"role":"user","content":[{"type":"image","url":"https://x/a.png","alt":"a graph"}]}]
        """);

        var turn = Assert.Single(turns);
        Assert.Equal("https://x/a.png", turn.ImageUrl);
        Assert.Equal("a graph", turn.ImageAlt);
        Assert.Equal("", turn.Text);
    }

    [Fact]
    public void SeveralImagesBecomeSeveralTurns()
    {
        var turns = Turns("""
        [{"role":"user","content":[
            {"type":"image","url":"https://x/a.png"},
            {"type":"image","url":"https://x/b.png"},
            {"type":"image","url":"https://x/c.png"}]}]
        """);

        Assert.Equal(3, turns.Count);
        Assert.All(turns, t => Assert.NotNull(t.ImageUrl));
    }

    // Text and a picture in one message produce both, with the picture first —
    // and the text turn carries no image, so the panel does not draw it twice.
    [Fact]
    public void TextAndAPictureBecomeTwoTurns()
    {
        var turns = Turns("""
        [{"role":"assistant","content":[
            {"type":"image","url":"https://x/a.png"},
            {"type":"text","text":"here it is"}]}]
        """);

        Assert.Equal(2, turns.Count);
        Assert.Equal("https://x/a.png", turns[0].ImageUrl);
        Assert.Equal("here it is", turns[1].Text);
        Assert.Null(turns[1].ImageUrl);
    }

    // An image block with no url is skipped rather than becoming a turn the panel
    // would try and fail to fetch.
    [Fact]
    public void AnImageWithNoUrlIsSkipped()
    {
        Assert.Empty(Turns("""
        [{"role":"user","content":[{"type":"image","alt":"nothing"}]}]
        """));
    }

    [Fact]
    public void AnImageWithNoAltGetsAnEmptyAltRatherThanNull()
    {
        var turns = Turns("""
        [{"role":"user","content":[{"type":"image","url":"https://x/a.png"}]}]
        """);

        Assert.Equal("", Assert.Single(turns).ImageAlt);
    }

    // ---- pictures carried inline (CB-91) ---------------------------------

    // The shape this gateway actually sends. Every real image block in its
    // own stored transcripts is data+mimeType with no url at all, and the
    // parser used to require a url and so dropped all of them silently.
    //
    // The base64 here is a one-pixel PNG, the same fixture the rest of this
    // repo's image tests use.
    private const string PixelBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg==";

    [Fact]
    public void AnImageBlockCarryingItsBytesInlineBecomesATurnWithThoseBytes()
    {
        var turns = Turns($$"""
        [{"role":"assistant","content":[
            {"type":"image","data":"{{PixelBase64}}","mimeType":"image/png"}]}]
        """);

        var turn = Assert.Single(turns);
        Assert.Equal(Convert.FromBase64String(PixelBase64), turn.ImageBytes);
        Assert.Null(turn.ImageUrl);
        Assert.Equal("", turn.Text);
    }

    // The gateway spells inline bytes both ways in different places — bare
    // base64 in these blocks, a data: URI in agents.list's avatarUrl — so
    // both are accepted here.
    [Fact]
    public void AnImageBlockCarryingADataUriAlsoBecomesBytes()
    {
        var turns = Turns($$"""
        [{"role":"assistant","content":[
            {"type":"image","data":"data:image/png;base64,{{PixelBase64}}"}]}]
        """);

        Assert.Equal(Convert.FromBase64String(PixelBase64), Assert.Single(turns).ImageBytes);
    }

    // A url still wins where one is given, so a deployment that sends the
    // url form keeps working exactly as before.
    [Fact]
    public void AUrlIsStillPreferredWhenTheBlockCarriesOne()
    {
        var turns = Turns($$"""
        [{"role":"assistant","content":[
            {"type":"image","url":"https://x/a.png","data":"{{PixelBase64}}"}]}]
        """);

        var turn = Assert.Single(turns);
        Assert.Equal("https://x/a.png", turn.ImageUrl);
        Assert.Null(turn.ImageBytes);
    }

    [Fact]
    public void AnImageBlockWithNeitherUrlNorDataIsSkipped()
    {
        Assert.Empty(Turns("""
        [{"role":"assistant","content":[{"type":"image","mimeType":"image/png"}]}]
        """));
    }

    // Data that is not base64 at all is a picture that cannot be shown, not
    // an exception and not an empty bubble.
    [Fact]
    public void AnImageBlockWhoseDataIsNotBase64IsSkipped()
    {
        Assert.Empty(Turns("""
        [{"role":"assistant","content":[{"type":"image","data":"not base64 !!!"}]}]
        """));
    }

    // A data: URI with an empty payload decodes to a real, zero-length array
    // rather than to null, so it reached the turn list as a picture that
    // cannot be drawn — no text, no url, nothing to show. Zero bytes is no
    // more a picture than a missing url is, which is what
    // BestImageMatch already says about the same value.
    [Fact]
    public void AnImageBlockWhoseDataUriCarriesNoPayloadIsSkipped()
    {
        Assert.Empty(Turns("""
        [{"role":"assistant","content":[{"type":"image","data":"data:image/png;base64,"}]}]
        """));
    }

    // QA (CB-91): a whitespace-only url used to survive alongside decoded
    // bytes, and the panel — which asks IsNullOrEmpty rather than
    // IsNullOrWhiteSpace — would then try to fetch "   " and never draw the
    // bytes sitting right beside it. One spelling of "no url" now.
    [Fact]
    public void AWhitespaceOnlyUrlIsNotAUrlAndTheInlineBytesAreUsed()
    {
        var turns = Turns($$"""
        [{"role":"assistant","content":[
            {"type":"image","url":"   ","data":"{{PixelBase64}}"}]}]
        """);

        var turn = Assert.Single(turns);
        Assert.Null(turn.ImageUrl);
        Assert.Equal(Convert.FromBase64String(PixelBase64), turn.ImageBytes);
    }

    [Fact]
    public void SeveralInlineImagesBecomeSeveralTurns()
    {
        var turns = Turns($$"""
        [{"role":"assistant","content":[
            {"type":"image","data":"{{PixelBase64}}"},
            {"type":"image","data":"{{PixelBase64}}"}]}]
        """);

        Assert.Equal(2, turns.Count);
        Assert.All(turns, t => Assert.NotNull(t.ImageBytes));
    }

    // An object-shaped content with no text in it at all. Worth a case
    // because it is the one arm of TextOf's switch that a real page never
    // seems to produce, and without it the `?? ""` there is a branch nothing
    // asks about — the same gap that hid a live defect twice on this feature.
    [Fact]
    public void AnObjectContentWithNoTextProducesNoTurn()
    {
        Assert.Empty(Turns("""
        [{"role":"assistant","content":{"mimeType":"image/png"}}]
        """));
    }

    // ---- a picture the gateway delivered (CB-94) -------------------------

    // The exact record shape read off the gateway's own stored transcript for
    // the drop Warren screenshotted: a delivery-mirror whose content is the
    // bare filename. It becomes a picture turn pointing at the shared media
    // directory through the read-scoped route.
    [Fact]
    public void ADeliveredPictureBecomesAPictureTurnRatherThanItsFilename()
    {
        var turns = Turns("""
        [{"role":"assistant","api":"openclaw-transcript","provider":"openclaw",
          "model":"delivery-mirror",
          "content":[{"type":"text","text":"lilibeth_cozy_621662447.png"}]}]
        """);

        var turn = Assert.Single(turns);
        Assert.Contains("/__openclaw__/assistant-media?source=", turn.ImageUrl);
        Assert.Contains("lilibeth_cozy_621662447.png", Uri.UnescapeDataString(turn.ImageUrl!));
        Assert.Contains("~/.openclaw/media/", Uri.UnescapeDataString(turn.ImageUrl!));
        Assert.Equal("lilibeth_cozy_621662447.png", turn.ImageAlt);
    }

    // The defect QA measured, end to end. A browser capture lives one
    // directory below the shared media root, so gluing its bare name to that
    // root fetched a 404 for a file that was on disk and servable the whole
    // time. The real path is on the same page, and now it is what gets used.
    [Fact]
    public void ADeliveredPictureUsesTheRealPathFromThePageRatherThanAGuess()
    {
        var turns = Turns("""
        [{"role":"assistant","model":"claude-sonnet-4-6",
          "content":[{"type":"text","text":"saved it to ~/.openclaw/media/browser/03a1be83.png"}]},
         {"role":"assistant","provider":"openclaw","model":"delivery-mirror",
          "content":[{"type":"text","text":"03a1be83.png"}]}]
        """);

        var picture = Assert.Single(turns, t => t.ImageUrl is not null);
        var source = Uri.UnescapeDataString(picture.ImageUrl!.Split('=')[^1]);

        Assert.Equal("~/.openclaw/media/browser/03a1be83.png", source);
    }

    // The same recovery, but with the path where it usually really is: inside
    // a tool_use block this parser never renders. Nine times as many pictures
    // resolve this way as from the rendered text alone (3 of 41 versus 27 of
    // 41, measured over the gateway host's whole corpus).
    [Fact]
    public void ADeliveredPictureFindsItsPathInsideAToolBlockItNeverRenders()
    {
        var turns = Turns("""
        [{"role":"assistant","content":[{"type":"tool_use","name":"bash",
          "input":{"command":"openclaw message send --media ~/.openclaw/media/browser/03a1be83.png"}}]},
         {"role":"assistant","provider":"openclaw","model":"delivery-mirror",
          "content":[{"type":"text","text":"03a1be83.png"}]}]
        """);

        // The tool block itself renders nothing, so the picture is the only
        // turn — the paths are read without the wall of JSON being shown.
        var picture = Assert.Single(turns);
        var source = Uri.UnescapeDataString(picture.ImageUrl!.Split('=')[^1]);

        Assert.Equal("~/.openclaw/media/browser/03a1be83.png", source);
    }

    // With no path anywhere on the page, the shared media directory is the
    // fallback — right for a file an agent copied there, as Lilibeth's own
    // runbook tells her to, and harmlessly wrong otherwise.
    [Fact]
    public void ADeliveredPictureFallsBackToTheSharedMediaDirectory()
    {
        var turns = Turns("""
        [{"role":"assistant","provider":"openclaw","model":"delivery-mirror",
          "content":[{"type":"text","text":"lilibeth_cozy_621662447.png"}]}]
        """);

        var source = Uri.UnescapeDataString(
            Assert.Single(turns).ImageUrl!.Split('=')[^1]);

        Assert.Equal("~/.openclaw/media/lilibeth_cozy_621662447.png", source);
    }

    // Two real files of the same name on one page. Rather than draw one of
    // them and be wrong half the time, the ambiguity is dropped and the
    // fallback takes over — the picture may not load, but it is never the
    // wrong picture.
    //
    // The two paths are written as prose mentions rather than as messages
    // whose whole text is a path. That is deliberate since CB-101: a message
    // that *is* only a path is now drawn as its own picture (CB-88's bare-path
    // arm, which the history parser finally honours), so path-only fixtures
    // would produce three picture turns and say nothing about ambiguity. A
    // mention still feeds the index, which is what this case is about.
    [Fact]
    public void AnAmbiguousFileNameFallsBackRatherThanDrawingTheWrongPicture()
    {
        var turns = Turns("""
        [{"role":"assistant","model":"m","content":[{"type":"text","text":"wrote it to /one/a.png just now"}]},
         {"role":"assistant","model":"m","content":[{"type":"text","text":"and a copy at /two/a.png as well"}]},
         {"role":"assistant","provider":"openclaw","model":"delivery-mirror",
          "content":[{"type":"text","text":"a.png"}]}]
        """);

        var picture = Assert.Single(turns, t => t.ImageUrl is not null);
        var source = Uri.UnescapeDataString(picture.ImageUrl!.Split('=')[^1]);

        Assert.Equal("~/.openclaw/media/a.png", source);
    }

    // CB-107: the envelope now names the same picture the mirror record
    // delivers, and the two collapse into one bubble via the existing CB-98
    // cross-arm rule (one named turn cancels one mirror) rather than each
    // drawing it separately — which is what actually avoids one delivered
    // picture appearing twice, without going back to never drawing the
    // envelope's own picture at all.
    [Fact]
    public void TheEnvelopeAndItsMirrorCollapseIntoOnePicture()
    {
        var turns = Turns("""
        [{"role":"user","content":"[Inter-session message] sourceSession=agent:comfyui:main\nrouted by OpenClaw\n/Users/w/.openclaw/media/pic.png"},
         {"role":"assistant","provider":"openclaw","model":"delivery-mirror",
          "content":[{"type":"text","text":"pic.png"}]}]
        """);

        Assert.Single(turns, t => t.ImageUrl is not null);
        Assert.Single(turns);
    }

    // And it keeps its filename as text, so a fetch that cannot succeed — a
    // gateway whose media root is somewhere else, a file since cleaned up —
    // leaves the reader exactly what they see today rather than an empty
    // bubble. The picture is the improvement; the text is the floor.
    [Fact]
    public void ADeliveredPictureStillReadsAsItsFilenameIfTheFetchFails()
    {
        var turns = Turns("""
        [{"role":"assistant","provider":"openclaw","model":"delivery-mirror",
          "content":[{"type":"text","text":"lilibeth_cozy_621662447.png"}]}]
        """);

        Assert.Equal("lilibeth_cozy_621662447.png", Assert.Single(turns).Text);
    }

    // A mirrored *text* message stays text. This is the same record type, and
    // this exact string was observed live, so getting it wrong would turn
    // ordinary messages into fetch attempts.
    [Fact]
    public void AMirroredTextMessageStaysText()
    {
        var turns = Turns("""
        [{"role":"assistant","provider":"openclaw","model":"delivery-mirror",
          "content":[{"type":"text","text":"**(via Claude Buddy)** try send me a picture"}]}]
        """);

        var turn = Assert.Single(turns);
        Assert.Null(turn.ImageUrl);
        Assert.Contains("try send me a picture", turn.Text);
    }

    // An agent that merely says a filename is not delivering a picture.
    [Fact]
    public void AnOrdinaryTurnNamingAFileStaysText()
    {
        var turns = Turns("""
        [{"role":"assistant","model":"claude-sonnet-4-6",
          "content":[{"type":"text","text":"lilibeth_cozy_621662447.png"}]}]
        """);

        var turn = Assert.Single(turns);
        Assert.Null(turn.ImageUrl);
        Assert.Equal("lilibeth_cozy_621662447.png", turn.Text);
    }

    // ---- timestamps ------------------------------------------------------

    [Fact]
    public void AUnixMillisecondTimestampIsRead()
    {
        var turns = Turns("""
        [{"role":"user","content":"hello","timestamp":1787000000000}]
        """);

        var expected = DateTimeOffset.FromUnixTimeMilliseconds(1787000000000).ToLocalTime();
        Assert.Equal(expected, Assert.Single(turns).At);
    }

    // No timestamp falls back to now rather than to 1970, which would sort the
    // whole page to the top of a merged room view and be read as the oldest thing
    // anyone said.
    [Fact]
    public void AMissingTimestampFallsBackToNowRatherThanTheEpoch()
    {
        var before = DateTimeOffset.Now.AddMinutes(-1);

        var turns = Turns("""[{"role":"user","content":"hello"}]""");

        Assert.True(Assert.Single(turns).At > before);
    }

    [Fact]
    public void AZeroTimestampAlsoFallsBackToNow()
    {
        var before = DateTimeOffset.Now.AddMinutes(-1);

        var turns = Turns("""[{"role":"user","content":"hello","timestamp":0}]""");

        Assert.True(Assert.Single(turns).At > before);
    }

    // ---- attribution -----------------------------------------------------

    // An inter-session message is unwrapped and attributed, which is the point of
    // routing it through Readable here rather than in the panel.
    [Fact]
    public void AnInterSessionMessageIsUnwrappedAndAttributed()
    {
        OpenClawSessions.SetIdentitiesForTests(
            new System.Collections.Generic.Dictionary<string, OpenClawSessions.AgentIdentity>
            {
                ["comfyui"] = new("ComfyUI", null, null),
            },
            new System.Collections.Generic.Dictionary<string, string> { ["comfyui"] = "ComfyUI" });

        var turns = Turns("""
        [{"role":"user","content":"[Inter-session message] sourceSession=agent:comfyui:discord:direct:1 isUser=false the render finished"}]
        """);

        var turn = Assert.Single(turns);
        Assert.Equal("the render finished", turn.Text);
        Assert.Equal("ComfyUI", turn.Speaker);
    }

    // An ordinary message has no speaker, so the panel draws it as whoever the
    // session belongs to rather than labelling it.
    [Fact]
    public void AnOrdinaryMessageHasNoSpeaker()
    {
        var turns = Turns("""[{"role":"user","content":"just me talking"}]""");

        Assert.Null(Assert.Single(turns).Speaker);
        Assert.Null(turns[0].SpeakerColor);
    }

    // ---- order and trimming ---------------------------------------------

    [Fact]
    public void TurnsComeBackInThePagesOwnOrder()
    {
        var turns = Turns("""
        [{"role":"user","content":"first"},
         {"role":"user","content":"second"},
         {"role":"user","content":"third"}]
        """);

        Assert.Equal(new[] { "first", "second", "third" }, turns.Select(t => t.Text));
    }

    [Fact]
    public void SurroundingWhitespaceIsTrimmedFromTheText()
    {
        var turns = Turns("""[{"role":"user","content":"  padded  "}]""");

        Assert.Equal("padded", Assert.Single(turns).Text);
    }

    // Content of a shape this parser has never seen — a bare number, which a
    // malformed row can produce — yields no text and so no turn, rather than
    // rendering the JSON or throwing. The `_ => ""` arm exists for exactly that.
    [Fact]
    public void ContentOfAnUnknownShapeProducesNoTurn()
    {
        Assert.Empty(Turns("""[{"role":"user","content":42}]"""));
        Assert.Empty(Turns("""[{"role":"user","content":true}]"""));
        Assert.Empty(Turns("""[{"role":"user","content":null}]"""));
    }

    // ---- who sent it, from __openclaw -------------------------------------

    // Five shapes, all of them taken from what a live gateway actually returns
    // and every value in them replaced. The structure is what these fixtures
    // are for; the ids, names and sentences are invented, because this
    // repository is public and the gateway is a Discord server with real people
    // in it.

    // Shape one: the operator typing in Discord. The gateway states it, and
    // stating it is the only unambiguous signal of the five.
    [Fact]
    public void AMessageTheOwnerSentInDiscordIsMine()
    {
        var turns = Turns("""
        [{"role":"user","content":"what did the overnight run say?",
          "timestamp":1787000000000,
          "__openclaw":{"senderIsOwner":true,"senderId":"100000000000000001",
                        "senderName":"quillfeather","senderUsername":"quillfeather",
                        "seq":1}}]
        """);

        var turn = Assert.Single(turns);
        Assert.True(turn.Mine);
        Assert.Equal(ChatRole.User, turn.Role);
        Assert.Null(turn.Speaker);
    }

    // Shape two: the operator typing here. No sender fields at all, and a
    // top-level idempotency key ending ":user" — which is the gateway's own
    // stamp on a message it accepted from this client.
    [Fact]
    public void AMessageThisAppSentIsMineByItsIdempotencyKey()
    {
        var turns = Turns("""
        [{"role":"user","content":"anyone free to look at the build?",
          "timestamp":1787000001000,
          "idempotencyKey":"3f1c8ad2-59b7-4e06-9c31-8ab7205de164:user",
          "__openclaw":{"id":"7a4d19e0-0c52-4b8e-a6d3-91f0c4ba7d58",
                        "idempotencyKey":"3f1c8ad2-59b7-4e06-9c31-8ab7205de164:user",
                        "seq":2}}]
        """);

        Assert.True(Assert.Single(turns).Mine);
    }

    // Shape three: our own mirror as the *other* agents in the room received it
    // — prefixed, and attributed to whichever bot account carried it. Yours
    // despite the name, and shown without the prefix, so it matches the copy in
    // the carrier's own transcript rather than being drawn beside it.
    [Fact]
    public void OurOwnMirrorComesBackAsMineWithThePrefixOff()
    {
        var turns = Turns("""
        [{"role":"user","content":"**(via Claude Buddy)** anyone free to look at the build?",
          "timestamp":1787000001100,
          "__openclaw":{"senderIsOwner":false,"senderId":"100000000000000002",
                        "senderName":"Quillbot","senderUsername":"Quillbot","seq":3}}]
        """);

        var turn = Assert.Single(turns);
        Assert.True(turn.Mine);
        Assert.Equal("anyone free to look at the build?", turn.Text);
        Assert.Null(turn.Speaker);
    }

    // Shape four: another agent's message relayed through the channel. Named,
    // which the ticket did not expect — the relay carries the bot's Discord
    // display name, so this is attributable rather than anonymous.
    [Fact]
    public void ARelayedAgentsMessageIsNamed()
    {
        var turns = Turns("""
        [{"role":"user","content":"Build is green on both legs.",
          "timestamp":1787000002000,
          "__openclaw":{"senderIsOwner":false,"senderId":"100000000000000003",
                        "senderName":"Thistle","senderUsername":"Thistle","seq":4}}]
        """);

        var turn = Assert.Single(turns);
        Assert.False(turn.Mine);
        Assert.Equal("Thistle", turn.Speaker);
    }

    // ...and with no colour, deliberately. senderName is a Discord display name
    // and the colours in this app are keyed by agent id; there is no map
    // between them the gateway offers. An uncoloured chip falls back to
    // initials, where a borrowed colour would say two different speakers were
    // the same one.
    [Fact]
    public void ARelayedAgentGetsNoColourBecauseADisplayNameIsNotAnAgentId()
    {
        var turns = Turns("""
        [{"role":"user","content":"Build is green on both legs.",
          "__openclaw":{"senderIsOwner":false,"senderName":"Thistle"}}]
        """);

        Assert.Null(Assert.Single(turns).SpeakerColor);
    }

    // Shape five: an inter-session message. No sender fields, and a machine
    // header Readable already turns into the message plus the agent behind it —
    // so the classification falls through to Unknown and leaves that better
    // answer alone. Readable's answer is an agent *id*, which is why this one
    // keeps its colour where the relayed shape above does not.
    [Fact]
    public void AnInterSessionMessageKeepsTheSpeakerReadableFound()
    {
        var turns = Turns("""
        [{"role":"user","content":"[Inter-session message] sourceSession=agent:thornwood:discord:direct:100000000000000004 sourceChannel=discord sourceTool=sessions_send isUser=false can you take the release notes?"}]
        """);

        var turn = Assert.Single(turns);
        Assert.False(turn.Mine);
        Assert.Equal("can you take the release notes?", turn.Text);
        Assert.NotNull(turn.Speaker);
    }

    // The whole rule degrading to what this app did before it existed. A page
    // with no `__openclaw` anywhere claims nothing about anybody — which is the
    // safety net if the gateway's undocumented internals move, and the reason
    // every existing case in this file above still passes unchanged.
    [Fact]
    public void AMessageWithNoMetadataClaimsNothing()
    {
        var turns = Turns("""[{"role":"user","content":"morning"}]""");

        var turn = Assert.Single(turns);
        Assert.False(turn.Mine);
        Assert.Null(turn.Speaker);
    }

    // A mirror with nothing after its prefix is nothing, and is dropped rather
    // than drawn as an empty bubble. Reachable because the prefix comes off
    // before the emptiness test, which is the order that makes it possible at
    // all — a message that is only addressing has no content.
    [Fact]
    public void AMirrorCarryingOnlyItsPrefixIsNotATurn()
    {
        Assert.Empty(Turns("""[{"role":"user","content":"**(via Claude Buddy)**   "}]"""));
    }

    // An assistant turn is never asked. It is the agent whose transcript this
    // is — the fact the whole room merge is built on — so a sender block on one
    // changes nothing, and an idempotency key on one must not be read as yours.
    [Fact]
    public void AnAssistantTurnIsNeverClassifiedAsYours()
    {
        var turns = Turns("""
        [{"role":"assistant","content":[{"type":"text","text":"On it."}],
          "idempotencyKey":"cli-assistant:9d0b6e37-4a15-42fc-b8e0-51c7ad38f962",
          "__openclaw":{"senderIsOwner":true,"senderName":"quillfeather"}}]
        """);

        var turn = Assert.Single(turns);
        Assert.False(turn.Mine);
        Assert.Null(turn.Speaker);
    }
}
