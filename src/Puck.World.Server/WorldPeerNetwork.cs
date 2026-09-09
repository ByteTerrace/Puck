using System.Net;
using Puck.Networking.Peers;

namespace Puck.World.Server;

/// <summary>Owns a World's peer-network lifetime. Local-only worlds create no transport. Networked worlds use
/// the networking library's QUIC transport and symmetric, certificate-bound peer handshake; there is no TCP
/// fallback. A composition root may persist the peer identity independently of its world admission policy.</summary>
public sealed class WorldPeerNetwork : IDisposable {
    private readonly Lazy<Peer> m_peer;
    private readonly Lock m_gate = new();
    private bool m_disposed;

    /// <summary>Creates a lazily initialized network owner.</summary>
    /// <param name="identityFile">A PKCS8 peer identity file to load or create; null creates an ephemeral identity.</param>
    public WorldPeerNetwork(string? identityFile = null) => m_peer = new(() => CreatePeer(identityFile));

    /// <summary>Gets the process or hosted authority's shared peer. The owner, not its consumers, disposes it.</summary>
    public Peer Peer {
        get {
            lock (m_gate) {
                ObjectDisposedException.ThrowIf(m_disposed, this);
                return m_peer.Value;
            }
        }
    }

    private static Peer CreatePeer(string? path) {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) || !QuicPeerTransport.IsSupported) {
            throw new PlatformNotSupportedException("World networking requires QUIC with TLS 1.3; no TCP fallback is available.");
        }
        var identity = path is not null && File.Exists(path) ? PeerIdentity.Load(path) : PeerIdentity.Create();
        try {
            if (path is not null && !File.Exists(path)) {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                identity.Save(path);
            }
            return new Peer(identity, new QuicPeerTransport(identity.CreateTransportCertificate()));
        } catch { identity.Dispose(); throw; }
    }

    /// <summary>Opens one authenticated application stream on the shared peer.</summary>
    /// <param name="endpoint">The remote QUIC endpoint.</param>
    /// <param name="ct">The connection deadline or cancellation.</param>
    /// <returns>A stream owning its peer link.</returns>
    public async ValueTask<Stream> ConnectAsync(IPEndPoint endpoint, CancellationToken ct) =>
        new PeerStream(await Peer.DialAsync(endpoint, ct).ConfigureAwait(false));

    /// <inheritdoc/>
    public void Dispose() {
        Peer? peer;
        lock (m_gate) {
            if (m_disposed) { return; }
            m_disposed = true;
            peer = m_peer.IsValueCreated ? m_peer.Value : null;
        }
        peer?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
