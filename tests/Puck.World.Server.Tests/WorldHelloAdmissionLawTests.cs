using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>A seat's join carries the key of the wire contract it speaks, and the Hello door refuses a join of another
/// key by name before a seat is allocated.</summary>
public sealed class WorldHelloAdmissionLawTests {
    [Fact]
    public void AJoinOfAnotherKeyIsRefusedByTheKeyAndTakesNoSeat() {
        using var fixture = Fixtures.FreshServer();
        var transport = new LoopbackTransport(server: fixture.Server);
        SessionReply? reply = null;

        transport.SubmitSession(
            completion: answered => reply = answered,
            request: new SessionRequest.Join(
                Principal: Principal.Seat(slot: 1),
                Slot: 1,
                IdentityName: null,
                WireProtocolKey: (WorldProtocol.WireProtocolKey ^ 1UL)
            )
        );
        Assert.NotNull(@object: reply);
        Assert.False(condition: reply.Value.Accepted);
        Assert.Contains(
            actualString: reply.Value.Reason,
            expectedSubstring: nameof(WorldHelloRefusal.WireProtocolKeyMismatch)
        );
        Assert.Equal(
            expected: -1,
            actual: reply.Value.AssignedIndex
        );
    }
    [Fact]
    public void AJoinOfThisBuildsKeyIsAdmitted() {
        using var fixture = Fixtures.FreshServer();
        var transport = new LoopbackTransport(server: fixture.Server);
        SessionReply? reply = null;

        transport.SubmitSession(
            completion: answered => reply = answered,
            request: new SessionRequest.Join(
                Principal: Principal.Seat(slot: 1),
                Slot: 1,
                IdentityName: null,
                WireProtocolKey: WorldProtocol.WireProtocolKey
            )
        );
        Assert.True(condition: (reply?.Accepted == true));
    }
}