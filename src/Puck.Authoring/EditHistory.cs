namespace Puck.Authoring;

/// <summary>
/// A bounded snapshot ring: the undo/redo mechanism for frame-rate authoring editors. Callers push an immutable
/// snapshot of the whole model after every completed edit (a drag gesture coalesces to ONE push, taken on the
/// gesture's release edge, never per frame); undo/redo hand back a prior snapshot to restore wholesale. A new edit
/// pushed after an undo truncates the redo tail (the standard linear-history model); a push beyond
/// <see cref="Capacity"/> silently ages out the oldest snapshot rather than growing unbounded. A deliberate save is
/// a keep-point, not a boundary — the history survives it; only <see cref="Reset"/> (a fresh load) rebaselines it.
/// </summary>
/// <typeparam name="T">The immutable model snapshot type.</typeparam>
public sealed class EditHistory<T> {
    private readonly int m_capacity;
    private readonly List<T> m_snapshots;
    private int m_index;

    /// <summary>Initializes the history around an initial baseline state.</summary>
    /// <param name="capacity">The bounded snapshot count (at least 2 — the baseline plus one edit).</param>
    /// <param name="initial">The baseline snapshot (the state before any edit).</param>
    public EditHistory(int capacity, T initial) {
        ArgumentOutOfRangeException.ThrowIfLessThan(other: 2, value: capacity);

        m_capacity = capacity;
        m_index = 0;
        m_snapshots = new List<T>(capacity: capacity) { initial, };
    }

    /// <summary>The bounded snapshot count passed at construction.</summary>
    public int Capacity => m_capacity;

    /// <summary>The number of snapshots currently retained (between 1 and <see cref="Capacity"/>).</summary>
    public int Count => m_snapshots.Count;

    /// <summary>Whether a redo step is available (an undo, or a fresh construction, leaves one behind).</summary>
    public bool CanRedo => (m_index < (m_snapshots.Count - 1));

    /// <summary>Whether an undo step is available (at least one edit sits before the current position).</summary>
    public bool CanUndo => (0 < m_index);

    /// <summary>Records a completed edit: truncates any redo tail, appends, and ages out the oldest snapshot beyond
    /// <see cref="Capacity"/>.</summary>
    /// <param name="snapshot">The model state AFTER the edit.</param>
    public void Push(T snapshot) {
        var truncateAt = (m_index + 1);

        if (truncateAt < m_snapshots.Count) {
            m_snapshots.RemoveRange(count: (m_snapshots.Count - truncateAt), index: truncateAt);
        }

        m_snapshots.Add(item: snapshot);

        if (m_capacity < m_snapshots.Count) {
            m_snapshots.RemoveAt(index: 0);
        }

        m_index = (m_snapshots.Count - 1);
    }

    /// <summary>Rebaselines the history around a freshly loaded/blank model (a load is a boundary; a save is not).</summary>
    /// <param name="initial">The new baseline snapshot.</param>
    public void Reset(T initial) {
        m_index = 0;
        m_snapshots.Clear();
        m_snapshots.Add(item: initial);
    }

    /// <summary>Steps forward, handing back the snapshot to restore.</summary>
    /// <param name="snapshot">The state to restore, when a redo step exists.</param>
    /// <returns><see langword="true"/> when a redo step existed.</returns>
    public bool TryRedo(out T snapshot) {
        if (!CanRedo) {
            snapshot = default!;

            return false;
        }

        m_index++;
        snapshot = m_snapshots[index: m_index];

        return true;
    }

    /// <summary>Steps back, handing back the snapshot to restore.</summary>
    /// <param name="snapshot">The state to restore, when an undo step exists.</param>
    /// <returns><see langword="true"/> when an undo step existed.</returns>
    public bool TryUndo(out T snapshot) {
        if (!CanUndo) {
            snapshot = default!;

            return false;
        }

        m_index--;
        snapshot = m_snapshots[index: m_index];

        return true;
    }
}
