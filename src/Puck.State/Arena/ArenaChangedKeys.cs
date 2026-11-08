using System.Collections;

namespace Puck.State;

/// <summary>Committed key changes since the carrier's owner last cleared it. Structural changes conservatively
/// invalidate the whole row, including deleted keys and changed audience declarations.</summary>
public sealed class ArenaChangedKeys {
    private readonly Dictionary<int, BitArray> m_keys = [];
    private readonly HashSet<int> m_whole = [];

    /// <summary>Marks a changed key, or the whole row when no surviving key describes the change.</summary>
    public void Mark(int row, int key = -1) {
        if (key < 0) { m_whole.Add(row); return; }
        if (!m_keys.TryGetValue(row, out var bits)) { m_keys[row] = bits = new BitArray(key + 1); }
        if (bits.Length <= key) { bits.Length = key + 1; }
        bits[key] = true;
    }
    /// <summary>Whether a structural change requires recomposing the row.</summary>
    public bool Whole(int row) => m_whole.Contains(row);
    /// <summary>Whether the row or one of these keys changed.</summary>
    public bool Overlaps(int row, ReadOnlySpan<int> keys) {
        if (Whole(row)) { return true; }
        if (!m_keys.TryGetValue(row, out var bits)) { return false; }
        foreach (var key in keys) { if ((key >= 0) && (key < bits.Length) && bits[key]) { return true; } }
        return false;
    }
    /// <summary>Starts the next carrier window, retaining its allocated bitsets.</summary>
    public void Clear() {
        m_whole.Clear();
        foreach (var bits in m_keys.Values) { bits.SetAll(false); }
    }
    /// <summary>Accumulates another publication into this tick's carrier.</summary>
    public void Include(ArenaChangedKeys other) {
        foreach (var row in other.m_whole) { Mark(row); }
        foreach (var (row, bits) in other.m_keys) {
            for (var key = 0; key < bits.Length; key++) { if (bits[key]) { Mark(row, key); } }
        }
    }
}
