using System.Diagnostics.CodeAnalysis;

namespace Puck.State;

/// <summary>
/// The columnar runtime store for every cell of every lane: one contiguous column per cell kind per shape per
/// lane, a column for every runtime-state field a row or cell carries, nested journal scopes, and the three
/// per-row counters a scheduler, a cache, and a pattern memo each read for a different question.
/// </summary>
/// <remarks>
/// <para>Three counters answer three questions and are never interchangeable. <see cref="RowVersion"/> moves only
/// when a commit leaves the row's bytes different from what they were when the outermost scope opened, so an
/// unchanged version proves the row's content unchanged — change detection. <see cref="RowGeneration"/> moves on
/// every mutation, including a no-op write and a rewind, so anything caching a derived answer over the row's
/// storage invalidates on it. <see cref="AppendGeneration"/>, on an ordered row, moves on every mutation except a
/// push at the tail, so a walk memoized over the row's prefix is valid exactly while it has not moved.</para>
/// <para>A host-owned row has a descriptor and no columns: every read answers <see langword="false"/> and every
/// write refuses by name, because its facet owns it.</para>
/// <para>Every value entering a row decides through <see cref="StateRow.TryAdmitWrite"/>, the one admission door:
/// a write, a ring push, a mint, the landing of a transfer, an imported row, and the authored section a
/// construction seeds from all carry their operand through it, against the same envelope, overflow policy, and
/// symbolic domain. Construction is a load of the section's own rows, so a value the document could not write is
/// refused by row and cell name rather than stored where nothing can reach or re-import it. Stored values then
/// reach the derived boards a row feeds through one internal recompute, so no path can skip either.</para>
/// </remarks>
public sealed partial class StateArena {
    private ulong[] m_appendGenerations;
    private StateCatalog m_catalog;
    private CellKeyTable m_keys;

    private int[] m_keyMarks = new int[16];
    private long m_importVisibilityBytes = -1L;

    private readonly ArenaJournal m_journal;

    private ArenaLayout m_layout;

    private sbyte[] m_carried = [];
    private int[] m_reorderAt = [];
    private int[] m_reorderWhich = [];

    private IReadOnlyList<StateRow> m_rows;
    private ulong[] m_rowGenerations;
    private ulong[] m_rowVersions;
    private ArenaSlotMap[] m_slotOfKey;
    private int[] m_poolOfDomainRow;
    private ulong[][] m_poolOccupancy;
    private ArenaPoolAddress[] m_poolAddresses;
    private long[] m_numbers;
    private ulong[] m_presence;
    private ulong[] m_clockSet;
    private int[] m_memberKeys;
    private long[] m_memberCounts;
    private long[] m_historyCursors;
    private long[] m_laneNumbers;
    private ulong[] m_laneRoster;
    private sbyte[] m_vectors;
    private long[]? m_clockEpochEngineTicks;
    private long[]? m_clockEpochTicks;
    private long[]? m_clockSubstepTicks;
    private long[]? m_clockV0;
    private long[]? m_clockY0;
    private long[]? m_drawCursors;
    private long[]? m_maskWords;
    private long[]? m_phaseSequences;
    private byte[]? m_behaviors;
    private StateObservation?[]? m_observations;
    private StateVisibility?[]? m_visibilities;
    private long m_declarationVisibilityBytes;
    private long m_visibilityBytes;
    private string?[]? m_provenance;
    private string?[]? m_texts;

    private bool[] m_changeDiffers = [];

    private long m_changeEpoch;

    private long[] m_changeStamp = [];

    private long[] m_rowChangeStamp;
    private int[] m_reindexRow;
    private long[] m_reindexStamp;
    private int m_reindexCount;
    private long m_reindexEpoch;

    private byte[] m_touchedColumn = new byte[64];

    private int m_touchedCount;

    private int[] m_touchedIndex = new int[64];
    private int[] m_touchedSlot = new int[64];

    /// <summary>Initializes an arena over one compiled catalog, seeded with the section's authored values.</summary>
    /// <param name="catalog">The compiled catalog the arena stores.</param>
    /// <param name="section">The authored section the catalog was compiled from.</param>
    /// <param name="options">The lane widths to build, or <see langword="null"/> for the defaults.</param>
    /// <param name="time">The clocks a cell born under a value-over-time trait settles to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The section carries a value the arena refuses, named by row and
    /// cell.</exception>
    /// <exception cref="InvalidOperationException">A row names a topology, space, or capacity the arena cannot lay
    /// out.</exception>
    public StateArena(StateCatalog catalog, IStateSection? section, ArenaTime time, ArenaOptions? options = null)
        : this(
        catalog: catalog,
        layout: ArenaLayout.Build(
            catalog: catalog,
            options: options,
            section: section
        ),
        section: section,
        time: time
    ) { }
    /// <summary>Initializes an arena over one compiled catalog and an already-built layout.</summary>
    /// <param name="catalog">The compiled catalog the arena stores.</param>
    /// <param name="layout">The column plan to store into.</param>
    /// <param name="section">The authored section the catalog was compiled from.</param>
    /// <param name="time">The clocks a cell born under a value-over-time trait settles to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> or <paramref name="layout"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The section carries a value the arena refuses, named by row and
    /// cell.</exception>
    public StateArena(StateCatalog catalog, ArenaLayout layout, IStateSection? section, ArenaTime time)
        : this(
        catalog: catalog,
        layout: layout,
        reason: out var refusal,
        section: section,
        time: time
    ) {
        if (refusal.Length != 0) {
            throw new ArgumentException(
                message: refusal,
                paramName: nameof(section)
            );
        }
    }

    private StateArena(StateCatalog catalog, ArenaLayout layout, IStateSection? section, in ArenaTime time, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: catalog);
        ArgumentNullException.ThrowIfNull(argument: layout);

        m_appendGenerations = new ulong[layout.RowCount];
        m_catalog = catalog;
        m_keys = catalog.Keys.Fork();
        m_historyCursors = new long[layout.RowCount];
        m_journal = new ArenaJournal();
        m_laneNumbers = new long[layout.LaneSlotCount];
        m_laneRoster = new ulong[((layout.LaneRosterCount + 63) / 64)];
        m_layout = layout;
        m_memberCounts = new long[layout.RowCount];
        m_memberKeys = new int[layout.CellSlotCount];
        m_numbers = new long[layout.CellSlotCount];
        m_presence = new ulong[((layout.CellSlotCount + 63) / 64)];
        m_clockSet = new ulong[((layout.CellSlotCount + 63) / 64)];
        m_reindexRow = new int[layout.RowCount];
        m_reindexStamp = new long[layout.RowCount];
        m_rowChangeStamp = new long[layout.RowCount];
        m_rowGenerations = new ulong[layout.RowCount];
        m_rowVersions = new ulong[layout.RowCount];
        m_rows = NormalizeRows(
            bytes: out m_declarationVisibilityBytes,
            reason: out reason,
            rows: StateCatalog.ExpandRows(section: section)
        );
        m_slotOfKey = new ArenaSlotMap[layout.RowCount];
        if (catalog.Pools.Count == 0) {
            m_poolOfDomainRow = [];
            m_poolOccupancy = [];
            m_poolAddresses = [];
        } else {
            m_poolOfDomainRow = new int[layout.RowCount];
            Array.Fill(array: m_poolOfDomainRow, value: -1);
            m_poolOccupancy = new ulong[catalog.Pools.Count][];
            foreach (var pool in catalog.Pools) {
                m_poolOfDomainRow[pool.DomainRowOrdinal] = pool.Ordinal;
                m_poolOccupancy[pool.Ordinal] = new ulong[((pool.Capacity + 63) / 64)];
            }
            m_poolAddresses = BuildPoolAddresses(catalog: catalog, layout: layout, occupancy: m_poolOccupancy);
        }
        m_vectors = new sbyte[layout.VectorByteCount];
        m_keys.SetByteBudget(budget: KeyByteBudget);

        if (reason.Length != 0) {
            return;
        }

        if (!TryValidateDerivedDomains(
            catalog: m_catalog,
            layout: m_layout,
            reason: out reason,
            rows: m_rows
        )) {
            return;
        }

        Array.Fill(
            array: m_memberKeys,
            value: -1
        );

        for (var ordinal = 0; (ordinal < layout.RowCount); ordinal++) {
            m_slotOfKey[ordinal] = new ArenaSlotMap();
        }

        m_poolMutationDepth++;
        var loaded = TryLoad(
            reason: out reason,
            rows: m_rows,
            time: in time
        );

        m_poolMutationDepth--;
        if (loaded) {
            // Construction is not a mutation, so nothing may look changed to a scheduler reading the arena for
            // the first time.
            Array.Clear(array: m_appendGenerations);
            Array.Clear(array: m_rowChangeStamp);
            Array.Clear(array: m_rowGenerations);
            Array.Clear(array: m_rowVersions);
        }
    }

    private static bool TryValidateDerivedDomains(StateCatalog catalog, ArenaLayout layout, IReadOnlyList<StateRow> rows, out string reason) {
        for (var boardOrdinal = 0; (boardOrdinal < layout.RowCount); boardOrdinal++) {
            ref readonly var boardLayout = ref layout[boardOrdinal];

            if (
                !boardLayout.IsDerivedBoard ||
                (catalog.Descriptors[boardOrdinal].Lane != StateLane.Document)
            ) {
                continue;
            }

            var board = rows[catalog.Descriptors[boardOrdinal].LaneOrdinal];
            var codesOrdinal = boardLayout.InverseCodesOrdinal;

            if (
                (codesOrdinal < 0) ||
                (codesOrdinal >= catalog.Descriptors.Count) ||
                (catalog.Descriptors[codesOrdinal].Lane != StateLane.Document)
            ) {
                reason = $"derived board '{board.Name.Value}' names an inverse codes row outside the document lane";

                return false;
            }

            var codes = rows[catalog.Descriptors[codesOrdinal].LaneOrdinal];

            _ = catalog.TryGetEnum(
                handle: catalog.Descriptors[boardOrdinal].Handle,
                symbols: out var boardSymbols
            );
            _ = catalog.TryGetEnum(
                handle: catalog.Descriptors[codesOrdinal].Handle,
                symbols: out var codeSymbols
            );

            if (!StateRow.TryProveDerivedDomain(
                board: board,
                boardSymbols: boardSymbols,
                codeSymbols: codeSymbols,
                codes: codes,
                reason: out reason
            )) {
                return false;
            }
        }

        reason = string.Empty;

        return true;
    }

    /// <summary>Creates an arena over one compiled catalog, refusing an inadmissible section by row and cell name
    /// rather than throwing.</summary>
    /// <param name="catalog">The compiled catalog the arena stores.</param>
    /// <param name="section">The authored section the catalog was compiled from.</param>
    /// <param name="options">The lane widths to build, or <see langword="null"/> for the defaults.</param>
    /// <param name="time">The clocks a cell born under a value-over-time trait settles to.</param>
    /// <param name="arena">The seeded arena on success; otherwise <see langword="null"/>.</param>
    /// <param name="reason">Why the section was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the arena was built and seeded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A row names a topology, space, or capacity the arena cannot lay
    /// out.</exception>
    public static bool TryCreate(StateCatalog catalog, IStateSection? section, ArenaOptions? options, in ArenaTime time, [NotNullWhen(true)] out StateArena? arena, out string reason) {
        var built = new StateArena(
            catalog: catalog,
            layout: ArenaLayout.Build(
                catalog: catalog,
                options: options,
                section: section
            ),
            reason: out reason,
            section: section,
            time: in time
        );

        arena = ((reason.Length == 0)
            ? built
            : null
        );

        return (arena is not null);
    }
    /// <summary>Measures the bounded visibility payload an arena seeded from a section retains in its immutable
    /// declaration snapshot and live cell columns, without constructing the arena.</summary>
    /// <param name="section">The section to measure.</param>
    /// <param name="bytes">The retained visibility bytes on success.</param>
    /// <param name="reason">Why a row or cell visibility exceeds its count or length limit, or empty on success.</param>
    /// <returns><see langword="true"/> when every visibility is bounded.</returns>
    /// <remarks>A cell visibility is charged twice because the declaration snapshot and live column each retain it.
    /// A row visibility is declaration metadata and is charged once.</remarks>
    public static bool TryMeasureVisibility(IStateSection? section, out long bytes, out string reason) {
        bytes = 0L;

        foreach (var row in StateCatalog.ExpandRows(section: section)) {
            if (row is null) {
                continue;
            }
            if (!StateVisibilityStorage.TryMeasure(
                bytes: out var rowBytes,
                reason: out reason,
                value: row.Visibility
            )) {
                reason = $"row '{row.Name.Value}' {reason}";
                return false;
            }

            bytes += rowBytes;

            foreach (var cell in (row.Cells ?? [])) {
                if (cell is null) {
                    continue;
                }
                if (!StateVisibilityStorage.TryMeasure(
                    bytes: out var cellBytes,
                    reason: out reason,
                    value: cell.Visibility
                )) {
                    reason = $"row '{row.Name.Value}' cell '{cell.Key.Value}' {reason}";
                    return false;
                }

                bytes += (2L * cellBytes);
            }
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>Gets the catalog whose descriptors this arena stores.</summary>
    public StateCatalog Catalog => m_catalog;
    /// <summary>Gets this arena's key table. Compiled catalog symbols remain readable; runtime additions belong
    /// only to this arena and speculative additions are released on rewind.</summary>
    public CellKeyTable Keys => m_keys;

    /// <summary>Gets the working storage an evaluation over this arena borrows instead of sizing a buffer from the
    /// document on the stack.</summary>
    public ArenaScratch Scratch { get; } = new();

    /// <summary>Gets the undo journal this arena's scopes record through.</summary>
    public ArenaJournal Journal => m_journal;
    /// <summary>Gets the column plan this arena stores into.</summary>
    public ArenaLayout Layout => m_layout;
    /// <summary>Gets the bytes reserved by the layout plus the bounded visibility and key storage the arena
    /// currently retains.</summary>
    public long Bytes => (((m_layout.Bytes + m_declarationVisibilityBytes) + m_visibilityBytes) + m_keys.Bytes);

    private long KeyByteBudget() => (((ArenaCapacity.MaxBytes - m_layout.Bytes) - m_declarationVisibilityBytes) - ((m_importVisibilityBytes >= 0L) ? m_importVisibilityBytes : m_visibilityBytes));

    /// <summary>Gets the authored rows the document lane was seeded from, with detached cell collections and
    /// normalized visibility policies.</summary>
    public IReadOnlyList<StateRow> Rows => m_rows;

    private static IReadOnlyList<StateRow> NormalizeRows(IReadOnlyList<StateRow>? rows, out long bytes, out string reason) {
        var source = (rows ?? []);
        var normalized = new StateRow[source.Count];

        bytes = 0L;

        for (var rowIndex = 0; (rowIndex < source.Count); rowIndex++) {
            var row = source[rowIndex];

            if (row is null) {
                normalized[rowIndex] = null!;
                continue;
            }
            if (!StateVisibilityStorage.TryNormalize(
                bytes: out var rowBytes,
                normalized: out var rowVisibility,
                reason: out reason,
                value: row.Visibility
            )) {
                reason = $"row '{row.Name.Value}' {reason}";
                return [];
            }

            bytes += rowBytes;

            var cells = row.Cells;
            var copiedCells = ((cells is null) ? null : new StateCell[cells.Count]);
            // A caller may hand the record any IReadOnlyList implementation. Detach every non-null cell collection
            // even when its current visibility values need no normalization, so later list mutation cannot change
            // what the arena's declaration snapshot retains.
            var changed = ((cells is not null) || !ReferenceEquals(objA: row.Visibility, objB: rowVisibility));

            for (var cellIndex = 0; (cellIndex < (cells?.Count ?? 0)); cellIndex++) {
                var cell = cells![cellIndex];

                if (cell is null) {
                    copiedCells![cellIndex] = null!;
                    continue;
                }
                if (!StateVisibilityStorage.TryNormalize(
                    bytes: out var cellBytes,
                    normalized: out var cellVisibility,
                    reason: out reason,
                    value: cell.Visibility
                )) {
                    reason = $"row '{row.Name.Value}' cell '{cell.Key.Value}' {reason}";
                    return [];
                }

                bytes += cellBytes;
                changed |= !ReferenceEquals(objA: cell.Visibility, objB: cellVisibility);
                copiedCells![cellIndex] = (ReferenceEquals(objA: cell.Visibility, objB: cellVisibility)
                    ? cell
                    : (cell with { Visibility = cellVisibility })
                );
            }

            normalized[rowIndex] = (changed
                ? (row with {
                    Cells = ((copiedCells is null) ? null : Array.AsReadOnly(array: copiedCells)),
                    Visibility = rowVisibility,
                })
                : row
            );
        }

        reason = string.Empty;
        return Array.AsReadOnly(array: normalized);
    }

    /// <summary>Returns an ordered row's append generation: a counter that moves on every mutation of the row
    /// except a push at its tail, so a walk memoized over the row's prefix is valid exactly while it has not
    /// moved.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <returns>The append generation.</returns>
    public ulong AppendGeneration(int rowOrdinal) => m_appendGenerations[rowOrdinal];
    /// <summary>Opens a journal scope: every write until the matching <see cref="Commit"/> or <see cref="Rewind"/>
    /// records the column position it overwrote. Scopes nest; close the innermost first.</summary>
    /// <returns>The mark the scope closes with.</returns>
    public int BeginScope() {
        var depth = m_journal.Scopes;

        if (depth == m_keyMarks.Length) {
            Array.Resize(array: ref m_keyMarks, newSize: (depth * 2));
        }
        m_keyMarks[depth] = m_keys.Count;
        return m_journal.BeginScope();
    }
    /// <summary>Closes the innermost open scope, keeping its writes. Closing the outermost one settles
    /// <see cref="RowVersion"/>: a row whose bytes differ from what they held when that scope opened moves its
    /// version, and a row written back to what it held does not.</summary>
    /// <param name="mark">The mark <see cref="BeginScope"/> returned.</param>
    /// <exception cref="InvalidOperationException">No scope is open, or <paramref name="mark"/> does not close the
    /// innermost one.</exception>
    public void Commit(int mark) {
        m_journal.EnsureCloses(mark: mark);

        if (m_journal.Scopes == 1) {
            SettleVersions(mark: mark);
            RetainCommittedEntries(mark: mark);
        }

        m_journal.CommitScope(mark: mark);
    }
    /// <summary>Returns a row's generation: a counter that moves on every mutation of the row, including a no-op
    /// write and a rewind, so anything caching a derived answer over the row's storage invalidates on it.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <returns>The generation.</returns>
    public ulong RowGeneration(int rowOrdinal) => m_rowGenerations[rowOrdinal];
    /// <summary>Returns a row's version: a counter that moves only when a commit leaves the row's bytes different
    /// from what they held when the outermost scope opened, so two reads taken at the same version prove the row's
    /// content unchanged.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <returns>The version.</returns>
    public ulong RowVersion(int rowOrdinal) => m_rowVersions[rowOrdinal];
    /// <summary>Flags every row the open scopes have written. <see cref="RowVersion"/> settles when the outermost
    /// scope commits, so until then this is what says which rows may differ from what it last proved.</summary>
    /// <param name="rows">One flag per catalog ordinal; a written row's flag is set and no flag is cleared.</param>
    /// <exception cref="ArgumentException"><paramref name="rows"/> is shorter than the layout's row count.</exception>
    public void FlagOpenRows(Span<bool> rows) {
        if (rows.Length < m_layout.RowCount) {
            throw new ArgumentException(
                message: $"the flags cover {rows.Length} rows and the layout declares {m_layout.RowCount}.",
                paramName: nameof(rows)
            );
        }

        for (var index = 0; (index < m_journal.Length); index++) {
            var entry = m_journal[index];

            if (entry.Column == ArenaColumn.LaneRoster) {
                var lane = LaneOfRoster(rosterIndex: entry.Index);

                rows.Slice(
                    length: lane.Count,
                    start: lane.FirstOrdinal
                ).Fill(value: true);

                continue;
            }

            var row = m_layout.RowOf(
                column: entry.Column,
                index: entry.Index
            );

            if (row >= 0) {
                rows[row] = true;
            }
        }
    }
    /// <summary>Closes the innermost open scope, restoring every column position it wrote to what it overwrote,
    /// most recent write first, and releasing keys first interned within the scope.</summary>
    /// <param name="mark">The mark <see cref="BeginScope"/> returned.</param>
    /// <exception cref="InvalidOperationException">No scope is open, or <paramref name="mark"/> does not close the
    /// innermost one.</exception>
    public void Rewind(int mark) {
        m_journal.EnsureCloses(mark: mark);

        var epoch = ++m_reindexEpoch;

        m_reindexCount = 0;

        for (var index = (m_journal.Length - 1); (index >= mark); index--) {
            var entry = m_journal[index];

            Restore(entry: entry);
            BumpGeneration(
                column: entry.Column,
                index: entry.Index,
                tailPush: false
            );
            MarkReindex(
                column: entry.Column,
                epoch: epoch,
                index: entry.Index
            );
        }

        // The key-to-slot index is derived from the membership columns rather than journalled, so every row whose
        // membership the rewind restored rebuilds it from the restored keys.
        for (var index = 0; (index < m_reindexCount); index++) {
            var rowOrdinal = m_reindexRow[index];

            Reindex(
                layout: m_layout[rowOrdinal],
                rowOrdinal: rowOrdinal
            );
        }

        m_reindexCount = 0;
        if (m_keys.Count != m_keyMarks[(m_journal.Scopes - 1)]) {
            // Topology and ring addresses may have been cached without a membership journal entry. Drop those
            // derived caches before an abandoned key ordinal can be reused for another name.
            for (var row = 0; (row < m_layout.RowCount); row++) {
                if (m_layout[row].Shape is (RowShape.Lattice or RowShape.Ring)) {
                    m_slotOfKey[row].Clear();
                }
            }
        }
        m_keys.Rewind(count: m_keyMarks[(m_journal.Scopes - 1)]);
        m_journal.RewindScope(mark: mark);
    }

    private void AddTouched(int slot, ArenaColumn column, int index) {
        if (m_touchedCount == m_touchedSlot.Length) {
            var size = (m_touchedSlot.Length * 2);

            Array.Resize(
                array: ref m_touchedColumn,
                newSize: size
            );
            Array.Resize(
                array: ref m_touchedIndex,
                newSize: size
            );
            Array.Resize(
                array: ref m_touchedSlot,
                newSize: size
            );
        }

        m_touchedColumn[m_touchedCount] = ((byte)column);
        m_touchedIndex[m_touchedCount] = index;
        m_touchedSlot[m_touchedCount] = slot;
        m_touchedCount++;
    }
    private void MarkReindex(ArenaColumn column, long epoch, int index) {
        if (column is not (ArenaColumn.MemberKey or ArenaColumn.MemberCount)) {
            return;
        }

        var row = m_layout.RowOf(
            column: column,
            index: index
        );

        if (
            (row < 0) ||
            m_catalog.IsPoolRow(rowOrdinal: row) ||
            (m_reindexStamp[row] == epoch)
        ) {
            return;
        }

        m_reindexStamp[row] = epoch;
        m_reindexRow[m_reindexCount] = row;
        m_reindexCount++;
    }
    private void BumpGeneration(ArenaColumn column, int index, bool tailPush) {
        // A roster bit belongs to a whole lane rather than to one row: joining or leaving changes what every slot
        // of that lane answers, so every one of its descriptors moves.
        if (column == ArenaColumn.LaneRoster) {
            var lane = LaneOfRoster(rosterIndex: index);

            for (var ordinal = lane.FirstOrdinal; (ordinal < (lane.FirstOrdinal + lane.Count)); ordinal++) {
                m_rowGenerations[ordinal]++;
            }

            return;
        }

        var row = m_layout.RowOf(
            column: column,
            index: index
        );

        if (row < 0) {
            return;
        }

        m_rowGenerations[row]++;

        if (
            m_layout[row].IsOrdered &&
            !tailPush
        ) {
            m_appendGenerations[row]++;
        }
    }
    private void MarkVersion(ArenaColumn column, int index) {
        if (column == ArenaColumn.LaneRoster) {
            var lane = LaneOfRoster(rosterIndex: index);

            for (var ordinal = lane.FirstOrdinal; (ordinal < (lane.FirstOrdinal + lane.Count)); ordinal++) {
                MarkRowVersion(rowOrdinal: ordinal);
            }

            return;
        }

        var row = m_layout.RowOf(
            column: column,
            index: index
        );

        if (row >= 0) {
            MarkRowVersion(rowOrdinal: row);
        }
    }
    private void MarkRowVersion(int rowOrdinal) {
        if (m_rowChangeStamp[rowOrdinal] != m_changeEpoch) {
            m_rowChangeStamp[rowOrdinal] = m_changeEpoch;
            m_rowVersions[rowOrdinal]++;
        }
    }
    // Reverse-walks the closing scope so the last entry seen for a position is the first write to it, whose
    // recorded value is therefore what the position held when the scope opened.
    private void SettleVersions(int mark) {
        if (m_journal.Length == mark) {
            return;
        }

        if (m_changeStamp.Length < m_layout.ChangeSlotCount) {
            m_changeDiffers = new bool[m_layout.ChangeSlotCount];
            m_changeStamp = new long[m_layout.ChangeSlotCount];
        }

        var epoch = ++m_changeEpoch;

        m_touchedCount = 0;

        for (var index = (m_journal.Length - 1); (index >= mark); index--) {
            var entry = m_journal[index];
            var slot = (m_layout.ChangeBase(column: entry.Column) + entry.Index);

            if (m_changeStamp[slot] != epoch) {
                m_changeStamp[slot] = epoch;

                AddTouched(
                    column: entry.Column,
                    index: entry.Index,
                    slot: slot
                );
            }

            m_changeDiffers[slot] = Differs(entry: entry);
        }

        for (var index = 0; (index < m_touchedCount); index++) {
            if (m_changeDiffers[m_touchedSlot[index]]) {
                MarkVersion(
                    column: ((ArenaColumn)m_touchedColumn[index]),
                    index: m_touchedIndex[index]
                );
            }
        }

        m_touchedCount = 0;
    }
}
