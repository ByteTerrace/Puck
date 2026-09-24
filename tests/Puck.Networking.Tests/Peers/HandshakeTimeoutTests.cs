using Puck.Testing;
using Puck.Networking.Peers;
using Xunit;

namespace Puck.Networking.Tests.Peers;

/// <summary>A handshake that goes silent is ended by a clock, named <see cref="PeerRefusal.HandshakeTimedOut"/>
/// with the clock's name in the detail, and its connection is released: on the accepting side by
/// <see cref="PeerWireProtocol.ControlStreamTimeout"/> when the control stream never opens, on the dialing side by
/// <see cref="PeerWireProtocol.HandshakeTimeout"/> when the stream never answers. Both run over
/// <see cref="FakePeerTransport"/>, because loopback QUIC cannot be made to go silent on cue.</summary>
public sealed class HandshakeTimeoutTests {
    [Fact]
    public async Task Acceptor_WhoseConnectionNeverOpensAControlStream_RecordsHandshakeTimedOut_AtTheControlStreamTimeout() {
        var ct = TestContext.Current.CancellationToken;
        var clock = new VirtualClock();

        var transport = new FakePeerTransport(dial: static _ => throw new InvalidOperationException(message: "this law never dials"));
        var connection = new SilentPeerConnection();

        await using var acceptor = new Peer(
            identity: PeerIdentity.Create(),
            timeProvider: clock,
            transport: transport
        );

        await acceptor.ListenAsync(
            ct: ct,
            endpoint: PeerTestSupport.Loopback()
        );
        transport.Accept(connection: connection);
        Assert.False(condition: acceptor.HandshakeRefusals.TryRead(item: out _));
        await clock.WhenArmedAsync(
            count: 1,
            ct: ct,
            dueTime: PeerWireProtocol.ControlStreamTimeout
        );
        clock.Advance(by: PeerWireProtocol.ControlStreamTimeout);

        var refused = await acceptor.HandshakeRefusals.ReadAsync(cancellationToken: ct);

        Assert.Equal(
            expected: PeerRefusal.HandshakeTimedOut,
            actual: refused.Failure.Refusal
        );
        Assert.Contains(
            actualString: refused.Failure.Detail,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: nameof(PeerWireProtocol.ControlStreamTimeout)
        );
        Assert.Empty(collection: acceptor.Links);
        Assert.Null(@object: acceptor.ListenerFault);

        // The acceptor records the refusal before it disposes the connection; its own disposal waits for every
        // handshake to finish, so once it returns the refused connection must already have been released.
        await acceptor.DisposeAsync();
        Assert.True(
            condition: connection.IsDisposed,
            userMessage: "a timed-out handshake must release its connection"
        );
    }
    [Fact]
    public async Task Dialer_WhoseStreamNeverAnswers_IsRefusedHandshakeTimedOut_AtTheHandshakeTimeout() {
        var ct = TestContext.Current.CancellationToken;
        var clock = new VirtualClock();

        var connection = new SilentPeerConnection();

        await using var dialer = new Peer(
            identity: PeerIdentity.Create(),
            timeProvider: clock,
            transport: new FakePeerTransport(dial: _ => connection)
        );

        var dialing = dialer.DialAsync(
            ct: ct,
            endpoint: PeerTestSupport.Loopback(port: 1)
        );

        Assert.False(condition: dialing.IsCompleted);
        await clock.WhenArmedAsync(
            count: 1,
            ct: ct,
            dueTime: PeerWireProtocol.HandshakeTimeout
        );
        clock.Advance(by: PeerWireProtocol.HandshakeTimeout);
        var thrown = await Assert.ThrowsAsync<PeerRefusedException>(testCode: () => dialing);

        Assert.Equal(
            expected: PeerRefusal.HandshakeTimedOut,
            actual: thrown.Failure.Refusal
        );
        Assert.Contains(
            actualString: thrown.Failure.Detail,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: nameof(PeerWireProtocol.HandshakeTimeout)
        );
        Assert.True(
            condition: connection.IsDisposed,
            userMessage: "a timed-out dial must release its connection"
        );
        Assert.Empty(collection: dialer.Links);
    }
}
