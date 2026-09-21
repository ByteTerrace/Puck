using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace Puck.State;

/// <summary>One row's place in a <see cref="StateArena"/>: which column carries it, where its cell slots start, and
/// the per-shape payloads a read or write needs without consulting the authored row again.</summary>
/// <param name="Ordinal">The row's catalog ordinal.</param>
/// <param name="Lane">The lane that owns the row.</param>
/// <param name="Shape">The row's storage shape.</param>
/// <param name="Kind">The cell kind every one of the row's cells is stored in.</param>
/// <param name="HostOwned">Whether a host facet serves the row; a host-owned row has no cell slots.</param>
/// <param name="Column">The index of the column this row sits in.</param>
/// <param name="CellStart">The row's first cell slot, or <c>-1</c> when the row stores no cells.</param>
/// <param name="CellCapacity">How many cell slots the row reserves.</param>
/// <param name="VectorByteStart">A vector row's first component byte.</param>
/// <param name="Dimensions">A vector row's component count per cell, the stride between its cells.</param>
/// <param name="Topology">A lattice row's compiled topology.</param>
/// <param name="Empty">A lattice or ring row's declared empty value.</param>
/// <param name="DomainOrdinal">An ordered row's token-domain row ordinal, or <c>-1</c>.</param>
/// <param name="InverseTokensOrdinal">A derived board's tokens row ordinal, or <c>-1</c>.</param>
/// <param name="InverseCodesOrdinal">A derived board's codes row ordinal, or <c>-1</c>.</param>
/// <param name="MaskWordStart">A draw site's first drawn-mask word, or <c>-1</c>.</param>
/// <param name="MaskCount">How many drawn masks a draw site reserves — the count its declaration carries, four
/// words each.</param>
/// <param name="LaneSlotStart">A lane slot's first lane-slot index, or <c>-1</c>.</param>
/// <param name="LaneCapacity">How many lane ordinals a lane slot reserves.</param>
/// <param name="HasTraits">Whether the row or any of its cells declares a value-over-time trait.</param>
public readonly record struct ArenaRowLayout(
    int Ordinal,
    StateLane Lane,
    RowShape Shape,
    CellKind Kind,
    bool HostOwned,
    int Column,
    int CellStart,
    int CellCapacity,
    int VectorByteStart,
    int Dimensions,
    CompiledTopology? Topology,
    long Empty,
    int DomainOrdinal,
    int InverseTokensOrdinal,
    int InverseCodesOrdinal,
    int MaskWordStart,
    int MaskCount,
    int LaneSlotStart,
    int LaneCapacity,
    bool HasTraits
) {
    /// <summary>Gets a value indicating whether this board's cells are derived from a token row rather than
    /// written directly.</summary>
    public bool IsDerivedBoard => ((InverseTokensOrdinal >= 0) && (InverseCodesOrdinal >= 0));
    /// <summary>Gets a value indicating whether the row reserves cell slots of its own.</summary>
    public bool IsStored => (CellStart >= 0);
    /// <summary>Gets a value indicating whether the row's cell order carries gameplay meaning.</summary>
    public bool IsOrdered => (Shape == RowShape.Ordered);
}
/// <summary>One column of a <see cref="StateArena"/>: the contiguous run of cell slots every row sharing a lane, a
/// shape, and a cell kind occupies.</summary>
/// <param name="Lane">The lane the column's rows belong to.</param>
/// <param name="Shape">The shape the column's rows share.</param>
/// <param name="Kind">The cell kind the column's rows share.</param>
/// <param name="FirstRow">The column's first entry in <see cref="ArenaLayout.ColumnRows"/>.</param>
/// <param name="RowCount">How many rows the column carries.</param>
/// <param name="FirstCell">The column's first cell slot.</param>
/// <param name="CellCount">How many cell slots the column spans.</param>
public readonly record struct ArenaColumnRange(StateLane Lane, RowShape Shape, CellKind Kind, int FirstRow, int RowCount, int FirstCell, int CellCount);
/// <summary>
/// The column plan every <see cref="StateArena"/> over one catalog shares: one contiguous column per cell kind per
/// shape per lane, a cell-slot offset for every key a row declares, ring and history cursors, ordered-row
/// membership, the derived boards each token and code row feeds, vector strides, and a column for every
/// runtime-state field a row or cell carries.
/// </summary>
/// <remarks>
/// A host-owned row (<see cref="StateRow.HostOwned"/>) is laid out with a descriptor and no cell slots: the arena
/// answers nothing for it, and its owning facet does.
/// <para>Offsets are a function of the catalog and the authored rows alone, so two runs of the same document lay
/// out identically and a hash folded in layout order compares across hosts.</para>
/// </remarks>
public sealed class ArenaLayout {
    // A settle keeps one eight-byte stamp and one flag for every position a journal entry can name.
    private const long ChangeBytesPerPosition = (sizeof(long) + sizeof(bool));
    private const long ArrayOverheadBytes = 32L;
    // A cell slot's share of the indexes: the row it belongs to, and a key-to-slot map that spans at most four
    // key ordinals a cell.
    private const long IndexBytesPerCellSlot = (sizeof(int) + (4L * sizeof(int)));
    // A row's bookkeeping: its layout record, its versions, generations and stamps, and its key map's slack.
    private const long BytesPerRow = 512L;
    // A string a reference column points at: the object's header, length and terminator, then two bytes a UTF-16
    // code unit. Every cell slot may carry a provenance, and every slot of a text row a text, each at the length
    // ceiling its write doors hold it to.
    private const long StringOverheadBytes = 32L;
    private const long ProvenanceBytesPerCellSlot = (StringOverheadBytes + (2L * StateCapacity.MaxProvenanceLength));
    private const long TextBytesPerTextSlot = (StringOverheadBytes + (2L * StateCapacity.MaxTextValueLength));

    private readonly int[] m_changeBases;
    private readonly ArenaColumnRange[] m_columns;
    private readonly int[] m_columnRows;
    private readonly Dictionary<int, int[]> m_codeDependents;
    private readonly Dictionary<int, int[]> m_dependents;
    private readonly ReadOnlyCollection<ArenaColumnRange> m_readOnlyColumns;
    private readonly int[] m_rowOfCellSlot;
    private readonly int[] m_rowOfLaneSlot;
    private readonly int[] m_rowOfMaskWord;
    private readonly ArenaRowLayout[] m_rows;
    private readonly int[] m_laneRosterBase;

    private ArenaLayout(
        ArenaRowLayout[] rows,
        ArenaColumnRange[] columns,
        int[] columnRows,
        int[] rowOfCellSlot,
        int[] rowOfLaneSlot,
        int[] rowOfMaskWord,
        int[] laneRosterBase,
        Dictionary<int, int[]> dependents,
        Dictionary<int, int[]> codeDependents,
        int cellSlotCount,
        int vectorByteCount,
        int laneSlotCount,
        int laneRosterCount,
        int maskWordCount,
        int poolCount,
        int poolWordCount,
        ArenaOptions options
    ) {
        m_codeDependents = codeDependents;
        m_columnRows = columnRows;
        m_columns = columns;
        m_dependents = dependents;
        m_laneRosterBase = laneRosterBase;
        m_readOnlyColumns = Array.AsReadOnly(array: columns);
        m_rowOfCellSlot = rowOfCellSlot;
        m_rowOfLaneSlot = rowOfLaneSlot;
        m_rowOfMaskWord = rowOfMaskWord;
        m_rows = rows;

        CellSlotCount = cellSlotCount;
        LaneRosterCount = laneRosterCount;
        LaneSlotCount = laneSlotCount;
        MaskWordCount = maskWordCount;
        Options = options;
        VectorByteCount = vectorByteCount;

        var textSlots = 0L;

        foreach (var row in rows) {
            if (row.Kind == CellKind.Text) {
                textSlots += row.CellCapacity;
            }
        }

        Bytes = Measure(
            cellSlots: cellSlotCount,
            laneRoster: laneRosterCount,
            laneSlots: laneSlotCount,
            maskWords: maskWordCount,
            poolCount: poolCount,
            poolWords: poolWordCount,
            rowCount: rows.Length,
            textSlots: textSlots,
            vectorBytes: vectorByteCount
        );

        m_changeBases = new int[ArenaColumns.All.Length];

        var next = 0;

        foreach (var column in ArenaColumns.All) {
            m_changeBases[((int)column)] = next;
            next += Size(column: column);
        }

        ChangeSlotCount = next;
    }

    /// <summary>Gets the bytes an arena over this layout reserves for its columns, indexes, and fixed-ceiling text
    /// and provenance payloads. <see cref="StateArena.Bytes"/> adds the bounded visibility payload and retained
    /// key storage currently in use.</summary>
    public long Bytes { get; }
    /// <summary>Gets how many cell slots the layout reserves across every column.</summary>
    public int CellSlotCount { get; }
    /// <summary>Gets how many distinct positions a journal entry can name, across every column.</summary>
    public int ChangeSlotCount { get; }
    /// <summary>Gets the columns in layout order.</summary>
    public IReadOnlyList<ArenaColumnRange> Columns => m_readOnlyColumns;
    /// <summary>Gets the row ordinals the columns carry, each column's run starting at its
    /// <see cref="ArenaColumnRange.FirstRow"/>.</summary>
    public ReadOnlySpan<int> ColumnRows => m_columnRows;
    /// <summary>Gets how many lane roster entries the two slot lanes reserve.</summary>
    public int LaneRosterCount { get; }
    /// <summary>Gets how many lane slots the two slot lanes reserve.</summary>
    public int LaneSlotCount { get; }
    /// <summary>Gets how many drawn-mask words the draw sites reserve.</summary>
    public int MaskWordCount { get; }
    /// <summary>Gets the lane widths this layout was built with.</summary>
    public ArenaOptions Options { get; }
    /// <summary>Gets how many rows the layout covers.</summary>
    public int RowCount => m_rows.Length;
    /// <summary>Gets how many component bytes the vector rows reserve.</summary>
    public int VectorByteCount { get; }

    /// <summary>Gets one row's place in the arena.</summary>
    /// <param name="ordinal">The row's catalog ordinal.</param>
    /// <returns>The row's layout.</returns>
    public ref readonly ArenaRowLayout this[int ordinal] => ref m_rows[ordinal];

    private static int Dimensions(IStateSection? section, StateRow row) {
        if (row.Kind != CellKind.Vector) {
            return 0;
        }

        var name = row.Space;
        var spaces = (section?.Spaces ?? []);

        // A vector row that names no space lives in the section's only one, the rule the validator and the rule
        // compiler read a row by.
        if (string.IsNullOrEmpty(value: name)) {
            return (((spaces.Count == 1) && (spaces[0] is { } only))
                ? only.Dimensions
                : throw new InvalidOperationException(message: $"State row '{row.Name.Value}' is a vector row naming no space, and the section declares {spaces.Count}; a row may leave its space out only when there is exactly one.")
            );
        }

        foreach (var space in spaces) {
            if (
                (space is not null) &&
                string.Equals(
                a: space.Name.Value,
                b: name,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return space.Dimensions;
            }
        }

        throw new InvalidOperationException(message: $"State row '{row.Name.Value}' names vector space '{name}', which the section does not declare.");
    }
    private static bool HasTraits(StateRow row) {
        if (
            (row.Advance is not null) ||
            (row.Cycle is not null) ||
            (row.Dynamics is not null)
        ) {
            return true;
        }

        foreach (var cell in (row.Cells ?? [])) {
            if (
                (cell is not null) &&
                ((cell.Advance is not null) || (cell.Cycle is not null) || (cell.Dynamics is not null) || (cell.Behavior != StateCellBehavior.Inherit))
            ) {
                return true;
            }
        }

        return false;
    }
    private static long Measure(long cellSlots, long textSlots, long rowCount, long maskWords, long laneSlots, long laneRoster, long poolCount, long poolWords, long vectorBytes) {
        var bytes = vectorBytes;

        bytes += (cellSlots * ProvenanceBytesPerCellSlot);
        bytes += (textSlots * TextBytesPerTextSlot);

        foreach (var column in ArenaColumns.All) {
            var size = (ArenaColumns.Space(column: column) switch {
                ArenaIndexSpace.Cell => cellSlots,
                ArenaIndexSpace.Row => rowCount,
                ArenaIndexSpace.MaskWord => maskWords,
                ArenaIndexSpace.LaneSlot => laneSlots,
                _ => laneRoster,
            });

            bytes += (ArenaColumns.StorageBytes(
                column: column,
                size: size
            ) + (size * ChangeBytesPerPosition));
        }

        bytes += (cellSlots * IndexBytesPerCellSlot);
        bytes += ((maskWords + laneSlots) * sizeof(int));
        bytes += (rowCount * BytesPerRow);
        if (poolCount > 0L) {
            // One row-to-pool array, one jagged occupancy array, and one ulong backing array per pool. Array headers
            // and the outer array's references are charged explicitly because these indexes scale independently of
            // cells. Catalogs without pools share empty arrays and pay none of this storage.
            bytes += (2L * ArrayOverheadBytes);
            bytes += (rowCount * sizeof(int));
            bytes += (poolCount * (sizeof(long) + ArrayOverheadBytes));
            bytes += (poolWords * sizeof(ulong));
        }

        return bytes;
    }
    private static void RequireFits(string what, long cellSlots, long textSlots, long rowCount, long maskWords, long laneSlots, long laneRoster, long poolCount, long poolWords, long vectorBytes) {
        var bytes = Measure(
            cellSlots: cellSlots,
            laneRoster: laneRoster,
            laneSlots: laneSlots,
            maskWords: maskWords,
            poolCount: poolCount,
            poolWords: poolWords,
            rowCount: rowCount,
            textSlots: textSlots,
            vectorBytes: vectorBytes
        );

        if (bytes > ArenaCapacity.MaxBytes) {
            throw new InvalidOperationException(message: $"{what} brings the arena to {bytes} bytes, past the {ArenaCapacity.MaxBytes}-byte ceiling; lower a row's capacity, lay a board over a smaller topology, or drop a row.");
        }
    }
    private static int SlotCapacity(StateRow row, CompiledTopology? topology) => (row.Shape switch {
        RowShape.Slot => 1,
        RowShape.Ring => ((StateDomain.Ring)row.EffectiveDomain).Capacity,
        RowShape.Lattice => (topology
            ?? throw new InvalidOperationException(message: $"State row '{row.Name.Value}' lies over topology '{((StateDomain.CellsOf)row.EffectiveDomain).Topology}', which the section does not declare.")
        ).CellCount,
        _ => row.CellCeiling,
    });

    /// <summary>Builds the column plan for one compiled catalog and the section it was compiled from.</summary>
    /// <param name="catalog">The compiled catalog whose descriptors the layout covers.</param>
    /// <param name="section">The authored section the catalog was compiled from.</param>
    /// <param name="options">The lane widths to build, or <see langword="null"/> for the defaults.</param>
    /// <returns>The layout.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A lattice row names a topology the section does not declare, a
    /// vector row names a space it does not declare, a draw site declares more masks than
    /// <see cref="ArenaCapacity.MaxDrawnMasks"/>, or the section lays out past
    /// <see cref="ArenaCapacity.MaxBytes"/>.</exception>
    public static ArenaLayout Build(StateCatalog catalog, IStateSection? section, ArenaOptions? options = null) {
        ArgumentNullException.ThrowIfNull(argument: catalog);

        var built = (options ?? ArenaOptions.Default);
        var rows = StateCatalog.ExpandRows(section: section);
        var layouts = new ArenaRowLayout[catalog.Count];
        var columns = new List<ArenaColumnRange>();
        var columnRows = new List<int>();
        var cellSlots = 0;
        var vectorBytes = 0;
        var laneSlots = 0;
        var maskWords = 0;
        // The same extents in a width a hostile capacity cannot wrap, checked as each row is placed so nothing is
        // allocated for a section that does not fit.
        var cellTotal = 0L;
        var textTotal = 0L;
        var vectorTotal = 0L;
        var laneTotal = 0L;
        var maskTotal = 0L;
        var poolCount = catalog.Pools.Count;
        var poolWords = catalog.Pools.Sum(selector: static pool => ((pool.Capacity + 63L) / 64L));
        var ordinalsByName = new Dictionary<string, int>(comparer: StringComparer.Ordinal);

        for (var ordinal = 0; (ordinal < catalog.Count); ordinal++) {
            var descriptor = catalog.Descriptors[ordinal];

            if (descriptor.Lane == StateLane.Document) {
                ordinalsByName[descriptor.Name] = ordinal;
            }
        }

        var laneRosterBase = new int[Enum.GetValues<StateLane>().Length];
        var laneRosterCount = 0;
        var rosterTotal = 0L;

        // A lane may be zero wide, which admits no ordinal; it may not be negative. The roster is measured before
        // any row is placed, so widths alone can refuse a section that declares no row at all.
        foreach (var lane in Enum.GetValues<StateLane>()) {
            var width = built.Capacity(lane: lane);

            if (width < 0) {
                throw new InvalidOperationException(message: $"The {lane} lane is {width} ordinals wide; a lane's width is zero or more.");
            }

            rosterTotal += width;

            RequireFits(
                cellSlots: 0L,
                laneRoster: rosterTotal,
                laneSlots: 0L,
                maskWords: 0L,
                poolCount: poolCount,
                poolWords: poolWords,
                rowCount: catalog.Count,
                textSlots: 0L,
                vectorBytes: 0L,
                what: $"The {lane} lane's roster"
            );

            laneRosterBase[((int)lane)] = laneRosterCount;
            laneRosterCount = ((int)rosterTotal);
        }

        // Columns are visited lane-major, then shape, then kind, so every row sharing the triple occupies one
        // contiguous run of cell slots and a fold in layout order reads each column once.
        foreach (var lane in Enum.GetValues<StateLane>()) {
            foreach (var shape in Enum.GetValues<RowShape>()) {
                foreach (var kind in Enum.GetValues<CellKind>()) {
                    var firstRow = columnRows.Count;
                    var firstCell = cellSlots;
                    var column = columns.Count;

                    for (var ordinal = 0; (ordinal < catalog.Count); ordinal++) {
                        var descriptor = catalog.Descriptors[ordinal];

                        if (
                            (descriptor.Lane != lane) ||
                            (descriptor.Shape != shape) ||
                            (descriptor.Kind != kind)
                        ) {
                            continue;
                        }

                        columnRows.Add(item: ordinal);

                        if (descriptor.Lane == StateLane.Document) {
                            var row = rows[descriptor.LaneOrdinal]!;
                            var topology = ((shape == RowShape.Lattice)
                                ? TopologyCompilation.Find(
                                    name: ((StateDomain.CellsOf)row.EffectiveDomain).Topology,
                                    section: section
                                )
                                : null);
                            var capacity = (descriptor.HostOwned
                                ? 0
                                : SlotCapacity(
                                    row: row,
                                    topology: topology
                                )
                            );
                            var dimensions = Dimensions(
                                row: row,
                                section: section
                            );
                            var maskCount = 0;
                            var maskStart = -1;

                            if (row.Draw is { } draw) {
                                // An inline source says how many masks its draws persist; a named source lives in
                                // the generators section the arena does not read, so the site reserves the ceiling.
                                maskCount = ((draw.Generator is { } generator)
                                    ? StateGenerator.MaskCount(generator: generator)
                                    : ArenaCapacity.MaxDrawnMasks
                                );

                                if (maskCount > ArenaCapacity.MaxDrawnMasks) {
                                    throw new InvalidOperationException(message: $"State row '{row.Name.Value}' declares a source persisting {maskCount} drawn masks, past the {ArenaCapacity.MaxDrawnMasks}-mask limit.");
                                }
                                if ((row.DrawnMasks?.Count ?? 0) > maskCount) {
                                    throw new InvalidOperationException(message: $"State row '{row.Name.Value}' carries {row.DrawnMasks!.Count} drawn masks, more than the {maskCount} its declared source persists.");
                                }

                                maskStart = maskWords;
                                maskTotal += (maskCount * 4L);
                            }

                            cellTotal += capacity;
                            textTotal += ((row.Kind == CellKind.Text)
                                ? capacity
                                : 0L
                            );
                            vectorTotal += (((long)dimensions) * capacity);

                            RequireFits(
                                cellSlots: cellTotal,
                                laneRoster: laneRosterCount,
                                laneSlots: laneTotal,
                                maskWords: maskTotal,
                                poolCount: poolCount,
                                poolWords: poolWords,
                                rowCount: catalog.Count,
                                textSlots: textTotal,
                                what: $"State row '{row.Name.Value}'",
                                vectorBytes: vectorTotal
                            );

                            maskWords = ((int)maskTotal);

                            layouts[ordinal] = new ArenaRowLayout(
                                CellCapacity: capacity,
                                CellStart: (descriptor.HostOwned
                                    ? -1
                                    : cellSlots
                                ),
                                Column: column,
                                Dimensions: dimensions,
                                DomainOrdinal: (((row.EffectiveDomain is StateDomain.KeysOf keysOf) && ordinalsByName.TryGetValue(
                                    key: keysOf.Row.Value,
                                    value: out var domain
                                ))
                                    ? domain
                                    : -1
                                ),
                                Empty: (row.EffectiveDomain switch {
                                    StateDomain.CellsOf board => board.Empty,
                                    StateDomain.Ring ring => ring.Empty,
                                    _ => 0L,
                                }),
                                HasTraits: HasTraits(row: row),
                                HostOwned: descriptor.HostOwned,
                                InverseCodesOrdinal: (((row.Inverse is { } codes) && ordinalsByName.TryGetValue(
                                    key: codes.Codes.Value,
                                    value: out var codesOrdinal
                                ))
                                    ? codesOrdinal
                                    : -1
                                ),
                                InverseTokensOrdinal: (((row.Inverse is { } tokens) && ordinalsByName.TryGetValue(
                                    key: tokens.Tokens.Value,
                                    value: out var tokensOrdinal
                                ))
                                    ? tokensOrdinal
                                    : -1
                                ),
                                Kind: kind,
                                Lane: lane,
                                LaneCapacity: 0,
                                LaneSlotStart: -1,
                                MaskCount: maskCount,
                                MaskWordStart: maskStart,
                                Ordinal: ordinal,
                                Shape: shape,
                                Topology: topology,
                                VectorByteStart: ((dimensions > 0)
                                    ? vectorBytes
                                    : 0
                                )
                            );

                            cellSlots += capacity;
                            vectorBytes += (dimensions * capacity);
                        } else {
                            var width = built.Capacity(lane: lane);

                            laneTotal += width;

                            RequireFits(
                                cellSlots: cellTotal,
                                laneRoster: laneRosterCount,
                                laneSlots: laneTotal,
                                maskWords: maskTotal,
                                poolCount: poolCount,
                                poolWords: poolWords,
                                rowCount: catalog.Count,
                                textSlots: textTotal,
                                what: $"State row '{descriptor.Name}'",
                                vectorBytes: vectorTotal
                            );

                            layouts[ordinal] = new ArenaRowLayout(
                                CellCapacity: 0,
                                CellStart: -1,
                                Column: column,
                                Dimensions: 0,
                                DomainOrdinal: -1,
                                Empty: 0L,
                                HasTraits: false,
                                HostOwned: false,
                                InverseCodesOrdinal: -1,
                                InverseTokensOrdinal: -1,
                                Kind: kind,
                                Lane: lane,
                                LaneCapacity: width,
                                LaneSlotStart: laneSlots,
                                MaskCount: 0,
                                MaskWordStart: -1,
                                Ordinal: ordinal,
                                Shape: shape,
                                Topology: null,
                                VectorByteStart: 0
                            );

                            laneSlots += width;
                        }
                    }

                    if (columnRows.Count > firstRow) {
                        columns.Add(item: new ArenaColumnRange(
                            CellCount: (cellSlots - firstCell),
                            FirstCell: firstCell,
                            FirstRow: firstRow,
                            Kind: kind,
                            Lane: lane,
                            RowCount: (columnRows.Count - firstRow),
                            Shape: shape
                        ));
                    }
                }
            }
        }

        var rowOfCellSlot = new int[cellSlots];
        var rowOfLaneSlot = new int[laneSlots];
        var rowOfMaskWord = new int[maskWords];
        var dependents = new Dictionary<int, int[]>();
        var codeDependents = new Dictionary<int, int[]>();
        var byTokens = new Dictionary<int, List<int>>();
        var byCodes = new Dictionary<int, List<int>>();

        for (var ordinal = 0; (ordinal < layouts.Length); ordinal++) {
            var layout = layouts[ordinal];

            if (
                layout.IsStored &&
                (layout.CellCapacity > 0)
            ) {
                rowOfCellSlot.AsSpan(
                    length: layout.CellCapacity,
                    start: layout.CellStart
                ).Fill(value: ordinal);
            }

            if (layout.LaneSlotStart >= 0) {
                rowOfLaneSlot.AsSpan(
                    length: layout.LaneCapacity,
                    start: layout.LaneSlotStart
                ).Fill(value: ordinal);
            }

            if (layout.MaskCount > 0) {
                rowOfMaskWord.AsSpan(
                    length: (layout.MaskCount * 4),
                    start: layout.MaskWordStart
                ).Fill(value: ordinal);
            }

            if (layout.IsDerivedBoard) {
                if (!byTokens.TryGetValue(
                    key: layout.InverseTokensOrdinal,
                    value: out var fed
                )) {
                    fed = [];
                    byTokens[layout.InverseTokensOrdinal] = fed;
                }

                fed.Add(item: ordinal);

                if (!byCodes.TryGetValue(
                    key: layout.InverseCodesOrdinal,
                    value: out var coded
                )) {
                    coded = [];
                    byCodes[layout.InverseCodesOrdinal] = coded;
                }

                coded.Add(item: ordinal);
            }
        }

        foreach (var (tokens, boards) in byTokens) {
            dependents[tokens] = [.. boards];
        }
        foreach (var (codes, boards) in byCodes) {
            codeDependents[codes] = [.. boards];
        }

        return new ArenaLayout(
            cellSlotCount: cellSlots,
            codeDependents: codeDependents,
            columnRows: [.. columnRows],
            columns: [.. columns],
            dependents: dependents,
            laneRosterBase: laneRosterBase,
            laneRosterCount: laneRosterCount,
            laneSlotCount: laneSlots,
            maskWordCount: maskWords,
            options: built,
            poolCount: poolCount,
            poolWordCount: checked((int)poolWords),
            rowOfCellSlot: rowOfCellSlot,
            rowOfLaneSlot: rowOfLaneSlot,
            rowOfMaskWord: rowOfMaskWord,
            rows: layouts,
            vectorByteCount: vectorBytes
        );
    }
    /// <summary>Builds the column plan, answering a section the arena cannot lay out with the reason instead of
    /// an exception.</summary>
    /// <param name="catalog">The compiled catalog whose descriptors the layout covers.</param>
    /// <param name="section">The authored section the catalog was compiled from.</param>
    /// <param name="options">The lane widths to build, or <see langword="null"/> for the defaults.</param>
    /// <param name="layout">The layout, on success.</param>
    /// <param name="reason">Why the section does not lay out, or empty.</param>
    /// <returns><see langword="true"/> when the section lays out.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is <see langword="null"/>.</exception>
    public static bool TryBuild(StateCatalog catalog, IStateSection? section, ArenaOptions? options, [NotNullWhen(true)] out ArenaLayout? layout, out string reason) {
        try {
            layout = Build(
                catalog: catalog,
                options: options,
                section: section
            );
            reason = string.Empty;

            return true;
        } catch (InvalidOperationException exception) {
            layout = null;
            reason = exception.Message;

            return false;
        }
    }
    /// <summary>Returns the base a column's indices occupy in the change-slot space.</summary>
    /// <param name="column">The column to place.</param>
    /// <returns>The column's first change slot.</returns>
    public int ChangeBase(ArenaColumn column) => m_changeBases[((int)column)];
    /// <summary>Returns the derived boards a codes row feeds, or <see langword="null"/> when it feeds none.</summary>
    /// <param name="codesOrdinal">The codes row's ordinal.</param>
    /// <returns>The dependent board ordinals.</returns>
    public int[]? DependentBoardsOfCodes(int codesOrdinal) => (m_codeDependents.TryGetValue(
        key: codesOrdinal,
        value: out var boards
    )
        ? boards
        : null
    );
    /// <summary>Returns the derived boards a tokens row feeds, or <see langword="null"/> when it feeds none.</summary>
    /// <param name="tokensOrdinal">The tokens row's ordinal.</param>
    /// <returns>The dependent board ordinals.</returns>
    public int[]? DependentBoardsOfTokens(int tokensOrdinal) => (m_dependents.TryGetValue(
        key: tokensOrdinal,
        value: out var boards
    )
        ? boards
        : null
    );
    /// <summary>Returns the first roster entry of one slot lane.</summary>
    /// <param name="lane">The lane to place.</param>
    /// <returns>The lane's first roster entry.</returns>
    public int LaneRosterBase(StateLane lane) => m_laneRosterBase[((int)lane)];
    /// <summary>Returns the row that owns one position of a column, or <c>-1</c> when the position belongs to a
    /// whole lane rather than to one row.</summary>
    /// <param name="column">The column the position is in.</param>
    /// <param name="index">The position within that column's index space.</param>
    /// <returns>The owning row's catalog ordinal, or <c>-1</c>.</returns>
    public int RowOf(ArenaColumn column, int index) => (ArenaColumns.Space(column: column) switch {
        ArenaIndexSpace.Cell => m_rowOfCellSlot[index],
        ArenaIndexSpace.Row => index,
        ArenaIndexSpace.MaskWord => m_rowOfMaskWord[index],
        ArenaIndexSpace.LaneSlot => m_rowOfLaneSlot[index],
        _ => -1,
    });
    /// <summary>Returns how many positions one column addresses.</summary>
    /// <param name="column">The column to size.</param>
    /// <returns>The column's position count.</returns>
    public int Size(ArenaColumn column) => (ArenaColumns.Space(column: column) switch {
        ArenaIndexSpace.Cell => CellSlotCount,
        ArenaIndexSpace.Row => RowCount,
        ArenaIndexSpace.MaskWord => MaskWordCount,
        ArenaIndexSpace.LaneSlot => LaneSlotCount,
        _ => LaneRosterCount,
    });
}
