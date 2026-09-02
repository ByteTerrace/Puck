namespace Puck.World.Server;

/// <summary>Names one capacity-bounded integer cell participating in an atomic economic settlement.</summary>
/// <param name="Row">The state row carrying the asset.</param>
/// <param name="Key">The holder key inside that row.</param>
public readonly record struct WorldEconomicCell(WorldCellName Row, WorldCellName Key);
/// <summary>One holder-cell movement in a balanced economic candidate receipt.</summary>
/// <param name="Cell">The touched cell.</param>
/// <param name="Before">Its live value at the settlement tick.</param>
/// <param name="After">Its committed candidate value.</param>
/// <param name="Delta">The signed change from <paramref name="Before"/> to <paramref name="After"/>.</param>
public readonly record struct WorldEconomicCellDelta(WorldEconomicCell Cell, long Before, long After, Int128 Delta);
/// <summary>One asset row's exact conservation proof in a balanced economic candidate receipt.</summary>
/// <param name="Row">The conserved asset row.</param>
/// <param name="CellDelta">The sum of holder-cell changes.</param>
/// <param name="ReserveDelta">The signed movement into external reserves.</param>
/// <param name="NetDelta">The exact sum of cell and reserve movement; zero for a committed receipt.</param>
public readonly record struct WorldEconomicConservationDelta(WorldCellName Row, Int128 CellDelta, Int128 ReserveDelta, Int128 NetDelta);
/// <summary>The immutable, bounded audit result emitted for a balanced candidate, before document validation and install.</summary>
public sealed class WorldEconomicCandidateReceipt {
    private readonly System.Collections.ObjectModel.ReadOnlyCollection<WorldEconomicCellDelta> m_cells;
    private readonly System.Collections.ObjectModel.ReadOnlyCollection<WorldEconomicConservationDelta> m_conservation;

    internal WorldEconomicCandidateReceipt(WorldEconomicCellDelta[] cells, WorldEconomicConservationDelta[] conservation) {
        m_cells = Array.AsReadOnly(array: cells);
        m_conservation = Array.AsReadOnly(array: conservation);
    }

    /// <summary>Gets the touched holder cells in deterministic first-touch order.</summary>
    public IReadOnlyList<WorldEconomicCellDelta> Cells => m_cells;
    /// <summary>Gets the per-asset conservation results in deterministic first-touch order.</summary>
    public IReadOnlyList<WorldEconomicConservationDelta> Conservation => m_conservation;
    /// <summary>Gets a value indicating whether every exact per-row net delta is zero.</summary>
    public bool Conserved {
        get {
            foreach (var row in m_conservation) {
                if (row.NetDelta != 0) {
                    return false;
                }
            }

            return true;
        }
    }
}
/// <summary>
/// Builds one bounded debit/credit settlement over world-owned integer cells, proves conservation per asset row, and
/// publishes a candidate document only when every precondition and arithmetic operation succeeds.
/// </summary>
/// <remarks>
/// <para>The settlement never mutates its source document. Reads are pinned to the constructor's tick and each cell is
/// read once, so an advancing cell is accrued once even when several operations touch it. Writes retain the cell's
/// advance, dynamics, and provenance traits; the caller's ordinary whole-document validation remains the authority for
/// authored row envelopes.</para>
/// <para><see cref="Reserve(WorldCellName, long)"/> and <see cref="Release(WorldCellName, long)"/> represent value
/// moving into and out of an external bounded reserve such as an auction listing or house-fee account. They carry no
/// storage or lifetime policy of their own: the built-in using the substrate owns that reserve and passes its exact
/// delta into the same atomic candidate.</para>
/// </remarks>
public sealed class WorldEconomicSettlement {
    /// <summary>The absolute receipt-cell ceiling implied by the document's row and per-row cell ceilings.</summary>
    public const int MaxTouchedCells = (WorldStateCapacity.MaxRows * WorldStateCapacity.MaxCellsPerRow);

    private readonly List<CellChange> m_cells = [];
    private readonly List<RowChange> m_conservation = [];

    private readonly WorldDefinition m_source;
    private readonly ulong m_tick;

    private string m_failure = string.Empty;

    /// <summary>Initializes an empty settlement against one immutable document image.</summary>
    /// <param name="source">The document whose live cell values are read.</param>
    /// <param name="tick">The authoritative tick at which advancing cells are evaluated.</param>
    public WorldEconomicSettlement(WorldDefinition source, ulong tick) {
        ArgumentNullException.ThrowIfNull(argument: source);

        m_source = source;
        m_tick = tick;
    }

    /// <summary>Gets the first failed precondition or operation, or an empty string while the settlement is viable.</summary>
    public string FailureReason => m_failure;

    /// <summary>Adds a non-negative amount to a holder cell.</summary>
    /// <param name="cell">The cell to credit.</param>
    /// <param name="amount">The amount to add.</param>
    /// <returns><see langword="true"/> when the operation was staged.</returns>
    public bool Credit(WorldEconomicCell cell, long amount) {
        if (!TryAmount(amount: amount, operation: "credit")) {
            return false;
        }
        if (amount == 0L) {
            return true;
        }

        if (!TryCell(cell: cell, change: out var entry)) {
            return false;
        }

        entry.Delta += amount;
        ChangeCellConservation(row: cell.Row, delta: amount);

        return true;
    }
    /// <summary>Subtracts a non-negative amount from a holder cell when its staged balance can cover it.</summary>
    /// <param name="cell">The cell to debit.</param>
    /// <param name="amount">The amount to subtract.</param>
    /// <param name="insufficientReason">The caller-facing refusal used when the balance is too small.</param>
    /// <returns><see langword="true"/> when the operation was staged.</returns>
    public bool Debit(WorldEconomicCell cell, long amount, string insufficientReason) {
        ArgumentException.ThrowIfNullOrEmpty(argument: insufficientReason);

        if (!TryAmount(amount: amount, operation: "debit")) {
            return false;
        }
        if (amount == 0L) {
            return true;
        }

        if (!TryCell(cell: cell, change: out var entry)) {
            return false;
        }

        var balance = (((Int128)entry.InitialValue) + entry.Delta);

        if (balance < amount) {
            return Require(condition: false, reason: insufficientReason);
        }

        entry.Delta -= amount;
        ChangeCellConservation(row: cell.Row, delta: -((Int128)amount));

        return true;
    }
    /// <summary>Reads a cell's live value plus operations already staged in this settlement.</summary>
    /// <param name="cell">The cell to read.</param>
    /// <param name="balance">The staged balance on success.</param>
    /// <returns><see langword="true"/> when the cell and its integer row resolve and the balance is representable.</returns>
    public bool TryBalance(WorldEconomicCell cell, out long balance) {
        balance = 0L;

        if (!TryCell(cell: cell, change: out var entry)) {
            return false;
        }

        var value = (((Int128)entry.InitialValue) + entry.Delta);

        if (
            (value < long.MinValue) ||
            (value > long.MaxValue)
        ) {
            return Require(condition: false, reason: $"economic balance '{cell.Row}.{cell.Key}' is outside the Int64 range");
        }

        balance = ((long)value);

        return true;
    }
    /// <summary>Stages value entering an external reserve for an asset row.</summary>
    /// <param name="row">The asset row.</param>
    /// <param name="amount">The non-negative amount entering reserve.</param>
    /// <returns><see langword="true"/> when the reserve delta was staged.</returns>
    public bool Reserve(WorldCellName row, long amount) {
        if (!TryAmount(amount: amount, operation: "reserve")) {
            return false;
        }
        if (amount == 0L) {
            return true;
        }
        if (!TryEconomicRow(resolved: out _, row: row)) {
            return false;
        }

        ChangeReserveConservation(delta: amount, row: row);

        return true;
    }
    /// <summary>Stages value leaving an external reserve for an asset row.</summary>
    /// <param name="row">The asset row.</param>
    /// <param name="amount">The non-negative amount leaving reserve.</param>
    /// <returns><see langword="true"/> when the reserve delta was staged.</returns>
    public bool Release(WorldCellName row, long amount) {
        if (!TryAmount(amount: amount, operation: "release")) {
            return false;
        }
        if (amount == 0L) {
            return true;
        }
        if (!TryEconomicRow(resolved: out _, row: row)) {
            return false;
        }

        ChangeReserveConservation(delta: -((Int128)amount), row: row);

        return true;
    }
    /// <summary>Latches a caller-owned precondition; the first refusal wins and prevents candidate publication.</summary>
    /// <param name="condition">The condition that must hold.</param>
    /// <param name="reason">The refusal used when it does not.</param>
    /// <returns><paramref name="condition"/> while the settlement has not already failed; otherwise
    /// <see langword="false"/>.</returns>
    public bool Require(bool condition, string reason) {
        ArgumentException.ThrowIfNullOrEmpty(argument: reason);

        if (!string.IsNullOrEmpty(value: m_failure)) {
            return false;
        }

        if (!condition) {
            m_failure = reason;

            return false;
        }

        return true;
    }
    /// <summary>Moves a non-negative amount between two holder cells in the same asset row.</summary>
    /// <param name="source">The cell to debit.</param>
    /// <param name="destination">The cell to credit.</param>
    /// <param name="amount">The amount to move.</param>
    /// <param name="insufficientReason">The caller-facing refusal used when the source balance is too small.</param>
    /// <returns><see langword="true"/> when both sides were staged.</returns>
    public bool Transfer(WorldEconomicCell source, WorldEconomicCell destination, long amount, string insufficientReason) {
        if (!Require(
            condition: (source.Row == destination.Row),
            reason: $"economic transfer crosses asset rows '{source.Row}' and '{destination.Row}'"
        )) {
            return false;
        }

        return (Debit(amount: amount, cell: source, insufficientReason: insufficientReason)
            && Credit(amount: amount, cell: destination));
    }
    /// <summary>Publishes the settlement's cell writes and caller-owned metadata as one candidate document.</summary>
    /// <param name="complete">Adds the built-in's metadata (for example, a listing row) to the state candidate.</param>
    /// <param name="candidate">The completed candidate, or the untouched source on refusal.</param>
    /// <param name="receipt">The balanced candidate audit image, or <see langword="null"/> on refusal. This is not an
    /// install or journal receipt; the surrounding mutation pipeline still owns validation and commit.</param>
    /// <param name="reason">The refusal reason, or an empty string on success.</param>
    /// <returns><see langword="true"/> only when all operations succeeded, every asset row conserved exactly, and
    /// every final cell value fits in <see cref="long"/>.</returns>
    public bool TryApply(Func<WorldDefinition, WorldDefinition> complete, out WorldDefinition candidate, out WorldEconomicCandidateReceipt? receipt, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: complete);
        candidate = m_source;
        receipt = null;

        if (!string.IsNullOrEmpty(value: m_failure)) {
            reason = m_failure;

            return false;
        }

        foreach (var row in m_conservation) {
            var net = (row.CellDelta + row.ReserveDelta);

            if (net != 0) {
                reason = $"economic settlement does not conserve '{row.Row}' (net {net})";

                return false;
            }
        }

        var rows = m_source.State;
        var cellDeltas = new WorldEconomicCellDelta[m_cells.Count];

        for (var index = 0; (index < m_cells.Count); index++) {
            var cell = m_cells[index];
            var value = (((Int128)cell.InitialValue) + cell.Delta);

            if (
                (value < long.MinValue) ||
                (value > long.MaxValue)
            ) {
                reason = $"economic balance '{cell.Cell.Row}.{cell.Cell.Key}' is outside the Int64 range";

                return false;
            }

            rows = WriteCell(
                rows: rows,
                cell: cell.Cell,
                value: ((long)value)
            );
            cellDeltas[index] = new WorldEconomicCellDelta(
                Cell: cell.Cell,
                Before: cell.InitialValue,
                After: ((long)value),
                Delta: cell.Delta
            );
        }

        candidate = complete(arg: m_source.WithWorldState(rows: rows));
        var conservation = new WorldEconomicConservationDelta[m_conservation.Count];

        for (var index = 0; (index < m_conservation.Count); index++) {
            var row = m_conservation[index];

            conservation[index] = new WorldEconomicConservationDelta(
                Row: row.Row,
                CellDelta: row.CellDelta,
                ReserveDelta: row.ReserveDelta,
                NetDelta: (row.CellDelta + row.ReserveDelta)
            );
        }

        receipt = new WorldEconomicCandidateReceipt(cells: cellDeltas, conservation: conservation);
        reason = string.Empty;

        return true;
    }

    private void ChangeCellConservation(WorldCellName row, Int128 delta) {
        foreach (var change in m_conservation) {
            if (change.Row == row) {
                change.CellDelta += delta;

                return;
            }
        }

        m_conservation.Add(item: new RowChange(cellDelta: delta, reserveDelta: 0, row: row));
    }
    private void ChangeReserveConservation(WorldCellName row, Int128 delta) {
        foreach (var change in m_conservation) {
            if (change.Row == row) {
                change.ReserveDelta += delta;

                return;
            }
        }

        m_conservation.Add(item: new RowChange(cellDelta: 0, reserveDelta: delta, row: row));
    }
    private bool TryAmount(long amount, string operation) => Require(
        condition: (amount >= 0L),
        reason: $"economic {operation} amount {amount} must be non-negative"
    );
    private bool TryCell(WorldEconomicCell cell, out CellChange change) {
        foreach (var found in m_cells) {
            if (found.Cell == cell) {
                change = found;

                return string.IsNullOrEmpty(value: m_failure);
            }
        }

        if (!string.IsNullOrEmpty(value: m_failure)) {
            change = null!;

            return false;
        }

        if (!WorldStateReader.TryRead(
            definition: m_source,
            key: cell.Key,
            rawValue: out var raw,
            row: out var row,
            rowName: cell.Row,
            text: out _,
            tick: m_tick
        )) {
            change = null!;

            return Require(condition: false, reason: $"no state row named '{cell.Row}'");
        }

        if (!TryEconomicRow(row: cell.Row, resolved: out _)) {
            change = null!;

            return false;
        }

        var existed = (WorldDefinitionRows.FindCell(cells: row.Cells, key: cell.Key) is not null);

        if (!existed) {
            var stagedNewCells = 0;

            foreach (var staged in m_cells) {
                if (
                    (staged.Cell.Row == cell.Row) &&
                    !staged.Existed
                ) {
                    stagedNewCells++;
                }
            }

            if (((row.Cells?.Count ?? 0) + stagedNewCells) >= row.Capacity!.Value) {
                change = null!;

                return Require(condition: false, reason: $"'{cell.Row}' has no capacity for economic cell '{cell.Key}'");
            }
        }

        if (m_cells.Count >= MaxTouchedCells) {
            change = null!;

            return Require(condition: false, reason: $"economic settlement exceeds the {MaxTouchedCells}-cell receipt ceiling");
        }

        change = new CellChange(cell: cell, existed: existed, initialValue: (raw ?? 0L));
        m_cells.Add(item: change);

        return true;
    }
    private bool TryEconomicRow(WorldCellName row, out WorldStateRow resolved) {
        resolved = WorldDefinitionRows.FindStateRow(rows: m_source.State, name: row)!;

        if (resolved is null) {
            return Require(condition: false, reason: $"no state row named '{row}'");
        }

        return Require(
            condition: ((resolved.Kind == CellKind.Int) && (resolved.Capacity is not null)),
            reason: $"'{row}' is not a capacity-bounded int state row"
        );
    }
    private static IReadOnlyList<WorldStateRow> WriteCell(IReadOnlyList<WorldStateRow> rows, WorldEconomicCell cell, long value) {
        var row = WorldDefinitionRows.FindStateRow(rows: rows, name: cell.Row)!;
        var existing = WorldDefinitionRows.FindCell(cells: row.Cells, key: cell.Key);
        var replacement = new WorldStateCell(
            Key: cell.Key,
            Value: value,
            Advance: existing?.Advance,
            Provenance: existing?.Provenance,
            Dynamics: existing?.Dynamics
        );
        var cells = ReplaceOrAppend(
            list: (row.Cells ?? []),
            item: replacement,
            keyOf: static value => value.Key
        );

        return ReplaceOrAppend(
            list: rows,
            item: (row with { Cells = cells }),
            keyOf: static value => value.Name
        );
    }
    private static IReadOnlyList<T> ReplaceOrAppend<T, TKey>(IReadOnlyList<T> list, T item, Func<T, TKey> keyOf) where TKey : notnull {
        var key = keyOf(arg: item);
        var result = new T[(list.Count + 1)];

        for (var index = 0; (index < list.Count); index++) {
            if (EqualityComparer<TKey>.Default.Equals(x: keyOf(arg: list[index]), y: key)) {
                var replaced = new T[list.Count];

                for (var copy = 0; (copy < list.Count); copy++) {
                    replaced[copy] = ((copy == index)
                        ? item
                        : list[copy]
                    );
                }

                return replaced;
            }

            result[index] = list[index];
        }

        result[^1] = item;

        return result;
    }

    private sealed class CellChange(WorldEconomicCell cell, bool existed, long initialValue) {
        public WorldEconomicCell Cell { get; } = cell;

        public Int128 Delta { get; set; }

        public bool Existed { get; } = existed;
        public long InitialValue { get; } = initialValue;
    }
    private sealed class RowChange(WorldCellName row, Int128 cellDelta, Int128 reserveDelta) {
        public Int128 CellDelta { get; set; } = cellDelta;
        public Int128 ReserveDelta { get; set; } = reserveDelta;
        public WorldCellName Row { get; } = row;
    }
}
