using Xunit;

using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>Durable recovery when an authority dies mid-crossing. Each side keeps a checkpoint taken before the crossing
/// and a crossing log written ahead of every step a peer can see: the source's departure before its commit and its
/// settlement before its acknowledgement or its return, the destination's arrival before its answer. The dead side
/// restarts from that image, the survivor carries on, and the drain reconciles. Whatever step the death interrupts, the
/// traveler ends on exactly one authority — arrived, or never departed — and no transfer stays in doubt. The red legs
/// run the same deaths without a log: the restarted source holds the traveler the destination also holds, or neither
/// side holds it.</summary>
public sealed class CrossingRecoveryLawTests {
    // Steps long enough for any destination lease taken during the crossing to expire.
    private const int PastEveryLease = 90;

    [Fact]
    public void ARefusedDurableArrivalDoesNotAdvanceTheRecoveryWatermark() {
        using var world = CrossingWorld.Build();

        _ = world.Cross();
        var arrival = world.DestinationLog.Read(defaults: world.Destination.Server.Definition.PlayerDefaults);
        using var restarted = world.Restart(sourceDied: true, destinationDied: true, destinationLogLost: true);
        var server = restarted.Destination.Server;

        Assert.True(condition: server.ApplySession(request: new Puck.World.Protocol.SessionRequest.Join(
            IdentityName: null,
            Principal: Puck.Commands.Principal.Seat(slot: 0),
            Slot: 0,
            WireProtocolKey: Puck.World.Protocol.WorldProtocol.WireProtocolKey
        )).Accepted);

        var failure = Assert.Throws<InvalidOperationException>(testCode: () => restarted.Host.RecoverCrossings(
            row: restarted.Destination.Instance,
            entries: arrival
        ));

        Assert.Contains(expectedSubstring: "crossing recovery refused", actualString: failure.Message);
        Assert.Equal(expected: 0UL, actual: server.CrossingSequence);

        _ = server.Population.TryDetachSeatForTransfer(profile: out _, slot: 0);
        restarted.Host.RecoverCrossings(row: restarted.Destination.Instance, entries: arrival);
        restarted.Host.RecoverCrossings(row: restarted.Destination.Instance, entries: arrival);
        Assert.Equal(expected: 1UL, actual: server.CrossingSequence);
        Assert.Equal(expected: 1, actual: restarted.CountTraveler());
    }

    private static void AssertConverged(CrossingWorld world, bool arrived) {
        world.Step(ticks: PastEveryLease);
        Assert.Equal(
            expected: 1,
            actual: world.CountTraveler()
        );
        Assert.Equal(
            expected: arrived,
            actual: world.TravelerAt(row: world.Destination)
        );
        Assert.Empty(collection: world.Host.CaptureRow(row: world.Source.Instance).InDoubtTransfers);
    }

    [Fact]
    public void TheSourceDiesBeforeItsDepartureIsDurable_AndTheTravelerNeverLeft() {
        using var world = CrossingWorld.Build();

        world.SourceLog.CrashBefore = typeof(WorldCrossingRecord.Departure);
        _ = Assert.Throws<AuthorityCrashedException>(testCode: () => world.Cross());
        Assert.Equal(
            expected: 0,
            actual: world.SourceLog.Count
        );

        using var restarted = world.Restart(
            destinationDied: false,
            sourceDied: true
        );

        AssertConverged(
            arrived: false,
            world: restarted
        );
    }
    [Fact]
    public void TheSourceDiesAfterItsDepartureIsDurable_BeforeItsCommitIsSent_AndTheTravelerNeverLeft() {
        using var world = CrossingWorld.Build();

        world.SourceLog.CrashAfter = typeof(WorldCrossingRecord.Departure);
        _ = Assert.Throws<AuthorityCrashedException>(testCode: () => world.Cross());

        using var restarted = world.Restart(
            destinationDied: false,
            sourceDied: true
        );

        // The restarted source finds the transfer in doubt, and the destination's lease answers the retried commit.
        AssertConverged(
            arrived: true,
            world: restarted
        );
    }
    [Fact]
    public void TheSourceDiesAfterTheDestinationCommits_BeforeItSettles_AndTheTravelerArrived() {
        using var world = CrossingWorld.Build();

        world.SourceLog.CrashBefore = typeof(WorldCrossingRecord.Settlement);
        _ = Assert.Throws<AuthorityCrashedException>(testCode: () => world.Cross());
        Assert.True(condition: world.TravelerAt(row: world.Destination));

        using var restarted = world.Restart(
            destinationDied: false,
            sourceDied: true
        );

        AssertConverged(
            arrived: true,
            world: restarted
        );
    }
    [Fact]
    public void TheSourceDiesAfterItSettles_AndTheTravelerArrived() {
        using var world = CrossingWorld.Build();

        world.SourceLog.CrashAfter = typeof(WorldCrossingRecord.Settlement);
        _ = Assert.Throws<AuthorityCrashedException>(testCode: () => world.Cross());

        using var restarted = world.Restart(
            destinationDied: false,
            sourceDied: true
        );

        AssertConverged(
            arrived: true,
            world: restarted
        );
    }
    [Fact]
    public void TheDestinationDiesBeforeItsArrivalIsDurable_AndTheTravelerNeverLeft() {
        using var world = CrossingWorld.Build();

        world.Host.SetPeerCallFault(
            fault: new DyingDestinationPeerCall(destination: world.Destination.Server),
            instanceName: "row-b"
        );
        world.DestinationLog.CrashBefore = typeof(WorldCrossingRecord.Arrival);
        _ = world.Cross();
        Assert.Single(collection: world.Host.CaptureRow(row: world.Source.Instance).InDoubtTransfers);

        using var restarted = world.Restart(
            destinationDied: true,
            sourceDied: false
        );

        AssertConverged(
            arrived: false,
            world: restarted
        );
    }
    [Fact]
    public void TheDestinationDiesAfterItsArrivalIsDurable_BeforeItAnswers_AndTheTravelerArrived() {
        using var world = CrossingWorld.Build();

        world.Host.SetPeerCallFault(
            fault: new DyingDestinationPeerCall(destination: world.Destination.Server),
            instanceName: "row-b"
        );
        world.DestinationLog.CrashAfter = typeof(WorldCrossingRecord.Arrival);
        _ = world.Cross();

        using var restarted = world.Restart(
            destinationDied: true,
            sourceDied: false
        );

        // The destination's checkpoint predates the crossing; its log lands the cohort again before it answers.
        AssertConverged(
            arrived: true,
            world: restarted
        );
    }
    [Fact]
    public void TheDestinationDiesAfterTheSourceSettles_AndTheTravelerArrived() {
        using var world = CrossingWorld.Build();

        _ = world.Cross();
        Assert.True(condition: world.TravelerAt(row: world.Destination));

        using var restarted = world.Restart(
            destinationDied: true,
            sourceDied: false
        );

        AssertConverged(
            arrived: true,
            world: restarted
        );
    }
    [Fact]
    public void BothDieAfterTheCommit_AndTheTravelerArrived() {
        using var world = CrossingWorld.Build();

        world.Host.SetPeerCallFault(
            fault: new DyingDestinationPeerCall(destination: world.Destination.Server) { LoseCommitAnswer = true },
            instanceName: "row-b"
        );
        _ = world.Cross();

        using var restarted = world.Restart(
            destinationDied: true,
            sourceDied: true
        );

        AssertConverged(
            arrived: true,
            world: restarted
        );
    }
    [Fact]
    public void WithoutTheSourcesLog_ARestartedSourceDuplicatesTheTraveler() {
        using var world = CrossingWorld.Build();

        _ = world.Cross();
        Assert.True(condition: world.TravelerAt(row: world.Destination));

        // The red leg: the source restarts from its checkpoint alone, as if it had logged nothing.
        using var restarted = world.Restart(
            destinationDied: false,
            sourceDied: true,
            sourceLogLost: true
        );

        restarted.Step(ticks: PastEveryLease);
        Assert.Equal(
            expected: 2,
            actual: restarted.CountTraveler()
        );
    }
    [Fact]
    public void WithoutTheDestinationsLog_ARestartedDestinationLosesTheTraveler() {
        using var world = CrossingWorld.Build();

        _ = world.Cross();

        // The red leg: the destination restarts from its checkpoint alone after the source already settled.
        using var restarted = world.Restart(
            destinationDied: true,
            destinationLogLost: true,
            sourceDied: false
        );

        restarted.Step(ticks: PastEveryLease);
        Assert.Equal(
            expected: 0,
            actual: restarted.CountTraveler()
        );
    }
}
