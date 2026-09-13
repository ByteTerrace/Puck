using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions.Machines;

namespace Puck.World.Server;

/// <summary>Prepares an authored metadata delta against a complete checkpoint without replaying gameplay or
/// taking the ordinary reload path. This preservation rule does not itself authorize a deployment.</summary>
public static class WorldReleaseMetadataTransition {
    /// <summary>Applies only the published metadata changes to both the live definition and its undo base.
    /// Arrays are indivisible values; object members merge independently. A changed live value refuses the
    /// transition, including when it already equals the requested target. Custom object key order may change
    /// after deletion and reintroduction; metadata values and all other checkpoint state are retained.</summary>
    /// <param name="before">The source release's published definition.</param>
    /// <param name="after">The target release's published definition.</param>
    /// <param name="current">The complete current gameplay checkpoint.</param>
    /// <param name="transitioned">The transformed checkpoint, or null on any refusal. Inputs are never modified.</param>
    /// <param name="reason">A named preservation conflict or validation failure.</param>
    /// <param name="machines">The installed machine vocabulary used by whole-document validation.</param>
    /// <returns>Whether the metadata-only change preserves this checkpoint.</returns>
    public static bool TryApply(WorldDefinition before, WorldDefinition after, WorldAuthorityCheckpoint current,
        out WorldAuthorityCheckpoint? transitioned, out string reason, IMachineValidationCatalog? machines = null) {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(current);
        transitioned = null;
        if (!Validate(before, machines, out reason) || !Validate(after, machines, out reason)) { return false; }
        if (!WorldDefinitionSerialization.Serialize(before with { Metadata = null }).AsSpan()
            .SequenceEqual(WorldDefinitionSerialization.Serialize(after with { Metadata = null }))) {
            reason = "release definition changes outside metadata have no admitted preservation rule";
            return false;
        }
        if (current.Server.Pending.Count != 0) {
            reason = "release definition transition requires a checkpoint without pending document work";
            return false;
        }
        try {
            var source = Metadata(before);
            var target = Metadata(after);
            var live = ApplyDefinition(current.Server.DefinitionJson, source, target, "live", machines);
            var undoBase = ApplyDefinition(current.Server.BaseDefinitionJson, source, target, "undo base", machines);
            transitioned = current with { Server = current.Server with { DefinitionJson = live, BaseDefinitionJson = undoBase } };
            reason = string.Empty;
            return true;
        } catch (Exception error) when (error is InvalidDataException or JsonException) {
            reason = error.Message;
            return false;
        }
    }

    private static byte[] ApplyDefinition(byte[] bytes, Member source, Member target, string scope, IMachineValidationCatalog? machines) {
        var definition = WorldDefinitionSerialization.Deserialize(bytes);
        var document = JsonNode.Parse(WorldDefinitionSerialization.Serialize(definition))!.AsObject();
        var metadata = Merge(source, target, Metadata(document), scope + "/metadata");
        if (metadata.Exists) { document["metadata"] = metadata.Value; }
        else { document.Remove("metadata"); }
        var result = WorldDefinitionSerialization.Deserialize(System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()));
        if (!Validate(result, machines, out var reason)) { throw new InvalidDataException($"{scope} release definition is invalid: {reason}"); }
        return WorldDefinitionSerialization.Serialize(result);
    }

    private static Member Merge(Member before, Member after, Member current, string path) {
        if (Same(before, after)) { return new(current.Exists, current.Value?.DeepClone()); }
        if ((before.Value is null or JsonObject) && (after.Value is null or JsonObject) && (before.Value is JsonObject || after.Value is JsonObject)) {
            if (current.Value is not null and not JsonObject) { throw Conflict(path); }
            var result = current.Value?.DeepClone().AsObject() ?? new JsonObject();
            var keys = (before.Value as JsonObject)?.Select(pair => pair.Key) ?? [];
            keys = keys.Concat((after.Value as JsonObject)?.Select(pair => pair.Key) ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            foreach (var key in keys) {
                var value = Merge(Read(before.Value as JsonObject, key), Read(after.Value as JsonObject, key), Read(result, key),
                    path + "/" + key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal));
                if (!value.Exists) { result.Remove(key); }
                else { result[key] = value.Value; }
            }
            return after.Value is null && result.Count == 0 ? new(after.Exists, null) : new(true, result);
        }
        if (!Same(before, current)) { throw Conflict(path); }
        return new(after.Exists, after.Value?.DeepClone());
    }

    // A literal null custom value is different from an absent member. Neither may erase the other on reversal.
    private readonly record struct Member(bool Exists, JsonNode? Value);
    private static Member Read(JsonObject? node, string key) => node is not null && node.TryGetPropertyValue(key, out var value) ? new(true, value) : default;
    private static bool Same(Member left, Member right) => left.Exists == right.Exists && JsonNode.DeepEquals(left.Value, right.Value);
    private static InvalidDataException Conflict(string path) => new($"release metadata conflicts with current {path}");
    private static Member Metadata(WorldDefinition definition) => Metadata(JsonNode.Parse(WorldDefinitionSerialization.Serialize(definition))!.AsObject());
    private static Member Metadata(JsonObject document) {
        var metadata = Read(document, "metadata");
        // Nullable typed fields use null for their unset default. Normalize only that fixed layer;
        // null values inside the free-form custom bag remain authored values with presence of their own.
        if (metadata.Value is JsonObject fields) {
            foreach (var key in fields.Where(pair => pair.Value is null).Select(pair => pair.Key).ToArray()) { fields.Remove(key); }
        }
        return metadata;
    }
    private static bool Validate(WorldDefinition definition, IMachineValidationCatalog? machines, out string reason) => machines is null
        ? WorldDefinitionValidator.TryValidateLocally(definition, out reason)
        : WorldDefinitionValidator.TryValidateLocally(definition, machines, out reason);
}
