using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions.Machines;

namespace Puck.AdvancedGamingBrick;

public sealed partial class AdvancedGamingBrickEngine {
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

        return request.Id switch {
            "content.insert" => new MachineOperationPreparation.Replacement(configuration: ReplaceContent(
            configuration: current.Configuration,
            path: request.Payload.GetProperty(propertyName: "content").GetProperty(propertyName: "path").GetString()!
        )),
            "content.eject" => new MachineOperationPreparation.Replacement(configuration: RemoveContent(configuration: current.Configuration)),
            "machine.reset" => new MachineOperationPreparation.Replacement(configuration: current.Configuration.Clone()),
            _ => Unsupported(id: request.Id),
        };
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
            reason: $"advanced-gaming-brick does not support operation '{id}'"
        ));

}
