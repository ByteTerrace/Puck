namespace Puck.State;

/// <summary>One storage column of a <see cref="StateArena"/> — the unit a journal entry names, a rewind restores,
/// and the hash folds.</summary>
/// <remarks>The value columns carry what a cell holds; every other column carries a runtime-state field the row or
/// cell model declares, so nothing a row carries today lives outside a column.</remarks>
public enum ArenaColumn : byte {
    /// <summary>A cell's numeric payload: an <see cref="CellKind.Int"/> value, a <see cref="CellKind.Fixed"/> row's
    /// raw bits, or a <see cref="CellKind.Bool"/> row's 0/1 encoding.</summary>
    Number = 0,

    /// <summary>A <see cref="CellKind.Text"/> cell's text.</summary>
    Text,

    /// <summary>A <see cref="CellKind.Vector"/> cell's components; the index is the cell slot, not the byte.</summary>
    Vector,

    /// <summary>Whether a cell slot holds a value at all.</summary>
    Presence,

    /// <summary>The interned <see cref="CellKey"/> ordinal occupying a cell slot, or <c>-1</c> for a free slot.</summary>
    MemberKey,

    /// <summary>A cell clock's simulation-tick epoch.</summary>
    ClockEpochTick,

    /// <summary>A cell clock's engine-tick epoch.</summary>
    ClockEpochEngineTick,

    /// <summary>A cell clock's sampled follower position, as raw <c>FixedQ4816</c> bits.</summary>
    ClockY0,

    /// <summary>A cell clock's sampled follower velocity per second, as raw <c>FixedQ4816</c> bits.</summary>
    ClockV0,

    /// <summary>A cell clock's carried rotation substep remainder, in ticks.</summary>
    ClockSubstepTicks,

    /// <summary>Whether a cell's clock has been written at all, so a clock whose five numbers are all zero is
    /// still a clock and not an absence of one.</summary>
    ClockSet,

    /// <summary>The identity that minted a cell's current value.</summary>
    Provenance,

    /// <summary>A cell's <see cref="StateCellBehavior"/> override.</summary>
    Behavior,

    /// <summary>A cell's own audience restriction.</summary>
    Visibility,

    /// <summary>A cell's persisted last-seen stamp.</summary>
    Observation,

    /// <summary>How many cells a row currently holds; for an ordered row this is also its pile length.</summary>
    MemberCount,

    /// <summary>How many values have ever been pushed into a ring row.</summary>
    HistoryCursor,

    /// <summary>How many samples a draw site has ever consumed.</summary>
    DrawCursor,

    /// <summary>One 64-bit word of a draw site's drawn masks, four words per mask.</summary>
    DrawnMaskWord,

    /// <summary>A phase row's persisted progression sequence.</summary>
    PhaseSequence,

    /// <summary>One participant-lane or identity-lane slot's value.</summary>
    LaneNumber,

    /// <summary>Whether one participant or identity ordinal has joined its lane.</summary>
    LaneRoster,
}
/// <summary>Which index space an <see cref="ArenaColumn"/> is addressed in.</summary>
public enum ArenaIndexSpace : byte {
    /// <summary>Indexed by cell slot, the space <see cref="ArenaLayout.CellSlotCount"/> sizes.</summary>
    Cell = 0,

    /// <summary>Indexed by catalog row ordinal.</summary>
    Row,

    /// <summary>Indexed by drawn-mask word.</summary>
    MaskWord,

    /// <summary>Indexed by lane slot — one per lane descriptor per lane ordinal.</summary>
    LaneSlot,

    /// <summary>Indexed by lane roster entry — one per lane ordinal across both slot lanes.</summary>
    LaneRoster,
}
/// <summary>Classifies an <see cref="ArenaColumn"/> so a journal entry, a rewind, and the change walk handle every
/// column through one table instead of repeating the case list.</summary>
public static class ArenaColumns {
    /// <summary>Every declared column, in the order a layout lays them out and a hash folds them.</summary>
    public static ReadOnlySpan<ArenaColumn> All => [
        ArenaColumn.Number,
        ArenaColumn.Text,
        ArenaColumn.Vector,
        ArenaColumn.Presence,
        ArenaColumn.MemberKey,
        ArenaColumn.ClockEpochTick,
        ArenaColumn.ClockEpochEngineTick,
        ArenaColumn.ClockY0,
        ArenaColumn.ClockV0,
        ArenaColumn.ClockSubstepTicks,
        ArenaColumn.ClockSet,
        ArenaColumn.Provenance,
        ArenaColumn.Behavior,
        ArenaColumn.Visibility,
        ArenaColumn.Observation,
        ArenaColumn.MemberCount,
        ArenaColumn.HistoryCursor,
        ArenaColumn.DrawCursor,
        ArenaColumn.DrawnMaskWord,
        ArenaColumn.PhaseSequence,
        ArenaColumn.LaneNumber,
        ArenaColumn.LaneRoster,
    ];

    /// <summary>Returns the index space <paramref name="column"/> is addressed in.</summary>
    /// <param name="column">The column to classify.</param>
    /// <returns>The column's index space.</returns>
    public static ArenaIndexSpace Space(ArenaColumn column) => (column switch {
        ArenaColumn.MemberCount or ArenaColumn.HistoryCursor or ArenaColumn.DrawCursor or ArenaColumn.PhaseSequence => ArenaIndexSpace.Row,
        ArenaColumn.DrawnMaskWord => ArenaIndexSpace.MaskWord,
        ArenaColumn.LaneNumber => ArenaIndexSpace.LaneSlot,
        ArenaColumn.LaneRoster => ArenaIndexSpace.LaneRoster,
        _ => ArenaIndexSpace.Cell,
    });
    /// <summary>Returns a value indicating whether <paramref name="column"/> stores object references rather than
    /// numbers.</summary>
    /// <param name="column">The column to classify.</param>
    /// <returns><see langword="true"/> for a reference column.</returns>
    public static bool IsReference(ArenaColumn column) => (column is (ArenaColumn.Text or ArenaColumn.Provenance or ArenaColumn.Visibility or ArenaColumn.Observation));
}
