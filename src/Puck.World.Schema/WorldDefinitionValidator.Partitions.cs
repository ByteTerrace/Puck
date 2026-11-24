using Puck.Abstractions.Machines;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // What reading the state rows reads: the declarations as the expansion lays them out (pool carriers bind bodies),
    // and, for a value, the rate and dynamics a cell's clock is read through (WorldStateReader).
    private static readonly WorldDocumentInputs StateInputs = (WorldDocumentInputs.State |
        WorldDocumentInputs.Of(WorldDocumentInput.Identity, WorldDocumentInput.Properties, WorldDocumentInput.Bodies, WorldDocumentInput.Simulation, WorldDocumentInput.Dynamics));
    private static readonly WorldDocumentInputs PlacementRowInputs = WorldDocumentInputs.Of(
        WorldDocumentInput.PlacementRows, WorldDocumentInput.Prototypes);

    // The one table of validation partitions, in the order a whole-document validation runs them. The state expansion
    // runs first, since every reader of the state rows reads them expanded; the document partition runs last, so the
    // checks it gates on a clean collection (the rule work budget, the census proofs) see every partition's refusals.
    // Every part a document holds is owned by exactly one partition (VerifyPartitions).
    private static readonly WorldValidationPartition[] PartitionTable = [
        new(
            name: "state.expansion",
            granularity: WorldValidationGranularity.Section,
            owns: WorldDocumentInputs.None,
            reads: StateInputs,
            validate: static (run, _) => {
                if (!TryValidatePoolExpansion(definition: run.Definition, reason: out var reason)) {
                    run.Errors.Add(item: reason);
                    run.Stopped = true;
                }
            }
        ),
        new(
            name: "prototypes",
            granularity: WorldValidationGranularity.Rows,
            owns: WorldDocumentInputs.Of(WorldDocumentInput.Prototypes),
            reads: (StateInputs | WorldDocumentInputs.Of(WorldDocumentInput.Text, WorldDocumentInput.Dynamics, WorldDocumentInput.Channels, WorldDocumentInput.Curves)),
            validate: static (run, rows) => ValidateCreationRows(definition: run.Definition, rows: rows, errors: run.Errors),
            rowCount: static definition => definition.Creations.Count,
            rowChange: CreationRowChange,
            bound: BoundRows(static d => d.Creations, static (d, rows) => d with { CreationsRaw = rows })
        ),
        RowPartition(
            name: "placements",
            owns: WorldDocumentInput.PlacementRows,
            reads: StateInputs | WorldDocumentInputs.Of(WorldDocumentInput.Adjacencies, WorldDocumentInput.Channels,
                WorldDocumentInput.Collision, WorldDocumentInput.Destinations,
                WorldDocumentInput.Generation, WorldDocumentInput.KitRows, WorldDocumentInput.LookRows,
                WorldDocumentInput.Patches, WorldDocumentInput.PlacementPolicy, WorldDocumentInput.Prototypes,
                WorldDocumentInput.ScreenMaterials, WorldDocumentInput.Text, WorldDocumentInput.DocumentDirectory),
            get: static d => d.Placements,
            set: static (d, rows) => d with { PlacementRowsRaw = rows },
            validate: static (run, rows) => {
                ValidatePlacements(run.Definition, rows, run.Errors);
                if (run.BoundUnit && !WorldPlacementFrameCompilation.TryValidate(run.Definition.Placements, out var reason)) {
                    run.Errors.Add(item: $"placements: {reason}");
                }
            },
            bound: PlacementBinding(),
            rowChange: static (before, after) => new(PlacementRowsReached(before, after, prototypesChanged: false), false),
            selectDependentRows: static (before, after, changed) => changed.Except(PlacementRowInputs).IsEmpty
                ? PlacementRowsReached(before, after, prototypesChanged: true)
                : null
        ),
        RowPartition(
            name: "screens",
            owns: WorldDocumentInput.Screens,
            reads: StateInputs | WorldDocumentInputs.Of(WorldDocumentInput.Cameras, WorldDocumentInput.Channels,
                WorldDocumentInput.Destinations, WorldDocumentInput.DocumentDirectory, WorldDocumentInput.KitRows,
                WorldDocumentInput.LookRows, WorldDocumentInput.Machines, WorldDocumentInput.Probes, WorldDocumentInput.Prototypes,
                WorldDocumentInput.ScreenMaterials, WorldDocumentInput.Text, WorldDocumentInput.Views),
            get: static d => d.Screens,
            set: static (d, rows) => d with { ScreensRaw = rows },
            validate: ValidateScreens,
            selectDependentRows: static (before, after, changed) =>
                changed.Except(WorldDocumentInputs.Of(WorldDocumentInput.Screens, WorldDocumentInput.Prototypes)).IsEmpty
                    ? ScreenRowsReachingCreations(before, after)
                    : null
        ),
        RowPartition(
            name: "curves",
            owns: WorldDocumentInput.Curves,
            reads: WorldDocumentInputs.None,
            get: static d => d.Curves,
            set: static (d, rows) => d with { CurvesRaw = rows },
            validate: static (run, rows) => _ = ValidateCurves(run.Definition.Curves, run.Errors, rows, checkDeclarations: false)
        ),
        RowPartition(
            name: "probes",
            owns: WorldDocumentInput.Probes,
            reads: StateInputs | WorldDocumentInputs.Of(WorldDocumentInput.Cameras, WorldDocumentInput.Channels,
                WorldDocumentInput.DocumentDirectory, WorldDocumentInput.Views),
            get: static d => d.ProbesRaw ?? [],
            set: static (d, rows) => d with { ProbesRaw = rows },
            validate: static (run, rows) => ValidateProbes(run.Definition,
                DeclaredNames(run.Definition.Cameras, static camera => camera.Name), run.Errors, run.Deferred, rows)
        ),
        RowPartition(
            name: "looks",
            owns: WorldDocumentInput.LookRows,
            reads: StateInputs | WorldDocumentInputs.Of(WorldDocumentInput.Prototypes),
            get: static d => d.Looks,
            set: static (d, rows) => d with { LookRowsRaw = rows },
            validate: static (run, rows) => ValidateLookRows(run.Definition, rows, run.Errors)
        ),
        new(
            name: "looks.assignment",
            granularity: WorldValidationGranularity.Section,
            owns: WorldDocumentInputs.Of(WorldDocumentInput.LookAssignment),
            reads: StateInputs | WorldDocumentInputs.Of(WorldDocumentInput.LookRows),
            validate: static (run, _) => ValidateLookAssignment(run.Definition, run.Definition.LookAssignment,
                DeclaredNames(run.Definition.Looks, static row => row.Name?.Value), run.Errors),
            bound: new WorldBoundValidation(
                Enumerate: static d => d.LookAssignmentRaw is { } assignment ? [assignment] : [],
                Resolve: static (d, unit, resolve) => resolve(unit) is WorldRowAssignment literal ? new(d with { LookAssignmentRaw = literal }) : null,
                Remove: static d => d with { LookAssignmentRaw = null })
        ),
        new(
            name: "render",
            granularity: WorldValidationGranularity.Section,
            owns: WorldDocumentInputs.Of(WorldDocumentInput.Render),
            reads: StateInputs | WorldDocumentInputs.Of(WorldDocumentInput.Timeline, WorldDocumentInput.Curves,
                WorldDocumentInput.Cameras, WorldDocumentInput.PlacementRows, WorldDocumentInput.Prototypes,
                WorldDocumentInput.Screens, WorldDocumentInput.ScreenMaterials, WorldDocumentInput.Shades, WorldDocumentInput.Views),
            validate: static (run, _) => ValidateRenderUnit(run),
            bound: new WorldBoundValidation(
                Enumerate: static d => d.Render.Sky is { } sky ? [sky] : [],
                Resolve: static (d, sky, resolve) => resolve(sky) is WorldRenderSky literal ? new(WithSky(d, literal)) : null,
                Remove: static d => d.Render.Sky is not null ? WithSky(d, null) : d)
        ),
        new(
            name: "hud",
            granularity: WorldValidationGranularity.Section,
            owns: WorldDocumentInputs.Of(WorldDocumentInput.Hud),
            reads: (StateInputs | WorldDocumentInputs.Of(WorldDocumentInput.Cameras, WorldDocumentInput.PlacementRows, WorldDocumentInput.Patches, WorldDocumentInput.DocumentDirectory)),
            validate: static (run, _) => ValidateHud(
                cameras: DeclaredNames(rows: run.Definition.Cameras, name: static camera => camera.Name),
                definition: run.Definition,
                errors: run.Errors,
                hud: run.Definition.Hud,
                delivered: run.Delivered,
                isIdentityScope: (run.Definition.Identity is not null),
                stateRows: DeclaredStateRows(definition: run.Definition)
            )
        ),
        new(
            name: "metadata",
            granularity: WorldValidationGranularity.Section,
            owns: WorldDocumentInputs.Of(WorldDocumentInput.Metadata),
            reads: WorldDocumentInputs.None,
            validate: static (run, _) => ValidateMetadata(metadata: run.Definition.Metadata, errors: run.Errors)
        ),
        new(
            name: "replays",
            granularity: WorldValidationGranularity.Section,
            owns: WorldDocumentInputs.Of(WorldDocumentInput.Replays),
            reads: (StateInputs | WorldDocumentInputs.Of(WorldDocumentInput.Bodies)),
            validate: static (run, _) => ValidateScopedReplays(definition: run.Definition, errors: run.Errors)
        ),
        new(
            name: "haptics",
            granularity: WorldValidationGranularity.Section,
            owns: WorldDocumentInputs.Of(WorldDocumentInput.Haptics),
            reads: (StateInputs | WorldDocumentInputs.Of(WorldDocumentInput.Bodies)),
            validate: static (run, _) => ValidateHaptics(definition: run.Definition, errors: run.Errors, localSeats: run.Definition.Population.LocalSeats)
        ),
        new(
            name: "keyLights",
            granularity: WorldValidationGranularity.Section,
            owns: WorldDocumentInputs.Of(WorldDocumentInput.KeyLights),
            reads: (StateInputs | WorldDocumentInputs.Of(WorldDocumentInput.Bodies)),
            validate: static (run, _) => ValidateKeyLights(definition: run.Definition, errors: run.Errors, localSeats: run.Definition.Population.LocalSeats)
        ),
        new(
            name: "highlights",
            granularity: WorldValidationGranularity.Section,
            owns: WorldDocumentInputs.Of(WorldDocumentInput.Highlights),
            reads: StateInputs,
            validate: static (run, _) => ValidateHighlights(definition: run.Definition, errors: run.Errors)
        ),
        new(
            name: "storage",
            granularity: WorldValidationGranularity.Section,
            owns: WorldDocumentInputs.Of(WorldDocumentInput.Storage),
            reads: WorldDocumentInputs.None,
            validate: static (run, _) => ValidateStorage(storage: run.Definition.Storage, errors: run.Errors)
        ),
        new(
            name: "captures",
            granularity: WorldValidationGranularity.Section,
            owns: WorldDocumentInputs.Of(WorldDocumentInput.Captures),
            reads: WorldDocumentInputs.Of(WorldDocumentInput.Cameras, WorldDocumentInput.Screens, WorldDocumentInput.Views),
            validate: static (run, _) => ValidateCaptures(definition: run.Definition, errors: run.Errors)
        ),
        new(
            name: "schedule",
            granularity: WorldValidationGranularity.Section,
            owns: WorldDocumentInputs.Of(WorldDocumentInput.Schedule),
            reads: WorldDocumentInputs.None,
            validate: static (run, _) => ValidateSchedule(definition: run.Definition, errors: run.Errors)
        ),
        new(
            name: "routes",
            granularity: WorldValidationGranularity.Section,
            owns: WorldDocumentInputs.Of(WorldDocumentInput.SeatPrograms, WorldDocumentInput.Routes),
            reads: WorldDocumentInputs.Of(WorldDocumentInput.PlacementRows, WorldDocumentInput.SpawnPoints, WorldDocumentInput.Channels, WorldDocumentInput.DocumentId),
            validate: static (run, _) => ValidateRoutes(definition: run.Definition, errors: run.Errors)
        ),
        new(
            name: "admission",
            granularity: WorldValidationGranularity.Section,
            owns: WorldDocumentInputs.Of(WorldDocumentInput.Admission),
            reads: WorldDocumentInputs.Of(WorldDocumentInput.Bodies),
            validate: static (run, _) => ValidateAdmission(deferred: run.Deferred, entries: run.Definition.Admission, errors: run.Errors, populationCapacity: run.Definition.Population.Capacity)
        ),
        new(
            name: "document",
            granularity: WorldValidationGranularity.Section,
            owns: WorldDocumentInputs.All,
            reads: WorldDocumentInputs.All,
            validate: static (run, _) => run.Compilation = ValidateDocumentPartition(run: run)
        ),
    ];

    private static readonly Lazy<WorldValidationPartition[]> VerifiedPartitions = new(valueFactory: VerifyPartitions, mode: LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Gets the validation partitions, in the order a whole-document validation runs them. Every part of a
    /// document is owned by exactly one of them; the last, <c>document</c>, owns every part no other partition
    /// owns and reads the whole document.</summary>
    /// <exception cref="InvalidOperationException">The table leaves a part without an owner or gives one two.</exception>
    public static IReadOnlyList<WorldValidationPartition> Partitions => VerifiedPartitions.Value;

    /// <summary>Returns how many units a whole-document validation of <paramref name="definition"/> validates: every
    /// partition's <see cref="WorldValidationPartition.UnitsIn"/>.</summary>
    /// <param name="definition">The document.</param>
    /// <returns>The unit count, which a whole validation adds to <see cref="WorldBootWork.Validations"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static int UnitsIn(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var units = 0;

        foreach (var partition in Partitions) { units += partition.UnitsIn(definition: definition); }
        return units;
    }

    /// <summary>Validates <paramref name="after"/> as a change of <paramref name="before"/>, running only the units the
    /// change reaches: every changed unit a partition owns, and every unit of a partition that reads a part the change
    /// made visible to it. A row partition's change that only the partition itself reads (a creation differing in what
    /// shading reads alone) reaches no other partition.</summary>
    /// <remarks>The verdict is the whole-document verdict when <paramref name="before"/> was admitted, because every
    /// validator reads only the parts its partition declares (each run is recorded and a read past them refuses), so a
    /// unit that does not run reads nothing the change moved. A unit left invalid in <paramref name="before"/> by a path
    /// that does not validate (a rule firing, a clock) answers nothing here unless the change reaches it, which is what
    /// keeps a hidden unit's validity out of a recipient's verdict. Each unit run adds one to
    /// <see cref="WorldBootWork.Validations"/>.</remarks>
    /// <param name="before">The admitted document the change was made from.</param>
    /// <param name="after">The candidate.</param>
    /// <param name="reason">The collapsed refusal, or empty when the candidate is valid.</param>
    /// <param name="compilation">The candidate's rule programs when <paramref name="retainCompilation"/> is set and the
    /// change reached the document partition, which compiles them; otherwise <see langword="null"/>.</param>
    /// <param name="retainCompilation">Whether to keep the programs the document partition compiles.</param>
    /// <param name="proveNeighbours">Whether the document partition, when it runs, proves the candidate's adjacency
    /// claims and judges its bound starting values, as a load does (<see cref="TryValidate"/>); otherwise it validates
    /// document-local facts only (<see cref="TryValidateLocally(WorldDefinition, out string)"/>).</param>
    /// <param name="neighbours">The resolver a proof of an adjacency claim reads, or <see langword="null"/> when none
    /// is reachable, which refuses an authored claim the proof reaches.</param>
    /// <param name="machines">The machine catalog, or <see langword="null"/> to defer provider checks.</param>
    /// <param name="delivered">Whether <paramref name="after"/> came from another process, so the units the change
    /// reaches are judged as a delivery (<see cref="TryValidateDelivered"/>).</param>
    /// <returns><see langword="true"/> when every unit the change reaches is valid.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="before"/> or <paramref name="after"/> is
    /// <see langword="null"/>.</exception>
    public static bool TryValidateChange(WorldDefinition before, WorldDefinition after, out string reason, out WorldRuleCompilation? compilation,
        bool retainCompilation = false, bool proveNeighbours = false, IWorldNeighbourResolver? neighbours = null, IMachineValidationCatalog? machines = null, bool delivered = false) {
        ArgumentNullException.ThrowIfNull(argument: before);
        ArgumentNullException.ThrowIfNull(argument: after);

        var run = new WorldValidationRun {
            Definition = after,
            Delivered = delivered,
            Machines = machines,
            Neighbours = neighbours,
            RetainCompilation = retainCompilation,
            ValidateAdjacencyClaims = proveNeighbours,
        };

        compilation = null;
        try {
            RunPartitions(run: run, plan: PlanChange(before: before, after: after));
        } catch (InvalidOperationException exception) {
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");
            return false;
        }
        if (run.Errors.Count > 0) {
            reason = DescribeRefusal(errors: run.Errors);
            return false;
        }
        compilation = run.Compilation;
        reason = string.Empty;
        return true;
    }

    // The units a change reaches: null for a partition it does not reach, an empty list for every unit of one it reaches
    // whole, and the changed rows of a row partition it reaches only through them.
    private static IReadOnlyList<int>?[] PlanChange(WorldDefinition before, WorldDefinition after) {
        var table = Partitions;
        var plan = new IReadOnlyList<int>?[table.Count];

        using (WorldDocumentReads.Unrecorded()) {
            var changed = WorldDocumentInputs.Changed(before: before, after: after);
            var visible = changed;
            var rowChanges = new WorldValidationRowChange?[table.Count];

            for (var index = 0; (index < table.Count); index++) {
                if ((table[index].RowChange is { } rowChange) && table[index].Owns.Overlaps(other: changed)) {
                    var change = rowChange(arg1: before, arg2: after);

                    rowChanges[index] = change;
                    if (change.Private) { visible = visible.Except(other: table[index].Owns); }
                }
            }
            for (var index = 0; (index < table.Count); index++) {
                var partition = table[index];

                if (partition.Reads.Except(other: partition.Owns).Overlaps(other: visible)) {
                    var selected = partition.SelectDependentRows?.Invoke(before, after, visible.Intersect(partition.Reads));
                    plan[index] = selected is null ? WholePartition : selected.Count > 0 ? selected : null;
                } else if (rowChanges[index] is { } change) {
                    plan[index] = ((change.Rows.Count > 0) ? change.Rows : null);
                } else if (partition.Owns.Overlaps(other: changed)) {
                    plan[index] = WholePartition;
                }
            }
        }
        return plan;
    }
    private static readonly int[] WholePartition = [];

    // Runs the planned units of every partition in table order (every unit of every partition for a null plan),
    // recording each run's reads and refusing one that read past its partition's declared parts.
    private static void RunPartitions(WorldValidationRun run, IReadOnlyList<int>?[]? plan) {
        var table = Partitions;

        for (var index = 0; ((index < table.Count) && !run.Stopped); index++) {
            var partition = table[index];
            IReadOnlyList<int>? rows;

            if (plan is null) {
                rows = null;
            } else if (plan[index] is not { } planned) {
                continue;
            } else {
                rows = ((planned.Count == 0) ? null : planned);
            }

            RunPartition(partition, run, rows, rows?.Count ?? partition.UnitsIn(definition: run.Definition));
        }
    }

    private static void RunPartition(WorldValidationPartition partition, WorldValidationRun run, IReadOnlyList<int>? rows, int units) {
        WorldBootWork.Count(kind: WorldBootWork.Validations, amount: units);
        var recording = WorldDocumentReads.Begin();
        WorldDocumentInputs read;

        try { partition.Validate(arg1: run, arg2: rows); }
        finally { read = recording.End(); }

        var undeclared = read.Except(other: partition.Reads);

        if (!undeclared.IsEmpty) {
            run.Errors.Add(item: $"validation partition '{partition.Name}' read {undeclared}, which its declared inputs do not name.");
        }
    }

    private static string DescribeRefusal(List<string> errors) {
        try {
            RefuseCollected(errors: errors);
        } catch (InvalidOperationException exception) {
            return exception.Message.ReplaceLineEndings(replacementText: " ");
        }
        return string.Empty;
    }

    // The table's own check, at first use: the partitions' owned parts are disjoint and cover every part, the document
    // partition is last and owns what no other partition owns, and every name is distinct.
    private static WorldValidationPartition[] VerifyPartitions() {
        var problems = new List<string>();
        var owned = WorldDocumentInputs.None;
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        var last = PartitionTable[^1];

        for (var index = 0; (index < (PartitionTable.Length - 1)); index++) {
            var partition = PartitionTable[index];

            if (!names.Add(item: partition.Name)) { problems.Add(item: $"two partitions are named '{partition.Name}'"); }
            if (owned.Overlaps(other: partition.Owns)) { problems.Add(item: $"partition '{partition.Name}' owns {owned.Intersect(other: partition.Owns)}, which another partition owns"); }
            owned |= partition.Owns;
        }
        if (!string.Equals(a: last.Name, b: "document", comparisonType: StringComparison.Ordinal) || (last.Reads != WorldDocumentInputs.All)) {
            problems.Add(item: "the last partition must be the document partition, reading the whole document");
        }
        if (problems.Count > 0) {
            throw new InvalidOperationException(message: $"The validation partition table is unsound: {string.Join(separator: "; ", values: problems)}.");
        }

        var table = (WorldValidationPartition[])PartitionTable.Clone();

        // The document partition owns exactly what the others leave, so no part is without an owner.
        table[^1] = new WorldValidationPartition(
            granularity: last.Granularity,
            name: last.Name,
            owns: WorldDocumentInputs.All.Except(other: owned),
            reads: last.Reads,
            validate: last.Validate
        );
        return table;
    }

    // A creation row change: the same ids in the same order with each replaced row differing in what shading reads alone
    // is private to this partition; any other change validates each row that is not the earlier document's row of its
    // id, and every other partition that reads the creations sees it.
    private static WorldValidationRowChange CreationRowChange(WorldDefinition before, WorldDefinition after) {
        var earlier = before.Creations;
        var later = after.Creations;
        var rows = new List<int>();

        if (earlier.Count == later.Count) {
            var aligned = true;
            var shading = true;

            for (var index = 0; (index < later.Count); index++) {
                if (ReferenceEquals(objA: earlier[index], objB: later[index])) { continue; }
                rows.Add(item: index);
                if ((earlier[index] is not { } left) || (later[index] is not { } right) ||
                    !string.Equals(a: left.Id.Value, b: right.Id.Value, comparisonType: StringComparison.Ordinal)) {
                    aligned = false;
                } else if (shading && !left.DiffersInShadingAlone(other: right)) {
                    shading = false;
                }
            }
            if (aligned) { return new WorldValidationRowChange(Private: shading, Rows: rows); }
            rows.Clear();
        }

        var previous = new Dictionary<string, WorldPrototype>(comparer: StringComparer.Ordinal);

        foreach (var row in earlier) {
            if (row is not null) { _ = previous.TryAdd(key: row.Id.Value, value: row); }
        }
        for (var index = 0; (index < later.Count); index++) {
            if ((later[index] is not { } row) || !previous.TryGetValue(key: row.Id.Value, value: out var old) || !ReferenceEquals(objA: old, objB: row)) {
                rows.Add(item: index);
            }
        }
        return new WorldValidationRowChange(Private: false, Rows: rows);
    }

    // The state rows a binding resolves against: each authored row by its name, the first of a duplicated name.
    private static Dictionary<string, WorldStateRow> DeclaredStateRows(WorldDefinition definition) =>
        FirstRowsByName(rows: definition.State.Where(predicate: static row => row is { Generated: false }), name: static row => row.Name);
    // A section's rows keyed by name, the first row of a duplicated name winning, as a reference resolves by a scan.
    private static Dictionary<string, TRow> FirstRowsByName<TRow>(IEnumerable<TRow?> rows, Func<TRow, string?> name) where TRow : class {
        var byName = new Dictionary<string, TRow>(comparer: StringComparer.Ordinal);

        foreach (var row in rows) {
            if ((row is not null) && (name(arg: row) is { } key)) { _ = byName.TryAdd(key: key, value: row); }
        }
        return byName;
    }
}
