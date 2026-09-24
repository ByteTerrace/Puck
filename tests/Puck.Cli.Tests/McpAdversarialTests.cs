using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Puck.Hosting;
using Puck.Mcp;
using Puck.Networking;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

// The Operator adapter in process, over held stdio and a real control host. The adapter and the host each run every
// deadline on their own VirtualClock, as the two processes do in production, so a deadline fires only when a test
// advances that clock and each step waits only for the event it asserts on.
public sealed class McpAdversarialTests {
    // Bounds a wait that the test expects to end without any timer; it measures nothing.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(seconds: 30);

    private const string Meta = """
        "_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"adapter-law","version":"1"},"io.modelcontextprotocol/clientCapabilities":{}}
        """;
    private const int StallMilliseconds = 100;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Law: stdin ends the adapter cleanly only at a well-formed EOF. Every input fault fails the adapter with its cause
    // rather than reading as EOF, whatever the read sizes; a UTF-8 scalar may span reads.
    [InlineData("scalar-spans-reads")]
    [InlineData("invalid-utf8")]
    [InlineData("line-over-64-KiB")]
    [InlineData("malformed-backlog")]
    [InlineData("read-failure")]
    [Theory]
    public async Task StdinEndsCleanlyOnlyAtAWellFormedEof(string input) {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        var (stream, failure, message) = input switch {
            "scalar-spans-reads" => (((Stream)Fragments(text: "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/unused\",\"params\":{\"text\":\"ç🌍\"}}\n")), ((Type?)null), ((string?)null)),
            "invalid-utf8" => (new FragmentStream(data: [0xc3, 0x28, 10]), typeof(InvalidDataException), "valid UTF-8"),
            "line-over-64-KiB" => (Fragments(text: new string(c: ' ', count: 65537)), typeof(InvalidDataException), "64 KiB"),
            "malformed-backlog" => (Fragments(text: string.Concat(values: Enumerable.Repeat(count: 129, element: "null\n"))), typeof(InvalidDataException), "128"),
            _ => (((Stream)new BrokenReadStream()), typeof(IOException), "broken input"),
        };
        using var host = new LocalControlServer(
            clock: new VirtualClock(),
            createSession: () => new ScriptedSession(entered: null)
        );
        using var output = new MemoryStream();

        using (stream) {
            var error = await Record.ExceptionAsync(testCode: () => OperatorMcpServer.RunAsync(
                host.AttachmentPath,
                stream,
                output,
                Token,
                new VirtualClock()
            ).WaitAsync(
                HangGuard,
                Token
            ));

            if (failure is null) {
                Assert.Null(@object: error);
            } else {
                Assert.IsType(
                    expectedType: failure,
                    @object: error
                );
                Assert.Contains(
                    message!,
                    error!.Message
                );
            }
        }
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
        using var host = new LocalControlServer(
            clock: new VirtualClock(),
            createSession: () => new ScriptedSession(entered: null)
        );
        using var input = new PrefixThenHoldStream(data: Encoding.UTF8.GetBytes(s: requests));
        using var output = new BlockedWriteStream();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token: Token);
        var clock = new VirtualClock();
        var run = OperatorMcpServer.RunAsync(
            host.AttachmentPath,
            input,
            output,
            stop.Token,
            clock
        );

        try {
            // Saturation closes the queue on its own, possibly before the first queued write reaches stdout, and
            // no deadline expires. A single reply enters the blocked writer and stays there until its write
            // deadline expires on the adapter's clock.
            if (!flood) {
                await output.Entered.Task.WaitAsync(
                    HangGuard,
                    Token
                );
                Assert.False(condition: run.IsCompleted);
                await clock.WhenArmedAsync(
                    count: 1,
                    ct: Token,
                    dueTime: TimeSpan.FromSeconds(seconds: 5)
                );
                clock.Advance(by: TimeSpan.FromSeconds(seconds: 5));
            }
            var error = await Record.ExceptionAsync(testCode: () => run.WaitAsync(
                HangGuard,
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
        using var host = new LocalControlServer(
            clock: new VirtualClock(),
            createSession: () => new ScriptedSession(entered: null)
        );
        using var input = new CloseOnlyReadStream();
        using var output = new MemoryStream();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token: Token);
        var run = OperatorMcpServer.RunAsync(
            host.AttachmentPath,
            input,
            output,
            stop.Token,
            new VirtualClock()
        );

        await input.Entered.Task.WaitAsync(
            HangGuard,
            Token
        );
        try {
            stop.Cancel();
            await run.WaitAsync(
                HangGuard,
                Token
            );
            Assert.True(condition: input.Closed);
        } finally { input.Dispose(); await run; }
    }
    // Law: invalid parameters are refused without touching the attachment, and a closed attachment is reported, never
    // replayed, and never ends the adapter. A stalled call is closed by whichever deadline reaches it, the adapter's or
    // the World's, or by the client cancelling it; the next call attaches anew. With no World to attach, calls are
    // refused before anything is dispatched while discovery still answers.
    [InlineData("adapter-deadline")]
    [InlineData("host-deadline")]
    [InlineData("client-cancel")]
    [InlineData("no-world")]
    [Theory]
    public async Task AClosedAttachmentIsReportedAndTheNextCallAttachesAnew(string closure) {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        var stall = TimeSpan.FromMilliseconds(value: StallMilliseconds);
        var entered = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        await using var adapter = new InProcessAdapter(createSession: () => new ScriptedSession(entered: entered));

        if (closure == "no-world") { adapter.Host.Dispose(); }
        using (var invalid = await adapter.RequestAsync(
            id: 1,
            parameters: """{"name":"puck_exec","arguments":{"command":"\ud800"}}"""
        )) {
            Assert.Equal(
                -32602,
                invalid.RootElement.GetProperty(propertyName: "error").GetProperty(propertyName: "code").GetInt32()
            );
        }
        if (closure == "no-world") {
            var result = await adapter.ExecAsync(
                command: "alive",
                id: 3
            );

            Assert.Equal(
                "refused",
                result.GetProperty(propertyName: "status").GetString()
            );
            Assert.Contains(
                "No World is attached",
                result.GetProperty(propertyName: "output").GetString()
            );
            using var tools = await adapter.RequestAsync(
                id: 4,
                method: "tools/list",
                parameters: "{}"
            );

            Assert.Equal(
                3,
                tools.RootElement.GetProperty(propertyName: "result").GetProperty(propertyName: "tools").GetArrayLength()
            );
        } else {
            Assert.Equal(
                "completed",
                Status(result: await adapter.ExecAsync(
                    command: "alive",
                    id: 2
                ))
            );
            await adapter.StallAsync(
                entered: entered,
                id: 3
            );
            if (closure == "client-cancel") {
                // A cancelled request is answered by nothing; the next reply belongs to the next request.
                adapter.Send(line: InProcessAdapter.Request(
                    id: null,
                    method: "notifications/cancelled",
                    parameters: """{"requestId":3}"""
                ));
            } else {
                var deadlines = ((closure == "adapter-deadline")
                    ? adapter.AdapterClock
                    : adapter.HostClock);

                await deadlines.WhenArmedAsync(
                    count: 1,
                    ct: Token,
                    dueTime: stall
                );
                deadlines.Advance(by: stall);
                Assert.Equal(
                    "unknown",
                    Status(result: await adapter.ResultAsync(id: 3))
                );
            }
            Assert.Equal(
                "completed",
                Status(result: await adapter.ExecAsync(
                    command: "alive",
                    id: 4
                ))
            );
        }
        Assert.Equal(
            actual: adapter.Attachments,
            expected: ((closure == "no-world")
                ? 0
                : 2)
        );
        Assert.False(condition: adapter.Run.IsCompleted);
        await adapter.StopAsync();
        Assert.True(condition: adapter.Input.Closed);
    }
    // Law: an adapter following a directory attaches to the newest World there that answers, skipping a capability
    // file whose World is gone, and after its attachment closes follows to whichever World now answers, with no
    // adapter restart.
    [Fact]
    public async Task FollowingADirectoryAttachesTheNewestWorldThatAnswers() {
        if (!OperatingSystem.IsWindows()) { Assert.Skip(reason: "Windows capability ACLs are required."); return; }
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-follow-").FullName;
        var entered = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        try {
            using var older = new LocalControlServer(
                clock: new VirtualClock(),
                createSession: () => new ScriptedSession(entered: null, name: "older"),
                directory: directory
            );
            await using var adapter = new InProcessAdapter(
                createSession: () => new ScriptedSession(entered: entered, name: "newer"),
                directory: directory
            );
            var stale = Path.Combine(
                path1: directory,
                path2: "puck-control-stale.json"
            );
            var closed = new TcpListener(
                localaddr: IPAddress.Loopback,
                port: 0
            );

            closed.Start();
            var port = ((IPEndPoint)closed.LocalEndpoint).Port;

            closed.Stop();
            new LocalEndpointCapability(port: port).WriteDescriptor(path: stale).Dispose();
            File.SetLastWriteTimeUtc(
                lastWriteTimeUtc: DateTime.UtcNow.AddMinutes(value: -2),
                path: older.AttachmentPath
            );
            File.SetLastWriteTimeUtc(
                lastWriteTimeUtc: DateTime.UtcNow.AddMinutes(value: -1),
                path: adapter.Host.AttachmentPath
            );
            File.SetLastWriteTimeUtc(
                lastWriteTimeUtc: DateTime.UtcNow,
                path: stale
            );
            Assert.Equal(
                "newer",
                Output(result: await adapter.ExecAsync(
                    command: "who",
                    id: 1
                ))
            );
            await adapter.StallAsync(
                entered: entered,
                id: 2
            );
            await adapter.AdapterClock.WhenArmedAsync(
                count: 1,
                ct: Token,
                dueTime: TimeSpan.FromMilliseconds(value: StallMilliseconds)
            );
            adapter.AdapterClock.Advance(by: TimeSpan.FromMilliseconds(value: StallMilliseconds));
            Assert.Equal(
                "unknown",
                Status(result: await adapter.ResultAsync(id: 2))
            );
            adapter.Host.Dispose();
            Assert.Equal(
                "older",
                Output(result: await adapter.ExecAsync(
                    command: "who",
                    id: 3
                ))
            );
            Assert.Equal(
                actual: adapter.Attachments,
                expected: 1
            );
        } finally { Directory.Delete(path: directory, recursive: true); }
    }

    private static FragmentStream Fragments(string text) => new(data: Encoding.UTF8.GetBytes(s: text));
    private static string? Output(JsonElement result) => result.GetProperty(propertyName: "output").GetString();
    private static string? Status(JsonElement result) => result.GetProperty(propertyName: "status").GetString();

    // One adapter and the host it attaches to: pinned to that host's capability file, or following the newest World
    // in the directory the host publishes into. Requests are sent serially and each reply is read before the next
    // request, so a reply line always belongs to the request that preceded it.
    private sealed class InProcessAdapter : IAsyncDisposable {
        private readonly LineCaptureStream m_output = new();
        private readonly CancellationTokenSource m_stop = CancellationTokenSource.CreateLinkedTokenSource(token: Token);

        private int m_attachments;

        internal InProcessAdapter(Func<IControlSession> createSession, string? directory = null) {
            Host = new LocalControlServer(
                clock: HostClock,
                createSession: () => { Interlocked.Increment(location: ref m_attachments); return createSession(); },
                directory: directory
            );
            Run = OperatorMcpServer.RunAsync(
                (directory ?? Host.AttachmentPath),
                Input,
                m_output,
                m_stop.Token,
                AdapterClock
            );
        }

        internal VirtualClock AdapterClock { get; } = new();

        internal int Attachments => Volatile.Read(location: ref m_attachments);
        internal LocalControlServer Host { get; }

        internal VirtualClock HostClock { get; } = new();
        internal HeldInputStream Input { get; } = new();

        internal Task Run { get; }

        // Formats one JSON-RPC message whose params object gains the per-request metadata; a null id is a notification.
        internal static string Request(int? id, string parameters, string method = "tools/call") {
            var withMeta = (((parameters[..^1] + ((parameters.Length > 2)
                ? ","
                : "")) + Meta) + "}");
            var identity = ((id is { } value)
                ? $"\"id\":{value},"
                : "");

            return ($$"""{"jsonrpc":"2.0",{{identity}}"method":"{{method}}","params":{{withMeta}}""" + "}");
        }
        // Sends one exec and returns the structured result its reply reports.
        internal async Task<JsonElement> ExecAsync(int id, string command) {
            Send(line: Request(
                id: id,
                parameters: (("""{"name":"puck_exec","arguments":{"command":""" + JsonSerializer.Serialize(value: command)) + "}}")
            ));
            return await ResultAsync(id: id);
        }
        internal async Task<JsonDocument> ReplyAsync() => JsonDocument.Parse(json: await m_output.NextLineAsync(cancellationToken: Token).WaitAsync(
            HangGuard,
            Token
        ));
        internal async Task<JsonDocument> RequestAsync(int id, string parameters, string method = "tools/call") {
            Send(line: Request(
                id: id,
                method: method,
                parameters: parameters
            ));
            return await ReplyAsync();
        }
        // Reads the next reply, which must answer the given request, and returns its structured result.
        internal async Task<JsonElement> ResultAsync(int id) {
            using var reply = await ReplyAsync();

            Assert.Equal(
                id,
                reply.RootElement.GetProperty(propertyName: "id").GetInt32()
            );
            return reply.RootElement.GetProperty(propertyName: "result").GetProperty(propertyName: "structuredContent").Clone();
        }
        internal void Send(string line) => Input.Send(line: line);
        // Sends an exec the session holds until cancelled, due after the stall deadline, and waits until the session
        // has it. Its reply, if any, is read by the caller.
        internal async Task StallAsync(int id, TaskCompletionSource entered) {
            Send(line: Request(
                id: id,
                parameters: (("""{"name":"puck_exec","arguments":{"command":"stall","timeoutMs":""" + StallMilliseconds.ToString(provider: CultureInfo.InvariantCulture)) + "}}")
            ));
            await entered.Task.WaitAsync(
                HangGuard,
                Token
            );
        }
        internal async Task StopAsync() {
            await m_stop.CancelAsync();
            await Run.WaitAsync(
                HangGuard,
                Token
            );
        }

        public async ValueTask DisposeAsync() {
            await m_stop.CancelAsync();
            try { await Run; } catch (OperationCanceledException) { }
            Host.Dispose();
            Input.Dispose();
            m_output.Dispose();
            m_stop.Dispose();
        }
    }
    private sealed class BrokenReadStream : MemoryStream {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException<int>(exception: new IOException(message: "broken input"));
    }
    private sealed class FragmentStream(byte[] data) : MemoryStream(data) {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(
            buffer: buffer[..Math.Min(
                val1: 1,
                val2: buffer.Length
            )],
            cancellationToken: cancellationToken
        );
    }
    private sealed class PrefixThenHoldStream(byte[] data) : MemoryStream(data) {
        private readonly TaskCompletionSource<int> m_closed = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool Closed { get; private set; }

        protected override void Dispose(bool disposing) { Closed = true; m_closed.TrySetResult(result: 0); base.Dispose(disposing: disposing); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
            var read = await base.ReadAsync(
                buffer: buffer,
                cancellationToken: cancellationToken
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

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { Entered.TrySetResult(); return new(task: m_closed.Task); }
    }
    private sealed class CloseOnlyReadStream : MemoryStream {
        private readonly TaskCompletionSource<int> m_closed = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Entered { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool Closed { get; private set; }

        protected override void Dispose(bool disposing) { Closed = true; m_closed.TrySetResult(result: 0); base.Dispose(disposing: disposing); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
            Entered.TrySetResult();
            return new(task: m_closed.Task);
        }
    }
    // Serves each sent line to the adapter and holds the read open between lines, as a client's stdin does.
    private sealed class HeldInputStream : MemoryStream {
        private readonly Channel<byte[]> m_lines = Channel.CreateUnbounded<byte[]>();

        private ReadOnlyMemory<byte> m_pending;

        internal bool Closed { get; private set; }

        internal void Send(string line) => Assert.True(condition: m_lines.Writer.TryWrite(item: Encoding.UTF8.GetBytes(s: (line + "\n"))));

        protected override void Dispose(bool disposing) { Closed = true; _ = m_lines.Writer.TryComplete(); base.Dispose(disposing: disposing); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
            while (m_pending.IsEmpty) {
                if (m_lines.Reader.TryRead(item: out var line)) { m_pending = line; } else if (!await m_lines.Reader.WaitToReadAsync(cancellationToken: cancellationToken)) { return 0; }
            }
            var count = Math.Min(
                val1: buffer.Length,
                val2: m_pending.Length
            );

            m_pending[..count].CopyTo(destination: buffer);
            m_pending = m_pending[count..];
            return count;
        }
    }
    // Publishes every complete line the adapter writes, so a test waits for the reply itself rather than for time.
    private sealed class LineCaptureStream : MemoryStream {
        private readonly Channel<string> m_lines = Channel.CreateUnbounded<string>();
        private readonly List<byte> m_partial = [];

        internal async Task<string> NextLineAsync(CancellationToken cancellationToken) => await m_lines.Reader.ReadAsync(cancellationToken: cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) {
            base.Write(
                buffer: buffer,
                count: count,
                offset: offset
            );
            foreach (var value in buffer.AsSpan(
                length: count,
                start: offset
            )) {
                if (value != ((byte)'\n')) { m_partial.Add(item: value); continue; }
                Assert.True(condition: m_lines.Writer.TryWrite(item: Encoding.UTF8.GetString(bytes: [.. m_partial])));
                m_partial.Clear();
            }
        }
    }
    // Answers each exec line with its name, or echoes the line; "stall" signals entry and waits for cancellation.
    private sealed class ScriptedSession(TaskCompletionSource? entered, string? name = null) : IControlSession {
        public void Dispose() { }
        public async Task<ControlResponse> ExecuteAsync(ControlRequest request, CancellationToken cancellationToken) {
            if (request.Command == "stall") {
                entered?.TrySetResult();
                await Task.Delay(
                    cancellationToken: cancellationToken,
                    millisecondsDelay: Timeout.Infinite
                );
                throw new UnreachableException();
            }
            return new(
                request.Id,
                "completed",
                (name ?? request.Command!)
            );
        }
    }
}
