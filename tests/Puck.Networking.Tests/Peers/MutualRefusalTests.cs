using System.Diagnostics;
using Puck.Networking.Peers;
using Xunit;

namespace Puck.Networking.Tests.Peers;

/// <summary>Two sides that refuse each other must not both sit out <see cref="PeerWireProtocol.HandshakeTimeout"/>
/// waiting for the other to close: each writes its refusal, drains for at most
/// <see cref="PeerWireProtocol.RefusalDrainTimeout"/>, and closes, so the whole exchange ends well inside the
/// handshake deadline.</summary>
public sealed class MutualRefusalTests {
    private static readonly TimeSpan MutualRefusalBudget = TimeSpan.FromSeconds(value: 5);

    [Fact]
    public async Task BothSidesRefusingChannelUnbound_CompleteWellInsideTheHandshakeTimeout() {
        using var deadline = Laws.SocketDeadline();
        using var certificateOwnerA = PeerIdentity.Create();
        using var certificateOwnerB = PeerIdentity.Create();

        await using var peerA = PeerTestSupport.NewPeerWithMismatchedCertificate(
            certificateOwner: certificateOwnerA,
            identity: PeerIdentity.Create()
        );
        await using var peerB = PeerTestSupport.NewPeerWithMismatchedCertificate(
            certificateOwner: certificateOwnerB,
            identity: PeerIdentity.Create()
        );

        var endpointB = await PeerTestSupport.ListenLoopbackAsync(peer: peerB);
        var clock = Stopwatch.StartNew();
        var atA = await Assert.ThrowsAsync<PeerRefusedException>(testCode: () => peerA.DialAsync(
            ct: deadline.Token,
            endpoint: endpointB
        ));
        var atB = await peerB.HandshakeRefusals.ReadAsync(cancellationToken: deadline.Token);

        clock.Stop();

        // Each side reads the other's offer before anything else, so each decides ChannelUnbound for itself
        // rather than learning it from the other's refusal frame.
        Assert.Equal(
            expected: PeerRefusal.ChannelUnbound,
            actual: atA.Failure.Refusal
        );
        Assert.Equal(
            expected: PeerRefusal.ChannelUnbound,
            actual: atB.Failure.Refusal
        );
        Assert.True(
            condition: (clock.Elapsed < MutualRefusalBudget),
            userMessage: $"both sides refusing took {clock.Elapsed}; the drain is bounded by {PeerWireProtocol.RefusalDrainTimeout}, not by the {PeerWireProtocol.HandshakeTimeout} handshake timeout"
        );
        Assert.Empty(collection: peerA.Links);
        Assert.Empty(collection: peerB.Links);
    }
}
