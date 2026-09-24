using System.Net;
using System.Security.Cryptography.X509Certificates;
using Puck.Networking.Peers;
using Xunit;

namespace Puck.Networking.Tests.Peers;

/// <summary>Shared support for a peer law: every peer runs over the real QUIC transport on loopback.</summary>
internal static class PeerTestSupport {
    private static QuicPeerTransport NewTransport(PeerIdentity certificateOwner) => NewTransport(certificate: certificateOwner.CreateTransportCertificate());

    /// <summary>Dials a fresh listening peer from another fresh peer and takes the accepted link, so a law that
    /// needs an established link on both sides starts from one line. The caller owns both peers.</summary>
    public static async Task<(Peer PeerA, Peer PeerB, PeerLink LinkAtoB, PeerLink LinkBtoA)> ConnectAsync(CancellationToken ct) {
        var peerA = NewPeer();
        var peerB = NewPeer();
        var endpointB = await ListenLoopbackAsync(peer: peerB);
        var linkAtoB = await peerA.DialAsync(
            ct: ct,
            endpoint: endpointB
        );
        var linkBtoA = await peerB.IncomingLinks.ReadAsync(cancellationToken: ct);

        return (peerA, peerB, linkAtoB, linkBtoA);
    }
    /// <summary>Connects two fresh peers over one <see cref="InMemoryPeerConnection"/> pair — A dials, B accepts —
    /// and takes the accepted link, for a law that needs a transport fact loopback QUIC never shows on cue. The
    /// caller owns both peers; <paramref name="clockA"/> is peer A's deadline clock.</summary>
    public static async Task<(Peer PeerA, Peer PeerB, PeerLink LinkAtoB, PeerLink LinkBtoA, InMemoryPeerConnection ConnectionAtA, InMemoryPeerConnection ConnectionAtB)> ConnectInMemoryAsync(CancellationToken ct, TimeProvider? clockA = null) {
        var identityA = PeerIdentity.Create();
        var identityB = PeerIdentity.Create();

        var (connectionAtA, connectionAtB) = InMemoryPeerConnection.Pair(
            keyProvedByA: identityA.SubjectPublicKeyInfo,
            keyProvedByB: identityB.SubjectPublicKeyInfo
        );
        var transportB = new FakePeerTransport(dial: static _ => throw new InvalidOperationException(message: "this law never dials from B"));
        var peerA = new Peer(
            identity: identityA,
            timeProvider: clockA,
            transport: new FakePeerTransport(dial: _ => connectionAtA)
        );
        var peerB = new Peer(
            identity: identityB,
            transport: transportB
        );

        await peerB.ListenAsync(
            ct: ct,
            endpoint: Loopback()
        );
        transportB.Accept(connection: connectionAtB);

        var linkAtoB = await peerA.DialAsync(
            ct: ct,
            endpoint: Loopback(port: 2)
        );
        var linkBtoA = await peerB.IncomingLinks.ReadAsync(cancellationToken: ct);

        return (peerA, peerB, linkAtoB, linkBtoA, connectionAtA, connectionAtB);
    }
    public static Task<IPEndPoint> ListenLoopbackAsync(Peer peer) => peer.ListenAsync(
        ct: TestContext.Current.CancellationToken,
        endpoint: Loopback()
    );
    public static IPEndPoint Loopback(int port = 0) => new(
        address: IPAddress.Loopback,
        port: port
    );
    /// <summary>Creates a peer over a fresh identity whose transport certificate is minted from that same identity.</summary>
    public static Peer NewPeer() => NewPeer(identity: PeerIdentity.Create());
    /// <summary>Creates a peer over <paramref name="identity"/> whose transport certificate is minted from that same identity.</summary>
    public static Peer NewPeer(PeerIdentity identity) => new(
        identity: identity,
        transport: NewTransport(certificateOwner: identity)
    );
    /// <summary>Creates a peer over <paramref name="identity"/> whose transport certificate is minted from a
    /// different identity — a channel whose proven key is not the identity the peer offers.</summary>
    public static Peer NewPeerWithMismatchedCertificate(PeerIdentity identity, PeerIdentity certificateOwner, TimeProvider? timeProvider = null) => new(
        identity: identity,
        timeProvider: timeProvider,
        transport: NewTransport(certificateOwner: certificateOwner)
    );
    /// <summary>Creates a peer over <paramref name="identity"/> whose transport is tapped, so a law can write raw
    /// frames onto the control stream the peer's link reads.</summary>
    public static (Peer Peer, TappedPeerTransport Tap) NewTappedPeer(PeerIdentity identity) {
        var tap = new TappedPeerTransport(inner: NewTransport(certificateOwner: identity));

        return (new Peer(
            identity: identity,
            transport: tap
        ), tap);
    }
    /// <summary>Creates the real QUIC transport over <paramref name="certificate"/>, which the transport then owns —
    /// any certificate, so a law can dial with a key no <see cref="PeerIdentity"/> could ever be built over.</summary>
    public static QuicPeerTransport NewTransport(X509Certificate2 certificate) {
        if (!QuicPeerTransport.IsSupported) {
            throw new PlatformNotSupportedException(message: "the peer laws need QUIC (msquic with TLS 1.3) on this host");
        }

        return new QuicPeerTransport(certificate: certificate);
    }
    /// <summary>Reads the next event <paramref name="link"/> publishes, bounded only by the test's own token: a law
    /// calls this for an event that must arrive, so no wall-clock budget can decide it.</summary>
    public static ValueTask<PeerEvent> NextEventAsync(PeerLink link) => link.Events.ReadAsync(cancellationToken: TestContext.Current.CancellationToken);
    /// <summary>Reads the next handshake refusal <paramref name="peer"/> records, bounded only by the test's own token.</summary>
    public static ValueTask<PeerHandshakeRefused> NextHandshakeRefusalAsync(Peer peer) => peer.HandshakeRefusals.ReadAsync(cancellationToken: TestContext.Current.CancellationToken);
    /// <summary>Writes one already-encoded-or-garbage message frame verbatim onto <paramref name="controlStream"/>,
    /// bypassing signing — the shape a refusal law needs to drive an unsigned, wrongly-signed, or tampered
    /// message onto an otherwise-honest link.</summary>
    public static Task SendRawMessageFrameAsync(Stream controlStream, byte[] attestationOrGarbageBytes, CancellationToken ct) {
        var writer = new WireWriter();

        writer.WriteBlock(value: attestationOrGarbageBytes);

        return WireFrame.WriteAsync(
            body: writer.ToArray(),
            ct: ct,
            kind: ((byte)PeerFrameKind.Message),
            stream: controlStream
        );
    }
}
/// <summary>An <see cref="IPeerTransport"/> decorator that records every control stream a connection opens or
/// accepts, so a law can write onto the same stream a peer's link reads.</summary>
internal sealed class TappedPeerTransport : IPeerTransport {
    private readonly IPeerTransport m_inner;

    private readonly List<Stream> m_streams = [];
    private readonly Lock m_streamsLock = new();

    public TappedPeerTransport(IPeerTransport inner) {
        m_inner = inner;
    }

    /// <summary>Gets a snapshot of every stream opened or accepted so far, in order.</summary>
    public IReadOnlyList<Stream> Streams {
        get {
            lock (m_streamsLock) {
                return [.. m_streams];
            }
        }
    }

    private void Record(Stream? stream) {
        if (stream is null) {
            return;
        }

        lock (m_streamsLock) {
            m_streams.Add(item: stream);
        }
    }

    public async ValueTask<IPeerConnection> DialAsync(EndPoint endpoint, CancellationToken ct = default) => new TappedConnection(
        inner: await m_inner.DialAsync(
            ct: ct,
            endpoint: endpoint
        ),
        record: Record
    );
    public ValueTask DisposeAsync() => m_inner.DisposeAsync();
    public async ValueTask<IPeerListener> ListenAsync(IPEndPoint endpoint, CancellationToken ct = default) => new TappedListener(
        inner: await m_inner.ListenAsync(
            ct: ct,
            endpoint: endpoint
        ),
        record: Record
    );

    private sealed class TappedListener : IPeerListener {
        private readonly IPeerListener m_inner;
        private readonly Action<Stream?> m_record;

        public TappedListener(IPeerListener inner, Action<Stream?> record) {
            m_inner = inner;
            m_record = record;
        }

        public IPEndPoint LocalEndpoint => m_inner.LocalEndpoint;

        public async ValueTask<IPeerConnection?> AcceptAsync(CancellationToken ct = default) {
            var connection = await m_inner.AcceptAsync(ct: ct);

            return ((connection is null)
                ? null
                : new TappedConnection(
                    inner: connection,
                    record: m_record
                )
            );
        }
        public ValueTask DisposeAsync() => m_inner.DisposeAsync();
    }
    private sealed class TappedConnection : IPeerConnection {
        private readonly IPeerConnection m_inner;
        private readonly Action<Stream?> m_record;

        public TappedConnection(IPeerConnection inner, Action<Stream?> record) {
            m_inner = inner;
            m_record = record;
        }

        public int MaxDatagramBytes => m_inner.MaxDatagramBytes;
        public EndPoint RemoteEndpoint => m_inner.RemoteEndpoint;
        public ReadOnlyMemory<byte> RemoteTransportKey => m_inner.RemoteTransportKey;

        public async ValueTask<Stream?> AcceptStreamAsync(CancellationToken ct = default) {
            var stream = await m_inner.AcceptStreamAsync(ct: ct);

            m_record(obj: stream);

            return stream;
        }
        public ValueTask DisposeAsync() => m_inner.DisposeAsync();
        public async ValueTask<Stream> OpenStreamAsync(CancellationToken ct = default) {
            var stream = await m_inner.OpenStreamAsync(ct: ct);

            m_record(obj: stream);

            return stream;
        }
        public ValueTask<ReadOnlyMemory<byte>?> ReceiveDatagramAsync(CancellationToken ct = default) => m_inner.ReceiveDatagramAsync(ct: ct);
        public ValueTask SendDatagramAsync(ReadOnlyMemory<byte> datagram, CancellationToken ct = default) => m_inner.SendDatagramAsync(
            ct: ct,
            datagram: datagram
        );
    }
}
