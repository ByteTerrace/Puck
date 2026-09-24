using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Puck.Networking;

namespace Puck.Mcp;

// Bound replies through the SDK's serialization and send lock, not only through engine dispatch.
internal sealed class BoundedMcpTransport(Stream input, Stream output, Action<Exception> fail, TimeProvider clock, CancellationToken lifetime)
    : StreamServerTransport(
    input,
    output,
    "Puck Operator"
) {
    // The time one reply may take to reach stdout before the adapter treats the client as stalled.
    private static readonly TimeSpan WriteDeadline = TimeSpan.FromSeconds(seconds: 5);

    private readonly TimeProvider m_clock = clock;
    private readonly Action<Exception> m_fail = fail;
    private readonly CancellationToken m_lifetime = lifetime;

    private int m_pendingSends;

    private static async Task ObserveAsync(Task send) {
        try { await send.ConfigureAwait(continueOnCapturedContext: false); } catch (Exception) { /* The adapter is closing; observe the abandoned SDK write. */ }
    }

    public override async Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) {
        var pending = Interlocked.Increment(location: ref m_pendingSends);
        Task? send = null;

        try {
            if (pending > 4) { throw new InvalidDataException(message: "MCP output exceeds four pending replies; the client must drain stdout."); }
            using var deadline = new OperationDeadline(
                caller: cancellationToken,
                lifetime: m_lifetime,
                timeProvider: m_clock,
                timeout: WriteDeadline
            );

            send = base.SendMessageAsync(
                message,
                deadline.Token
            );
            await send.WaitAsync(cancellationToken: deadline.Token).ConfigureAwait(continueOnCapturedContext: false);
        } catch (Exception error) {
            // A partially written line cannot be resumed by sending the next reply on the same stream.
            if (!m_lifetime.IsCancellationRequested) {
                m_fail(new IOException(
                innerException: error,
                message: "MCP output failed or stalled; attachment closed."
            ));
            }
            if (send is not null) { _ = ObserveAsync(send: send); }
            throw;
        } finally { Interlocked.Decrement(location: ref m_pendingSends); }
    }
}
