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
/// A field row (a lattice row whose trait declares it one of <see cref="WorldDefinition.Fields"/>) is read otherwise:
/// its live cells are simulation state the snapshot carries (<see cref="WorldSnapshot.FieldCells"/>), never document
/// cells, so the view keeps the cells its owner hands it through <see cref="ApplyFieldCells"/> and reads the row's cell
/// <c>i</c> as lattice cell <c>i</c> (z, then layer, then x), a Fixed value. Those cells are a field's one truth on
/// the client, and every presentation consumer, a field's brick and a pass's array alike, reads them through the state
/// mirror as it reads any other row.
/// </para>
/// </summary>
/// <param name="definition">Returns the definition currently delivered; read on every call.</param>
public sealed class WorldDocumentStateView(Func<WorldDefinition> definition) : IWorldStateView {
    private readonly Func<WorldDefinition> m_definition = (definition ?? throw new ArgumentNullException(paramName: nameof(definition)));
    // The delivered field cells, one raw Q48.16 array per declared field over the lattice they were delivered on; the
    // field each document ordinal names (-1 for a row that is no field) and the ordinal each field's row holds, both
    // resolved against the definition last shaped for.
    private long[][] m_cells = [];
    private int[] m_fieldOfOrdinal = [];

    private WorldFieldLatticeDefinition? m_lattice;

    private int[] m_ordinalOfField = [];

    private WorldDefinition? m_shaped;

    /// <inheritdoc/>
    public WorldPresentationManifest Manifest => WorldPresentationManifest.Of(definition: m_definition());

    /// <inheritdoc/>
    public bool TryResolveRow(string rowName, out int ordinal) {
        ArgumentNullException.ThrowIfNull(argument: rowName);

        var document = m_definition();

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

        if (
            !CellName.TryParse(
            candidate: (key ?? StateRow.SlotKey.Value),
            name: out var cellKey,
            reason: out _
        ) ||
            (StateRows.FindCell(
            cells: row.Cells,
            key: cellKey
        ) is not { } cell)
        ) {
            sample = new WorldStateSample(
                Max: row.Max,
                Min: row.Min,
                Motion: WorldStateMotion.Still,
                Value: default
            );

            return true;
        }

        var behavior = EffectiveBehavior.Resolve(
            cell: cell,
            row: row
        );
        var motion = (behavior switch {
            { Advance.PerSecondNumerator: not 0L } => WorldStateMotion.Advancing,
            { Cycle: not null } => WorldStateMotion.Stepping,
            _ => WorldStateMotion.Still,
        });

        StateReader.ReadCell(
            engineTick: engineTick,
            key: key,
            rawValue: out var raw,
            row: row,
            text: out var text,
            tick: tick
        );

        // The stored truth a .$target read answers never moves between writes; only the eased follower does.
        if (
            !target &&
            (raw is not null) &&
            (behavior.Dynamics is not null) &&
            StateReader.TryEvaluateDynamics(
            cell: cell,
            dynamics: document.Dynamics,
            row: row,
            sample: out var eased,
            tick: tick,
            ticksPerSecond: document.SimulationRateHz,
            trait: out _
        )
        ) {
            var stored = StateReader.DynamicsRowRawToFixed(
                raw: cell.Value.Raw,
                row: row
            );

            if (
                (eased.Value != stored) ||
                (eased.Velocity != FixedQ4816.Zero)
            ) {
                motion = WorldStateMotion.Easing;
            }

            raw = row.ClampToEnvelope(value: StateReader.DynamicsFixedToRowRaw(
                row: row,
                value: eased.Value
            ));
        }

        sample = new WorldStateSample(
            Max: row.Max,
            Min: row.Min,
            Motion: motion,
            Value: (row.Kind switch {
                CellKind.Vector => cell.Value,
                CellKind.Text => ((raw is not null)
                    ? CellValue.Text(value: text)
                    : default),
                _ => ((raw is { } number)
                    ? CellValue.FromNumber(
                        kind: row.Kind,
                        raw: number
                    )
                    : default),
            })
        );

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
                _ = WorldStateMirror.TryConvertNumber(
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
    /// <summary>Applies one snapshot's field-cell writes (<see cref="WorldSnapshot.FieldCells"/>) to the cells a field
    /// row is read from, and names each field row a write moved, so the owner refreshes the mirror slots bound to it
    /// (<see cref="WorldStateMirror.RefreshRows"/>). A write naming a field or a cell the delivered lattice does not
    /// hold is ignored. It allocates nothing unless the delivered lattice changed shape, which starts every field's
    /// cells at zero until the next full snapshot writes them.</summary>
    /// <param name="deltas">The snapshot's cell writes.</param>
    /// <param name="moved">Receives the catalog ordinal of each field row a write changed; at least
    /// <see cref="WorldFieldCapacity.MaxFields"/> long.</param>
    /// <returns>How many ordinals <paramref name="moved"/> received.</returns>
    public int ApplyFieldCells(ReadOnlySpan<FieldCellDelta> deltas, Span<int> moved) {
        if (deltas.IsEmpty) {
            return 0;
        }

        _ = Shape();

        Span<bool> changed = stackalloc bool[WorldFieldCapacity.MaxFields];

        foreach (var delta in deltas) {
            if (delta.Field >= m_cells.Length) {
                continue;
            }

            var cells = m_cells[delta.Field];

            if (
                (((uint)delta.Cell) >= ((uint)cells.Length)) ||
                (cells[delta.Cell] == delta.Raw)
            ) {
                continue;
            }

            cells[delta.Cell] = delta.Raw;
            changed[delta.Field] = true;
        }

        var count = 0;

        for (var field = 0; (field < m_cells.Length); field++) {
            if (
                changed[field] &&
                (m_ordinalOfField[field] >= 0)
            ) {
                moved[count++] = m_ordinalOfField[field];
            }
        }

        return count;
    }

    // Resolves the field rows of the definition currently delivered, keeping the delivered cells while the lattice
    // keeps its shape; a definition already shaped for costs one comparison.
    private WorldDefinition Shape() {
        var document = m_definition();

        if (ReferenceEquals(
            objA: document,
            objB: m_shaped
        )) {
            return document;
        }

        m_shaped = document;

        var fields = document.Fields;
        var fieldCount = (fields?.Fields.Count ?? 0);

        if (
            (fields?.Lattice != m_lattice) ||
            (m_cells.Length != fieldCount)
        ) {
            var cellCount = ((fields?.Lattice is { } lattice)
                ? ((lattice.Width * lattice.Layers) * lattice.Depth)
                : 0
            );

            m_lattice = fields?.Lattice;
            m_cells = new long[fieldCount][];

            for (var field = 0; (field < fieldCount); field++) {
                m_cells[field] = new long[cellCount];
            }
        }

        var rows = document.State;

        if (m_fieldOfOrdinal.Length < rows.Count) {
            m_fieldOfOrdinal = new int[rows.Count];
        }
        if (m_ordinalOfField.Length < fieldCount) {
            m_ordinalOfField = new int[fieldCount];
        }

        Array.Fill(
            array: m_fieldOfOrdinal,
            value: -1
        );

        for (var field = 0; (field < fieldCount); field++) {
            m_ordinalOfField[field] = -1;

            if (
                document.StateCatalog.TryResolve(
                handle: out var handle,
                lane: StateLane.Document,
                name: fields!.Fields[field].Name
            ) &&
                (handle.Ordinal < rows.Count)
            ) {
                m_ordinalOfField[field] = handle.Ordinal;
                m_fieldOfOrdinal[handle.Ordinal] = field;
            }
        }

        return document;
    }
}
