using Xunit;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions.Machines;
using Puck.AdvancedGamingBrick;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge.Tune;

namespace Puck.World.Tests;

public sealed class MachineOperationProviderLawTests {
    private static MachineCreationRequest CurrentConfiguration(string schema) {
        using var document = JsonDocument.Parse($"{{\"schema\":\"{schema}\"}}");

        return new(
            document.RootElement.Clone(),
            new Dictionary<string, PreparedMachineAsset>()
        );
    }
    private static JsonDocument Payload(string schema, string body) {
        var value = JsonNode.Parse(body)!.AsObject();

        value["schema"] = schema;
        return JsonDocument.Parse(value.ToJsonString());
    }

    [Fact]
    public void AdvancedProviderKeepsModelUnsupportedAndPreparesResetOnTheRealHost() {
        var engine = new AdvancedGamingBrickEngine();
        var current = CurrentConfiguration(schema: "puck.advanced-gaming-brick.config.v1");
        using var runtime = engine.Create(options: "stub");
        using var payload = Payload(
            body: "{\"model\":\"dmg\"}",
            schema: "puck.advanced-gaming-brick.device-model.v1"
        );
        var unsupported = ((IMachineOperationProvider)engine).PrepareOperation(
            current: current,
            request: new(
                id: "device.model",
                payload: payload.RootElement
            )
        );

        Assert.Equal(
            MachineOperationStatus.Unsupported,
            Assert.IsType<MachineOperationPreparation.Refusal>(@object: unsupported).Result.Status
        );
        using var resetPayload = Payload(
            body: "{}",
            schema: "puck.advanced-gaming-brick.machine-reset.v1"
        );
        var reset = ((IMachineOperationProvider)engine).PrepareOperation(
            current: current,
            request: new(
                id: "machine.reset",
                payload: resetPayload.RootElement
            )
        );

        Assert.IsType<MachineOperationPreparation.Replacement>(@object: reset);
        Assert.Equal(
            MachineRuntimeStatus.Empty,
            runtime.Status
        );
    }
    [Fact]
    public void EveryProviderPreparesEachAdvertisedOperationAndItsReplacementValidates() {
        var humble = new GamingBrickEngine();
        var advanced = new AdvancedGamingBrickEngine();
        var tune = new TuneInstrumentEngine();
        var cases = new (IMachineEngine Engine, IMachineOperationProvider Provider, Func<IMachineRuntime> Create, string Schema)[] {
            (humble, ((IMachineOperationProvider)humble), () => humble.Create(options: "fast"), "puck.gaming-brick.configuration.v1"),
            (advanced, ((IMachineOperationProvider)advanced), () => advanced.Create(options: "stub"), "puck.advanced-gaming-brick.config.v1"),
            (tune, ((IMachineOperationProvider)tune), () => tune.Create(options: null), "puck.tune-instrument.configuration.v1"),
        };

        foreach (var item in cases) {
            using var runtime = item.Create();
            var current = CurrentConfiguration(schema: item.Schema);

            foreach (var descriptor in item.Engine.Descriptor.Operations) {
                var body = descriptor.Id switch {
                    "content.insert" => "{\"content\":{\"path\":\"asset.bin\"}}",
                    "device.model" => "{\"model\":\"dmgc\"}",
                    _ => "{}",
                };
                using var payload = Payload(
                    descriptor.Payload.Id,
                    body
                );
                var prepared = item.Provider.PrepareOperation(
                    current: current,
                    request: new(
                        id: descriptor.Id,
                        payload: payload.RootElement
                    )
                );
                var configuration = prepared switch {
                    MachineOperationPreparation.Replacement replacement => replacement.Configuration,
                    MachineOperationPreparation.Runtime runtimeOperation => runtimeOperation.Configuration!.Value,
                    _ => throw new Xunit.Sdk.XunitException(userMessage: $"{item.Engine.Id} refused advertised operation {descriptor.Id}: {Assert.IsType<MachineOperationPreparation.Refusal>(@object: prepared).Result.Reason}"),
                };
                var errors = new List<string>();

                Assert.True(
                    condition: MachineConfigurationValidation.TryValidate(
                        item.Engine.Descriptor.Configuration,
                        configuration,
                        schemaTag: true,
                        errors
                    ),
                    userMessage: string.Join(
                        separator: " ",
                        values: errors
                    )
                );
            }
        }
    }
    [Fact]
    public void HumbleDeviceModelOperationUsesTheRealRuntimeAndReturnsTheAuthoredToken() {
        var engine = new GamingBrickEngine();
        var current = CurrentConfiguration(schema: "puck.gaming-brick.configuration.v1");
        using var runtime = engine.Create(
            options: "cgb fast",
            contentBytes: new byte[0x8000]
        );
        using var payload = Payload(
            body: "{\"model\":\"dmgc\"}",
            schema: "puck.gaming-brick.device-model-set.v1"
        );
        var prepared = Assert.IsType<MachineOperationPreparation.Runtime>(@object: ((IMachineOperationProvider)engine).PrepareOperation(
            current: current,
            request: new(
                id: "device.model",
                payload: payload.RootElement
            )
        ));

        var result = prepared.Operation.Apply(runtime: runtime);

        Assert.Equal(
            MachineOperationStatus.Applied,
            result.Status
        );
        Assert.Equal(
            "dmgc",
            result.Value!.Value.GetString()
        );
        Assert.StartsWith(
            "dmg ",
            ((IReconfigurableMachine)runtime).Options,
            StringComparison.Ordinal
        );
        Assert.Equal(
            "dmgc",
            prepared.Configuration!.Value.GetProperty(propertyName: "model").GetString()
        );
    }
    [Fact]
    public void HumbleDeviceModelRefusesDuringColdBootAndPreservesHardwareState() {
        var engine = new GamingBrickEngine();
        var current = CurrentConfiguration(schema: "puck.gaming-brick.configuration.v1");
        using var runtime = engine.Create(
            options: "cgb cold",
            contentBytes: new byte[0x8000]
        );
        var memory = Assert.IsAssignableFrom<IMachineMemoryPeek>(@object: runtime);
        var reconfigurable = Assert.IsAssignableFrom<IReconfigurableMachine>(@object: runtime);

        Assert.Equal(
            0,
            memory.PeekByte(address: 0xFF50) & 1
        );
        var before = reconfigurable.Options;
        using var payload = Payload(
            body: "{\"model\":\"dmgc\"}",
            schema: "puck.gaming-brick.device-model-set.v1"
        );
        var prepared = Assert.IsType<MachineOperationPreparation.Runtime>(@object: ((IMachineOperationProvider)engine).PrepareOperation(
            current: current,
            request: new(
                id: "device.model",
                payload: payload.RootElement
            )
        ));

        var result = prepared.Operation.Apply(runtime: runtime);

        Assert.Equal(
            MachineOperationStatus.Refused,
            result.Status
        );
        Assert.Equal(
            before,
            reconfigurable.Options
        );
        Assert.Equal(
            0,
            memory.PeekByte(address: 0xFF50) & 1
        );
    }
    [Fact]
    public void InsertPreparesAHostReplacementWithoutTouchingTheLiveMachine() {
        var engine = new GamingBrickEngine();
        var current = CurrentConfiguration(schema: "puck.gaming-brick.configuration.v1");
        using var runtime = engine.Create(options: "cgb fast");
        using var payload = Payload(
            body: "{\"content\":{\"path\":\"new.gb\"}}",
            schema: "puck.gaming-brick.content-insert.v1"
        );
        var prepared = ((IMachineOperationProvider)engine).PrepareOperation(
            current: current,
            request: new(
                id: "content.insert",
                payload: payload.RootElement
            )
        );
        var replacement = Assert.IsType<MachineOperationPreparation.Replacement>(@object: prepared);

        Assert.Equal(
            MachineRuntimeStatus.Empty,
            runtime.Status
        );
        Assert.Equal(
            "new.gb",
            replacement.Configuration.GetProperty(propertyName: "content").GetProperty(propertyName: "path").GetString()
        );
        Assert.Equal(
            "puck.gaming-brick.configuration.v1",
            replacement.Configuration.GetProperty(propertyName: "schema").GetString()
        );
    }
    [Fact]
    public void OperationEnvelopesDetachPayloadsAndDefaultOrRefusalResultsCannotClaimApplied() {
        MachineOperationRequest request;
        MachineOperationPreparation prepared;

        using (var payload = Payload(
            body: "{\"content\":{\"path\":\"detached.gb\"}}",
            schema: "puck.gaming-brick.content-insert.v1"
        )) {
            request = new(
                id: "content.insert",
                payload: payload.RootElement
            );
            prepared = ((IMachineOperationProvider)new GamingBrickEngine()).PrepareOperation(
                current: CurrentConfiguration(schema: "puck.gaming-brick.configuration.v1"),
                request: request
            );
        }

        Assert.Equal(
            "detached.gb",
            request.Payload.GetProperty(propertyName: "content").GetProperty(propertyName: "path").GetString()
        );
        Assert.Equal(
            "detached.gb",
            Assert.IsType<MachineOperationPreparation.Replacement>(@object: prepared).Configuration.GetProperty(propertyName: "content").GetProperty(propertyName: "path").GetString()
        );
        Assert.Equal(
            MachineOperationStatus.Refused,
            default(MachineOperationResult).Status
        );
        Assert.Throws<ArgumentException>(testCode: () => new MachineOperationPreparation.Refusal(result: new(MachineOperationStatus.Applied)));
    }
    [Fact]
    public void UnknownWrongTypedAndMissingPayloadsRefuseBeforeAnyReplacementIsPrepared() {
        var engine = new GamingBrickEngine();
        var current = CurrentConfiguration(schema: "puck.gaming-brick.configuration.v1");
        using var unknownPayload = Payload(
            body: "{}",
            schema: "puck.gaming-brick.unknown.v1"
        );
        using var wrongTypePayload = Payload(
            body: "{\"content\":{\"path\":3}}",
            schema: "puck.gaming-brick.content-insert.v1"
        );
        using var missingPayload = Payload(
            body: "{\"content\":{}}",
            schema: "puck.gaming-brick.content-insert.v1"
        );
        var provider = ((IMachineOperationProvider)engine);

        var unknown = provider.PrepareOperation(
            current: current,
            request: new(
                id: "unknown",
                payload: unknownPayload.RootElement
            )
        );
        var wrongType = provider.PrepareOperation(
            current: current,
            request: new(
                id: "content.insert",
                payload: wrongTypePayload.RootElement
            )
        );
        var missing = provider.PrepareOperation(
            current: current,
            request: new(
                id: "content.insert",
                payload: missingPayload.RootElement
            )
        );

        Assert.Equal(
            MachineOperationStatus.Unsupported,
            Assert.IsType<MachineOperationPreparation.Refusal>(@object: unknown).Result.Status
        );
        Assert.Equal(
            MachineOperationStatus.Refused,
            Assert.IsType<MachineOperationPreparation.Refusal>(@object: wrongType).Result.Status
        );
        Assert.Equal(
            MachineOperationStatus.Refused,
            Assert.IsType<MachineOperationPreparation.Refusal>(@object: missing).Result.Status
        );
    }
}
