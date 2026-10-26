using System.IO.Pipelines;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Puck.Testing;
using Xunit;

namespace Puck.Mcp.Tests;

public sealed class OperatorMcpFrontLawTests {
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(seconds: 30);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly JsonElement Input = JsonElement.Parse("""{"type":"object"}""");

    [Fact]
    public Task AnIdleSupersededWorkerIsDisposedBeforeSessionEnds() => ASupersededWorkerDrainsOnlyAfterItsCallsWorldsAndPendingStartsEnd(scenario: "idle");
    [InlineData("pending")]
    [InlineData("failed-start")]
    [InlineData("stop")]
    [InlineData("exit")]
    [InlineData("in-flight")]
    [Theory]
    public async Task ASupersededWorkerDrainsOnlyAfterItsCallsWorldsAndPendingStartsEnd(string scenario) {
        var clock = new VirtualClock();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token: Token);
        var input = new Pipe();
        var output = new Pipe();
        var backends = new List<Backend>();
        var version = 1;
        var run = OperatorMcpFront.RunAsync(input.Reader.AsStream(), output.Writer.AsStream(),
            _ => Task.FromResult(result: Volatile.Read(location: ref version).ToString(provider: System.Globalization.CultureInfo.InvariantCulture)),
            async token => {
                var backend = new Backend();

                backends.Add(item: backend);
                var client = await backend.ConnectAsync(token: token);

                return (client, backend.StopAsync);
            }, lifetime.Token, clock);
        var front = await McpClient.CreateAsync(new StreamClientTransport(input.Writer.AsStream(), output.Reader.AsStream()),
            new() { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan, ProtocolVersion = "2025-11-25" }, cancellationToken: Token);

        try {
            async Task<JsonElement> Call(string name, Dictionary<string, object?>? args = null) =>
                (await front.CallToolAsync(name, args, cancellationToken: Token).AsTask().WaitAsync(Guard, Token)).StructuredContent!.Value;
            async Task Sweep() {
                await clock.WhenArmedAsync(TimeSpan.FromSeconds(seconds: 1), 1, Token).WaitAsync(Guard, Token);
                clock.Advance(by: TimeSpan.FromSeconds(seconds: 1));
            }
            _ = await Call("puck_adapter");
            var first = Assert.Single(collection: backends);
            Task<JsonElement>? held = null;

            if (scenario is "pending" or "failed-start") {
                first.Pending = true;
                Assert.Equal("pending", (await Call("puck_world_start")).GetProperty(propertyName: "status").GetString());
            } else if (scenario is "stop" or "exit") {
                first.Owned = true;
                Assert.Equal("started", (await Call("puck_world_start")).GetProperty(propertyName: "status").GetString());
            } else if (scenario == "in-flight") {
                held = Call("puck_exec");
                await first.Entered.Task.WaitAsync(Guard, Token);
            }
            Volatile.Write(location: ref version, value: 2);
            _ = await Call("puck_adapter");
            Assert.Equal(2, backends.Count);
            await Sweep();
            if (scenario != "idle") {
                Assert.False(condition: first.Disposed.Task.IsCompleted);
                if (scenario is "pending" or "failed-start") {
                    var args = new Dictionary<string, object?> { ["startId"] = "start-1" };

                    Assert.Equal("pending", (await Call(args: args, name: "puck_world_start_wait")).GetProperty(propertyName: "status").GetString());
                    first.Owned = (scenario == "pending");
                    first.Pending = false;
                    if (first.Owned) {
                        Assert.Equal("started", (await Call(args: args, name: "puck_world_start_wait")).GetProperty(propertyName: "status").GetString());
                        Assert.Equal("stopped", (await Call("puck_world_stop", new() { ["world"] = "attachment-1" })).GetProperty(propertyName: "status").GetString());
                    }
                } else if (scenario == "stop") {
                    Assert.Equal("stopped", (await Call("puck_world_stop", new() { ["world"] = "123" })).GetProperty(propertyName: "status").GetString());
                } else if (scenario == "exit") {
                    first.Owned = false;
                } else {
                    first.Release.TrySetResult();
                    await held!;
                }
                await Sweep();
            }
            await first.Disposed.Task.WaitAsync(Guard, Token);
            Assert.False(condition: backends[1].Disposed.Task.IsCompleted);
            if (scenario == "failed-start") {
                // A completed refusal stays readable without holding the old worker's transport or process.
                Assert.Equal("refused", (await Call("puck_world_start_wait", new() { ["startId"] = "start-1" })).GetProperty(propertyName: "status").GetString());
            }
        } finally {
            await lifetime.CancelAsync();
            await front.DisposeAsync();
            await run.WaitAsync(Guard, Token);
        }
        Assert.All(backends, backend => Assert.True(condition: backend.Disposed.Task.IsCompleted));
    }

    private sealed class Backend {
        private readonly Pipe m_input = new();
        private readonly Pipe m_output = new();
        private readonly CancellationTokenSource m_stop = new();

        private McpServer? m_server;
        private McpClient? m_client;
        private Task? m_run;

        public volatile bool Owned;
        public volatile bool Pending;

        public readonly TaskCompletionSource Entered = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Disposed = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<McpClient> ConnectAsync(CancellationToken token) {
            m_server = McpServer.Create(new StreamServerTransport(m_input.Reader.AsStream(), m_output.Writer.AsStream(), "worker"), new() {
                ServerInfo = new() { Name = "worker", Version = "1" },
                Handlers = new() {
                    ListToolsHandler = (_, _) => ValueTask.FromResult(result: new ListToolsResult { Tools = [new Tool { InputSchema = Input, Name = "puck_adapter" }] }),
                    CallToolHandler = async (context, cancellation) => {
                        if (context.Params!.Name == "puck_adapter") {
                            return Result(data: JsonSerializer.SerializeToElement(new {
                                ownedWorlds = (Owned ? new[] { new { processId = 123, attachmentId = "attachment-1" } } : []),
                                pendingStarts = (Pending ? new[] { "start-1" } : []),
                            }));
                        }
                        if (context.Params.Name == "puck_exec") {
                            Entered.TrySetResult();
                            await Release.Task.WaitAsync(cancellationToken: cancellation);
                            return Result(data: JsonElement.Parse("""{"status":"completed"}"""));
                        }
                        if (context.Params.Name == "puck_world_stop") {
                            Assert.True(condition: Owned);
                            Owned = false;
                            return Result(data: JsonElement.Parse("""{"status":"stopped"}"""));
                        }
                        if (Pending) { return Result(data: JsonElement.Parse("""{"status":"pending","startId":"start-1"}""")); }
                        return Result(data: (Owned
                            ? JsonElement.Parse("""{"status":"started","processId":123,"world":{"attachmentId":"attachment-1"}}""")
                            : JsonElement.Parse("""{"status":"refused"}""")));
                    },
                },
            });
            m_run = m_server.RunAsync(cancellationToken: m_stop.Token);
            m_client = await McpClient.CreateAsync(new StreamClientTransport(m_input.Writer.AsStream(), m_output.Reader.AsStream()),
                new() { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan, ProtocolVersion = "2025-11-25" }, cancellationToken: token);
            return m_client;
        }

        private static CallToolResult Result(JsonElement data) => new() { Content = [new TextContentBlock { Text = "result" }], StructuredContent = data };

        public async Task StopAsync() {
            await m_stop.CancelAsync();
            await m_client!.DisposeAsync();
            try { await m_run!; } catch (OperationCanceledException) { }
            await m_server!.DisposeAsync();
            m_stop.Dispose();
            Assert.False(condition: Disposed.Task.IsCompleted);
            Disposed.TrySetResult();
        }
    }
}
