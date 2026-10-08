using Puck.Commands;
using System.Text.Json;
using Puck.Abstractions.Machines;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Testing;

/// <summary>A screen machine whose engine records the operations a server submits to it.</summary>
internal static class MachineOperationFixtures {
    internal static WorldDefinition Document() => Fixtures.BuildDocument() with {
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
    internal static WorldMachineOperation Operation(string instance, ulong generation, string model) {
        using var document = JsonDocument.Parse($"{{\"schema\":\"puck.operation-test.model.v1\",\"model\":\"{model}\"}}");

        return new WorldMachineOperation(
            instance,
            generation,
            "device.model",
            document.RootElement
        );
    }
    internal static WorldSubmissionResult Submit(WorldServer server, Principal principal, WorldMachineOperation operation) {
        WorldSubmissionResult? completion = null;

        server.Submit(
            new SubmissionEnvelope(
                SubmissionEnvelope.LocalConnectionId,
                0,
                1,
                1,
                principal,
                new WorldSubmissionPayload.Operation(Value: operation),
                Guid.Empty
            ),
            result => completion = result
        );
        Assert.NotNull(@object: completion);
        return completion!;
    }

    internal sealed class OperationEngine : IMachineEngine, IMachineOperationProvider {
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
    internal sealed class OperationRuntime(string model) : IMachineRuntime {
        public string Model { get; set; } = model;
        public MachineRuntimeStatus Status => MachineRuntimeStatus.Running;

        public bool Advance(ulong deltaTicks) => true;
        public void Dispose() { }
    }

    internal static JsonElement Config(string model) {
        using var document = JsonDocument.Parse($"{{\"schema\":\"puck.operation-test.config.v1\",\"model\":\"{model}\"}}");

        return document.RootElement.Clone();
    }
}
