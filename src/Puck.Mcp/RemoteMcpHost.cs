using System.Text.Json;
using Puck.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Puck.Mcp;

/// <summary>A composition-owned source of isolated delegated sessions. The caller owns and disposes each returned session.</summary>
public abstract class RemoteMcpHost {
    /// <summary>The tool definition for writing state vectors into admitted vector cells.</summary>
    public static Tool StateVectorWriteTool { get; } = new() {
        Name = "puck_state_vector_write",
        Description = "Writes one vector into a cell of an already-declared vector-kind state row, as the console line `world.state.cell.set <row> <key> <vector>`, under the same authority and with the same result shape as puck_exec. The vector must match the dimension count of the row's vector space. A row that is not live as a vector row does not take a vector write; its own kind reads the token instead. A submitted status means the write was accepted, not that it applied; read the row back to confirm.",
        InputSchema = JsonElement.Parse("""{"type":"object","properties":{"row":{"type":"string","description":"The declared vector-kind state row."},"key":{"type":["string","null"],"description":"The cell key. Omitted, null or empty writes the row's $value cell."},"vector":{"type":"string","description":"Unpadded base64url encoding of the vector's signed 8-bit components: 8 to 1024 of them, none equal to -128, non-zero and near unit length."},"timeoutMs":{"type":"integer","minimum":1,"maximum":120000,"default":30000,"description":"Milliseconds to wait for the result. When it passes, the attachment closes and the outcome is reported as unknown."}},"required":["row","vector"],"additionalProperties":false}"""),
        OutputSchema = OperatorMcpServer.ResultSchema,
        Annotations = new() { DestructiveHint = true, IdempotentHint = false, OpenWorldHint = true, ReadOnlyHint = false },
    };
    /// <summary>Whether the host is accepting new attachments; health checks never consume a session slot.</summary>
    public abstract bool IsReady { get; }
    /// <summary>Additional explicitly installed service tools. The host must enforce caller-specific grants at dispatch.</summary>
    public virtual IReadOnlyList<Tool> ServiceTools => [];
    /// <summary>Whether this composition exposes stateful Console attachments. Service-only hosts return false.</summary>
    public virtual bool SupportsAttachments => true;

    /// <summary>Decides one tool call's delegated authorization before the MCP response can start, performing any token
    /// exchange that can yield a user challenge. It runs for every <c>tools/call</c> after the caller's bearer token is
    /// validated and before the request reaches the MCP dispatcher.</summary>
    /// <remarks>Throw <see cref="RemoteMcpAuthorizationException"/> here to answer this HTTP request with a bearer
    /// challenge. <see cref="AttachAsync"/> and <see cref="CallServiceAsync"/> run after the response may have started
    /// streaming: a <see cref="RemoteMcpAuthorizationException"/> they throw, when a downstream service rejects the
    /// delegated token after dispatch, fails that call and challenges the caller's next request instead.</remarks>
    /// <param name="caller">The validated caller.</param>
    /// <param name="request">The call about to be dispatched; the host decides by its name and arguments.</param>
    /// <param name="cancellationToken">Request disconnect, token expiry, grant revocation, or shutdown.</param>
    /// <returns>Host-owned state for this call, such as an exchanged downstream token, which the dispatched call reads
    /// from <see cref="RemoteMcpCaller.Authorization"/>; <see langword="null"/> when the call needs none.</returns>
    public virtual ValueTask<object?> AuthorizeAsync(RemoteMcpCaller caller, CallToolRequestParams request, CancellationToken cancellationToken) => ValueTask.FromResult<object?>(result: null);
    /// <summary>Creates a session for an already authorized subject without replaying prior work.</summary>
    /// <param name="caller">The validated caller. The host must preserve issuer and subject when creating ingress; assertions remain request-confined.</param>
    /// <param name="cancellationToken">Cancels attachment creation.</param>
    /// <returns>A new independently ordered session.</returns>
    /// <exception cref="RemoteMcpAuthorizationException">A downstream service rejected the delegated token; the caller's
    /// next request is challenged.</exception>
    public abstract ValueTask<IControlSession> AttachAsync(RemoteMcpCaller caller, CancellationToken cancellationToken);
    /// <summary>Runs an installed service operation under this request's authenticated identity and deadline.</summary>
    /// <param name="caller">Validated identity and request-confined delegation assertion.</param>
    /// <param name="request">A call to a name advertised in ServiceTools.</param>
    /// <param name="cancellationToken">Request disconnect, token expiry, grant revocation, or shutdown.</param>
    /// <returns>The bounded tool result.</returns>
    /// <exception cref="RemoteMcpAuthorizationException">A downstream service rejected the delegated token; the caller's
    /// next request is challenged.</exception>
    public virtual ValueTask<CallToolResult> CallServiceAsync(RemoteMcpCaller caller, CallToolRequestParams request, CancellationToken cancellationToken) =>
        ValueTask.FromException<CallToolResult>(exception: new McpProtocolException(
            errorCode: McpErrorCode.InvalidParams,
            message: "Unknown tool."
        ));
    /// <summary>Describes control capabilities for this caller without opening a session. Defaults to no disclosed controls.</summary>
    /// <param name="caller">The validated caller.</param>
    /// <param name="cancellationToken">Cancels discovery.</param>
    /// <returns>The host's current disclosure; dispatch still enforces live grants.</returns>
    public virtual ValueTask<ControlCapabilities> DescribeControlAsync(RemoteMcpCaller caller, CancellationToken cancellationToken) => ValueTask.FromResult(result: new ControlCapabilities(""));
    /// <summary>Filters service discovery for the caller. Dispatch must enforce the same grants independently.</summary>
    /// <param name="caller">Validated caller requesting discovery.</param>
    /// <returns>Only service tools and argument choices disclosed to this caller.</returns>
    public virtual IReadOnlyList<Tool> GetServiceTools(RemoteMcpCaller caller) => ServiceTools;
}

internal sealed class UnconfiguredRemoteMcpHost : RemoteMcpHost {
    public override bool IsReady => false;

    public override ValueTask<IControlSession> AttachAsync(RemoteMcpCaller caller, CancellationToken cancellationToken) =>
        ValueTask.FromException<IControlSession>(exception: new InvalidOperationException(message: "Remote MCP requires a delegated host adapter; local Operator capabilities cannot be published over HTTP."));
}
