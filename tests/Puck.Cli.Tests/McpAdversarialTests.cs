using System.Text;
using Puck.Hosting;
using Puck.Mcp;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class McpAdversarialTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AUnicodeScalarMaySpanInputReads() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        using var host = new LocalControlServer(createSession: () => new EchoSession());
        using var input = new FragmentStream(data: Encoding.UTF8.GetBytes(s: "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/unused\",\"params\":{\"text\":\"ç🌍\"}}\n"));
        using var output = new MemoryStream();

        await OperatorMcpServer.RunAsync(
            host.AttachmentPath,
            input,
            output,
            Token
        );
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task BlockedStdoutHasBothADeadlineAndABoundedReplyQueue(bool flood) {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        const string Parameters = """
            "method":"server/discover","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"blocked-output","version":"1"},"io.modelcontextprotocol/clientCapabilities":{}}}
            """;
        var requests = string.Concat(values: Enumerable.Range(
            count: (flood
            ? 13
            : 1),
            start: 1
        ).Select(selector: id => $"{{\"jsonrpc\":\"2.0\",\"id\":{id},{Parameters}}}\n"));
        using var host = new LocalControlServer(createSession: () => new EchoSession());
        using var input = new PrefixThenHoldStream(data: Encoding.UTF8.GetBytes(s: requests));
        using var output = new BlockedWriteStream();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token: Token);
        var run = OperatorMcpServer.RunAsync(
            host.AttachmentPath,
            input,
            output,
            stop.Token
        );

        try {
            // Saturation may close the queue before the first queued write reaches stdout.
            // Only the single-reply case must enter the blocked writer to prove its deadline.
            if (!flood) { await output.Entered.Task.WaitAsync(
                TimeSpan.FromSeconds(seconds: 3),
                Token
            ); }
            var error = await Record.ExceptionAsync(testCode: () => run.WaitAsync(
                TimeSpan.FromSeconds(seconds: (flood
                ? 3
                : 8)),
                Token
            ));

            Assert.IsType<IOException>(@object: error);
            Assert.True(condition: input.Closed);
            Assert.True(condition: output.Closed);
        } finally {
            stop.Cancel(); input.Dispose(); output.Dispose();
            try { await run; } catch (IOException) { }
        }
    }
    [Fact]
    public async Task CancellationClosesUnderlyingStdinEvenWhenReadIgnoresItsToken() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        using var host = new LocalControlServer(createSession: () => new EchoSession());
        using var input = new CloseOnlyReadStream();
        using var output = new MemoryStream();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token: Token);
        var run = OperatorMcpServer.RunAsync(
            host.AttachmentPath,
            input,
            output,
            stop.Token
        );

        await input.Entered.Task.WaitAsync(
            TimeSpan.FromSeconds(seconds: 3),
            Token
        );
        try {
            stop.Cancel();
            await run.WaitAsync(
                TimeSpan.FromSeconds(seconds: 3),
                Token
            );
            Assert.True(condition: input.Closed);
        } finally { input.Dispose(); await run; }
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task InvalidStdioIsReportedAsFailureRatherThanCleanEof(bool invalidUtf8) {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        using var host = new LocalControlServer(createSession: () => new EchoSession());
        using var input = new MemoryStream(buffer: (invalidUtf8
            ? new byte[] { 0xc3, 0x28, 10 }
            : Encoding.UTF8.GetBytes(s: new string(
                c: ' ',
                count: 65537
            ))));
        using var output = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(testCode: () => OperatorMcpServer.RunAsync(
            host.AttachmentPath,
            input,
            output,
            Token
        ));
    }
    [Fact]
    public async Task MalformedMessagesCannotAccumulateWithoutAnInputBudget() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        using var host = new LocalControlServer(createSession: () => new EchoSession());
        using var input = new MemoryStream(buffer: Encoding.UTF8.GetBytes(s: string.Concat(values: Enumerable.Repeat(
            count: 129,
            element: "null\n"
        ))));
        using var output = new MemoryStream();
        var error = await Assert.ThrowsAsync<InvalidDataException>(testCode: () => OperatorMcpServer.RunAsync(
            host.AttachmentPath,
            input,
            output,
            Token
        ));

        Assert.Contains(
            "128",
            error.Message
        );
    }
    [Fact]
    public async Task StdinReadFailureIsNotCleanEof() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        using var host = new LocalControlServer(createSession: () => new EchoSession());
        using var input = new BrokenReadStream();
        using var output = new MemoryStream();
        var error = await Assert.ThrowsAsync<IOException>(testCode: () => OperatorMcpServer.RunAsync(
            host.AttachmentPath,
            input,
            output,
            Token
        ));

        Assert.Equal(
            "broken input",
            error.Message
        );
    }

    private sealed class BrokenReadStream : MemoryStream {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException<int>(new IOException(message: "broken input"));
    }
    private sealed class FragmentStream(byte[] data) : MemoryStream(data) {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(
            buffer[..Math.Min(
                1,
                buffer.Length
            )],
            cancellationToken
        );
    }
    private sealed class PrefixThenHoldStream(byte[] data) : MemoryStream(data) {
        private readonly TaskCompletionSource<int> m_closed = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool Closed { get; private set; }

        protected override void Dispose(bool disposing) { Closed = true; m_closed.TrySetResult(result: 0); base.Dispose(disposing: disposing); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
            var read = await base.ReadAsync(
                buffer,
                cancellationToken
            );

            return ((read == 0)
                ? await m_closed.Task
                : read
            );
        }
    }
    private sealed class BlockedWriteStream : MemoryStream {
        private readonly TaskCompletionSource m_closed = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Entered { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool Closed { get; private set; }

        protected override void Dispose(bool disposing) { Closed = true; m_closed.TrySetResult(); base.Dispose(disposing: disposing); }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { Entered.TrySetResult(); return new(m_closed.Task); }
    }
    private sealed class CloseOnlyReadStream : MemoryStream {
        private readonly TaskCompletionSource<int> m_closed = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Entered { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool Closed { get; private set; }

        protected override void Dispose(bool disposing) { Closed = true; m_closed.TrySetResult(result: 0); base.Dispose(disposing: disposing); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
            Entered.TrySetResult();
            return new(m_closed.Task);
        }
    }
    private sealed class EchoSession : IControlSession {
        public void Dispose() { }
        public Task<ControlResponse> ExecuteAsync(ControlRequest request, CancellationToken cancellationToken) => Task.FromResult(result: new ControlResponse(
            request.Id,
            "completed",
            "echo"
        ));
    }
}
