using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Puck.Mcp;

// The session's stdout. The one fact that ends a session from this side is the client going away: a write the pipe refuses
// (the client closed its end or exited). A slow client is not a gone one, and the session owns Worlds that ending it stops, so
// a reply waits for the client to drain stdout however long that takes; a busy harness reading a multi-megabyte capture is
// slow, not gone. Only the session's own shutdown cancels a write. A request's cancellation never does: a partially written
// line cannot be resumed, and the client ignores a reply to a request it cancelled.
internal sealed class WholeReplyMcpTransport(Stream input, Stream output, Action<Exception> fail, CancellationToken lifetime)
    : StreamServerTransport(
    input,
    output,
    "Puck Operator"
) {
    private readonly Action<Exception> m_fail = fail;
    private readonly CancellationToken m_lifetime = lifetime;

    public override async Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) {
        try {
            await base.SendMessageAsync(message, m_lifetime).ConfigureAwait(continueOnCapturedContext: false);
        } catch (Exception error) when (!m_lifetime.IsCancellationRequested) {
            m_fail(new IOException(innerException: error, message: "MCP output failed: the client closed its end of stdout; attachment closed."));
            throw;
        }
    }
}
