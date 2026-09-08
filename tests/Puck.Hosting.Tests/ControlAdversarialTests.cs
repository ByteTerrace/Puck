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

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DeadlineAndDisconnectReleaseAnUncooperativeSession(bool disconnect) {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows capability ACLs are required."); return; }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new TaskCompletionSource<ControlResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposals = 0;
        using var host = new LocalControlServer(() => new StubbornSession(entered, work, () => { Interlocked.Increment(ref disposals); disposed.TrySetResult(); }));
        using var peer = await ConnectRawAsync(host.AttachmentPath);
        try {
            await WireFrame.WriteAsync(peer.GetStream(), 1, "{\"id\":1,\"operation\":\"exec\",\"command\":\"wait\",\"timeoutMilliseconds\":100}"u8.ToArray(), Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), Token);
            if (disconnect) { peer.Dispose(); }
            else {
                var frame = await WireFrame.ReadAsync(peer.GetStream(), 1024, Token).WaitAsync(TimeSpan.FromSeconds(3), Token);
                Assert.Equal(WireRefusal.ConnectionClosed, frame.Failure.Refusal);
            }
            await disposed.Task.WaitAsync(TimeSpan.FromSeconds(3), Token);
            Assert.Equal(1, disposals);
            Assert.False(work.Task.IsCompleted);
        } finally { work.TrySetResult(new(1, "completed", "late")); }
    }

    [Theory]
    [InlineData("{\"id\":1,\"operation\":\"capture\",\"timeoutMilliseconds\":1000}")]
    [InlineData("{\"id\":1,\"operation\":\"exec\",\"operation\":\"capture\",\"command\":null,\"timeoutMilliseconds\":1000}")]
    public async Task MissingAndDuplicateRequestFieldsNeverDispatch(string json) {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows capability ACLs are required."); return; }
        var calls = 0;
        using var host = new LocalControlServer(() => new CallbackSession(request => { Interlocked.Increment(ref calls); return new(request.Id, "completed", "ran"); }));
        using var peer = await ConnectRawAsync(host.AttachmentPath);
        await WireFrame.WriteAsync(peer.GetStream(), 1, Encoding.UTF8.GetBytes(json), Token);
        var frame = await WireFrame.ReadAsync(peer.GetStream(), 1024, Token).WaitAsync(TimeSpan.FromSeconds(3), Token);
        Assert.Equal(WireRefusal.ConnectionClosed, frame.Failure.Refusal);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("{\"id\":1,\"status\":\"completed\"}")]
    [InlineData("{\"id\":1,\"status\":\"completed\",\"output\":null}")]
    [InlineData("{\"id\":1,\"status\":\"invented\",\"output\":\"bad\"}")]
    [InlineData("{\"id\":1,\"status\":\"completed\",\"output\":\"one\",\"output\":\"two\"}")]
    [InlineData("{\"id\":1,\"status\":\"completed\",\"output\":\"bad\",\"png\":\"AQID\"}")]
    public async Task ClientRejectsMalformedAuthenticatedResponses(string json) {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows capability ACLs are required."); return; }
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var capability = new LocalEndpointCapability(((IPEndPoint)listener.LocalEndpoint).Port);
        var path = Path.Combine(Path.GetTempPath(), $"puck-response-test-{Guid.NewGuid():N}.json");
        try {
            using var file = capability.WriteDescriptor(path);
            var connect = LocalControlClient.ConnectAsync(path, Token);
            using var socket = await listener.AcceptTcpClientAsync(Token);
            await capability.AuthenticateAsync(socket.GetStream(), server: true, Token);
            using var client = await connect;
            var call = client.ExecuteAsync("exec", "probe", cancellationToken: Token);
            Assert.True((await WireFrame.ReadAsync(socket.GetStream(), ControlLimits.RequestBytes + WireFrame.PrefixBytes, Token)).Ok);
            await WireFrame.WriteAsync(socket.GetStream(), 2, Encoding.UTF8.GetBytes(json), Token);
            var error = await Record.ExceptionAsync(async () => await call);
            Assert.True(error is InvalidDataException or JsonException, error?.ToString() ?? "Malformed response was accepted.");
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ExecuteAsync("exec", "never", cancellationToken: Token));
        } finally { File.Delete(path); }
    }

    [Fact]
    public async Task DisposingConsoleSessionEndsAnAcceptedCaptureWaitWithoutDeletingItsPathEarly() {
        var source = new TextCommandSource(new CommandRegistry([]));
        FrameCaptureRequest? capture = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var session = new ConsoleControlSession(source, path => capture = new(path));
        var operation = session.ExecuteAsync(new(1, "capture", null, 1000), stop.Token);
        source.Collect();
        Assert.NotNull(capture);
        try {
            session.Dispose();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(2), Token));
            Assert.True(Directory.Exists(Path.GetDirectoryName(capture.Path)));
        } finally {
            stop.Cancel();
            capture.TryFail(new IOException("Test capture ended."));
            try { await operation; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task HostReportsInvalidSessionResultsAsUnknownInsteadOfCrashingOrCertifyingSuccess() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows capability ACLs are required."); return; }
        using var host = new LocalControlServer(() => new CallbackSession(request => new(request.Id, "completed", null!)));
        using var client = await LocalControlClient.ConnectAsync(host.AttachmentPath, Token);
        var result = await client.ExecuteAsync("exec", "probe", cancellationToken: Token);
        Assert.True(result.IsError);
        Assert.Equal("unknown", result.Status);
    }

    private static async Task<TcpClient> ConnectRawAsync(string path) {
        var capability = LocalEndpointCapability.ReadDescriptor(path);
        var peer = new TcpClient();
        try {
            await peer.ConnectAsync(IPAddress.Loopback, capability.Port, Token);
            await capability.AuthenticateAsync(peer.GetStream(), server: false, Token);
            return peer;
        } catch { peer.Dispose(); throw; }
    }

    private sealed class StubbornSession(TaskCompletionSource entered, TaskCompletionSource<ControlResponse> work, Action disposed) : IControlSession {
        public Task<ControlResponse> ExecuteAsync(ControlRequest request, CancellationToken cancellationToken) { entered.TrySetResult(); return work.Task; }
        public void Dispose() => disposed();
    }
    private sealed class CallbackSession(Func<ControlRequest, ControlResponse> execute) : IControlSession {
        public Task<ControlResponse> ExecuteAsync(ControlRequest request, CancellationToken cancellationToken) => Task.FromResult(execute(request));
        public void Dispose() { }
    }
}
