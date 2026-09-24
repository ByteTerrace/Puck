using System.Globalization;
using System.Text.Json;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using Puck.Hosting;
using Puck.Networking;

namespace Puck.Mcp;

/// <summary>Official-SDK stdio adapter for an explicitly trusted local Operator. World owns its own lifetime.</summary>
public static class OperatorMcpServer {
    internal const string StatusMeanings = "status is completed when the command returned output, submitted when it returned none (accepted, which does not certify authoritative application), refused when the command or adapter reported an error, and unknown when the attachment closed or the deadline passed after dispatch; inspect state before retrying an unknown call. Mutation commands that report an authority settlement wait for its applied or refused verdict in output. clearTranscript is true when the command asked the console to clear its transcript.";

    private static readonly JsonElement ExecInput = JsonElement.Parse("""{"type":"object","properties":{"command":{"type":"string","minLength":1,"maxLength":8192,"description":"One console line, exactly as typed at the Puck console."},"timeoutMs":{"type":"integer","minimum":1,"maximum":120000,"default":30000,"description":"Milliseconds to wait for the result. When it passes, the attachment closes and the outcome is reported as unknown."}},"required":["command"],"additionalProperties":false}""");
    private static readonly JsonElement CaptureInput = JsonElement.Parse("""{"type":"object","properties":{"timeoutMs":{"type":"integer","minimum":1,"maximum":120000,"default":30000,"description":"Milliseconds to wait for the frame. When it passes, the attachment closes and the outcome is reported as unknown."}},"additionalProperties":false}""");

    /// <summary>The output schema every console-backed tool's structured result satisfies: <c>puck_exec</c>,
    /// <c>puck_capture_frame</c>, and <c>puck_state_vector_write</c>, locally and over HTTP.</summary>
    internal static readonly JsonElement ResultSchema = JsonElement.Parse("""{"type":"object","properties":{"requestId":{"type":["string","null"]},"status":{"type":"string","enum":["completed","submitted","refused","unknown"]},"output":{"type":"string"},"isError":{"type":"boolean"},"clearTranscript":{"type":"boolean"}},"required":["requestId","status","output","isError","clearTranscript"],"additionalProperties":false}""");

    internal static async ValueTask<CallToolResult> CallAsync(IControlSession client, CallToolRequestParams? parameters, TimeProvider clock, CancellationToken token, long requestId = 1) {
        if (
            (parameters is null) ||
            (parameters.Name is not ("puck_exec" or "puck_capture_frame" or "puck_state_vector_write"))
        ) {
            throw new McpProtocolException(
            errorCode: McpErrorCode.InvalidParams,
            message: "Unknown tool."
        );
        }
        var exec = (parameters.Name == "puck_exec");
        var vectorWrite = (parameters.Name == "puck_state_vector_write");
        var timeout = 30_000;
        string? command = null;
        string? row = null;
        string? key = null;
        string? vector = null;

        if (parameters.Arguments is { } arguments) {
            foreach (var (argumentKey, value) in arguments) {
                if (
                    (argumentKey == "timeoutMs") &&
                    (value.ValueKind == JsonValueKind.Number) &&
                    value.TryGetInt32(value: out var parsed) &&
                    (parsed is >= 1 and <= ControlLimits.TimeoutMilliseconds)
                ) { timeout = parsed; } else if (
                    (argumentKey == "command") &&
                    exec &&
                    (value.ValueKind == JsonValueKind.String)
                ) { command = value.GetString(); } else if (
                    (argumentKey == "row") &&
                    vectorWrite &&
                    (value.ValueKind == JsonValueKind.String)
                ) { row = value.GetString(); } else if (
                    (argumentKey == "key") &&
                    vectorWrite &&
                    (value.ValueKind is (JsonValueKind.String or JsonValueKind.Null))
                ) { key = ((value.ValueKind == JsonValueKind.Null) ? null : value.GetString()); } else if (
                    (argumentKey == "vector") &&
                    vectorWrite &&
                    (value.ValueKind == JsonValueKind.String)
                ) { vector = value.GetString(); } else {
                    return Error(
                    $"Invalid argument: {argumentKey}.",
                    unknown: false
                );
                }
            }
        }
        if (vectorWrite) {
            if (string.IsNullOrWhiteSpace(value: row)) {
                return Error(
                    "Invalid argument: row.",
                    unknown: false
                );
            }
            if (string.IsNullOrWhiteSpace(value: vector)) {
                return Error(
                    "Invalid argument: vector.",
                    unknown: false
                );
            }
            var cellKey = (string.IsNullOrEmpty(value: key) ? "$value" : key);

            command = $"world.state.cell.set {row} {cellKey} {vector}";
        }
        if (LocalControlServer.Validate(request: new(
            Command: command,
            Id: 1,
            Operation: ((exec || vectorWrite)
            ? "exec"
            : "capture"),
            TimeoutMilliseconds: timeout
        )) is { } refusal) {
            return Error(
            refusal.Output,
            unknown: false
        );
        }
        try {
            var request = new ControlRequest(
                Command: command,
                Id: requestId,
                Operation: ((exec || vectorWrite)
                ? "exec"
                : "capture"),
                TimeoutMilliseconds: timeout
            );
            using var deadline = new OperationDeadline(
                caller: token,
                timeout: TimeSpan.FromMilliseconds(value: timeout),
                timeProvider: clock
            );

            var operation = client.ExecuteAsync(
                request,
                deadline.Token
            );
            // A host can ignore cancellation. Bound the caller and observe a late failure without replaying work.
            _ = operation.ContinueWith(
                static task => { _ = task.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
            var result = await operation.WaitAsync(cancellationToken: deadline.Token).ConfigureAwait(continueOnCapturedContext: false);

            if (
                (result is null) ||
                ((client is not LocalControlClient) && !result.IsValidFor(request: request))
            ) { client.Dispose(); return Error("Host returned an invalid result; inspect state before retrying."); }
            return Result(
                ((result.Id == 0)
                ? null
                : result.Id.ToString(provider: CultureInfo.InvariantCulture)),
                result.Status,
                result.Output,
                result.IsError,
                result.ClearTranscript,
                result.Png
            );
        } catch (Exception error) when (((error is TimeoutException) || ((error is OperationCanceledException) && !token.IsCancellationRequested))) {
            client.Dispose();
            return Error("Deadline expired. Attachment closed; dispatched outcome is unknown. Inspect state before retrying; the next call attaches anew.");
        } catch (OperationCanceledException) {
            client.Dispose();
            throw;
        } catch (Exception error) when ((error is IOException or InvalidDataException or System.Net.Sockets.SocketException or ObjectDisposedException or JsonException)) {
            return Error($"Attachment closed; dispatched outcome may be unknown. Inspect state before retrying; the next call attaches anew. {error.Message}");
        }
    }
    internal static Tool CaptureTool() => new() {
        Name = "puck_capture_frame",
        Description = "Await the next completed composed frame, ordered after this attachment's prior commands and waits. Returns PNG directly, including overlays, up to 16 MiB. Requires a renderer; busy and capture failures are tool errors. No output path or exact-tick guarantee.",
        InputSchema = CaptureInput,
        OutputSchema = ResultSchema,
        Annotations = new() { DestructiveHint = false, IdempotentHint = false, OpenWorldHint = false, ReadOnlyHint = true },
    };
    internal static CallToolResult Error(string message, bool unknown = true) => Result(
        clearTranscript: false,
        isError: true,
        output: message,
        png: null,
        requestId: null,
        status: (unknown
        ? "unknown"
        : "refused")
    );
    internal static Tool ExecTool() => new() {
        Name = "puck_exec",
        Description = ("Execute one Puck console line as the explicitly trusted Operator, with the full console registry, ordinary validation and the Operator's authority. The line must be one nonblank, non-comment line; batches are refused. Run help to discover console verbs. " + StatusMeanings),
        InputSchema = ExecInput,
        OutputSchema = ResultSchema,
        Annotations = new() { DestructiveHint = true, IdempotentHint = false, OpenWorldHint = true, ReadOnlyHint = false },
    };

    private static CallToolResult Result(string? requestId, string status, string output, bool isError, bool clearTranscript, byte[]? png) {
        var metadata = JsonSerializer.SerializeToElement(
            new OperatorMcpResultMetadata(
                ClearTranscript: clearTranscript,
                IsError: isError,
                Output: output,
                RequestId: requestId,
                Status: status
            ),
            OperatorMcpJson.Default.OperatorMcpResultMetadata
        );
        List<ContentBlock> content = [new TextContentBlock { Text = metadata.GetRawText() }];

        if (
            !isError &&
            (png is { Length: > 0 })
        ) {
            content.Add(item: ImageContentBlock.FromBytes(
            bytes: png,
            mimeType: "image/png"
        ));
        }
        return new() { Content = content, IsError = isError, StructuredContent = metadata };
    }

    /// <summary>Serves the tools until EOF or cancellation, independent of any World's lifetime. Each call runs on the
    /// current attachment, opening one first when there is none: to the capability file <paramref name="target"/>
    /// names, or to the newest World in the directory it names that answers. A call whose attachment closes is
    /// reported with an unknown outcome and never replayed; the next call attaches anew, so a World restart needs no
    /// adapter restart.</summary>
    /// <param name="target">A World's private capability file, or a directory of them whose newest answering World
    /// each attachment follows; Worlds publish theirs in the user's temporary directory.</param>
    /// <param name="input">MCP JSON-RPC input, with a 64 KiB line ceiling.</param>
    /// <param name="output">Protocol-only output. The adapter owns and closes both streams on shutdown.</param>
    /// <param name="cancellationToken">Adapter shutdown.</param>
    /// <param name="clock">Drives every adapter-side deadline: the attachment's connect and handshake, each call, the
    /// control connection's own call deadline, and each reply write. <see langword="null"/> is
    /// <see cref="TimeProvider.System"/>. The World's side runs its deadlines on its own clock.</param>
    public static async Task RunAsync(string target, Stream input, Stream output, CancellationToken cancellationToken = default, TimeProvider? clock = null) {
        ArgumentException.ThrowIfNullOrEmpty(target);
        clock ??= TimeProvider.System;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token: cancellationToken);
        using var attachment = new OperatorAttachment(
            clock: clock,
            target: target
        );
        using var bounded = new BoundedMcpInput(
            input,
            () => { attachment.Dispose(); lifetime.Cancel(); }
        );
        await using var transport = new BoundedMcpTransport(
            bounded,
            output,
            bounded.Fail,
            clock,
            lifetime.Token
        );
        using var interruptIo = lifetime.Token.Register(callback: () => { bounded.Dispose(); output.Dispose(); });
        await using var server = McpServer.Create(
            transport,
            // No revision is pinned: a pinned one turns the initialize handshake off, and an editor or an agent
            // harness opens with it. The server answers whichever revision the client speaks.
            new McpServerOptions {
                ServerInfo = new() { Name = "puck-operator", Version = "1.0.0" },
                ServerInstructions = "Trusted local Operator: full Puck Console authority; participant and remote access are not provided. Each call attaches to the running World on demand; with no World it is refused and nothing runs. The adapter runs one call at a time, so issue calls serially when their order matters: a capture is ordered after the commands before it. A submitted result is not an authoritative mutation receipt. Cancellation, timeout or a lost World closes the attachment and reports an unknown outcome; the next call attaches anew, possibly to a restarted World with fresh state, so inspect state before any retry.",
                Filters = new() {
                    Message = new() {
                        IncomingFilters = [next => (context, token) => {
                    bounded.MessageConsumed();
                    OperatorMcpJson.ValidateParameters(message: context.JsonRpcMessage);
                    return next(
                        context,
                        token
                    );
                }],
                    },
                },
                Handlers = new() {
                    ListToolsHandler = (_, _) => ValueTask.FromResult(result: new ListToolsResult { Tools = [ExecTool(), CaptureTool(), RemoteMcpHost.StateVectorWriteTool] }),
                    CallToolHandler = async (context, token) => await attachment.RunAsync(
                        call: (client, callToken) => CallAsync(
                            client,
                            context.Params,
                            clock,
                            callToken
                        ).AsTask(),
                        refuse: refusal => Error(
                            refusal,
                            unknown: false
                        ),
                        cancellationToken: token
                    ).ConfigureAwait(continueOnCapturedContext: false),
                },
            }
        );

        try { await server.RunAsync(cancellationToken: lifetime.Token).ConfigureAwait(continueOnCapturedContext: false); } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        if (bounded.Failure is { } failure) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(source: failure).Throw(); }
    }
}
