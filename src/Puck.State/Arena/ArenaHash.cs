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
    // segment, valid until any of its rows refolds (a refold through ComputeHash or a standalone RowDigest read
    // alike). Both belong to one layout and one key table: a relayout, reservation or adoption replaces the layout and
    // a restore replaces the key table, and either drops the cache. The arena is not thread-safe, and the cache
    // makes that sharper: a fold taken beside a write would store the old bytes under the new version for good, so a
    // reader that takes the cache excludes every writer (a world host hashes under its authority gate).
    private ulong[] m_rowDigests = [];
    private ulong[] m_rowDigestVersions = [];
    private bool[] m_rowDigestKnown = [];
    private ulong[] m_segmentDigests = [];
    private bool[] m_segmentDigestKnown = [];
    private (ArenaColumn Column, int Index)[] m_unownedPositions = [];
    private ArenaLayout? m_digestLayout;
    private CellKeyTable? m_digestKeys;
    // The next row and segment CheckDigests samples.
    private int m_checkRow;
    private int m_checkSegment;

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
    /// <see cref="RecomputeHash"/> is the same value with no cache at all, and <see cref="CheckDigests"/> samples
    /// the cache against it a row and a segment at a time.</para>
    /// <para>The caller excludes every writer for the duration: the arena is not thread-safe, and a hash taken beside
    /// a write can cache the old bytes under the new version.</para>
    /// <para>A host-owned row folds as a fixed mark: it has no columns here, and its facet hashes what it
    /// serves. Each segment folds its rows' digests in ordinal order, so a row's position is framed there rather than
    /// in its digest.</para>
    /// <para>Members fold their names in slot order, independently of intern ordinals. The retained key ledger
    /// folds its count and order-independent digest sum because orphan names affect future mint admission.</para>
    /// </remarks>
    public ulong ComputeHash() => Digest(cached: (m_journal.Scopes == 0));
    /// <summary>Computes <see cref="ComputeHash"/>'s value with every row folded afresh, reading and writing no
    /// cached digest: the full recompute an incremental digest is checked against.</summary>
    /// <returns>The hash value.</returns>
    public ulong RecomputeHash() => Digest(cached: false);
    /// <summary>Returns one row's digest: <see cref="AddRowTo"/> folded into a fresh hash, cached by the row's
    /// <see cref="RowVersion"/> outside an open scope. A refold invalidates the row's segment digest.</summary>
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
        PrepareDigestCache();
        return CachedRowDigest(rowOrdinal: rowOrdinal);
    }
    /// <summary>Samples the digest cache against the stored bytes, right after a <see cref="ComputeHash"/>: refolds
    /// the next row in rotation and compares it with its cached digest and version, and refolds the next segment in
    /// rotation from its rows' cached digests and compares it with the segment's own. A mismatch means a write moved
    /// bytes without moving the version the cache trusts, a fold left a moved row unrefolded, or a refold left its
    /// segment stale: the arena drops the whole cache, so the next <see cref="ComputeHash"/> folds afresh, and names
    /// what disagreed.</summary>
    /// <returns><see langword="null"/> when both samples agree or nothing was cached; otherwise the disagreement.</returns>
    /// <remarks>The work is one row's fold and at most <see cref="HashSegmentRows"/> cached digests; every row and
    /// segment is sampled once per pass of the rotation. The caller excludes writers, as for
    /// <see cref="ComputeHash"/>.</remarks>
    public string? CheckDigests() {
        if ((m_journal.Scopes != 0) || !ReferenceEquals(objA: m_digestLayout, objB: m_layout) || !ReferenceEquals(objA: m_digestKeys, objB: m_keys)) { return null; }
        var rows = m_layout.RowCount;

        if (rows == 0) { return null; }
        var row = (m_checkRow % rows);
        var segment = (m_checkSegment % m_segmentDigests.Length);
        string? fault = null;

        m_checkRow = (row + 1);
        m_checkSegment = (segment + 1);
        if (m_rowDigestKnown[row]) {
            var bytes = FreshRowDigest(rowOrdinal: row);

            if (m_rowDigestVersions[row] != m_rowVersions[row]) {
                fault = $"row '{RowName(rowOrdinal: row)}' stands at version {m_rowVersions[row]} after a fold that cached it at version {m_rowDigestVersions[row]}";
            } else if (bytes != m_rowDigests[row]) {
                fault = $"row '{RowName(rowOrdinal: row)}' at version {m_rowVersions[row]} caches digest {m_rowDigests[row]:x16} and its bytes fold {bytes:x16}";
            }
        }
        if ((fault is null) && m_segmentDigestKnown[segment]) {
            var children = SegmentOfCachedRows(segment: segment);

            if (children != m_segmentDigests[segment]) {
                fault = $"segment {segment} caches digest {m_segmentDigests[segment]:x16} and its rows' cached digests fold {children:x16}";
            }
        }
        if (fault is not null) { m_digestLayout = null; }
        return fault;
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
    // The one place a row digest enters the cache, so the segment that folded the old one is invalidated with it.
    private ulong CachedRowDigest(int rowOrdinal) {
        var version = m_rowVersions[rowOrdinal];

        if (m_rowDigestKnown[rowOrdinal] && (m_rowDigestVersions[rowOrdinal] == version)) {
            return m_rowDigests[rowOrdinal];
        }
        var digest = FreshRowDigest(rowOrdinal: rowOrdinal);

        RowDigestFolds++;
        m_rowDigests[rowOrdinal] = digest;
        m_rowDigestVersions[rowOrdinal] = version;
        m_rowDigestKnown[rowOrdinal] = true;
        m_segmentDigestKnown[(rowOrdinal / HashSegmentRows)] = false;
        return digest;
    }
    private ulong CachedSegmentDigest(int segment) {
        var first = (segment * HashSegmentRows);
        var end = Math.Min(val1: (first + HashSegmentRows), val2: m_layout.RowCount);

        for (var row = first; (row < end); row++) { _ = CachedRowDigest(rowOrdinal: row); }
        if (m_segmentDigestKnown[segment]) { return m_segmentDigests[segment]; }
        var digest = SegmentOfCachedRows(segment: segment);

        m_segmentDigests[segment] = digest;
        m_segmentDigestKnown[segment] = true;
        return digest;
    }
    private ulong SegmentOfCachedRows(int segment) {
        var first = (segment * HashSegmentRows);
        var end = Math.Min(val1: (first + HashSegmentRows), val2: m_layout.RowCount);
        var hash = Fnv1aHash.Create();

        for (var row = first; (row < end); row++) { hash.Add(value: m_rowDigests[row]); }
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
    /// <para>The fold is independent of the row's catalog ordinal, so a row keeps its fold across a re-declared
    /// catalog that moves it. A host-owned row stores nothing here and folds as one fixed mark; its facet hashes what
    /// it serves. The lane roster belongs to a lane rather than a row and is not folded.</para>
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

        // Each column's own ordinal frames the row's runs, so two adjacent columns cannot present the same values by
        // dividing them differently. The row's ordinal is not folded: a row's fold is its content wherever the catalog
        // places it, so a scope's recorded row digests survive a declaration edit that moves the row's ordinal, and a
        // caller folding several rows frames them by the order it folds them in.
        if (layout.HostOwned) {
            hash.Add(value: ulong.MaxValue);
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
    private void FoldCell(ref Fnv1aHash hash, ArenaColumn column, in ArenaRowLayout layout, int slot) {
        switch (column) {
            case ArenaColumn.MemberKey:
                m_keys.AddNameTo(
                    hash: ref hash,
                    ordinal: m_memberKeys[slot]
                );

                break;
            case ArenaColumn.Text:
                // Fnv1aHash folds a string's length (or null's mark) first, so no two records present one run of
                // values by dividing it differently.
                hash.Add(value: m_texts?[slot]);

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
