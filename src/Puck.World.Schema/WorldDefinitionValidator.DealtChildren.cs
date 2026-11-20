using Puck.World.Authoring;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    /// <summary>Validates the rows a deal sweep installs for one template: the checks that depend on what a child is
    /// (its id, slot range, prototype, transform, scale envelope, spatial volumes and surface), not on the rest of the
    /// document, which the template's own validation already covers. A deal sweep installs a projection of state
    /// without composing the whole document, so this is the gate that keeps a child no document validation would
    /// admit out of the installed document.</summary>
    /// <param name="definition">The installed document the children join.</param>
    /// <param name="template">The placement carrying the deal facet.</param>
    /// <param name="children">The children the sweep would add or replace for <paramref name="template"/>; slot
    /// uniqueness is the sweep's own allocation.</param>
    /// <param name="reason">The first refusal, naming the child, or an empty string when every child is valid.</param>
    /// <returns><see langword="true"/> when every child is one the document validator would admit.</returns>
    public static bool TryValidateDealtChildren(WorldDefinition definition, WorldPlacement template, IReadOnlyList<WorldPlacement> children, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: template);
        ArgumentNullException.ThrowIfNull(argument: children);

        var slots = WorldPlacementDeal.InstanceCount(
            template: template,
            worldSeed: (definition.Generation?.WorldSeed ?? 0UL)
        );
        var errors = new List<string>();

        foreach (var child in children) {
            var path = $"placement '{child.Id}'";

            if (!WorldPlacementDeal.IsChild(parent: template, placement: child)) {
                errors.Add(item: $"{path} is not a child of deal template '{template.Id}'.");
                break;
            }
            if (!WorldPlacement.TryValidateId(id: child.Id, reason: out var idReason)) {
                errors.Add(item: $"{path}: {idReason}.");
            }
            if ((child.DealSlot is not { } slot) || (slot < 0) || (slot >= slots)) {
                errors.Add(item: $"{path}.dealSlot must name a slot in parent '{template.Id}' (0..{(slots - 1)}).");
            }
            RefuseUndisclosingMembers(definition: definition, errors: errors, path: path, placement: child, template: template);
            if (WorldDefinitionRows.FindCreation(creations: definition.Creations, id: child.PrototypeId) is null) {
                errors.Add(item: $"{path}.prototypeId '{child.PrototypeId}' names no declared creation.");
            }
            if (!TryValidatePlacementGeometry(placement: child, reason: out var geometryReason)) {
                errors.Add(item: $"{path}: {geometryReason}");
            }
            ValidatePlacementScale(
                authoring: definition.Authoring,
                errors: errors,
                path: path,
                placement: child
            );
            if (!WorldPlacementStamp.TryValidateSurface(definition: definition, placement: child, reason: out var surfaceReason)) {
                errors.Add(item: $"{path}: {surfaceReason}");
            }
            if (child.Solid is not null) {
                foreach (var variantId in SolidVariantIds(placement: child)) {
                    if (WorldDefinitionRows.FindCreation(creations: definition.Creations, id: variantId) is { } variantCreation) {
                        _ = RefuseSolidVariantShapes(
                            creation: variantCreation,
                            errors: errors,
                            path: path,
                            variantId: variantId
                        );
                    }
                }
            }
            if (errors.Count > 0) {
                break;
            }
        }
        reason = ((errors.Count == 0)
            ? string.Empty
            : errors[0]);
        return (errors.Count == 0);
    }
    // The creations a solid row can show: its own prototype, then each response entry's target. Contact geometry is
    // owed for every one of them, since a response swap shows any of them at runtime.
    private static List<string> SolidVariantIds(WorldPlacement placement) {
        var ids = new List<string> { placement.PrototypeId };

        if (placement.Respond is { Count: > 0 } responses) {
            foreach (var response in responses) {
                if (response?.PrototypeId is { } responsePrototypeId) {
                    ids.Add(item: responsePrototypeId);
                }
            }
        }
        return ids;
    }
    // The shape-level laws a solid row owes one creation it can show: the clauses
    // CreationShapeLaws.SolidRefusals names, and a fold the contact compilers cannot expand. A detail shape reaches no
    // contact compiler, so it owes none and contributes no collider. Returns the collider count the creation costs
    // (one per expanded copy), for the placement ceiling.
    private static long RefuseSolidVariantShapes(WorldPrototype creation, string path, string variantId, List<string>? errors) {
        var colliders = 0L;

        foreach (var solidShape in (creation.Document.Shapes ?? [])) {
            if (solidShape.Detail == true) {
                continue;
            }

            foreach (var shapeRefusal in (errors is null ? [] : CreationShapeLaws.SolidRefusals(shape: solidShape))) {
                errors?.Add(item: $"{path}.solid names creation '{variantId}', whose shape {solidShape.Id} {shapeRefusal}");
            }

            if (!ShapeDomainOps.TryExpand(
                domain: solidShape.Domain,
                subject: solidShape.Position,
                frames: out var solidFrames,
                refusal: out var solidRefusal
            )) {
                errors?.Add(item: $"{path}.solid names creation '{variantId}', whose shape {solidShape.Id} carries {solidRefusal} — a solid row needs contact geometry for every copy its fold draws.");

                continue;
            }

            colliders += solidFrames.Length;
        }
        return colliders;
    }
}
