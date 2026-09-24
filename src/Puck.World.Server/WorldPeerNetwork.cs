using System.Net;
using Puck.Networking.Peers;

namespace Puck.World.Server;

/// <summary>Owns a World's peer-network lifetime. Local-only worlds create no transport. Networked worlds use
/// the networking library's QUIC transport and symmetric, certificate-bound peer handshake; there is no TCP
/// fallback. A composition root may persist the peer identity independently of its world admission policy.</summary>
public sealed class WorldPeerNetwork : IDisposable {
    private readonly bool m_allowOutbound;
    private readonly Lock m_gate = new();
    private readonly Lazy<Peer> m_peer;

    private bool m_disposed;

    /// <summary>Creates a lazily initialized network owner.</summary>
    /// <param name="identityFile">A PKCS8 peer identity file to load or create; null creates an ephemeral identity.</param>
    /// <param name="allowOutbound">Whether this authority may initiate remote streams. A closed rewind group
    /// keeps its player listener but refuses outbound authority connections.</param>
    /// <param name="timeProvider">The host clock the peer's control-stream, handshake, refusal-drain, and send deadlines
    /// run on; <see langword="null"/> is <see cref="TimeProvider.System"/>.</param>
    /// <param name="transportHandshakeTimeout">The wall-clock bound on each QUIC/TLS handshake;
    /// <see langword="null"/> is <see cref="QuicPeerTransport.DefaultHandshakeTimeout"/>.</param>
    public WorldPeerNetwork(string? identityFile = null, bool allowOutbound = true, TimeProvider? timeProvider = null, TimeSpan? transportHandshakeTimeout = null) {
        m_allowOutbound = allowOutbound;
        Clock = (timeProvider ?? TimeProvider.System);
        m_peer = new(valueFactory: () => CreatePeer(
            handshakeTimeout: transportHandshakeTimeout,
            path: identityFile,
            timeProvider: Clock
        ));
    }

    /// <summary>Gets the host clock this network was built on: its peer's deadlines run on it, and every federation
    /// lane, answer deadline and retry pacing that dials through this network reads it too, so one host has one clock
    /// for its whole peer surface.</summary>
    public TimeProvider Clock { get; }
    /// <summary>Gets the process or hosted authority's shared peer. The owner, not its consumers, disposes it.</summary>
    public Peer Peer {
        get {
            lock (m_gate) {
                ObjectDisposedException.ThrowIf(
                    condition: m_disposed,
                    instance: this
                );
                return m_peer.Value;
            }
        }
    }

    private static Peer CreatePeer(string? path, TimeProvider timeProvider, TimeSpan? handshakeTimeout) {
        if (
            !(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) ||
            !QuicPeerTransport.IsSupported
        ) {
            throw new PlatformNotSupportedException(message: "World networking requires QUIC with TLS 1.3; no TCP fallback is available.");
        }
        var identity = (((path is not null) && File.Exists(path: path))
            ? PeerIdentity.Load(path: path)
            : PeerIdentity.Create()
        );

        try {
            if (
                (path is not null) &&
                !File.Exists(path: path)
            ) {
                Directory.CreateDirectory(path: Path.GetDirectoryName(path: Path.GetFullPath(path: path))!);
                identity.Save(path: path);
            }
            return new Peer(
                identity,
                new QuicPeerTransport(
                    certificate: identity.CreateTransportCertificate(),
                    handshakeTimeout: handshakeTimeout
                ),
                timeProvider: timeProvider
            );
        } catch { identity.Dispose(); throw; }
    }

    /// <summary>Opens one authenticated application stream on the shared peer.</summary>
    /// <param name="endpoint">The remote QUIC endpoint.</param>
    /// <param name="ct">The connection deadline or cancellation.</param>
    /// <returns>A stream owning its peer link.</returns>
    public async ValueTask<Stream> ConnectAsync(EndPoint endpoint, CancellationToken ct) {
        if (!m_allowOutbound) { throw new InvalidOperationException(message: "closed rewind group refuses outbound federation"); }
        return new PeerStream(link: await Peer.DialAsync(
            ct: ct,
            endpoint: endpoint
        ).ConfigureAwait(continueOnCapturedContext: false));
    }
    /// <inheritdoc/>
    public void Dispose() {
        Peer? peer;

        lock (m_gate) {
            if (m_disposed) { return; }
            m_disposed = true;
            peer = (m_peer.IsValueCreated
                ? m_peer.Value
                : null
            );
        }
        peer?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
