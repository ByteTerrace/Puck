using Xunit;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions.Machines;
using Puck.AdvancedGamingBrick;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge.Tune;

namespace Puck.World.Tests;

public sealed class MachineOperationProviderLawTests {
    [Fact]
    public void UnknownWrongTypedAndMissingPayloadsRefuseBeforeAnyReplacementIsPrepared() {
        var engine = new GamingBrickEngine();
        var current = CurrentConfiguration("puck.gaming-brick.config.v1");
        using var unknownPayload = Payload("puck.gaming-brick.unknown.v1", "{}");
        using var wrongTypePayload = Payload("puck.gaming-brick.content-insert.v1", "{\"content\":{\"path\":3}}");
        using var missingPayload = Payload("puck.gaming-brick.content-insert.v1", "{\"content\":{}}");
        var provider = (IMachineOperationProvider)engine;

        var unknown = provider.PrepareOperation(current, new("unknown", unknownPayload.RootElement));
        var wrongType = provider.PrepareOperation(current, new("content.insert", wrongTypePayload.RootElement));
        var missing = provider.PrepareOperation(current, new("content.insert", missingPayload.RootElement));

        Assert.Equal(MachineOperationStatus.Unsupported, Assert.IsType<MachineOperationPreparation.Refusal>(unknown).Result.Status);
        Assert.Equal(MachineOperationStatus.Refused, Assert.IsType<MachineOperationPreparation.Refusal>(wrongType).Result.Status);
        Assert.Equal(MachineOperationStatus.Refused, Assert.IsType<MachineOperationPreparation.Refusal>(missing).Result.Status);
    }

    [Fact]
    public void InsertPreparesAHostReplacementWithoutTouchingTheLiveMachine() {
        var engine = new GamingBrickEngine();
        var current = CurrentConfiguration("puck.gaming-brick.config.v1");
        using var runtime = engine.Create(options: "cgb fast");
        using var payload = Payload("puck.gaming-brick.content-insert.v1", "{\"content\":{\"path\":\"new.gb\"}}");
        var prepared = ((IMachineOperationProvider)engine).PrepareOperation(current, new("content.insert", payload.RootElement));
        var replacement = Assert.IsType<MachineOperationPreparation.Replacement>(prepared);

        Assert.Equal(MachineRuntimeStatus.Empty, runtime.Status);
        Assert.Equal("new.gb", replacement.Configuration.GetProperty("content").GetProperty("path").GetString());
        Assert.Equal("puck.gaming-brick.config.v1", replacement.Configuration.GetProperty("schema").GetString());
    }

    [Fact]
    public void HumbleDeviceModelOperationUsesTheRealRuntimeAndReturnsTheAuthoredToken() {
        var engine = new GamingBrickEngine();
        var current = CurrentConfiguration("puck.gaming-brick.config.v1");
        using var runtime = engine.Create(options: "cgb fast", contentBytes: new byte[0x8000]);
        using var payload = Payload("puck.gaming-brick.device-model.v1", "{\"model\":\"dmgc\"}");
        var prepared = Assert.IsType<MachineOperationPreparation.Runtime>(((IMachineOperationProvider)engine).PrepareOperation(current, new("device.model", payload.RootElement)));

        var result = prepared.Operation.Apply(runtime);

        Assert.Equal(MachineOperationStatus.Applied, result.Status);
        Assert.Equal("dmgc", result.Value!.Value.GetString());
        Assert.StartsWith("dmg ", ((IReconfigurableMachine)runtime).Options, StringComparison.Ordinal);
        Assert.Equal("dmgc", prepared.Configuration!.Value.GetProperty("model").GetString());
    }

    [Fact]
    public void HumbleDeviceModelRefusesDuringColdBootAndPreservesHardwareState() {
        var engine = new GamingBrickEngine();
        var current = CurrentConfiguration("puck.gaming-brick.config.v1");
        using var runtime = engine.Create(options: "cgb cold", contentBytes: new byte[0x8000]);
        var memory = Assert.IsAssignableFrom<IMachineMemoryPeek>(runtime);
        var reconfigurable = Assert.IsAssignableFrom<IReconfigurableMachine>(runtime);
        Assert.Equal(0, memory.PeekByte(0xFF50) & 1);
        var before = reconfigurable.Options;
        using var payload = Payload("puck.gaming-brick.device-model.v1", "{\"model\":\"dmgc\"}");
        var prepared = Assert.IsType<MachineOperationPreparation.Runtime>(((IMachineOperationProvider)engine).PrepareOperation(current, new("device.model", payload.RootElement)));

        var result = prepared.Operation.Apply(runtime);

        Assert.Equal(MachineOperationStatus.Refused, result.Status);
        Assert.Equal(before, reconfigurable.Options);
        Assert.Equal(0, memory.PeekByte(0xFF50) & 1);
    }

    [Fact]
    public void AdvancedProviderKeepsModelUnsupportedAndPreparesResetOnTheRealHost() {
        var engine = new AdvancedGamingBrickEngine();
        var current = CurrentConfiguration("puck.advanced-gaming-brick.config.v1");
        using var runtime = engine.Create(options: "stub");
        using var payload = Payload("puck.advanced-gaming-brick.device-model.v1", "{\"model\":\"dmg\"}");
        var unsupported = ((IMachineOperationProvider)engine).PrepareOperation(current, new("device.model", payload.RootElement));
        Assert.Equal(MachineOperationStatus.Unsupported, Assert.IsType<MachineOperationPreparation.Refusal>(unsupported).Result.Status);
        using var resetPayload = Payload("puck.advanced-gaming-brick.machine-reset.v1", "{}");
        var reset = ((IMachineOperationProvider)engine).PrepareOperation(current, new("machine.reset", resetPayload.RootElement));
        Assert.IsType<MachineOperationPreparation.Replacement>(reset);
        Assert.Equal(MachineRuntimeStatus.Empty, runtime.Status);
    }

    [Fact]
    public void EveryProviderPreparesEachAdvertisedOperationAndItsReplacementValidates() {
        var humble = new GamingBrickEngine();
        var advanced = new AdvancedGamingBrickEngine();
        var tune = new TuneInstrumentEngine();
        var cases = new (IMachineEngine Engine, IMachineOperationProvider Provider, Func<IMachineRuntime> Create, string Schema)[] {
            (humble, (IMachineOperationProvider)humble, () => humble.Create(options: "fast"), "puck.gaming-brick.config.v1"),
            (advanced, (IMachineOperationProvider)advanced, () => advanced.Create(options: "stub"), "puck.advanced-gaming-brick.config.v1"),
            (tune, (IMachineOperationProvider)tune, () => tune.Create(options: null), "puck.tune-instrument.config.v1"),
        };

        foreach (var item in cases) {
            using var runtime = item.Create();
            var current = CurrentConfiguration(item.Schema);
            foreach (var descriptor in item.Engine.Descriptor.Operations) {
                var body = descriptor.Id switch {
                    "content.insert" => "{\"content\":{\"path\":\"asset.bin\"}}",
                    "device.model" => "{\"model\":\"dmgc\"}",
                    _ => "{}",
                };
                using var payload = Payload(descriptor.Payload.Id, body);
                var prepared = item.Provider.PrepareOperation(current, new(descriptor.Id, payload.RootElement));
                var configuration = prepared switch {
                    MachineOperationPreparation.Replacement replacement => replacement.Configuration,
                    MachineOperationPreparation.Runtime runtimeOperation => runtimeOperation.Configuration!.Value,
                    _ => throw new Xunit.Sdk.XunitException($"{item.Engine.Id} refused advertised operation {descriptor.Id}: {Assert.IsType<MachineOperationPreparation.Refusal>(prepared).Result.Reason}"),
                };
                var errors = new List<string>();
                Assert.True(MachineConfigurationValidation.TryValidate(item.Engine.Descriptor.Configuration, configuration, schemaTag: true, errors),
                    string.Join(" ", errors));
            }
        }
    }

    [Fact]
    public void OperationEnvelopesDetachPayloadsAndDefaultOrRefusalResultsCannotClaimApplied() {
        MachineOperationRequest request;
        MachineOperationPreparation prepared;
        using (var payload = Payload("puck.gaming-brick.content-insert.v1", "{\"content\":{\"path\":\"detached.gb\"}}")) {
            request = new("content.insert", payload.RootElement);
            prepared = ((IMachineOperationProvider)new GamingBrickEngine()).PrepareOperation(
                CurrentConfiguration("puck.gaming-brick.config.v1"), request);
        }

        Assert.Equal("detached.gb", request.Payload.GetProperty("content").GetProperty("path").GetString());
        Assert.Equal("detached.gb", Assert.IsType<MachineOperationPreparation.Replacement>(prepared).Configuration.GetProperty("content").GetProperty("path").GetString());
        Assert.Equal(MachineOperationStatus.Refused, default(MachineOperationResult).Status);
        Assert.Throws<ArgumentException>(() => new MachineOperationPreparation.Refusal(new(MachineOperationStatus.Applied)));
    }

    private static JsonDocument Payload(string schema, string body) {
        var value = JsonNode.Parse(body)!.AsObject();
        value["schema"] = schema;
        return JsonDocument.Parse(value.ToJsonString());
    }

    private static MachineCreationRequest CurrentConfiguration(string schema) {
        using var document = JsonDocument.Parse($"{{\"schema\":\"{schema}\"}}");
        return new(document.RootElement.Clone(), new Dictionary<string, PreparedMachineAsset>());
    }
}