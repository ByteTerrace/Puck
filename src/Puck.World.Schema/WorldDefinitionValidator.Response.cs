namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // The response facet (see WorldPlacementResponse): an ordered set of condition-gated prototype swaps. A Field
    // condition reuses the fields.reactions condition grammar (WorldFieldCondition-shaped: field/comparison/value)
    // rather than a parallel one, so this pass checks it exactly like ValidateFields checks a Transform/Expose
    // condition. A State condition reuses ActionPredicate.CompareState's own field convention (state/key, and
    // value XOR comparandState/comparandKey) rather than a parallel one, on the same (row, key) pair rule.
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

            switch (response.When) {
                case null:
                    errors.Add(item: $"{entryPath}.when is required.");

                    break;
                case WorldPlacementResponseCondition.FieldCondition field:
                    ValidateFieldCondition(condition: field, fieldNames: fieldNames, definition: definition, entryPath: entryPath, errors: errors);

                    break;
                case WorldPlacementResponseCondition.StateCondition state:
                    ValidateStateCondition(condition: state, definition: definition, entryPath: entryPath, errors: errors);

                    break;
            }
        }
    }
    private static void ValidateFieldCondition(WorldPlacementResponseCondition.FieldCondition condition, HashSet<string> fieldNames, WorldDefinition definition, string entryPath, List<string> errors) {
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
    // The (row, key) pair rule every reader of a named cell enforces: an absent key reads the row's slot, which a
    // keyed row does not have; a present key addresses one cell of a keyed row, which a slot row does not have.
    // Returns the resolved row's kind so a comparand's kind can be checked against it, or null when the row itself
    // already refused (so the caller does not pile a second, confusing error on top).
    private static CellKind? ValidateStateCell(string? row, string? key, string entryPath, WorldDefinition definition, List<string> errors) {
        if ((row is null) || (WorldDefinitionRows.FindStateRow(rows: definition.State, name: row) is not { } declared)) {
            errors.Add(item: $"{entryPath} references state row '{row}', which the document does not declare.");

            return null;
        }

        if (declared.Kind == CellKind.Text) {
            errors.Add(item: $"{entryPath} references state row '{row}', which is kind=text — a response compares numbers, never text.");
        }

        if (declared.IsKeyed && (key is null)) {
            errors.Add(item: $"{entryPath} names keyed row '{row}' without a 'key' — a keyed row has no single cell, so name the one you mean.");
        } else if (!declared.IsKeyed && (key is not null)) {
            errors.Add(item: $"{entryPath} names row '{row}' with a 'key', but the row is not keyed — omit 'key' to read its slot cell.");
        } else if ((key is not null) && !CellName.TryParse(candidate: key, name: out _, reason: out var reason)) {
            errors.Add(item: $"{entryPath} key '{key}' {reason}");
        }

        return declared.Kind;
    }
    private static void ValidateStateCondition(WorldPlacementResponseCondition.StateCondition condition, WorldDefinition definition, string entryPath, List<string> errors) {
        if (!Enum.IsDefined(value: condition.Comparison)) {
            errors.Add(item: $"{entryPath}.when.comparison '{condition.Comparison}' is unknown.");
        }

        var kind = ValidateStateCell(row: condition.State, key: condition.Key, entryPath: $"{entryPath}.when.state", definition: definition, errors: errors);
        var hasValue = (condition.Value is not null);
        var hasComparand = (condition.ComparandState is not null);

        if ((condition.ComparandKey is not null) && (condition.ComparandState is null)) {
            errors.Add(item: $"{entryPath}.when names 'comparandKey' without 'comparandState' — a comparand key addresses a cell inside a comparand row, which must be named.");
        }

        if (hasValue == hasComparand) {
            errors.Add(item: (hasValue
                ? $"{entryPath}.when names both 'value' and 'comparandState' — a state condition spells exactly one comparand, never both."
                : $"{entryPath}.when names neither 'value' nor 'comparandState' — a state condition must spell exactly one comparand."
            ));

            return;
        }

        if (hasValue) {
            if (!float.IsFinite(f: condition.Value!.Value)) {
                errors.Add(item: $"{entryPath}.when.value must carry a finite value.");
            }

            return;
        }

        var comparandKind = ValidateStateCell(row: condition.ComparandState, key: condition.ComparandKey, entryPath: $"{entryPath}.when.comparandState", definition: definition, errors: errors);

        if ((kind is { } primaryKind) && (comparandKind is { } otherKind) && (primaryKind != otherKind)) {
            errors.Add(item: $"{entryPath}.when '{condition.State}' is kind={primaryKind} but comparand '{condition.ComparandState}' is kind={otherKind} — mixed-kind comparisons are refused; author both sides the same kind.");
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
