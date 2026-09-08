using System.Globalization;
using System.Text.Json;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using Puck.Hosting;

namespace Puck.Mcp;

/// <summary>Official-SDK stdio adapter for an explicitly trusted local Operator. World owns its own lifetime.</summary>
public static class OperatorMcpServer {
    private static readonly JsonElement ExecInput = JsonElement.Parse("""{"type":"object","properties":{"command":{"type":"string","minLength":1,"maxLength":8192},"timeoutMs":{"type":"integer","minimum":1,"maximum":120000,"default":30000}},"required":["command"],"additionalProperties":false}""");
    private static readonly JsonElement CaptureInput = JsonElement.Parse("""{"type":"object","properties":{"timeoutMs":{"type":"integer","minimum":1,"maximum":120000,"default":30000}},"additionalProperties":false}""");
    private static readonly JsonElement ResultSchema = JsonElement.Parse("""{"type":"object","properties":{"requestId":{"type":["string","null"]},"status":{"type":"string","enum":["completed","submitted","refused","unknown"]},"output":{"type":"string"},"isError":{"type":"boolean"},"clearTranscript":{"type":"boolean"}},"required":["requestId","status","output","isError","clearTranscript"],"additionalProperties":false}""");

    /// <summary>Serves two tools until EOF or cancellation, closing only the attached Console session.</summary>
    /// <param name="attachmentPath">The running World's private capability file.</param>
    /// <param name="input">MCP JSON-RPC input, with a 64 KiB line ceiling.</param>
    /// <param name="output">Protocol-only output. The adapter owns and closes both streams on shutdown.</param>
    /// <param name="cancellationToken">Adapter shutdown.</param>
    public static async Task RunAsync(string attachmentPath, Stream input, Stream output, CancellationToken cancellationToken = default) {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token: cancellationToken);
        using var client = await LocalControlClient.ConnectAsync(attachmentPath: attachmentPath, cancellationToken: lifetime.Token).ConfigureAwait(continueOnCapturedContext: false);
        using var bounded = new BoundedMcpInput(input, () => { client.Dispose(); lifetime.Cancel(); });
        await using var transport = new BoundedMcpTransport(bounded, output, bounded.Fail, lifetime.Token);
        using var interruptIo = lifetime.Token.Register(callback: () => { bounded.Dispose(); output.Dispose(); });
        await using var server = McpServer.Create(transport, new McpServerOptions {
            ProtocolVersion = "2026-07-28",
            ServerInfo = new() { Name = "puck-operator", Version = "1.0.0" },
            ServerInstructions = "Trusted local Operator: full Puck Console authority. Call tools serially. Exec evaluates one Puck console line. A submitted result is not an authoritative mutation receipt. Capture returns the next completed composed PNG, including overlays. Cancellation or timeout closes the attachment; restart the adapter and inspect state before any retry. World continues running. Participant and remote access are not provided.",
            Filters = new() {
                Message = new() {
                    IncomingFilters = [next => (context, token) => {
                    bounded.MessageConsumed();
                    OperatorMcpJson.ValidateParameters(message: context.JsonRpcMessage);
                    return next(context, token);
                }],
                },
            },
            Handlers = new() {
                ListToolsHandler = (_, _) => ValueTask.FromResult(result: new ListToolsResult { Tools = [ExecTool(), CaptureTool()] }),
                CallToolHandler = (context, token) => CallAsync(client, context.Params, token),
            },
        });

        try { await server.RunAsync(cancellationToken: lifetime.Token).ConfigureAwait(continueOnCapturedContext: false); } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        if (bounded.Failure is { } failure) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(source: failure).Throw(); }
    }

    internal static Tool ExecTool() => new() {
        Name = "puck_exec",
        Description = "Execute one Puck console command as the explicitly trusted Operator. Full existing registry, ordinary validation and authority. No batches, blank lines or comments. Results preserve output/error/clearTranscript; submitted does not certify authoritative application. Use help to discover console verbs.",
        InputSchema = ExecInput,
        OutputSchema = ResultSchema,
        Annotations = new() { DestructiveHint = true, IdempotentHint = false, OpenWorldHint = true, ReadOnlyHint = false },
    };
    internal static Tool CaptureTool() => new() {
        Name = "puck_capture_frame",
        Description = "Await the next completed composed frame, ordered after this attachment's prior commands and waits. Returns PNG directly, including overlays, up to 16 MiB. Requires a renderer; busy and capture failures are tool errors. No output path or exact-tick guarantee.",
        InputSchema = CaptureInput,
        OutputSchema = ResultSchema,
        Annotations = new() { DestructiveHint = false, IdempotentHint = false, OpenWorldHint = false, ReadOnlyHint = true },
    };
    internal static async ValueTask<CallToolResult> CallAsync(IControlSession client, CallToolRequestParams? parameters, CancellationToken token, long requestId = 1) {
        if ((parameters is null) || (parameters.Name is not ("puck_exec" or "puck_capture_frame"))) { throw new McpProtocolException(errorCode: McpErrorCode.InvalidParams, message: "Unknown tool."); }
        var exec = (parameters.Name == "puck_exec");
        var timeout = 30_000;
        string? command = null;

        if (parameters.Arguments is { } arguments) {
            foreach (var (key, value) in arguments) {
                if ((key == "timeoutMs") && (value.ValueKind == JsonValueKind.Number) && value.TryGetInt32(value: out var parsed) && (parsed is >= 1 and <= ControlLimits.TimeoutMilliseconds)) { timeout = parsed; } else if ((key == "command") && exec && (value.ValueKind == JsonValueKind.String)) { command = value.GetString(); } else { return Error($"Invalid argument: {key}.", unknown: false); }
            }
        }
        if (LocalControlServer.Validate(request: new(Command: command, Id: 1, Operation: (exec ? "exec" : "capture"), TimeoutMilliseconds: timeout)) is { } refusal) { return Error(refusal.Output, unknown: false); }
        try {
            var request = new ControlRequest(Command: command, Id: requestId, Operation: (exec ? "exec" : "capture"), TimeoutMilliseconds: timeout);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token: token);

            deadline.CancelAfter(millisecondsDelay: timeout);
            var operation = client.ExecuteAsync(request, deadline.Token);
            // A host can ignore cancellation. Bound the caller and observe a late failure without replaying work.
            _ = operation.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var result = await operation.WaitAsync(cancellationToken: deadline.Token).ConfigureAwait(continueOnCapturedContext: false);

            if ((result is null) || ((client is not LocalControlClient) && !result.IsValidFor(request: request))) { client.Dispose(); return Error("Host returned an invalid result; inspect state before retrying."); }
            return Result(((result.Id == 0) ? null : result.Id.ToString(provider: CultureInfo.InvariantCulture)), result.Status, result.Output, result.IsError, result.ClearTranscript, result.Png);
        } catch (Exception error) when (((error is TimeoutException) || ((error is OperationCanceledException) && !token.IsCancellationRequested))) {
            client.Dispose();
            return Error("Deadline expired. Attachment closed; dispatched outcome is unknown. Restart and inspect state before retrying.");
        } catch (OperationCanceledException) {
            client.Dispose();
            throw;
        } catch (Exception error) when ((error is IOException or InvalidDataException or System.Net.Sockets.SocketException or ObjectDisposedException or JsonException)) {
            return Error($"Attachment closed; dispatched outcome may be unknown. Restart and inspect state before retrying. {error.Message}");
        }
    }
    internal static CallToolResult Error(string message, bool unknown = true) => Result(clearTranscript: false, isError: true, output: message, png: null, requestId: null, status: (unknown ? "unknown" : "refused"));

    private static CallToolResult Result(string? requestId, string status, string output, bool isError, bool clearTranscript, byte[]? png) {
        var metadata = JsonSerializer.SerializeToElement(
            new OperatorMcpResultMetadata(ClearTranscript: clearTranscript, IsError: isError, Output: output, RequestId: requestId, Status: status),
            OperatorMcpJson.Default.OperatorMcpResultMetadata);
        List<ContentBlock> content = [new TextContentBlock { Text = metadata.GetRawText() }];

        if (!isError && (png is { Length: > 0 })) { content.Add(item: ImageContentBlock.FromBytes(bytes: png, mimeType: "image/png")); }
        return new() { Content = content, IsError = isError, StructuredContent = metadata };
    }


}
