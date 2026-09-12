using System.Text.Json;
using Puck.Abstractions.Machines;
using Puck.World.Machines;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Direct ordered-domain laws for canonical machine operation adoption and authority/CAS refusals.</summary>
public sealed class WorldMachineOperationServerLawTests {
    [Fact]
    public void RecordingTapRefusesMachineOperationWithoutMutation() {
        var engine = new OperationEngine();
        using var fixture = Fixtures.FreshServer(Document(), machineCatalog: new WorldMachineCatalog([engine]));
        var actor = WorldPrincipal.Addon("operator");
        fixture.Server.Grant(new WorldGrant(actor, WorldCapability.Control, GrantSubject.Machine("cabinet"), false), WorldPrincipal.Console);
        var generation = fixture.Server.Machines.InstanceState("cabinet")!.Value.Generation;
        var before = DefinitionBytes(fixture.Server);
        fixture.Server.ScreenOpTap = (_, _, _) => { };

        var completion = Submit(fixture.Server, actor, Operation("cabinet", generation, "next"));
        var refused = Assert.IsType<WorldSubmissionResult.MachineOperation>(completion).Result;
        Assert.Equal(MachineOperationStatus.Refused, refused.Status);
        Assert.Contains("record", refused.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, DefinitionBytes(fixture.Server));
        Assert.Equal(generation, fixture.Server.Machines.InstanceState("cabinet")!.Value.Generation);
        Assert.Equal("base", engine.Created[0].Model);
        Assert.False(fixture.Server.AnyScreenOpEverApplied);
    }

    [Fact]
    public void AppliedMachineOperationLatchesUncapturableStateGuard() {
        var engine = new OperationEngine();
        using var fixture = Fixtures.FreshServer(Document(), machineCatalog: new WorldMachineCatalog([engine]));
        var actor = WorldPrincipal.Addon("operator");
        fixture.Server.Grant(new WorldGrant(actor, WorldCapability.Control, GrantSubject.Machine("cabinet"), false), WorldPrincipal.Console);
        var generation = fixture.Server.Machines.InstanceState("cabinet")!.Value.Generation;
        Assert.False(fixture.Server.AnyScreenOpEverApplied);

        var completion = Submit(fixture.Server, actor, Operation("cabinet", generation, "next"));
        var applied = Assert.IsType<WorldSubmissionResult.MachineOperation>(completion).Result;
        Assert.Equal(MachineOperationStatus.Applied, applied.Status);
        Assert.True(fixture.Server.AnyScreenOpEverApplied);
        Assert.Equal("next", engine.Created[0].Model);
    }

    [Fact]
    public void AppliedOperationAdoptsCanonicalDefinitionAndSurvivesAnUnrelatedMutation() {
        var engine = new OperationEngine();
        var definition = Document();
        using var fixture = Fixtures.FreshServer(definition, machineCatalog: new WorldMachineCatalog([engine]));
        var actor = WorldPrincipal.Addon("operator");
        fixture.Server.Grant(new WorldGrant(actor, WorldCapability.Control, GrantSubject.Machine("cabinet"), false), WorldPrincipal.Console);
        var generation = fixture.Server.Machines.InstanceState("cabinet")!.Value.Generation;

        var completion = Submit(fixture.Server, actor, Operation("cabinet", generation, "next"));
        var applied = Assert.IsType<WorldSubmissionResult.MachineOperation>(completion).Result;
        Assert.Equal(MachineOperationStatus.Applied, applied.Status);
        Assert.Equal("next", fixture.Server.Definition.Machines.Single(row => row.Name == "cabinet").Configuration.GetProperty("model").GetString());
        Assert.Equal("next", engine.Created[0].Model);

        fixture.Server.EnqueueMutation(new WorldMutation.UpsertMachine(
            WorldPrincipal.Console,
            new WorldMachine("other", "operation-test", Config("other"))
        ));
        fixture.Step();

        Assert.Equal("next", fixture.Server.Definition.Machines.Single(row => row.Name == "cabinet").Configuration.GetProperty("model").GetString());
        Assert.Equal("other", fixture.Server.Definition.Machines.Single(row => row.Name == "other").Configuration.GetProperty("model").GetString());
        using var readback = JsonDocument.Parse(DefinitionBytes(fixture.Server));
        Assert.Equal("next", readback.RootElement.GetProperty("machines").EnumerateArray().Single(row => row.GetProperty("name").GetString() == "cabinet").GetProperty("configuration").GetProperty("model").GetString());
    }

    [Fact]
    public void UnauthorizedAndStaleOperationsLeaveRuntimeGenerationAndDefinitionUnchanged() {
        var engine = new OperationEngine();
        using var fixture = Fixtures.FreshServer(Document(), machineCatalog: new WorldMachineCatalog([engine]));
        var generation = fixture.Server.Machines.InstanceState("cabinet")!.Value.Generation;
        var before = DefinitionBytes(fixture.Server);
        var blocked = Submit(fixture.Server, WorldPrincipal.Addon("blocked"), Operation("cabinet", generation, "next"));
        Assert.Equal(MachineOperationStatus.Refused, Assert.IsType<WorldSubmissionResult.MachineOperation>(blocked).Result.Status);
        Assert.Equal(before, DefinitionBytes(fixture.Server));
        Assert.Equal(generation, fixture.Server.Machines.InstanceState("cabinet")!.Value.Generation);
        Assert.Equal("base", engine.Created[0].Model);

        var actor = WorldPrincipal.Addon("operator");
        fixture.Server.Grant(new WorldGrant(actor, WorldCapability.Control, GrantSubject.Machine("cabinet"), false), WorldPrincipal.Console);
        var stale = Submit(fixture.Server, actor, Operation("cabinet", generation - 1, "next"));
        Assert.Equal(MachineOperationStatus.Refused, Assert.IsType<WorldSubmissionResult.MachineOperation>(stale).Result.Status);
        Assert.Equal(before, DefinitionBytes(fixture.Server));
        Assert.Equal(generation, fixture.Server.Machines.InstanceState("cabinet")!.Value.Generation);
        Assert.Equal("base", engine.Created[0].Model);
    }

    private static byte[] DefinitionBytes(WorldServer server) => WorldDefinitionSerialization.Serialize(server.Definition);

    private static WorldSubmissionResult Submit(WorldServer server, WorldPrincipal principal, WorldMachineOperation operation) {
        WorldSubmissionResult? completion = null;
        server.Submit(new SubmissionEnvelope(
            SubmissionEnvelope.LocalConnectionId,
            0,
            1,
            1,
            principal,
            new WorldSubmissionPayload.Operation(operation)
        ), result => completion = result);
        Assert.NotNull(completion);
        return completion!;
    }

    private static WorldMachineOperation Operation(string instance, ulong generation, string model) {
        using var document = JsonDocument.Parse($"{{\"schema\":\"puck.operation-test.model.v1\",\"model\":\"{model}\"}}");
        return new WorldMachineOperation(instance, generation, "device.model", document.RootElement);
    }

    private static WorldDefinition Document() => Fixtures.BuildDocument() with {
        MachinesRaw = [
            new WorldMachine("cabinet", "operation-test", Config("base")),
            new WorldMachine("other", "operation-test", Config("base")),
        ],
        ScreensRaw = null,
    };

    private static JsonElement Config(string model) {
        using var document = JsonDocument.Parse($"{{\"schema\":\"puck.operation-test.config.v1\",\"model\":\"{model}\"}}");
        return document.RootElement.Clone();
    }

    private sealed class OperationEngine : IMachineEngine, IMachineOperationProvider {
        public string Id => "operation-test";
        public List<OperationRuntime> Created { get; } = [];
        public MachineEngineDescriptor Descriptor { get; } = new(
            "operation-test",
            "Ordered operation test device",
            new("puck.operation-test.config.v1", [new("model", MachineFieldKind.String, "Current model", Required: true, Choices: ["base", "next", "other"])]),
            [], [], [],
            [new("device.model", "Changes the live model", new("puck.operation-test.model.v1", [new("model", MachineFieldKind.String, "New model", Required: true, Choices: ["base", "next", "other"])]))],
            []
        );

        public IMachineRuntime CreateMachine(MachineCreationRequest request) {
            var runtime = new OperationRuntime(request.Configuration.GetProperty("model").GetString()!);
            Created.Add(runtime);
            return runtime;
        }

        public IMachineRuntime Create(string? options, byte[]? contentBytes = null, string? savePath = null, int audioSampleRate = 0) =>
            new OperationRuntime(options ?? "base");

        public MachineOperationPreparation PrepareOperation(MachineCreationRequest current, MachineOperationRequest request) {
            if (!MachineOperationValidation.TryValidate(Descriptor, request, out _, out var failure)) {
                return new MachineOperationPreparation.Refusal(failure);
            }
            return new MachineOperationPreparation.Runtime(
                new SetModelOperation(request.Payload.GetProperty("model").GetString()!),
                Config(request.Payload.GetProperty("model").GetString()!)
            );
        }

        private sealed class SetModelOperation(string model) : IMachinePreparedOperation {
            public MachineOperationResult Apply(IMachineRuntime runtime) {
                var target = Assert.IsType<OperationRuntime>(runtime);
                target.Model = model;
                return new MachineOperationResult(MachineOperationStatus.Applied, reason: "model applied");
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