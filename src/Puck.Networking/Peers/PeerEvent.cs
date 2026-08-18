using System.Net;

namespace Puck.Networking.Peers;

/// <summary>One event a <see cref="PeerLink"/> surfaces on its <see cref="PeerLink.Events"/> reader.</summary>
public abstract record PeerEvent {
    private PeerEvent() {
    }

    /// <summary>One inbound message that verified against the link's established identity.</summary>
    /// <param name="Payload">The opaque message bytes.</param>
    public sealed record Received(ReadOnlyMemory<byte> Payload) : PeerEvent;
    /// <summary>One inbound message frame the link refused. The link stays open and continues delivering
    /// subsequent honest traffic.</summary>
    /// <param name="Failure">The named refusal.</param>
    public sealed record Refused(PeerFailure Failure) : PeerEvent;
    /// <summary>The link closed and will deliver no further events.</summary>
    /// <param name="Reason">Why the link closed.</param>
    public sealed record Closed(string Reason) : PeerEvent;
}
/// <summary>One inbound connection a <see cref="Peer"/> accepted at the transport but refused at the handshake,
/// surfaced on <see cref="Peer.HandshakeRefusals"/>. No link exists for it.</summary>
/// <param name="RemoteEndpoint">The remote address.</param>
/// <param name="Failure">The named refusal.</param>
public sealed record PeerHandshakeRefused(EndPoint RemoteEndpoint, PeerFailure Failure);
