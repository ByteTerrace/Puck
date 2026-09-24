using System.Net;
using System.Net.Sockets;
using Puck.Commands;
using Puck.Networking;
using Puck.Testing;

namespace Puck.Hosting.Tests;

// Law: every deadline on a control call runs on the clock its owner was given. Each deadline here is the longest the
// contract admits, so wall time cannot fire it inside a hang guard; only advancing the injected clock does.
public sealed class ControlDeadlineTests {
    private static readonly TimeSpan HandshakeDeadline = TimeSpan.FromSeconds(seconds: 5);
    // Bounds a wait that the test expects to end without any timer; it measures nothing.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(seconds: 30);
    private static readonly TimeSpan LongestCall = TimeSpan.FromMilliseconds(value: ControlLimits.TimeoutMilliseconds);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ClientConnectDeadlineRunsOnTheClientClock() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        using var listener = new TcpListener(
            localaddr: IPAddress.Loopback,
            port: 0
        );

        listener.Start();
        var capability = new LocalEndpointCapability(port: ((IPEndPoint)listener.LocalEndpoint).Port);
        var path = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-deadline-test-{Guid.NewGuid():N}.json"
        );

        try {
            using var file = capability.WriteDescriptor(path: path);
            var clock = new VirtualClock();
            var connect = LocalControlClient.ConnectAsync(
                attachmentPath: path,
                cancellationToken: Token,
                clock: clock
            );
            // The peer accepts and never speaks, so the handshake waits on nothing but the deadline.
            using var silent = await listener.AcceptTcpClientAsync(cancellationToken: Token).AsTask().WaitAsync(
                HangGuard,
                Token
            );

            Assert.False(condition: connect.IsCompleted);
            await clock.WhenArmedAsync(
                count: 1,
                ct: Token,
                dueTime: HandshakeDeadline
            );
            clock.Advance(by: HandshakeDeadline);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => connect.WaitAsync(
                HangGuard,
                Token
            ));
        } finally { File.Delete(path: path); }
    }
    [Fact]
    public async Task ClientCallDeadlineRunsOnTheClientClockAndClosesTheAttachment() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        var entered = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = new LocalControlServer(
            clock: new VirtualClock(),
            createSession: () => new StallSession(entered: entered)
        );
        var clock = new VirtualClock();
        using var client = await LocalControlClient.ConnectAsync(
            attachmentPath: host.AttachmentPath,
            cancellationToken: Token,
            clock: clock
        ).WaitAsync(
            HangGuard,
            Token
        );
        var call = client.ExecuteAsync(
            "exec",
            "stall",
            ControlLimits.TimeoutMilliseconds,
            Token
        );

        await entered.Task.WaitAsync(
            HangGuard,
            Token
        );
        Assert.False(condition: call.IsCompleted);
        await clock.WhenArmedAsync(
            count: 1,
            ct: Token,
            dueTime: LongestCall
        );
        clock.Advance(by: LongestCall);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => call.WaitAsync(
            HangGuard,
            Token
        ));
        Assert.True(condition: client.IsClosed);
    }
    [Fact]
    public async Task HostHandshakeDeadlineRunsOnTheHostClock() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        var clock = new VirtualClock();
        var sessions = 0;
        using var host = new LocalControlServer(
            clock: clock,
            createSession: () => { Interlocked.Increment(location: ref sessions); return new StallSession(entered: new()); }
        );
        using var peer = new TcpClient();

        await peer.ConnectAsync(
            IPAddress.Loopback,
            LocalEndpointCapability.ReadDescriptor(path: host.AttachmentPath).Port,
            Token
        );
        // The peer reads the host's challenge and never answers it.
        await clock.WhenArmedAsync(
            count: 1,
            ct: Token,
            dueTime: HandshakeDeadline
        );
        clock.Advance(by: HandshakeDeadline);
        await DrainUntilClosedAsync(stream: peer.GetStream());
        Assert.Equal(
            actual: Volatile.Read(location: ref sessions),
            expected: 0
        );
    }
    [Fact]
    public async Task HostRequestDeadlineRunsOnTheHostClockAndClosesTheConnection() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        var entered = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new VirtualClock();
        using var host = new LocalControlServer(
            clock: clock,
            createSession: () => new StallSession(entered: entered)
        );
        using var client = await LocalControlClient.ConnectAsync(
            attachmentPath: host.AttachmentPath,
            cancellationToken: Token,
            clock: new VirtualClock()
        ).WaitAsync(
            HangGuard,
            Token
        );
        var call = client.ExecuteAsync(
            "exec",
            "stall",
            ControlLimits.TimeoutMilliseconds,
            Token
        );

        await entered.Task.WaitAsync(
            HangGuard,
            Token
        );
        await clock.WhenArmedAsync(
            count: 1,
            ct: Token,
            dueTime: LongestCall
        );
        clock.Advance(by: LongestCall);
        await Assert.ThrowsAnyAsync<IOException>(testCode: () => call.WaitAsync(
            HangGuard,
            Token
        ));
        Assert.True(condition: client.IsClosed);
    }
    [Fact]
    public async Task ConsoleSessionDeadlineRunsOnTheSessionClock() {
        var source = new TextCommandSource(new CommandRegistry([]));
        var clock = new VirtualClock();
        using var session = new ConsoleControlSession(
            source,
            _ => throw new NotSupportedException(),
            clock: clock
        );
        // Nothing collects the source, so the line never settles and only the deadline can end the call.
        var call = session.ExecuteAsync(
            new(
                Command: "never-settles",
                Id: 1,
                Operation: "exec",
                TimeoutMilliseconds: ControlLimits.TimeoutMilliseconds
            ),
            Token
        );

        Assert.False(condition: call.IsCompleted);
        await clock.WhenArmedAsync(
            count: 1,
            ct: Token,
            dueTime: LongestCall
        );
        clock.Advance(by: LongestCall);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => call.WaitAsync(
            HangGuard,
            Token
        ));
    }

    private static async Task DrainUntilClosedAsync(NetworkStream stream) {
        var buffer = new byte[4096];

        while (await stream.ReadAsync(
            buffer: buffer,
            cancellationToken: Token
        ).AsTask().WaitAsync(
            HangGuard,
            Token
        ) > 0) { }
    }

    private sealed class StallSession(TaskCompletionSource entered) : IControlSession {
        public void Dispose() { }
        public async Task<ControlResponse> ExecuteAsync(ControlRequest request, CancellationToken cancellationToken) {
            entered.TrySetResult();
            await Task.Delay(
                cancellationToken: cancellationToken,
                millisecondsDelay: Timeout.Infinite
            );
            throw new InvalidOperationException(message: "A stalled session never completes.");
        }
    }
}
