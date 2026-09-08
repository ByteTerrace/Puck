using Puck.Networking.Peers;

namespace Puck.Networking;

/// <summary>Terminal stream cleanup shared by peer and application handshakes.</summary>
public static class StreamDrain {
    /// <summary>Discards inbound bytes until the remote closes, transport fails, or cancellation expires. After a
    /// terminal reply, this gives the remote time to receive it before the caller closes the underlying connection.
    /// For a <see cref="PeerStream"/>, a half-close only ends one sending direction: draining waits for the link to
    /// close, including when the application already read that direction's EOF. The caller supplies a bounded
    /// deadline and retains ownership of the stream.</summary>
    /// <param name="stream">The exclusively read stream whose protocol exchange has finished.</param>
    /// <param name="ct">The caller's bounded drain deadline or shutdown token.</param>
    /// <returns>The terminal drain operation. Transport closure and cancellation complete normally.</returns>
    public static async Task UntilClosedAsync(Stream stream, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(stream);
        try {
            if (stream is PeerStream peerStream) {
                await peerStream.DrainUntilClosedAsync(ct).ConfigureAwait(false);
                return;
            }
            var sink = new byte[256];
            while (!ct.IsCancellationRequested && await stream.ReadAsync(sink, ct).ConfigureAwait(false) > 0) { }
        } catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException) { }
    }
}
