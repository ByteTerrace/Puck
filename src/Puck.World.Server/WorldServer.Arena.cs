using System.Diagnostics.CodeAnalysis;
using Puck.Abstractions.Counting;
using Puck.Maths;

namespace Puck.World.Server;

/// <summary>The server's columnar store: the arena is the authoritative state during a tick, and the installed
/// document is the arena's export at the end of it.</summary>
/// <remarks>Construction, document installation, and checkpoint restoration validate the complete state section.
/// Pool storage is admitted through its typed snapshot; ordinary rows use <see cref="StateArena.TryLoad"/>.</remarks>
public sealed partial class WorldServer {
    // The arena's and the search's counters as the World registers them: the instance behind each is replaced when a
    // definition install adopts a new arena, and the forwarder carries the retired one's totals so neither goes down.
    private readonly ForwardingWorkCounterSource m_arenaWork = new(
        kinds: ArenaWork.Kinds,
        name: ArenaWork.SourceName
    );
    private readonly ForwardingWorkCounterSource m_searchWork = new(
        kinds: SearchWorkKinds.Kinds,
        name: SearchWorkKinds.SourceName
    );
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
    /// <summary>Gets the <c>state.arena</c> counters over every arena this server has held: the current arena's counts
    /// on top of the totals of each one a definition install retired, so a reading never goes down.</summary>
    public IWorkCounterSource ArenaWorkSource => m_arenaWork;
    /// <summary>Gets the <c>state.search</c> counters over every search this server has held, carried forward across
    /// each arena replacement the same way as <see cref="ArenaWorkSource"/>.</summary>
    public IWorkCounterSource SearchWorkSource => m_searchWork;

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

        // Construction normally has no earlier arena. Keeping the copy here makes an explicitly rebuilt host carry
        // its session lanes through the same routine as a prepared replacement.
        m_arena?.CopyLanesTo(target: built);
        m_arena = built;
        m_arenaWork.Retarget(target: built);
        AdoptLayout(definition: definition);
    }
    // Settles against a seed taken at this host's current tick pair. Construction-time and load-time callers settle
    // before their one admission instead, so the document the receipt names is the document that installs.
    private WorldDefinition SettleInstalledRows(WorldDefinition definition) {
        var time = m_ruleHost.Time;

        return WorldStateSettlement.Settle(
            definition: definition,
            time: in time
        );
    }

    // Builds the exact arena a prepared document install will adopt. The live arena remains untouched until the
    // document's commit, so a capacity refusal cannot leave its catalog, rows, lanes, or retained keys half moved.
    internal bool TryPrepareArenaReplacement(WorldDefinition definition, out StateArena prepared, out string reason, out WorldDefinition settled) {
        var time = m_ruleHost.Time;

        if (!StateArena.TryCreate(
            arena: out var built,
            catalog: definition.StateCatalog,
            options: WorldSlotLanes.Options(definition: definition),
            reason: out reason,
            section: definition.StateRaw,
            time: in time
        )) {
            prepared = null!;
            settled = definition;
            return false;
        }

        prepared = built;

        settled = WorldStateSettlement.SettleFrom(
            definition: definition,
            seeded: prepared
        );
        if (!prepared.TryRestoreKeys(
            names: m_arena.Keys.Names,
            reason: out reason
        )) {
            prepared = null!;
            return false;
        }

        m_arena.CopyLanesTo(target: prepared);
        reason = string.Empty;
        return true;
    }
    internal void AdoptPreparedArena(StateArena arena, WorldDefinition definition) {
        m_arena = arena;
        m_arenaWork.Retarget(target: arena);
        AdoptSearch(arena: arena);
        AdoptLayout(definition: definition);
        m_ruleHost.InvalidateArenaScheduling();
    }

    // Builds the search over an arena, makes it the one the server steps, and points the search counters at it.
    [MemberNotNull(member: nameof(m_search))]
    private void AdoptSearch(StateArena arena) {
        m_search = new ArenaSearch(
            arena: arena,
            narrate: (channel, text) => {
                if (m_output.HasNarrationSink) {
                    m_output.Narrate(channel: channel, text: text);
                }
            }
        );
        m_searchWork.Retarget(target: m_search);
    }
    // Rebinds everything addressed by catalog ordinal after the arena's layout moved: the draw sites the seed
    // ladder folds, the per-row version marks, the bodies' action-state slot lanes, and the host that carries them.
    private void AdoptLayout(WorldDefinition definition) {
        m_arenaCatalog = definition.StateCatalog;
        m_drawSites = WorldDrawSites.Of(catalog: m_arenaCatalog);
        m_documentRowCount = m_arenaCatalog.Lane(lane: StateLane.Document).Count;
        m_publishedVersions = new ulong[m_arena.Layout.RowCount];
        MarkPublished();
        m_publishEveryRow = true;
        m_movedEverything = true;
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
    // columns and reloads their values; a reconstruction-time re-declaration relayouts atomically and carries the
    // runtime key ledger plus the participant and identity lanes. Relayout refusal leaves the arena alone; a load
    // refusal after a successful relayout throws from the reconstruction path rather than dropping the ledger via
    // a fresh-build fallback.
    public void SyncArena(WorldDefinition definition) {
        var time = m_ruleHost.Time;

        // Generated pool rows cannot pass through ordinary row import: doing so would bypass the relationship
        // between live slots, field domains, and lifetime generations. Reconstruct the whole section instead.
        if ((m_arena.Catalog.Pools.Count != 0) || (definition.StateCatalog.Pools.Count != 0)) {
            if (!TryPrepareArenaReplacement(definition: definition, prepared: out var replacement,
                reason: out var replacementReason, settled: out var settled)) {
                throw new InvalidOperationException(message: $"the installed state section does not load into the arena: {replacementReason}");
            }

            AdoptPreparedArena(arena: replacement, definition: settled);
            return;
        }

        if (ReferenceEquals(
            objA: m_arenaCatalog,
            objB: definition.StateCatalog
        )) {
            if (!m_arena.TryLoad(
                reason: out var reason,
                rows: definition.State,
                time: in time
            )) {
                throw new InvalidOperationException(message: $"the installed document does not load into the arena: {reason}");
            }
            if (reason.Length != 0) {
                throw new InvalidOperationException(message: $"the installed document does not load into the arena: {reason}");
            }

            MarkPublished();
            m_publishEveryRow = true;

            return;
        }
        if (!m_arena.TryRelayout(
            catalog: definition.StateCatalog,
            reason: out var relayoutReason,
            section: definition.StateRaw,
            time: in time
        )) {
            throw new InvalidOperationException(message: $"the installed document does not relayout into the arena: {relayoutReason}");
        }
        if (!m_arena.TryLoad(
            reason: out var loaded,
            rows: definition.State,
            time: in time
        )) {
            throw new InvalidOperationException(message: $"the relaid-out document does not load into the arena: {loaded}");
        }
        if (loaded.Length != 0) {
            throw new InvalidOperationException(message: $"the relaid-out document only partially loads into the arena: {loaded}");
        }

        AdoptLayout(definition: definition);
    }

    // The document and the arena agree on every row as they stand.
    private void MarkPublished() {
        var rows = m_arena.Layout.RowCount;

        if (m_publishedVersions.Length < rows) {
            m_publishedVersions = new ulong[rows];
        }
        for (var ordinal = 0; (ordinal < rows); ordinal++) {
            var version = m_arena.RowVersion(rowOrdinal: ordinal);

            if (m_publishedVersions[ordinal] != version) {
                NoteMovedRow(
                    ordinal: ordinal,
                    rowCount: rows
                );
            }

            m_publishedVersions[ordinal] = version;
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

        var installed = before.AuthoredState;
        var scaleRow = before.Population.ScaleRow;
        var descriptors = m_arena.Catalog.Descriptors;
        var bodyScale = false;
        var driveGate = false;
        var refresh = false;
        var rows = new WorldStateRow[installed.Count];

        // Document rows occupy the catalog's first ordinals in declaration order, which is the order the installed
        // document lists them in. A row is kept only where that holds by name; anything else is read from the arena.
        for (var ordinal = 0; (ordinal < rows.Length); ordinal++) {
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

        var definition = before.WithWorldState(rows: rows, pools: ((before.StateRaw?.Pools is { Count: > 0 }) ? m_arena.ToPools() : null), pairPools: ((before.StateRaw?.PairPools is { Count: > 0 }) ? m_arena.ToPairPools() : null));
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
            compilation: m_ruleCompilation!,
            judges: out var judgeRules,
            plans: out var authored,
            reason: out var planReason,
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
            sites: m_drawSites,
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
        var cursor = 0;

        while (m_arena.TryNextCell(
            cursor: ref cursor,
            key: out var key,
            rowOrdinal: rowOrdinal
        )) {
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

    public WorldDefinition RecompileRules(WorldDefinition definition, WorldRuleCompilation? compilation = null, StateArena? arena = null) {
        if (arena is null) {
            definition = SettleInstalledRows(definition: definition);
        }
        m_document.AdoptDefinition(definition: definition);
        // The rows a document value reads are a function of the sections this install may have replaced.
        m_documentValueRows = null;
        // Recomposition may change dependencies: only the exact definition can reuse its validation result.
        if (!ReferenceEquals(
            objA: compilation?.Definition,
            objB: definition
        )) { compilation = WorldRuleCompilation.Compile(definition: definition); }
        m_ruleHost.Install(compilation: compilation!);
        m_ruleCompilation = compilation;
        if (arena is null) {
            SyncArena(definition: definition);
        } else {
            AdoptPreparedArena(arena: arena, definition: definition);
        }
        m_ruleHost.ConfigureUndo(plans: compilation!.Groups.Where(predicate: group => (group.Undo is not null)).Select(selector: group => group.Undo!).ToArray());
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
