using System.Diagnostics;
using Puck.Networking.Peers;
using Xunit;

namespace Puck.Networking.Tests.Peers;

/// <summary>A handshake that goes silent is ended by a clock, named <see cref="PeerRefusal.HandshakeTimedOut"/>
/// with the clock's name in the detail, and its connection is released: on the accepting side by
/// <see cref="PeerWireProtocol.ControlStreamTimeout"/> when the control stream never opens, on the dialing side by
/// <see cref="PeerWireProtocol.HandshakeTimeout"/> when the stream never answers. Both run over
/// <see cref="FakePeerTransport"/>, because loopback QUIC cannot be made to go silent on cue.</summary>
public sealed class HandshakeTimeoutTests {
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(value: 2);

    [Fact]
    public async Task Acceptor_WhoseConnectionNeverOpensAControlStream_RecordsHandshakeTimedOut_AtTheControlStreamTimeout() {
        using var deadline = Laws.SocketDeadline();

        var transport = new FakePeerTransport(dial: static _ => throw new InvalidOperationException(message: "this law never dials"));
        var connection = new SilentPeerConnection();

        await using var acceptor = new Peer(
            identity: PeerIdentity.Create(),
            transport: transport
        );

        await acceptor.ListenAsync(
            ct: deadline.Token,
            endpoint: PeerTestSupport.Loopback()
        );

        var clock = Stopwatch.StartNew();

        transport.Accept(connection: connection);

        var refused = await acceptor.HandshakeRefusals.ReadAsync(cancellationToken: deadline.Token);

        clock.Stop();

        Assert.Equal(
            expected: PeerRefusal.HandshakeTimedOut,
            actual: refused.Failure.Refusal
        );
        Assert.Contains(
            actualString: refused.Failure.Detail,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: nameof(PeerWireProtocol.ControlStreamTimeout)
        );
        Assert.True(
            condition: (clock.Elapsed < (PeerWireProtocol.ControlStreamTimeout + Slack)),
            userMessage: $"the refusal took {clock.Elapsed}; the control stream clock is {PeerWireProtocol.ControlStreamTimeout}"
        );

        // The acceptor records the refusal before it disposes the connection, so the release is observable only
        // after the read the law just completed; the law polls for it rather than assuming the ordering.
        await PeerTestSupport.WaitUntilAsync(
            condition: () => connection.IsDisposed,
            ct: deadline.Token
        );
        Assert.True(
            condition: connection.IsDisposed,
            userMessage: "a timed-out handshake must release its connection"
        );
        Assert.Empty(collection: acceptor.Links);
        Assert.Null(@object: acceptor.ListenerFault);
    }
    [Fact]
    public async Task Dialer_WhoseStreamNeverAnswers_IsRefusedHandshakeTimedOut_AtTheHandshakeTimeout() {
        using var deadline = Laws.SocketDeadline();

        var connection = new SilentPeerConnection();

        await using var dialer = new Peer(
            identity: PeerIdentity.Create(),
            transport: new FakePeerTransport(dial: _ => connection)
        );

        var clock = Stopwatch.StartNew();
        var thrown = await Assert.ThrowsAsync<PeerRefusedException>(testCode: () => dialer.DialAsync(
            ct: deadline.Token,
            endpoint: PeerTestSupport.Loopback(port: 1)
        ));

        clock.Stop();

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
            condition: (clock.Elapsed < (PeerWireProtocol.HandshakeTimeout + Slack)),
            userMessage: $"the refusal took {clock.Elapsed}; the handshake clock is {PeerWireProtocol.HandshakeTimeout}"
        );
        Assert.True(
            condition: connection.IsDisposed,
            userMessage: "a timed-out dial must release its connection"
        );
        Assert.Empty(collection: dialer.Links);
    }
}
