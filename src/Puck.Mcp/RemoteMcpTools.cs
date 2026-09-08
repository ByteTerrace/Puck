using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Puck.Mcp;

internal sealed class RemoteMcpTools(RemoteAttachmentPool attachments, string owner, int idleTimeout, RemoteMcpDiagnostics diagnostics, RemoteMcpHost host, RemoteMcpCaller caller) {
    private static readonly JsonElement DetachInput = JsonElement.Parse("""{"type":"object","properties":{"attachmentId":{"type":"string"}},"required":["attachmentId"],"additionalProperties":false}""");
    private static readonly JsonElement AttachInput = JsonElement.Parse("""{"type":"object","additionalProperties":false}""");
    private static readonly JsonElement AttachmentOutput = JsonElement.Parse("""{"type":"object","properties":{"attachmentId":{"type":["string","null"]},"idleTimeoutSeconds":{"type":"integer"},"output":{"type":"string"},"isError":{"type":"boolean"}},"required":["attachmentId","idleTimeoutSeconds","output","isError"],"additionalProperties":false}""");
    private static readonly JsonElement ExecInput = AttachmentSchema(source: OperatorMcpServer.ExecTool());
    private static readonly JsonElement CaptureInput = AttachmentSchema(source: OperatorMcpServer.CaptureTool());

    internal static ListToolsResult List(RemoteMcpHost host) => new() { Tools = host.SupportsAttachments
        ? [AttachmentTool(detach: false), AttachmentTool(detach: true), WithAttachment(OperatorMcpServer.ExecTool(), ExecInput), WithAttachment(OperatorMcpServer.CaptureTool(), CaptureInput), .. host.ServiceTools]
        : [.. host.ServiceTools] };
    internal async ValueTask<CallToolResult> CallAsync(CallToolRequestParams? parameters, CancellationToken token) {
        var service = parameters is not null && host.ServiceTools.Any(tool => tool.Name == parameters.Name);
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var tool = (service || (parameters?.Name is "puck_attach" or "puck_detach" or "puck_exec" or "puck_capture_frame") ? parameters!.Name : "unknown");
        using var activity = diagnostics.Start(tool: tool);
        var outcome = "failed";
        string? requestId = null;

        try {
            var result = service ? await host.CallServiceAsync(caller, parameters!, token).ConfigureAwait(false) : await DispatchAsync(parameters: parameters, token: token).ConfigureAwait(false);

            outcome = ((result.IsError == true) ? "refused" : "completed");
            if (result.StructuredContent is { } content) {
                if (content.TryGetProperty(propertyName: "status", value: out var status)) { outcome = (status.GetString() ?? outcome); }
                if (content.TryGetProperty(propertyName: "requestId", value: out var id)) { requestId = id.GetString(); }
            }
            return result;
        } catch (OperationCanceledException) { outcome = "cancelled"; throw; } finally { diagnostics.Completed(outcome: outcome, requestId: requestId, start: start, subject: owner, tool: tool); }
    }

    private async ValueTask<CallToolResult> DispatchAsync(CallToolRequestParams? parameters, CancellationToken token) {
        if (!host.SupportsAttachments) { throw new McpProtocolException(errorCode: McpErrorCode.InvalidParams, message: "Unknown tool."); }
        if (parameters?.Name is not ("puck_attach" or "puck_detach" or "puck_exec" or "puck_capture_frame")) { throw new McpProtocolException(errorCode: McpErrorCode.InvalidParams, message: "Unknown tool."); }
        if (parameters.Name == "puck_attach") {
            if (parameters.Arguments is { Count: > 0 }) { return AttachmentResult(error: true, id: null, output: "Attach takes no arguments."); }
            try {
                var id = await attachments.AttachAsync(caller: caller, token: token).ConfigureAwait(continueOnCapturedContext: false);

                return AttachmentResult(error: (id is null), id: id, output: ((id is null) ? "All four attachment slots are occupied." : "Delegated World attachment created. Keep this handle private and call serially."));
            } catch (Exception error) when ((error is IOException or InvalidDataException or UnauthorizedAccessException or System.Net.Sockets.SocketException or InvalidOperationException or JsonException)) {
                return AttachmentResult(error: true, id: null, output: "World attachment unavailable. Check account onboarding, World admission and target readiness.");
            }
        }
        var arguments = ((parameters.Arguments is { } supplied) ? new Dictionary<string, JsonElement>(supplied, StringComparer.Ordinal) : []);

        if (!arguments.Remove(key: "attachmentId", value: out var value) || (value.ValueKind != JsonValueKind.String) || !Guid.TryParseExact(value.GetString(), "N", out var idValue)) {
            return ((parameters.Name == "puck_detach") ? AttachmentResult(error: true, id: null, output: "A valid attachmentId is required.") : OperatorMcpServer.Error(message: "A valid attachmentId is required.", unknown: false));
        }
        var idText = idValue.ToString(format: "N");

        if (parameters.Name == "puck_detach") {
            if (arguments.Count != 0) { return AttachmentResult(error: true, id: null, output: "Detach accepts only attachmentId."); }
            var removed = attachments.Detach(id: idText, owner: owner);

            return AttachmentResult(error: !removed, id: null, output: (removed ? "Attachment closed. Dispatched effects may remain." : "Unknown, closed or expired attachment."));
        }
        return await attachments.CallAsync(owner, idText, new() { Arguments = arguments, Name = parameters.Name }, token).ConfigureAwait(continueOnCapturedContext: false);
    }
    private CallToolResult AttachmentResult(string? id, string output, bool error) {
        var metadata = JsonSerializer.SerializeToElement(new RemoteAttachmentMetadata(AttachmentId: id, IdleTimeoutSeconds: idleTimeout, IsError: error, Output: output), OperatorMcpJson.Default.RemoteAttachmentMetadata);

        return new() { IsError = error, StructuredContent = metadata, Content = [new TextContentBlock { Text = metadata.GetRawText() }] };
    }
    private static Tool AttachmentTool(bool detach) => new() {
        Name = (detach ? "puck_detach" : "puck_attach"),
        Description = (detach ? "Close your delegated attachment and cancel its pending ingress. Already dispatched effects cannot be undone." : "Create an isolated delegated attachment to this gateway's World. Returns a caller-bound attachmentId required for exec and capture. At most four attachments; idle expiry applies. Never automatically reattach and replay uncertain commands."),
        InputSchema = (detach ? DetachInput : AttachInput),
        OutputSchema = AttachmentOutput,
        Annotations = new() { DestructiveHint = detach, IdempotentHint = false, OpenWorldHint = false, ReadOnlyHint = false },
    };
    private static JsonElement AttachmentSchema(Tool source) {
        var schema = JsonNode.Parse(source.InputSchema.GetRawText())!.AsObject();

        schema["properties"]!.AsObject()["attachmentId"] = new JsonObject { ["type"] = "string" };
        var required = (schema["required"]?.AsArray() ?? new JsonArray());

        required.Add(item: ((JsonNode?)JsonValue.Create("attachmentId")));
        if (schema["required"] is null) { schema["required"] = required; }
        return JsonElement.Parse(schema.ToJsonString());
    }
    private static Tool WithAttachment(Tool source, JsonElement schema) {
        source.Description += " Requires your remote attachmentId.";
        source.InputSchema = schema;
        return source;
    }
}
internal readonly record struct RemoteAttachmentMetadata(string? AttachmentId, int IdleTimeoutSeconds, string Output, bool IsError);
