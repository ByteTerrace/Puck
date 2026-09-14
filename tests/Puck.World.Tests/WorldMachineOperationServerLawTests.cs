using System.Text.Json;
using Puck.Abstractions.Machines;
using Puck.World.Machines;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Direct ordered-domain laws for canonical machine operation adoption and authority/CAS refusals.</summary>
public sealed class WorldMachineOperationServerLawTests {
    private static JsonElement Config(string model) {
        using var document = JsonDocument.Parse($"{{\"schema\":\"puck.operation-test.config.v1\",\"model\":\"{model}\"}}");

        return document.RootElement.Clone();
    }
    private static byte[] DefinitionBytes(WorldServer server) => WorldDefinitionSerialization.Serialize(definition: server.Definition);
    private static WorldDefinition Document() => Fixtures.BuildDocument() with {
        MachinesRaw = [
            new WorldMachine(
            "cabinet",
            "operation-test",
            Config(model: "base")
        ),
            new WorldMachine(
            "other",
            "operation-test",
            Config(model: "base")
        ),
        ],
        ScreensRaw = null,
    };
    private static WorldMachineOperation Operation(string instance, ulong generation, string model) {
        using var document = JsonDocument.Parse($"{{\"schema\":\"puck.operation-test.model.v1\",\"model\":\"{model}\"}}");

        return new WorldMachineOperation(
            instance,
            generation,
            "device.model",
            document.RootElement
        );
    }
    private static WorldSubmissionResult Submit(WorldServer server, WorldPrincipal principal, WorldMachineOperation operation) {
        WorldSubmissionResult? completion = null;

        server.Submit(
            new SubmissionEnvelope(
                SubmissionEnvelope.LocalConnectionId,
                0,
                1,
                1,
                principal,
                new WorldSubmissionPayload.Operation(Value: operation)
            ),
            result => completion = result
        );
        Assert.NotNull(@object: completion);
        return completion!;
    }

    [Fact]
    public void AppliedMachineOperationLatchesUncapturableStateGuard() {
        var engine = new OperationEngine();
        using var fixture = Fixtures.FreshServer(
            Document(),
            machineCatalog: new WorldMachineCatalog([engine])
        );
        var actor = WorldPrincipal.Addon(name: "operator");

        fixture.Server.Grant(
            new WorldGrant(
                actor,
                WorldCapability.Control,
                GrantSubject.Machine(name: "cabinet"),
                false
            ),
            WorldPrincipal.Console
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
        var actor = WorldPrincipal.Addon(name: "operator");

        fixture.Server.Grant(
            new WorldGrant(
                actor,
                WorldCapability.Control,
                GrantSubject.Machine(name: "cabinet"),
                false
            ),
            WorldPrincipal.Console
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
            WorldPrincipal.Console,
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
        var actor = WorldPrincipal.Addon(name: "operator");

        fixture.Server.Grant(
            new WorldGrant(
                actor,
                WorldCapability.Control,
                GrantSubject.Machine(name: "cabinet"),
                false
            ),
            WorldPrincipal.Console
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
            WorldPrincipal.Addon(name: "blocked"),
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

        var actor = WorldPrincipal.Addon(name: "operator");

        fixture.Server.Grant(
            new WorldGrant(
                actor,
                WorldCapability.Control,
                GrantSubject.Machine(name: "cabinet"),
                false
            ),
            WorldPrincipal.Console
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

    private sealed class OperationEngine : IMachineEngine, IMachineOperationProvider {
        public string Id => "operation-test";

        public List<OperationRuntime> Created { get; } = [];
        public MachineEngineDescriptor Descriptor { get; } = new(
            "operation-test",
            "Ordered operation test device",
            new(
                "puck.operation-test.config.v1",
                [new(
                        "model",
                        MachineFieldKind.String,
                        "Current model",
                        Required: true,
                        Choices: ["base", "next", "other"]
                    )]
            ),
            [],
            [],
            [],
            [new(
                    "device.model",
                    "Changes the live model",
                    new(
                        "puck.operation-test.model.v1",
                        [new(
                                "model",
                                MachineFieldKind.String,
                                "New model",
                                Required: true,
                                Choices: ["base", "next", "other"]
                            )]
                    )
                )],
            []
        );

        public IMachineRuntime Create(string? options, byte[]? contentBytes = null, string? savePath = null, int audioSampleRate = 0) =>
            new OperationRuntime(model: (options ?? "base"));
        public IMachineRuntime CreateMachine(MachineCreationRequest request) {
            var runtime = new OperationRuntime(model: request.Configuration.GetProperty(propertyName: "model").GetString()!);

            Created.Add(item: runtime);
            return runtime;
        }
        public MachineOperationPreparation PrepareOperation(MachineCreationRequest current, MachineOperationRequest request) {
            if (!MachineOperationValidation.TryValidate(
                Descriptor,
                request,
                out _,
                out var failure
            )) {
                return new MachineOperationPreparation.Refusal(result: failure);
            }
            return new MachineOperationPreparation.Runtime(
                new SetModelOperation(model: request.Payload.GetProperty(propertyName: "model").GetString()!),
                Config(model: request.Payload.GetProperty(propertyName: "model").GetString()!)
            );
        }

        private sealed class SetModelOperation(string model) : IMachinePreparedOperation {
            public MachineOperationResult Apply(IMachineRuntime runtime) {
                var target = Assert.IsType<OperationRuntime>(@object: runtime);

                target.Model = model;
                return new MachineOperationResult(
                    MachineOperationStatus.Applied,
                    reason: "model applied"
                );
            }
        }
    }
    private sealed class OperationRuntime(string model) : IMachineRuntime {
        public string Model { get; set; } = model;
        public MachineRuntimeStatus Status => MachineRuntimeStatus.Running;

        public bool Advance(ulong deltaTicks) => true;
        public void Dispose() { }
    }
}
