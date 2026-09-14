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

    private static async Task EventuallyAsync(Func<bool> ready) {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token: Token);

        deadline.CancelAfter(delay: TimeSpan.FromSeconds(seconds: 5));
        while (!ready()) { await Task.Delay(
            10,
            deadline.Token
        ); }
    }
    private static async Task<TcpClient> RawConnectAsync(string attachment, bool wrongSecret) {
        using var capability = new FileStream(
            access: FileAccess.Read,
            mode: FileMode.Open,
            path: attachment,
            share: FileShare.ReadWrite
        );
        using var descriptor = JsonDocument.Parse(capability);
        var root = descriptor.RootElement;
        var socket = new TcpClient();

        await socket.ConnectAsync(
            IPAddress.Loopback,
            root.GetProperty(propertyName: "port").GetInt32(),
            Token
        );
        var stream = socket.GetStream();
        using var challenge = JsonDocument.Parse((await ReadFrameAsync(
            stream,
            4096,
            Token
        ))!);
        var host = root.GetProperty(propertyName: "host").GetString();
        var nonce = Convert.ToHexString(inArray: RandomNumberGenerator.GetBytes(count: 32));
        var serverNonce = challenge.RootElement.GetProperty(propertyName: "nonce").GetString();
        var secret = (wrongSecret
            ? new byte[32]
            : Convert.FromHexString(s: root.GetProperty(propertyName: "secret").GetString()!)
        );
        var proof = Convert.ToHexString(inArray: HMACSHA256.HashData(
            key: secret,
            source: Encoding.UTF8.GetBytes(s: $"puck-control:1:{host}:client:{serverNonce}:{nonce}")
        ));
        var response = Encoding.UTF8.GetBytes(s: $$"""{"revision":1,"host":"{{host}}","nonce":"{{nonce}}","proof":"{{proof}}"}""");

        await WriteFrameAsync(
            stream,
            response,
            4096,
            Token
        );
        var accepted = await ReadFrameAsync(
            stream,
            4096,
            Token
        );

        if (wrongSecret) { Assert.Null(@object: accepted); } else { Assert.NotNull(@object: accepted); }
        return socket;
    }
    private static async Task<byte[]?> ReadFrameAsync(Stream stream, int maximumBytes, CancellationToken token) {
        var frame = await WireFrame.ReadAsync(
            ct: token,
            maxFrameBytes: (maximumBytes + WireFrame.PrefixBytes),
            stream: stream
        );

        if (frame.Failure.Refusal == WireRefusal.ConnectionClosed) { return null; }
        Assert.False(
            condition: frame.Failure.IsRefusal,
            userMessage: frame.Failure.ToString()
        );
        Assert.Equal(
            ((maximumBytes == 4096)
            ? (byte)0
            : (byte)2),
            frame.Kind
        );
        return frame.Body.ToArray();
    }
    private static Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, int maximumBytes, CancellationToken token) =>
        WireFrame.WriteAsync(
            body: payload,
            ct: token,
            kind: ((maximumBytes == 4096)
            ? (byte)0
            : (byte)1),
            stream: stream
        );

    [Fact]
    public async Task BusyClientRefusesImmediatelyAndCancellationInvalidatesTheSession() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        var entered = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new LocalControlServer(createSession: () => new WaitingSession(
            cancelled: cancelled,
            entered: entered
        ));
        using var client = await LocalControlClient.ConnectAsync(
            attachmentPath: server.AttachmentPath,
            cancellationToken: Token
        );
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token: Token);
        var active = client.ExecuteAsync(
            "exec",
            "wait",
            cancellationToken: stop.Token
        );

        await entered.Task.WaitAsync(
            TimeSpan.FromSeconds(seconds: 5),
            Token
        );
        var busy = client.ExecuteAsync(
            "exec",
            "must-not-run",
            cancellationToken: Token
        );

        Assert.True(condition: busy.IsCompletedSuccessfully);
        Assert.True(condition: (await busy).IsError);
        Assert.Equal(
            0,
            (await busy).Id
        );
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => active);
        await cancelled.Task.WaitAsync(
            TimeSpan.FromSeconds(seconds: 5),
            Token
        );
        await Assert.ThrowsAsync<ObjectDisposedException>(testCode: () => client.ExecuteAsync(
            "exec",
            "must-not-reconnect",
            cancellationToken: Token
        ));
    }
    [Fact]
    public async Task CancellationRemovesQueuedWorkAndCleansAcceptedCaptureOnlyAfterCompletion() {
        var source = new TextCommandSource(new CommandRegistry([new ProbeModule(held: () => false)]));
        FrameCaptureRequest? armed = null;
        using var session = new ConsoleControlSession(
            source,
            path => armed = new(path: path)
        );
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token: Token);
        var operation = session.ExecuteAsync(
            new(
                Command: null,
                Id: 1,
                Operation: "capture",
                TimeoutMilliseconds: 1000
            ),
            cancel.Token
        );

        source.Collect();
        Assert.NotNull(@object: armed);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => operation);
        Assert.True(condition: Directory.Exists(path: Path.GetDirectoryName(path: armed.Path)));
        armed.Write(writer: path => File.WriteAllBytes(
            bytes: [1, 2, 3],
            path: path
        ));
        await EventuallyAsync(ready: () => !Directory.Exists(path: Path.GetDirectoryName(path: armed.Path)));

        var calls = 0;
        using var queued = new ConsoleControlSession(
            source,
            path => { calls++; return new(path: path); }
        );
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token: Token);
        var pending = queued.ExecuteAsync(
            new(
                Command: null,
                Id: 1,
                Operation: "capture",
                TimeoutMilliseconds: 1000
            ),
            cancelled.Token
        );

        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => pending);
        source.Collect();
        Assert.Equal(
            actual: calls,
            expected: 0
        );
    }
    [Fact]
    public async Task CapabilityIsUserOnlyAndCannotBeReplacedWhileHostOwnsIt() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        using var server = new LocalControlServer(createSession: () => new EchoSession(disposed: () => { }));
        var acl = new FileInfo(fileName: server.AttachmentPath).GetAccessControl();
        using var user = WindowsIdentity.GetCurrent();

        Assert.Equal(
            user.User,
            acl.GetOwner(targetType: typeof(SecurityIdentifier))
        );
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            targetType: typeof(SecurityIdentifier)
        )) {
            Assert.Equal(
                user.User,
                rule.IdentityReference
            );
        }
        Assert.Throws<IOException>(testCode: () => File.Delete(path: server.AttachmentPath));
        using var client = await LocalControlClient.ConnectAsync(
            attachmentPath: server.AttachmentPath,
            cancellationToken: Token
        );

        Assert.Equal(
            "ok",
            (await client.ExecuteAsync(
                "exec",
                "ok",
                cancellationToken: Token
            )).Output
        );
    }
    [Fact]
    public async Task ClientRejectsAHostThatCannotProveTheDescriptorSecret() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        using var real = new LocalControlServer(createSession: () => throw new InvalidOperationException(message: "No session should be created."));
        using var original = new FileStream(
            real.AttachmentPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite
        );
        using var descriptor = JsonDocument.Parse(original);
        var host = descriptor.RootElement.GetProperty(propertyName: "host").GetString();
        var secret = descriptor.RootElement.GetProperty(propertyName: "secret").GetString();
        using var fake = new TcpListener(
            localaddr: IPAddress.Loopback,
            port: 0
        );

        fake.Start();
        var port = ((IPEndPoint)fake.LocalEndpoint).Port;
        var path = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-fake-{Guid.NewGuid():N}.json"
        );

        try {
            using (var file = new FileInfo(fileName: path).Create(
                FileMode.CreateNew,
                FileSystemRights.Write,
                FileShare.None,
                4096,
                FileOptions.None,
                new FileInfo(fileName: real.AttachmentPath).GetAccessControl()
            )) {
                file.Write(buffer: Encoding.UTF8.GetBytes(s: $$"""{"revision":1,"host":"{{host}}","port":{{port}},"secret":"{{secret}}"}"""));
            }
            var attempt = LocalControlClient.ConnectAsync(
                attachmentPath: path,
                cancellationToken: Token
            );
            using var peer = await fake.AcceptTcpClientAsync(cancellationToken: Token);
            var nonce = new string(
                c: 'A',
                count: 64
            );
            var challenge = Encoding.UTF8.GetBytes(s: $$"""{"revision":1,"host":"{{host}}","nonce":"{{nonce}}","proof":""}""");

            await WriteFrameAsync(
                peer.GetStream(),
                challenge,
                4096,
                Token
            );
            Assert.NotNull(@object: await ReadFrameAsync(
                peer.GetStream(),
                4096,
                Token
            ));
            var forged = Encoding.UTF8.GetBytes(s: $$"""{"revision":1,"host":"{{host}}","nonce":"{{nonce}}","proof":"{{new string(
                c: '0',
                count: 64
            )}}"}""");

            await WriteFrameAsync(
                peer.GetStream(),
                forged,
                4096,
                Token
            );
            await Assert.ThrowsAsync<UnauthorizedAccessException>(testCode: () => attempt);
        } finally { File.Delete(path: path); }
    }
    [InlineData("")]
    [InlineData(" \t")]
    [InlineData(" #comment")]
    [InlineData("help\nquit")]
    [InlineData("help\rquit")]
    [InlineData("help\0")]
    [Theory]
    public void DroppedLinesCannotStrandAReply(string line) => Assert.True(condition: LocalControlServer.Validate(request: new(
        Command: line,
        Id: 1,
        Operation: "exec",
        TimeoutMilliseconds: 1000
    ))?.IsError);
    [Fact]
    public async Task IndependentSessionsRetainConsoleAuthorityAndCaptureCompletion() {
        var held = true;
        var source = new TextCommandSource(new CommandRegistry([new ProbeModule(held: () => held)]));
        FrameCaptureRequest? armed = null;
        using var session = new ConsoleControlSession(
            source,
            path => armed = new(path: path)
        );
        var hold = session.ExecuteAsync(
            new(
                Command: "hold",
                Id: 1,
                Operation: "exec",
                TimeoutMilliseconds: 1000
            ),
            Token
        );

        source.Collect();
        Assert.Equal(
            "completed",
            (await hold).Status
        );
        var capture = session.ExecuteAsync(
            new(
                Command: null,
                Id: 2,
                Operation: "capture",
                TimeoutMilliseconds: 1000
            ),
            Token
        );
        var humanReply = new TaskCompletionSource<CommandResult>();
        using var human = source.CreateSession(
            CommandPrincipal.Console,
            onResult: (_, result) => humanReply.SetResult(result: result)
        );

        human.Enqueue(line: "probe");
        source.Collect();
        Assert.Equal(
            "operator",
            (await humanReply.Task).Output
        );
        Assert.Null(@object: armed);
        held = false;
        source.Collect();
        Assert.NotNull(@object: armed);
        Assert.False(condition: capture.IsCompleted);
        if (OperatingSystem.IsWindows()) {
            using var user = WindowsIdentity.GetCurrent();
            var access = new DirectoryInfo(path: Path.GetDirectoryName(path: armed.Path)!).GetAccessControl();

            foreach (FileSystemAccessRule rule in access.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                targetType: typeof(SecurityIdentifier)
            )) { Assert.Equal(
                user.User,
                rule.IdentityReference
            ); }
        }
        var png = Convert.FromBase64String(s: "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j5p8AAAAASUVORK5CYII=");

        armed.Write(writer: path => File.WriteAllBytes(
            bytes: png,
            path: path
        ));
        Assert.Equal(
            png,
            (await capture).Png
        );
        Assert.False(condition: File.Exists(path: armed.Path));
    }
    [Fact]
    public async Task PipeliningWhileWorkWaitsClosesAndCancelsTheIngress() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        var entered = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new LocalControlServer(createSession: () => new WaitingSession(
            cancelled: cancelled,
            entered: entered
        ));
        using var client = await RawConnectAsync(
            server.AttachmentPath,
            wrongSecret: false
        );

        await WriteFrameAsync(
            client.GetStream(),
            "{\"id\":1,\"operation\":\"exec\",\"command\":\"wait\",\"timeoutMilliseconds\":10000}"u8.ToArray(),
            ControlLimits.RequestBytes,
            Token
        );
        await entered.Task.WaitAsync(
            TimeSpan.FromSeconds(seconds: 5),
            Token
        );
        await WriteFrameAsync(
            client.GetStream(),
            "{\"id\":2,\"operation\":\"exec\",\"command\":\"must-not-run\",\"timeoutMilliseconds\":10000}"u8.ToArray(),
            ControlLimits.RequestBytes,
            Token
        );
        Assert.Null(@object: await ReadFrameAsync(
            client.GetStream(),
            ControlLimits.ResponseBytes,
            Token
        ));
        await cancelled.Task.WaitAsync(
            TimeSpan.FromSeconds(seconds: 5),
            Token
        );
    }
    [Fact]
    public async Task RealAttachmentRejectsWrongSecretDuplicateIdsAndReconnectsWithoutStoppingHost() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        var sessions = 0;
        var disposed = 0;
        using var server = new LocalControlServer(createSession: () => { Interlocked.Increment(location: ref sessions); return new EchoSession(disposed: () => Interlocked.Increment(location: ref disposed)); });

        using (var client = await LocalControlClient.ConnectAsync(
            attachmentPath: server.AttachmentPath,
            cancellationToken: Token
        )) {
            Assert.Equal(
                0,
                (await client.ExecuteAsync(
                    "exec",
                    "#comment",
                    cancellationToken: Token
                )).Id
            );
            Assert.Equal(
                0,
                (await client.ExecuteAsync(
                    "exec",
                    new string(
                        c: 'ç',
                        count: 8000
                    ),
                    cancellationToken: Token
                )).Id
            );
            var first = await client.ExecuteAsync(
                "exec",
                "no-image",
                cancellationToken: Token
            );

            Assert.Equal(
                1,
                first.Id
            );
            Assert.Null(@object: first.Png);
            Assert.Equal(
                "a\tç",
                (await client.ExecuteAsync(
                    "exec",
                    "a\tç",
                    cancellationToken: Token
                )).Output
            );
            Assert.True(condition: (await client.ExecuteAsync(
                "exec",
                "#comment",
                cancellationToken: Token
            )).IsError);
            Assert.Equal(
                "after",
                (await client.ExecuteAsync(
                    "exec",
                    "after",
                    cancellationToken: Token
                )).Output
            );
        }
        await EventuallyAsync(ready: () => (disposed == 1));
        using var raw = await RawConnectAsync(
            server.AttachmentPath,
            wrongSecret: true
        );

        Assert.Equal(
            actual: sessions,
            expected: 1
        );
        using var authenticated = await RawConnectAsync(
            server.AttachmentPath,
            wrongSecret: false
        );

        await WriteFrameAsync(
            authenticated.GetStream(),
            "{\"id\":1,\"operation\":\"exec\",\"command\":\"once\",\"timeoutMilliseconds\":1000}"u8.ToArray(),
            ControlLimits.RequestBytes,
            Token
        );
        Assert.NotNull(@object: await ReadFrameAsync(
            authenticated.GetStream(),
            ControlLimits.ResponseBytes,
            Token
        ));
        await WriteFrameAsync(
            authenticated.GetStream(),
            "{\"id\":1,\"operation\":\"exec\",\"command\":\"twice\",\"timeoutMilliseconds\":1000}"u8.ToArray(),
            ControlLimits.RequestBytes,
            Token
        );
        Assert.Null(@object: await ReadFrameAsync(
            authenticated.GetStream(),
            ControlLimits.ResponseBytes,
            Token
        ));
        using var again = await LocalControlClient.ConnectAsync(
            attachmentPath: server.AttachmentPath,
            cancellationToken: Token
        );

        Assert.Equal(
            "still-running",
            (await again.ExecuteAsync(
                "exec",
                "still-running",
                cancellationToken: Token
            )).Output
        );
        var path = server.AttachmentPath;

        server.Dispose();
        Assert.False(condition: File.Exists(path: path));
    }
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(255)]
    [Theory]
    public async Task WrongOperationFrameKindClosesWithoutDispatch(byte kind) {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        var calls = 0;
        var closed = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new LocalControlServer(createSession: () => new CountingSession(
            called: () => Interlocked.Increment(location: ref calls),
            closed: () => closed.TrySetResult()
        ));
        using var peer = await RawConnectAsync(
            server.AttachmentPath,
            wrongSecret: false
        );

        await WireFrame.WriteAsync(
            peer.GetStream(),
            kind,
            "{\"id\":1,\"operation\":\"exec\",\"command\":\"probe\",\"timeoutMilliseconds\":1000}"u8.ToArray(),
            Token
        );
        Assert.Null(@object: await ReadFrameAsync(
            peer.GetStream(),
            ControlLimits.ResponseBytes,
            Token
        ));
        await closed.Task.WaitAsync(
            TimeSpan.FromSeconds(seconds: 5),
            Token
        );
        Assert.Equal(
            actual: calls,
            expected: 0
        );
    }

    private sealed class CountingSession(Action called, Action closed) : IControlSession {
        public void Dispose() => closed();
        public Task<ControlResponse> ExecuteAsync(ControlRequest request, CancellationToken cancellationToken) {
            called();
            return Task.FromResult(result: new ControlResponse(
                request.Id,
                "completed",
                "called"
            ));
        }
    }
    private sealed class WaitingSession(TaskCompletionSource entered, TaskCompletionSource cancelled) : IControlSession {
        public void Dispose() { }
        public async Task<ControlResponse> ExecuteAsync(ControlRequest request, CancellationToken cancellationToken) {
            entered.SetResult();
            try { await Task.Delay(
                cancellationToken: cancellationToken,
                millisecondsDelay: Timeout.Infinite
            ); } finally { cancelled.TrySetResult(); }
            throw new InvalidOperationException(message: "Unreachable.");
        }
    }
    private sealed class EchoSession(Action disposed) : IControlSession {
        public void Dispose() => disposed();
        public Task<ControlResponse> ExecuteAsync(ControlRequest request, CancellationToken cancellationToken) => Task.FromResult(result: new ControlResponse(
            request.Id,
            "completed",
            (request.Command ?? "capture")
        ));
    }
    private sealed class ProbeModule(Func<bool> held) : ICommandModule {
        public IEnumerable<CommandDefinition> GetCommands() {
            yield return CommandDefinition.WithWireArgs(
                "probe",
                "Check principal.",
                (context, _) => new(((context.Principal == CommandPrincipal.Console)
                ? "operator"
                : "wrong")),
                bindability: CommandBindability.Unbindable
            );
            yield return CommandDefinition.WithWireArgs(
                "hold",
                "Hold this session.",
                (context, _) => { context.TextSession!.HoldWhile(hold: held); return new("holding"); },
                bindability: CommandBindability.Unbindable
            );
        }
    }
}
