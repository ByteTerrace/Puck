using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldAuthorityRetirementLawTests {
    [Fact]
    public void RetirementDrainsAcceptedEditsAndDoesNotAdvanceSimulation() {
        using var fixture = Fixtures.FreshServer();
        var server = fixture.Server;
        var kit = server.Definition.Kits[0] with { Name = "retirement-kit" };
        var tick = server.NextInputTick;
        server.Submit(new(0, 0, 1, 1, WorldPrincipal.Console,
            new WorldSubmissionPayload.Mutation(new WorldMutation.UpsertKit(WorldPrincipal.Console, kit)), Guid.NewGuid()));
        Assert.DoesNotContain(server.Definition.Kits, candidate => candidate.Name == kit.Name);

        server.FreezeForRetirement();

        Assert.True(server.IsRetiring);
        Assert.Contains(server.Definition.Kits, candidate => candidate.Name == kit.Name);
        Assert.Equal(tick, server.NextInputTick);
        Assert.False(server.DrainAdministrative());
        server.Advance(Fixtures.StepTicks);
        Assert.Equal(tick, server.NextInputTick);
        Assert.True(server.TryCaptureCheckpoint(EmptyHostRow(), out var first, out var reason), reason);
        server.FreezeForRetirement();
        Assert.True(server.TryCaptureCheckpoint(EmptyHostRow(), out var second, out reason), reason);
        Assert.Equal(WorldAuthorityCheckpointCodec.Encode(first!), WorldAuthorityCheckpointCodec.Encode(second!));
    }

    [Fact]
    public void RetiredActivationCannotRunAnExternalAuthorityClosure() {
        using var fixture = Fixtures.FreshServer();
        var executed = 0;
        fixture.Server.ExecuteAuthorityOperation(() => { executed++; });
        Assert.Equal(1, executed);
        fixture.Server.FreezeForRetirement();
        var error = Assert.Throws<InvalidOperationException>(() =>
            fixture.Server.ExecuteAuthorityOperation(() => { executed++; }));
        Assert.Contains("retiring", error.Message);
        Assert.Throws<InvalidOperationException>(() => fixture.Server.ExecuteAuthorityOperation(() => ++executed));
        Assert.Equal(1, executed);
    }

    [Fact]
    public void LateSubmissionCompletesWithNamedRefusalAndCannotChangeFrozenDefinition() {
        using var fixture = Fixtures.FreshServer();
        fixture.Server.FreezeForRetirement();
        var before = fixture.DefinitionBytes();
        WorldSubmissionResult? completion = null;
        var count = 0;
        var kit = fixture.Server.Definition.Kits[0] with { Name = "too-late" };
        fixture.Server.Submit(new(0, 0, 1, 1, WorldPrincipal.Console,
            new WorldSubmissionPayload.Mutation(new WorldMutation.UpsertKit(WorldPrincipal.Console, kit)), Guid.NewGuid()),
            result => { count++; completion = result; });
        var refusal = Assert.IsType<WorldSubmissionResult.Refusal>(completion);
        Assert.Equal("world.authority.retiring", refusal.Code);
        Assert.Equal(1, count);
        Assert.False(fixture.Server.DrainAdministrative());
        Assert.Equal(before, fixture.DefinitionBytes());
    }

    private static WorldAuthorityHostRowCheckpoint EmptyHostRow() => new(
        AnnouncedCrossingHolds: [], AppliedTransferHighWater: null, AppliedTransferIds: [],
        ElapsedEngineTicks: 0, ForwardedBodies: [], FreshCounter: 0, InDoubtTransfers: [],
        IsPaused: false, NextTransferId: 1, PortalOccupancy: [], Retained: false,
        ScheduleAccumulatorTicks: 0, SeededArrivals: []);
}
