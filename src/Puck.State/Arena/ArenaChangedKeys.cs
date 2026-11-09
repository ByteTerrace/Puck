using Puck.Abstractions.Collections;

namespace Puck.State;

/// <summary>Committed key changes since the carrier's owner last cleared it. Structural changes conservatively
/// invalidate the whole row, including deleted keys and changed audience declarations.</summary>
/// <remarks>Marking, testing, accumulating and clearing cost the changes the window holds, never the rows and keys
/// earlier windows touched: each keyed row's keys and the keyed rows themselves are <see cref="MarkedIndexSet"/>s.</remarks>
public sealed class ArenaChangedKeys {
    private readonly MarkedIndexSet m_whole = new();
    private readonly MarkedIndexSet m_keyedRows = new();
    // Each row's changed keys, by row ordinal; a set outlives the windows it is cleared across.
    private MarkedIndexSet?[] m_keys = [];

    /// <summary>Marks a changed key, or the whole row when no surviving key describes the change.</summary>
    /// <param name="row">The row ordinal.</param>
    /// <param name="key">The key ordinal, or a negative value for the whole row.</param>
    public void Mark(int row, int key = -1) {
        if (key < 0) { m_whole.Add(index: row); return; }
        if (row >= m_keys.Length) {
            Array.Resize(array: ref m_keys, newSize: Math.Max(val1: 16, val2: (int)System.Numerics.BitOperations.RoundUpToPowerOf2(value: (uint)(row + 1))));
        }
        (m_keys[row] ??= new MarkedIndexSet()).Add(index: key);
        m_keyedRows.Add(index: row);
    }
    /// <summary>Whether a structural change requires recomposing the row.</summary>
    /// <param name="row">The row ordinal.</param>
    /// <returns><see langword="true"/> when the row was marked whole.</returns>
    public bool Whole(int row) => m_whole.Contains(index: row);
    /// <summary>Whether the row or one of these keys changed.</summary>
    /// <param name="row">The row ordinal.</param>
    /// <param name="keys">The key ordinals to test; negative ordinals never match.</param>
    /// <returns><see langword="true"/> when the row was marked whole or one of the keys was marked.</returns>
    public bool Overlaps(int row, ReadOnlySpan<int> keys) {
        if (Whole(row)) { return true; }
        if ((((uint)row) >= ((uint)m_keys.Length)) || (m_keys[row] is not { Count: > 0 } changed)) { return false; }
        foreach (var key in keys) { if (changed.Contains(index: key)) { return true; } }
        return false;
    }
    /// <summary>Starts the next carrier window, retaining the allocated sets.</summary>
    public void Clear() {
        m_whole.Clear();
        foreach (var row in m_keyedRows.Members) { m_keys[row]!.Clear(); }
        m_keyedRows.Clear();
    }
    /// <summary>Accumulates another publication into this tick's carrier.</summary>
    /// <param name="other">The carrier whose marks are added; it is left unchanged.</param>
    public void Include(ArenaChangedKeys other) {
        ArgumentNullException.ThrowIfNull(argument: other);
        foreach (var row in other.m_whole.Members) { Mark(row: row); }
        foreach (var row in other.m_keyedRows.Members) {
            foreach (var key in other.m_keys[row]!.Members) { Mark(row: row, key: key); }
        }
    }
}
