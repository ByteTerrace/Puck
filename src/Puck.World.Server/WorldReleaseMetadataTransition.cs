using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions.Machines;

namespace Puck.World.Server;

/// <summary>Prepares an authored metadata delta against a complete checkpoint without replaying gameplay or
/// taking the ordinary reload path. This preservation rule does not itself authorize a deployment.</summary>
public static class WorldReleaseMetadataTransition {
    private static byte[] ApplyDefinition(byte[] bytes, Member source, Member target, string scope, string? documentDirectory, IMachineValidationCatalog? machines) {
        var definition = WorldDefinitionSerialization.Deserialize(documentDirectory: documentDirectory, utf8Json: bytes);
        var document = JsonNode.Parse(WorldDefinitionSerialization.Serialize(definition: definition))!.AsObject();
        var metadata = Merge(
            source,
            target,
            Metadata(document: document),
            (scope + "/metadata")
        );

        if (metadata.Exists) { document["metadata"] = metadata.Value; } else { document.Remove(propertyName: "metadata"); }
        var result = WorldDefinitionSerialization.Deserialize(documentDirectory: documentDirectory, utf8Json: System.Text.Encoding.UTF8.GetBytes(s: document.ToJsonString()));

        if (!Validate(
            definition: result,
            machines: machines,
            reason: out var reason
        )) { throw new InvalidDataException(message: $"{scope} release definition is invalid: {reason}"); }
        return WorldServerCheckpoint.DocumentBytes(definition: result);
    }
    private static InvalidDataException Conflict(string path) => new(message: $"release metadata conflicts with current {path}");
    private static Member Merge(Member before, Member after, Member current, string path) {
        if (Same(
            left: before,
            right: after
        )) {
            return new(
            Exists: current.Exists,
            Value: current.Value?.DeepClone()
        );
        }
        if (
            (before.Value is null or JsonObject) &&
            (after.Value is null or JsonObject) &&
            ((before.Value is JsonObject) || (after.Value is JsonObject))
        ) {
            if (current.Value is not null and not JsonObject) { throw Conflict(path: path); }
            var result = (current.Value?.DeepClone().AsObject() ?? new JsonObject());
            var keys = ((before.Value as JsonObject)?.Select(selector: pair => pair.Key) ?? []);

            keys = keys.Concat(second: ((after.Value as JsonObject)?.Select(selector: pair => pair.Key) ?? [])).Distinct(comparer: StringComparer.Ordinal).Order(comparer: StringComparer.Ordinal);
            foreach (var key in keys) {
                var value = Merge(
                    Read(
                        (before.Value as JsonObject),
                        key
                    ),
                    Read(
                        (after.Value as JsonObject),
                        key
                    ),
                    Read(
                        key: key,
                        node: result
                    ),
                    Puck.Assets.Documents.DocumentJsonPointer.Child(name: key, pointer: path)
                );

                if (!value.Exists) { result.Remove(propertyName: key); } else { result[key] = value.Value; }
            }
            return (((after.Value is null) && (result.Count == 0))
                ? new(
                    Exists: after.Exists,
                    Value: null
                )
                : new(
                    Exists: true,
                    Value: result
                )
            );
        }
        if (!Same(
            left: before,
            right: current
        )) { throw Conflict(path: path); }
        return new(
            Exists: after.Exists,
            Value: after.Value?.DeepClone()
        );
    }
    private static Member Metadata(WorldDefinition definition) => Metadata(document: JsonNode.Parse(WorldDefinitionSerialization.Serialize(definition: definition))!.AsObject());
    private static Member Metadata(JsonObject document) {
        var metadata = Read(
            key: "metadata",
            node: document
        );
        // Nullable typed fields use null for their unset default. Normalize only that fixed layer;
        // null values inside the free-form custom bag remain authored values with presence of their own.
        if (metadata.Value is JsonObject fields) {
            foreach (var key in fields.Where(predicate: pair => (pair.Value is null)).Select(selector: pair => pair.Key).ToArray()) { fields.Remove(propertyName: key); }
        }
        return metadata;
    }
    private static Member Read(JsonObject? node, string key) => (((node is not null) && node.TryGetPropertyValue(
        jsonNode: out var value,
        propertyName: key
    ))
        ? new(
            Exists: true,
            Value: value
        )
        : default
    );
    private static bool Same(Member left, Member right) => ((left.Exists == right.Exists) && JsonNode.DeepEquals(
        node1: left.Value,
        node2: right.Value
    ));
    private static bool Validate(WorldDefinition definition, IMachineValidationCatalog? machines, out string reason) => ((machines is null)
        ? WorldDefinitionValidator.TryValidateLocally(
            definition: definition,
            reason: out reason
        )
        : WorldDefinitionValidator.TryValidateLocally(
            definition: definition,
            machines: machines,
            reason: out reason
        )
    );

    /// <summary>Returns whether two published definitions differ anywhere but their metadata: a change this transition
    /// does not preserve, so a deployment that carries one resets hosted state
    /// (<see cref="WorldReleaseCoordinator.FindDefinitionResetAsync"/>).</summary>
    /// <param name="before">The source release's published definition.</param>
    /// <param name="after">The target release's published definition.</param>
    /// <returns><see langword="true"/> when the canonical documents differ once metadata is set aside.</returns>
    public static bool ChangesOutsideMetadata(WorldDefinition before, WorldDefinition after) {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return !WorldDefinitionSerialization.Serialize(definition: before with { Metadata = null }).AsSpan()
            .SequenceEqual(other: WorldDefinitionSerialization.Serialize(definition: after with { Metadata = null }));
    }
    /// <summary>Applies only the published metadata changes to both the live definition and its undo base.
    /// Arrays are indivisible values; object members merge independently. A changed live value refuses the
    /// transition, including when it already equals the requested target. Custom object key order may change
    /// after deletion and reintroduction; metadata values and all other checkpoint state are retained. Both packages and
    /// the checkpoint's documents resolve their relative paths beside the one directory the packages carry
    /// (<see cref="WorldDefinition.DocumentDirectory"/>, as <see cref="WorldDefinitionLoader.TryParseBeside"/> gives it),
    /// the hosting silo's layout of its documents; packages that carry different directories are refused.</summary>
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
        var documentDirectory = before.DocumentDirectory;

        if (!string.Equals(a: documentDirectory, b: after.DocumentDirectory, comparisonType: StringComparison.Ordinal)) {
            reason = "release definitions resolve their relative paths beside different directories";
            return false;
        }
        // Both package definitions are published undrawn; each is proved through a drawn copy, as a release read proves
        // it, since a document whose references read a drawn cell cannot be judged undrawn.
        if (
            !WorldDefinitionLoader.TryProvePublishable(
                catalog: machines,
                definition: before,
                reason: out reason,
                sourceName: "source release definition"
            ) ||
            !WorldDefinitionLoader.TryProvePublishable(
                catalog: machines,
                definition: after,
                reason: out reason,
                sourceName: "target release definition"
            )
        ) { return false; }
        if (ChangesOutsideMetadata(after: after, before: before)) {
            reason = "release definition changes outside metadata have no admitted preservation rule";
            return false;
        }
        if (current.Server.Pending.Count != 0) {
            reason = "release definition transition requires a checkpoint without pending document work";
            return false;
        }
        try {
            var source = Metadata(definition: before);
            var target = Metadata(definition: after);
            var live = ApplyDefinition(
                current.Server.DefinitionJson,
                source,
                target,
                "live",
                documentDirectory,
                machines
            );
            var undoBase = ApplyDefinition(
                current.Server.BaseDefinitionJson,
                source,
                target,
                "undo base",
                documentDirectory,
                machines
            );

            transitioned = current with { Server = current.Server with { DefinitionJson = live, BaseDefinitionJson = undoBase } };
            reason = string.Empty;
            return true;
        } catch (Exception error) when ((error is InvalidDataException or JsonException)) {
            reason = error.Message;
            return false;
        }
    }

    // A literal null custom value is different from an absent member. Neither may erase the other on reversal.
    private readonly record struct Member(bool Exists, JsonNode? Value);
}
