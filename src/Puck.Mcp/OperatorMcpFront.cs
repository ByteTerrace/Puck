using System.Text.Json;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Puck.Mcp;

/// <summary>A session's stable stdio endpoint. Activated releases serve new calls; earlier workers retain their
/// owned Worlds until those Worlds stop, and every worker drains when the client closes the session.</summary>
public static class OperatorMcpFront {
    /// <summary>Serves an installed operator adapter through the existing application stub.</summary>
    public static async Task RunAsync(Stream input, Stream output, string stub, string[] arguments,
        Func<CancellationToken, Task<string>> activate, CancellationToken cancellationToken) {
        var workers = new Dictionary<string, McpClient>();
        var owners = new ConcurrentDictionary<string, McpClient>(comparer: StringComparer.OrdinalIgnoreCase);
        var starts = new ConcurrentDictionary<string, McpClient>(comparer: StringComparer.Ordinal);
        var gate = new SemaphoreSlim(initialCount: 1);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token: cancellationToken);
        using var bounded = new BoundedMcpInput(ended: lifetime.Cancel, input: input);
        await using var transport = new BoundedMcpTransport(bounded, output, bounded.Fail, TimeProvider.System, lifetime.Token);
        using var interrupt = lifetime.Token.Register(callback: () => { bounded.Dispose(); output.Dispose(); });
        McpServer? endpoint = null;

        async Task<McpClient> Active(CancellationToken token) {
            var version = await activate(token).ConfigureAwait(continueOnCapturedContext: false);

            if (workers.TryGetValue(key: version, value: out var existing)) { return existing; }
            var client = await McpClient.CreateAsync(new StdioClientTransport(new() {
                Command = stub,
                Arguments = arguments,
                Name = $"puck-operator-{version}",
                StandardErrorLines = line => Console.Error.WriteLine(value: line),
            }), new() { ClientInfo = new() { Name = "puck-operator-front", Version = "1.0.0" } }, cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);

            workers.Add(key: version, value: client);
            if ((workers.Count > 1) && (endpoint is not null)) {
                await endpoint.SendNotificationAsync(cancellationToken: token, method: NotificationMethods.ToolListChangedNotification).ConfigureAwait(continueOnCapturedContext: false);
            }
            return client;
        }
        async Task<McpClient> Current(CancellationToken token) {
            await gate.WaitAsync(cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);
            try { return await Active(token: token).ConfigureAwait(continueOnCapturedContext: false); } finally { gate.Release(); }
        }
        await using var server = McpServer.Create(transport, new McpServerOptions {
            ServerInfo = new() { Name = "puck-operator", Version = "1.0.0" },
            Capabilities = new() { Tools = new() { ListChanged = true } },
            ServerInstructions = "Trusted local Operator. New activated builds take effect on the next call. Worlds and pending starts remain owned by this session across updates. Name your World in every call; inspect unknown outcomes before retrying.",
            Filters = new() {
                Message = new() {
                    IncomingFilters = [next => (context, token) => {
                bounded.MessageConsumed(); OperatorMcpJson.ValidateParameters(message: context.JsonRpcMessage); return next(context, token);
            }],
                },
            },
            Handlers = new() {
                ListToolsHandler = async (_, token) => {
                    return new ListToolsResult { Tools = [.. (await (await Current(token: token)).ListToolsAsync(cancellationToken: token)).Select(selector: tool => tool.ProtocolTool)] };
                },
                CallToolHandler = async (context, token) => {
                    var parameters = context.Params!;
                    var active = await Current(token: token).ConfigureAwait(continueOnCapturedContext: false);
                    var selected = active;

                    if ((parameters.Name == "puck_world_stop") && (parameters.Arguments?.TryGetValue(key: "world", value: out var world) == true) &&
                        (world.ValueKind == JsonValueKind.String) && owners.TryGetValue(key: world.GetString()!, value: out var owner)) { selected = owner; }
                    if ((parameters.Name == "puck_world_start_wait") && (parameters.Arguments?.TryGetValue(key: "startId", value: out var start) == true) &&
                        (start.ValueKind == JsonValueKind.String) && starts.TryGetValue(key: start.GetString()!, value: out var starter)) { selected = starter; }
                    var result = await selected.CallToolAsync(parameters.Name,
                        parameters.Arguments?.ToDictionary(entry => entry.Key, entry => ((object?)entry.Value)), cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);

                    if (result.StructuredContent is { } data) {
                        if (parameters.Name == "puck_worlds") {
                            var listed = JsonNode.Parse(data.GetRawText())!;

                            foreach (var entry in listed["worlds"]!.AsArray()) {
                                if (owners.ContainsKey(key: entry!["processId"]!.ToString())) { entry["started"] = true; }
                            }
                            data = JsonElement.Parse(listed.ToJsonString());
                            result.StructuredContent = data;
                            result.Content = [new TextContentBlock { Text = data.GetRawText() }];
                        }
                        if (data.TryGetProperty(propertyName: "startId", value: out var id) && (id.ValueKind == JsonValueKind.String)) { starts[id.GetString()!] = selected; }
                        if (data.TryGetProperty(propertyName: "status", value: out var status) && (status.GetString() == "started")) {
                            if (data.TryGetProperty(propertyName: "processId", value: out var pid)) { owners[pid.ToString()] = selected; }
                            if (data.TryGetProperty(propertyName: "world", value: out var reached) && (reached.ValueKind == JsonValueKind.Object) &&
                                reached.TryGetProperty(propertyName: "attachmentId", value: out var attachment)) { owners[attachment.GetString()!] = selected; }
                        }
                    }
                    return result;
                },
            },
        });

        endpoint = server;
        try { await server.RunAsync(cancellationToken: lifetime.Token).ConfigureAwait(continueOnCapturedContext: false); } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { } finally {
            // SDK disposal closes worker stdin. The worker stops only the Worlds it started before it exits.
            foreach (var worker in workers.Values) { await worker.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false); }
            gate.Dispose();
        }
        if (bounded.Failure is { } failure) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(source: failure).Throw(); }
    }
}
