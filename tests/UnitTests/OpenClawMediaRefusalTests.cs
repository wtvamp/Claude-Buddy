using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeBuddy.Tests;

// CB-93: when the gateway refuses `MEDIA:<path>`, `&meta=1` on the same route
// says why — this covers turning that answer into the line and tooltip the
// panel shows, and the two guards that decide whether to ask at all.
public class OpenClawMediaRefusalTests
{
    // ---- ShouldAskWhy: the happy-path guard -----------------------------

    [Fact]
    public void NonEmptyBytesNeverAsksWhy()
    {
        Assert.False(OpenClawMediaRefusal.ShouldAskWhy(
            new byte[] { 1 }, OpenClawSessions.AssistantMediaRoute + "x"));
    }

    [Fact]
    public void EmptyBytesAgainstTheAssistantMediaRouteAsksWhy()
    {
        Assert.True(OpenClawMediaRefusal.ShouldAskWhy(
            Array.Empty<byte>(), OpenClawSessions.AssistantMediaRoute + "x"));
    }

    [Fact]
    public void NullBytesAgainstAnOrdinaryAttachmentUrlNeverAsksWhy()
    {
        // An ordinary [media attached: ...] url has no &meta=1 variant, so
        // asking would be a second wasted request against a route that was
        // never going to explain itself.
        Assert.False(OpenClawMediaRefusal.ShouldAskWhy(
            null, "/__openclaw__/inbound?source=x"));
    }

    [Fact]
    public void NullBytesAgainstTheAssistantMediaRouteAsksWhy()
    {
        Assert.True(OpenClawMediaRefusal.ShouldAskWhy(
            null, OpenClawSessions.AssistantMediaRoute + "x"));
    }

    [Fact]
    public void ANullUrlNeverAsksWhy()
    {
        Assert.False(OpenClawMediaRefusal.ShouldAskWhy(null, null));
    }

    // ---- MetaRoute --------------------------------------------------------

    [Fact]
    public void MetaRouteAppendsTheMetaFlag()
    {
        Assert.EndsWith("&meta=1", OpenClawMediaRefusal.MetaRoute("/a/b.png"), StringComparison.Ordinal);
    }

    [Fact]
    public void MetaRoutePercentEncodesASpace()
    {
        var route = OpenClawMediaRefusal.MetaRoute("/a drop/b.png");
        Assert.Contains("%20", route, StringComparison.Ordinal);
    }

    [Fact]
    public void MetaRoutePercentEncodesAHash()
    {
        var route = OpenClawMediaRefusal.MetaRoute("/a#b.png");
        Assert.Contains("%23", route, StringComparison.Ordinal);
    }

    [Fact]
    public void MetaRouteStartsWithTheAssistantMediaRoute()
    {
        var route = OpenClawMediaRefusal.MetaRoute("/a/b.png");
        Assert.StartsWith(OpenClawSessions.AssistantMediaRoute, route, StringComparison.Ordinal);
    }

    // ---- PathFromUrl --------------------------------------------------------

    [Fact]
    public void PathFromUrlRecoversAnEscapedPath()
    {
        const string path = "/a drop/b.png";
        var url = OpenClawSessions.AssistantMediaRoute + Uri.EscapeDataString(path);

        Assert.Equal(path, OpenClawMediaRefusal.PathFromUrl(url));
    }

    [Fact]
    public void PathFromUrlIsNullForAUrlOutsideTheRoute()
    {
        Assert.Null(OpenClawMediaRefusal.PathFromUrl("/__openclaw__/inbound?source=x"));
    }

    [Fact]
    public void PathFromUrlIsNullForANullUrl()
    {
        Assert.Null(OpenClawMediaRefusal.PathFromUrl(null));
    }

    // ---- Explain ------------------------------------------------------------

    [Fact]
    public void OutsideAllowedFoldersGetsTheActionableRemedy()
    {
        var line = OpenClawMediaRefusal.Explain(
            "{\"available\":false,\"code\":\"outside-allowed-folders\",\"reason\":\"Outside allowed folders\"}");

        Assert.Contains("~/.openclaw/media/", line, StringComparison.Ordinal);
        Assert.Contains("won't serve files from that folder", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AnyOtherCodeWithAReasonReportsTheReason()
    {
        var line = OpenClawMediaRefusal.Explain(
            "{\"available\":false,\"code\":\"some-other-code\",\"reason\":\"not on this host\"}");

        Assert.Equal("Picture not shown — the gateway refused it: not on this host", line);
    }

    [Fact]
    public void ACodeWithNoReasonNamesTheCode()
    {
        var line = OpenClawMediaRefusal.Explain("{\"available\":false,\"code\":\"nope\"}");

        Assert.Equal("Picture not shown — the gateway refused it (nope).", line);
    }

    [Fact]
    public void NeitherCodeNorReasonIsTheGenericRefusal()
    {
        var line = OpenClawMediaRefusal.Explain("{\"available\":false}");

        Assert.Equal("Picture not shown — the gateway refused it.", line);
    }

    [Fact]
    public void AvailableTrueSaysTheFetchDidNotFinish()
    {
        var line = OpenClawMediaRefusal.Explain(
            "{\"available\":true,\"mediaTicket\":\"v1.abc\",\"mediaTicketExpiresAt\":\"later\"}");

        Assert.Equal("Picture not shown — the gateway has the file but the fetch didn't finish.", line);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    public void AnUnusableAnswerSaysItCouldNotAsk(string? json)
    {
        Assert.Equal("Picture not shown — couldn't ask the gateway why.", OpenClawMediaRefusal.Explain(json));
    }

    [Fact]
    public void CodePresentWithAvailableMissingIsTreatedAsRefused()
    {
        var line = OpenClawMediaRefusal.Explain("{\"code\":\"nope\"}");

        Assert.Equal("Picture not shown — the gateway refused it (nope).", line);
    }

    [Fact]
    public void AWhitespaceOnlyReasonFallsBackToTheCode()
    {
        var line = OpenClawMediaRefusal.Explain("{\"available\":false,\"code\":\"nope\",\"reason\":\"   \"}");

        Assert.Equal("Picture not shown — the gateway refused it (nope).", line);
    }

    [Fact]
    public void ACodeThatIsNotAStringIsTreatedAsMissing()
    {
        // The gateway's own answer is always a string code, but a value of
        // the wrong JSON kind should read as "no code" rather than throw.
        var line = OpenClawMediaRefusal.Explain("{\"available\":false,\"code\":123}");

        Assert.Equal("Picture not shown — the gateway refused it.", line);
    }

    [Fact]
    public void AnOverLongReasonIsTruncatedAt200Characters()
    {
        var reason = new string('x', 500);
        var line = OpenClawMediaRefusal.Explain(
            $"{{\"available\":false,\"code\":\"nope\",\"reason\":\"{reason}\"}}");

        Assert.Equal("Picture not shown — the gateway refused it: " + new string('x', 200), line);
    }

    [Fact]
    public void UnknownExtraFieldsAreIgnored()
    {
        var line = OpenClawMediaRefusal.Explain(
            "{\"available\":false,\"code\":\"nope\",\"somethingElse\":123,\"nested\":{\"a\":1}}");

        Assert.Equal("Picture not shown — the gateway refused it (nope).", line);
    }

    // ---- Detail (tooltip) ----------------------------------------------------

    [Fact]
    public void DetailPairsThePathWithTheCode()
    {
        var detail = OpenClawMediaRefusal.Detail(
            "{\"available\":false,\"code\":\"outside-allowed-folders\"}", "/a/b.png");

        Assert.Equal("/a/b.png — outside-allowed-folders", detail);
    }

    [Fact]
    public void DetailIsThePathAloneWithNoCode()
    {
        Assert.Equal("/a/b.png", OpenClawMediaRefusal.Detail("{\"available\":false}", "/a/b.png"));
    }

    [Fact]
    public void DetailIsThePathAloneWhenTheAnswerCouldNotBeAsked()
    {
        Assert.Equal("/a/b.png", OpenClawMediaRefusal.Detail(null, "/a/b.png"));
    }

    // ---- The HTTP half: a JSON body survives ReadResponseAsync intact -------
    //
    // Reusing OpenClawSocket.ReadResponseAsync over a MemoryStream, the same
    // seam OpenClawSocketTests already drives it through, rather than a fake
    // socket of this file's own: the interesting question is whether a meta
    // answer's bytes come back unmangled, and that is the exact thing this
    // method already proves for a picture's bytes.
    [Fact]
    public async Task AMetaJsonBodySurvivesTheHttpReadIntact()
    {
        const string json = "{\"available\":false,\"code\":\"outside-allowed-folders\",\"reason\":\"Outside allowed folders\"}";
        var body = Encoding.UTF8.GetBytes(json);
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n");

        using var stream = new MemoryStream(headers.Length + body.Length);
        stream.Write(headers);
        stream.Write(body);
        stream.Position = 0;

        var bytes = await OpenClawSocket.ReadResponseAsync(stream, CancellationToken.None);

        Assert.Equal(json, Encoding.UTF8.GetString(bytes!));
    }
}
