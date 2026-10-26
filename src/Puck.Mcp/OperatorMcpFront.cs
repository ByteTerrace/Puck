using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Puck.Mcp;

/// <summary>A session's stable stdio endpoint. Activated releases serve new calls; superseded workers drain once
/// their calls, Worlds and pending starts end. Every remaining worker drains when the client closes the session.</summary>
public static class OperatorMcpFront {
    private sealed class Worker(McpClient client, Func<Task> stop) {
        public readonly McpClient Client = client;
        public readonly Func<Task> Stop = stop;

        public int Calls;
    }

    /// <summary>Serves an installed operator adapter through the existing application stub.</summary>
    public static Task RunAsync(Stream input, Stream output, string stub, string[] arguments,
        Func<CancellationToken, Task<string>> activate, CancellationToken cancellationToken) =>
        RunAsync(input, output, activate, async token => {
            var info = new ProcessStartInfo(fileName: stub) {
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };

            foreach (var argument in arguments) { info.ArgumentList.Add(item: argument); }
            var process = (Process.Start(startInfo: info) ?? throw new IOException(message: "The application stub did not start."));
            var errors = ForwardErrors(process: process);
            McpClient client;

            try {
                client = await McpClient.CreateAsync(new StreamClientTransport(serverInput: process.StandardInput.BaseStream, serverOutput: process.StandardOutput.BaseStream),
                    new() {
                        ClientInfo = new() { Name = "puck-operator-front", Version = "1.0.0" },
                        ProtocolVersion = "2025-11-25",
                        DiscoverProbeTimeout = Timeout.InfiniteTimeSpan,
                    }, cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);
            } catch {
                await StopWorker(client: null, errors: errors, process: process).ConfigureAwait(continueOnCapturedContext: false);
                throw;
            }
            return (client, () => StopWorker(client: client, errors: errors, process: process));
        }, cancellationToken);
    /// <summary>Serves a stable endpoint over owned SDK worker connections. The factory supplies the connection and
    /// a shutdown operation that drains and disposes it, including its transport.</summary>
    /// <param name="input">The session's JSON-RPC input.</param>
    /// <param name="output">The session's JSON-RPC output.</param>
    /// <param name="activate">Resolves the activated release on each new call.</param>
    /// <param name="connect">Starts an owned worker for the activated release.</param>
    /// <param name="cancellationToken">Ends the session and drains its workers.</param>
    /// <param name="clock">Drives idle-worker inspection and transport deadlines; null uses system time.</param>
    public static async Task RunAsync(Stream input, Stream output, Func<CancellationToken, Task<string>> activate,
        Func<CancellationToken, Task<(McpClient Client, Func<Task> Stop)>> connect, CancellationToken cancellationToken,
        TimeProvider? clock = null) {
        clock ??= TimeProvider.System;
        var workers = new Dictionary<string, Worker>();
        var owners = new Dictionary<string, Worker>(comparer: StringComparer.OrdinalIgnoreCase);
        var starts = new Dictionary<string, Worker>(comparer: StringComparer.Ordinal);
        var completed = new Dictionary<string, CallToolResult>(comparer: StringComparer.Ordinal);
        var gate = new SemaphoreSlim(initialCount: 1);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token: cancellationToken);
        using var bounded = new BoundedMcpInput(ended: lifetime.Cancel, input: input);
        await using var transport = new BoundedMcpTransport(bounded, output, bounded.Fail, clock, lifetime.Token);
        using var interrupt = lifetime.Token.Register(callback: () => { bounded.Dispose(); output.Dispose(); });
        McpServer? endpoint = null;
        string? current = null;

        async Task<Worker> Active(CancellationToken token) {
            var version = await activate(token).ConfigureAwait(continueOnCapturedContext: false);

            if (!workers.TryGetValue(key: version, value: out var worker)) {
                var connection = await connect(token).ConfigureAwait(continueOnCapturedContext: false);

                worker = new Worker(client: connection.Client, stop: connection.Stop);
                workers.Add(key: version, value: worker);
            }
            var changed = ((current is not null) && (current != version));

            current = version;
            if (changed && (endpoint is not null)) {
                await endpoint.SendNotificationAsync(cancellationToken: token, method: NotificationMethods.ToolListChangedNotification).ConfigureAwait(continueOnCapturedContext: false);
            }
            return worker;
        }
        void Record(Worker worker, string tool, IDictionary<string, JsonElement>? arguments, CallToolResult result) {
            if (result.StructuredContent is not { } data) { return; }
            var status = (data.TryGetProperty(propertyName: "status", value: out var state) ? state.GetString() : null);

            if (data.TryGetProperty(propertyName: "startId", value: out var id) && (id.ValueKind == JsonValueKind.String)) { starts[id.GetString()!] = worker; }
            if (status == "started") {
                if (data.TryGetProperty(propertyName: "processId", value: out var pid)) { owners[pid.ToString()] = worker; }
                if (data.TryGetProperty(propertyName: "world", value: out var reached) && (reached.ValueKind == JsonValueKind.Object) &&
                    reached.TryGetProperty(propertyName: "attachmentId", value: out var attachment)) { owners[attachment.GetString()!] = worker; }
            }
            if ((tool == "puck_world_start_wait") && (status != "pending") &&
                (arguments?.TryGetValue(key: "startId", value: out var start) == true) && (start.ValueKind == JsonValueKind.String)) {
                var key = start.GetString()!;

                starts.Remove(key: key);
                if ((completed.Count == 64) && !completed.ContainsKey(key: key)) { completed.Remove(key: completed.Keys.First()); }
                completed[key] = result;
            }
        }
        async Task InspectAsync() {
            KeyValuePair<string, Worker>[] previous;

            await gate.WaitAsync(cancellationToken: lifetime.Token).ConfigureAwait(continueOnCapturedContext: false);
            try {
                previous = [.. workers.Where(predicate: entry => ((entry.Key != current) && (entry.Value.Calls == 0)))];
                foreach (var entry in previous) { entry.Value.Calls++; }
            } finally { gate.Release(); }
            foreach (var (version, worker) in previous) {
                var retired = false;

                try {
                    // The backend accounts for starts admitted despite a lost/cancelled front response, and for
                    // Worlds that have exited without a stop call. Inspect only superseded, otherwise idle workers.
                    var result = await worker.Client.CallToolAsync("puck_adapter", cancellationToken: lifetime.Token).ConfigureAwait(continueOnCapturedContext: false);

                    await gate.WaitAsync(cancellationToken: lifetime.Token).ConfigureAwait(continueOnCapturedContext: false);
                    try {
                        if (result.StructuredContent is { } data) {
                            var owned = data.GetProperty(propertyName: "ownedWorlds");
                            var pending = data.GetProperty(propertyName: "pendingStarts");

                            foreach (var key in owners.Where(predicate: entry => (entry.Value == worker)).Select(selector: entry => entry.Key).ToArray()) { owners.Remove(key: key); }
                            foreach (var world in owned.EnumerateArray()) {
                                owners[world.GetProperty(propertyName: "processId").ToString()] = worker;
                                if (world.TryGetProperty(propertyName: "attachmentId", value: out var attachment) && (attachment.ValueKind == JsonValueKind.String)) {
                                    owners[attachment.GetString()!] = worker;
                                }
                            }
                            foreach (var id in pending.EnumerateArray()) { starts[id.GetString()!] = worker; }
                            if ((current != version) && (worker.Calls == 1) && (owned.GetArrayLength() == 0) && (pending.GetArrayLength() == 0)) {
                                // Preserve terminal start answers without keeping their process. Keep the routing
                                // gate until those routes are replaced, so no new call can enter a retiring worker.
                                var finished = starts.Where(predicate: entry => (entry.Value == worker)).Select(selector: entry => entry.Key).ToArray();

                                foreach (var id in finished) {
                                    var arguments = new Dictionary<string, object?> { ["startId"] = id, ["timeoutMs"] = 1 };
                                    var answer = await worker.Client.CallToolAsync("puck_world_start_wait", arguments, cancellationToken: lifetime.Token).ConfigureAwait(continueOnCapturedContext: false);

                                    Record(worker, "puck_world_start_wait", new Dictionary<string, JsonElement> { ["startId"] = JsonSerializer.SerializeToElement(id, OperatorMcpJson.Default.String) }, answer);
                                }
                                retired = true;
                                workers.Remove(key: version);
                                foreach (var key in owners.Where(predicate: entry => (entry.Value == worker)).Select(selector: entry => entry.Key).ToArray()) { owners.Remove(key: key); }
                            }
                        }
                    } finally { gate.Release(); }
                } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; } catch (Exception error) {
                    // A failed inspection is no evidence of no ownership: keep the worker until a later read or EOF.
                    Console.Error.WriteLine(value: $"[mcp.worker] inspect {version}: {error.Message}");
                } finally {
                    await gate.WaitAsync().ConfigureAwait(continueOnCapturedContext: false);
                    try {
                        worker.Calls--;
                        if (retired) {
                            foreach (var key in starts.Where(predicate: entry => (entry.Value == worker)).Select(selector: entry => entry.Key).ToArray()) { starts.Remove(key: key); }
                        }
                    } finally { gate.Release(); }
                    if (retired) {
                        try { await worker.Stop().ConfigureAwait(continueOnCapturedContext: false); }
                        catch (Exception error) { Console.Error.WriteLine(value: $"[mcp.worker] retire {version}: {error.Message}"); }
                    }
                }
            }
        }
        async Task SweepAsync() {
            using var timer = new PeriodicTimer(period: TimeSpan.FromSeconds(seconds: 1), timeProvider: clock);

            try {
                while (await timer.WaitForNextTickAsync(cancellationToken: lifetime.Token).ConfigureAwait(continueOnCapturedContext: false)) { await InspectAsync().ConfigureAwait(continueOnCapturedContext: false); }
            } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
        await using var server = McpServer.Create(transport, new McpServerOptions {
            ServerInfo = new() { Name = "puck-operator", Version = "1.0.0" },
            Capabilities = new() { Tools = new() { ListChanged = true } },
            ServerInstructions = OperatorMcpServer.Instructions,
            Filters = new() {
                Message = new() {
                    IncomingFilters = [next => (context, token) => {
                bounded.MessageConsumed(); OperatorMcpJson.ValidateParameters(message: context.JsonRpcMessage); return next(context, token);
            }],
                },
            },
            Handlers = new() {
                ListToolsHandler = async (_, token) => {
                    Worker worker;

                    await gate.WaitAsync(cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);
                    try { worker = await Active(token: token).ConfigureAwait(continueOnCapturedContext: false); worker.Calls++; } finally { gate.Release(); }
                    try { return new ListToolsResult { Tools = [.. (await worker.Client.ListToolsAsync(cancellationToken: token)).Select(selector: tool => tool.ProtocolTool)] }; } finally {
                        await gate.WaitAsync().ConfigureAwait(continueOnCapturedContext: false);
                        try { worker.Calls--; } finally { gate.Release(); }
                    }
                },
                CallToolHandler = async (context, token) => {
                    var parameters = context.Params!;
                    Worker worker;

                    await gate.WaitAsync(cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);
                    try {
                        worker = await Active(token: token).ConfigureAwait(continueOnCapturedContext: false);
                        if ((parameters.Name == "puck_world_stop") && (parameters.Arguments?.TryGetValue(key: "world", value: out var world) == true) &&
                            (world.ValueKind == JsonValueKind.String) && owners.TryGetValue(key: world.GetString()!, value: out var owner)) { worker = owner; }
                        if ((parameters.Name == "puck_world_start_wait") && (parameters.Arguments?.TryGetValue(key: "startId", value: out var start) == true) && (start.ValueKind == JsonValueKind.String)) {
                            if (completed.TryGetValue(key: start.GetString()!, value: out var answer)) { return answer; }
                            if (starts.TryGetValue(key: start.GetString()!, value: out var starter)) { worker = starter; }
                        }
                        worker.Calls++;
                    } finally { gate.Release(); }
                    try {
                        var result = await worker.Client.CallToolAsync(parameters.Name,
                            parameters.Arguments?.ToDictionary(entry => entry.Key, entry => ((object?)entry.Value)), cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);

                        await gate.WaitAsync(cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);
                        try {
                            Record(worker, parameters.Name, parameters.Arguments, result);
                            if ((parameters.Name == "puck_worlds") && (result.StructuredContent is { } data)) {
                                var listed = JsonNode.Parse(data.GetRawText())!;

                                foreach (var entry in listed["worlds"]!.AsArray()) {
                                    if (owners.ContainsKey(key: entry!["processId"]!.ToString())) { entry["started"] = true; }
                                }
                                result.StructuredContent = JsonElement.Parse(listed.ToJsonString());
                            }
                        } finally { gate.Release(); }
                        return result;
                    } finally {
                        await gate.WaitAsync().ConfigureAwait(continueOnCapturedContext: false);
                        try { worker.Calls--; } finally { gate.Release(); }
                    }
                },
            },
        });

        endpoint = server;
        var sweep = SweepAsync();

        try { await server.RunAsync(cancellationToken: lifetime.Token).ConfigureAwait(continueOnCapturedContext: false); } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { } finally {
            await lifetime.CancelAsync().ConfigureAwait(continueOnCapturedContext: false);
            try { await sweep.ConfigureAwait(continueOnCapturedContext: false); }
            finally {
                try { await Task.WhenAll(tasks: workers.Values.Select(selector: worker => worker.Stop())).ConfigureAwait(continueOnCapturedContext: false); }
                finally { gate.Dispose(); }
            }
        }
        if (bounded.Failure is { } failure) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(source: failure).Throw(); }
    }

    private static async Task ForwardErrors(Process process) {
        while (await process.StandardError.ReadLineAsync().ConfigureAwait(continueOnCapturedContext: false) is { } line) { Console.Error.WriteLine(value: line); }
    }
    private static async Task StopWorker(McpClient? client, Process process, Task errors) {
        try {
            try { process.StandardInput.Close(); } catch (IOException) { }
            try { await process.WaitForExitAsync().WaitAsync(timeout: TimeSpan.FromSeconds(seconds: 30)).ConfigureAwait(continueOnCapturedContext: false); } catch (TimeoutException) {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); }
                await process.WaitForExitAsync().ConfigureAwait(continueOnCapturedContext: false);
            }
            await errors.ConfigureAwait(continueOnCapturedContext: false);
        } finally {
            try { if (client is not null) { await client.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false); } } finally { process.Dispose(); }
        }
    }
}
