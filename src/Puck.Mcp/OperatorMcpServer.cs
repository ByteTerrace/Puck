using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using Puck.Hosting;
using Puck.Networking;

namespace Puck.Mcp;

/// <summary>Official-SDK stdio adapter for an explicitly trusted local Operator. World owns its own lifetime.</summary>
public static class OperatorMcpServer {
    internal const string StatusMeanings = "status is completed when the command returned output, submitted when it returned none (accepted, which does not certify authoritative application), refused when the command or adapter reported an error, and unknown when the attachment closed or the deadline passed after dispatch; inspect state before retrying an unknown call. Mutation commands that report an authority settlement wait for its applied or refused verdict in output. clearTranscript is true when the command asked the console to clear its transcript. truncated is true when the output was longer than the 1048576-character transport limit: output is then its head, ending in a marker naming the full length, under the command's own status; narrow the command to read the rest.";

    private static readonly JsonElement ExecInput = JsonElement.Parse("""{"type":"object","properties":{"command":{"type":"string","minLength":1,"maxLength":8192,"description":"One console line, exactly as typed at the Puck console."},"timeoutMs":{"type":"integer","minimum":1,"maximum":120000,"default":30000,"description":"Milliseconds to wait for the result. When it passes, the attachment closes and the outcome is reported as unknown."}},"required":["command"],"additionalProperties":false}""");
    private static readonly JsonElement CaptureInput = JsonElement.Parse("""{"type":"object","properties":{"timeoutMs":{"type":"integer","minimum":1,"maximum":120000,"default":30000,"description":"Milliseconds to wait for the frame. When it passes, the attachment closes and the outcome is reported as unknown."},"mode":{"type":"string","enum":["settled","presented"],"default":"settled","description":"settled: one clean still; the capture holds presentation and temporal views converge over the held frame, so it hides frame-to-frame defects. presented: what the display actually showed; nothing is held or settled, and each frame is read back as composed for the display, with its jitter, temporal history and motion. Use presented for flicker, ghosting and trails."},"frames":{"type":"integer","minimum":1,"maximum":120,"default":1,"description":"presented only: the consecutive presented frames to read. One frame returns its PNG inline. More than one writes each PNG to a directory on the World's machine and returns, instead of images, a report: per frame its path, presentation index, the presentation its image was rendered on (earlier means the display repeated an image), its tick, and against the previous frame the changed-pixel count and the mean and largest absolute Rec. 709 luma change; then a summary line. Read only the frames the report flags."},"region":{"type":"object","description":"presented only: the rectangle the report's differences are measured over, in fractions of the frame (origin top-left), to isolate one screen or object; omitted, the whole frame.","properties":{"x":{"type":"number","minimum":0,"maximum":1},"y":{"type":"number","minimum":0,"maximum":1},"width":{"type":"number","exclusiveMinimum":0,"maximum":1},"height":{"type":"number","exclusiveMinimum":0,"maximum":1}},"required":["x","y","width","height"],"additionalProperties":false}},"additionalProperties":false}""");

    private static readonly JsonElement WorldsInput = JsonElement.Parse("""{"type":"object","properties":{},"additionalProperties":false}""");
    private static readonly JsonElement WorldsOutput = JsonElement.Parse("""{"type":"object","properties":{"worlds":{"type":"array","items":{"type":"object","properties":{"attachmentId":{"type":"string","description":"Names this World in a call's world argument."},"processId":{"type":"integer","description":"Also names this World in a call's world argument."},"document":{"type":["string","null"],"description":"The world document the World booted from."},"startedUtc":{"type":"string"},"current":{"type":"boolean","description":"True for the World a call that names none runs on."}},"required":["attachmentId","processId","document","startedUtc","current"],"additionalProperties":false}}},"required":["worlds"],"additionalProperties":false}""");

    /// <summary>The output schema every console-backed tool's structured result satisfies: <c>puck_exec</c>,
    /// <c>puck_capture_frame</c>, and <c>puck_state_vector_write</c>, locally and over HTTP.</summary>
    internal static readonly JsonElement ResultSchema = JsonElement.Parse("""{"type":"object","properties":{"requestId":{"type":["string","null"]},"status":{"type":"string","enum":["completed","submitted","refused","unknown"]},"output":{"type":"string"},"isError":{"type":"boolean"},"clearTranscript":{"type":"boolean"},"truncated":{"type":"boolean"},"world":{"type":["object","null"],"description":"The World the call reached, as puck_worlds lists it; null when none was reached.","properties":{"attachmentId":{"type":"string"},"processId":{"type":"integer"},"document":{"type":["string","null"]},"startedUtc":{"type":"string"}},"required":["attachmentId","processId","document","startedUtc"],"additionalProperties":false}},"required":["requestId","status","output","isError","clearTranscript","truncated","world"],"additionalProperties":false}""");

    /// <summary>Runs one tool call on an attachment.</summary>
    /// <param name="client">The attachment the call runs on.</param>
    /// <param name="parameters">The call's name and arguments.</param>
    /// <param name="clock">Drives the call's deadline.</param>
    /// <param name="token">Cancels the call.</param>
    /// <param name="requestId">The control request id for this attachment's next request.</param>
    /// <param name="world">The World the attachment reaches, reported in the result; <see langword="null"/> for an
    /// attachment that has none to name.</param>
    /// <param name="selectsWorld">Whether the call may carry the <c>world</c> selector, which the caller has already
    /// resolved to <paramref name="client"/>; a remote call may not.</param>
    internal static async ValueTask<CallToolResult> CallAsync(IControlSession client, CallToolRequestParams? parameters, TimeProvider clock, CancellationToken token, long requestId = 1, OperatorMcpWorld? world = null, bool selectsWorld = false) {
        CallToolResult Fail(string message, bool unknown = true) => Error(message, unknown, world);

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
        var capture = (parameters.Name == "puck_capture_frame");
        var timeout = 30_000;
        string? command = null;
        string? row = null;
        string? key = null;
        string? vector = null;
        string? mode = null;
        int? frames = null;
        double[]? region = null;

        if (parameters.Arguments is { } arguments) {
            foreach (var (argumentKey, value) in arguments) {
                if (
                    (argumentKey == "timeoutMs") &&
                    (value.ValueKind == JsonValueKind.Number) &&
                    value.TryGetInt32(value: out var parsed) &&
                    (parsed is >= 1 and <= ControlLimits.TimeoutMilliseconds)
                ) { timeout = parsed; } else if (
                    (argumentKey == "world") &&
                    selectsWorld &&
                    (value.ValueKind == JsonValueKind.String)
                ) { } else if (
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
                ) { vector = value.GetString(); } else if (
                    (argumentKey == "mode") &&
                    capture &&
                    (value.ValueKind == JsonValueKind.String)
                ) { mode = value.GetString(); } else if (
                    (argumentKey == "frames") &&
                    capture &&
                    (value.ValueKind == JsonValueKind.Number) &&
                    value.TryGetInt32(value: out var parsedFrames)
                ) { frames = parsedFrames; } else if (
                    (argumentKey == "region") &&
                    capture &&
                    (RegionOf(value: value) is { } parsedRegion)
                ) { region = parsedRegion; } else {
                    return Fail(
                    $"Invalid argument: {argumentKey}.",
                    unknown: false
                );
                }
            }
        }
        if (vectorWrite) {
            if (string.IsNullOrWhiteSpace(value: row)) {
                return Fail(
                    "Invalid argument: row.",
                    unknown: false
                );
            }
            if (string.IsNullOrWhiteSpace(value: vector)) {
                return Fail(
                    "Invalid argument: vector.",
                    unknown: false
                );
            }
            var cellKey = (string.IsNullOrEmpty(value: key) ? "$value" : key);

            command = $"world.state.cell.set {row} {cellKey} {vector}";
        }

        var captureOptions = (((mode ?? (((frames is not null) || (region is not null)) ? ControlCapture.Presented : null)) is { } named)
            ? new ControlCapture(Frames: (frames ?? 1), Mode: named, Region: region)
            : null);

        if (LocalControlServer.Validate(request: new(
            Capture: captureOptions,
            Command: command,
            Id: 1,
            Operation: ((exec || vectorWrite)
            ? "exec"
            : "capture"),
            TimeoutMilliseconds: timeout
        )) is { } refusal) {
            return Fail(
            refusal.Output,
            unknown: false
        );
        }
        try {
            var request = new ControlRequest(
                Capture: captureOptions,
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
            // An in-process session's long output is cut to the transport limit as the control server cuts it, its
            // status kept.
            var result = (await operation.WaitAsync(cancellationToken: deadline.Token).ConfigureAwait(continueOnCapturedContext: false))?.Bounded();

            if (
                (result is null) ||
                ((client is not LocalControlClient) && !result.IsValidFor(request: request))
            ) { client.Dispose(); return Fail("Host returned an invalid result; inspect state before retrying."); }
            return Result(
                ((result.Id == 0)
                ? null
                : result.Id.ToString(provider: CultureInfo.InvariantCulture)),
                result.Status,
                result.Output,
                result.IsError,
                result.ClearTranscript,
                result.Png,
                result.Truncated,
                world
            );
        } catch (Exception error) when (((error is TimeoutException) || ((error is OperationCanceledException) && !token.IsCancellationRequested))) {
            client.Dispose();
            return Fail("Deadline expired. Attachment closed; dispatched outcome is unknown. Inspect state before retrying; the next call attaches anew.");
        } catch (OperationCanceledException) {
            client.Dispose();
            throw;
        } catch (Exception error) when ((error is IOException or InvalidDataException or System.Net.Sockets.SocketException or ObjectDisposedException or JsonException)) {
            return Fail($"Attachment closed; dispatched outcome may be unknown. Inspect state before retrying; the next call attaches anew. {error.Message}");
        }
    }
    /// <summary>Describes a World's attachment file for a result.</summary>
    /// <param name="info">What the World published.</param>
    /// <returns>The World as a result's <c>world</c> member and a listing entry carry it.</returns>
    internal static OperatorMcpWorld Describe(LocalEndpointInfo info) => new(
        AttachmentId: info.Id,
        Document: info.World,
        ProcessId: info.ProcessId,
        StartedUtc: info.StartedUtc
    );
    internal static Tool WorldsTool() => new() {
        Name = "puck_worlds",
        Description = "List the running Worlds that accept an Operator attachment through this adapter: each one's attachmentId, processId, world document, startedUtc and whether it is the adapter's current World, which a call that names none runs on. Name a World in puck_exec, puck_capture_frame or puck_state_vector_write with their world argument. A World whose process has exited is never listed. Takes no arguments and attaches to nothing.",
        InputSchema = WorldsInput,
        OutputSchema = WorldsOutput,
        Annotations = new() { DestructiveHint = false, IdempotentHint = true, OpenWorldHint = false, ReadOnlyHint = true },
    };
    internal static Tool WithWorldSelector(Tool source) {
        var schema = JsonNode.Parse(json: source.InputSchema.GetRawText())!.AsObject();

        schema["properties"]!.AsObject()["world"] = new JsonObject {
            ["type"] = "string",
            ["minLength"] = 1,
            ["description"] = "Optional. The attachmentId or processId of the running World this call must reach, from puck_worlds. The call attaches to exactly that World or is refused by name; it never falls back to another. Omitted, it runs on the adapter's current World, which the result's world member names.",
        };

        return new() {
            Annotations = source.Annotations,
            Description = source.Description + " Optional world selects which running World the call reaches.",
            InputSchema = JsonElement.Parse(json: schema.ToJsonString()),
            Name = source.Name,
            OutputSchema = source.OutputSchema,
        };
    }
    // The region argument's x, y, width and height, or null when it is not an object of four numbers; the control
    // request's validation judges the values.
    private static double[]? RegionOf(JsonElement value) {
        if (
            (value.ValueKind != JsonValueKind.Object) ||
            !value.TryGetProperty(propertyName: "x", value: out var x) || (x.ValueKind != JsonValueKind.Number) ||
            !value.TryGetProperty(propertyName: "y", value: out var y) || (y.ValueKind != JsonValueKind.Number) ||
            !value.TryGetProperty(propertyName: "width", value: out var width) || (width.ValueKind != JsonValueKind.Number) ||
            !value.TryGetProperty(propertyName: "height", value: out var height) || (height.ValueKind != JsonValueKind.Number) ||
            (value.EnumerateObject().Count() != 4)
        ) {
            return null;
        }

        return [x.GetDouble(), y.GetDouble(), width.GetDouble(), height.GetDouble()];
    }
    internal static Tool CaptureTool() => new() {
        Name = "puck_capture_frame",
        Description = "Capture the composed frame, including overlays, ordered after this attachment's prior commands and waits. mode settled (the default) is a clean still: the capture holds presentation from the frame it is armed in until it is served, the World keeps rendering that held frame with no presentation time passing, temporal views converge over it, and the PNG is a render of it, so it cannot show flicker, ghosting or trails. mode presented reads what the display actually showed, holding and settling nothing: frames 1 returns that frame's PNG inline; frames 2..120 reads that many consecutive presented frames, writes their PNGs on the World's machine and returns a per-frame report (paths, presentation indices so gaps and repeated images show, ticks, changed pixels and luma change against the previous frame, optionally over a region) instead of images. A still scene whose presented frames change is flicker; read the flagged PNGs to see it. External desktop and camera screens show their capture fill while any capture is armed. PNGs up to 16 MiB. Requires a renderer; busy and capture failures are tool errors. No exact-tick guarantee.",
        InputSchema = CaptureInput,
        OutputSchema = ResultSchema,
        Annotations = new() { DestructiveHint = false, IdempotentHint = false, OpenWorldHint = false, ReadOnlyHint = true },
    };
    internal static CallToolResult Error(string message, bool unknown = true, OperatorMcpWorld? world = null) => Result(
        clearTranscript: false,
        isError: true,
        output: message,
        png: null,
        requestId: null,
        status: (unknown
        ? "unknown"
        : "refused"),
        truncated: false,
        world: world
    );
    internal static Tool ExecTool() => new() {
        Name = "puck_exec",
        Description = ("Execute one Puck console line as the explicitly trusted Operator, with the full console registry, ordinary validation and the Operator's authority. The line must be one nonblank, non-comment line; batches are refused. help --names lists every console verb by name, help <prefix> describes the verbs whose names start with the prefix (help world.state), and help alone describes them all. " + StatusMeanings),
        InputSchema = ExecInput,
        OutputSchema = ResultSchema,
        Annotations = new() { DestructiveHint = true, IdempotentHint = false, OpenWorldHint = true, ReadOnlyHint = false },
    };

    private static CallToolResult ListWorlds(IDictionary<string, JsonElement>? arguments, OperatorAttachment attachment) {
        if (arguments is { Count: > 0 }) {
            return Error(
                "Invalid argument: puck_worlds takes none.",
                unknown: false
            );
        }

        var listed = new OperatorMcpWorldList(Worlds: [.. attachment.List().Select(selector: entry => new OperatorMcpListedWorld(
            AttachmentId: entry.Info.Id,
            Current: entry.Current,
            Document: entry.Info.World,
            ProcessId: entry.Info.ProcessId,
            StartedUtc: entry.Info.StartedUtc
        ))]);
        var metadata = JsonSerializer.SerializeToElement(
            listed,
            OperatorMcpJson.Default.OperatorMcpWorldList
        );

        return new() {
            Content = [new TextContentBlock { Text = metadata.GetRawText() }],
            IsError = false,
            StructuredContent = metadata,
        };
    }
    private static (string? Selector, CallToolResult? Invalid) SelectedWorld(CallToolRequestParams? parameters) {
        if (
            (parameters?.Arguments is { } arguments) &&
            arguments.TryGetValue(key: "world", value: out var value)
        ) {
            return (((value.ValueKind == JsonValueKind.String) && !string.IsNullOrWhiteSpace(value: value.GetString()))
                ? (value.GetString()!.Trim(), null)
                : (null, Error(
                    "Invalid argument: world. Give an attachmentId or processId from puck_worlds.",
                    unknown: false
                )));
        }

        return (null, null);
    }
    private static CallToolResult Result(string? requestId, string status, string output, bool isError, bool clearTranscript, byte[]? png, bool truncated, OperatorMcpWorld? world = null) {
        var metadata = JsonSerializer.SerializeToElement(
            new OperatorMcpResultMetadata(
                ClearTranscript: clearTranscript,
                IsError: isError,
                Output: output,
                RequestId: requestId,
                Status: status,
                Truncated: truncated,
                World: world
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
                ServerInstructions = "Trusted local Operator: full Puck Console authority; participant and remote access are not provided. Each call attaches to a running World on demand; with no World it is refused and nothing runs. Several Worlds can run at once and this adapter serves every caller: puck_worlds lists them, and the optional world argument of puck_exec, puck_capture_frame and puck_state_vector_write names the one a call must reach, by attachment id or process id. A named call reaches exactly that World or is refused, never another. A call that names none runs on the adapter's current World, which may be another caller's; every result's world member says which World it reached, so name your own World. The adapter runs one call at a time, so issue calls serially when their order matters: a capture is ordered after the commands before it. A submitted result is not an authoritative mutation receipt. Cancellation, timeout or a lost World closes the attachment and reports an unknown outcome; the next call attaches anew, possibly to a restarted World with fresh state, so inspect state before any retry.",
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
                    ListToolsHandler = (_, _) => ValueTask.FromResult(result: new ListToolsResult { Tools = [WorldsTool(), WithWorldSelector(source: ExecTool()), WithWorldSelector(source: CaptureTool()), WithWorldSelector(source: RemoteMcpHost.StateVectorWriteTool)] }),
                    CallToolHandler = async (context, token) => {
                        if (context.Params?.Name == "puck_worlds") {
                            return ListWorlds(
                                arguments: context.Params.Arguments,
                                attachment: attachment
                            );
                        }

                        var (selector, invalid) = SelectedWorld(parameters: context.Params);

                        if (invalid is not null) {
                            return invalid;
                        }

                        return await attachment.RunAsync(
                            world: selector,
                            call: (client, callToken) => CallAsync(
                                client,
                                context.Params,
                                clock,
                                callToken,
                                world: Describe(info: client.Endpoint),
                                selectsWorld: true
                            ).AsTask(),
                            refuse: refusal => Error(
                                refusal,
                                unknown: false
                            ),
                            cancellationToken: token
                        ).ConfigureAwait(continueOnCapturedContext: false);
                    },
                },
            }
        );

        try { await server.RunAsync(cancellationToken: lifetime.Token).ConfigureAwait(continueOnCapturedContext: false); } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        if (bounded.Failure is { } failure) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(source: failure).Throw(); }
    }
}
