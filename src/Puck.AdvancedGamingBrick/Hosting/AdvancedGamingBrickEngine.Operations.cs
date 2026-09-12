using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions.Machines;

namespace Puck.AdvancedGamingBrick;

public sealed partial class AdvancedGamingBrickEngine {
    /// <inheritdoc/>
    public MachineOperationPreparation PrepareOperation(MachineCreationRequest current, MachineOperationRequest request) {
        ArgumentNullException.ThrowIfNull(current);

        if (!MachineOperationValidation.TryValidate(Descriptor, request, out _, out var failure)) {
            return new MachineOperationPreparation.Refusal(failure);
        }

        return request.Id switch {
            "content.insert" => new MachineOperationPreparation.Replacement(ReplaceContent(
                current.Configuration,
                request.Payload.GetProperty("content").GetProperty("path").GetString()!
            )),
            "content.eject" => new MachineOperationPreparation.Replacement(RemoveContent(current.Configuration)),
            "machine.reset" => new MachineOperationPreparation.Replacement(current.Configuration.Clone()),
            _ => Unsupported(request.Id),
        };
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
        new MachineOperationPreparation.Refusal(new(MachineOperationStatus.Unsupported, reason: $"advanced-gaming-brick does not support operation '{id}'"));

}
