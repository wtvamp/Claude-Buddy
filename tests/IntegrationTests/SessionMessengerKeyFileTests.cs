using Xunit;

namespace ClaudeBuddy.IntegrationTests;

// SessionMessenger.Live's ReadKey seam against a real key file on disk — the
// half of the seam that isn't a socket. Marked [ExcludeFromCodeCoverage]
// alongside the rest of Live for the same reason UsagePoller.ReadAccountFile
// is: it is real File I/O, not decision logic. Excluded from the coverage
// count does not mean untested, so it is checked here the same way the
// integration suite already checks the other file-backed seams in this repo.
public class SessionMessengerKeyFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "cb-keyfile-" + Guid.NewGuid().ToString("N")[..8]);

    public SessionMessengerKeyFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static SessionRegistry.Entry EntryWithKeyPath(string? keyPath) =>
        new(1, "a", null, "bg", 1, "/tmp/a.sock", "idle", keyPath);

    private static Func<SessionRegistry.Entry, string?> ReadKey() =>
        SessionMessenger.Live(Array.Empty<string>()).ReadKey;

    [Fact]
    public void ARealKeyFileYieldsItsPeerToken()
    {
        var path = Path.Combine(_dir, "1.abc.key");
        File.WriteAllText(path, """{"peerToken":"deadbeefcafe"}""");

        Assert.Equal("deadbeefcafe", ReadKey()(EntryWithKeyPath(path)));
    }

    [Fact]
    public void ANullKeyPathIsNoToken()
    {
        Assert.Null(ReadKey()(EntryWithKeyPath(null)));
    }

    [Fact]
    public void AKeyPathThatDoesNotExistIsNoToken()
    {
        Assert.Null(ReadKey()(EntryWithKeyPath(Path.Combine(_dir, "missing.key"))));
    }

    [Fact]
    public void AMalformedKeyFileIsNoTokenRatherThanAThrow()
    {
        var path = Path.Combine(_dir, "1.abc.key");
        File.WriteAllText(path, "{ not json");

        Assert.Null(ReadKey()(EntryWithKeyPath(path)));
    }

    [Fact]
    public void AKeyFileWithNoPeerTokenFieldIsNoToken()
    {
        var path = Path.Combine(_dir, "1.abc.key");
        File.WriteAllText(path, """{"otherField":"x"}""");

        Assert.Null(ReadKey()(EntryWithKeyPath(path)));
    }

    [Fact]
    public void AKeyFileWhoseTopLevelIsNotAnObjectIsNoToken()
    {
        var path = Path.Combine(_dir, "1.abc.key");
        File.WriteAllText(path, "[1, 2, 3]");

        Assert.Null(ReadKey()(EntryWithKeyPath(path)));
    }
}
