using Puck.Networking.Peers;
using Puck.Testing;
using Xunit;

namespace Puck.Networking.Tests.Peers;

/// <summary>
/// A peer signs its identity proofs and message claims at its own clock's instant and judges the other side's there,
/// so its claim windows and its deadlines read one clock: two peers whose clocks agree admit each other and exchange
/// messages at any instant, however far from the wall, and a pair whose clocks stand an hour apart refuses the
/// handshake, since each side's proof lies outside the other's window.
/// </summary>
public sealed class ClaimClockTests {
    private static readonly DateTimeOffset Instant = new(
        day: 1,
        hour: 0,
        minute: 0,
        month: 1,
        offset: TimeSpan.Zero,
        second: 0,
        year: 2000
    );

    private static Peer NewPeer(TimeProvider clock) {
        var identity = PeerIdentity.Create();

        return new Peer(
            identity: identity,
            timeProvider: clock,
            transport: PeerTestSupport.NewTransport(certificate: identity.CreateTransportCertificate())
        );
    }

    [Fact]
    public async Task PeersOnOneClockFarFromTheWallAdmitEachOtherAndExchangeMessages() {
        var ct = TestContext.Current.CancellationToken;
        var clock = new VirtualClock(start: Instant);

        await using var dialer = NewPeer(clock: clock);
        await using var acceptor = NewPeer(clock: clock);

        var endpoint = await PeerTestSupport.ListenLoopbackAsync(peer: acceptor);
        var dialed = await dialer.DialAsync(
            ct: ct,
            endpoint: endpoint
        );
        var accepted = await acceptor.IncomingLinks.ReadAsync(cancellationToken: ct);

        await dialed.SendAsync(
            ct: ct,
            payload: new byte[] { 1, 2, 3 }
        );

        var received = Assert.IsType<PeerEvent.Received>(@object: await PeerTestSupport.NextEventAsync(link: accepted));

        Assert.Equal(
            actual: received.Payload.ToArray(),
            expected: [1, 2, 3]
        );
    }
    [Fact]
    public async Task PeersWhoseClocksStandAnHourApartRefuseTheHandshake() {
        var ct = TestContext.Current.CancellationToken;
        var dialerClock = new VirtualClock(start: Instant);

        await using var dialer = NewPeer(clock: dialerClock);
        await using var acceptor = NewPeer(clock: new VirtualClock(start: Instant.AddHours(hours: 1)));

        var endpoint = await PeerTestSupport.ListenLoopbackAsync(peer: acceptor);
        var dial = dialer.DialAsync(
            ct: ct,
            endpoint: endpoint
        );
        // Each side refuses the other's proof, then drains for the other to close first, on its own clock: the dialer's
        // drain is expired once armed, and its close ends the acceptor's drain.
        var armed = dialerClock.WhenArmedAsync(
            count: 1,
            ct: ct,
            dueTime: PeerWireProtocol.RefusalDrainTimeout
        );

        if (await Task.WhenAny(task1: armed, task2: dial) == armed) {
            dialerClock.Advance(by: PeerWireProtocol.RefusalDrainTimeout);
        }

        _ = await Assert.ThrowsAsync<PeerRefusedException>(testCode: () => dial);
        _ = await PeerTestSupport.NextHandshakeRefusalAsync(peer: acceptor);
        Assert.Empty(collection: dialer.Links);
        Assert.Empty(collection: acceptor.Links);
    }
}
