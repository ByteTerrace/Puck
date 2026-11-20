using Puck.Abstractions.Machines;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static WorldValidationPartition RowPartition<TRow>(
        string name, WorldDocumentInput owns, WorldDocumentInputs reads, Func<WorldDefinition, IReadOnlyList<TRow>> get,
        Func<WorldDefinition, IReadOnlyList<TRow>?, WorldDefinition> set, Action<WorldValidationRun, IReadOnlyList<int>?> validate,
        WorldBoundValidation? bound = null, Func<WorldDefinition, WorldDefinition, WorldValidationRowChange>? rowChange = null,
        Func<WorldDefinition, WorldDefinition, WorldDocumentInputs, IReadOnlyList<int>?>? selectDependentRows = null
    ) where TRow : class => new(
        name: name,
        granularity: WorldValidationGranularity.Rows,
        owns: WorldDocumentInputs.Of(owns),
        reads: reads,
        validate: validate,
        rowCount: definition => get(definition).Count,
        rowChange: rowChange ?? ((before, after) => new(ChangedRows(get(before), get(after)), false)),
        bound: bound ?? BoundRows(get, set),
        selectDependentRows: selectDependentRows
    );

    private static WorldBoundValidation BoundRows<TRow>(Func<WorldDefinition, IReadOnlyList<TRow>> get,
        Func<WorldDefinition, IReadOnlyList<TRow>?, WorldDefinition> set) where TRow : class => new(
        Enumerate: definition => get(definition),
        // Keep sibling declarations available for name lookups, and run only the literal at the head of the projection.
        Resolve: (definition, unit, resolve) => resolve(unit) is TRow literal
            ? new(set(definition, [literal, .. get(definition).Where(row => !ReferenceEquals(row, unit))]), [0])
            : null,
        Remove: definition => set(definition, null)
    );

    private static WorldBoundValidation PlacementBinding() => new(
        Enumerate: static definition => definition.Placements.Where(static row =>
            row is { Id: not null } && !WorldPlacementDeal.TrySplitChildId(row.Id, out _, out _)),
        Resolve: static (definition, unit, resolve) => {
            var chain = new List<WorldPlacement>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var byId = WorldPlacementIndex.ById(definition.Placements);

            for (var row = (WorldPlacement?)unit; row is not null && seen.Add(row.Id);) {
                if (resolve(row) is not WorldPlacement literal) { return null; }
                chain.Add(literal);
                row = row.Parent is { } parent ? byId.GetValueOrDefault(parent) : null;
            }
            chain.Reverse();
            return new(definition with { PlacementRowsRaw = chain });
        },
        Remove: static definition => definition with { PlacementRowsRaw = null }
    );

    /// <summary>Judges the registered units using the written state row. The resolver admits a unit's source rows
    /// and detaches its values at the caller's clock. A remaining binding asks the caller for wider validation.</summary>
    public static bool TryValidateBoundUnits(WorldDefinition definition, string stateRow, Func<object, object?> resolve,
        out bool needsWholeDocument, out string reason) {
        var remainder = definition;
        needsWholeDocument = false;

        foreach (var partition in Partitions) {
            if (partition.Bound is not { } bound) { continue; }

            foreach (var unit in bound.Enumerate(definition)) {
                if (unit is null || !WorldStateDocumentValues.ReferencesRow(definition, unit, stateRow)) { continue; }

                if (bound.Resolve(definition, unit, resolve) is not { } literal) {
                    reason = $"state '{stateRow}' leaves a {partition.Name} unit it binds unreadable";
                    return false;
                }
                var run = new WorldValidationRun { BoundUnit = true, Definition = literal.Definition };

                RunPartition(partition, run, literal.Rows, units: 1);
                if (run.Errors.Count > 0) {
                    reason = $"state '{stateRow}' leaves a {partition.Name} unit it binds invalid: {run.Errors[0]}";
                    return false;
                }
            }
            remainder = bound.Remove(remainder);
        }
        needsWholeDocument = WorldStateDocumentValues.ReferencesRow(definition, remainder, stateRow);
        reason = string.Empty;
        return true;
    }

    private static IEnumerable<int> RowOrdinals(int count, IReadOnlyList<int>? selected) =>
        selected is null ? Enumerable.Range(0, count) : selected;

    private static List<int> ChangedRows<TRow>(IReadOnlyList<TRow> before, IReadOnlyList<TRow> after) where TRow : class {
        var selected = new List<int>();
        if (ReferenceEquals(before, after)) { return selected; }
        var previous = new HashSet<TRow>(before, ReferenceEqualityComparer.Instance);

        for (var index = 0; index < after.Count; index++) {
            if (after[index] is not { } row || !previous.Contains(row)) { selected.Add(index); }
        }
        return selected;
    }

    private static HashSet<string> ChangedCreationIds(WorldDefinition before, WorldDefinition after) {
        var earlier = FirstRowsByName(before.Creations, static row => row.Id?.Value);
        var later = FirstRowsByName(after.Creations, static row => row.Id?.Value);
        var changed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (id, row) in earlier) {
            if (!ReferenceEquals(row, later.GetValueOrDefault(id))) { changed.Add(id); }
        }
        foreach (var id in later.Keys) {
            if (!earlier.ContainsKey(id)) { changed.Add(id); }
        }
        return changed;
    }

    private static IReadOnlyList<int> ScreenRowsReachingCreations(WorldDefinition before, WorldDefinition after) {
        var changed = ChangedRows(before.Screens, after.Screens).ToHashSet();
        var creations = ChangedCreationIds(before, after);
        var looks = FirstRowsByName(after.Looks, static look => look.Name?.Value);

        for (var index = 0; index < after.Screens.Count; index++) {
            if (after.Screens[index]?.Carry?.Look is { } name &&
                looks.GetValueOrDefault(name)?.Source is WorldLookSource.Creation source &&
                creations.Contains(source.PrototypeId.Value)) {
                changed.Add(index);
            }
        }
        return [.. changed.Order()];
    }

    private static IReadOnlyList<int> PlacementRowsReached(WorldDefinition before, WorldDefinition after, bool prototypesChanged) {
        var rows = after.Placements;
        var previous = WorldPlacementIndex.ById(before.Placements);
        var next = WorldPlacementIndex.ById(rows);
        var changed = new HashSet<string>(StringComparer.Ordinal);
        var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var creationIds = prototypesChanged ? ChangedCreationIds(before, after) : null;
        var work = new Queue<string>();

        foreach (var id in previous.Keys) {
            if (!next.ContainsKey(id)) { Reach(id); }
        }
        foreach (var row in rows) {
            if (row is not { Id: not null }) { continue; }
            if (!ReferenceEquals(row, previous.GetValueOrDefault(row.Id)) ||
                (creationIds is { Count: > 0 } && ReferencesCreation(row, creationIds))) {
                Reach(row.Id);
            }
            if (row.Parent is { Length: > 0 } parent) { Link(parent, row.Id); }
            foreach (var target in row.Surface?.Targets ?? []) {
                if (target is not null) { Link(target, row.Id); }
            }
        }
        while (work.TryDequeue(out var id)) {
            if (!dependents.TryGetValue(id, out var readers)) { continue; }
            foreach (var reader in readers) { Reach(reader); }
        }
        var selected = new List<int>();

        for (var index = 0; index < rows.Count; index++) {
            if (rows[index] is not { Id: not null } row || changed.Contains(row.Id)) { selected.Add(index); }
        }
        return selected;

        void Reach(string id) {
            if (changed.Add(id)) { work.Enqueue(id); }
        }
        void Link(string input, string reader) {
            if (!dependents.TryGetValue(input, out var readers)) {
                readers = [];
                dependents.Add(input, readers);
            }
            readers.Add(reader);
        }
    }

    private static bool ReferencesCreation(WorldPlacement placement, HashSet<string> ids) =>
        ids.Contains(placement.PrototypeId) ||
        (placement.Respond?.Any(response => response?.PrototypeId is { } id && ids.Contains(id)) == true) ||
        (placement.Deal?.Variants?.Map?.Values.Any(ids.Contains) == true);

    private static WorldDefinition WithSky(WorldDefinition definition, WorldRenderSky? sky) =>
        definition with { RenderRaw = definition.Render with { Sky = sky } };

    private static void ValidateRenderUnit(WorldValidationRun run) {
        var definition = run.Definition;
        var errors = run.Errors;
        var lighting = definition.Render.Lighting;

        if (!run.BoundUnit) {
            ValidateRenderFarDistance(definition.Render.FarDistance, errors);
            ValidateExposure(definition, definition.Render.Exposure, errors);
            ValidateRenderResolution(definition, errors);
            lighting = ExpandValidatedLighting(definition, errors);
            ValidateRenderLighting(definition, lighting, errors);
            ValidateRenderEnvironment(definition, definition.Render.Environment, errors);
            ValidateRenderAtmosphere(definition, definition.Render.Atmosphere, errors);
        }
        ValidateRenderSky(definition: definition, sky: ExpandValidatedSky(definition, errors), errors: errors,
            lighting: ResolvedLightingShape(lighting));
    }

    private static ValidationScope DeclaredPlacementScope(WorldDefinition definition) => DeclaredSourceScope(definition) with {
        KitNames = DeclaredNames(rows: definition.Kits, name: static kit => kit.Name),
        LookNames = DeclaredNames(rows: definition.Looks, name: static look => look.Name?.Value),
        PatchIds = DeclaredNames(rows: definition.Patches, name: static patch => patch.Name),
        PrototypeIds = DeclaredNames(rows: definition.Creations, name: static creation => creation.Id?.Value),
    };

    private static ValidationScope DeclaredScreenScope(WorldDefinition definition, IMachineValidationCatalog? machines) =>
        DeclaredSourceScope(definition) with {
            Cameras = DeclaredNames(rows: definition.Cameras, name: static camera => camera.Name),
            Machines = machines,
        };

    private static ValidationScope DeclaredSourceScope(WorldDefinition definition) {
        var text = definition.Text;
        return new ValidationScope {
            DestinationNames = DeclaredNames(rows: definition.Destinations, name: static destination => destination.Name),
            FontNames = DeclaredNames(rows: text?.Fonts, name: static font => font.Name),
            HasTextCatalog = text is not null,
        };
    }

    // References resolve against present declarations. The owning validator judges blanks and duplicates.
    private static HashSet<string> DeclaredNames<TRow>(IEnumerable<TRow?>? rows, Func<TRow, string?> name) where TRow : class {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var row in (rows ?? [])) {
            if ((row is not null) && (name(arg: row) is { } value) && !string.IsNullOrWhiteSpace(value: value)) { _ = names.Add(item: value); }
        }
        return names;
    }

    private static HashSet<string> ValidateUniqueRowNames<TRow>(IReadOnlyList<TRow> rows, Func<TRow, string?> name, string section, string field, List<string> errors) where TRow : class {
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < rows.Count; index++) {
            if (rows[index] is { } row) { RequireUniqueName(name(row), names, $"{section}[{index}]", field, errors); }
        }
        return names;
    }
}
