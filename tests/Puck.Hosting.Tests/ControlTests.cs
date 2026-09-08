using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Networking;


namespace Puck.Hosting.Tests;

public sealed class ControlTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("")] [InlineData(" \t")] [InlineData(" #comment")] [InlineData("help\nquit")] [InlineData("help\rquit")] [InlineData("help\0")]
    public void DroppedLinesCannotStrandAReply(string line) => Assert.True(LocalControlServer.Validate(new(1, "exec", line, 1000))?.IsError);

    [Fact]
    public async Task IndependentSessionsRetainConsoleAuthorityAndCaptureCompletion() {
        var held = true;
        var source = new TextCommandSource(new CommandRegistry([new ProbeModule(() => held)]));
        FrameCaptureRequest? armed = null;
        using var session = new ConsoleControlSession(source, path => armed = new(path));
        var hold = session.ExecuteAsync(new(1, "exec", "hold", 1000), Token);
        source.Collect();
        Assert.Equal("completed", (await hold).Status);
        var capture = session.ExecuteAsync(new(2, "capture", null, 1000), Token);
        var humanReply = new TaskCompletionSource<CommandResult>();
        using var human = source.CreateSession(CommandPrincipal.Console, onResult: (_, result) => humanReply.SetResult(result));
        human.Enqueue("probe");
        source.Collect();
        Assert.Equal("operator", (await humanReply.Task).Output);
        Assert.Null(armed);
        held = false;
        source.Collect();
        Assert.NotNull(armed);
        Assert.False(capture.IsCompleted);
        if (OperatingSystem.IsWindows()) {
            using var user = WindowsIdentity.GetCurrent();
            var access = new DirectoryInfo(Path.GetDirectoryName(armed.Path)!).GetAccessControl();
            foreach (FileSystemAccessRule rule in access.GetAccessRules(true, true, typeof(SecurityIdentifier))) { Assert.Equal(user.User, rule.IdentityReference); }
        }
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j5p8AAAAASUVORK5CYII=");
        armed.Write(path => File.WriteAllBytes(path, png));
        Assert.Equal(png, (await capture).Png);
        Assert.False(File.Exists(armed.Path));
    }

    [Fact]
    public async Task CancellationRemovesQueuedWorkAndCleansAcceptedCaptureOnlyAfterCompletion() {
        var source = new TextCommandSource(new CommandRegistry([new ProbeModule(() => false)]));
        FrameCaptureRequest? armed = null;
        using var session = new ConsoleControlSession(source, path => armed = new(path));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var operation = session.ExecuteAsync(new(1, "capture", null, 1000), cancel.Token);
        source.Collect();
        Assert.NotNull(armed);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.True(Directory.Exists(Path.GetDirectoryName(armed.Path)));
        armed.Write(path => File.WriteAllBytes(path, [1, 2, 3]));
        await EventuallyAsync(() => !Directory.Exists(Path.GetDirectoryName(armed.Path)));

        var calls = 0;
        using var queued = new ConsoleControlSession(source, path => { calls++; return new(path); });
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = queued.ExecuteAsync(new(1, "capture", null, 1000), cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        source.Collect();
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RealAttachmentRejectsWrongSecretDuplicateIdsAndReconnectsWithoutStoppingHost() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows capability ACLs are required."); return; }
        var sessions = 0;
        var disposed = 0;
        using var server = new LocalControlServer(() => { Interlocked.Increment(ref sessions); return new EchoSession(() => Interlocked.Increment(ref disposed)); });
        using (var client = await LocalControlClient.ConnectAsync(server.AttachmentPath, Token)) {
            Assert.Equal(0, (await client.ExecuteAsync("exec", "#comment", cancellationToken: Token)).Id);
            Assert.Equal(0, (await client.ExecuteAsync("exec", new string('ç', 8000), cancellationToken: Token)).Id);
            var first = await client.ExecuteAsync("exec", "no-image", cancellationToken: Token);
            Assert.Equal(1, first.Id);
            Assert.Null(first.Png);
            Assert.Equal("a\tç", (await client.ExecuteAsync("exec", "a\tç", cancellationToken: Token)).Output);
            Assert.True((await client.ExecuteAsync("exec", "#comment", cancellationToken: Token)).IsError);
            Assert.Equal("after", (await client.ExecuteAsync("exec", "after", cancellationToken: Token)).Output);
        }
        await EventuallyAsync(() => disposed == 1);
        using var raw = await RawConnectAsync(server.AttachmentPath, wrongSecret: true);
        Assert.Equal(1, sessions);
        using var authenticated = await RawConnectAsync(server.AttachmentPath, wrongSecret: false);
        await WriteFrameAsync(authenticated.GetStream(), "{\"id\":1,\"operation\":\"exec\",\"command\":\"once\",\"timeoutMilliseconds\":1000}"u8.ToArray(), ControlLimits.RequestBytes, Token);
        Assert.NotNull(await ReadFrameAsync(authenticated.GetStream(), ControlLimits.ResponseBytes, Token));
        await WriteFrameAsync(authenticated.GetStream(), "{\"id\":1,\"operation\":\"exec\",\"command\":\"twice\",\"timeoutMilliseconds\":1000}"u8.ToArray(), ControlLimits.RequestBytes, Token);
        Assert.Null(await ReadFrameAsync(authenticated.GetStream(), ControlLimits.ResponseBytes, Token));
        using var again = await LocalControlClient.ConnectAsync(server.AttachmentPath, Token);
        Assert.Equal("still-running", (await again.ExecuteAsync("exec", "still-running", cancellationToken: Token)).Output);
        var path = server.AttachmentPath;
        server.Dispose();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task CapabilityIsUserOnlyAndCannotBeReplacedWhileHostOwnsIt() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows capability ACLs are required."); return; }
        using var server = new LocalControlServer(() => new EchoSession(() => { }));
        var acl = new FileInfo(server.AttachmentPath).GetAccessControl();
        using var user = WindowsIdentity.GetCurrent();
        Assert.Equal(user.User, acl.GetOwner(typeof(SecurityIdentifier)));
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier))) {
            Assert.Equal(user.User, rule.IdentityReference);
        }
        Assert.Throws<IOException>(() => File.Delete(server.AttachmentPath));
        using var client = await LocalControlClient.ConnectAsync(server.AttachmentPath, Token);
        Assert.Equal("ok", (await client.ExecuteAsync("exec", "ok", cancellationToken: Token)).Output);
    }

    [Fact]
    public async Task PipeliningWhileWorkWaitsClosesAndCancelsTheIngress() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows capability ACLs are required."); return; }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new LocalControlServer(() => new WaitingSession(entered, cancelled));
        using var client = await RawConnectAsync(server.AttachmentPath, wrongSecret: false);
        await WriteFrameAsync(client.GetStream(), "{\"id\":1,\"operation\":\"exec\",\"command\":\"wait\",\"timeoutMilliseconds\":10000}"u8.ToArray(), ControlLimits.RequestBytes, Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await WriteFrameAsync(client.GetStream(), "{\"id\":2,\"operation\":\"exec\",\"command\":\"must-not-run\",\"timeoutMilliseconds\":10000}"u8.ToArray(), ControlLimits.RequestBytes, Token);
        Assert.Null(await ReadFrameAsync(client.GetStream(), ControlLimits.ResponseBytes, Token));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Fact]
    public async Task ClientRejectsAHostThatCannotProveTheDescriptorSecret() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows capability ACLs are required."); return; }
        using var real = new LocalControlServer(() => throw new InvalidOperationException("No session should be created."));
        using var original = new FileStream(real.AttachmentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var descriptor = JsonDocument.Parse(original);
        var host = descriptor.RootElement.GetProperty("host").GetString();
        var secret = descriptor.RootElement.GetProperty("secret").GetString();
        using var fake = new TcpListener(IPAddress.Loopback, 0);
        fake.Start();
        var port = ((IPEndPoint)fake.LocalEndpoint).Port;
        var path = Path.Combine(Path.GetTempPath(), $"puck-fake-{Guid.NewGuid():N}.json");
        try {
            using (var file = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, new FileInfo(real.AttachmentPath).GetAccessControl())) {
                file.Write(Encoding.UTF8.GetBytes($$"""{"revision":1,"host":"{{host}}","port":{{port}},"secret":"{{secret}}"}"""));
            }
            var attempt = LocalControlClient.ConnectAsync(path, Token);
            using var peer = await fake.AcceptTcpClientAsync(Token);
            var nonce = new string('A', 64);
            var challenge = Encoding.UTF8.GetBytes($$"""{"revision":1,"host":"{{host}}","nonce":"{{nonce}}","proof":""}""");
            await WriteFrameAsync(peer.GetStream(), challenge, 4096, Token);
            Assert.NotNull(await ReadFrameAsync(peer.GetStream(), 4096, Token));
            var forged = Encoding.UTF8.GetBytes($$"""{"revision":1,"host":"{{host}}","nonce":"{{nonce}}","proof":"{{new string('0',64)}}"}""");
            await WriteFrameAsync(peer.GetStream(), forged, 4096, Token);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => attempt);
        } finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(0)] [InlineData(2)] [InlineData(255)]
    public async Task WrongOperationFrameKindClosesWithoutDispatch(byte kind) {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows capability ACLs are required."); return; }
        var calls = 0;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new LocalControlServer(() => new CountingSession(() => Interlocked.Increment(ref calls), () => closed.TrySetResult()));
        using var peer = await RawConnectAsync(server.AttachmentPath, wrongSecret: false);
        await WireFrame.WriteAsync(peer.GetStream(), kind, "{\"id\":1,\"operation\":\"exec\",\"command\":\"probe\",\"timeoutMilliseconds\":1000}"u8.ToArray(), Token);
        Assert.Null(await ReadFrameAsync(peer.GetStream(), ControlLimits.ResponseBytes, Token));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task BusyClientRefusesImmediatelyAndCancellationInvalidatesTheSession() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows capability ACLs are required."); return; }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new LocalControlServer(() => new WaitingSession(entered, cancelled));
        using var client = await LocalControlClient.ConnectAsync(server.AttachmentPath, Token);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var active = client.ExecuteAsync("exec", "wait", cancellationToken: stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var busy = client.ExecuteAsync("exec", "must-not-run", cancellationToken: Token);
        Assert.True(busy.IsCompletedSuccessfully);
        Assert.True((await busy).IsError);
        Assert.Equal(0, (await busy).Id);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ExecuteAsync("exec", "must-not-reconnect", cancellationToken: Token));
    }

    private sealed class CountingSession(Action called, Action closed) : IControlSession {
        public Task<ControlResponse> ExecuteAsync(ControlRequest request, CancellationToken cancellationToken) {
            called();
            return Task.FromResult(new ControlResponse(request.Id, "completed", "called"));
        }
        public void Dispose() => closed();
    }

    private sealed class WaitingSession(TaskCompletionSource entered, TaskCompletionSource cancelled) : IControlSession {
        public async Task<ControlResponse> ExecuteAsync(ControlRequest request, CancellationToken cancellationToken) {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            finally { cancelled.TrySetResult(); }
            throw new InvalidOperationException("Unreachable.");
        }
        public void Dispose() { }
    }

    private static async Task<TcpClient> RawConnectAsync(string attachment, bool wrongSecret) {
        using var capability = new FileStream(attachment, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var descriptor = JsonDocument.Parse(capability);
        var root = descriptor.RootElement;
        var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, root.GetProperty("port").GetInt32(), Token);
        var stream = socket.GetStream();
        using var challenge = JsonDocument.Parse((await ReadFrameAsync(stream, 4096, Token))!);
        var host = root.GetProperty("host").GetString();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var serverNonce = challenge.RootElement.GetProperty("nonce").GetString();
        var secret = wrongSecret ? new byte[32] : Convert.FromHexString(root.GetProperty("secret").GetString()!);
        var proof = Convert.ToHexString(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes($"puck-control:1:{host}:client:{serverNonce}:{nonce}")));
        var response = Encoding.UTF8.GetBytes($$"""{"revision":1,"host":"{{host}}","nonce":"{{nonce}}","proof":"{{proof}}"}""");
        await WriteFrameAsync(stream, response, 4096, Token);
        var accepted = await ReadFrameAsync(stream, 4096, Token);
        if (wrongSecret) { Assert.Null(accepted); } else { Assert.NotNull(accepted); }
        return socket;
    }

    private static Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, int maximumBytes, CancellationToken token) =>
        WireFrame.WriteAsync(stream, maximumBytes == 4096 ? (byte)0 : (byte)1, payload, token);

    private static async Task<byte[]?> ReadFrameAsync(Stream stream, int maximumBytes, CancellationToken token) {
        var frame = await WireFrame.ReadAsync(stream, maximumBytes + WireFrame.PrefixBytes, token);
        if (frame.Failure.Refusal == WireRefusal.ConnectionClosed) { return null; }
        Assert.False(frame.Failure.IsRefusal, frame.Failure.ToString());
        Assert.Equal(maximumBytes == 4096 ? (byte)0 : (byte)2, frame.Kind);
        return frame.Body.ToArray();
    }

    private static async Task EventuallyAsync(Func<bool> ready) {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (!ready()) { await Task.Delay(10, deadline.Token); }
    }
    private sealed class EchoSession(Action disposed) : IControlSession {
        public Task<ControlResponse> ExecuteAsync(ControlRequest request, CancellationToken cancellationToken) => Task.FromResult(new ControlResponse(request.Id, "completed", request.Command ?? "capture"));
        public void Dispose() => disposed();
    }
    private sealed class ProbeModule(Func<bool> held) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs("probe", "Check principal.", (context, _) => new(context.Principal == CommandPrincipal.Console ? "operator" : "wrong"), bindability: CommandBindability.Unbindable);
            yield return CommandDefinition.WithWireArgs("hold", "Hold this session.", (context, _) => { context.TextSession!.HoldWhile(held); return new("holding"); }, bindability: CommandBindability.Unbindable);
        }
    }
}
