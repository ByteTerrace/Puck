using Puck.Hosting;
using Puck.Networking;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Puck.Mcp;

/// <summary>A composition-owned source of isolated Operator sessions. The caller owns and disposes each returned session.</summary>
public abstract class RemoteMcpHost {
    /// <summary>Whether this composition exposes stateful Console attachments. Service-only hosts return false.</summary>
    public virtual bool SupportsAttachments => true;
    /// <summary>Whether the host is accepting new attachments; health checks never consume a session slot.</summary>
    public abstract bool IsReady { get; }
    /// <summary>Creates a session for an already authorized subject without replaying prior work.</summary>
    /// <param name="subject">The validated, explicitly granted subject.</param>
    /// <param name="cancellationToken">Cancels attachment creation.</param>
    /// <returns>A new independently ordered session.</returns>
    public abstract ValueTask<IControlSession> AttachAsync(string subject, CancellationToken cancellationToken);
    /// <summary>Additional explicitly installed service tools. The host must enforce caller-specific grants at dispatch.</summary>
    public virtual IReadOnlyList<Tool> ServiceTools => [];
    /// <summary>Runs an installed service operation under this request's authenticated identity and deadline.</summary>
    /// <param name="caller">Validated identity and request-confined delegation assertion.</param>
    /// <param name="request">A call to a name advertised in ServiceTools.</param>
    /// <param name="cancellationToken">Request disconnect, token expiry, grant revocation, or shutdown.</param>
    /// <returns>The bounded tool result.</returns>
    public virtual ValueTask<CallToolResult> CallServiceAsync(RemoteMcpCaller caller, CallToolRequestParams request, CancellationToken cancellationToken) =>
        ValueTask.FromException<CallToolResult>(new McpProtocolException("Unknown tool.", McpErrorCode.InvalidParams));
}

internal sealed class LocalRemoteMcpHost(RemoteMcpOptions options) : RemoteMcpHost {
    private string m_path = options.AttachmentPath;
    internal void SetPath(string path) => Volatile.Write(ref m_path, path);
    public override bool IsReady {
        get {
            try { _ = LocalEndpointCapability.ReadDescriptor(Volatile.Read(ref m_path)); return true; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or System.Text.Json.JsonException) { return false; }
        }
    }
    public override async ValueTask<IControlSession> AttachAsync(string subject, CancellationToken cancellationToken) =>
        await LocalControlClient.ConnectAsync(Volatile.Read(ref m_path), cancellationToken).ConfigureAwait(false);
}
