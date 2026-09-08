using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using System.Text.Json.Serialization;

namespace Puck.Mcp;

// One typed value supplies structured content and JSON text without an intermediate mutable DOM.
internal readonly record struct OperatorMcpResultMetadata(string? RequestId, string Status, string Output, bool IsError, bool ClearTranscript);
[JsonSerializable(typeof(OperatorMcpResultMetadata))]
[JsonSerializable(typeof(RemoteMcpOptions))]
[JsonSerializable(typeof(RemoteAttachmentMetadata))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true, AllowDuplicateProperties = false)]
internal sealed partial class OperatorMcpJson : JsonSerializerContext {
    // Lazy SDK JsonNode parameters must be validated before typed dispatch, including malformed Unicode.
    internal static void ValidateParameters(JsonRpcMessage message) {
        if (message is not JsonRpcRequest { Params: { } parameters }) { return; }
        try { _ = parameters.ToJsonString(); } catch (Exception error) when ((error is InvalidOperationException or JsonException)) {
            throw new McpProtocolException(errorCode: McpErrorCode.InvalidParams, message: "Request parameters contain invalid JSON strings.");
        }
    }
}
