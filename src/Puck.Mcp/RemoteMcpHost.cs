using Puck.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Puck.Mcp;

/// <summary>A composition-owned source of isolated delegated sessions. The caller owns and disposes each returned session.</summary>
public abstract class RemoteMcpHost {
    /// <summary>Whether this composition exposes stateful Console attachments. Service-only hosts return false.</summary>
    public virtual bool SupportsAttachments => true;
    /// <summary>Whether the host is accepting new attachments; health checks never consume a session slot.</summary>
    public abstract bool IsReady { get; }
    /// <summary>Describes control capabilities for this caller without opening a session. Defaults to no disclosed controls.</summary>
    /// <param name="caller">The validated caller.</param>
    /// <param name="cancellationToken">Cancels discovery.</param>
    /// <returns>The host's current disclosure; dispatch still enforces live grants.</returns>
    public virtual ValueTask<ControlCapabilities> DescribeControlAsync(RemoteMcpCaller caller, CancellationToken cancellationToken) => ValueTask.FromResult(new ControlCapabilities(""));
    /// <summary>Creates a session for an already authorized subject without replaying prior work.</summary>
    /// <param name="caller">The validated caller. The host must preserve issuer and subject when creating ingress; assertions remain request-confined.</param>
    /// <param name="cancellationToken">Cancels attachment creation.</param>
    /// <returns>A new independently ordered session.</returns>
    public abstract ValueTask<IControlSession> AttachAsync(RemoteMcpCaller caller, CancellationToken cancellationToken);
    /// <summary>Additional explicitly installed service tools. The host must enforce caller-specific grants at dispatch.</summary>
    public virtual IReadOnlyList<Tool> ServiceTools => [];
    /// <summary>Filters service discovery for the caller. Dispatch must enforce the same grants independently.</summary>
    /// <param name="caller">Validated caller requesting discovery.</param>
    /// <returns>Only service tools and argument choices disclosed to this caller.</returns>
    public virtual IReadOnlyList<Tool> GetServiceTools(RemoteMcpCaller caller) => ServiceTools;
    /// <summary>Runs an installed service operation under this request's authenticated identity and deadline.</summary>
    /// <param name="caller">Validated identity and request-confined delegation assertion.</param>
    /// <param name="request">A call to a name advertised in ServiceTools.</param>
    /// <param name="cancellationToken">Request disconnect, token expiry, grant revocation, or shutdown.</param>
    /// <returns>The bounded tool result.</returns>
    public virtual ValueTask<CallToolResult> CallServiceAsync(RemoteMcpCaller caller, CallToolRequestParams request, CancellationToken cancellationToken) =>
        ValueTask.FromException<CallToolResult>(new McpProtocolException("Unknown tool.", McpErrorCode.InvalidParams));
}

internal sealed class UnconfiguredRemoteMcpHost : RemoteMcpHost {
    public override bool IsReady => false;
    public override ValueTask<IControlSession> AttachAsync(RemoteMcpCaller caller, CancellationToken cancellationToken) =>
        ValueTask.FromException<IControlSession>(new InvalidOperationException("Remote MCP requires a delegated host adapter; local Operator capabilities cannot be published over HTTP."));
}
