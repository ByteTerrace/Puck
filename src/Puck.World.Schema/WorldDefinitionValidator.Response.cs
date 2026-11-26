namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // The pointer facet (see WorldPointerSurface): a rectangle in the row's frame with the same frame rules a screen's
    // face keeps, so the mapping a rule compiles from it validates. It rides the frame the row resolves to, which an
    // attached row does not have, so the facet is refused there.
    private static void ValidatePointerSurface(WorldPlacement placement, WorldPointerSurface pointer, string path, List<string> errors) {
        if (placement.Attach is not null) {
            errors.Add(item: $"{path} is refused on an attached row: a pointer surface rides the row's resolved static frame.");
        }

        if (
            (pointer.Origin is null) || (pointer.Right is null) || (pointer.Up is null) ||
            !Puck.Maths.VectorFunctions.IsFinite(vector: pointer.Origin) ||
            !Puck.Maths.VectorFunctions.IsFinite(vector: pointer.Right) ||
            !Puck.Maths.VectorFunctions.IsFinite(vector: pointer.Up)
        ) {
            errors.Add(item: $"{path} frame vectors must contain finite coordinates.");
        } else if (
            (MathF.Abs(x: (pointer.Right.Value.LengthSquared() - 1f)) > 1e-3f) ||
            (MathF.Abs(x: (pointer.Up.Value.LengthSquared() - 1f)) > 1e-3f) ||
            (MathF.Abs(x: System.Numerics.Vector3.Dot(vector1: pointer.Right, vector2: pointer.Up)) > MaximumScreenBasisSkew)
        ) {
            errors.Add(item: $"{path} right/up must be orthogonal unit axes.");
        }
        if (!float.IsFinite(f: pointer.HalfWidth) || !float.IsFinite(f: pointer.HalfHeight) || (pointer.HalfWidth <= 0f) || (pointer.HalfHeight <= 0f)) {
            errors.Add(item: $"{path} half extents must be finite and positive.");
        }

        WorldPointerRegion.Validate(errors: errors, path: $"{path}.regions", regions: pointer.Regions);
    }
    // The response facet (see WorldPlacementResponse): an ordered set of condition-gated prototype swaps. A Field
    // condition reuses the fields.reactions condition grammar (WorldFieldCondition-shaped: field/comparison/value)
    // rather than a parallel one, so this pass checks it exactly like ValidateFields checks a Transform/Expose
    // condition. A State condition reuses ActionPredicate.CompareState's own field convention (state/key, and
    // value XOR comparandState/comparandKey) rather than a parallel one, on the same (row, key) pair rule.
    private static void ValidatePlacementResponse(WorldPlacement placement, WorldDefinition definition, HashSet<string> prototypeIds, string placementPath, List<string> errors) {
        var responses = placement.Respond!;
        var path = $"{placementPath}.respond";

        if (
            (placement.Inhabit is not null) ||
            (placement.FaceSources is not null)
        ) {
            errors.Add(item: $"{path} is refused alongside inhabit/faceSources — a response swaps a placement's prototype, not a population body's look or per-instance face sources.");
        }

        if (responses.Count == 0) {
            errors.Add(item: $"{path} declares no response entry.");
        } else if (responses.Count > WorldResponseCapacity.MaxEntries) {
            errors.Add(item: $"{path} declares {responses.Count} entries, exceeding the {WorldResponseCapacity.MaxEntries}-entry ceiling.");
        }

        var fieldNames = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var field in definition.FieldRegions.SelectMany(selector: static region => region.Fields)) {
            fieldNames.Add(item: field.Name);
        }

        // A response may show an animated creation: every reader draws the row as the creation it shows
        // (WorldPlacement.ShownPrototypeId), so the static stamps and the stamp pool trade the row as its holding moves.
        // What only a static stamp carries stays refused on a row that can show an animated one.
        RefuseStaticOnlyFacets(
            creations: definition.Creations,
            errors: errors,
            path: placementPath,
            placement: placement,
            prototypeId: placement.PrototypeId
        );

        if (!placement.Holding.Fits(count: responses.Count)) {
            errors.Add(item: $"{placementPath}.holding {placement.Holding} names an entry past the {responses.Count} response entries.");
        }

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

            RefuseStaticOnlyFacets(
                creations: definition.Creations,
                errors: errors,
                path: entryPath,
                placement: placement,
                prototypeId: response.PrototypeId
            );

            switch (response.When) {
                case null:
                    errors.Add(item: $"{entryPath}.when is required.");

                    break;
                case WorldPlacementResponseCondition.FieldCondition field:
                    ValidateFieldCondition(
                        condition: field,
                        definition: definition,
                        entryPath: entryPath,
                        errors: errors,
                        fieldNames: fieldNames
                    );

                    break;
                case WorldPlacementResponseCondition.StateCondition state:
                    ValidateStateCondition(
                        condition: state,
                        definition: definition,
                        entryPath: entryPath,
                        errors: errors
                    );

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

        if (!condition.Comparison.IsComparison()) {
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
                errors.Add(item: $"{entryPath}.when.value references state row '{row}', which must be a scalar kind=Fixed row.");
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
        if (
            (row is null) ||
            (WorldDefinitionRows.FindStateRow(
            rows: definition.State,
            name: row
        ) is not { } declared)
        ) {
            errors.Add(item: $"{entryPath} references state row '{row}', which the document does not declare.");

            return null;
        }

        if (declared.Kind is CellKind.Text or CellKind.Vector) {
            errors.Add(item: $"{entryPath} references state row '{row}', which is kind={declared.Kind} — a response compares numbers, never {StateSpelling.Kind(kind: declared.Kind)}.");
        }

        if (
            declared.IsKeyed &&
            (key is null)
        ) {
            errors.Add(item: $"{entryPath} names keyed row '{row}' without a 'key' — a keyed row has no single cell, so name the one you mean.");
        } else if (
            !declared.IsKeyed &&
            (key is not null)
        ) {
            errors.Add(item: $"{entryPath} names row '{row}' with a 'key', but the row is not keyed — omit 'key' to read its slot cell.");
        } else if (
            (key is not null) &&
            !CellName.TryParse(
            candidate: key,
            name: out _,
            reason: out var reason
        )
        ) {
            errors.Add(item: $"{entryPath} key '{key}' {reason}");
        }

        return declared.Kind;
    }
    private static void ValidateStateCondition(WorldPlacementResponseCondition.StateCondition condition, WorldDefinition definition, string entryPath, List<string> errors) {
        if (!condition.Comparison.IsComparison()) {
            errors.Add(item: $"{entryPath}.when.comparison '{condition.Comparison}' is unknown.");
        }

        var kind = ValidateStateCell(
            row: condition.State,
            key: condition.Key,
            entryPath: $"{entryPath}.when.state",
            definition: definition,
            errors: errors
        );
        var hasValue = (condition.Value is not null);
        var hasComparand = (condition.ComparandState is not null);

        if (
            (condition.ComparandKey is not null) &&
            (condition.ComparandState is null)
        ) {
            errors.Add(item: $"{entryPath}.when names 'comparandKey' without 'comparandState' — a comparand key addresses a cell inside a comparand row, which must be named.");
        }

        if (hasValue == hasComparand) {
            errors.Add(item: (hasValue
                ? $"{entryPath}.when names both 'value' and 'comparandState' — a state condition spells exactly one comparand, never both."
                : $"{entryPath}.when names neither 'value' nor 'comparandState' — a state condition must spell exactly one comparand."));

            return;
        }

        if (hasValue) {
            if (!float.IsFinite(f: condition.Value!.Value)) {
                errors.Add(item: $"{entryPath}.when.value must carry a finite value.");
            }

            return;
        }

        var comparandKind = ValidateStateCell(
            row: condition.ComparandState,
            key: condition.ComparandKey,
            entryPath: $"{entryPath}.when.comparandState",
            definition: definition,
            errors: errors
        );

        if (
            (kind is { } primaryKind) &&
            (comparandKind is { } otherKind) &&
            (primaryKind != otherKind)
        ) {
            errors.Add(item: $"{entryPath}.when '{condition.State}' is kind={primaryKind} but comparand '{condition.ComparandState}' is kind={otherKind} — mixed-kind comparisons are refused; author both sides the same kind.");
        }
    }
    // A creation a responding row can show (its authored prototype or an entry's) that animates replays through the stamp
    // pool while it shows, so the row carries the animated-row constraints whatever it shows now: a distribution or a
    // mirror is a static stamp's alone (ValidatePlacements' animated-row check reads the authored prototype only).
    private static void RefuseStaticOnlyFacets(string prototypeId, WorldPlacement placement, IReadOnlyList<WorldPrototype> creations, string path, List<string> errors) {
        if (
            ((placement.Distribution is not null) || (placement.Mirror is not null)) &&
            CarriesMotion(creation: WorldDefinitionRows.FindCreation(creations: creations, id: prototypeId))
        ) {
            errors.Add(item: $"{path} can show '{prototypeId}', which carries timeline frames or drivers — a row that can show an animated creation carries no distribution or mirror, which are static-stamp-only.");
        }
    }
    // A prototype a dealt template stamps must resolve to a declared creation carrying no timeline frames or drivers: its
    // children are plain static stamps.
    private static void RequireStaticCreation(string prototypeId, HashSet<string> prototypeIds, IReadOnlyList<WorldPrototype> creations, string path, List<string> errors) {
        if (prototypeIds.Contains(item: prototypeId) && CarriesMotion(creation: WorldDefinitionRows.FindCreation(creations: creations, id: prototypeId))) {
            errors.Add(item: $"{path} '{prototypeId}' carries timeline frames or drivers — a dealt template's children are plain static stamps.");
        }
    }
    // Whether a creation carries timeline frames or drivers: the validator's one reading of an animated creation, which
    // replays through the stamp pool and carries none of a static stamp's facets (a distribution, a mirror, a portal, a
    // deal). The presentation's fork (WorldPrototype.Rig's Animates) also counts effectors and leaves out frames that move
    // nothing, so the two disagree on those creations.
    private static bool CarriesMotion(WorldPrototype? creation) =>
        (creation is { Document.Frames.Count: > 0 } or { Document.Drivers.Count: > 0 });
}
