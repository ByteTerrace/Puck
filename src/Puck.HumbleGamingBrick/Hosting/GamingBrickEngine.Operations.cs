using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions.Machines;

namespace Puck.HumbleGamingBrick;

public sealed partial class GamingBrickEngine {
    /// <inheritdoc/>
    public MachineOperationPreparation PrepareOperation(MachineCreationRequest current, MachineOperationRequest request) {
        ArgumentNullException.ThrowIfNull(current);

        if (!MachineOperationValidation.TryValidate(
            Descriptor,
            request,
            out _,
            out var failure
        )) {
            return new MachineOperationPreparation.Refusal(result: failure);
        }

        try {
            return request.Id switch {
                "content.insert" => new MachineOperationPreparation.Replacement(configuration: ReplaceContent(
                configuration: current.Configuration,
                path: request.Payload.GetProperty(propertyName: "content").GetProperty(propertyName: "path").GetString()!
            )),
                "content.eject" => new MachineOperationPreparation.Replacement(configuration: RemoveContent(configuration: current.Configuration)),
                "machine.reset" => new MachineOperationPreparation.Replacement(configuration: current.Configuration.Clone()),
                "device.model" => PrepareModel(
                configuration: current.Configuration,
                modelToken: request.Payload.GetProperty(propertyName: "model").GetString()!
            ),
                _ => Unsupported(id: request.Id),
            };
        } catch (Exception exception) when ((exception is ArgumentException or InvalidOperationException or KeyNotFoundException)) {
            return Refused(reason: exception.Message);
        }
    }

    private MachineOperationPreparation PrepareModel(JsonElement configuration, string modelToken) {
        if (!ModelTokens.TryGetValue(
            key: modelToken,
            value: out var model
        )) {
            return Refused(reason: $"unknown gaming-brick model '{modelToken}'");
        }

        var updated = JsonNode.Parse(configuration.GetRawText())!.AsObject();

        updated["model"] = modelToken;

        return new MachineOperationPreparation.Runtime(
            new ModelOperation(
                model: model,
                modelToken: modelToken
            ),
            CloneJson(value: updated)
        );
    }
    private static JsonElement ReplaceContent(JsonElement configuration, string path) {
        var updated = JsonNode.Parse(configuration.GetRawText())!.AsObject();

        updated["content"] = new JsonObject { ["path"] = path };

        return CloneJson(value: updated);
    }
    private static JsonElement RemoveContent(JsonElement configuration) {
        var updated = JsonNode.Parse(configuration.GetRawText())!.AsObject();

        updated.Remove(propertyName: "content");

        return CloneJson(value: updated);
    }
    private static JsonElement CloneJson(JsonNode value) {
        using var document = JsonDocument.Parse(value.ToJsonString());

        return document.RootElement.Clone();
    }
    private static MachineOperationPreparation Unsupported(string id) =>
        new MachineOperationPreparation.Refusal(result: new(
            MachineOperationStatus.Unsupported,
            reason: $"gaming-brick does not support operation '{id}'"
        ));
    private static MachineOperationPreparation Refused(string reason) =>
        new MachineOperationPreparation.Refusal(result: new(
            MachineOperationStatus.Refused,
            reason: reason
        ));

    private sealed class ModelOperation(ConsoleModel model, string modelToken) : IMachinePreparedOperation {
        public MachineOperationResult Apply(IMachineRuntime runtime) {
            if (runtime is not IReconfigurableMachine reconfigurable) {
                return new(
                    MachineOperationStatus.Unsupported,
                    reason: "runtime does not support live device reconfiguration"
                );
            }

            try {
                var current = ParseOptions(reconfigurable.Options);
                var options = FormatOptions(
                    boot: current.Boot,
                    dmgSpeed: current.DmgSpeed,
                    model: model
                );

                if (!reconfigurable.TryReconfigure(
                    options: options,
                    reason: out var reason
                )) {
                    return new(
                        MachineOperationStatus.Refused,
                        reason: reason
                    );
                }

                using var resultDocument = JsonDocument.Parse($"\"{modelToken}\"");

                return new(
                    MachineOperationStatus.Applied,
                    value: resultDocument.RootElement.Clone(),
                    reason: reason
                );
            } catch (ArgumentException exception) {
                return new(
                    MachineOperationStatus.Refused,
                    reason: exception.Message
                );
            }
        }
    }
}
