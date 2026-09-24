using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Networking;

namespace Puck.Hosting.Tests;

public sealed class ControlAdversarialTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<TcpClient> ConnectRawAsync(string path) {
        var capability = LocalEndpointCapability.ReadDescriptor(path: path);
        var peer = new TcpClient();

        try {
            await peer.ConnectAsync(
                IPAddress.Loopback,
                capability.Port,
                Token
            );
            await capability.AuthenticateAsync(
                peer.GetStream(),
                server: false,
                Token
            );
            return peer;
        } catch { peer.Dispose(); throw; }
    }

    [InlineData("{\"id\":1,\"status\":\"completed\"}")]
    [InlineData("{\"id\":1,\"status\":\"completed\",\"output\":null}")]
    [InlineData("{\"id\":1,\"status\":\"invented\",\"output\":\"bad\"}")]
    [InlineData("{\"id\":1,\"status\":\"completed\",\"output\":\"one\",\"output\":\"two\"}")]
    [InlineData("{\"id\":1,\"status\":\"completed\",\"output\":\"bad\",\"png\":\"AQID\"}")]
    [Theory]
    public async Task ClientRejectsMalformedAuthenticatedResponses(string json) {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        using var listener = new TcpListener(
            localaddr: IPAddress.Loopback,
            port: 0
        );

        listener.Start();
        var capability = new LocalEndpointCapability(port: ((IPEndPoint)listener.LocalEndpoint).Port);
        var path = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-response-test-{Guid.NewGuid():N}.json"
        );

        try {
            using var file = capability.WriteDescriptor(path: path);
            var connect = LocalControlClient.ConnectAsync(
                attachmentPath: path,
                cancellationToken: Token
            );
            using var socket = await listener.AcceptTcpClientAsync(cancellationToken: Token);

            await capability.AuthenticateAsync(
                socket.GetStream(),
                server: true,
                Token
            );
            using var client = await connect;
            var call = client.ExecuteAsync(
                "exec",
                "probe",
                cancellationToken: Token
            );

            Assert.True(condition: (await WireFrame.ReadAsync(
                socket.GetStream(),
                (ControlLimits.RequestBytes + WireFrame.PrefixBytes),
                Token
            )).Ok);
            await WireFrame.WriteAsync(
                socket.GetStream(),
                2,
                Encoding.UTF8.GetBytes(s: json),
                Token
            );
            var error = await Record.ExceptionAsync(testCode: async () => await call);

            Assert.True(
                condition: (error is InvalidDataException or JsonException),
                userMessage: (error?.ToString() ?? "Malformed response was accepted.")
            );
            await Assert.ThrowsAsync<ObjectDisposedException>(testCode: () => client.ExecuteAsync(
                "exec",
                "never",
                cancellationToken: Token
            ));
        } finally { File.Delete(path: path); }
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task DeadlineAndDisconnectReleaseAnUncooperativeSession(bool disconnect) {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        var entered = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new TaskCompletionSource<ControlResponse>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var disposals = 0;
        using var host = new LocalControlServer(createSession: () => new StubbornSession(
            entered,
            work,
            () => { Interlocked.Increment(location: ref disposals); disposed.TrySetResult(); }
        ));
        using var peer = await ConnectRawAsync(path: host.AttachmentPath);

        try {
            await WireFrame.WriteAsync(
                peer.GetStream(),
                1,
                "{\"id\":1,\"operation\":\"exec\",\"command\":\"wait\",\"timeoutMilliseconds\":100}"u8.ToArray(),
                Token
            );
            await entered.Task.WaitAsync(
                TimeSpan.FromSeconds(seconds: 3),
                Token
            );
            if (disconnect) { peer.Dispose(); } else {
                var frame = await WireFrame.ReadAsync(
                    peer.GetStream(),
                    1024,
                    Token
                ).WaitAsync(
                    TimeSpan.FromSeconds(seconds: 3),
                    Token
                );

                Assert.Equal(
                    WireRefusal.ConnectionClosed,
                    frame.Failure.Refusal
                );
            }
            await disposed.Task.WaitAsync(
                TimeSpan.FromSeconds(seconds: 3),
                Token
            );
            Assert.Equal(
                actual: disposals,
                expected: 1
            );
            Assert.False(condition: work.Task.IsCompleted);
        } finally {
            work.TrySetResult(result: new(
            1,
            "completed",
            "late"
        ));
        }
    }
    [Fact]
    public async Task DisposingConsoleSessionEndsAnAcceptedCaptureWaitWithoutDeletingItsPathEarly() {
        var source = new TextCommandSource(new CommandRegistry([]));
        FrameCaptureRequest? capture = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token: Token);
        using var session = new ConsoleControlSession(
            source,
            path => capture = new(path: path)
        );
        var operation = session.ExecuteAsync(
            new(
                Command: null,
                Id: 1,
                Operation: "capture",
                TimeoutMilliseconds: 1000
            ),
            stop.Token
        );

        source.Collect();
        Assert.NotNull(@object: capture);
        try {
            session.Dispose();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => operation.WaitAsync(
                TimeSpan.FromSeconds(seconds: 2),
                Token
            ));
            Assert.True(condition: Directory.Exists(path: Path.GetDirectoryName(path: capture.Path)));
        } finally {
            stop.Cancel();
            capture.TryFail(error: new IOException(message: "Test capture ended."));
            try { await operation; } catch (OperationCanceledException) { }
        }
    }
    [Fact]
    public async Task HostReportsInvalidSessionResultsAsUnknownInsteadOfCrashingOrCertifyingSuccess() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        using var host = new LocalControlServer(createSession: () => new CallbackSession(execute: request => new(
            request.Id,
            "completed",
            null!
        )));
        using var client = await LocalControlClient.ConnectAsync(
            attachmentPath: host.AttachmentPath,
            cancellationToken: Token
        );
        var result = await client.ExecuteAsync(
            "exec",
            "probe",
            cancellationToken: Token
        );

        Assert.True(condition: result.IsError);
        Assert.Equal(
            "unknown",
            result.Status
        );
    }
    [InlineData("{\"id\":1,\"operation\":\"capture\",\"timeoutMilliseconds\":1000}")]
    [InlineData("{\"id\":1,\"operation\":\"exec\",\"operation\":\"capture\",\"command\":null,\"timeoutMilliseconds\":1000}")]
    [Theory]
    public async Task MissingAndDuplicateRequestFieldsNeverDispatch(string json) {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        var calls = 0;
        using var host = new LocalControlServer(createSession: () => new CallbackSession(execute: request => {
            Interlocked.Increment(location: ref calls); return new(
            request.Id,
            "completed",
            "ran"
        );
        }));
        using var peer = await ConnectRawAsync(path: host.AttachmentPath);

        await WireFrame.WriteAsync(
            peer.GetStream(),
            1,
            Encoding.UTF8.GetBytes(s: json),
            Token
        );
        var frame = await WireFrame.ReadAsync(
            peer.GetStream(),
            1024,
            Token
        ).WaitAsync(
            TimeSpan.FromSeconds(seconds: 3),
            Token
        );

        Assert.Equal(
            WireRefusal.ConnectionClosed,
            frame.Failure.Refusal
        );
        Assert.Equal(
            actual: calls,
            expected: 0
        );
    }

    private sealed class StubbornSession(TaskCompletionSource entered, TaskCompletionSource<ControlResponse> work, Action disposed) : IControlSession {
        public void Dispose() => disposed();
        public Task<ControlResponse> ExecuteAsync(ControlRequest request, CancellationToken cancellationToken) { entered.TrySetResult(); return work.Task; }
    }
    private sealed class CallbackSession(Func<ControlRequest, ControlResponse> execute) : IControlSession {
        public void Dispose() { }
        public Task<ControlResponse> ExecuteAsync(ControlRequest request, CancellationToken cancellationToken) => Task.FromResult(result: execute(request));
    }
}
