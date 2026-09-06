namespace Puck.State;

/// <summary>How one row's cells are laid into a frame's value array.</summary>
public enum FrameRowKind : byte {
    /// <summary>Not framed: a text row, or a fixed-point row over a field topology. Reads fall through to the row's own cells; writes refuse.</summary>
    Unframed,
    /// <summary>One value.</summary>
    Slot,
    /// <summary>One value per cell of the row's cell list, in list order; the frame never mints a key.</summary>
    Keyed,
    /// <summary>One value per topology cell, indexed by cell ordinal.</summary>
    Board,
    /// <summary>One value per ring slot followed by the cursor.</summary>
    Ring,
}

/// <summary>One row's place in a frame.</summary>
/// <param name="Kind">How the row is laid out.</param>
/// <param name="Offset">The first value's index.</param>
/// <param name="Length">How many values the row occupies; a ring's includes its cursor.</param>
/// <param name="Topology">A board row's topology.</param>
/// <param name="Empty">A board or ring row's empty value.</param>
/// <param name="InverseTokensOrdinal">A derived board's <see cref="StateInverse.Tokens"/> row ordinal, or -1 for a
/// board that carries no <see cref="StateRow.Inverse"/> (or whose declared tokens row does not resolve).</param>
/// <param name="InverseCodesOrdinal">A derived board's <see cref="StateInverse.Codes"/> row ordinal, or -1 on the
/// same terms as <see cref="InverseTokensOrdinal"/>.</param>
public readonly record struct FrameRowLayout(FrameRowKind Kind, int Offset, int Length, CompiledTopology? Topology, long Empty, int InverseTokensOrdinal = -1, int InverseCodesOrdinal = -1) {
    /// <summary>Gets a value indicating whether this board's cells are derived from a token row rather than
    /// authored — <see cref="InverseTokensOrdinal"/> and <see cref="InverseCodesOrdinal"/> both resolved.</summary>
    public bool IsDerivedBoard => ((InverseTokensOrdinal >= 0) && (InverseCodesOrdinal >= 0));
}

/// <summary>The layout every frame over one section shares: each row's kind, offset, and length, computed once from
/// the rows' structure. Frames built on one layout copy into one another as a single span copy.</summary>
public sealed class FrameLayout {
    private readonly FrameRowLayout[] m_rows;
    private readonly Dictionary<string, int> m_ordinals = new(comparer: StringComparer.Ordinal);
    private readonly Func<string, CompiledTopology?> m_topology;
    // A tokens row ordinal -> the derived board ordinals it feeds, so a keyed write can find what to recompute
    // without a per-write scan of every row. Absent for a row that feeds none.
    private readonly Dictionary<int, int[]> m_dependents = [];

    /// <summary>Lays out a section's rows.</summary>
    /// <param name="rows">The rows.</param>
    /// <param name="topology">Resolves a board row's topology by name; a board whose topology does not resolve is unframed.</param>
    public FrameLayout(IReadOnlyList<StateRow> rows, Func<string, CompiledTopology?> topology) {
        ArgumentNullException.ThrowIfNull(argument: rows);
        ArgumentNullException.ThrowIfNull(argument: topology);
        m_rows = new FrameRowLayout[rows.Count];
        m_topology = topology;

        for (var index = 0; index < rows.Count; index++) {
            m_ordinals[rows[index].Name.Value] = index;
        }

        var offset = 0;
        var dependents = new Dictionary<int, List<int>>();

        for (var index = 0; index < rows.Count; index++) {
            var row = rows[index];
            var layout = Layout(row: row, topology: topology, offset: offset, ordinals: m_ordinals);

            m_rows[index] = layout;
            offset += layout.Length;

            if (layout.IsDerivedBoard) {
                if (!dependents.TryGetValue(key: layout.InverseTokensOrdinal, value: out var fed)) {
                    fed = [];
                    dependents[layout.InverseTokensOrdinal] = fed;
                }

                fed.Add(item: index);
            }
        }

        foreach (var (tokensOrdinal, boards) in dependents) {
            m_dependents[tokensOrdinal] = [.. boards];
        }

        Length = offset;
    }

    /// <summary>Gets the derived board ordinals whose cells recompute from a write to the row at
    /// <paramref name="tokensOrdinal"/>, or <see langword="null"/> when that row feeds none.</summary>
    /// <param name="tokensOrdinal">The candidate tokens row's ordinal.</param>
    public int[]? DependentBoards(int tokensOrdinal) => (m_dependents.TryGetValue(key: tokensOrdinal, value: out var boards) ? boards : null);

    /// <summary>Gets how many values a frame on this layout holds.</summary>
    public int Length { get; }
    /// <summary>Gets how many rows the layout covers.</summary>
    public int RowCount => m_rows.Length;
    /// <summary>Gets a row's layout by ordinal.</summary>
    /// <param name="ordinal">The row's position in the laid-out rows.</param>
    public FrameRowLayout this[int ordinal] => m_rows[ordinal];

    /// <summary>Finds a row's ordinal by name.</summary>
    /// <param name="name">The row name.</param>
    /// <param name="ordinal">The ordinal.</param>
    public bool TryOrdinal(string name, out int ordinal) => m_ordinals.TryGetValue(key: name, value: out ordinal);

    /// <summary>Returns whether other rows would lay out identically — the same names in the same order, each with
    /// the same kind and length — so a frame on this layout can be rebound to them without a new layout.</summary>
    /// <param name="rows">The candidate rows.</param>
    public bool Fits(IReadOnlyList<StateRow> rows) {
        ArgumentNullException.ThrowIfNull(argument: rows);

        if (rows.Count != m_rows.Length) {
            return false;
        }

        for (var index = 0; index < rows.Count; index++) {
            var row = rows[index];

            if (!m_ordinals.TryGetValue(key: row.Name.Value, value: out var ordinal) || (ordinal != index)) {
                return false;
            }

            var candidate = Layout(row: row, topology: m_topology, offset: m_rows[index].Offset, ordinals: m_ordinals);

            if (
                (candidate.Kind != m_rows[index].Kind) || (candidate.Length != m_rows[index].Length) || (candidate.Empty != m_rows[index].Empty) ||
                !ReferenceEquals(objA: candidate.Topology, objB: m_rows[index].Topology) ||
                (candidate.InverseTokensOrdinal != m_rows[index].InverseTokensOrdinal) || (candidate.InverseCodesOrdinal != m_rows[index].InverseCodesOrdinal)
            ) {
                return false;
            }
        }

        return true;
    }

    private static FrameRowLayout Layout(StateRow row, Func<string, CompiledTopology?> topology, int offset, Dictionary<string, int> ordinals) {
        if (row.Kind == CellKind.Text) {
            return new FrameRowLayout(Kind: FrameRowKind.Unframed, Offset: offset, Length: 0, Topology: null, Empty: 0L);
        }

        switch (row.EffectiveDomain) {
            case StateDomain.Slot:
                return new FrameRowLayout(Kind: FrameRowKind.Slot, Offset: offset, Length: 1, Topology: null, Empty: 0L);
            case StateDomain.CellsOf board:
                var compiled = topology(board.Topology);

                if ((compiled is null) || (row.Kind == CellKind.Fixed)) {
                    return new FrameRowLayout(Kind: FrameRowKind.Unframed, Offset: offset, Length: 0, Topology: null, Empty: 0L);
                }

                var tokensOrdinal = -1;
                var codesOrdinal = -1;

                if (row.Inverse is { } inverse) {
                    tokensOrdinal = (ordinals.TryGetValue(key: inverse.Tokens.Value, value: out var tokens) ? tokens : -1);
                    codesOrdinal = (ordinals.TryGetValue(key: inverse.Codes.Value, value: out var codes) ? codes : -1);
                }

                return new FrameRowLayout(Kind: FrameRowKind.Board, Offset: offset, Length: compiled.CellCount, Topology: compiled, Empty: board.Empty, InverseTokensOrdinal: tokensOrdinal, InverseCodesOrdinal: codesOrdinal);
            case StateDomain.Ring ring:
                return new FrameRowLayout(Kind: FrameRowKind.Ring, Offset: offset, Length: (ring.Capacity + 1), Topology: null, Empty: ring.Empty);
            default:
                return new FrameRowLayout(Kind: FrameRowKind.Keyed, Offset: offset, Length: (row.Cells?.Count ?? 0), Topology: null, Empty: 0L);
        }
    }
}

/// <summary>A value frame over a section's rows: every integer cell laid out once by a <see cref="FrameLayout"/>, so
/// a hypothetical evaluation reads and writes an array while the rows keep supplying keys, domains, and traits. A
/// frame never changes structure — it refuses a key its row does not hold — and a text row reads through to the
/// row's own cells.</summary>
public sealed class StateFrame : StateStore {
    private readonly long[] m_values;

    /// <summary>Initializes an all-zero frame.</summary>
    /// <param name="layout">The layout.</param>
    /// <param name="rows">The rows the layout was computed from.</param>
    public StateFrame(FrameLayout layout, IReadOnlyList<StateRow> rows) {
        ArgumentNullException.ThrowIfNull(argument: layout);
        ArgumentNullException.ThrowIfNull(argument: rows);
        Layout = layout;
        m_rows = rows;
        m_values = new long[layout.Length];
    }

    /// <summary>Gets the layout.</summary>
    public FrameLayout Layout { get; }
    private IReadOnlyList<StateRow> m_rows;

    /// <inheritdoc/>
    public override IReadOnlyList<StateRow> Rows => m_rows;

    /// <summary>Rebinds the frame's structure to rows the layout <see cref="FrameLayout.Fits"/>; the values stay.</summary>
    /// <param name="rows">The rows.</param>
    public void Rebind(IReadOnlyList<StateRow> rows) {
        ArgumentNullException.ThrowIfNull(argument: rows);
        m_rows = rows;
    }
    /// <summary>Gets the frame's values, in layout order.</summary>
    public Span<long> Values => m_values;

    /// <summary>Copies another store's values into this frame: every framed row is read through the source.</summary>
    /// <param name="source">The store to read; its rows must share this frame's structure.</param>
    public void Load(StateStore source) {
        ArgumentNullException.ThrowIfNull(argument: source);

        for (var ordinal = 0; ordinal < Layout.RowCount; ordinal++) {
            var layout = Layout[ordinal];
            var row = Rows[ordinal];
            var values = m_values.AsSpan(start: layout.Offset, length: layout.Length);

            switch (layout.Kind) {
                case FrameRowKind.Slot:
                    values[0] = (source.TryStored(row: row, key: StateRow.SlotKey, value: out var slot, text: out _) ? slot : 0L);
                    break;
                case FrameRowKind.Keyed:
                    for (var index = 0; index < layout.Length; index++) {
                        values[index] = (source.TryStoredAt(row: row, index: index, value: out var keyed) ? keyed : 0L);
                    }
                    break;
                case FrameRowKind.Board:
                    source.ReadBoard(row: row, topology: layout.Topology!, values: values);
                    break;
                case FrameRowKind.Ring:
                    for (var index = 0; index < (layout.Length - 1); index++) {
                        values[index] = (source.TryStoredAt(row: row, index: index, value: out var pushed) ? pushed : layout.Empty);
                    }
                    values[layout.Length - 1] = source.HistoryCursor(row: row);
                    break;
                default:
                    break;
            }
        }
    }
    /// <summary>Copies another frame on the same layout.</summary>
    /// <param name="other">The frame to copy.</param>
    public void CopyFrom(StateFrame other) {
        ArgumentNullException.ThrowIfNull(argument: other);
        if (!ReferenceEquals(objA: other.Layout, objB: Layout)) {
            throw new ArgumentException(message: "The frames are laid out differently.", paramName: nameof(other));
        }

        other.m_values.AsSpan().CopyTo(destination: m_values);
    }

    /// <inheritdoc/>
    public override bool TryStored(StateRow row, CellName key, out long value, out string? text) {
        text = null;

        if (!Layout.TryOrdinal(name: row.Name.Value, ordinal: out var ordinal) || (Layout[ordinal].Kind == FrameRowKind.Unframed)) {
            return RowStore.Stored(row: row, key: key, value: out value, text: out text);
        }

        var layout = Layout[ordinal];

        switch (layout.Kind) {
            case FrameRowKind.Slot:
                value = m_values[layout.Offset];

                return (key == StateRow.SlotKey);
            case FrameRowKind.Keyed: {
                var index = IndexOf(cells: row.Cells, key: key);
                value = ((index >= 0) ? m_values[layout.Offset + index] : 0L);

                return (index >= 0);
            }
            case FrameRowKind.Board: {
                if (!layout.Topology!.TryCell(key: key.Value, cell: out var cell)) {
                    value = 0L;

                    return false;
                }

                value = m_values[layout.Offset + cell];

                // The row's own list holds only the cells it was given; a frame cell still at the empty value that the
                // row never held reads absent, the same answer the row would give.
                return ((value != layout.Empty) || (IndexOf(cells: row.Cells, key: key) >= 0));
            }
            case FrameRowKind.Ring: {
                var capacity = (layout.Length - 1);

                if (!StateReader.TryParseCandidateIndex(key: key.Value, index: out var slot) || (slot >= Math.Min(val1: m_values[layout.Offset + capacity], val2: capacity))) {
                    value = 0L;

                    return false;
                }

                value = m_values[layout.Offset + slot];

                return true;
            }
            default:
                value = 0L;

                return false;
        }
    }
    /// <inheritdoc/>
    public override bool TryStoredAt(StateRow row, int index, out long value) {
        if (!Layout.TryOrdinal(name: row.Name.Value, ordinal: out var ordinal)) {
            value = 0L;

            return false;
        }

        var layout = Layout[ordinal];

        switch (layout.Kind) {
            case FrameRowKind.Unframed:
                if ((row.Cells is { } cells) && (((uint)index) < ((uint)cells.Count))) {
                    value = cells[index].Value;

                    return true;
                }
                break;
            case FrameRowKind.Slot:
            case FrameRowKind.Keyed:
                if (((uint)index) < ((uint)layout.Length)) {
                    value = m_values[layout.Offset + index];

                    return true;
                }
                break;
            case FrameRowKind.Board:
                if ((row.Cells is { } boardCells) && (((uint)index) < ((uint)boardCells.Count)) && layout.Topology!.TryCell(key: boardCells[index].Key.Value, cell: out var cell)) {
                    value = m_values[layout.Offset + cell];

                    return true;
                }
                break;
            case FrameRowKind.Ring:
                if (((uint)index) < ((uint)(layout.Length - 1))) {
                    value = m_values[layout.Offset + index];

                    return true;
                }
                break;
            default:
                break;
        }

        value = 0L;

        return false;
    }
    /// <inheritdoc/>
    public override long HistoryCursor(StateRow row) =>
        ((Layout.TryOrdinal(name: row.Name.Value, ordinal: out var ordinal) && (Layout[ordinal] is { Kind: FrameRowKind.Ring } layout))
            ? m_values[layout.Offset + layout.Length - 1]
            : row.HistoryCursor);
    /// <inheritdoc/>
    public override void ReadBoard(StateRow row, CompiledTopology topology, Span<long> values) {
        if (Layout.TryOrdinal(name: row.Name.Value, ordinal: out var ordinal) && (Layout[ordinal] is { Kind: FrameRowKind.Board } layout) && ReferenceEquals(objA: layout.Topology, objB: topology)) {
            m_values.AsSpan(start: layout.Offset, length: layout.Length).CopyTo(destination: values);

            return;
        }

        BoardQueries.Read(row: row, topology: topology, values: values);
    }

    /// <summary>Writes one cell the row already holds, refusing a key the row lacks, a text row, a ring, a derived
    /// board, or a value outside the row's envelope.</summary>
    /// <param name="row">The row.</param>
    /// <param name="key">The cell key.</param>
    /// <param name="value">The operand, in the row's encoding.</param>
    /// <param name="write">Set or add.</param>
    /// <param name="reason">Why the write refused, or empty.</param>
    public bool TryWrite(StateRow row, CellName key, long value, StateWriteKind write, out string reason) {
        if (!Layout.TryOrdinal(name: row.Name.Value, ordinal: out var ordinal)) {
            reason = $"row '{row.Name}' is not in the frame";

            return false;
        }

        var layout = Layout[ordinal];

        if (layout.IsDerivedBoard) {
            reason = $"row '{row.Name}' is a derived board (inverse) — write its token row instead";

            return false;
        }

        var index = layout.Kind switch {
            FrameRowKind.Slot => ((key == StateRow.SlotKey) ? 0 : -1),
            FrameRowKind.Keyed => IndexOf(cells: row.Cells, key: key),
            FrameRowKind.Board => (layout.Topology!.TryCell(key: key.Value, cell: out var cell) ? cell : -1),
            _ => -1,
        };

        if (layout.Kind is FrameRowKind.Unframed or FrameRowKind.Ring) {
            reason = $"row '{row.Name}' is a {(layout.Kind == FrameRowKind.Ring ? "ring, which a frame only pushes" : "text or field row, which a frame does not write")}";

            return false;
        }
        if (index < 0) {
            reason = $"row '{row.Name}' holds no cell '{key}'; a frame never mints a key";

            return false;
        }

        ref var slot = ref m_values[layout.Offset + index];
        var next = ((write == StateWriteKind.Add) ? unchecked(slot + value) : value);

        if ((row.ClampToEnvelope(value: next) != next) || ((row.Kind == CellKind.Bool) && (next is not (0L or 1L)))) {
            reason = $"row '{row.Name}' cell '{key}' would leave the row's envelope";

            return false;
        }

        var previous = slot;

        slot = next;
        reason = string.Empty;

        // A tokens-row relocation recomputes every derived board it feeds — cheap: only the moved token's old and
        // new cells can have changed, so this touches at most two cells of each dependent board rather than
        // recomputing the whole thing.
        if ((layout.Kind == FrameRowKind.Keyed) && (Layout.DependentBoards(ordinal) is { } boards)) {
            foreach (var boardOrdinal in boards) {
                RecomputeDerivedBoard(boardOrdinal: boardOrdinal, previousCell: previous, currentCell: next);
            }
        }

        return true;
    }
    // Rewrites at most the two board cells a token's relocation could have changed: the cell it left (whose winner
    // may now be a different, still-resident token) and the cell it entered (whose winner may now be this token, or
    // whichever other token also names it and sits later in row order).
    private void RecomputeDerivedBoard(int boardOrdinal, long previousCell, long currentCell) {
        var boardLayout = Layout[boardOrdinal];
        var tokensLayout = Layout[boardLayout.InverseTokensOrdinal];
        var codesLayout = Layout[boardLayout.InverseCodesOrdinal];
        var topology = boardLayout.Topology!;

        if (previousCell != currentCell) {
            RecomputeDerivedCell(boardLayout: boardLayout, tokensLayout: tokensLayout, codesLayout: codesLayout, cellCount: topology.CellCount, cell: previousCell);
        }

        RecomputeDerivedCell(boardLayout: boardLayout, tokensLayout: tokensLayout, codesLayout: codesLayout, cellCount: topology.CellCount, cell: currentCell);
    }
    private void RecomputeDerivedCell(FrameRowLayout boardLayout, FrameRowLayout tokensLayout, FrameRowLayout codesLayout, int cellCount, long cell) {
        if ((cell < 0) || (cell >= cellCount)) {
            return;
        }

        var tokens = m_values.AsSpan(start: tokensLayout.Offset, length: tokensLayout.Length);
        var winner = -1;

        for (var index = 0; (index < tokens.Length); index++) {
            if (tokens[index] == cell) {
                winner = index;
            }
        }

        m_values[boardLayout.Offset + (int)cell] = ((winner >= 0) && (winner < codesLayout.Length))
            ? m_values[codesLayout.Offset + winner]
            : boardLayout.Empty;
    }
    /// <summary>Pushes one value onto a ring row, overwriting the oldest slot once the ring is full.</summary>
    /// <param name="row">The ring row.</param>
    /// <param name="value">The value pushed.</param>
    /// <param name="reason">Why the push refused, or empty.</param>
    public bool TryPush(StateRow row, long value, out string reason) {
        if (!Layout.TryOrdinal(name: row.Name.Value, ordinal: out var ordinal) || (Layout[ordinal] is not { Kind: FrameRowKind.Ring } layout)) {
            reason = $"row '{row.Name}' is not a ring in the frame";

            return false;
        }
        if (row.ClampToEnvelope(value: value) != value) {
            reason = $"row '{row.Name}' refuses a pushed value outside its envelope";

            return false;
        }

        var capacity = (layout.Length - 1);
        ref var cursor = ref m_values[layout.Offset + capacity];

        m_values[layout.Offset + (int)(cursor % capacity)] = value;
        cursor++;
        reason = string.Empty;

        return true;
    }
    /// <summary>Clears the components a placed value enclosed, on the frame's dense board.</summary>
    /// <param name="enclosed">The transform, its origin already resolved to a cell key.</param>
    /// <param name="reason">Why the transform refused, or empty.</param>
    public bool TryClearEnclosed(StateTransform.ClearEnclosed enclosed, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: enclosed);

        if (!Layout.TryOrdinal(name: enclosed.Row, ordinal: out var ordinal) || (Layout[ordinal] is not { Kind: FrameRowKind.Board } layout) || (Rows[ordinal].Kind != CellKind.Int)) {
            reason = $"clearEnclosed row '{enclosed.Row}' is not an integer board in the frame";

            return false;
        }
        if (layout.IsDerivedBoard) {
            reason = $"clearEnclosed row '{enclosed.Row}' is a derived board — write its token row instead";

            return false;
        }
        if (!layout.Topology!.TryCell(key: enclosed.From, cell: out var source)) {
            reason = "clearEnclosed 'from' names no cell of the board's topology";

            return false;
        }
        if ((enclosed.Lower > enclosed.Upper) || ((layout.Empty >= enclosed.Lower) && (layout.Empty <= enclosed.Upper))) {
            reason = "clearEnclosed takes a range that excludes the board's empty value";

            return false;
        }

        _ = BoardQueries.ClearEnclosed(topology: layout.Topology, values: m_values.AsSpan(start: layout.Offset, length: layout.Length), source: source, lower: enclosed.Lower, upper: enclosed.Upper, empty: layout.Empty);
        reason = string.Empty;

        return true;
    }
    /// <summary>Applies a board combine on the frame's dense boards, on the same terms the installed section's transform holds.</summary>
    /// <param name="combine">The transform.</param>
    /// <param name="reason">Why the transform refused, or empty.</param>
    public bool TryBoardCombine(StateTransform.BoardCombine combine, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: combine);

        if (!Layout.TryOrdinal(name: combine.Row, ordinal: out var ordinal) || (Layout[ordinal] is not { Kind: FrameRowKind.Board } layout)) {
            reason = $"boardCombine row '{combine.Row}' is not a board in the frame";

            return false;
        }
        if (layout.IsDerivedBoard) {
            reason = $"boardCombine row '{combine.Row}' is a derived board — write its token row instead";

            return false;
        }

        var row = Rows[ordinal];
        var topology = layout.Topology!;
        var operation = combine.Operation;
        var needsLeft = (operation is not (BoardCombineOp.Fill or BoardCombineOp.Clear));
        var needsRight = (operation is BoardCombineOp.And or BoardCombineOp.Or or BoardCombineOp.Xor or BoardCombineOp.AndNot);

        if (
            !Enum.IsDefined(value: operation) || (needsLeft != (combine.Left is not null)) || (needsRight != (combine.Right is not null)) ||
            ((operation == BoardCombineOp.Shift) != (combine.Direction is not null)) || ((operation == BoardCombineOp.Image) != (combine.Element is not null))
        ) {
            reason = "boardCombine takes left for every operation but fill and clear, right for and/or/xor/andNot, direction for shift alone, and element for image alone";

            return false;
        }
        if ((row.ClampToEnvelope(value: combine.Value) != combine.Value) || ((row.Kind == CellKind.Bool) && (combine.Value is not (0L or 1L))) || (combine.Value == layout.Empty)) {
            reason = "boardCombine writes a member value the board admits and that is not the board's own empty value";

            return false;
        }

        var direction = ((combine.Direction is { } directionName) ? topology.Direction(token: directionName) : -1);
        var element = ((combine.Element is { } elementName) ? topology.Element(name: elementName) : -1);

        if (((combine.Direction is not null) && (direction < 0)) || ((combine.Element is not null) && (element < 0))) {
            reason = "boardCombine names a direction or point-group element its topology does not declare";

            return false;
        }
        var leftLayout = default(FrameRowLayout);
        var rightLayout = default(FrameRowLayout);

        if ((needsLeft && !TryBoard(name: combine.Left!, topology: topology, layout: out leftLayout)) || (needsRight && !TryBoard(name: combine.Right!, topology: topology, layout: out rightLayout))) {
            reason = $"boardCombine sources must be boards over the same topology as '{combine.Row}'";

            return false;
        }

        Span<long> left = stackalloc long[topology.CellCount];
        Span<long> right = stackalloc long[topology.CellCount];
        Span<bool> member = stackalloc bool[topology.CellCount];
        var leftEmpty = 0L;
        var rightEmpty = 0L;

        if (needsLeft) {
            m_values.AsSpan(start: leftLayout.Offset, length: topology.CellCount).CopyTo(destination: left);
            leftEmpty = leftLayout.Empty;
        }
        if (needsRight) {
            m_values.AsSpan(start: rightLayout.Offset, length: topology.CellCount).CopyTo(destination: right);
            rightEmpty = rightLayout.Empty;
        }

        for (var cell = 0; cell < topology.CellCount; cell++) {
            var inLeft = (needsLeft && (left[cell] != leftEmpty));
            var inRight = (needsRight && (right[cell] != rightEmpty));

            switch (operation) {
                case BoardCombineOp.Fill: member[cell] = true; break;
                case BoardCombineOp.And: member[cell] = (inLeft && inRight); break;
                case BoardCombineOp.Or: member[cell] = (inLeft || inRight); break;
                case BoardCombineOp.Xor: member[cell] = (inLeft ^ inRight); break;
                case BoardCombineOp.AndNot: member[cell] = (inLeft && !inRight); break;
                case BoardCombineOp.Not: member[cell] = !inLeft; break;
                case BoardCombineOp.Shift:
                    if (inLeft && (topology.Neighbour(cell: cell, direction: direction) is >= 0 and var neighbour)) { member[neighbour] = true; }
                    break;
                case BoardCombineOp.Image:
                    if (inLeft) { member[topology.Image(element: element, cell: cell)] = true; }
                    break;
                default: break;
            }
        }

        var target = m_values.AsSpan(start: layout.Offset, length: topology.CellCount);

        for (var cell = 0; cell < topology.CellCount; cell++) {
            target[cell] = ((operation == BoardCombineOp.Copy)
                ? ((left[cell] != leftEmpty) ? left[cell] : layout.Empty)
                : (member[cell] ? combine.Value : layout.Empty));
        }

        reason = string.Empty;

        return true;
    }

    private bool TryBoard(string name, CompiledTopology topology, out FrameRowLayout layout) {
        if (Layout.TryOrdinal(name: name, ordinal: out var ordinal) && (Layout[ordinal] is { Kind: FrameRowKind.Board } found) && ReferenceEquals(objA: found.Topology, objB: topology)) {
            layout = found;

            return true;
        }

        layout = default;

        return false;
    }

    private static int IndexOf(IReadOnlyList<StateCell>? cells, CellName key) {
        if (cells is null) {
            return -1;
        }

        for (var index = 0; index < cells.Count; index++) {
            if (cells[index].Key == key) {
                return index;
            }
        }

        return -1;
    }
}
