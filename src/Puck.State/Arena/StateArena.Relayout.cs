namespace Puck.State;

public sealed partial class StateArena {
    /// <summary>Rebuilds the arena over a re-declared row set, carrying every value across by row name and cell
    /// key.</summary>
    /// <param name="catalog">The catalog compiled from <paramref name="section"/>.</param>
    /// <param name="section">The re-declared section.</param>
    /// <param name="time">The clocks a cell born under a value-over-time trait settles to.</param>
    /// <param name="reason">Why the relayout was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the arena now stores <paramref name="catalog"/>.</returns>
    /// <remarks>
    /// A row the new catalog declares keeps the values it held under its old name and keys; a row the new catalog
    /// no longer declares loses them; a row only the new catalog declares holds what the section seeds it with.
    /// A row whose kind or shape changed is refused by name, and a carried value the re-declared row's envelope,
    /// capacity, or symbolic domain will not admit is refused by row and cell name, leaving the arena as it was.
    /// <para>Every value crosses through the same admission door an authored write does, so a relayout cannot land
    /// a value no write could.</para>
    /// <para>The participant and identity lanes cross whole — the roster and each named lane slot's values — since
    /// they belong to the host's session rather than to the document being re-declared.</para>
    /// <para>Three structures are layout-bound and rebuilt rather than carried: the per-row key-to-slot index, the
    /// change-walk scratch, and every span <see cref="TryReadVector"/> has handed out, which aliases storage this
    /// replaces. Cell keys are interned per catalog, so a <see cref="CellKey"/> resolved before a relayout
    /// addresses nothing after it.</para>
    /// <para>Each of the three per-row counters lands above every value any row held before, because the ordinals
    /// they are indexed by have been reassigned.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A row names a topology, space, or capacity the arena cannot lay
    /// out.</exception>
    public bool TryRelayout(StateCatalog catalog, IStateSection? section, in ArenaTime time, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: catalog);

        // A journal position names a column index, and a relayout moves every index, so an open scope could no
        // longer be rewound onto the storage it recorded.
        if (m_journal.Scopes > 0) {
            reason = "the arena has an open journal scope, whose recorded positions are layout-relative, so a relayout waits for the outermost scope to close";

            return false;
        }

        var carried = new List<StateRow>(capacity: m_rows.Count);

        foreach (var row in ToRows()) {
            if (catalog.TryResolve(
                handle: out _,
                lane: StateLane.Document,
                name: row.Name
            )) {
                carried.Add(item: row);
            }
        }

        if (!TryCreate(
            arena: out var rebuilt,
            catalog: catalog,
            options: m_layout.Options,
            reason: out reason,
            section: section,
            time: in time
        )) {
            return false;
        }

        if (!rebuilt.TryLoad(
            reason: out reason,
            rows: carried,
            time: in time
        )) {
            return false;
        }

        CarryLanes(target: rebuilt);
        Adopt(source: rebuilt);

        reason = string.Empty;

        return true;
    }
    /// <summary>Copies this arena's slot lanes — the roster and every named lane slot's values — into a
    /// replacement arena.</summary>
    /// <param name="target">The arena that takes them.</param>
    /// <remarks>The slot lanes belong to the host's session rather than to the document, so a replacement arena
    /// inherits them whether it was reached by a relayout or built fresh. A lane slot the replacement no longer
    /// declares loses its values; one only the replacement declares keeps what it was created with. Both arenas
    /// must be built from the same <see cref="ArenaOptions"/>, or the narrower lane bounds the copy.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
    public void CopyLanesTo(StateArena target) {
        ArgumentNullException.ThrowIfNull(argument: target);

        CarryLanes(target: target);
    }

    // The slot lanes are the host's roster, not the document's, so a re-declared document carries them across
    // whole: both layouts reserve the same roster entries because both are built from one ArenaOptions, and each
    // lane slot's values follow its name.
    private void CarryLanes(StateArena target) {
        m_laneRoster.CopyTo(
            array: target.m_laneRoster,
            index: 0
        );

        for (var rowOrdinal = 0; (rowOrdinal < m_layout.RowCount); rowOrdinal++) {
            ref readonly var from = ref m_layout[rowOrdinal];

            if (from.LaneSlotStart < 0) {
                continue;
            }

            if (!target.m_catalog.TryResolve(
                handle: out var handle,
                lane: from.Lane,
                name: m_catalog.Descriptors[rowOrdinal].Name
            )) {
                continue;
            }

            var to = target.m_layout[handle.Ordinal];

            if (to.LaneSlotStart < 0) {
                continue;
            }

            Array.Copy(
                destinationArray: target.m_laneNumbers,
                destinationIndex: to.LaneSlotStart,
                length: Math.Min(
                    val1: from.LaneCapacity,
                    val2: to.LaneCapacity
                ),
                sourceArray: m_laneNumbers,
                sourceIndex: from.LaneSlotStart
            );
        }
    }
    private void Adopt(StateArena source) {
        var next = 0UL;

        for (var rowOrdinal = 0; (rowOrdinal < m_layout.RowCount); rowOrdinal++) {
            next = Math.Max(
                val1: next,
                val2: Math.Max(
                    val1: m_appendGenerations[rowOrdinal],
                    val2: Math.Max(
                        val1: m_rowGenerations[rowOrdinal],
                        val2: m_rowVersions[rowOrdinal]
                    )
                )
            );
        }

        next++;

        m_appendGenerations = source.m_appendGenerations;
        m_behaviors = source.m_behaviors;
        m_catalog = source.m_catalog;
        m_clockEpochEngineTicks = source.m_clockEpochEngineTicks;
        m_clockEpochTicks = source.m_clockEpochTicks;
        m_clockSubstepTicks = source.m_clockSubstepTicks;
        m_clockV0 = source.m_clockV0;
        m_clockY0 = source.m_clockY0;
        m_drawCursors = source.m_drawCursors;
        m_historyCursors = source.m_historyCursors;
        m_laneNumbers = source.m_laneNumbers;
        m_laneRoster = source.m_laneRoster;
        m_layout = source.m_layout;
        m_maskWords = source.m_maskWords;
        m_memberCounts = source.m_memberCounts;
        m_memberKeys = source.m_memberKeys;
        m_numbers = source.m_numbers;
        m_observations = source.m_observations;
        m_phaseSequences = source.m_phaseSequences;
        m_presence = source.m_presence;
        m_clockSet = source.m_clockSet;
        m_provenance = source.m_provenance;
        m_reindexRow = source.m_reindexRow;
        m_reindexStamp = source.m_reindexStamp;
        m_rowChangeStamp = source.m_rowChangeStamp;
        m_rowGenerations = source.m_rowGenerations;
        m_rowVersions = source.m_rowVersions;
        m_rows = source.m_rows;
        m_slotOfKey = source.m_slotOfKey;
        m_texts = source.m_texts;
        m_vectors = source.m_vectors;
        m_visibilities = source.m_visibilities;
        m_declarationVisibilityBytes = source.m_declarationVisibilityBytes;
        m_visibilityBytes = source.m_visibilityBytes;

        m_changeDiffers = [];
        m_changeStamp = [];
        m_reindexCount = 0;
        m_touchedCount = 0;

        Array.Clear(array: m_reindexStamp);
        Array.Clear(array: m_rowChangeStamp);
        Array.Fill(
            array: m_appendGenerations,
            value: next
        );
        Array.Fill(
            array: m_rowGenerations,
            value: next
        );
        Array.Fill(
            array: m_rowVersions,
            value: next
        );
    }
}
