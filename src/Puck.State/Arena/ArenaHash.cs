using System.Runtime.InteropServices;
using Puck.Maths;

namespace Puck.State;

public sealed partial class StateArena {
    /// <summary>The rows one hash segment holds: the arena's digest folds one digest per segment of this many
    /// consecutive catalog ordinals, and a segment folds one digest per row (<see cref="RowDigest"/>).</summary>
    /// <remarks>It divides the parallel tick's 256-row snapshot partition evenly, so a partitioned fold takes whole
    /// segments.</remarks>
    public const int HashSegmentRows = 32;

    // The digest cache: one digest per row, valid while the row's version is the one it was folded at, and one per
    // segment, valid while none of its rows refolded. Both belong to one layout and one key table: a relayout,
    // reservation or adoption replaces the layout and a restore replaces the key table, and either drops the cache.
    // Readers on other threads (a console hash beside the tick) share it under the gate.
    private readonly Lock m_digestGate = new();

    private ulong[] m_rowDigests = [];
    private ulong[] m_rowDigestVersions = [];
    private bool[] m_rowDigestKnown = [];
    private ulong[] m_segmentDigests = [];
    private bool[] m_segmentDigestKnown = [];
    private (ArenaColumn Column, int Index)[] m_unownedPositions = [];
    private ArenaLayout? m_digestLayout;
    private CellKeyTable? m_digestKeys;

    /// <summary>Gets how many row digests the arena folded into its cache since it was built: a load-independent
    /// count of the work <see cref="ComputeHash"/> and <see cref="RowDigest"/> did, which an unchanged row never adds
    /// to and <see cref="RecomputeHash"/> never counts.</summary>
    public long RowDigestFolds { get; private set; }

    /// <summary>Folds every stored column of every lane plus the retained key ledger into one hash: one digest per
    /// segment of <see cref="HashSegmentRows"/> rows, each over its rows' <see cref="RowDigest"/>, then the positions
    /// no row owns, the key ledger and the undo history.</summary>
    /// <returns>The hash value.</returns>
    /// <remarks>
    /// The fold reads the logical value of each position rather than the array behind it, so an arena that has
    /// never written a clock hashes the same as one whose clock was written back to zero. Reservations follow
    /// the catalog, authored rows and admitted growth, so hosts running the same document and writes fold the
    /// same bytes in the same order.
    /// <para>A row refolds only when its <see cref="RowVersion"/> moved since its digest was taken, or the layout or
    /// key table was replaced, so a tick that wrote three rows folds three. Inside an open scope versions have not
    /// settled, so the whole arena folds afresh and the cache is neither read nor written.
    /// <see cref="RecomputeHash"/> is the same value with no cache at all, which a verification run compares
    /// against.</para>
    /// <para>A host-owned row folds as its ordinal alone: it has no columns here, and its facet hashes what it
    /// serves.</para>
    /// <para>Members fold their names in slot order, independently of intern ordinals. The retained key ledger
    /// folds its count and order-independent digest sum because orphan names affect future mint admission.</para>
    /// </remarks>
    public ulong ComputeHash() {
        if (m_journal.Scopes != 0) { return Digest(cached: false); }
        lock (m_digestGate) { return Digest(cached: true); }
    }
    /// <summary>Computes <see cref="ComputeHash"/>'s value with every row folded afresh, reading and writing no
    /// cached digest: the full recompute an incremental digest is checked against.</summary>
    /// <returns>The hash value.</returns>
    public ulong RecomputeHash() => Digest(cached: false);
    /// <summary>Returns one row's digest: <see cref="AddRowTo"/> folded into a fresh hash, cached by the row's
    /// <see cref="RowVersion"/> outside an open scope.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <returns>The digest.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rowOrdinal"/> addresses no row.</exception>
    public ulong RowDigest(int rowOrdinal) {
        if (((uint)rowOrdinal) >= ((uint)m_layout.RowCount)) {
            throw new ArgumentOutOfRangeException(
                actualValue: rowOrdinal,
                message: "The arena carries no row at that ordinal.",
                paramName: nameof(rowOrdinal)
            );
        }
        if (m_journal.Scopes != 0) { return FreshRowDigest(rowOrdinal: rowOrdinal); }
        lock (m_digestGate) {
            PrepareDigestCache();
            return CachedRowDigest(refolded: out _, rowOrdinal: rowOrdinal);
        }
    }

    private ulong Digest(bool cached) {
        if (cached) { PrepareDigestCache(); }
        var hash = Fnv1aHash.Create();
        var rows = m_layout.RowCount;
        var unowned = (cached ? m_unownedPositions : UnownedPositions(layout: m_layout));

        hash.Add(value: ((ulong)rows));
        for (var segment = 0; ((segment * HashSegmentRows) < rows); segment++) {
            hash.Add(value: (cached
                ? CachedSegmentDigest(segment: segment)
                : FreshSegmentDigest(segment: segment)));
        }
        // A lane roster bit belongs to a lane rather than a row, and a mask word or lane slot outside every row's
        // range belongs to none: no row version covers them, so they fold whole every time.
        hash.Add(value: ((ulong)unowned.Length));
        foreach (var (column, index) in unowned) {
            hash.Add(value: ReadNumberRaw(
                column: column,
                index: index
            ));
        }
        // Retained names affect future mint admission, including names no row currently uses. The digest sum
        // preserves that resource state without making allocation ordinals part of content identity.
        m_keys.AddLedgerTo(hash: ref hash);
        AddUndoTo(hash: ref hash);
        return hash.Value;
    }
    private void PrepareDigestCache() {
        if (ReferenceEquals(objA: m_digestLayout, objB: m_layout) && ReferenceEquals(objA: m_digestKeys, objB: m_keys)) { return; }
        var rows = m_layout.RowCount;
        var segments = ((rows + (HashSegmentRows - 1)) / HashSegmentRows);

        m_rowDigests = new ulong[rows];
        m_rowDigestVersions = new ulong[rows];
        m_rowDigestKnown = new bool[rows];
        m_segmentDigests = new ulong[segments];
        m_segmentDigestKnown = new bool[segments];
        m_unownedPositions = UnownedPositions(layout: m_layout);
        m_digestLayout = m_layout;
        m_digestKeys = m_keys;
    }
    private static (ArenaColumn Column, int Index)[] UnownedPositions(ArenaLayout layout) {
        var positions = new List<(ArenaColumn Column, int Index)>();

        foreach (var column in ArenaColumns.All) {
            if (IsLinkColumn(column: column) || (ArenaColumns.Space(column: column) == ArenaIndexSpace.Cell)) { continue; }
            for (var index = 0; (index < layout.Size(column: column)); index++) {
                if (layout.RowOf(column: column, index: index) < 0) { positions.Add(item: (column, index)); }
            }
        }
        return [.. positions];
    }
    private ulong FreshRowDigest(int rowOrdinal) {
        var hash = Fnv1aHash.Create();

        AddRowTo(hash: ref hash, rowOrdinal: rowOrdinal);
        return hash.Value;
    }
    private ulong CachedRowDigest(int rowOrdinal, out bool refolded) {
        var version = m_rowVersions[rowOrdinal];

        if (m_rowDigestKnown[rowOrdinal] && (m_rowDigestVersions[rowOrdinal] == version)) {
            refolded = false;
            return m_rowDigests[rowOrdinal];
        }
        var digest = FreshRowDigest(rowOrdinal: rowOrdinal);

        RowDigestFolds++;
        m_rowDigests[rowOrdinal] = digest;
        m_rowDigestVersions[rowOrdinal] = version;
        m_rowDigestKnown[rowOrdinal] = true;
        refolded = true;
        return digest;
    }
    private ulong CachedSegmentDigest(int segment) {
        var first = (segment * HashSegmentRows);
        var end = Math.Min(val1: (first + HashSegmentRows), val2: m_layout.RowCount);
        var refolded = !m_segmentDigestKnown[segment];

        for (var row = first; (row < end); row++) {
            _ = CachedRowDigest(refolded: out var rowRefolded, rowOrdinal: row);
            refolded |= rowRefolded;
        }
        if (!refolded) { return m_segmentDigests[segment]; }
        var hash = Fnv1aHash.Create();

        for (var row = first; (row < end); row++) { hash.Add(value: m_rowDigests[row]); }
        m_segmentDigests[segment] = hash.Value;
        m_segmentDigestKnown[segment] = true;
        return hash.Value;
    }
    private ulong FreshSegmentDigest(int segment) {
        var first = (segment * HashSegmentRows);
        var end = Math.Min(val1: (first + HashSegmentRows), val2: m_layout.RowCount);
        var hash = Fnv1aHash.Create();

        for (var row = first; (row < end); row++) { hash.Add(value: FreshRowDigest(rowOrdinal: row)); }
        return hash.Value;
    }
    /// <summary>Folds one column of every lane into its own hash.</summary>
    /// <param name="column">The column to fold.</param>
    /// <returns>The hash value.</returns>
    public ulong ComputeColumnHash(ArenaColumn column) {
        var hash = Fnv1aHash.Create();

        FoldColumn(
            column: column,
            hash: ref hash
        );

        return hash.Value;
    }
    /// <summary>Folds everything the arena stores for one row into a running hash: every column's positions that
    /// belong to the row, in column order: the fold <see cref="RowDigest"/> takes and <see cref="ComputeHash"/>
    /// combines.</summary>
    /// <param name="hash">The running hash.</param>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <remarks>
    /// A row is more than its values. Its member keys say which key stands at which position, its cursors say where
    /// a ring's oldest slot and a draw site's next sample are, its presence bits tell a lattice cell holding the
    /// empty value from one holding none, and its runtime-state columns carry what a live read answers from. Two
    /// rows with the same stored fields fold the same independently of key allocation order, so a
    /// caller keying a cache by a set of rows folds each through here rather than reading values back by position.
    /// <para>A host-owned row stores nothing here and folds as its ordinal alone; its facet hashes what it
    /// serves. The lane roster belongs to a lane rather than a row and is not folded.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rowOrdinal"/> addresses no row.</exception>
    public void AddRowTo(ref Fnv1aHash hash, int rowOrdinal) {
        if (((uint)rowOrdinal) >= ((uint)m_layout.RowCount)) {
            throw new ArgumentOutOfRangeException(
                actualValue: rowOrdinal,
                message: "The arena carries no row at that ordinal.",
                paramName: nameof(rowOrdinal)
            );
        }

        ref readonly var layout = ref m_layout[rowOrdinal];

        // The ordinal and each column's own ordinal frame the row's runs, so neither two rows nor two adjacent
        // columns of one row can present the same values by dividing them differently.
        hash.Add(value: ((ulong)rowOrdinal));

        if (layout.HostOwned) {
            return;
        }

        foreach (var column in ArenaColumns.All) {
            if (IsLinkColumn(column: column)) { continue; }
            hash.Add(value: ((ulong)((byte)column)));

            switch (ArenaColumns.Space(column: column)) {
                case ArenaIndexSpace.Cell:
                    if (layout.IsStored) {
                        hash.Add(value: (ulong)layout.CellCapacity);
                for (var position = 0; (position < HashCellExtent(layout: layout)); position++) {
                            FoldCell(
                                column: column,
                                hash: ref hash,
                                layout: layout,
                                slot: CellSlotAt(layout: layout, position: position)
                            );
                        }
                    }

                    break;
                case ArenaIndexSpace.Row:
                    hash.Add(value: ReadNumberRaw(
                        column: column,
                        index: rowOrdinal
                    ));

                    break;
                case ArenaIndexSpace.MaskWord:
                    if (layout.MaskWordStart >= 0) {
                        for (var word = 0; (word < (layout.MaskCount * 4)); word++) {
                            hash.Add(value: ReadNumberRaw(
                                column: column,
                                index: (layout.MaskWordStart + word)
                            ));
                        }
                    }

                    break;
                case ArenaIndexSpace.LaneSlot:
                    if (layout.LaneSlotStart >= 0) {
                        for (var slot = 0; (slot < layout.LaneCapacity); slot++) {
                            hash.Add(value: ReadNumberRaw(
                                column: column,
                                index: (layout.LaneSlotStart + slot)
                            ));
                        }
                    }

                    break;
                default:
                    break;
            }
        }
    }

    private void FoldColumn(ref Fnv1aHash hash, ArenaColumn column) {
        if (IsLinkColumn(column: column)) { return; }
        if (ArenaColumns.Space(column: column) != ArenaIndexSpace.Cell) {
            var size = m_layout.Size(column: column);

            for (var index = 0; (index < size); index++) {
                var row = m_layout.RowOf(
                    column: column,
                    index: index
                );

                if (
                    (row >= 0) &&
                    m_layout[row].HostOwned
                ) {
                    continue;
                }

                hash.Add(value: ReadNumberRaw(
                    column: column,
                    index: index
                ));
            }

            return;
        }

        foreach (var range in m_layout.Columns) {
            foreach (var rowOrdinal in m_layout.ColumnRows.Slice(
                length: range.RowCount,
                start: range.FirstRow
            )) {
                ref readonly var layout = ref m_layout[rowOrdinal];

                if (
                    layout.HostOwned ||
                    !layout.IsStored
                ) {
                    continue;
                }

                hash.Add(value: (ulong)layout.CellCapacity);
                for (var position = 0; (position < HashCellExtent(layout: layout)); position++) {
                    FoldCell(
                        column: column,
                        hash: ref hash,
                        layout: layout,
                        slot: CellSlotAt(layout: layout, position: position)
                    );
                }
            }
        }
    }
    // Every variable-length part folds its own length first, so no two records can present the same run of
    // values by dividing it differently.
    private static void FoldText(ref Fnv1aHash hash, string? text) {
        if (text is null) {
            hash.Add(value: 0UL);

            return;
        }

        hash.Add(value: 1UL);
        hash.Add(value: ((ulong)text.Length));
        hash.Add(value: Fnv1aHash.Compute(values: text.AsSpan()));
    }
    private void FoldCell(ref Fnv1aHash hash, ArenaColumn column, in ArenaRowLayout layout, int slot) {
        switch (column) {
            case ArenaColumn.MemberKey:
                m_keys.AddNameTo(
                    hash: ref hash,
                    ordinal: m_memberKeys[slot]
                );

                break;
            case ArenaColumn.Text:
                FoldText(
                    hash: ref hash,
                    text: m_texts?[slot]
                );

                break;
            case ArenaColumn.Vector:
                hash.Add(value: ((ulong)layout.Dimensions));

                if (layout.Dimensions > 0) {
                    hash.Add(values: MemoryMarshal.Cast<sbyte, byte>(span: VectorSpan(slot: slot)));
                }

                break;
            case ArenaColumn.Visibility:
                StateVisibilityHash.Append(
                    hash: ref hash,
                    visibility: m_visibilities?[slot]
                );

                break;
            case ArenaColumn.Observation:
                if (m_observations?[slot] is { } observation) {
                    hash.Add(value: 1UL);
                    hash.Add(value: observation.Tick);
                    hash.Add(value: (observation.Visible
                        ? 1UL
                        : 0UL
                    ));
                } else {
                    hash.Add(value: 0UL);
                }

                break;
            default:
                hash.Add(value: ReadNumberRaw(
                    column: column,
                    index: slot
                ));

                break;
        }
    }
}
