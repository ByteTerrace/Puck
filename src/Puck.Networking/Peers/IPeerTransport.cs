using System.Net;

namespace Puck.Networking.Peers;

/// <summary>The transport a <see cref="Peer"/> dials and listens through: an authenticated, encrypted, multiplexed
/// connection to some key. A transport proves possession of a public key at its own handshake and hands that key
/// to the peer handshake as <see cref="IPeerConnection.RemoteTransportKey"/>, so the identity a peer proves over
/// the wire can be bound to the channel it proved it on. Nothing here names who dialed: a dialed and an accepted
/// connection expose the same shape.</summary>
public interface IPeerTransport : IAsyncDisposable {
    /// <summary>Opens an authenticated connection to a remote peer.</summary>
    /// <param name="endpoint">The address to dial.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The established connection.</returns>
    ValueTask<IPeerConnection> DialAsync(EndPoint endpoint, CancellationToken ct = default);
    /// <summary>Binds a listener that accepts authenticated connections from remote peers.</summary>
    /// <param name="endpoint">The endpoint to bind; port 0 picks a free port.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The bound listener.</returns>
    ValueTask<IPeerListener> ListenAsync(IPEndPoint endpoint, CancellationToken ct = default);
}
/// <summary>One bound listener. A connection whose transport-level handshake fails is dropped inside
/// <see cref="AcceptAsync"/>, which then waits for the next; only the listener's own closure ends the sequence.</summary>
public interface IPeerListener : IAsyncDisposable {
    /// <summary>Gets the endpoint the listener is bound to.</summary>
    IPEndPoint LocalEndpoint { get; }

    /// <summary>Accepts the next authenticated connection.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The connection, or <see langword="null"/> once the listener has closed.</returns>
    ValueTask<IPeerConnection?> AcceptAsync(CancellationToken ct = default);
}
/// <summary>One authenticated connection between two peers, carrying reliable ordered streams and, when the
/// transport can, unreliable datagrams. Streams are opened by one side and accepted by the other; each is a
/// bidirectional <see cref="Stream"/> whose read end reports end-of-stream when the remote side completes its
/// writes.</summary>
public interface IPeerConnection : IAsyncDisposable {
    /// <summary>Gets the largest datagram <see cref="SendDatagramAsync"/> accepts, or 0 when the transport carries no
    /// datagrams.</summary>
    int MaxDatagramBytes { get; }
    /// <summary>Gets the remote address.</summary>
    EndPoint RemoteEndpoint { get; }
    /// <summary>Gets the DER-encoded SubjectPublicKeyInfo of the key the remote side proved possession of at the
    /// transport's own handshake, or empty when it proved none.</summary>
    ReadOnlyMemory<byte> RemoteTransportKey { get; }

    /// <summary>Accepts the next stream the remote side opened.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The stream, or <see langword="null"/> once the connection has closed.</returns>
    ValueTask<Stream?> AcceptStreamAsync(CancellationToken ct = default);
    /// <summary>Opens a reliable, ordered, bidirectional stream.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The stream.</returns>
    ValueTask<Stream> OpenStreamAsync(CancellationToken ct = default);
    /// <summary>Receives the next unreliable datagram.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The datagram, or <see langword="null"/> once the connection has closed.</returns>
    /// <exception cref="NotSupportedException"><see cref="MaxDatagramBytes"/> is 0.</exception>
    ValueTask<ReadOnlyMemory<byte>?> ReceiveDatagramAsync(CancellationToken ct = default);
    /// <summary>Sends one unreliable, unordered datagram; a datagram may be lost or arrive out of order relative to
    /// every other send.</summary>
    /// <param name="datagram">The datagram bytes, at most <see cref="MaxDatagramBytes"/> long.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The send task.</returns>
    /// <exception cref="NotSupportedException"><see cref="MaxDatagramBytes"/> is 0.</exception>
    ValueTask SendDatagramAsync(ReadOnlyMemory<byte> datagram, CancellationToken ct = default);
}
