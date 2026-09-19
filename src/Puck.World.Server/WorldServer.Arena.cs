using Puck.Maths;

namespace Puck.World.Server;

/// <summary>The server's columnar store: the arena is the authoritative state during a tick, and the installed
/// document is the arena's export at the end of it.</summary>
/// <remarks>Every seeding of the arena — construction, a document install, a checkpoint restore — runs the authored
/// rows through <see cref="StateArena.TryLoad"/>, the one import door, so a value the document could not write is
/// refused by row and cell name rather than stored.</remarks>
public sealed partial class WorldServer {
    private StateArena m_arena = null!;
    private StateCatalog? m_arenaCatalog;
    private string[] m_drawSites = [];
    private ulong[] m_rowVersionMarks = [];
    private HashSet<string>? m_documentValueRows;
    // Document rows occupy the catalog's first ordinals, so this bounds every walk that only the exported document
    // depends on — a slot lane's write moves its own row's version and nothing the document carries.
    private int m_documentRowCount;

    /// <summary>Gets the columnar store every state read and write of the tick in flight addresses.</summary>
    public StateArena Arena => m_arena;

    // The site descriptor a draw's seed ladder folds. It must stay the document's own "state.<row>" spelling: the
    // seed, and therefore every shuffle permutation and random transfer, is a function of this string.
    internal static string[] DrawSitesOf(StateCatalog catalog) {
        var sites = new string[catalog.Count];

        for (var ordinal = 0; (ordinal < catalog.Count); ordinal++) {
            var descriptor = catalog.Descriptors[ordinal];

            sites[ordinal] = ((descriptor.Lane == StateLane.Document)
                ? WorldDrawSites.StateRow(rowName: CellName.Parse(candidate: descriptor.Name))
                : descriptor.Name
            );
        }

        return sites;
    }

    /// <summary>Returns the descriptor a draw site's seed ladder and stream id fold.</summary>
    /// <param name="rowOrdinal">The site row's catalog ordinal.</param>
    /// <returns>The site descriptor.</returns>
    public string DrawSite(int rowOrdinal) => ((((uint)rowOrdinal) < ((uint)m_drawSites.Length))
        ? m_drawSites[rowOrdinal]
        : string.Empty
    );

    // Refuses a document the arena cannot hold. A document the validator passed and the arena refuses is a
    // validator hole, never an authoring error reaching this far.
    private void BuildArena(WorldDefinition definition) {
        var catalog = definition.StateCatalog;
        var time = m_ruleHost.Time;

        if (!StateArena.TryCreate(
            arena: out var built,
            catalog: catalog,
            options: WorldSlotLanes.Options(definition: definition),
            reason: out var reason,
            section: definition.StateRaw,
            time: in time
        )) {
            throw new InvalidOperationException(message: $"the world's state section does not load into the arena: {reason}");
        }

        // The slot lanes belong to this host's session, not to the document being installed, so a replacement
        // arena inherits them through the same routine a relayout carries them with. Without this a fallback build
        // would silently re-birth every live body's registers at their authored initials.
        m_arena?.CopyLanesTo(target: built);
        m_arena = built;
        AdoptLayout(definition: definition);
    }
    // An install is a birth: the arena's load settles a clock on every cell whose effective behavior is timed and
    // whose installed record carries none, so a rotation, an accumulation, or an ease runs from the tick the cell
    // was installed at rather than from the origin, and recomputes every derived board from its own tokens and
    // codes rows. A clockless cell's clock and a derived board's cells are all this adopts — every other field of
    // every row stays the document's own, which is why this settles through the export rather than replacing the
    // section with it. A load the arena refuses is left to BuildArena, which names the row and cell it is about.
    private WorldDefinition SettleInstalledRows(WorldDefinition definition) {
        var rows = definition.State;

        if (rows.Count == 0) {
            return definition;
        }

        var time = m_ruleHost.Time;

        if (!StateArena.TryCreate(
            arena: out var seeded,
            catalog: definition.StateCatalog,
            options: WorldSlotLanes.Options(definition: definition),
            reason: out _,
            section: definition.StateRaw,
            time: in time
        )) {
            return definition;
        }

        var exported = seeded.ToRows();
        List<WorldStateRow>? settled = null;

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];

            if (StateRows.FindStateRow(
                rows: exported,
                name: row.Name.Value
            ) is not { } born) {
                continue;
            }
            // A derived board's cells are the arena's own recompute, never the document's — the board is never
            // authored, only derived. A board the recompute agrees with is left as the very object the install
            // handed over, so an install that changed no board keeps the definition it was given and its
            // compilation receipt with it.
            if (row.Inverse is not null) {
                if (SameBoardCells(
                    left: row.Cells,
                    right: born.Cells
                )) {
                    continue;
                }

                settled ??= new List<WorldStateRow>(collection: rows);
                settled[index] = (row with { Cells = born.Cells });

                continue;
            }
            if (row.Cells is not { Count: > 0 } cells) {
                continue;
            }

            List<StateCell>? bornCells = null;

            for (var cell = 0; (cell < cells.Count); cell++) {
                if (
                    (cells[cell].Clock is not null) ||
                    (StateRows.FindCell(
                    cells: born.Cells,
                    key: cells[cell].Key
                )?.Clock is not { } clock)
                ) {
                    continue;
                }

                bornCells ??= new List<StateCell>(collection: cells);
                bornCells[cell] = (cells[cell] with { Clock = clock });
            }

            if (bornCells is null) {
                continue;
            }

            settled ??= new List<WorldStateRow>(collection: rows);
            settled[index] = (row with { Cells = bornCells });
        }

        return ((settled is null)
            ? definition
            : definition.WithWorldState(rows: settled)
        );
    }
    // A board is a key and a value per occupied cell; nothing else about its cells is derived, so nothing else
    // decides whether the recompute moved it.
    private static bool SameBoardCells(IReadOnlyList<StateCell>? left, IReadOnlyList<StateCell>? right) {
        var authored = (left ?? []);
        var derived = (right ?? []);

        if (authored.Count != derived.Count) {
            return false;
        }

        for (var index = 0; (index < authored.Count); index++) {
            if (
                (authored[index].Key != derived[index].Key) ||
                (authored[index].Value != derived[index].Value)
            ) {
                return false;
            }
        }

        return true;
    }
    // Rebinds everything addressed by catalog ordinal after the arena's layout moved: the draw sites the seed
    // ladder folds, the per-row version marks, the bodies' action-state slot lanes, and the host that carries them.
    private void AdoptLayout(WorldDefinition definition) {
        m_arenaCatalog = definition.StateCatalog;
        m_drawSites = DrawSitesOf(catalog: m_arenaCatalog);
        m_documentRowCount = m_arenaCatalog.Lane(lane: StateLane.Document).Count;
        m_rowVersionMarks = new ulong[m_arena.Layout.RowCount];
        m_population.BindActionStateLane(
            arena: m_arena,
            definition: definition
        );
        m_arenaHost = new WorldArenaHost(
            arena: m_arena,
            documentSeed: (definition.Generation?.WorldSeed ?? 0UL),
            dynamics: definition.Dynamics,
            generators: definition.Generators,
            instanceIdentity: InstanceIdentity,
            sites: m_drawSites,
            ticksPerSecond: definition.SimulationRateHz,
            verdicts: WorldVerdictStamp.From(
                catalog: m_arenaCatalog,
                definition: definition
            )
        );
    }
    // Re-seeds the arena from an installed document. A document whose catalog is the same instance keeps its
    // columns and reloads their values; a re-declared row set relayouts in place, which moves every column to its
    // new ordinal and carries the participant and identity lanes across, where a fresh build would drop them.
    public void SyncArena(WorldDefinition definition) {
        var time = m_ruleHost.Time;

        if (ReferenceEquals(
            objA: m_arenaCatalog,
            objB: definition.StateCatalog
        )) {
            if (!m_arena.TryLoad(
                reason: out var reason,
                rows: definition.State,
                time: in time
            )) {
                BuildArena(definition: definition);

                return;
            }
            if (reason.Length != 0) {
                throw new InvalidOperationException(message: $"the installed document does not load into the arena: {reason}");
            }

            return;
        }
        if (
            m_arena.TryRelayout(
            catalog: definition.StateCatalog,
            reason: out _,
            section: definition.StateRaw,
            time: in time
        ) &&
            m_arena.TryLoad(
            reason: out var loaded,
            rows: definition.State,
            time: in time
        ) &&
            (loaded.Length == 0)
        ) {
            AdoptLayout(definition: definition);

            return;
        }

        BuildArena(definition: definition);
    }
    // Remembers every row's version before the tick's rules run, so the end-of-tick install can tell a tick that
    // wrote nothing from one that did without rebuilding the export first.
    public void MarkRowVersions() {
        var rows = m_arena.Layout.RowCount;

        if (m_rowVersionMarks.Length < rows) {
            m_rowVersionMarks = new ulong[rows];
        }
        for (var ordinal = 0; (ordinal < rows); ordinal++) {
            m_rowVersionMarks[ordinal] = m_arena.RowVersion(rowOrdinal: ordinal);
        }
    }
    private bool ArenaMoved() {
        var rows = Math.Min(
            val1: m_documentRowCount,
            val2: m_arena.Layout.RowCount
        );

        for (var ordinal = 0; (ordinal < rows); ordinal++) {
            if (m_rowVersionMarks[ordinal] != m_arena.RowVersion(rowOrdinal: ordinal)) {
                return true;
            }
        }

        return false;
    }
    // Whether any row a document value reads by name moved since the mark. The referenced set is a function of the
    // document's own non-state sections, so it is collected once per installed document rather than per tick.
    private bool ReferencedRowMoved() {
        if (m_documentValueRows is null) {
            m_documentValueRows = new HashSet<string>(comparer: StringComparer.Ordinal);

            WorldStateDocumentValues.CollectReferencedRows(
                definition: m_document.Definition,
                rows: m_documentValueRows
            );
        }
        if (m_documentValueRows.Count == 0) {
            return false;
        }

        var descriptors = m_arena.Catalog.Descriptors;
        var rows = m_arena.Layout.RowCount;

        for (var ordinal = 0; (ordinal < rows); ordinal++) {
            if (
                (m_rowVersionMarks[ordinal] != m_arena.RowVersion(rowOrdinal: ordinal)) &&
                m_documentValueRows.Contains(item: descriptors[ordinal].Name)
            ) {
                return true;
            }
        }

        return false;
    }
    // The document becomes the arena's export. Every exported row is the row the arena was built over with its
    // stored columns written back, so a WorldStateRow's own traits ride across and the cast is total.
    public bool InstallArenaExport() {
        if (!ArenaMoved()) {
            return false;
        }

        var exported = m_arena.ToRows();
        var rows = new WorldStateRow[exported.Count];

        for (var index = 0; (index < exported.Count); index++) {
            rows[index] = ((WorldStateRow)exported[index]);
        }

        // A document value bound to a state row — a placement's spatial extent, a creation's scale — is resolved
        // against the row's value, so a row a rule wrote re-resolves the values reading it.
        var refresh = ReferencedRowMoved();

        m_document.AdoptDefinition(definition: m_document.Definition.WithWorldState(rows: rows));

        if (refresh) {
            if (WorldStateDocumentValues.TryRehydrate(
                definition: m_document.Definition,
                reason: out var refreshReason,
                refreshed: out var refreshed
            )) {
                m_document.AdoptDefinition(definition: refreshed);
            } else if (m_output.HasNarrationSink) {
                m_output.Narrate(
                    channel: "world.state",
                    text: $"[world.state: a document value reading a row this tick wrote did not re-resolve — {refreshReason}]"
                );
            }
        }

        m_arenaCatalog = m_document.Definition.StateCatalog;
        // The installed document now carries values no sink has seen: a rule's own write reaches a client through the
        // same state delivery a console write does.
        m_document.MarkStateDeliveryPending();

        // The document equals the arena again, so this is where the next export's baseline sits.
        MarkRowVersions();

        return true;
    }
    // Every search job is one resolved plan and one judge over this server's own arena, admitted at install: a
    // judge whose rules the host cannot serve refuses the plan by name rather than at a tick.
    private void RebuildSearch(WorldDefinition definition) {
        if (!WorldSearchCompilation.TryPlanAll(
            definition: definition,
            judge: out var judgeRules,
            plans: out var authored,
            reason: out var planReason,
            rules: m_ruleHost.Rules,
            scores: out var scores
        )) {
            throw new InvalidOperationException(message: $"search failed to plan after validation: {planReason}");
        }

        var plans = new ArenaSearchPlan[authored.Length];
        var judges = new IArenaSearchJudge[authored.Length];
        var host = new ArenaSearchEffectHost(
            arena: m_arena,
            documentSeed: (definition.Generation?.WorldSeed ?? 0UL),
            dynamics: definition.Dynamics,
            generators: definition.Generators,
            instanceIdentity: InstanceIdentity,
            ticksPerSecond: definition.SimulationRateHz
        );

        for (var index = 0; (index < authored.Length); index++) {
            if (!ArenaSearchPlan.TryResolve(
                catalog: m_arena.Catalog,
                drawSeed: 0UL,
                plan: authored[index],
                reason: out var resolveReason,
                resolved: out var resolved
            )) {
                throw new InvalidOperationException(message: $"search '{authored[index].Name}' does not resolve against the arena: {resolveReason}");
            }

            plans[index] = resolved;
            judges[index] = new RuleArenaSearchJudge(
                host: host,
                rules: judgeRules,
                score: scores[index]
            );
        }

        if (!m_search.Rebuild(
            judges: judges,
            plans: plans,
            reason: out var rebuildReason
        )) {
            throw new InvalidOperationException(message: $"search failed to install after validation: {rebuildReason}");
        }
    }

    // The placement ordinal a 'placement:<id>'/'placement:$each' reference resolves to: the id's index in the
    // document's own placements list, rebuilt when the list is a new reference.
    private IReadOnlyList<WorldPlacement>? m_placementOrdinalsFrom;
    private Dictionary<string, int> m_placementOrdinals = [];

    internal int PlacementOrdinalOf(string id) {
        var placements = m_document.Definition.Placements;

        if (!ReferenceEquals(
            objA: m_placementOrdinalsFrom,
            objB: placements
        )) {
            var ordinals = new Dictionary<string, int>(
                capacity: placements.Count,
                comparer: StringComparer.Ordinal
            );

            for (var ordinal = 0; (ordinal < placements.Count); ordinal++) {
                ordinals[placements[ordinal].Id] = ordinal;
            }

            m_placementOrdinals = ordinals;
            m_placementOrdinalsFrom = placements;
        }

        return (m_placementOrdinals.TryGetValue(
            key: id,
            value: out var found
        )
            ? found
            : -1
        );
    }
    /// <summary>Returns one cell's value as a fixed-point number, or zero when the cell is absent.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The interned cell key.</param>
    /// <returns>The value, lifted from the row's own kind.</returns>
    internal FixedQ4816 ReadArenaCell(int rowOrdinal, CellKey key) {
        if (((uint)rowOrdinal) >= ((uint)m_arena.Layout.RowCount)) {
            return FixedQ4816.Zero;
        }

        var layout = m_arena.Layout[rowOrdinal];
        var time = m_ruleHost.Time;

        if (!m_arena.TryReadLiveNumber(
            key: key,
            rowOrdinal: rowOrdinal,
            time: in time,
            value: out var raw
        )) {
            return FixedQ4816.Zero;
        }

        return ((layout.Kind == CellKind.Fixed)
            ? FixedQ4816.FromRawBits(value: raw)
            : StateReader.LiftSaturating(raw: raw)
        );
    }
    // The $argmax:/$argmin: extremum over a keyed row whose keys are body indices, filtered to the indices the live
    // population holds. Ties resolve to the lowest eligible index; -1 means no cell was eligible.
    internal int ResolveArgBodyOrdinal(int rowOrdinal, StateReduceOp op, int filterRowOrdinal) {
        if (((uint)rowOrdinal) >= ((uint)m_arena.Layout.RowCount)) {
            return -1;
        }

        var best = -1;
        var bestValue = FixedQ4816.Zero;
        var count = m_arena.CellCount(rowOrdinal: rowOrdinal);

        for (var position = 0; (position < count); position++) {
            if (!m_arena.TryKeyAt(
                key: out var key,
                position: position,
                rowOrdinal: rowOrdinal
            )) {
                continue;
            }

            var name = m_arena.Catalog.Keys[key].Value;

            if (
                !int.TryParse(
                s: name,
                style: System.Globalization.NumberStyles.Integer,
                provider: System.Globalization.CultureInfo.InvariantCulture,
                result: out var index
            ) ||
                (index < 0) ||
                (index >= m_population.Capacity) ||
                (Body(index: index) is null)
            ) {
                continue;
            }
            if (
                (filterRowOrdinal >= 0) &&
                (ReadArenaCell(
                key: key,
                rowOrdinal: filterRowOrdinal
            ) == FixedQ4816.Zero)
            ) {
                continue;
            }

            var value = ReadArenaCell(
                key: key,
                rowOrdinal: rowOrdinal
            );

            if (
                (best < 0) ||
                ((op == StateReduceOp.Max)
                ? (value > bestValue)
                : (value < bestValue))
            ) {
                best = index;
                bestValue = value;
            }
        }

        return best;
    }
    public WorldDefinition RecompileRules(WorldDefinition definition, WorldRuleCompilation? compilation = null) {
        definition = SettleInstalledRows(definition: definition);
        m_document.AdoptDefinition(definition: definition);
        // The rows a document value reads are a function of the sections this install may have replaced.
        m_documentValueRows = null;
        // Recomposition may change dependencies: only the exact definition can reuse its validation result.
        if (!ReferenceEquals(
            objA: compilation?.Definition,
            objB: definition
        )) { compilation = WorldRuleCompilation.Compile(definition: definition); }
        m_ruleHost.Install(compilation: compilation!);
        SyncArena(definition: definition);
        m_ruleHost.PruneLatches();
        m_tick.PruneBoardEnforcement(definition: definition);
        m_ruleHost.ReconcileDecisions();
        m_ruleHost.ReconcilePatterns(definition: definition);

        RebuildSearch(definition: definition);
        m_population.BindFlockAffinities(
            definition: definition,
            reader: m_ruleHost.EvaluateFlockAffinity
        );

        return definition;
    }
    // The search jobs advance right after the rules, so a job judges the position this tick's rules settled and a
    // finished job's outputs are delivered with the same tick.
    private ulong m_searchTick;

    /// <summary>Advances every search job, delivering a landed job's writes with the same tick.</summary>
    /// <param name="tick">The simulation tick.</param>
    public void StepSearch(ulong tick) {
        m_searchTick = tick;

        if (m_search.Step(
            apply: m_searchApply,
            engineTick: CompletedEngineTicks,
            tick: tick
        )) {
            m_document.DeliverPending();
        }
    }

    /// <summary>Lists every search job's progress.</summary>
    public IReadOnlyList<ArenaSearchStatus> SearchStatus() {
        var status = new ArenaSearchStatus[m_search.Count];

        for (var index = 0; (index < status.Length); index++) {
            status[index] = m_search.Status(index: index);
        }

        return status;
    }

}
