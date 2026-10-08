using Puck.Commands;
using System.Text.Json;
using Puck.Abstractions.Machines;
using Puck.World.Machines;
using Puck.World.Protocol;
using Xunit;
using static Puck.World.Testing.MachineOperationFixtures;

namespace Puck.World.Server.Tests;

/// <summary>Direct ordered-domain laws for canonical machine operation adoption and authority/CAS refusals.</summary>
public sealed class WorldMachineOperationServerLawTests {
    private static byte[] DefinitionBytes(WorldServer server) => WorldDefinitionSerialization.Serialize(definition: server.Definition);

    [Fact]
    public void AppliedMachineOperationLatchesUncapturableStateGuard() {
        var engine = new OperationEngine();
        using var fixture = Fixtures.FreshServer(
            Document(),
            machineCatalog: new WorldMachineCatalog([engine])
        );
        var actor = Principal.Addon(name: "operator");

        fixture.Server.Grant(
            new WorldGrant(
                actor,
                WorldCapability.Control,
                GrantSubject.Machine(name: "cabinet"),
                false
            ),
            Principal.Console
        );
        var generation = fixture.Server.Machines.InstanceState(name: "cabinet")!.Value.Generation;

        Assert.False(condition: fixture.Server.AnyScreenOpEverApplied);

        var completion = Submit(
            fixture.Server,
            actor,
            Operation(
                generation: generation,
                instance: "cabinet",
                model: "next"
            )
        );
        var applied = Assert.IsType<WorldSubmissionResult.MachineOperation>(@object: completion).Result;

        Assert.Equal(
            MachineOperationStatus.Applied,
            applied.Status
        );
        Assert.True(condition: fixture.Server.AnyScreenOpEverApplied);
        Assert.Equal(
            "next",
            engine.Created[0].Model
        );
    }
    [Fact]
    public void AppliedOperationAdoptsCanonicalDefinitionAndSurvivesAnUnrelatedMutation() {
        var engine = new OperationEngine();
        var definition = Document();
        using var fixture = Fixtures.FreshServer(
            definition,
            machineCatalog: new WorldMachineCatalog([engine])
        );
        var actor = Principal.Addon(name: "operator");

        fixture.Server.Grant(
            new WorldGrant(
                actor,
                WorldCapability.Control,
                GrantSubject.Machine(name: "cabinet"),
                false
            ),
            Principal.Console
        );
        var generation = fixture.Server.Machines.InstanceState(name: "cabinet")!.Value.Generation;

        var completion = Submit(
            fixture.Server,
            actor,
            Operation(
                generation: generation,
                instance: "cabinet",
                model: "next"
            )
        );
        var applied = Assert.IsType<WorldSubmissionResult.MachineOperation>(@object: completion).Result;

        Assert.Equal(
            MachineOperationStatus.Applied,
            applied.Status
        );
        Assert.Equal(
            "next",
            fixture.Server.Definition.Machines.Single(predicate: row => (row.Name == "cabinet")).Configuration.GetProperty(propertyName: "model").GetString()
        );
        Assert.Equal(
            "next",
            engine.Created[0].Model
        );

        fixture.Server.EnqueueMutation(new WorldMutation.UpsertMachine(
            Principal.Console,
            new WorldMachine(
                "other",
                "operation-test",
                Config(model: "other")
            )
        ));
        fixture.Step();

        Assert.Equal(
            "next",
            fixture.Server.Definition.Machines.Single(predicate: row => (row.Name == "cabinet")).Configuration.GetProperty(propertyName: "model").GetString()
        );
        Assert.Equal(
            "other",
            fixture.Server.Definition.Machines.Single(predicate: row => (row.Name == "other")).Configuration.GetProperty(propertyName: "model").GetString()
        );
        using var readback = JsonDocument.Parse(DefinitionBytes(server: fixture.Server));

        Assert.Equal(
            "next",
            readback.RootElement.GetProperty(propertyName: "machines").EnumerateArray().Single(predicate: row => (row.GetProperty(propertyName: "name").GetString() == "cabinet")).GetProperty(propertyName: "configuration").GetProperty(propertyName: "model").GetString()
        );
    }
    [Fact]
    public void RecordingTapRefusesMachineOperationWithoutMutation() {
        var engine = new OperationEngine();
        using var fixture = Fixtures.FreshServer(
            Document(),
            machineCatalog: new WorldMachineCatalog([engine])
        );
        var actor = Principal.Addon(name: "operator");

        fixture.Server.Grant(
            new WorldGrant(
                actor,
                WorldCapability.Control,
                GrantSubject.Machine(name: "cabinet"),
                false
            ),
            Principal.Console
        );
        var generation = fixture.Server.Machines.InstanceState(name: "cabinet")!.Value.Generation;
        var before = DefinitionBytes(server: fixture.Server);

        fixture.Server.ScreenOpTap = (_, _, _) => { };

        var completion = Submit(
            fixture.Server,
            actor,
            Operation(
                generation: generation,
                instance: "cabinet",
                model: "next"
            )
        );
        var refused = Assert.IsType<WorldSubmissionResult.MachineOperation>(@object: completion).Result;

        Assert.Equal(
            MachineOperationStatus.Refused,
            refused.Status
        );
        Assert.Contains(
            "record",
            refused.Reason,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Equal(
            before,
            DefinitionBytes(server: fixture.Server)
        );
        Assert.Equal(
            generation,
            fixture.Server.Machines.InstanceState(name: "cabinet")!.Value.Generation
        );
        Assert.Equal(
            "base",
            engine.Created[0].Model
        );
        Assert.False(condition: fixture.Server.AnyScreenOpEverApplied);
    }
    [Fact]
    public void UnauthorizedAndStaleOperationsLeaveRuntimeGenerationAndDefinitionUnchanged() {
        var engine = new OperationEngine();
        using var fixture = Fixtures.FreshServer(
            Document(),
            machineCatalog: new WorldMachineCatalog([engine])
        );
        var generation = fixture.Server.Machines.InstanceState(name: "cabinet")!.Value.Generation;
        var before = DefinitionBytes(server: fixture.Server);
        var blocked = Submit(
            fixture.Server,
            Principal.Addon(name: "blocked"),
            Operation(
                generation: generation,
                instance: "cabinet",
                model: "next"
            )
        );

        Assert.Equal(
            MachineOperationStatus.Refused,
            Assert.IsType<WorldSubmissionResult.MachineOperation>(@object: blocked).Result.Status
        );
        Assert.Equal(
            before,
            DefinitionBytes(server: fixture.Server)
        );
        Assert.Equal(
            generation,
            fixture.Server.Machines.InstanceState(name: "cabinet")!.Value.Generation
        );
        Assert.Equal(
            "base",
            engine.Created[0].Model
        );

        var actor = Principal.Addon(name: "operator");

        fixture.Server.Grant(
            new WorldGrant(
                actor,
                WorldCapability.Control,
                GrantSubject.Machine(name: "cabinet"),
                false
            ),
            Principal.Console
        );
        var stale = Submit(
            fixture.Server,
            actor,
            Operation(
                generation: (generation - 1),
                instance: "cabinet",
                model: "next"
            )
        );

        Assert.Equal(
            MachineOperationStatus.Refused,
            Assert.IsType<WorldSubmissionResult.MachineOperation>(@object: stale).Result.Status
        );
        Assert.Equal(
            before,
            DefinitionBytes(server: fixture.Server)
        );
        Assert.Equal(
            generation,
            fixture.Server.Machines.InstanceState(name: "cabinet")!.Value.Generation
        );
        Assert.Equal(
            "base",
            engine.Created[0].Model
        );
    }
}
