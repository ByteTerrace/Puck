using System.Globalization;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>
/// The <see cref="IWorldStateView"/> a client implements over the definition it was last delivered, until the
/// presentation view replaces the delivered definition. It reads a row by its catalog ordinal, which indexes the
/// document lane of <see cref="WorldDefinition.State"/> directly, and one cell of it through the engine's own
/// <see cref="StateReader"/> computations, so a bound read and a rule read never disagree about a value.
/// <para>
/// A field row (a lattice row whose domain names one of <see cref="WorldDefinition.FieldRegions"/>) is read otherwise:
/// its live cells are simulation state the snapshot carries (<see cref="WorldSnapshot.FieldCells"/>), never document
/// cells, so the view keeps the cells its owner hands it through <see cref="ApplyFieldCells"/> and reads the row's cell
/// <c>i</c> as lattice cell <c>i</c> (z, then layer, then x), a Fixed value. Those cells are a field's one truth on
/// the client, and every presentation consumer, a field's brick and a pass's array alike, reads them through the state
/// mirror as it reads any other row.
/// </para>
/// <para>
/// A sink that receives documents from an authority builds its view with the delivering constructor, and the view is
/// then the one door a delivered document passes to become the sink's (<see cref="Adopt"/>): every state-bound value in
/// it is bound again to the arena this view's deliveries fill, so nothing the sink holds reads the authority's store or
/// its clock, whichever thread the authority's tick runs on.
/// </para>
/// </summary>
public sealed class WorldDocumentStateView : IWorldStateView {
    private StateArena m_arena = null!;

    private readonly Func<WorldDefinition> m_definition;
    // The store a delivered document's values are bound to, and the document Adopt last bound to it; both null for a
    // view over a document its caller owns.
    private readonly WorldLiveValues? m_values;
    private WorldDefinition? m_delivered;

    /// <summary>Initializes a new instance of the <see cref="WorldDocumentStateView"/> class over a document its caller
    /// owns.</summary>
    /// <param name="definition">Returns the document the view reads; read on every call.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public WorldDocumentStateView(Func<WorldDefinition> definition) {
        m_definition = (definition ?? throw new ArgumentNullException(paramName: nameof(definition)));
    }
    /// <summary>Initializes a new instance of the <see cref="WorldDocumentStateView"/> class as a sink's door for the
    /// documents its authority delivers, adopting <paramref name="delivered"/> as the first.</summary>
    /// <param name="delivered">The first delivered document.</param>
    /// <param name="clock">Reads the tick pair of the latest delivered snapshot, which a bound value with a
    /// value-over-time trait is read at.</param>
    /// <param name="gate">The lock the sink holds while a delivery writes this view, which a bound value's read takes
    /// too; <see langword="null"/> for a sink whose deliveries arrive on the thread that reads it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="delivered"/> or <paramref name="clock"/> is
    /// <see langword="null"/>.</exception>
    public WorldDocumentStateView(WorldDefinition delivered, Func<(ulong Tick, ulong EngineTick)> clock, Lock? gate = null) {
        ArgumentNullException.ThrowIfNull(argument: delivered);
        ArgumentNullException.ThrowIfNull(argument: clock);

        m_definition = () => m_delivered!;
        m_values = new WorldLiveValues(
            clock: () => {
                var (tick, engineTick) = clock();
                var document = m_delivered!;

                return new ArenaTime(Tick: tick, EngineTick: engineTick, TicksPerSecond: document.SimulationRateHz, Dynamics: document.Dynamics);
            },
            gate: gate
        );
        _ = Adopt(delivered: delivered);
    }

    /// <summary>Gets the document the view reads: for a sink's door, the document <see cref="Adopt"/> last
    /// returned.</summary>
    public WorldDefinition Definition => m_definition();

    /// <summary>Adopts a delivered document as the sink's own: shapes the view for it and returns it with every
    /// state-bound value bound to the arena this view's deliveries fill, read at the sink's delivered tick. Every
    /// document a sink holds comes from here, so none of its values reads the authority's arena or clock.</summary>
    /// <remarks>A row or holder delivered unchanged comes back as the copy handed back before, so a consumer comparing
    /// documents by reference sees only what the delivery changed.</remarks>
    /// <param name="delivered">The document the authority delivered, which may hold values bound to its own store.</param>
    /// <param name="valuesFollow">Whether the delivery changed values alone and the snapshot that follows carries the
    /// cells that moved, so the view keeps the cells it holds while the state catalog is unchanged.</param>
    /// <returns>The sink's own document, which <see cref="Definition"/> answers until the next adoption.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="delivered"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The view is over a document its caller owns, or a value in
    /// <paramref name="delivered"/> names a row its own state section does not declare.</exception>
    public WorldDefinition Adopt(WorldDefinition delivered, bool valuesFollow = false) {
        ArgumentNullException.ThrowIfNull(argument: delivered);

        var values = (m_values ?? throw new InvalidOperationException(message: "Only a view built as a sink's door adopts delivered documents."));

        // The document adopted last, delivered again, is already shaped for and bound.
        if (values.TryProjection(definition: delivered, projection: out var earlier) && ReferenceEquals(objA: earlier, objB: m_delivered)) {
            return earlier;
        }
        if (!valuesFollow || (m_shaped is not { } shaped) || !ReferenceEquals(objA: shaped.StateCatalog, objB: delivered.StateCatalog)) {
            _ = Shape(document: delivered);
        }

        values.Arena = m_arena;

        var adopted = WorldStateDocumentValues.BindLive(definition: delivered, live: values, shareUnchanged: true);

        // The projection shares the delivered state section and catalog, so the shape just taken stands for it.
        m_shaped = adopted;
        m_delivered = adopted;

        return adopted;
    }
    // The delivered field cells, one raw Q48.16 array per declared field over the lattice they were delivered on; the
    // field each document ordinal names (-1 for a row that is no field) and the ordinal each field's row holds, both
    // resolved against the definition last shaped for.
    private long[][] m_cells = [];
    private int[] m_fieldOfOrdinal = [];
    private readonly Dictionary<(string Region, string Field), int> m_fieldAddresses = [];
    private WorldFieldLatticeDefinition[] m_latticeOfField = [];
    private int[] m_ordinalOfField = [];
    // The delivered action-state register file of every entity index (raw Q48.16 presented values, the slots of one
    // body contiguous), the catalog ordinal each slot's lane row holds, and which slots a delivery moved since the
    // owner last took them, all shaped for the definition last shaped for.
    private long[] m_bodyState = [];
    // Whether a delivery has applied each body's register file. A body the sink knows of before its first snapshot
    // entry (a seeded route) reads no value, never the zeros its registers start at.
    private bool[] m_bodyStateApplied = [];
    private bool[] m_bodyStateMoved = [];
    private int[] m_bodyStateOrdinals = [];

    private WorldDefinition? m_shaped;
    private readonly Dictionary<int, WorldObservedRow> m_observations = [];

    /// <summary>The last delivered observable row metadata, including a hidden count or anonymous backs.</summary>
    public WorldObservedRow? Observation(int ordinal) => m_observations.GetValueOrDefault(ordinal);

    /// <inheritdoc/>
    public WorldPresentationManifest Manifest => WorldPresentationManifest.Of(definition: m_definition());
    /// <summary>Gets the most body-state rows one <see cref="TakeMovedBodyState"/> names.</summary>
    public static int MaxMovedBodyState => StateCapacity.MaxBodySlots;

    // The register-file slot a catalog ordinal's body-state row is, or -1 for any other ordinal.
    private int BodyStateSlotOf(int ordinal) => ((ordinal < 0)
        ? -1
        : Array.IndexOf(
            array: m_bodyStateOrdinals,
            value: ordinal
        )
    );

    /// <inheritdoc/>
    /// <remarks>A name spelling <see cref="WorldBodyStateReads.RowPrefix"/> and a declared action-state slot resolves
    /// to that slot's lane row, read by body index from the values the snapshots carry
    /// (<see cref="ApplyBodyState"/>).</remarks>
    public bool TryResolveRow(string rowName, out int ordinal) {
        ArgumentNullException.ThrowIfNull(argument: rowName);

        var document = Shape();

        if (rowName.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldBodyStateReads.RowPrefix
        )) {
            ordinal = (WorldBodyStateReads.TryResolveSlot(
                definition: document,
                kind: out _,
                name: rowName[WorldBodyStateReads.RowPrefix.Length..],
                slot: out var slot
            )
                ? m_bodyStateOrdinals[slot]
                : -1
            );

            return (ordinal >= 0);
        }

        if (
            document.StateCatalog.TryResolve(
            handle: out var handle,
            lane: StateLane.Document,
            name: rowName
        ) &&
            (handle.Ordinal < document.State.Count)
        ) {
            ordinal = handle.Ordinal;

            return true;
        }

        ordinal = -1;

        return false;
    }
    /// <inheritdoc/>
    public int RowLength(int ordinal) {
        var document = m_definition();
        var rows = document.State;

        return ((
            (((uint)ordinal) < ((uint)rows.Count)) &&
            WorldBoundRow.TryResolve(
                definition: document,
                length: out var length,
                row: out _,
                rowName: rows[ordinal].Name.Value
            )
        )
            ? length
            : 0);
    }
    /// <inheritdoc/>
    public bool TryRead(int ordinal, string? key, bool target, ulong tick, ulong engineTick, out WorldStateSample sample) {
        var document = Shape();
        var rows = document.State;

        if (BodyStateSlotOf(ordinal: ordinal) is var stateSlot and >= 0) {
            var slots = m_bodyStateOrdinals.Length;
            var stateCell = ((int.TryParse(
                provider: CultureInfo.InvariantCulture,
                result: out var stateBody,
                s: key,
                style: NumberStyles.None
            ) && (((uint)stateBody) < ((uint)(m_bodyState.Length / slots))))
                ? ((stateBody * slots) + stateSlot)
                : -1
            );

            sample = new WorldStateSample(
                Max: null,
                Min: null,
                Motion: WorldStateMotion.Still,
                Value: (((stateCell >= 0) && m_bodyStateApplied[stateBody])
                    ? CellValue.Fixed(rawBits: m_bodyState[stateCell])
                    : default)
            );

            return true;
        }
        if (((uint)ordinal) >= ((uint)rows.Count)) {
            sample = default;

            return false;
        }

        var row = rows[ordinal];

        if (m_fieldOfOrdinal[ordinal] is var field and >= 0) {
            var cells = m_cells[field];

            sample = new WorldStateSample(
                Max: row.Max,
                Min: row.Min,
                Motion: WorldStateMotion.Still,
                Value: ((int.TryParse(
                    provider: CultureInfo.InvariantCulture,
                    result: out var index,
                    s: key,
                    style: NumberStyles.None
                ) && (((uint)index) < ((uint)cells.Length)))
                    ? CellValue.Fixed(rawBits: cells[index])
                    : default)
            );

            return true;
        }

        var time = new ArenaTime(Tick: tick, EngineTick: engineTick, TicksPerSecond: document.SimulationRateHz, Dynamics: document.Dynamics);
        var value = default(CellValue);
        var motion = WorldStateMotion.Still;

        if (CellName.TryParse(candidate: (key ?? StateRow.SlotKey.Value), name: out var name, reason: out _) &&
            m_arena.Keys.TryResolve(key: out var cell, name: name)) {
            if (target) { m_arena.TryReadLive(key: cell, rowOrdinal: ordinal, time: in time, value: out value); } else { m_arena.TryReadEased(key: cell, rowOrdinal: ordinal, time: in time, value: out value); }
            var behavior = m_arena.LiveBehavior(key: cell, rowOrdinal: ordinal);

            motion = behavior switch {
                { Advance.PerSecondNumerator: not 0L } => WorldStateMotion.Advancing,
                { Cycle: not null } => WorldStateMotion.Stepping,
                _ => WorldStateMotion.Still,
            };
            if (!target && m_arena.TryReadDynamics(key: cell, rowOrdinal: ordinal, sample: out var eased, time: in time) &&
                m_arena.TryRead(key: cell, rowOrdinal: ordinal, value: out var stored) &&
                ((eased.Velocity != FixedQ4816.Zero) || (eased.Value != StateReader.DynamicsRowRawToFixed(row: row, raw: stored.Raw)))) {
                motion = WorldStateMotion.Easing;
            }
        }
        sample = new WorldStateSample(Value: value, Min: row.Min, Max: row.Max, Motion: motion);
        return true;
    }
    /// <inheritdoc/>
    public bool ReadRow(int ordinal, bool target, ulong tick, ulong engineTick, Span<double> elements, out WorldStateMotion motion) {
        var document = Shape();
        var changed = false;

        motion = WorldStateMotion.Still;

        // A field row's cells are read by index, the lattice cell each element is.
        if (
            (((uint)ordinal) < ((uint)document.State.Count)) &&
            (m_fieldOfOrdinal[ordinal] is var field and >= 0)
        ) {
            var cells = m_cells[field];

            for (var index = 0; (index < elements.Length); index++) {
                var number = ((index < cells.Length)
                    ? ((double)FixedQ4816.FromRawBits(value: cells[index]))
                    : 0d
                );

                if (elements[index] != number) {
                    elements[index] = number;
                    changed = true;
                }
            }

            return changed;
        }

        // Any other keyed row holds at most TopologyCompilation.MaxCells cells, each under a cached decimal key.
        for (var index = 0; (index < elements.Length); index++) {
            var number = 0d;

            if (TryRead(
                engineTick: engineTick,
                key: IndexKeyCache.Get(index: index),
                ordinal: ordinal,
                sample: out var sample,
                target: target,
                tick: tick
            )) {
                _ = WorldStateReader.TryNumber(
                    number: out number,
                    value: sample.Value
                );

                if (sample.Motion != WorldStateMotion.Still) {
                    motion = sample.Motion;
                }
            }
            if (elements[index] != number) {
                elements[index] = number;
                changed = true;
            }
        }

        return changed;
    }
    /// <summary>Applies one snapshot's cell writes (<see cref="WorldSnapshot.FieldCells"/>): a field write to the cells
    /// a field row is read from, and a document row's cells (a write naming a row) to the arena the view reads every
    /// other row from, clocks included. A row replacement rewrites only the cells it changes. It names each row a write moved, so the owner refreshes the mirror slots bound
    /// to it (<see cref="WorldStateMirror.RefreshRows"/>) or notes them for its next refresh. A write naming a field or
    /// a cell the delivered lattice does not hold is ignored. It allocates nothing unless the delivered lattice changed
    /// shape, which starts every field's cells at zero until the next full snapshot writes them.</summary>
    /// <param name="deltas">The snapshot's cell writes.</param>
    /// <param name="moved">Receives the catalog ordinal of each row a write changed, each once; at least
    /// <see cref="StateCapacity.MaxRows"/> long.</param>
    /// <returns>How many ordinals <paramref name="moved"/> received.</returns>
    public int ApplyFieldCells(ReadOnlySpan<FieldCellDelta> deltas, Span<int> moved) {
        if (deltas.IsEmpty) {
            return 0;
        }

        _ = Shape();

        Span<bool> changed = stackalloc bool[StateCapacity.MaxRows];
        Span<bool> valueChanged = stackalloc bool[StateCapacity.MaxRows];
        var count = 0;

        for (var index = 0; (index < deltas.Length); index++) {
            var delta = deltas[index];

            if (delta.Row >= 0) {
                if (((uint)delta.Row) >= ((uint)m_arena.Rows.Count)) { continue; }
                var metadataMoved = delta.Cell == -1 && delta.Observation != m_observations.GetValueOrDefault(delta.Row);
                if (delta.Cell == -1) {
                    if (delta.Observation is { } observation) { m_observations[delta.Row] = observation; }
                    else { m_observations.Remove(delta.Row); }
                }
                if (delta.Cell >= 0) {
                    m_arena.ImportProjectionCell(rowOrdinal: delta.Row, position: delta.Cell, image: delta.Image);
                } else {
                    var replaced = 0;

                    while (((index + 1 + replaced) < deltas.Length) && (deltas[(index + 1 + replaced)].Row == delta.Row) && (deltas[(index + 1 + replaced)].Cell >= 0)) { replaced++; }
                    if (!ReplaceRow(header: delta, cells: deltas.Slice(start: (index + 1), length: replaced)) && !metadataMoved) {
                        index += replaced;
                        continue;
                    }
                    index += replaced;
                }
                if (!valueChanged[delta.Row]) { moved[count++] = delta.Row; valueChanged[delta.Row] = true; }
                continue;
            }
            // Recipient disclosure can remove preceding fields; the authority's local ordinal then differs from
            // this document's. The named address remains stable across that projection.
            if (!m_fieldAddresses.TryGetValue(key: (delta.Region, delta.FieldName), value: out var field)) {
                continue;
            }

            var cells = m_cells[field];

            if (
                (((uint)delta.Cell) >= ((uint)cells.Length)) ||
                (cells[delta.Cell] == delta.Raw)
            ) {
                continue;
            }

            cells[delta.Cell] = delta.Raw;
            changed[field] = true;
        }

        for (var field = 0; (field < m_cells.Length); field++) {
            if (
                changed[field] &&
                (m_ordinalOfField[field] >= 0) &&
                !valueChanged[m_ordinalOfField[field]]
            ) {
                moved[count++] = m_ordinalOfField[field];
            }
        }

        return count;
    }
    // Applies one row replacement: in place, position by position, when the row already holds the replacement's
    // keys in order, so an unchanged cell costs a comparison; otherwise by clearing the row and importing every cell.
    // Returns whether the row's stored cells changed.
    private bool ReplaceRow(in FieldCellDelta header, ReadOnlySpan<FieldCellDelta> cells) {
        var count = checked((int)header.Raw);

        if ((cells.Length == count) && m_arena.HoldsProjectionShape(rowOrdinal: header.Row, count: count)) {
            var changed = false;
            var position = 0;

            while ((position < count) && (cells[position].Cell == position) &&
                m_arena.TryRefreshProjectionCell(rowOrdinal: header.Row, position: position, image: cells[position].Image, moved: out var cellMoved)) {
                changed |= cellMoved;
                position++;
            }
            if (position == count) {
                return changed;
            }
        }
        m_arena.BeginProjectionRow(rowOrdinal: header.Row, count: count);
        foreach (var cell in cells) {
            m_arena.ImportProjectionCell(rowOrdinal: header.Row, position: cell.Cell, image: cell.Image);
        }
        return true;
    }
    /// <summary>Applies one snapshot entry's action-state register file (<see cref="EntitySnapshot.State"/>) to the
    /// values its body's <see cref="WorldBodyStateReads.RowPrefix"/> rows read, noting each slot a value moved until
    /// <see cref="TakeMovedBodyState"/>. A body outside the delivered population, or a register file whose width is not
    /// the delivered definition's, is ignored. It allocates nothing unless the delivered definition changed the
    /// population or the register file, after which every body reads no value until the next snapshot writes it.</summary>
    /// <param name="body">The 0-based entity index.</param>
    /// <param name="values">The entry's presented slot values.</param>
    public void ApplyBodyState(int body, ReadOnlySpan<long> values) {
        _ = Shape();

        var slots = m_bodyStateOrdinals.Length;

        if (
            (values.Length != slots) ||
            (slots == 0) ||
            (((uint)body) >= ((uint)(m_bodyState.Length / slots)))
        ) {
            return;
        }

        var stored = m_bodyState.AsSpan(
            length: slots,
            start: (body * slots)
        );
        // A body's first register file moves every row it reads: each goes from no value to one.
        var first = !m_bodyStateApplied[body];

        m_bodyStateApplied[body] = true;
        for (var slot = 0; (slot < slots); slot++) {
            if (first || (stored[slot] != values[slot])) {
                stored[slot] = values[slot];
                m_bodyStateMoved[slot] = true;
            }
        }
    }
    /// <summary>Forgets a body's register file, for a slot a new occupant takes before any snapshot entry has described
    /// it: its rows read no value until <see cref="ApplyBodyState"/> writes them, never the occupant before's.</summary>
    /// <param name="body">The 0-based entity index; one outside the delivered population is ignored.</param>
    public void ForgetBodyState(int body) {
        _ = Shape();

        if ((((uint)body) >= ((uint)m_bodyStateApplied.Length)) || !m_bodyStateApplied[body]) {
            return;
        }

        m_bodyStateApplied[body] = false;
        Array.Fill(array: m_bodyStateMoved, value: true);
    }
    /// <summary>Names each body-state row a value moved since the last call, so the owner refreshes the mirror slots
    /// bound to it (<see cref="WorldStateMirror.RefreshRows"/>), and forgets them.</summary>
    /// <param name="moved">Receives the catalog ordinal of each moved row; at least <see cref="MaxMovedBodyState"/>
    /// long.</param>
    /// <returns>How many ordinals <paramref name="moved"/> received.</returns>
    public int TakeMovedBodyState(Span<int> moved) {
        var count = 0;

        for (var slot = 0; (slot < m_bodyStateMoved.Length); slot++) {
            if (m_bodyStateMoved[slot]) {
                m_bodyStateMoved[slot] = false;
                moved[count++] = m_bodyStateOrdinals[slot];
            }
        }

        return count;
    }

    // Resolves the action-state rows of the definition currently delivered, keeping the delivered values while the
    // population and the register file keep their widths.
    private void ShapeBodyState(WorldDefinition document) {
        var bodyState = document.BodyState;
        var identityState = document.IdentityState;
        var slots = (bodyState.Count + identityState.Count);
        var width = (Math.Max(
            val1: 1,
            val2: document.Population.Capacity
        ) * slots);

        if (
            (m_bodyStateOrdinals.Length != slots) ||
            (m_bodyState.Length != width)
        ) {
            m_bodyState = new long[width];
            m_bodyStateApplied = new bool[((slots == 0) ? 0 : (width / slots))];
            m_bodyStateMoved = new bool[slots];
            m_bodyStateOrdinals = new int[slots];
        }

        for (var slot = 0; (slot < slots); slot++) {
            var body = (slot < bodyState.Count);

            m_bodyStateOrdinals[slot] = (document.StateCatalog.TryResolve(
                handle: out var handle,
                lane: (body
                    ? StateLane.Participant
                    : StateLane.Identity),
                name: (body
                    ? bodyState[slot].Name
                    : identityState[(slot - bodyState.Count)].Name)
            )
                ? handle.Ordinal
                : -1
            );
        }
    }
    private readonly List<StateRow> m_reloadedRows = [];

    // Reloads into the kept arena the rows of after (its pools' generated rows included) that are not the instances
    // before holds at the same position, when after's catalog has before's shape and the two sections share every
    // declaration list; false, leaving the arena as it was, when they do not or the arena refuses the rows. A reloaded
    // row's observation header is dropped, as a build drops every one: the authority owes it again with the row's next
    // snapshot.
    private bool TryReloadRows(WorldDefinition before, WorldDefinition after) {
        if ((before.StateRaw is not { } heldSection) || (after.StateRaw is not { } nextSection)) { return false; }
        if (!heldSection.SharesDeclarations(other: nextSection)) {
            WorldBootWork.Count(kind: WorldBootWork.ArenaReloadsRefusedDeclarations);
            return false;
        }
        if (!ReferenceEquals(objA: before.StateCatalog, objB: after.StateCatalog) && !before.StateCatalog.MatchesShape(section: nextSection)) {
            WorldBootWork.Count(kind: WorldBootWork.ArenaReloadsRefusedCatalog);
            return false;
        }

        var held = before.State;
        var next = after.State;

        if (held.Count != next.Count) {
            WorldBootWork.Count(kind: WorldBootWork.ArenaReloadsRefusedRows);
            return false;
        }

        var rows = m_reloadedRows;

        rows.Clear();
        for (var index = 0; (index < next.Count); index++) {
            if (!ReferenceEquals(objA: held[index], objB: next[index])) { rows.Add(item: next[index]); }
        }
        if ((rows.Count != 0) && !m_arena.TryLoad(rows: rows, time: ArenaTime.Origin, reason: out _)) {
            WorldBootWork.Count(kind: WorldBootWork.ArenaReloadsRefusedLoad);
            rows.Clear();
            return false;
        }
        for (var index = 0; (index < next.Count); index++) {
            if (!ReferenceEquals(objA: held[index], objB: next[index])) { _ = m_observations.Remove(key: index); }
        }
        WorldBootWork.Count(amount: rows.Count, kind: WorldBootWork.ArenaRowReloads);
        rows.Clear();

        return true;
    }
    // Resolves the field rows of the definition currently delivered, keeping the delivered cells while the lattice
    // keeps its shape; a definition already shaped for costs one comparison.
    private WorldDefinition Shape() => Shape(document: m_definition());
    private WorldDefinition Shape(WorldDefinition document) {
        if (ReferenceEquals(
            objA: document,
            objB: m_shaped
        )) {
            return document;
        }

        // A definition carrying the same state section and catalog over (a response swap moves only placements) keeps the
        // arena and the values delivered into it: building one again from the section costs a whole state load.
        // A section of the same shape whose rows are the instances the arena last loaded except for some (a replica
        // document disclosed again after its frame moved) reloads only those rows, as the authority's own store does
        // (WorldServer.SyncArenaValues).
        if (
            (m_shaped is not { } shaped) ||
            ((!ReferenceEquals(objA: document.StateRaw, objB: shaped.StateRaw) || !ReferenceEquals(objA: document.StateCatalog, objB: shaped.StateCatalog)) &&
                !TryReloadRows(before: shaped, after: document))
        ) {
            WorldBootWork.Count(kind: WorldBootWork.ArenaBuilds);
            WorldBootWork.Count(kind: WorldBootWork.ArenaViewBuilds);
            m_arena = new StateArena(catalog: document.StateCatalog, section: document.StateRaw, time: ArenaTime.Origin);
            m_observations.Clear();
        }
        m_shaped = document;
        ShapeBodyState(document: document);

        var regions = document.FieldRegions;
        var fieldCount = regions.Sum(selector: static region => region.Fields.Count);
        var cells = new long[fieldCount][];
        var latticeOfField = new WorldFieldLatticeDefinition[fieldCount];
        var addresses = new Dictionary<(string Region, string Field), int>();
        var rows = document.State;

        m_fieldOfOrdinal = new int[rows.Count];
        m_ordinalOfField = new int[fieldCount];
        Array.Fill(array: m_fieldOfOrdinal, value: -1);
        Array.Fill(array: m_ordinalOfField, value: -1);

        var field = 0;

        foreach (var region in regions) {
            var cellCount = checked(((region.Lattice.Width * region.Lattice.Layers) * region.Lattice.Depth));

            for (var local = 0; (local < region.Fields.Count); local++, field++) {
                var name = region.Fields[local].Name;

                latticeOfField[field] = region.Lattice;
                // An unrelated region changing shape never discards this region's delivered cells. A sender decides
                // whether a replacement keeps them all by the same rule (WorldFieldsSection.KeepsCells).
                cells[field] = ((m_fieldAddresses.TryGetValue(key: (region.Name, name), value: out var previous) &&
                    (m_latticeOfField[previous] == region.Lattice))
                    ? m_cells[previous]
                    : new long[cellCount]);
                addresses.Add(key: (region.Name, name), value: field);
                if (document.StateCatalog.TryResolve(handle: out var handle, lane: StateLane.Document, name: name) &&
                    (handle.Ordinal < rows.Count)) {
                    m_ordinalOfField[field] = handle.Ordinal;
                    m_fieldOfOrdinal[handle.Ordinal] = field;
                }
            }
        }
        m_cells = cells;
        m_latticeOfField = latticeOfField;
        m_fieldAddresses.Clear();
        foreach (var address in addresses) { m_fieldAddresses.Add(key: address.Key, value: address.Value); }

        return document;
    }
}
