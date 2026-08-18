using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Puck.Networking.Peers;

/// <summary>The QUIC transport (<see cref="System.Net.Quic"/> over msquic): every connection is TLS 1.3 with a
/// certificate on both sides, so <see cref="IPeerConnection.RemoteTransportKey"/> is the public key of the
/// certificate the remote side proved possession of. Certificate validation accepts any certificate the remote
/// side can prove; binding that certificate's key to a peer identity is the peer handshake's job, so a certificate
/// is a channel credential here, never a trust decision. Datagrams are absent: this runtime's QUIC surface exposes
/// no RFC 9221 datagram API, so <see cref="IPeerConnection.MaxDatagramBytes"/> is 0 on every connection.</summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class QuicPeerTransport : IPeerTransport {
    /// <summary>The ALPN token every peer connection negotiates.</summary>
    public static readonly SslApplicationProtocol ApplicationProtocol = new(protocol: "puck-peer");
    /// <summary>The TLS server name a dialer offers. Peers are addressed by identity, never by DNS name, so it is a
    /// fixed label the acceptor's certificate is not expected to match.</summary>
    public const string ServerName = "puck-peer";

    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(value: 10);
    private const int MaxInboundStreams = 16;

    private readonly X509Certificate2 m_certificate;

    /// <summary>Initializes the transport over the certificate this side presents on every connection it dials or
    /// accepts. The transport owns and disposes the certificate.</summary>
    /// <param name="certificate">A certificate with a private key; <see cref="PeerIdentity.CreateTransportCertificate"/>
    /// mints one over the identity's own key.</param>
    public QuicPeerTransport(X509Certificate2 certificate) {
        ArgumentNullException.ThrowIfNull(argument: certificate);

        m_certificate = certificate;
    }

    /// <summary>Gets a value indicating whether QUIC is available on this host: an operating system this transport
    /// supports and a loadable msquic with TLS 1.3 support.</summary>
    [SupportedOSPlatformGuard("windows")]
    [SupportedOSPlatformGuard("linux")]
    [SupportedOSPlatformGuard("macos")]
    public static bool IsSupported => (
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) &&
        QuicListener.IsSupported &&
        QuicConnection.IsSupported
    );

    private static bool AcceptAnyPresentedCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) => (certificate is not null);
    private static ReadOnlyMemory<byte> TransportKeyOf(QuicConnection connection) => (connection.RemoteCertificate switch {
        X509Certificate2 certificate => certificate.PublicKey.ExportSubjectPublicKeyInfo(),
        _ => ReadOnlyMemory<byte>.Empty,
    });

    /// <inheritdoc/>
    public async ValueTask<IPeerConnection> DialAsync(EndPoint endpoint, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(argument: endpoint);

        var connection = await QuicConnection.ConnectAsync(
            cancellationToken: ct,
            options: new QuicClientConnectionOptions {
                ClientAuthenticationOptions = new SslClientAuthenticationOptions {
                    ApplicationProtocols = [ApplicationProtocol],
                    ClientCertificates = [m_certificate],
                    RemoteCertificateValidationCallback = AcceptAnyPresentedCertificate,
                    TargetHost = ServerName,
                },
                DefaultCloseErrorCode = 0,
                DefaultStreamErrorCode = 0,
                KeepAliveInterval = KeepAliveInterval,
                MaxInboundBidirectionalStreams = MaxInboundStreams,
                RemoteEndPoint = endpoint,
            }
        ).ConfigureAwait(continueOnCapturedContext: false);

        return new Connection(connection: connection);
    }
    /// <inheritdoc/>
    public async ValueTask<IPeerListener> ListenAsync(IPEndPoint endpoint, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(argument: endpoint);

        var listener = await QuicListener.ListenAsync(
            cancellationToken: ct,
            options: new QuicListenerOptions {
                ApplicationProtocols = [ApplicationProtocol],
                ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(result: new QuicServerConnectionOptions {
                    DefaultCloseErrorCode = 0,
                    DefaultStreamErrorCode = 0,
                    KeepAliveInterval = KeepAliveInterval,
                    MaxInboundBidirectionalStreams = MaxInboundStreams,
                    ServerAuthenticationOptions = new SslServerAuthenticationOptions {
                        ApplicationProtocols = [ApplicationProtocol],
                        ClientCertificateRequired = true,
                        RemoteCertificateValidationCallback = AcceptAnyPresentedCertificate,
                        ServerCertificate = m_certificate,
                    },
                }),
                ListenEndPoint = endpoint,
            }
        ).ConfigureAwait(continueOnCapturedContext: false);

        return new Listener(listener: listener);
    }
    /// <inheritdoc/>
    public ValueTask DisposeAsync() {
        m_certificate.Dispose();

        return ValueTask.CompletedTask;
    }

    private sealed class Listener : IPeerListener {
        private readonly QuicListener m_listener;

        public Listener(QuicListener listener) {
            m_listener = listener;
        }

        public IPEndPoint LocalEndpoint => m_listener.LocalEndPoint;

        public async ValueTask<IPeerConnection?> AcceptAsync(CancellationToken ct = default) {
            while (true) {
                try {
                    var connection = await m_listener.AcceptConnectionAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);

                    return new Connection(connection: connection);
                } catch (QuicException exception) when ((exception.QuicError != QuicError.OperationAborted) && !ct.IsCancellationRequested) {
                    // One remote side failed the transport handshake; the listener stays open for the next.
                } catch (AuthenticationException) when (!ct.IsCancellationRequested) {
                    // Same: the remote side's certificate did not satisfy the TLS handshake.
                } catch (Exception exception) when ((exception is QuicException or ObjectDisposedException or OperationCanceledException)) {
                    return null;
                }
            }
        }
        public ValueTask DisposeAsync() => m_listener.DisposeAsync();
    }
    private sealed class Connection : IPeerConnection {
        private readonly QuicConnection m_connection;

        public Connection(QuicConnection connection) {
            m_connection = connection;

            RemoteTransportKey = TransportKeyOf(connection: connection);
        }

        public int MaxDatagramBytes => 0;
        public EndPoint RemoteEndpoint => m_connection.RemoteEndPoint;
        public ReadOnlyMemory<byte> RemoteTransportKey { get; }

        public async ValueTask<Stream?> AcceptStreamAsync(CancellationToken ct = default) {
            try {
                return await m_connection.AcceptInboundStreamAsync(cancellationToken: ct).ConfigureAwait(continueOnCapturedContext: false);
            } catch (Exception exception) when ((exception is QuicException or ObjectDisposedException)) {
                return null;
            }
        }
        public async ValueTask<Stream> OpenStreamAsync(CancellationToken ct = default) => await m_connection.OpenOutboundStreamAsync(
            cancellationToken: ct,
            type: QuicStreamType.Bidirectional
        ).ConfigureAwait(continueOnCapturedContext: false);
        public ValueTask<ReadOnlyMemory<byte>?> ReceiveDatagramAsync(CancellationToken ct = default) => throw new NotSupportedException(message: "this QUIC surface carries no datagrams");
        public ValueTask SendDatagramAsync(ReadOnlyMemory<byte> datagram, CancellationToken ct = default) => throw new NotSupportedException(message: "this QUIC surface carries no datagrams");
        public ValueTask DisposeAsync() => m_connection.DisposeAsync();
    }
}
