using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeBuddy.IntegrationTests;

// SessionMessenger.Live's Write seam against a real Unix domain socket — the
// one thing SessionMessageFrameTests cannot prove: that Encode's bytes are
// actually wire-compatible with something listening on the far end, not
// merely JSON-shaped. Loopback for the socket's own reason a TCP test would
// use 127.0.0.1: this proves the framing and the connect/write/close
// sequence without needing an actual Claude Code process to receive it.
public class SessionMessengerSocketTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "cb-msgsock-" + Guid.NewGuid().ToString("N")[..8]);

    public SessionMessengerSocketTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string SocketPath(string name) => Path.Combine(_dir, name);

    // The real Write delegate SessionMessenger.Live wires up — pulled out of
    // a live Seams rather than reflected into, so this exercises exactly what
    // DeliverAsync would call in production, configRoots included even though
    // Write itself never looks at them.
    private static Func<string, byte[], CancellationToken, Task<bool>> RealWrite() =>
        SessionMessenger.Live(Array.Empty<string>()).Write;

    private static Socket Listen(string path)
    {
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        return listener;
    }

    private static async Task<byte[]> ReadAllAsync(Socket socket)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[4096];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        while (true)
        {
            var read = await socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token);
            if (read == 0) break;
            stream.Write(buffer, 0, read);
        }

        return stream.ToArray();
    }

    [Fact]
    public async Task TheDeliverLineArrivesByteForByteOnARealListener()
    {
        var path = SocketPath("a.sock");
        using var listener = Listen(path);
        var acceptTask = listener.AcceptAsync();

        var bytes = SessionMessageFrame.Encode(null, "buddy", "hello", Guid.NewGuid());

        Assert.True(await RealWrite()(path, bytes, CancellationToken.None));

        using var accepted = await acceptTask.WaitAsync(TimeSpan.FromSeconds(10));
        var received = await ReadAllAsync(accepted);

        Assert.Equal(bytes, received);
    }

    [Fact]
    public async Task TheAuthLineArrivesBeforeTheDeliverLineWhenATokenIsGiven()
    {
        var path = SocketPath("b.sock");
        using var listener = Listen(path);
        var acceptTask = listener.AcceptAsync();

        var bytes = SessionMessageFrame.Encode("tok123", "buddy", "hello", Guid.NewGuid());

        Assert.True(await RealWrite()(path, bytes, CancellationToken.None));

        using var accepted = await acceptTask.WaitAsync(TimeSpan.FromSeconds(10));
        var received = Encoding.UTF8.GetString(await ReadAllAsync(accepted));

        var lines = received.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"type\":\"auth\"", lines[0]);
        Assert.Contains("tok123", lines[0]);
        Assert.Contains("\"type\":\"user\"", lines[1]);
    }

    [Fact]
    public async Task ConnectingWhereNothingIsListeningFailsCleanlyRatherThanThrowing()
    {
        var path = SocketPath("nobody-home.sock");

        var ok = await RealWrite()(path, new byte[] { 1 }, CancellationToken.None);

        Assert.False(ok);
    }

    // A peer that accepts and then hangs up without reading a byte must not
    // hang the caller past Write's own five-second ceiling — the shape a
    // headless session that has just crashed would present.
    [Fact]
    public async Task ARemoteThatAcceptsThenClosesWithoutReadingDoesNotHang()
    {
        var path = SocketPath("c.sock");
        using var listener = Listen(path);

        var acceptAndClose = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptAsync();
            accepted.Close();
        });

        var bytes = SessionMessageFrame.Encode(null, "buddy", "hello", Guid.NewGuid());

        var stopwatch = Stopwatch.StartNew();
        await RealWrite()(path, bytes, CancellationToken.None);
        stopwatch.Stop();

        await acceptAndClose.WaitAsync(TimeSpan.FromSeconds(10));

        // Either true or false is a defensible answer from a peer that never
        // reads — what must not happen is waiting past the write's own
        // timeout, which this asserts with headroom rather than pinning the
        // exact boolean an OS-dependent race would otherwise decide.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(8));
    }
}
