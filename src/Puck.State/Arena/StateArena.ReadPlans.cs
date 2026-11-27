using Puck.Maths;

namespace Puck.State;

/// <summary>A cached address of one numeric cell under no value-over-time trait: the slot an arena's key-to-slot map
/// gave a declared key, with the map and the map's version it was read under. Only <see cref="StateArena"/>
/// constructs one and only its planned read
/// (<see cref="StateArena.TryReadPlannedNumber"/>) reads one,
/// after proving it still stands, so no caller can read the slot a plan cached without that proof.</summary>
/// <remarks>A plan is bound to the arena that made it for its whole life and is refreshed in place only by that
/// arena, so it follows the arena's own threading contract; a plan handed to another arena is replaced, never
/// read.</remarks>
public sealed class CellReadPlan {
    internal CellReadPlan(StateArena arena) => Arena = arena;

    internal StateArena Arena { get; }
    internal CellKey Key;
    internal ArenaSlotMap? Map;
    internal int RowOrdinal;
    internal int Slot;
    internal ulong Version;
}
public sealed partial class StateArena {
    /// <summary>Reads one numeric cell's live value through a cached plan of its slot, answering exactly what
    /// <see cref="TryReadLiveNumber(int, CellKey, in ArenaTime, out long)"/> answers for the same cell.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <param name="key">The cell key, resolved by this arena's key table.</param>
    /// <param name="time">The clocks the traits are evaluated against.</param>
    /// <param name="plan">The caller's plan for this read: <see langword="null"/>, or what an earlier call left. On
    /// return it holds a plan for this cell whenever the cell can be planned (a declared key of a stored, trait-free
    /// numeric row the key-to-slot map holds), and is left as it was otherwise.</param>
    /// <param name="value">The live raw value on success; otherwise zero.</param>
    /// <returns><see langword="true"/> when the row holds a numeric cell there.</returns>
    /// <remarks>A plan stands while it names this arena, this row and key, the row's current key-to-slot map, and the
    /// version that map stood at when the plan read it. The map's version moves on every change to the map (a member
    /// joining, leaving or moving, a reindex after a rewind, a relayout's rebuilt map), so a standing plan's slot is
    /// the slot <see cref="TryCellSlot"/> would resolve; presence, the row's kind and its traits are read live on
    /// every call.</remarks>
    public bool TryReadPlannedNumber(int rowOrdinal, CellKey key, in ArenaTime time, ref CellReadPlan? plan, out long value) {
        if (((uint)rowOrdinal) >= ((uint)m_layout.RowCount)) {
            value = 0L;
            return false;
        }

        ref readonly var layout = ref m_layout[rowOrdinal];

        if (!layout.IsStored || layout.HasTraits || (layout.Kind is (CellKind.Text or CellKind.Vector))) {
            return TryReadLiveNumber(key: key, rowOrdinal: rowOrdinal, time: in time, value: out value);
        }

        var map = m_slotOfKey[rowOrdinal];
        var held = plan;

        if (
            (held is null) ||
            !ReferenceEquals(objA: held.Arena, objB: this) ||
            (held.RowOrdinal != rowOrdinal) ||
            (held.Key != key) ||
            !ReferenceEquals(objA: held.Map, objB: map) ||
            (held.Version != map.Version)
        ) {
            if (!m_keys.IsSeed(key: key) || !map.TryGetValue(key: key.Ordinal, value: out var slot)) {
                return TryReadLiveNumber(key: key, rowOrdinal: rowOrdinal, time: in time, value: out value);
            }
            if ((held is null) || !ReferenceEquals(objA: held.Arena, objB: this)) {
                held = new CellReadPlan(arena: this);
                plan = held;
            }

            held.Key = key;
            held.Map = map;
            held.RowOrdinal = rowOrdinal;
            held.Slot = slot;
            held.Version = map.Version;
        }
        if (!BitWords.Contains(index: held.Slot, words: m_presence)) {
            value = 0L;
            return false;
        }

        var raw = m_numbers[held.Slot];

        value = ((layout.Kind == CellKind.Bool) ? ((raw != 0L) ? 1L : 0L) : raw);
        return true;
    }
}
