using Puck.Networking.Peers;
using Xunit;

namespace Puck.Networking.Tests.Peers;

/// <summary><see cref="PeerEvent.Closed"/> names which side ended the link and how: <see cref="PeerRefusal.ConnectionClosed"/>
/// when the peer went away, <see cref="PeerRefusal.Disposed"/> when this side let go, <see cref="PeerRefusal.RefusedByPeer"/>
/// when a well-formed <see cref="PeerFrameKind.HelloRefused"/> reached the established link, and
/// <see cref="PeerRefusal.FrameMalformed"/> — never a handshake name — when one arrived that does not decode; and
/// <see cref="PeerLink.CloseFailure"/> always agrees with the event.</summary>
public sealed class LinkClosureTests {
    private static async Task AssertHelloRefusedBodyClosesAsAsync(byte[] body, PeerRefusal expected) {
        var ct = TestContext.Current.CancellationToken;

        var (peerA, peerB, linkAtoB, controlStreamAtB) = await ConnectTappedAsync(ct: ct);

        await using var disposeA = peerA;
        await using var disposeB = peerB;

        await WireFrame.WriteAsync(
            body: body,
            ct: ct,
            kind: ((byte)PeerFrameKind.HelloRefused),
            stream: controlStreamAtB
        );

        var closed = Assert.IsType<PeerEvent.Closed>(@object: await PeerTestSupport.NextEventAsync(link: linkAtoB));

        Assert.Equal(
            expected: expected,
            actual: closed.Failure.Refusal
        );
        Assert.Equal(
            expected: closed.Failure,
            actual: linkAtoB.CloseFailure
        );
        Assert.False(condition: linkAtoB.IsOpen);
        await linkAtoB.Events.Completion.WaitAsync(cancellationToken: ct);
        await linkAtoB.Released.WaitAsync(cancellationToken: ct);
        Assert.Empty(collection: peerA.Links);
    }
    /// <summary>Connects a plain peer A to a tapped peer B and hands back the control stream at B, so a law can
    /// write a raw frame onto the stream A's link reads.</summary>
    private static async Task<(Peer PeerA, Peer PeerB, PeerLink LinkAtoB, Stream ControlStreamAtB)> ConnectTappedAsync(CancellationToken ct) {
        var peerA = PeerTestSupport.NewPeer();

        var (peerB, tapB) = PeerTestSupport.NewTappedPeer(identity: PeerIdentity.Create());
        var endpointB = await PeerTestSupport.ListenLoopbackAsync(peer: peerB);
        var linkAtoB = await peerA.DialAsync(
            ct: ct,
            endpoint: endpointB
        );

        _ = await peerB.IncomingLinks.ReadAsync(cancellationToken: ct);

        return (peerA, peerB, linkAtoB, Assert.Single(collection: tapB.Streams));
    }

    [Fact]
    public async Task Closed_CarriesConnectionClosed_WhenThePeerDisposes() {
        var ct = TestContext.Current.CancellationToken;

        var (peerA, peerB, linkAtoB, _) = await PeerTestSupport.ConnectAsync(ct: ct);

        await using var disposeA = peerA;

        await peerB.DisposeAsync();

        var closed = Assert.IsType<PeerEvent.Closed>(@object: await PeerTestSupport.NextEventAsync(link: linkAtoB));

        Assert.Equal(
            expected: PeerRefusal.ConnectionClosed,
            actual: closed.Failure.Refusal
        );
        Assert.Equal(
            expected: closed.Failure,
            actual: linkAtoB.CloseFailure
        );
        Assert.False(condition: linkAtoB.IsOpen);
        await linkAtoB.Events.Completion.WaitAsync(cancellationToken: ct);
        await linkAtoB.Released.WaitAsync(cancellationToken: ct);
        Assert.Empty(collection: peerA.Links);
    }
    [Fact]
    public async Task Closed_CarriesDisposed_WhenThisSideDisposesTheLink() {
        var ct = TestContext.Current.CancellationToken;

        var (peerA, peerB, linkAtoB, _) = await PeerTestSupport.ConnectAsync(ct: ct);

        await using var disposeA = peerA;
        await using var disposeB = peerB;

        await linkAtoB.DisposeAsync();

        var closed = Assert.IsType<PeerEvent.Closed>(@object: await PeerTestSupport.NextEventAsync(link: linkAtoB));

        Assert.Equal(
            expected: PeerRefusal.Disposed,
            actual: closed.Failure.Refusal
        );
        Assert.Equal(
            expected: closed.Failure,
            actual: linkAtoB.CloseFailure
        );
        Assert.False(condition: linkAtoB.IsOpen);
        await linkAtoB.Events.Completion.WaitAsync(cancellationToken: ct);
        Assert.Empty(collection: peerA.Links);
    }
    [Fact]
    public Task Closed_CarriesFrameMalformed_WhenAHelloRefusedFrameNamesAnUnknownRefusalOnTheEstablishedLink() => AssertHelloRefusedBodyClosesAsAsync(
        body: [0x7f],
        expected: PeerRefusal.FrameMalformed
    );
    [Fact]
    public Task Closed_CarriesFrameMalformed_WhenAHelloRefusedFrameWithATrailingByteArrivesOnTheEstablishedLink() => AssertHelloRefusedBodyClosesAsAsync(
        body: [((byte)PeerRefusal.ChannelUnbound), 0],
        expected: PeerRefusal.FrameMalformed
    );
    [Fact]
    public Task Closed_CarriesRefusedByPeer_WhenAWellFormedHelloRefusedFrameArrivesOnTheEstablishedLink() => AssertHelloRefusedBodyClosesAsAsync(
        body: [((byte)PeerRefusal.ChannelUnbound)],
        expected: PeerRefusal.RefusedByPeer
    );
}
