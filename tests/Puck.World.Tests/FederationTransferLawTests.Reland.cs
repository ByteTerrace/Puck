using Puck.Commands;
using Puck.Maths;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class FederationTransferLawTests {
    // THE LAW: a commit refused at a reserved index that an ordinary join took after the reservation replays as it ran,
    // and recovery still refuses it. A peer connection skips a leased index, so only a seat's join can take one. The
    // commit lands its first traveler, is refused at the occupied seat and rolls the landing back; the re-drive needs
    // only the landed prefix free, reproduces the landing's generation and stops at the recorded traveler without
    // touching the occupant. Recovery has no recorded stop: an occupied member refuses the whole recorded lease.
    [Fact]
    public void AnArrivalRollbackReplaysWhenALaterReservedSlotWasTaken() {
        using var directory = new TemporaryDirectory(prefix: "puck-arrival-occupied-");
        using var fixture = Fixtures.FreshServer();
        var transport = new LoopbackTransport(server: fixture.Server);
        var tape = ArrivalTape(fixture, directory.RootPath, transport);
        var join = new SessionRequest.Join(Principal: Principal.Seat(slot: 1), Slot: 1, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey);

        Assert.True(condition: tape.TryBeginRecording(name: "arrivals", refusal: out var refusal), userMessage: refusal);
        var request = Reservation(border: "door", sourceAuthority: "source", transferId: 161);

        request = request with {
            Members = [request.Members[0], request.Members[0] with {
            PreferredSlot = 1, Mobility = Mobility(index: 1),
        }],
        };
        Assert.Equal([0, 1], fixture.Server.ReserveTransfer(request: request).BodyIndices);
        SessionReply? joined = null;

        transport.SubmitSession(completion: reply => joined = reply, request: join);
        Assert.True(condition: (joined?.Accepted == true));
        Assert.Equal(WorldTransferStatus.Missing, fixture.Server.CommitTransfer(
            sourceAuthority: request.SourceAuthority, transferId: request.TransferId,
            members: [Turned(travelTurn: FixedQ4816.One), Turned(travelTurn: FixedQ4816.One)], reason: out var reason));
        Assert.Contains(actualString: reason, expectedSubstring: "no longer free");
        Assert.False(condition: fixture.Server.Population.IsActive(index: 0));
        Assert.Equal(1, fixture.Server.Population.Generation(index: 0));
        fixture.Step();
        tape.NoteTick();
        _ = tape.StopRecording();

        var arrival = Assert.Single(collection: ReadArrivalTape(tape: tape).Ticks[0].Authority.OfType<WorldReplayEntry.Arrival>());

        Assert.True(condition: arrival.Outcome.RolledBack);
        Assert.Equal([1], arrival.Outcome.Generations);
        Assert.Equal(-1, tape.Verify(name: "arrivals").Primary.DivergedAt);

        using var recovery = Fixtures.FreshServer();

        Assert.True(condition: recovery.Server.ApplySession(request: join).Accepted);
        Assert.False(condition: recovery.Server.ExecuteAuthorityOperation(operation: () => recovery.Server.TransferEscrow.TryReland(
            arrival: DecodeArrival(arrival: arrival, defaults: recovery.Server.Definition.PlayerDefaults),
            recorded: null, reason: out reason)));
        Assert.Contains(actualString: reason, expectedSubstring: "occupied");
        Assert.Equal(0, recovery.Server.Population.Generation(index: 0));
    }
    // THE LAW: an arrival never overwrites a seat an ordinary join took after the reservation. The join is idempotent
    // over an active seat, so the landing refuses an occupied index before admitting anyone: the occupant keeps its
    // body, pose, incarnation and turn, the landing ahead of it rolls back, and the recording verifies.
    [Fact]
    public void AnArrivalCannotOverwriteASeatJoinedAfterItsReservation() {
        using var directory = new TemporaryDirectory(prefix: "puck-arrival-seat-occupied-");
        using var fixture = Fixtures.FreshServer();
        var transport = new LoopbackTransport(server: fixture.Server);
        var tape = ArrivalTape(fixture, directory.RootPath, transport);

        Assert.True(condition: tape.TryBeginRecording(name: "arrivals", refusal: out var refusal), userMessage: refusal);
        var request = Reservation(border: "door", sourceAuthority: "source", transferId: 162);

        request = request with {
            Members = [request.Members[0], request.Members[0] with {
            PreferredSlot = 1, Mobility = Mobility(index: 1),
        }],
        };
        Assert.True(condition: fixture.Server.ReserveTransfer(request: request).Accepted);
        SessionReply? joined = null;

        transport.SubmitSession(
            completion: reply => joined = reply,
            request: new SessionRequest.Join(
                Principal: Principal.Seat(slot: 1), Slot: 1, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey));
        Assert.True(condition: (joined?.Accepted == true));
        var occupant = fixture.Server.Population.EntryBody(index: 1)!;
        var position = occupant.FixedPosition;
        var incarnation = fixture.Server.Population.ResolveIncarnation(index: 1, authority: fixture.Server.AuthorityIdentity);

        Assert.Equal(WorldTransferStatus.Missing, fixture.Server.CommitTransfer(
            sourceAuthority: request.SourceAuthority, transferId: request.TransferId,
            members: [Turned(travelTurn: FixedQ4816.One), Turned(travelTurn: FixedQ4816.One)], reason: out var reason));
        Assert.Contains(actualString: reason, expectedSubstring: "no longer free");
        Assert.Same(expected: occupant, actual: fixture.Server.Population.EntryBody(index: 1));
        Assert.Equal(expected: position, actual: occupant.FixedPosition);
        Assert.Equal(expected: incarnation, actual: fixture.Server.Population.ResolveIncarnation(index: 1, authority: fixture.Server.AuthorityIdentity));
        Assert.Equal(FixedQ4816.Zero, fixture.Server.Population.TravelTurn(index: 1));
        Assert.False(condition: fixture.Server.Population.IsActive(index: 0));
        Assert.Equal(1, fixture.Server.Population.Generation(index: 0));
        fixture.Step();
        tape.NoteTick();
        _ = tape.StopRecording();
        Assert.Equal(-1, tape.Verify(name: "arrivals").Primary.DivergedAt);
    }
}
