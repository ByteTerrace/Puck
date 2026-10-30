using Puck.Commands;
using Puck.World.Protocol;
using Xunit;
using Rows = Puck.World.Server.Tests.CompanionRows;

namespace Puck.World.Server.Tests;

/// <summary>Step 5 of the companion custody protocol (docs/plans/companion-redesign.md, section 5.2 and section 7) on
/// real authorities: a companion's return is its own operation, verified at home through one door against home's own
/// records, landed in the slot its title holds and re-adopted into its placement; the held slot is never taken by
/// another body; and an install that releases a title wins over a departure still in doubt. Each law is the engine
/// form of a named trace of the custody model.</summary>
public sealed class CompanionReturnLawTests {
    // Model traces H1_TheTicketArrivesInTheCommitAndTheReturnLandsReadopted, H4_HomeNeverRefusesItsOwnReturnAndLandsItReadopted
    // and TheCensusNeverTakesAHeldSlotAndTheReturnLandsInIt: while the companion is away its slot at home stays empty
    // and no replacement spawns for its placement; its return from the custodian that holds the title lands in exactly
    // that slot one epoch above, re-adopted into its placement, and the title is home at that epoch.
    [Fact]
    public void TheReturnLandsReadoptedInItsHeldSlot() {
        using var rows = new Rows();
        var companion = rows.Companions[0];

        rows.Cross(from: rows.Home, to: rows.B);
        rows.Step(ticks: 6);
        Assert.Equal(actual: Rows.SlotOf(row: rows.Home, incarnation: companion), expected: -1);
        Assert.False(condition: rows.Home.Server.Population.IsActive(index: companion.Index));
        Assert.Equal(actual: PlacementBodies(row: rows.Home, placementId: "companion-0"), expected: 0);

        Assert.True(condition: rows.Host.TryEnqueueCompanionReturn(reason: out var reason, slot: Rows.SlotOf(row: rows.B, incarnation: companion), sourceInstance: "row-b", transferId: out _), userMessage: reason);
        rows.Step(ticks: 4);

        Assert.Equal(actual: Rows.SlotOf(row: rows.Home, incarnation: companion), expected: companion.Index);
        Assert.Equal(actual: Rows.SlotOf(row: rows.B, incarnation: companion), expected: -1);
        Assert.Equal(actual: Rows.EpochAt(row: rows.Home, incarnation: companion), expected: 2UL);
        Assert.Equal(actual: rows.Home.Server.Population.InhabitantPlacementId(index: companion.Index), expected: "companion-0");

        var title = rows.Title(companion: companion);

        Assert.Equal(actual: (title.Kind, title.Epoch), expected: (WorldCustodyKind.Home, 2UL));
        Assert.Equal(actual: Assert.Single(collection: rows.Records(row: rows.Home).OfType<WorldCrossingRecord.Arrival>()).Value.Readopted, expected: [companion]);
    }
    // Model trace H3_TheReturnIsItsOwnOperationBoundByTheHomeRecord: home decides a return from its own record alone.
    // The same ticket presented by an authority that does not hold the title is Stale, whoever names itself the
    // custodian; the custodian that does hold it at that epoch is accepted.
    [Fact]
    public void AReturnIsVerifiedAgainstHomesRecordAlone() {
        using var rows = new Rows();
        var companion = rows.Companions[0];

        rows.Cross(from: rows.Home, to: rows.B);

        var stranger = rows.Home.Server.ReserveTransfer(request: Return(rows: rows, source: rows.C, companion: companion, epoch: 1UL, transferId: 990UL));

        Assert.False(condition: stranger.Accepted);
        Assert.Contains(expectedSubstring: nameof(WorldCompanionRefusal.Stale), actualString: stranger.Reason);

        var wrongEpoch = rows.Home.Server.ReserveTransfer(request: Return(rows: rows, source: rows.B, companion: companion, epoch: 0UL, transferId: 990UL));

        Assert.False(condition: wrongEpoch.Accepted);
        Assert.Contains(expectedSubstring: nameof(WorldCompanionRefusal.Stale), actualString: wrongEpoch.Reason);
        Assert.True(condition: rows.Home.Server.ReserveTransfer(request: Return(rows: rows, source: rows.B, companion: companion, epoch: 1UL, transferId: 991UL)).Accepted);
    }
    // Model traces AnInstallWinsOverADepartureInDoubt and InstallDuringAPartialLandingRetiresTheUnlandedCompanion: an
    // install releases the title while home's own departure is still in doubt; the destination, asking home at the
    // commit, leaves the companion out, the player lands, and home retires the companion rather than restoring it.
    [Fact]
    public void AnInstallWinsOverADepartureInDoubt() {
        using var rows = new Rows();
        var companion = rows.Companions[0];
        var released = false;

        rows.Host.SetPeerCallFault(fault: new LoseFirstCommit(Destination: rows.B.Server, Lost: () => {
            Assert.Equal(actual: rows.Home.Server.RecordCustody(reason: out _, record: new WorldCrossingRecord.Release(Incarnation: companion)), expected: WorldCrossingDurability.Durable);
            released = true;
        }), instanceName: "row-b");
        rows.Cross(from: rows.Home, to: rows.B);
        rows.Step(ticks: 4);

        Assert.True(condition: released);
        Assert.True(condition: Rows.SlotOf(row: rows.B, incarnation: rows.Player) >= 0);
        Assert.Equal(actual: Rows.SlotOf(row: rows.B, incarnation: companion), expected: -1);
        Assert.Equal(actual: Rows.SlotOf(row: rows.Home, incarnation: companion), expected: -1);
        Assert.Equal(actual: rows.Title(companion: companion).Kind, expected: WorldCustodyKind.Released);
        Assert.Contains(collection: rows.Records(row: rows.Home).OfType<WorldCrossingRecord.Settlement>(), filter: static settlement => settlement.Retired.Count == 1);
    }
    // Model trace AReconnectAtHomeKeepsThePlacementBinding: at home a companion follows whoever holds its placement's
    // seat, at any generation. The companion comes home ahead of its player, the player comes home into the seat
    // afterwards, and the companion goes with that player's next crossing at the epoch the return gave it.
    [Fact]
    public void AReconnectAtHomeKeepsThePlacementBinding() {
        using var rows = new Rows(retained: true);
        var companion = rows.Companions[0];

        rows.Cross(from: rows.Home, to: rows.B);
        Assert.True(condition: rows.Host.TryEnqueueCompanionReturn(reason: out var reason, slot: Rows.SlotOf(row: rows.B, incarnation: companion), sourceInstance: "row-b", transferId: out _), userMessage: reason);
        rows.Step(ticks: 4);

        rows.Cross(from: rows.B, to: rows.Home);
        Assert.Equal(actual: Rows.SlotOf(row: rows.Home, incarnation: companion), expected: companion.Index);
        Assert.Equal(actual: Rows.SlotOf(row: rows.Home, incarnation: rows.Player), expected: 0);

        rows.Cross(from: rows.Home, to: rows.B);

        Assert.True(condition: Rows.SlotOf(row: rows.B, incarnation: rows.Player) >= 0);
        Assert.True(condition: Rows.SlotOf(row: rows.B, incarnation: companion) >= 0);
        Assert.Equal(actual: Rows.EpochAt(row: rows.B, incarnation: companion), expected: 3UL);
    }

    private static int PlacementBodies(HostRow row, string placementId) => Enumerable.Range(start: 0, count: row.Server.Population.Capacity).Count(predicate: slot => row.Server.Population.IsActive(index: slot) && string.Equals(a: row.Server.Population.InhabitantPlacementId(index: slot), b: placementId, comparisonType: StringComparison.Ordinal));
    // A return of home's companion from `source`, carrying the ticket of its last landing at `epoch`.
    private static WorldTransferReservationRequest Return(Rows rows, HostRow source, WorldEntityAddress companion, ulong epoch, ulong transferId) {
        var key = new WorldTransferKey(SourceAuthority: source.Server.AuthorityIdentity, TransferId: transferId);
        var tick = (rows.Home.Server.NextInputTick - 1UL);
        var origin = new WorldEntityAddress(Authority: source.Server.AuthorityIdentity, Generation: 1, Index: WorldBodiesLimits.LocalSeatCount);

        return new WorldTransferReservationRequest(
            Border: "return",
            BorderCapacity: null,
            DeadlineSourceTick: (tick + 30UL),
            Members: [new WorldTransferReservationMember(
                BodyColor: default,
                CatalogRig: 0,
                Companion: new WorldTransferCompanion(Driver: null, FollowsMember: null, Home: companion.Authority, Role: "companion", Ticket: new WorldCustodyTicket(Epoch: epoch, Home: companion.Authority, Hop: key, Incarnation: companion)),
                Identity: null,
                MemberId: 0,
                Mobility: new WorldMobilityIdentity(DepartedFrom: origin, Epoch: epoch, Incarnation: companion),
                PreferredSlot: WorldBodiesLimits.LocalSeatCount,
                Principal: Principal.Console,
                Source: IntentSource.Idle
            )],
            PartyAllOrNothing: true,
            PeerAdmission: false,
            SourceAuthority: key.SourceAuthority,
            SourceRateHz: rows.Home.Server.Definition.SimulationRateHz,
            SourceTick: tick,
            TransferId: key.TransferId
        );
    }
    // Forwards every call to the destination except the first commit, which it loses on the way after running `Lost`:
    // the source's departure is then in doubt and the destination's lease still live.
    private sealed record LoseFirstCommit(WorldServer Destination, Action Lost) : IWorldPeerCall {
        private readonly bool[] m_lost = [false];

        public void Abort(string sourceAuthority, ulong transferId) => Destination.AbortTransfer(sourceAuthority: sourceAuthority, transferId: transferId);
        public void Acknowledge(string sourceAuthority, ulong transferId) => Destination.AcknowledgeTransfer(sourceAuthority: sourceAuthority, transferId: transferId);
        public WorldTransferReservationReply Reserve(WorldTransferReservationRequest request) => Destination.ReserveTransfer(request: request);
        public WorldTransferStep Commit(string sourceAuthority, ulong transferId, IReadOnlyList<WorldTransferCommitMember> members, out WorldTransferStatus status, out string reason) {
            if (!m_lost[0]) {
                m_lost[0] = true;
                Lost();
                status = WorldTransferStatus.Missing;
                reason = "the commit was lost on the lane";
                return WorldTransferStep.Unreachable;
            }
            status = Destination.CommitTransfer(members: members, reason: out reason, sourceAuthority: sourceAuthority, transferId: transferId);
            return WorldTransferStep.Answered;
        }
        public bool TryStatus(string sourceAuthority, ulong transferId, out WorldTransferStatus status) {
            status = Destination.TransferStatus(sourceAuthority: sourceAuthority, transferId: transferId);
            return true;
        }
        public bool TryLanded(string sourceAuthority, ulong transferId, out IReadOnlyList<int>? landed) {
            landed = Destination.TransferLanded(sourceAuthority: sourceAuthority, transferId: transferId);
            return true;
        }
    }
}
