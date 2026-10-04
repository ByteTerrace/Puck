using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>A seat's join carries the shape of the wire contract it speaks, and the Hello door refuses a join of this
/// build's key under another shape by name before a seat is allocated.</summary>
public sealed class WorldHelloShapeAdmissionLawTests {
    [Fact]
    public void AJoinOfThisBuildsKeyUnderAnotherShapeIsRefusedByTheShapeAndTakesNoSeat() {
        using var fixture = Fixtures.FreshServer();
        var transport = new LoopbackTransport(server: fixture.Server);
        var other = ((WorldProtocol.WireShape[0] == '0') ? ('1' + WorldProtocol.WireShape[1..]) : ('0' + WorldProtocol.WireShape[1..]));
        SessionReply? reply = null;

        transport.SubmitSession(
            completion: answered => reply = answered,
            request: new SessionRequest.Join(
                Principal: Principal.Seat(slot: 1),
                Slot: 1,
                IdentityName: null,
                WireProtocolKey: WorldProtocol.WireProtocolKey,
                WireShape: other
            )
        );
        Assert.NotNull(@object: reply);
        Assert.False(condition: reply.Value.Accepted);
        Assert.Contains(
            actualString: reply.Value.Reason,
            expectedSubstring: nameof(WorldHelloRefusal.WireShapeMismatch)
        );
        Assert.Equal(
            expected: -1,
            actual: reply.Value.AssignedIndex
        );
    }
    [Fact]
    public void AJoinOfThisBuildsKeyAndShapeIsAdmitted() {
        using var fixture = Fixtures.FreshServer();
        var transport = new LoopbackTransport(server: fixture.Server);
        SessionReply? reply = null;

        transport.SubmitSession(
            completion: answered => reply = answered,
            request: new SessionRequest.Join(
                Principal: Principal.Seat(slot: 1),
                Slot: 1,
                IdentityName: null,
                WireProtocolKey: WorldProtocol.WireProtocolKey,
                WireShape: WorldProtocol.WireShape
            )
        );
        Assert.True(condition: (reply?.Accepted == true));
    }
}
