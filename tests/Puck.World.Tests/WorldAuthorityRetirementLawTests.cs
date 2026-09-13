using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldAuthorityRetirementLawTests {
    private static WorldAuthorityHostRowCheckpoint EmptyHostRow() => new(
        AnnouncedCrossingHolds: [],
        AppliedTransferHighWater: null,
        AppliedTransferIds: [],
        ElapsedEngineTicks: 0,
        ForwardedBodies: [],
        FreshCounter: 0,
        InDoubtTransfers: [],
        IsPaused: false,
        NextTransferId: 1,
        PortalOccupancy: [],
        Retained: false,
        ScheduleAccumulatorTicks: 0,
        SeededArrivals: []
    );

    [Fact]
    public void LateSubmissionCompletesWithNamedRefusalAndCannotChangeFrozenDefinition() {
        using var fixture = Fixtures.FreshServer();

        fixture.Server.FreezeForRetirement();
        var before = fixture.DefinitionBytes();
        WorldSubmissionResult? completion = null;
        var count = 0;
        var kit = fixture.Server.Definition.Kits[0] with { Name = "too-late" };

        fixture.Server.Submit(
            new(
                0,
                0,
                1,
                1,
                WorldPrincipal.Console,
                new WorldSubmissionPayload.Mutation(Value: new WorldMutation.UpsertKit(
                    WorldPrincipal.Console,
                    kit
                )),
                Guid.NewGuid()
            ),
            result => { count++; completion = result; }
        );
        var refusal = Assert.IsType<WorldSubmissionResult.Refusal>(@object: completion);

        Assert.Equal(
            "world.authority.retiring",
            refusal.Code
        );
        Assert.Equal(
            actual: count,
            expected: 1
        );
        Assert.False(condition: fixture.Server.DrainAdministrative());
        Assert.Equal(
            before,
            fixture.DefinitionBytes()
        );
    }
    [Fact]
    public void RetiredActivationCannotRunAnExternalAuthorityClosure() {
        using var fixture = Fixtures.FreshServer();
        var executed = 0;

        fixture.Server.ExecuteAuthorityOperation(operation: () => { executed++; });
        Assert.Equal(
            actual: executed,
            expected: 1
        );
        fixture.Server.FreezeForRetirement();
        var error = Assert.Throws<InvalidOperationException>(testCode: () =>
            fixture.Server.ExecuteAuthorityOperation(operation: () => { executed++; }));

        Assert.Contains(
            "retiring",
            error.Message
        );
        Assert.Throws<InvalidOperationException>(testCode: () => fixture.Server.ExecuteAuthorityOperation(operation: () => ++executed));
        Assert.Equal(
            actual: executed,
            expected: 1
        );
    }
    [Fact]
    public void RetirementDrainsAcceptedEditsAndDoesNotAdvanceSimulation() {
        using var fixture = Fixtures.FreshServer();
        var server = fixture.Server;
        var kit = server.Definition.Kits[0] with { Name = "retirement-kit" };
        var tick = server.NextInputTick;

        server.Submit(new(
            0,
            0,
            1,
            1,
            WorldPrincipal.Console,
            new WorldSubmissionPayload.Mutation(Value: new WorldMutation.UpsertKit(
                WorldPrincipal.Console,
                kit
            )),
            Guid.NewGuid()
        ));
        Assert.DoesNotContain(
            collection: server.Definition.Kits,
            filter: candidate => (candidate.Name == kit.Name)
        );

        server.FreezeForRetirement();

        Assert.True(condition: server.IsRetiring);
        Assert.Contains(
            collection: server.Definition.Kits,
            filter: candidate => (candidate.Name == kit.Name)
        );
        Assert.Equal(
            tick,
            server.NextInputTick
        );
        Assert.False(condition: server.DrainAdministrative());
        server.Advance(stepTicks: Fixtures.StepTicks);
        Assert.Equal(
            tick,
            server.NextInputTick
        );
        Assert.True(
            condition: server.TryCaptureCheckpoint(
                EmptyHostRow(),
                out var first,
                out var reason
            ),
            userMessage: reason
        );
        server.FreezeForRetirement();
        Assert.True(
            condition: server.TryCaptureCheckpoint(
                EmptyHostRow(),
                out var second,
                out reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            WorldAuthorityCheckpointCodec.Encode(checkpoint: first!),
            WorldAuthorityCheckpointCodec.Encode(checkpoint: second!)
        );
    }
}
