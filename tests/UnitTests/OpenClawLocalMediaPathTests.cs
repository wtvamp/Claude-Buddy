using System.Collections.Generic;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// CB-88: an agent's own generated picture, named by its own path on the
// gateway host rather than a fetchable URL. Two real shapes, both captured
// from a live gateway via tools/openclaw-probe rather than assumed — see
// OpenClawSessions.LocalMediaPathFrom's own comment.
public class OpenClawLocalMediaPathTests
{
    // The real captured example: two paragraphs of in-character reply, then
    // the MEDIA: line last — not the first line of the message.
    [Fact]
    public void AMediaLineAfterOtherParagraphsIsFound()
    {
        var text = "got her path — delivering now 🌸\n\n"
                  + "hey War War... caught me in the golden hour 💛✨\n\n"
                  + "MEDIA:/Users/warrenthompson/.openclaw/workspace-comfyui-zara/outputs/"
                  + "lilibeth/lilibeth_drop_204143557_1788477875_00001_.png";

        Assert.Equal(
            "/Users/warrenthompson/.openclaw/workspace-comfyui-zara/outputs/"
            + "lilibeth/lilibeth_drop_204143557_1788477875_00001_.png",
            OpenClawSessions.LocalMediaPathFrom(text));
    }

    [Fact]
    public void AMediaLineAsTheWholeMessageIsFound()
    {
        Assert.Equal("/tmp/pic.png", OpenClawSessions.LocalMediaPathFrom("MEDIA:/tmp/pic.png"));
    }

    [Fact]
    public void WhitespaceAroundTheMediaLineIsTrimmed()
    {
        Assert.Equal("/tmp/pic.png", OpenClawSessions.LocalMediaPathFrom("MEDIA:  /tmp/pic.png  "));
    }

    [Fact]
    public void AMediaMarkerWithNoPathIsNotAPicture()
    {
        Assert.Null(OpenClawSessions.LocalMediaPathFrom("MEDIA:"));
        Assert.Null(OpenClawSessions.LocalMediaPathFrom("MEDIA:   "));
    }

    [Fact]
    public void OrdinaryTextIsNeverMistakenForAMarker()
    {
        Assert.Null(OpenClawSessions.LocalMediaPathFrom("just an ordinary reply, nothing attached"));
    }

    // Mentioning "MEDIA:" mid-sentence, not as its own line, is not the
    // marker — only a line that starts with it counts.
    [Fact]
    public void MediaMentionedMidLineIsNotTheMarker()
    {
        Assert.Null(OpenClawSessions.LocalMediaPathFrom("the SOCIAL MEDIA:/path.png thing you mentioned"));
    }

    // QA (CB-88) found this real gap: an ordinary sentence that happens to
    // start a line with "MEDIA:" would otherwise have everything after the
    // colon extracted as a "path" and fired at the gateway as one. The text
    // after the prefix now has to pass the same shape check the bare-path
    // arm already required.
    [Fact]
    public void AnOrdinarySentenceStartingWithTheWordMediaIsNotAMarker()
    {
        Assert.Null(OpenClawSessions.LocalMediaPathFrom("MEDIA: is a broad term for a lot of things"));
    }

    // The prefix alone doesn't make it a picture — a relative-looking path
    // after it is rejected the same way a bare relative path already is.
    [Fact]
    public void AMediaLineWithARelativePathIsNotAPicture()
    {
        Assert.Null(OpenClawSessions.LocalMediaPathFrom("MEDIA:outputs/lilibeth/pic.png"));
    }

    // The other real shape: the same automation's duplicate-post bug (before
    // it was fixed) left a bare path as an entire assistant turn, no MEDIA:
    // prefix at all.
    [Fact]
    public void ABarePathThatIsTheWholeMessageIsFound()
    {
        Assert.Equal(
            "/Users/warrenthompson/.openclaw/workspace-comfyui-zara/outputs/lilibeth/lilibeth_drop.png",
            OpenClawSessions.LocalMediaPathFrom(
                "/Users/warrenthompson/.openclaw/workspace-comfyui-zara/outputs/lilibeth/lilibeth_drop.png"));
    }

    [Theory]
    [InlineData("/tmp/pic.png")]
    [InlineData("/tmp/pic.PNG")]
    [InlineData("/tmp/pic.jpg")]
    [InlineData("/tmp/pic.jpeg")]
    [InlineData("/tmp/pic.gif")]
    [InlineData("/tmp/pic.webp")]
    public void EveryKnownImageExtensionIsRecognisedAsABarePath(string path)
    {
        Assert.Equal(path, OpenClawSessions.LocalMediaPathFrom(path));
    }

    // A relative-looking path is not what this matches — every real example
    // captured is absolute, and a relative one is more likely a citation or
    // a filename mentioned in conversation than a picture to fetch.
    [Fact]
    public void ARelativeBarePathIsNotAPicture()
    {
        Assert.Null(OpenClawSessions.LocalMediaPathFrom("outputs/lilibeth/pic.png"));
    }

    // A sentence that happens to end in something that looks like a
    // filename is not a bare path — the whole trimmed message has to be
    // nothing else.
    [Fact]
    public void ASentenceEndingInAFilenameIsNotABarePath()
    {
        Assert.Null(OpenClawSessions.LocalMediaPathFrom("saved it to /tmp/pic.png just now"));
    }

    [Fact]
    public void ABarePathToAnUnknownExtensionIsNotAPicture()
    {
        Assert.Null(OpenClawSessions.LocalMediaPathFrom("/tmp/notes.txt"));
    }

    // CB-107: an agent's caption paired descriptive text with the file rather
    // than sending it alone, breaking both existing arms. Caught live in a
    // real OpenClaw orb — none of the reported messages drew a thumbnail.
    [Fact]
    public void ACaptionWithABarePathOnTheNextLineIsFound()
    {
        Assert.Equal(
            "/Users/w/.openclaw/workspace-example/outputs/agent/photo_275866713.png",
            OpenClawSessions.LocalMediaPathFrom(
                "here's the shot   /Users/w/.openclaw/"
                + "workspace-example/outputs/agent/photo_275866713.png"));
    }

    // A bare filename with no directory at all has nothing to fetch on its
    // own — ResolveLocalMediaPath is what turns this into something
    // fetchable, so this returns the raw name unresolved.
    [Fact]
    public void ACaptionWithABareFilenameOnTheNextLineIsFoundUnresolved()
    {
        Assert.Equal(
            "photo_773311913.png",
            OpenClawSessions.LocalMediaPathFrom(
                "here's this morning's shot\n"
                + "photo_773311913.png"));
    }

    // Video is a real shape in the same corpus, but a deliberately separate
    // gap: ImageExtensions has no video extensions, so this stays plain text
    // rather than being silently treated as an image.
    [Fact]
    public void ACaptionWithATrailingVideoFilenameIsNotAPicture()
    {
        Assert.Null(OpenClawSessions.LocalMediaPathFrom(
            "Fixed the clip, here you go.\n"
            + "clip_v4_final.mp4"));
    }

    // A bare trailing word still has to look like a filename — the relative-
    // path and unknown-extension rules from the whole-message checks above
    // apply here too, not just "ends the message".
    [Fact]
    public void ATrailingWordThatIsNotFilenameShapedIsNotAPicture()
    {
        Assert.Null(OpenClawSessions.LocalMediaPathFrom(
            "just an ordinary reply that ends in a word"));
    }
}

// CB-107: turning what LocalMediaPathFrom found into something fetchable.
public class OpenClawResolveLocalMediaPathTests
{
    [Fact]
    public void ARootedPathIsReturnedUnchanged()
    {
        Assert.Equal("/tmp/pic.png",
            OpenClawSessions.ResolveLocalMediaPath("/tmp/pic.png", null));
    }

    [Fact]
    public void ATildePathIsReturnedUnchanged()
    {
        Assert.Equal("~/.openclaw/media/pic.png",
            OpenClawSessions.ResolveLocalMediaPath("~/.openclaw/media/pic.png", null));
    }

    [Fact]
    public void ABareFilenameKnownOnThePageResolvesToItsHarvestedDirectory()
    {
        var mediaPaths = new Dictionary<string, string>
        {
            ["pic.png"] = "/Users/w/.openclaw/workspace-example/outputs/agent/pic.png"
        };

        Assert.Equal(
            "/Users/w/.openclaw/workspace-example/outputs/agent/pic.png",
            OpenClawSessions.ResolveLocalMediaPath("pic.png", mediaPaths));
    }

    [Fact]
    public void ABareFilenameUnknownOnThePageFallsBackToTheSharedMediaDirectory()
    {
        Assert.Equal(
            OpenClawSessions.SharedMediaDir + "pic.png",
            OpenClawSessions.ResolveLocalMediaPath("pic.png", new Dictionary<string, string>()));
    }

    [Fact]
    public void ABareFilenameWithNoPageAtAllFallsBackToTheSharedMediaDirectory()
    {
        Assert.Equal(
            OpenClawSessions.SharedMediaDir + "pic.png",
            OpenClawSessions.ResolveLocalMediaPath("pic.png", null));
    }
}
