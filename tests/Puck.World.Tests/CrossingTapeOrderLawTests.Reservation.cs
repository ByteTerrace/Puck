using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>A reservation never mints the traveler's mobility credential. The authoritative hash folds a body's
/// continuation, credential included, at every recorded tick, and the tape records no reservation, so a credential a
/// reservation minted would stand in the live run and not in the re-drive. Only the departure's detach mints it,
/// and the re-drive runs that detach too. Each law records the source and verifies its tape tick for tick: a
/// reservation refused and retried, an abort between the reservation and the detach, and a reservation for a body
/// slot reused after a departure. The in-doubt restore, whose live rollback installs the departing credential, is
/// held by <see cref="AnInDoubtDepartureThatRollsBackReplaysInAuthorityOrder"/> and
/// <see cref="ARedrivenRollbackRestoresTheFieldsTheLiveRollbackRestored"/>. Red legs: a reservation that mints the
/// credential diverges at the tick it was refused or aborted.</summary>
public sealed partial class CrossingTapeOrderLawTests {
    // Records the scenario from Begin with the seam installed, runs `act`, steps four more ticks, and verifies the
    // tape replays the live hash at every tick.
    private static void AssertReplaysTickForTick(Scenario scenario, string name) {
        for (var tick = 0; (tick < 4); tick++) {
            scenario.Step();
        }

        _ = scenario.Tape.StopRecording();
        Assert.Equal(
            actual: scenario.Tape.Verify(name: name).Primary.DivergedAt,
            expected: -1
        );
    }
    // Steps until the source's slot 0 has departed, and answers how many ticks that took.
    private static int StepUntilDeparted(Scenario scenario) {
        var ticks = 0;

        for (; ((ticks < 64) && scenario.Source.Server.Population.IsActive(index: 0)); ticks++) {
            scenario.Step();
        }

        Assert.False(condition: scenario.Source.Server.Population.IsActive(index: 0));

        return ticks;
    }

    // THE LAW: a reservation the destination refuses as full, retried on the next tick and then accepted, replays
    // tick for tick: the refused tick leaves the traveler at the source with no credential minted.
    [Fact]
    public void AReservationRefusedAndRetriedReplaysTickForTick() {
        using var scenario = new Scenario();
        var name = scenario.Begin();
        var refused = 0;

        scenario.Host.SetPeerCallFault(
            fault: new SeamPeerCall(
                afterAcknowledge: null,
                commitFault: string.Empty,
                destination: scenario.Destination.Server,
                reserve: (_, forward) => ((refused++ == 0)
                    ? WorldTransferReservationReply.Refused(reason: "'row-b' is full for the first attempt")
                    : forward())
            ),
            instanceName: "row-b"
        );
        scenario.EnqueueCrossing();
        Assert.InRange(actual: StepUntilDeparted(scenario: scenario), high: 64, low: 2);
        Assert.Equal(actual: refused, expected: 2);
        AssertReplaysTickForTick(name: name, scenario: scenario);
    }
    // THE LAW: a reservation the destination accepts but the source aborts before any member detaches (here, an
    // acceptance that names no destination definition) replays tick for tick: the traveler stays, uncredentialed.
    [Fact]
    public void AnAbortBetweenTheReservationAndTheDetachReplaysTickForTick() {
        using var scenario = new Scenario();
        var name = scenario.Begin();
        var reserved = 0;

        scenario.Host.SetPeerCallFault(
            fault: new SeamPeerCall(
                afterAcknowledge: null,
                commitFault: string.Empty,
                destination: scenario.Destination.Server,
                reserve: (_, forward) => {
                    reserved++;

                    return (forward() with { DestinationDefinition = null });
                }
            ),
            instanceName: "row-b"
        );
        scenario.EnqueueCrossing();
        scenario.Step();
        Assert.Equal(actual: reserved, expected: 1);
        Assert.True(condition: scenario.Source.Server.Population.IsActive(index: 0));
        AssertReplaysTickForTick(name: name, scenario: scenario);
        Assert.True(condition: scenario.Source.Server.Population.IsActive(index: 0));
    }
    // THE LAW: after a traveler departs, a new occupant takes its body slot under the next generation; a reservation
    // refused for that occupant replays tick for tick.
    [Fact]
    public void AReservationForABodySlotReusedAfterADepartureReplaysTickForTick() {
        using var scenario = new Scenario();
        var name = scenario.Begin();
        var crossings = 0;

        scenario.Host.SetPeerCallFault(
            fault: new SeamPeerCall(
                afterAcknowledge: null,
                commitFault: string.Empty,
                destination: scenario.Destination.Server,
                reserve: (_, forward) => ((crossings++ == 0)
                    ? forward()
                    : WorldTransferReservationReply.Refused(reason: "the law refuses the second crossing"))
            ),
            instanceName: "row-b"
        );
        scenario.EnqueueCrossing();
        _ = StepUntilDeparted(scenario: scenario);

        var departedGeneration = scenario.Source.Server.Population.Generation(index: 0);

        SessionReply? joined = null;

        // Through the transport, which tapes the join, so the re-drive seats the new occupant too.
        ((LoopbackTransport)scenario.Source.Instance.Link).SubmitSession(
            completion: reply => joined = reply,
            request: new SessionRequest.Join(
                IdentityName: null,
                Principal: Principal.Seat(slot: 0),
                Slot: 0,
                WireProtocolKey: WorldProtocol.WireProtocolKey
            )
        );
        Assert.True(condition: joined?.Accepted, userMessage: joined?.Reason);
        Assert.NotEqual(expected: departedGeneration, actual: scenario.Source.Server.Population.Generation(index: 0));
        scenario.Step();
        scenario.EnqueueCrossing();
        scenario.Step();
        Assert.Equal(actual: crossings, expected: 2);
        Assert.True(condition: scenario.Source.Server.Population.IsActive(index: 0));
        AssertReplaysTickForTick(name: name, scenario: scenario);
    }
}
