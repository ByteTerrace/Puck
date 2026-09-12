using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions.Machines;

namespace Puck.HumbleGamingBrick;

public sealed partial class GamingBrickEngine {
    /// <inheritdoc/>
    public MachineOperationPreparation PrepareOperation(MachineCreationRequest current, MachineOperationRequest request) {
        ArgumentNullException.ThrowIfNull(current);

        if (!MachineOperationValidation.TryValidate(Descriptor, request, out _, out var failure)) {
            return new MachineOperationPreparation.Refusal(failure);
        }

        try {
            return request.Id switch {
                "content.insert" => new MachineOperationPreparation.Replacement(
                    ReplaceContent(current.Configuration, request.Payload.GetProperty("content").GetProperty("path").GetString()!)),
                "content.eject" => new MachineOperationPreparation.Replacement(RemoveContent(current.Configuration)),
                "machine.reset" => new MachineOperationPreparation.Replacement(current.Configuration.Clone()),
                "device.model" => PrepareModel(current.Configuration, request.Payload.GetProperty("model").GetString()!),
                _ => Unsupported(request.Id),
            };
        } catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException) {
            return Refused(exception.Message);
        }
    }

    private MachineOperationPreparation PrepareModel(JsonElement configuration, string modelToken) {
        if (!ModelTokens.TryGetValue(modelToken, out var model)) {
            return Refused($"unknown gaming-brick model '{modelToken}'");
        }

        var updated = JsonNode.Parse(configuration.GetRawText())!.AsObject();
        updated["model"] = modelToken;

        return new MachineOperationPreparation.Runtime(
            new ModelOperation(model, modelToken),
            CloneJson(updated)
        );
    }

    private static JsonElement ReplaceContent(JsonElement configuration, string path) {
        var updated = JsonNode.Parse(configuration.GetRawText())!.AsObject();
        updated["content"] = new JsonObject { ["path"] = path };

        return CloneJson(updated);
    }

    private static JsonElement RemoveContent(JsonElement configuration) {
        var updated = JsonNode.Parse(configuration.GetRawText())!.AsObject();
        updated.Remove("content");

        return CloneJson(updated);
    }

    private static JsonElement CloneJson(JsonNode value) {
        using var document = JsonDocument.Parse(value.ToJsonString());
        return document.RootElement.Clone();
    }

    private static MachineOperationPreparation Unsupported(string id) =>
        new MachineOperationPreparation.Refusal(new(MachineOperationStatus.Unsupported, reason: $"gaming-brick does not support operation '{id}'"));


    private static MachineOperationPreparation Refused(string reason) =>
        new MachineOperationPreparation.Refusal(new(MachineOperationStatus.Refused, reason: reason));

    private sealed class ModelOperation(ConsoleModel model, string modelToken) : IMachinePreparedOperation {
        public MachineOperationResult Apply(IMachineRuntime runtime) {
            if (runtime is not IReconfigurableMachine reconfigurable) {
                return new(MachineOperationStatus.Unsupported, reason: "runtime does not support live device reconfiguration");
            }

            try {
                var current = ParseOptions(reconfigurable.Options);
                var options = FormatOptions(model, current.DmgSpeed, current.Boot);

                if (!reconfigurable.TryReconfigure(options, out var reason)) {
                    return new(MachineOperationStatus.Refused, reason: reason);
                }

                using var resultDocument = JsonDocument.Parse($"\"{modelToken}\"");
                return new(MachineOperationStatus.Applied, value: resultDocument.RootElement.Clone(), reason: reason);
            } catch (ArgumentException exception) {
                return new(MachineOperationStatus.Refused, reason: exception.Message);
            }
        }
    }
}
