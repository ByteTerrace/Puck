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
    // Each row's version as of the last time the installed document and the arena agreed on it: when the arena was
    // seeded from the document, and when the row was last published. A row whose version has moved past this is
    // what the next publication carries, whenever the write happened.
    private ulong[] m_publishedVersions = [];
    // A seeded arena holds the document's rows as authored, and a published row is the arena's own spelling of it:
    // a row holding no cell carries none rather than an empty list. The first publication after a seed therefore
    // carries every row, so the installed document is in one spelling from then on.
    private bool m_publishEveryRow;
    // One flag per catalog ordinal: the rows an open scope has written, as of the proposal in flight.
    private bool[] m_openRows = [];
    // What the last exports moved that a consumer outside the arena keeps its own copy of, until SettleStateConsumers
    // brings them up to date.
    private bool m_bodyScaleOwed;
    private bool m_consumersOwed;
    private bool m_driveGateOwed;
    private bool m_fieldsOwed;
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
        m_publishedVersions = new ulong[m_arena.Layout.RowCount];
        MarkPublished();
        m_publishEveryRow = true;
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

            MarkPublished();
            m_publishEveryRow = true;

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

    // The document and the arena agree on every row as they stand.
    private void MarkPublished() {
        var rows = m_arena.Layout.RowCount;

        if (m_publishedVersions.Length < rows) {
            m_publishedVersions = new ulong[rows];
        }
        for (var ordinal = 0; (ordinal < rows); ordinal++) {
            m_publishedVersions[ordinal] = m_arena.RowVersion(rowOrdinal: ordinal);
        }
    }
    // The rows a document value reads by name. The set is a function of the document's own non-state sections, so
    // it is collected once per installed document rather than per publication.
    private HashSet<string> DocumentValueRows() {
        if (m_documentValueRows is null) {
            m_documentValueRows = new HashSet<string>(comparer: StringComparer.Ordinal);

            WorldStateDocumentValues.CollectReferencedRows(
                definition: m_document.Definition,
                rows: m_documentValueRows
            );
        }

        return m_documentValueRows;
    }

    /// <summary>Composes what a publication would install, and installs nothing: the installed document carrying
    /// the arena's values as they stand, an open scope's writes included.</summary>
    /// <remarks>A proposal reads the rows whose version has moved since the document and the arena last agreed on
    /// them, and the rows an open scope has written, and keeps every other installed row as it is, so what it costs
    /// follows what was written and not what the document declares. Every row it reads is the row the arena was
    /// built over with its stored columns written back, so a row's own traits ride across. The document values that
    /// read a moved row are re-resolved in the proposal. <see cref="PublishArena"/> adopts exactly this, so what a
    /// preflight judges is what the commit installs.</remarks>
    /// <returns>The proposal.</returns>
    public WorldArenaPublication ProposePublication() {
        var before = m_document.Definition;
        var count = m_arena.Layout.RowCount;

        if (m_openRows.Length < count) {
            m_openRows = new bool[count];
        }

        var open = m_openRows.AsSpan(
            length: count,
            start: 0
        );

        open.Clear();

        if (m_arena.Journal.Length != 0) {
            m_arena.FlagOpenRows(rows: open);
        }

        var any = false;

        for (var ordinal = 0; (!any && (ordinal < m_documentRowCount)); ordinal++) {
            any = (
                open[ordinal] ||
                (m_publishedVersions[ordinal] != m_arena.RowVersion(rowOrdinal: ordinal))
            );
        }
        if (!any) {
            return new WorldArenaPublication(
                BodyScale: false,
                Definition: before,
                DriveGate: false,
                Moved: false,
                RefreshRefusal: null
            );
        }

        var installed = before.State;
        var scaleRow = before.Population.ScaleRow;
        var descriptors = m_arena.Catalog.Descriptors;
        var bodyScale = false;
        var driveGate = false;
        var refresh = false;
        var rows = new WorldStateRow[m_documentRowCount];

        // Document rows occupy the catalog's first ordinals in declaration order, which is the order the installed
        // document lists them in. A row is kept only where that holds by name; anything else is read from the arena.
        for (var ordinal = 0; (ordinal < m_documentRowCount); ordinal++) {
            var name = descriptors[ordinal].Name;
            var moved = (
                open[ordinal] ||
                (m_publishedVersions[ordinal] != m_arena.RowVersion(rowOrdinal: ordinal))
            );

            if (
                !moved &&
                !m_publishEveryRow &&
                (ordinal < installed.Count) &&
                string.Equals(
                    a: installed[ordinal].Name.Value,
                    b: name,
                    comparisonType: StringComparison.Ordinal
                )
            ) {
                rows[ordinal] = installed[ordinal];

                continue;
            }

            var published = ((WorldStateRow)m_arena.ToRow(rowOrdinal: ordinal));

            rows[ordinal] = published;

            if (!moved) {
                continue;
            }

            // Which of the consumers that keep their own copy of a state value read a row this publication moves.
            driveGate |= published.GatesDrive;
            bodyScale |= string.Equals(
                a: name,
                b: scaleRow,
                comparisonType: StringComparison.Ordinal
            );
            // A document value bound to a state row, a placement's spatial extent or a creation's scale, is
            // resolved against the row's value, so a moved row re-resolves the values reading it.
            refresh |= DocumentValueRows().Contains(item: name);
        }

        var definition = before.WithWorldState(rows: rows);
        string? refreshRefusal = null;

        if (refresh) {
            if (WorldStateDocumentValues.TryRehydrate(
                definition: definition,
                reason: out var refreshReason,
                refreshed: out var refreshed
            )) {
                definition = refreshed;
            } else {
                refreshRefusal = refreshReason;
            }
        }

        return new WorldArenaPublication(
            BodyScale: bodyScale,
            Definition: definition,
            DriveGate: driveGate,
            Moved: true,
            RefreshRefusal: refreshRefusal
        );
    }
    /// <summary>Publishes what the arena holds into the installed document: the one boundary a state value written
    /// in the arena crosses to reach everything outside it.</summary>
    /// <remarks>It installs what <see cref="ProposePublication"/> composes, then does what any installed state value
    /// owes: marks state delivery pending, and brings the consumers that keep their own copy of a value up to date
    /// (<see cref="WorldDocument.ReconcileStateConsumers"/>), which is the routine a value mutation ends in.</remarks>
    /// <param name="reconcile">Whether those consumers are brought up to date now. A publication taken in the
    /// middle of a firing leaves that owed, and the publication that ends the tick settles it whether or not
    /// anything moved in between.</param>
    /// <returns><see langword="true"/> when a row was published.</returns>
    public bool PublishArena(bool reconcile = true) => AdoptPublication(
        publication: ProposePublication(),
        reconcile: reconcile
    );
    /// <summary>Installs a proposal as the publication it describes.</summary>
    /// <param name="publication">A proposal taken from the arena as it stands now. One taken inside a scope that
    /// has since committed, with nothing written between, qualifies: the arena holds what it proposed.</param>
    /// <param name="reconcile">Whether the consumers that keep their own copy of a value are brought up to date
    /// now.</param>
    /// <returns><see langword="true"/> when a row was published.</returns>
    public bool AdoptPublication(WorldArenaPublication publication, bool reconcile = true) {
        if (!publication.Moved) {
            if (reconcile) {
                SettleStateConsumers();
            }

            return false;
        }

        var before = m_document.Definition;

        m_document.AdoptDefinition(definition: publication.Definition);

        if (
            (publication.RefreshRefusal is { } refreshReason) &&
            m_output.HasNarrationSink
        ) {
            m_output.Narrate(
                channel: "world.state",
                text: $"[world.state: a document value reading a row this tick wrote did not re-resolve — {refreshReason}]"
            );
        }

        MarkPublished();
        m_publishEveryRow = false;
        m_arenaCatalog = m_document.Definition.StateCatalog;
        // The installed document now carries values no sink has seen: a rule's own write reaches a client through the
        // same state delivery a console write does.
        m_document.MarkStateDeliveryPending();
        m_bodyScaleOwed |= publication.BodyScale;
        m_driveGateOwed |= publication.DriveGate;
        // The field section compiles from the state section and is republished only when what it reads changed, so
        // its reference is what says whether the lattice's input moved.
        m_fieldsOwed |= !ReferenceEquals(
            objA: before.Fields,
            objB: m_document.Definition.Fields
        );
        m_consumersOwed = true;

        if (reconcile) {
            SettleStateConsumers();
        }

        return true;
    }

    private void SettleStateConsumers() {
        if (!m_consumersOwed) {
            return;
        }

        var bodyScale = m_bodyScaleOwed;
        var driveGate = m_driveGateOwed;
        var fields = m_fieldsOwed;

        m_bodyScaleOwed = false;
        m_consumersOwed = false;
        m_driveGateOwed = false;
        m_fieldsOwed = false;
        m_document.ReconcileStateConsumers(
            bodyScale: bodyScale,
            definition: m_document.Definition,
            driveGate: driveGate,
            fields: fields
        );
    }
    // Every search job is one resolved plan and one judge over this server's own arena, admitted at install: a
    // judge whose rules the host cannot serve refuses the plan by name rather than at a tick.
    private void RebuildSearch(WorldDefinition definition) {
        if (!WorldSearchCompilation.TryPlanAll(
            definition: definition,
            judges: out var judgeRules,
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
                rules: judgeRules[index],
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

        ref readonly var layout = ref m_arena.Layout[rowOrdinal];
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

            var name = m_arena.Keys[key].Value;

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
