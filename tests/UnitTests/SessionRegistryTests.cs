using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace ClaudeBuddy.Tests;

// SessionRegistry — turning Claude Code's own ~/.claude/sessions/<pid>.json
// files into Entry values a caller can dial. The fixture below is the
// (redacted) real example this file was built from, not invented — see
// SessionRegistry.cs's header for where it came from.
public class SessionRegistryTests : IDisposable
{
    private const string RealSessionJson = """
        {"pid":40957,"sessionId":"01991a2c-1234-7abc-9def-0123456789ab","cwd":"/some/path",
         "version":"2.1.261","peerProtocol":1,
         "peerFeatures":["notify_idle","reply_across_default_dirs","artifact_yield"],
         "kind":"bg","entrypoint":"cli","messagingSocketPath":"/tmp/cc-socks/40957.sock",
         "name":"job-hunter","jobId":"94f106","status":"idle","bridgeSessionId":"abc123"}
        """;

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "cb-registry-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // --- Parse --------------------------------------------------------------

    [Fact]
    public void ARealEntryParsesEveryFieldThisFileUses()
    {
        var entry = SessionRegistry.Parse(RealSessionJson, keyPath: "/some/key");

        Assert.NotNull(entry);
        Assert.Equal(40957, entry!.Value.Pid);
        Assert.Equal("01991a2c-1234-7abc-9def-0123456789ab", entry.Value.SessionId);
        Assert.Equal("job-hunter", entry.Value.Name);
        Assert.Equal("bg", entry.Value.Kind);
        Assert.Equal(1, entry.Value.PeerProtocol);
        Assert.Equal("/tmp/cc-socks/40957.sock", entry.Value.SocketPath);
        Assert.Equal("idle", entry.Value.Status);
        Assert.Equal("/some/key", entry.Value.KeyPath);
    }

    [Fact]
    public void MissingMessagingSocketPathIsRejected()
    {
        const string json = """{"pid":1,"sessionId":"abc"}""";

        Assert.Null(SessionRegistry.Parse(json, null));
    }

    [Fact]
    public void MissingSessionIdIsRejected()
    {
        const string json = """{"pid":1,"messagingSocketPath":"/tmp/x.sock"}""";

        Assert.Null(SessionRegistry.Parse(json, null));
    }

    [Fact]
    public void MissingPidIsRejected()
    {
        const string json = """{"sessionId":"abc","messagingSocketPath":"/tmp/x.sock"}""";

        Assert.Null(SessionRegistry.Parse(json, null));
    }

    [Fact]
    public void APidThatIsNotANumberIsRejected()
    {
        const string json = """
            {"pid":"not-a-number","sessionId":"abc","messagingSocketPath":"/tmp/x.sock"}
            """;

        Assert.Null(SessionRegistry.Parse(json, null));
    }

    // A JSON number that TryGetInt32 cannot represent — the number branch is
    // taken, but the conversion itself still fails.
    [Fact]
    public void APidThatIsANonIntegerNumberIsRejected()
    {
        const string json = """
            {"pid":40957.5,"sessionId":"abc","messagingSocketPath":"/tmp/x.sock"}
            """;

        Assert.Null(SessionRegistry.Parse(json, null));
    }

    [Fact]
    public void AbsentPeerProtocolDefaultsToZeroRatherThanMatchingSupported()
    {
        const string json = """{"pid":1,"sessionId":"abc","messagingSocketPath":"/tmp/x.sock"}""";

        var entry = SessionRegistry.Parse(json, null);

        Assert.NotNull(entry);
        Assert.Equal(0, entry!.Value.PeerProtocol);
        Assert.False(SessionRegistry.Speaks(entry.Value));
    }

    [Fact]
    public void APresentPeerProtocolIsCarriedThrough()
    {
        const string json = """
            {"pid":1,"sessionId":"abc","messagingSocketPath":"/tmp/x.sock","peerProtocol":2}
            """;

        var entry = SessionRegistry.Parse(json, null);

        Assert.Equal(2, entry!.Value.PeerProtocol);
    }

    // A peerProtocol of the wrong JSON type is treated the same as an absent
    // one — it defaults to 0 rather than throwing or matching by coincidence.
    [Fact]
    public void APeerProtocolOfTheWrongTypeDefaultsToZero()
    {
        const string json = """
            {"pid":1,"sessionId":"abc","messagingSocketPath":"/tmp/x.sock","peerProtocol":"one"}
            """;

        var entry = SessionRegistry.Parse(json, null);

        Assert.Equal(0, entry!.Value.PeerProtocol);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"pid\": ")]
    [InlineData("[1, 2, 3]")]
    public void MalformedOrWrongShapedJsonNeverThrows(string json)
    {
        Assert.Null(SessionRegistry.Parse(json, null));
    }

    [Fact]
    public void OptionalFieldsFallBackWhenAbsent()
    {
        const string json = """{"pid":1,"sessionId":"abc","messagingSocketPath":"/tmp/x.sock"}""";

        var entry = SessionRegistry.Parse(json, null);

        Assert.Null(entry!.Value.Name);
        Assert.Equal("", entry.Value.Kind);
        Assert.Null(entry.Value.Status);
    }

    // --- KeyFileName ----------------------------------------------------------

    [Fact]
    public void KeyFileNameMatchesAManuallyComputedSha256()
    {
        const string socketPath = "/tmp/cc-socks/40957.sock";
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(socketPath)));

        Assert.Equal($"40957.{expectedHash}.key", SessionRegistry.KeyFileName(40957, socketPath));
    }

    [Fact]
    public void KeyFileNameIsDeterministic()
    {
        var first = SessionRegistry.KeyFileName(1, "/tmp/a.sock");
        var second = SessionRegistry.KeyFileName(1, "/tmp/a.sock");

        Assert.Equal(first, second);
    }

    [Fact]
    public void KeyFileNameDependsOnTheFullSocketPathNotJustThePid()
    {
        Assert.NotEqual(
            SessionRegistry.KeyFileName(1, "/tmp/a.sock"),
            SessionRegistry.KeyFileName(1, "/tmp/b.sock"));
    }

    // --- Scan -------------------------------------------------------------

    private string Root(string name) => Path.Combine(_dir, name);

    private static void WriteSession(string sessionsDir, string fileName, string json)
    {
        Directory.CreateDirectory(sessionsDir);
        File.WriteAllText(Path.Combine(sessionsDir, fileName), json);
    }

    [Fact]
    public void ScanSkipsARootWithNoSessionsDirectoryEntirely()
    {
        var missingRoot = Root("missing");
        var goodRoot = Root("good");
        WriteSession(Path.Combine(goodRoot, "sessions"), "1.json",
            """{"pid":1,"sessionId":"one","messagingSocketPath":"/tmp/one.sock"}""");

        var entries = SessionRegistry.Scan(new[] { missingRoot, goodRoot });

        Assert.Single(entries);
        Assert.Equal("one", entries[0].SessionId);
    }

    // A file that reads fine but doesn't parse — Parse itself catches this
    // and returns null, so this is the `withoutKey is null` continue on
    // Scan's happy path, not the outer catch. AFileThatCannotBeReadAtAllDoesNotStopTheScan
    // below is the one that reaches the try/catch around the read itself.
    [Fact]
    public void AFileThatParsesToNothingDoesNotStopTheRestOfTheScan()
    {
        var sessionsDir = Path.Combine(Root("account"), "sessions");
        WriteSession(sessionsDir, "1.json",
            """{"pid":1,"sessionId":"one","messagingSocketPath":"/tmp/one.sock"}""");
        WriteSession(sessionsDir, "2.json", "{ this is not json");
        WriteSession(sessionsDir, "3.json",
            """{"pid":3,"sessionId":"three","messagingSocketPath":"/tmp/three.sock"}""");

        var entries = SessionRegistry.Scan(new[] { Root("account") });

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.SessionId == "one");
        Assert.Contains(entries, e => e.SessionId == "three");
    }

    // The outer catch itself: a file that exists but cannot be read at all,
    // so File.ReadAllText throws before Parse ever sees any bytes. Without
    // this catch, one unreadable status file would take every other
    // session's entry down with it.
    //
    // Arranged per platform because there is no one portable way to make a
    // file unreadable — Unix has mode bits, Windows has share modes — the
    // same split BundleCacheLayoutTests.AMarkerThatCannotBeReadIsTreatedAsNotMatching
    // uses for the same reason.
    [Fact]
    public void AFileThatCannotBeReadAtAllDoesNotStopTheRestOfTheScan()
    {
        var sessionsDir = Path.Combine(Root("account"), "sessions");
        WriteSession(sessionsDir, "1.json",
            """{"pid":1,"sessionId":"one","messagingSocketPath":"/tmp/one.sock"}""");
        var unreadable = Path.Combine(sessionsDir, "2.json");
        File.WriteAllText(unreadable, """{"pid":2,"sessionId":"two","messagingSocketPath":"/tmp/two.sock"}""");
        WriteSession(sessionsDir, "3.json",
            """{"pid":3,"sessionId":"three","messagingSocketPath":"/tmp/three.sock"}""");

        if (OperatingSystem.IsWindows())
        {
            using var exclusive = new FileStream(
                unreadable, FileMode.Open, FileAccess.Read, FileShare.None);

            var entries = SessionRegistry.Scan(new[] { Root("account") });

            Assert.Equal(2, entries.Count);
            Assert.DoesNotContain(entries, e => e.SessionId == "two");
        }
        else
        {
            File.SetUnixFileMode(unreadable, UnixFileMode.None);
            try
            {
                var entries = SessionRegistry.Scan(new[] { Root("account") });

                Assert.Equal(2, entries.Count);
                Assert.DoesNotContain(entries, e => e.SessionId == "two");
            }
            finally
            {
                // Readable again, or Dispose cannot delete the scratch directory.
                File.SetUnixFileMode(unreadable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    [Fact]
    public void AFileWithNoMatchingKeyGetsANullKeyPath()
    {
        var sessionsDir = Path.Combine(Root("account"), "sessions");
        WriteSession(sessionsDir, "1.json",
            """{"pid":1,"sessionId":"one","messagingSocketPath":"/tmp/no-key.sock"}""");

        var entries = SessionRegistry.Scan(new[] { Root("account") });

        var entry = Assert.Single(entries);
        Assert.Null(entry.KeyPath);
    }

    [Fact]
    public void AFileWithAMatchingKeyGetsItsPathResolved()
    {
        var sessionsDir = Path.Combine(Root("account"), "sessions");
        const string socketPath = "/tmp/cc-socks/40957.sock";
        WriteSession(sessionsDir, "40957.json",
            $$"""{"pid":40957,"sessionId":"one","messagingSocketPath":"{{socketPath}}"}""");

        var keyFileName = SessionRegistry.KeyFileName(40957, socketPath);
        File.WriteAllText(Path.Combine(sessionsDir, keyFileName), """{"peerToken":"abc"}""");

        var entries = SessionRegistry.Scan(new[] { Root("account") });

        var entry = Assert.Single(entries);
        Assert.Equal(Path.Combine(sessionsDir, keyFileName), entry.KeyPath);
    }

    [Fact]
    public void TwoConfigRootsBothContributeEntries()
    {
        WriteSession(Path.Combine(Root("one"), "sessions"), "1.json",
            """{"pid":1,"sessionId":"a","messagingSocketPath":"/tmp/a.sock"}""");
        WriteSession(Path.Combine(Root("two"), "sessions"), "2.json",
            """{"pid":2,"sessionId":"b","messagingSocketPath":"/tmp/b.sock"}""");

        var entries = SessionRegistry.Scan(new[] { Root("one"), Root("two") });

        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public void ScanningNoRootsAtAllIsEmptyRatherThanAnError()
    {
        Assert.Empty(SessionRegistry.Scan(Array.Empty<string>()));
    }

    // --- Find -------------------------------------------------------------

    [Fact]
    public void FindReturnsTheEntryWhosePidIsAlive()
    {
        var entries = new[]
        {
            SessionRegistry.Parse(
                """{"pid":1,"sessionId":"target","messagingSocketPath":"/tmp/a.sock"}""", null)!.Value,
        };

        var found = SessionRegistry.Find(entries, "target", pid => pid == 1);

        Assert.NotNull(found);
        Assert.Equal(1, found!.Value.Pid);
    }

    [Fact]
    public void FindFiltersOutADeadPid()
    {
        var entries = new[]
        {
            SessionRegistry.Parse(
                """{"pid":1,"sessionId":"target","messagingSocketPath":"/tmp/a.sock"}""", null)!.Value,
        };

        var found = SessionRegistry.Find(entries, "target", pid => false);

        Assert.Null(found);
    }

    [Fact]
    public void FindIsCaseSensitiveOnTheSessionId()
    {
        var entries = new[]
        {
            SessionRegistry.Parse(
                """{"pid":1,"sessionId":"AbC","messagingSocketPath":"/tmp/a.sock"}""", null)!.Value,
        };

        Assert.Null(SessionRegistry.Find(entries, "abc", pid => true));
        Assert.NotNull(SessionRegistry.Find(entries, "AbC", pid => true));
    }

    [Fact]
    public void FindAgainstNoEntriesIsNull()
    {
        Assert.Null(SessionRegistry.Find(Array.Empty<SessionRegistry.Entry>(), "anything", pid => true));
    }

    // --- Speaks -------------------------------------------------------------

    [Fact]
    public void ASupportedProtocolWithARealSocketPathSpeaks()
    {
        var entry = SessionRegistry.Parse(
            """{"pid":1,"sessionId":"a","messagingSocketPath":"/tmp/a.sock","peerProtocol":1}""", null)!.Value;

        Assert.True(SessionRegistry.Speaks(entry));
    }

    [Fact]
    public void AnUnsupportedProtocolVersionDoesNotSpeak()
    {
        var entry = SessionRegistry.Parse(
            """{"pid":1,"sessionId":"a","messagingSocketPath":"/tmp/a.sock","peerProtocol":2}""", null)!.Value;

        Assert.False(SessionRegistry.Speaks(entry));
    }

    [Fact]
    public void AWindowsNamedPipePathDoesNotSpeak()
    {
        var entry = SessionRegistry.Parse(
            """
            {"pid":1,"sessionId":"a","messagingSocketPath":"\\\\.\\pipe\\40957","peerProtocol":1}
            """, null)!.Value;

        Assert.False(SessionRegistry.Speaks(entry));
    }

    // Parse itself never produces an empty SocketPath — MissingMessagingSocketPathIsRejected
    // covers that — but Speaks is asked to judge an Entry, not JSON, so an
    // empty one built directly must still be refused rather than assumed away.
    [Fact]
    public void AnEmptySocketPathDoesNotSpeak()
    {
        var entry = new SessionRegistry.Entry(1, "a", null, "", 1, "", null, null);

        Assert.False(SessionRegistry.Speaks(entry));
    }
}
