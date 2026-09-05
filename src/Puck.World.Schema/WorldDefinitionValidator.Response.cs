namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // The response facet (see WorldPlacementResponse): an ordered set of lattice-field-condition-gated prototype
    // swaps. Reuses the fields.reactions condition grammar (WorldFieldCondition/WorldFieldComparison/
    // WorldLatticeScalar) rather than a parallel one, so this pass checks a condition exactly like ValidateFields
    // checks a Transform/Expose condition.
    private static void ValidatePlacementResponse(WorldPlacement placement, WorldDefinition definition, HashSet<string> prototypeIds, string placementPath, List<string> errors) {
        var responses = placement.Respond!;
        var path = $"{placementPath}.respond";

        if (
            (placement.Attach is not null) ||
            (placement.Inhabit is not null) ||
            (placement.FaceSources is not null)
        ) {
            errors.Add(item: $"{path} is refused alongside attach/inhabit/faceSources — a response swap is a static-prototype concern only.");
        }

        if (responses.Count == 0) {
            errors.Add(item: $"{path} declares no response entry.");
        } else if (responses.Count > WorldResponseCapacity.MaxEntries) {
            errors.Add(item: $"{path} declares {responses.Count} entries, exceeding the {WorldResponseCapacity.MaxEntries}-entry ceiling.");
        }

        var fieldNames = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var field in (definition.Fields?.Fields ?? [])) {
            fieldNames.Add(item: field.Name);
        }

        RequireStaticCreation(
            creations: definition.Creations,
            errors: errors,
            path: $"{placementPath}.prototypeId",
            prototypeId: placement.PrototypeId,
            prototypeIds: prototypeIds
        );

        for (var index = 0; (index < responses.Count); index++) {
            var response = responses[index];
            var entryPath = $"{path}[{index}]";

            if (response is null) {
                errors.Add(item: $"{entryPath} is required.");

                continue;
            }

            RequireDeclared(
                value: response.PrototypeId,
                declaredSet: prototypeIds,
                path: entryPath,
                field: "prototypeId",
                rowNoun: "creation",
                errors: errors
            );

            RequireStaticCreation(
                creations: definition.Creations,
                errors: errors,
                path: $"{entryPath}.prototypeId",
                prototypeId: response.PrototypeId,
                prototypeIds: prototypeIds
            );

            if (response.When is not { } condition) {
                errors.Add(item: $"{entryPath}.when is required.");

                continue;
            }

            if (
                (condition.Field is null) ||
                !fieldNames.Contains(item: condition.Field)
            ) {
                errors.Add(item: $"{entryPath}.when.field names field '{condition.Field}', which fields.fields does not declare.");
            }

            if (!Enum.IsDefined(value: condition.Comparison)) {
                errors.Add(item: $"{entryPath}.when.comparison '{condition.Comparison}' is unknown.");
            }

            if (condition.Value.Row is { } row) {
                if (WorldDefinitionRows.FindStateRow(
                    rows: definition.State,
                    name: row
                ) is not { } declared) {
                    errors.Add(item: $"{entryPath}.when.value references state row '{row}', which the document does not declare.");
                } else if (
                    (declared.Kind != CellKind.Fixed) ||
                    declared.IsKeyed ||
                    (declared.Field is not null)
                ) {
                    errors.Add(item: $"{entryPath}.when.value references state row '{row}', which must be a scalar kind=fixed row.");
                }
            } else if (!float.IsFinite(f: (condition.Value.Literal ?? 0f))) {
                errors.Add(item: $"{entryPath}.when.value must carry a finite value.");
            }
        }
    }
    // A prototype a response facet could show at runtime (the row's own base id, or a response entry's target) must
    // resolve to a declared creation carrying no timeline frames — a response only ever swaps between STATIC
    // creations, never animates a row that validated as a static stamp.
    private static void RequireStaticCreation(string prototypeId, HashSet<string> prototypeIds, IReadOnlyList<WorldPrototype> creations, string path, List<string> errors) {
        if (!prototypeIds.Contains(item: prototypeId)) {
            return;
        }

        if (WorldDefinitionRows.FindCreation(
            creations: creations,
            id: prototypeId
        ) is { Document.Frames.Count: > 0 }) {
            errors.Add(item: $"{path} '{prototypeId}' carries timeline frames — a response facet only ever swaps between static creations.");
        }
    }
}
