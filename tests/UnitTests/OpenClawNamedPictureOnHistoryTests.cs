using System;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// CB-101. A picture the agent named by path used to render only while its
// reply was streaming in, because CB-88 wired LocalMediaPathFrom into the live
// path alone. Read the same message back — reopen the panel, reconnect, scroll
// — and the MEDIA: line was just text.
//
// The fixtures here are the real message Warren screenshotted, not an invented
// one: the file was on disk and the gateway answered `available:true` for it,
// so the only thing that failed was this parser never asking.
public class OpenClawNamedPictureOnHistoryTests
{
    private const string RealPath =
        "/Users/warrenthompson/.openclaw/workspace-manager-lilibeth/.openclaw-cli-images/"
        + "ff8ca57e874675a6c539e612448971ca25f907bcab3e1eee860587df5bf79836.jpg";

    private static System.Collections.Generic.List<HistoryTurn> Turns(string json)
        => OpenClawSessions.TurnsFromHistory(JsonDocument.Parse(json).RootElement);

    private static string SourceOf(HistoryTurn turn)
        => Uri.UnescapeDataString(turn.ImageUrl!.Split('=')[^1]);

    // The exact reply that rendered as bare text at 12:49.
    [Fact]
    public void AMediaLineOnAHistoryReadNowDrawsThePicture()
    {
        var turns = Turns($$"""
        [{"role":"assistant","content":[{"type":"text","text":
          "Hey War War 🌸 I'm here! Cute pic, right? That's the one Zara whipped up earlier.\nMEDIA:{{RealPath}}\nWhat's up? 😊"}]}]
        """);

        var turn = Assert.Single(turns);
        Assert.Equal(RealPath, SourceOf(turn));

        // Alt text is the filename, which is all there is to say about it.
        Assert.Equal(
            "ff8ca57e874675a6c539e612448971ca25f907bcab3e1eee860587df5bf79836.jpg",
            turn.ImageAlt);
    }

    // And the prose survives, so a fetch that cannot succeed leaves exactly
    // what was there before rather than an empty bubble. Same shape the live
    // path produces.
    [Fact]
    public void TheMessageAroundTheMediaLineIsStillReadable()
    {
        var turn = Assert.Single(Turns($$"""
        [{"role":"assistant","content":[{"type":"text","text":
          "here you go\nMEDIA:{{RealPath}}\nenjoy"}]}]
        """));

        Assert.Contains("here you go", turn.Text);
        Assert.Contains("enjoy", turn.Text);
    }

    // A bare path as the whole message — the other arm of the same helper,
    // and a real shape (CB-88 found it left by a duplicate-post bug).
    [Fact]
    public void ABarePathAsTheWholeMessageDrawsToo()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"assistant","content":[{"type":"text","text":"/Users/w/pics/a.png"}]}]
        """));

        Assert.Equal("/Users/w/pics/a.png", SourceOf(turn));
    }

    // CB-97, in the shape that matters: the gateway expands a leading tilde
    // itself, and rejecting it threw away pictures it would have served.
    [Fact]
    public void ATildeRootedPathIsAcceptedNow()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"assistant","content":[{"type":"text","text":
          "saved it\nMEDIA:~/.openclaw/media/browser/03a1be83.png"}]}]
        """));

        Assert.Equal("~/.openclaw/media/browser/03a1be83.png", SourceOf(turn));
    }

    // CB-107 revisits this. It used to stay text: the bare-path arm required
    // the *whole* trimmed message to be the path, and an inter-session
    // envelope's machine preamble above it meant it never was — deliberately,
    // to avoid the picture appearing on both the forwarding turn and its
    // origin.
    //
    // LocalMediaPathFrom now also matches a trailing path after a caption
    // (a real observed captioning shape — see OpenClawLocalMediaPathTests),
    // and there is no text-only way to tell that apart from "routing preamble
    // ending in a path": both are a line of prose followed by nothing but a
    // path. Left drawing rather than blocked, the same call already made for
    // AnEnvelopeCarryingAMediaLineDoesDraw below — a relayed message carrying
    // a picture should show it, and a possible second render on the
    // originating session's own orb costs less than an OpenClaw agent's
    // pictures silently going text-only, which is the bug CB-107 exists for.
    [Fact]
    public void AnEnvelopeWhoseLastLineIsABarePathNowDrawsToo()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"assistant","content":[{"type":"text","text":
          "[Inter-session message] sourceSession=agent:comfyui:main\nrouted by OpenClaw\n/Users/w/.openclaw/media/pic.png"}]}]
        """));

        Assert.Equal("/Users/w/.openclaw/media/pic.png", SourceOf(turn));
    }

    // But an envelope carrying a MEDIA: line DOES draw, and this test exists
    // because the PR for CB-101 originally claimed otherwise. QA found the
    // real case: LocalMediaPathFrom's *first* arm scans every line for the
    // marker and does not require the whole message to be the path, so the
    // preamble above it is no obstacle.
    //
    // Asserted as the actual behaviour rather than the behaviour that was
    // claimed, because it is defensible on its own terms — a relayed message
    // carrying an agent's own picture should show that picture — and because
    // an assertion that quietly disagrees with the code is worse than either.
    [Fact]
    public void AnEnvelopeCarryingAMediaLineDoesDraw()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"user","content":
          "content was routed by OpenClaw from another session or internal tool.\nMEDIA:/Users/w/.openclaw/workspace-social-annabel/tmp/opusclip/frame.jpg\nFill re-render is done — does this framing look right?"}]
        """));

        Assert.Equal(
            "/Users/w/.openclaw/workspace-social-annabel/tmp/opusclip/frame.jpg",
            SourceOf(turn));
    }

    // ---- one delivery, two arms, one bubble (CB-98, live not latent) -------

    // The defect QA measured at df3974d. A page carries the agent's own
    // path-only message *and* the gateway's mirror of delivering that file;
    // CB-94 recovers the mirror's directory from that very path, so both arms
    // resolve to the identical source and drew the same picture twice.
    //
    // The mirror copy is the one dropped: in the general shape the named turn
    // carries the agent's prose where the mirror's whole content is the
    // filename the picture already shows.
    [Fact]
    public void AFileDrawnByBothArmsIsDrawnOnce()
    {
        var turns = Turns("""
        [{"role":"assistant","content":[{"type":"text","text":"~/.openclaw/media/browser/03a1be83.png"}]},
         {"role":"assistant","provider":"openclaw","model":"delivery-mirror",
          "content":[{"type":"text","text":"03a1be83.png"}]}]
        """);

        var turn = Assert.Single(turns);
        Assert.Equal("~/.openclaw/media/browser/03a1be83.png", SourceOf(turn));

        // The named turn survived, not the mirror: its text is the path it
        // named, where the mirror's would have been the bare filename.
        Assert.Contains("browser", turn.Text);
    }

    // Same collapse when the mirror comes first on the page, so the rule does
    // not depend on the order the two arms happen to appear in.
    [Fact]
    public void TheCollapseDoesNotDependOnWhichArmComesFirst()
    {
        var turns = Turns("""
        [{"role":"assistant","provider":"openclaw","model":"delivery-mirror",
          "content":[{"type":"text","text":"03a1be83.png"}]},
         {"role":"assistant","content":[{"type":"text","text":"~/.openclaw/media/browser/03a1be83.png"}]}]
        """);

        var turn = Assert.Single(turns);
        Assert.Equal("~/.openclaw/media/browser/03a1be83.png", SourceOf(turn));
    }

    // Two *mirrors* of one file are two deliveries and stay two bubbles —
    // distinct records with distinct timestamps, measured 36 seconds apart in
    // one real case and 47 minutes in another. Collapsing them would hide an
    // event the gateway recorded, which is the opposite mistake.
    [Fact]
    public void TwoMirrorsOfOneFileStayTwoTurns()
    {
        var turns = Turns("""
        [{"role":"assistant","provider":"openclaw","model":"delivery-mirror",
          "content":[{"type":"text","text":"countdown_mar12.png"}]},
         {"role":"assistant","provider":"openclaw","model":"delivery-mirror",
          "content":[{"type":"text","text":"countdown_mar12.png"}]}]
        """);

        Assert.Equal(2, turns.Count);
        Assert.All(turns, t => Assert.NotNull(t.ImageUrl));
    }

    // The edge QA found by reading the rule rather than the corpus: a page
    // with a named turn AND two mirrors of the same file. One named turn
    // cancels one mirror — the cross-arm pair — and the second delivery keeps
    // its own bubble. A membership test instead of a budget would have eaten
    // both, silently losing a real delivery.
    [Fact]
    public void ANamedTurnCancelsOneMirrorAndNotEveryMirror()
    {
        var turns = Turns("""
        [{"role":"assistant","content":[{"type":"text","text":"~/.openclaw/media/browser/03a1be83.png"}]},
         {"role":"assistant","provider":"openclaw","model":"delivery-mirror",
          "content":[{"type":"text","text":"03a1be83.png"}]},
         {"role":"assistant","provider":"openclaw","model":"delivery-mirror",
          "content":[{"type":"text","text":"03a1be83.png"}]}]
        """);

        // The named turn plus the surviving second delivery.
        Assert.Equal(2, turns.Count);
        Assert.All(turns, t => Assert.NotNull(t.ImageUrl));

        // The named turn is the one that kept its prose.
        Assert.Contains(turns, t => t.Text.Contains("browser"));

        // And exactly one bare-filename mirror bubble is left.
        Assert.Single(turns, t => t.Text == "03a1be83.png");
    }

    // A mirror for a *different* file is untouched by the collapse.
    [Fact]
    public void AMirrorForADifferentFileIsNotCollapsed()
    {
        var turns = Turns("""
        [{"role":"assistant","content":[{"type":"text","text":"~/.openclaw/media/browser/one.png"}]},
         {"role":"assistant","provider":"openclaw","model":"delivery-mirror",
          "content":[{"type":"text","text":"two.png"}]}]
        """);

        Assert.Equal(2, turns.Count);
        Assert.All(turns, t => Assert.NotNull(t.ImageUrl));
    }

    // CB-89: traversal refused, so nothing here builds a request out of one.
    [Fact]
    public void ATraversalIsRefused()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"assistant","content":[{"type":"text","text":
          "MEDIA:/Users/w/../../etc/shadow.png"}]}]
        """));

        Assert.Null(turn.ImageUrl);
    }

    [Fact]
    public void AProtocolRelativeUrlIsRefused()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"assistant","content":[{"type":"text","text":
          "MEDIA://cdn.discordapp.com/attachments/1/2/a.png"}]}]
        """));

        Assert.Null(turn.ImageUrl);
    }

    // The prose-mentions-MEDIA case QA found against CB-88, still refused:
    // without validating what follows the prefix, this extracted "is a broad
    // term..." and fired a real request for it.
    [Fact]
    public void ASentenceBeginningMediaIsNotAPath()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"assistant","content":[{"type":"text","text":
          "MEDIA: is a broad term covering images and video."}]}]
        """));

        Assert.Null(turn.ImageUrl);
    }

    // An ordinary sentence that happens to name a file is not a delivery.
    [Fact]
    public void ProseMentioningAPictureIsNotAPicture()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"assistant","content":[{"type":"text","text":
          "I saved it to /Users/w/pics/a.png earlier, have a look."}]}]
        """));

        Assert.Null(turn.ImageUrl);
    }

    // A delivery-mirror record still takes the branch above rather than this
    // one — its text is a bare filename, which is not rooted.
    [Fact]
    public void ADeliveryMirrorIsUnaffected()
    {
        var turn = Assert.Single(Turns("""
        [{"role":"assistant","provider":"openclaw","model":"delivery-mirror",
          "content":[{"type":"text","text":"lilibeth_cozy_621662447.png"}]}]
        """));

        Assert.Contains("~/.openclaw/media/lilibeth_cozy_621662447.png", SourceOf(turn));
    }
}
