using System.Net;
using Puck.Networking.Peers;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>A real QUIC peer used by the World wire laws. Application admission is deliberately separate from
/// the certificate-bound transport identity, so malformed application proofs still exercise the real door.</summary>
internal sealed class PeerTestClient : IDisposable {
    /// <summary>The QUIC/TLS handshake bound every in-process World test host and client uses. The handshake runs on
    /// msquic's own wall-clock timer, which no law can drive, and a loaded machine can stall a loopback handshake past
    /// <see cref="QuicPeerTransport.DefaultHandshakeTimeout"/>; a genuine hang still ends at the test's own
    /// cancellation.</summary>
    public static readonly TimeSpan TransportHandshakeTimeout = TimeSpan.FromMinutes(minutes: 5);

    private readonly WorldPeerNetwork m_network = new(transportHandshakeTimeout: TransportHandshakeTimeout);

    private PeerStream? m_stream;

    public ValueTask CompleteWritesAsync(CancellationToken ct) => GetStream().CompleteWritesAsync(ct: ct);
    public async Task ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken) =>
        m_stream = ((PeerStream)await m_network.ConnectAsync(
            new IPEndPoint(
                address: address,
                port: port
            ),
            cancellationToken
        ));
    public void Dispose() { m_stream?.Dispose(); m_network.Dispose(); }
    public PeerStream GetStream() => (m_stream ?? throw new InvalidOperationException(message: "Not connected."));
}
