using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeBuddy.Tests;

// SessionMessenger.DeliverAsync — every DeliveryResult arm, driven against a
// fake Seams. No real socket anywhere here; that seam is proved wire-compatible
// separately, in tests/IntegrationTests/SessionMessengerSocketTests.cs.
public class SessionMessengerTests
{
    private static SessionRegistry.Entry Entry(
        string sessionId = "abc", int pid = 1, int peerProtocol = 1, string? keyPath = null) =>
        SessionRegistry.Parse(
            $$"""
            {"pid":{{pid}},"sessionId":"{{sessionId}}","messagingSocketPath":"/tmp/a.sock",
             "peerProtocol":{{peerProtocol}},"status":"idle"}
            """, keyPath)!.Value;

    private static SessionMessenger.Seams Seams(
        SessionRegistry.Entry[]? entries = null,
        Func<int, bool>? pidAlive = null,
        Func<SessionRegistry.Entry, string?>? readKey = null,
        Func<string, byte[], CancellationToken, Task<bool>>? write = null) =>
        new(
            Registry: () => entries ?? Array.Empty<SessionRegistry.Entry>(),
            PidAlive: pidAlive ?? (_ => true),
            ReadKey: readKey ?? (_ => null),
            Write: write ?? ((_, _, _) => Task.FromResult(true)));

    [Fact]
    public async Task NoMatchingRegistryEntryIsReportedAsNoRegistryEntry()
    {
        var messenger = new SessionMessenger(Seams(entries: Array.Empty<SessionRegistry.Entry>()));

        var receipt = await messenger.DeliverAsync("missing", "buddy", "hi", CancellationToken.None);

        Assert.Equal(DeliveryResult.NoRegistryEntry, receipt.Result);
        Assert.Null(receipt.AgentStatus);
    }

    [Fact]
    public async Task ADeadPidIsAlsoReportedAsNoRegistryEntry()
    {
        var messenger = new SessionMessenger(Seams(
            entries: new[] { Entry() },
            pidAlive: _ => false));

        var receipt = await messenger.DeliverAsync("abc", "buddy", "hi", CancellationToken.None);

        Assert.Equal(DeliveryResult.NoRegistryEntry, receipt.Result);
    }

    [Fact]
    public async Task AnUnsupportedProtocolIsReportedWithTheAgentsStatus()
    {
        var messenger = new SessionMessenger(Seams(
            entries: new[] { Entry(peerProtocol: 2) }));

        var receipt = await messenger.DeliverAsync("abc", "buddy", "hi", CancellationToken.None);

        Assert.Equal(DeliveryResult.UnsupportedProtocol, receipt.Result);
        Assert.Equal("idle", receipt.AgentStatus);
    }

    [Fact]
    public async Task AWriteThatReturnsFalseIsReportedAsSocketRefused()
    {
        var messenger = new SessionMessenger(Seams(
            entries: new[] { Entry() },
            write: (_, _, _) => Task.FromResult(false)));

        var receipt = await messenger.DeliverAsync("abc", "buddy", "hi", CancellationToken.None);

        Assert.Equal(DeliveryResult.SocketRefused, receipt.Result);
        Assert.Equal("idle", receipt.AgentStatus);
    }

    [Fact]
    public async Task AWriteThatReturnsTrueIsReportedAsAccepted()
    {
        var messenger = new SessionMessenger(Seams(
            entries: new[] { Entry() },
            write: (_, _, _) => Task.FromResult(true)));

        var receipt = await messenger.DeliverAsync("abc", "buddy", "hi", CancellationToken.None);

        Assert.Equal(DeliveryResult.Accepted, receipt.Result);
        Assert.Equal("idle", receipt.AgentStatus);
    }

    [Fact]
    public async Task TheSocketPathHandedToWriteComesFromTheMatchedEntry()
    {
        string? seen = null;
        var messenger = new SessionMessenger(Seams(
            entries: new[] { Entry() },
            write: (path, _, _) => { seen = path; return Task.FromResult(true); }));

        await messenger.DeliverAsync("abc", "buddy", "hi", CancellationToken.None);

        Assert.Equal("/tmp/a.sock", seen);
    }

    [Fact]
    public async Task TheKeyReadIsUsedAsTheEncodedAuthToken()
    {
        byte[]? sentBytes = null;
        var messenger = new SessionMessenger(Seams(
            entries: new[] { Entry() },
            readKey: _ => "the-token",
            write: (_, bytes, _) => { sentBytes = bytes; return Task.FromResult(true); }));

        await messenger.DeliverAsync("abc", "buddy", "hi", CancellationToken.None);

        Assert.NotNull(sentBytes);
        var text = System.Text.Encoding.UTF8.GetString(sentBytes!);
        Assert.Contains("\"the-token\"", text);
        Assert.Contains("\"type\":\"auth\"", text);
    }

    [Fact]
    public void FromNameWrapsTheMachineTag()
    {
        Assert.Equal("Claude Buddy on mini", SessionMessenger.FromName("mini"));
    }
}
