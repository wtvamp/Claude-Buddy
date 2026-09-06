using System.Text;
using System.Text.Json;
using Xunit;

namespace ClaudeBuddy.Tests;

// SessionMessageFrame — the pure wire-format encoding for one delivery to a
// headless session's registry socket. No socket anywhere in this file, per
// the split SessionMessenger's header comment describes: this is the half
// that can and must be checked against golden bytes without one.
public class SessionMessageFrameTests
{
    // --- Wrap -------------------------------------------------------------

    [Fact]
    public void TheOrdinaryShapeWrapsTheTextBetweenTags()
    {
        var wrapped = SessionMessageFrame.Wrap("Claude Buddy on mini", "hello there");

        Assert.Equal(
            "<cross-session-message from=\"Claude Buddy on mini\" from-mode=\"prompting\">\n" +
            "hello there\n" +
            "</cross-session-message>",
            wrapped);
    }

    [Theory]
    [InlineData("say \"hi\"", "say hi")]
    [InlineData("<script>", "script")]
    [InlineData("a<b>c\"d", "abcd")]
    public void QuotesAndAngleBracketsAreStrippedFromTheFromName(string dirty, string clean)
    {
        var wrapped = SessionMessageFrame.Wrap(dirty, "text");

        Assert.Equal(
            $"<cross-session-message from=\"{clean}\" from-mode=\"prompting\">\ntext\n</cross-session-message>",
            wrapped);
    }

    [Fact]
    public void ANameLongerThan64CharsIsTruncated()
    {
        var longName = new string('a', 100);

        var wrapped = SessionMessageFrame.Wrap(longName, "text");

        Assert.Equal(
            $"<cross-session-message from=\"{new string('a', 64)}\" from-mode=\"prompting\">\ntext\n</cross-session-message>",
            wrapped);
    }

    // Stripped characters are removed before the 64-char cut is taken, so a
    // name that is only over the limit because of characters that will not
    // survive anyway is not truncated for no reason.
    [Fact]
    public void TruncationIsAppliedAfterStrippingNotBefore()
    {
        var dirty = "\"" + new string('a', 64) + "\"";

        var wrapped = SessionMessageFrame.Wrap(dirty, "text");

        Assert.Equal(
            $"<cross-session-message from=\"{new string('a', 64)}\" from-mode=\"prompting\">\ntext\n</cross-session-message>",
            wrapped);
    }

    [Fact]
    public void AMultiLineBodyIsPreservedVerbatimInsideTheTag()
    {
        var wrapped = SessionMessageFrame.Wrap("buddy", "line one\nline two\nline three");

        Assert.Equal(
            "<cross-session-message from=\"buddy\" from-mode=\"prompting\">\n" +
            "line one\nline two\nline three\n" +
            "</cross-session-message>",
            wrapped);
    }

    // --- Encode -------------------------------------------------------------

    [Fact]
    public void WithATokenTheAuthLineComesFirst()
    {
        var msgId = Guid.NewGuid();
        var bytes = SessionMessageFrame.Encode("deadbeef", "buddy", "hi", msgId);
        var text = Encoding.UTF8.GetString(bytes);

        var lines = SplitLines(text);
        Assert.Equal(2, lines.Length);

        using var auth = JsonDocument.Parse(lines[0]);
        Assert.Equal("auth", auth.RootElement.GetProperty("type").GetString());
        Assert.Equal("deadbeef", auth.RootElement.GetProperty("token").GetString());
    }

    [Fact]
    public void WithNoTokenThereIsOnlyTheDeliverLine()
    {
        var bytes = SessionMessageFrame.Encode(null, "buddy", "hi", Guid.NewGuid());
        var lines = SplitLines(Encoding.UTF8.GetString(bytes));

        Assert.Single(lines);
        using var doc = JsonDocument.Parse(lines[0]);
        Assert.Equal("user", doc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public void AnEmptyTokenIsTreatedAsNoTokenRatherThanAnEmptyAuthLine()
    {
        var bytes = SessionMessageFrame.Encode("", "buddy", "hi", Guid.NewGuid());
        var lines = SplitLines(Encoding.UTF8.GetString(bytes));

        Assert.Single(lines);
    }

    [Fact]
    public void TheDeliverLineCarriesEveryFieldTheProtocolNeeds()
    {
        var msgId = Guid.NewGuid();
        var bytes = SessionMessageFrame.Encode("tok", "Claude Buddy on mini", "do the thing", msgId);
        var lines = SplitLines(Encoding.UTF8.GetString(bytes));

        using var doc = JsonDocument.Parse(lines[1]);
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("msgV").GetInt32());
        Assert.Equal(msgId.ToString(), root.GetProperty("msg_id").GetString());
        Assert.Equal("user", root.GetProperty("type").GetString());
        Assert.Equal("next", root.GetProperty("priority").GetString());
        Assert.Equal("Claude Buddy on mini", root.GetProperty("from").GetString());

        var message = root.GetProperty("message");
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal(
            SessionMessageFrame.Wrap("Claude Buddy on mini", "do the thing"),
            message.GetProperty("content").GetString());
    }

    // A fresh id every call — two deliveries must not collide on msg_id.
    [Fact]
    public void TheGivenMsgIdIsUsedVerbatimAndLowercaseWithDashes()
    {
        var msgId = Guid.NewGuid();
        var bytes = SessionMessageFrame.Encode(null, "buddy", "hi", msgId);
        var lines = SplitLines(Encoding.UTF8.GetString(bytes));

        using var doc = JsonDocument.Parse(lines[0]);
        var id = doc.RootElement.GetProperty("msg_id").GetString();

        Assert.Equal(msgId.ToString(), id);
        Assert.DoesNotContain(id, c => char.IsUpper(c));
        Assert.Contains('-', id);
    }

    [Fact]
    public void EveryLineEndsInExactlyOneNewlineAndNoCarriageReturn()
    {
        var bytes = SessionMessageFrame.Encode("tok", "buddy", "hi", Guid.NewGuid());
        var text = Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain("\r", text);
        Assert.EndsWith("\n", text);
        Assert.DoesNotContain("\n\n", text);
    }

    [Fact]
    public void TheContentValueIsNotDoubleEscaped()
    {
        // A body containing a literal quote must come back out exactly as it
        // went in once JSON-decoded — if Encode had escaped Wrap's string by
        // hand as well as through the serializer, this would come back with
        // doubled backslashes instead.
        var bytes = SessionMessageFrame.Encode(null, "buddy", "she said \"hi\"", Guid.NewGuid());
        var lines = SplitLines(Encoding.UTF8.GetString(bytes));

        using var doc = JsonDocument.Parse(lines[0]);
        var content = doc.RootElement.GetProperty("message").GetProperty("content").GetString();

        Assert.Contains("she said \"hi\"", content);
        Assert.DoesNotContain("\\\\", content);
    }

    private static string[] SplitLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
