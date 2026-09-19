namespace Puck.State;

public sealed partial class StateArena {
    /// <summary>Returns a draw site's cursor: how many samples it has ever consumed.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <returns>The draw cursor.</returns>
    public long DrawCursor(int rowOrdinal) => ((m_drawCursors is null)
        ? 0L
        : m_drawCursors[rowOrdinal]
    );
    /// <summary>Returns a ring row's history cursor: how many values have ever been pushed into it.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <returns>The history cursor.</returns>
    public long HistoryCursor(int rowOrdinal) => m_historyCursors[rowOrdinal];
    /// <summary>Returns a phase row's persisted progression sequence.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <returns>The phase sequence.</returns>
    public long PhaseSequence(int rowOrdinal) => ((m_phaseSequences is null)
        ? 0L
        : m_phaseSequences[rowOrdinal]
    );
    /// <summary>Reads one drawn mask of a draw site.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="index">The mask's context ordinal.</param>
    /// <returns>The mask, or the empty set when the site carries none there or the ordinal names no row.</returns>
    public ClosedBitset256 DrawnMask(int rowOrdinal, int index) {
        if (((uint)rowOrdinal) >= ((uint)m_layout.RowCount)) {
            return default;
        }

        ref readonly var layout = ref m_layout[rowOrdinal];

        if (
            (layout.MaskWordStart < 0) ||
            (((uint)index) >= ((uint)layout.MaskCount)) ||
            (m_maskWords is null)
        ) {
            return default;
        }

        var word = (layout.MaskWordStart + (index * 4));

        return new ClosedBitset256(
            Word0: ((ulong)m_maskWords[word]),
            Word1: ((ulong)m_maskWords[(word + 1)]),
            Word2: ((ulong)m_maskWords[(word + 2)]),
            Word3: ((ulong)m_maskWords[(word + 3)])
        );
    }
    /// <summary>Reads one cell's <see cref="StateCellBehavior"/> override.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The cell key.</param>
    /// <returns>The override, or <see cref="StateCellBehavior.Inherit"/> when the cell declares none.</returns>
    public StateCellBehavior Behavior(int rowOrdinal, CellKey key) => ((TryCellSlot(
        key: key,
        rowOrdinal: rowOrdinal,
        slot: out var slot
    ) && (m_behaviors is not null))
        ? ((StateCellBehavior)m_behaviors[slot])
        : StateCellBehavior.Inherit
    );
    /// <summary>Reads one cell's persisted last-seen stamp.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The cell key.</param>
    /// <returns>The stamp, or <see langword="null"/> when the cell carries none.</returns>
    public StateObservation? Observation(int rowOrdinal, CellKey key) => ((TryCellSlot(
        key: key,
        rowOrdinal: rowOrdinal,
        slot: out var slot
    ) && (m_observations is not null))
        ? m_observations[slot]
        : null
    );
    /// <summary>Reads the identity that minted one cell's current value.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The cell key.</param>
    /// <returns>The provenance, or <see langword="null"/> for a locally minted value.</returns>
    public string? Provenance(int rowOrdinal, CellKey key) => ((TryCellSlot(
        key: key,
        rowOrdinal: rowOrdinal,
        slot: out var slot
    ) && (m_provenance is not null))
        ? m_provenance[slot]
        : null
    );
    /// <summary>Reads one cell's own audience restriction.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The cell key.</param>
    /// <returns>The restriction, or <see langword="null"/> when the cell declares none.</returns>
    public StateVisibility? Visibility(int rowOrdinal, CellKey key) => ((TryCellSlot(
        key: key,
        rowOrdinal: rowOrdinal,
        slot: out var slot
    ) && (m_visibilities is not null))
        ? m_visibilities[slot]
        : null
    );
    /// <summary>Attempts to read one cell's clock without materializing a record.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The cell key.</param>
    /// <param name="epochTick">The simulation-tick epoch.</param>
    /// <param name="epochEngineTick">The engine-tick epoch.</param>
    /// <param name="y0">The follower's sampled position, as raw <c>FixedQ4816</c> bits.</param>
    /// <param name="v0">The follower's sampled velocity per second, as raw <c>FixedQ4816</c> bits.</param>
    /// <param name="substepTicks">The rotation's carried substep remainder, in ticks.</param>
    /// <param name="set">Whether a clock has ever been settled here; the columns read zero either way, so a cell
    /// under no trait and a cell whose clock settled at the origin are told apart by this alone.</param>
    /// <returns><see langword="true"/> when the cell address resolves.</returns>
    public bool TryReadClock(int rowOrdinal, CellKey key, out long epochTick, out long epochEngineTick, out long y0, out long v0, out long substepTicks, out bool set) {
        if (TryCellSlot(
            key: key,
            rowOrdinal: rowOrdinal,
            slot: out var slot
        )) {
            set = HasClock(slot: slot);
            epochEngineTick = ((m_clockEpochEngineTicks is null) ? 0L : m_clockEpochEngineTicks[slot]);
            epochTick = ((m_clockEpochTicks is null) ? 0L : m_clockEpochTicks[slot]);
            substepTicks = ((m_clockSubstepTicks is null) ? 0L : m_clockSubstepTicks[slot]);
            v0 = ((m_clockV0 is null) ? 0L : m_clockV0[slot]);
            y0 = ((m_clockY0 is null) ? 0L : m_clockY0[slot]);

            return true;
        }

        epochEngineTick = 0L;
        epochTick = 0L;
        set = false;
        substepTicks = 0L;
        v0 = 0L;
        y0 = 0L;

        return false;
    }
    /// <summary>Attempts to write one cell's clock.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The cell key.</param>
    /// <param name="epochTick">The simulation-tick epoch; never negative.</param>
    /// <param name="epochEngineTick">The engine-tick epoch; never negative.</param>
    /// <param name="y0">The follower's sampled position, as raw <c>FixedQ4816</c> bits.</param>
    /// <param name="v0">The follower's sampled velocity per second, as raw <c>FixedQ4816</c> bits.</param>
    /// <param name="substepTicks">The rotation's carried substep remainder, in ticks; never negative.</param>
    /// <param name="reason">Why the write was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the clock was stored.</returns>
    public bool TryWriteClock(int rowOrdinal, CellKey key, long epochTick, long epochEngineTick, long y0, long v0, long substepTicks, out string reason) {
        if (!TryPresentSlot(
            key: key,
            rowOrdinal: rowOrdinal,
            slot: out var slot
        )) {
            reason = $"row '{RowName(rowOrdinal: rowOrdinal)}' holds no cell to clock";

            return false;
        }
        if (
            (epochTick < 0L) ||
            (epochEngineTick < 0L) ||
            (substepTicks < 0L)
        ) {
            reason = $"row '{RowName(rowOrdinal: rowOrdinal)}' cell clock carries a negative epoch or substep";

            return false;
        }

        WriteNumber(
            column: ArenaColumn.ClockEpochTick,
            index: slot,
            value: epochTick
        );
        WriteNumber(
            column: ArenaColumn.ClockEpochEngineTick,
            index: slot,
            value: epochEngineTick
        );
        WriteNumber(
            column: ArenaColumn.ClockY0,
            index: slot,
            value: y0
        );
        WriteNumber(
            column: ArenaColumn.ClockV0,
            index: slot,
            value: v0
        );
        WriteNumber(
            column: ArenaColumn.ClockSubstepTicks,
            index: slot,
            value: substepTicks
        );
        WriteNumber(
            column: ArenaColumn.ClockSet,
            index: slot,
            value: 1L
        );

        reason = string.Empty;

        return true;
    }
    /// <summary>Writes one cell's <see cref="StateCellBehavior"/> override.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The cell key.</param>
    /// <param name="behavior">The override to store.</param>
    /// <returns><see langword="true"/> when the cell address resolves.</returns>
    public bool TryWriteBehavior(int rowOrdinal, CellKey key, StateCellBehavior behavior) => TryWriteCellNumber(
        column: ArenaColumn.Behavior,
        key: key,
        rowOrdinal: rowOrdinal,
        value: ((long)behavior)
    );
    /// <summary>Writes one draw site's cursor.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="cursor">The cursor to store; never negative.</param>
    /// <returns><see langword="true"/> when the row is a draw site and the cursor is not negative.</returns>
    public bool TryWriteDrawCursor(int rowOrdinal, long cursor) {
        if (
            (cursor < 0L) ||
            !TryRowLayout(
            layout: out var layout,
            rowOrdinal: rowOrdinal
        ) ||
            (layout.MaskWordStart < 0)
        ) {
            return false;
        }

        WriteNumber(
            column: ArenaColumn.DrawCursor,
            index: rowOrdinal,
            value: cursor
        );

        return true;
    }
    /// <summary>Writes one drawn mask of a draw site.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="index">The mask's context ordinal.</param>
    /// <param name="mask">The mask to store.</param>
    /// <returns><see langword="true"/> when the row is a draw site with room for that context.</returns>
    public bool TryWriteDrawnMask(int rowOrdinal, int index, ClosedBitset256 mask) {
        if (
            !TryRowLayout(
            layout: out var layout,
            rowOrdinal: rowOrdinal
        ) ||
            (layout.MaskWordStart < 0) ||
            (((uint)index) >= ((uint)layout.MaskCount))
        ) {
            return false;
        }

        var word = (layout.MaskWordStart + (index * 4));

        WriteNumber(
            column: ArenaColumn.DrawnMaskWord,
            index: word,
            value: ((long)mask.Word0)
        );
        WriteNumber(
            column: ArenaColumn.DrawnMaskWord,
            index: (word + 1),
            value: ((long)mask.Word1)
        );
        WriteNumber(
            column: ArenaColumn.DrawnMaskWord,
            index: (word + 2),
            value: ((long)mask.Word2)
        );
        WriteNumber(
            column: ArenaColumn.DrawnMaskWord,
            index: (word + 3),
            value: ((long)mask.Word3)
        );

        return true;
    }
    /// <summary>Writes a ring row's history cursor.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="cursor">The cursor to store; never negative.</param>
    /// <returns><see langword="true"/> when the row is a ring and the cursor is not negative.</returns>
    /// <remarks>The cursor counts every value ever pushed, so the slot a push lands on is the cursor modulo the
    /// ring's capacity and the cursor itself never wraps.</remarks>
    public bool TryWriteHistoryCursor(int rowOrdinal, long cursor) {
        if (
            (cursor < 0L) ||
            !TryRowLayout(
            layout: out var layout,
            rowOrdinal: rowOrdinal
        ) ||
            (layout.Shape != RowShape.Ring)
        ) {
            return false;
        }

        WriteNumber(
            column: ArenaColumn.HistoryCursor,
            index: rowOrdinal,
            value: cursor
        );

        return true;
    }
    /// <summary>Writes one cell's persisted last-seen stamp.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The cell key.</param>
    /// <param name="observation">The stamp to store.</param>
    /// <returns><see langword="true"/> when the cell address resolves.</returns>
    public bool TryWriteObservation(int rowOrdinal, CellKey key, StateObservation? observation) => TryWriteCellReference(
        column: ArenaColumn.Observation,
        key: key,
        rowOrdinal: rowOrdinal,
        value: observation
    );
    /// <summary>Writes one cell's persisted last-seen stamp, addressed by position.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="position">The position within the row — a lattice cell ordinal, a pile position, a ring slot,
    /// or a declaration position on a keyed row.</param>
    /// <param name="observation">The stamp to store.</param>
    /// <returns><see langword="true"/> when the position holds a cell.</returns>
    /// <remarks>A lattice row's cells are addressed by topology cell ordinal without interning a key per cell,
    /// which is what a whole-board sweep needs.</remarks>
    public bool TryWriteObservationAt(int rowOrdinal, int position, StateObservation? observation) {
        if (!TryPresentSlotAt(
            position: position,
            rowOrdinal: rowOrdinal,
            slot: out var slot
        )) {
            return false;
        }

        WriteReference(
            column: ArenaColumn.Observation,
            index: slot,
            value: observation
        );

        return true;
    }
    /// <summary>Reads one cell's persisted last-seen stamp, addressed by position.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="position">The position within the row.</param>
    /// <returns>The stamp, or <see langword="null"/> when the position holds no cell or carries none.</returns>
    public StateObservation? ObservationAt(int rowOrdinal, int position) => ((TryPresentSlotAt(
        position: position,
        rowOrdinal: rowOrdinal,
        slot: out var slot
    ) && (m_observations is not null))
        ? m_observations[slot]
        : null
    );
    /// <summary>Writes a phase row's persisted progression sequence.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="sequence">The sequence to store; never negative.</param>
    /// <returns><see langword="true"/> when the row declares a <see cref="StatePhase"/> and the sequence is not
    /// negative.</returns>
    /// <remarks>A sequence belongs to the row that declares the phase, which is also the only row an export
    /// carries one back out of.</remarks>
    public bool TryWritePhaseSequence(int rowOrdinal, long sequence) {
        if (
            (sequence < 0L) ||
            !TryRowLayout(
            layout: out _,
            rowOrdinal: rowOrdinal
        ) ||
            (DocumentRow(rowOrdinal: rowOrdinal).Phase is null)
        ) {
            return false;
        }

        WriteNumber(
            column: ArenaColumn.PhaseSequence,
            index: rowOrdinal,
            value: sequence
        );

        return true;
    }
    /// <summary>Writes the identity that minted one cell's current value.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The cell key.</param>
    /// <param name="provenance">The issuer id, or <see langword="null"/> for a locally minted value.</param>
    /// <returns><see langword="true"/> when the cell address resolves and the id is no longer than
    /// <see cref="StateCapacity.MaxProvenanceLength"/>, the length the arena's byte measure counts a provenance
    /// at.</returns>
    public bool TryWriteProvenance(int rowOrdinal, CellKey key, string? provenance) => (
        ((provenance?.Length ?? 0) <= StateCapacity.MaxProvenanceLength) &&
        TryWriteCellReference(
            column: ArenaColumn.Provenance,
            key: key,
            rowOrdinal: rowOrdinal,
            value: provenance
        )
    );
    /// <summary>Writes one cell's own audience restriction.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The cell key.</param>
    /// <param name="visibility">The restriction to store.</param>
    /// <returns><see langword="true"/> when the cell address resolves.</returns>
    public bool TryWriteVisibility(int rowOrdinal, CellKey key, StateVisibility? visibility) => TryWriteCellReference(
        column: ArenaColumn.Visibility,
        key: key,
        rowOrdinal: rowOrdinal,
        value: visibility
    );

    // A cell's runtime state rides its value: export, import, and relayout carry a cell only while it holds one, so
    // a cell that is addressable but holds nothing takes no runtime state that those would then drop.
    private bool TryPresentSlot(int rowOrdinal, CellKey key, out int slot) => (TryCellSlot(
        key: key,
        rowOrdinal: rowOrdinal,
        slot: out slot
    ) && Bit(
        index: slot,
        words: m_presence
    ));
    private bool TryPresentSlotAt(int rowOrdinal, int position, out int slot) {
        slot = -1;

        if (
            !TryRowLayout(
            layout: out var layout,
            rowOrdinal: rowOrdinal
        ) ||
            (((uint)position) >= ((uint)layout.CellCapacity))
        ) {
            return false;
        }

        slot = (layout.CellStart + position);

        return Bit(
            index: slot,
            words: m_presence
        );
    }
    private bool TryWriteCellNumber(int rowOrdinal, CellKey key, ArenaColumn column, long value) {
        if (!TryPresentSlot(
            key: key,
            rowOrdinal: rowOrdinal,
            slot: out var slot
        )) {
            return false;
        }

        WriteNumber(
            column: column,
            index: slot,
            value: value
        );

        return true;
    }
    private bool TryWriteCellReference(int rowOrdinal, CellKey key, ArenaColumn column, object? value) {
        if (!TryPresentSlot(
            key: key,
            rowOrdinal: rowOrdinal,
            slot: out var slot
        )) {
            return false;
        }

        WriteReference(
            column: column,
            index: slot,
            value: value
        );

        return true;
    }
}
