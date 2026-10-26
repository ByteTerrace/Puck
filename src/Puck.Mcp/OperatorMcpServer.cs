using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using Puck.Hosting;
using Puck.Networking;

namespace Puck.Mcp;

/// <summary>Official-SDK stdio adapter for an explicitly trusted local Operator; the session owns Worlds it starts.</summary>
public static class OperatorMcpServer {
    internal const string Instructions = "Trusted local Operator. Activated builds take effect on the next call; started Worlds and pending starts stay owned by this session across updates. Name your World in every call and inspect state after an unknown outcome before retrying.";
    internal const string StatusMeanings = "status is completed (returned output or data), submitted (accepted with no output; not an authoritative mutation receipt), refused (error), or unknown (attachment closed or deadline expired after dispatch; inspect state before retrying). truncated marks output cut at the 1048576-character limit. Native JSON returns in data instead of output.";
    internal const string TimeoutMsProperty = """{"type":"integer","minimum":1,"maximum":120000,"default":30000,"description":"Deadline in milliseconds; expiry closes the attachment and reports status unknown."}""";

    private const string WorldProperty = """{"type":"object","properties":{"attachmentId":{"type":"string"},"processId":{"type":"integer"},"document":{"type":"string"},"startedUtc":{"type":"string"}},"required":["attachmentId","processId","startedUtc"],"additionalProperties":false}""";

    private static readonly JsonElement EmptyInput = JsonElement.Parse("""{"type":"object","properties":{},"additionalProperties":false}""");
    private static readonly JsonElement ExecInput = JsonElement.Parse($$$$$"""{"type":"object","properties":{"command":{"type":"string","minLength":1,"maxLength":8192,"description":"Console line or newline-separated script (at most 64 nonblank, noncomment lines)."},"timeoutMs":{{{{{TimeoutMsProperty}}}}}},"required":["command"],"additionalProperties":false}""");
    private static readonly JsonElement CaptureInput = JsonElement.Parse($$$$$"""{"type":"object","properties":{"tick":{"type":"integer","minimum":0,"description":"settled only: future or current tick to hold and capture after it completes; omitted captures the last completed tick."},"timeoutMs":{{{{{TimeoutMsProperty}}}}},"mode":{"type":"string","enum":["settled","presented"],"default":"settled","description":"settled holds presentation for a converged still; presented reads live frames as displayed."},"frames":{"type":"integer","minimum":1,"maximum":120,"default":1,"description":"presented only: 1 returns the PNG inline; 2..120 writes PNGs and returns a diff report."},"region":{"type":"object","description":"presented only: normalized top-left rectangle (0..1) for diff metrics.","properties":{"x":{"type":"number","minimum":0,"maximum":1},"y":{"type":"number","minimum":0,"maximum":1},"width":{"type":"number","exclusiveMinimum":0,"maximum":1},"height":{"type":"number","exclusiveMinimum":0,"maximum":1}},"required":["x","y","width","height"],"additionalProperties":false}},"additionalProperties":false}""");
    private static readonly JsonElement WorldsOutput = JsonElement.Parse("""{"type":"object","properties":{"worlds":{"type":"array","items":{"type":"object","properties":{"attachmentId":{"type":"string"},"processId":{"type":"integer"},"document":{"type":"string"},"startedUtc":{"type":"string"},"current":{"type":"boolean","default":false},"started":{"type":"boolean","default":false}},"required":["attachmentId","processId","startedUtc"],"additionalProperties":false}}},"required":["worlds"],"additionalProperties":false}""");
    private static readonly JsonElement StartInput = JsonElement.Parse("""{"type":"object","properties":{"document":{"type":"string","minLength":1,"default":"worlds/debug","description":"World package, .puck source, or JSON under the checkout's worlds/ directory."},"checkout":{"type":"string","minLength":1,"description":"Absolute path to the adapter's checkout (default) or a worktree of its repository."},"backend":{"type":"string","enum":["directx","vulkan"],"description":"Graphics backend (--backend); vulkan builds SPIR-V."},"windowed":{"type":"boolean","default":false,"description":"Boot windowed under the GPU lease instead of headless."},"ownerPlay":{"type":"boolean","default":false,"description":"Open physical input devices (--devices all) when the owner asked to play."},"holder":{"type":"string","minLength":1,"default":"puck-mcp","description":"GPU lease holder name when windowed."},"timeoutSeconds":{"type":"integer","minimum":1,"maximum":1800,"default":900,"description":"Deadline in seconds covering build, GPU lease, and attachment."}},"additionalProperties":false}""");
    private static readonly JsonElement StopInput = JsonElement.Parse("""{"type":"object","properties":{"world":{"type":"string","minLength":1,"description":"The started World's processId or attachmentId."},"timeoutMs":{"type":"integer","minimum":1,"maximum":120000,"default":30000,"description":"Grace period in milliseconds before killing the process tree."}},"required":["world"],"additionalProperties":false}""");
    private static readonly JsonElement RunOutput = JsonElement.Parse($$$$$"""{"type":"object","properties":{"startId":{"type":"string"},"status":{"type":"string","enum":["pending","started","stopped","refused"]},"output":{"type":"string"},"isError":{"type":"boolean","default":false},"world":{{{{{WorldProperty}}}}},"processId":{"type":"integer"},"checkout":{"type":"string"},"build":{"type":"string"},"stateDirectory":{"type":"string"},"stdoutLog":{"type":"string"},"stderrLog":{"type":"string"},"exitCode":{"type":"integer"},"killed":{"type":"boolean","default":false},"leaseReleased":{"type":"boolean","default":false}},"required":["status"],"additionalProperties":false}""");

    /// <summary>The output schema every console-backed tool's structured result satisfies: <c>puck_exec</c>,
    /// <c>puck_capture_frame</c>, and <c>puck_state_vector_write</c>, locally and over HTTP.</summary>
    internal static readonly JsonElement ResultSchema = JsonElement.Parse($$$$$"""{"type":"object","properties":{"requestId":{"type":"string"},"status":{"type":"string","enum":["completed","submitted","refused","unknown"]},"output":{"type":"string"},"isError":{"type":"boolean","default":false},"clearTranscript":{"type":"boolean","default":false},"truncated":{"type":"boolean","default":false},"world":{{{{{WorldProperty}}}}},"data":{"type":["object","array"]},"commands":{"type":"array","items":{"type":"object","properties":{"line":{"type":"integer"},"command":{"type":"string"},"status":{"type":"string","enum":["completed","submitted","refused"]},"output":{"type":"string"},"isError":{"type":"boolean","default":false},"clearTranscript":{"type":"boolean","default":false},"truncated":{"type":"boolean","default":false},"data":{"type":["object","array"]}},"required":["line","command","status"],"additionalProperties":false}}},"required":["status"],"additionalProperties":false}""");

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
        CallToolResult Fail(string message, bool unknown = true) => Error(message: message, unknown: unknown, world: world);

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
        ulong? tick = null;

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
                ) { region = parsedRegion; } else if (
                    (argumentKey == "tick") &&
                    capture &&
                    (value.ValueKind == JsonValueKind.Number) &&
                    value.TryGetUInt64(value: out var parsedTick)
                ) { tick = parsedTick; } else {
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

        var captureOptions = (((mode ?? (((frames is not null) || (region is not null)) ? ControlCapture.Presented : ((tick is not null) ? ControlCapture.Settled : null))) is { } named)
            ? new ControlCapture(Frames: (frames ?? 1), Mode: named, Region: region, Tick: tick)
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
                world,
                result.Commands
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
        Description = "List running Worlds that accept an Operator attachment: attachmentId, processId, document, startedUtc, current (default target when world is omitted) and started (owned by this adapter and stoppable with puck_world_stop).",
        InputSchema = EmptyInput,
        OutputSchema = WorldsOutput,
        Annotations = new() { DestructiveHint = false, IdempotentHint = true, OpenWorldHint = false, ReadOnlyHint = true },
    };
    internal static Tool StartTool() => new() {
        Name = "puck_world_start",
        Description = "Start a World from a checkout's current src/Puck.World Release build and return pending with startId; poll puck_world_start_wait with startId (do not repeat puck_world_start) and stop a started World with puck_world_stop. Boots headless with --devices none, its own state directory and --control unless windowed or ownerPlay is set; a windowed World holds the GPU lease until exit. Jobs survive across calls and adapter updates until timeoutSeconds, cancellation, or session exit.",
        InputSchema = StartInput,
        OutputSchema = RunOutput,
        Annotations = new() { DestructiveHint = false, IdempotentHint = false, OpenWorldHint = true, ReadOnlyHint = false },
    };
    internal static Tool StartWaitTool() => new() {
        Name = "puck_world_start_wait",
        Description = "Wait up to 20 seconds for a pending start in this session (up to 64 retained). Poll the same startId while pending; cancel stops an unfinished start and any World it launched.",
        InputSchema = JsonElement.Parse("""{"type":"object","properties":{"startId":{"type":"string","minLength":1},"timeoutMs":{"type":"integer","minimum":1,"maximum":20000,"default":20000},"cancel":{"type":"boolean","default":false}},"required":["startId"],"additionalProperties":false}"""),
        OutputSchema = RunOutput,
        Annotations = new() { DestructiveHint = true, IdempotentHint = true, OpenWorldHint = false, ReadOnlyHint = false },
    };
    internal static Tool StopTool() => new() {
        Name = "puck_world_stop",
        Description = "Stop a World this adapter started with puck_world_start: send quit, kill its process tree after timeoutMs, release its GPU lease, and return exitCode and log paths. Foreign Worlds are refused and untouched.",
        InputSchema = StopInput,
        OutputSchema = RunOutput,
        Annotations = new() { DestructiveHint = true, IdempotentHint = false, OpenWorldHint = false, ReadOnlyHint = false },
    };
    internal static Tool WithWorldSelector(Tool source) {
        var schema = JsonNode.Parse(json: source.InputSchema.GetRawText())!.AsObject();

        schema["properties"]!.AsObject()["world"] = new JsonObject {
            ["type"] = "string",
            ["minLength"] = 1,
            ["description"] = "Target World attachmentId or processId from puck_worlds (refused if absent; never falls back). Omitted uses the adapter's current World.",
        };

        return new() {
            Annotations = source.Annotations,
            Description = source.Description,
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
        Description = "Capture the composed frame (with overlays, up to 16 MiB PNG) ordered after prior commands on this attachment. mode settled (default) holds simulation and presentation after the target tick so temporal views converge into a clean still (reporting tick=<T> hash=<state> pose=<pose> in output), hiding frame-to-frame defects. mode presented captures live display output without holding: frames=1 returns the PNG inline; frames=2..120 writes PNGs on the host and returns a per-frame diff and luma report (with presentation indices, ticks, and optional region) to diagnose flicker, ghosting, or trails. External desktop/camera screens show capture fill while armed; see help world.screenshot and help world.lockstep.",
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
        Description = ("Execute a Puck console line or newline-separated script (up to 64 nonblank, noncomment lines and 8192 characters) as the local Operator. Lines queue together on one session and settle in order, so successive simulation commands apply on adjacent ticks and world.wait holds only this script; scripts continue after refusals and report per-line results in commands. Use help (--names, --full, or <prefix>), help world.at, and help world.screenshot to discover commands. " + StatusMeanings),
        InputSchema = ExecInput,
        OutputSchema = ResultSchema,
        Annotations = new() { DestructiveHint = true, IdempotentHint = false, OpenWorldHint = true, ReadOnlyHint = false },
    };

    private static CallToolResult ListWorlds(IDictionary<string, JsonElement>? arguments, OperatorAttachment attachment, OperatorWorlds worlds) {
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
            Started: worlds.Owns(processId: entry.Info.ProcessId),
            StartedUtc: entry.Info.StartedUtc
        ))]);
        var metadata = JsonSerializer.SerializeToElement(
            listed,
            OperatorMcpJson.Default.OperatorMcpWorldList
        );
        var text = ((listed.Worlds.Length == 0)
            ? "0 worlds"
            : string.Join(separator: '\n', values: listed.Worlds.Select(selector: static world => $"{world.ProcessId} {world.AttachmentId}")));

        return new() {
            Content = [new TextContentBlock { Text = text }],
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
    private static JsonElement? DataOf(string output, bool truncated) {
        if (truncated || (output.AsSpan().TrimStart() is not [('{' or '['), ..])) { return null; }
        try {
            using var json = JsonDocument.Parse(output);

            return json.RootElement.Clone();
        } catch (JsonException) { return null; }
    }
    private static CallToolResult Result(string? requestId, string status, string output, bool isError, bool clearTranscript, byte[]? png, bool truncated, OperatorMcpWorld? world = null, ControlCommandResult[]? commands = null) {
        var single = (commands is null);
        var data = (single ? DataOf(output: output, truncated: truncated) : null);
        var metadata = JsonSerializer.SerializeToElement(
            new OperatorMcpResultMetadata(
                ClearTranscript: clearTranscript,
                IsError: isError,
                Output: ((single && (data is null) && !string.IsNullOrEmpty(value: output)) ? output : null),
                RequestId: requestId,
                Status: status,
                Truncated: truncated,
                World: world,
                Data: data,
                Commands: commands?.Select(selector: static line => {
                    var lineData = DataOf(output: line.Output, truncated: line.Truncated);

                    return new OperatorMcpCommandResult(
                        line.Line,
                        line.Command,
                        line.Status,
                        (((lineData is null) && !string.IsNullOrEmpty(value: line.Output)) ? line.Output : null),
                        line.IsError,
                        line.ClearTranscript,
                        line.Truncated,
                        lineData
                    );
                }).ToArray()
            ),
            OperatorMcpJson.Default.OperatorMcpResultMetadata
        );
        List<ContentBlock> content = [new TextContentBlock { Text = ((isError && !string.IsNullOrEmpty(value: output)) ? output : status) }];

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
    /// <param name="launch">The checkout and GPU lease <c>puck_world_start</c> boots Worlds with, or
    /// <see langword="null"/> for an adapter outside any checkout, whose start is refused. Every World the adapter
    /// started is stopped when it shuts down.</param>
    public static async Task RunAsync(string target, Stream input, Stream output, CancellationToken cancellationToken = default, TimeProvider? clock = null, OperatorWorldLaunch? launch = null) {
        ArgumentException.ThrowIfNullOrEmpty(target);
        clock ??= TimeProvider.System;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token: cancellationToken);
        using var attachment = new OperatorAttachment(
            clock: clock,
            target: target
        );
        // Disposed before the attachments, so a World this adapter started is asked to quit while it is still attached.
        using var worlds = new OperatorWorlds(clock: clock, launch: launch);
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
                ServerInstructions = Instructions,
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
                    ListToolsHandler = (_, _) => ValueTask.FromResult(result: new ListToolsResult { Tools = [new Tool { Name = "puck_adapter", Description = "Report the serving adapter process, moduleId, directory, live owned Worlds and pending starts.", Annotations = new() { DestructiveHint = false, IdempotentHint = true, OpenWorldHint = false, ReadOnlyHint = true }, InputSchema = EmptyInput, OutputSchema = JsonElement.Parse("""{"type":"object","properties":{"processId":{"type":"integer"},"moduleId":{"type":"string"},"directory":{"type":"string"},"ownedWorlds":{"type":"array","items":{"type":"object","properties":{"processId":{"type":"integer"},"attachmentId":{"type":["string","null"]}},"required":["processId","attachmentId"],"additionalProperties":false}},"pendingStarts":{"type":"array","items":{"type":"string"}}},"required":["processId","moduleId","directory","ownedWorlds","pendingStarts"],"additionalProperties":false}""") }, WorldsTool(), StartTool(), StartWaitTool(), StopTool(), WithWorldSelector(source: ExecTool()), WithWorldSelector(source: CaptureTool()), WithWorldSelector(source: RemoteMcpHost.StateVectorWriteTool)] }),
                    CallToolHandler = async (context, token) => {
                        if (context.Params?.Name == "puck_adapter") {
                            if (context.Params.Arguments is { Count: > 0 }) { return Error("puck_adapter takes no arguments.", false); }
                            var moduleId = typeof(OperatorMcpServer).Module.ModuleVersionId.ToString();
                            var metadata = worlds.Ownership();

                            metadata["processId"] = Environment.ProcessId;
                            metadata["moduleId"] = moduleId;
                            metadata["directory"] = AppContext.BaseDirectory.Replace(newChar: '/', oldChar: '\\');
                            var data = JsonElement.Parse(metadata.ToJsonString());

                            return new() { Content = [new TextContentBlock { Text = $"{Environment.ProcessId} {moduleId}" }], StructuredContent = data };
                        }
                        if (context.Params?.Name == "puck_worlds") {
                            return ListWorlds(
                                arguments: context.Params.Arguments,
                                attachment: attachment,
                                worlds: worlds
                            );
                        }
                        if (context.Params?.Name == "puck_world_start") {
                            return await worlds.StartAsync(arguments: context.Params.Arguments, attachment: attachment, cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);
                        }
                        if (context.Params?.Name == "puck_world_start_wait") {
                            return await worlds.WaitStartAsync(arguments: context.Params.Arguments, cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);
                        }
                        if (context.Params?.Name == "puck_world_stop") {
                            return await worlds.StopAsync(arguments: context.Params.Arguments, cancellationToken: token).ConfigureAwait(continueOnCapturedContext: false);
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
