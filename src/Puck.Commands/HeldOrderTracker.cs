using System.Runtime.InteropServices;

namespace Puck.Commands;

/// <summary>
/// A signal-free ordered held-modifier set: a fixed number of modifier slots (typically triggers/shoulders), each
/// latched held/released with independent press/release hysteresis, kept in PRESS order — so an order-sensitive
/// chord (<c>left</c>-then-<c>right</c> vs <c>right</c>-then-<c>left</c>) is recoverable from <see cref="HeldOrder"/>
/// alone. This is the primitive <see cref="BindingChordTracker"/> resolves a page from; it is also the exact state
/// machine every hand-rolled LT/RT chord tracker in the demo re-implemented per-controller — extracted here so a new
/// order-sensitive pad chord never needs its own copy.
/// </summary>
public sealed class HeldOrderTracker {
    private readonly List<int> m_heldOrder = [];
    private readonly bool[] m_latched;
    private readonly float[] m_pressThresholds;
    private readonly float[] m_releaseThresholds;

    /// <summary>Initializes a new instance with the SAME press/release threshold for every modifier.</summary>
    /// <param name="modifierCount">How many independent modifier slots to track.</param>
    /// <param name="pressThreshold">The value at or above which an unlatched modifier latches held.</param>
    /// <param name="releaseThreshold">The value at or below which a latched modifier releases; at most <paramref name="pressThreshold"/>.</param>
    public HeldOrderTracker(int modifierCount, float pressThreshold, float releaseThreshold)
        : this(pressThresholds: Fill(count: modifierCount, value: pressThreshold), releaseThresholds: Fill(count: modifierCount, value: releaseThreshold)) {
    }

    /// <summary>Initializes a new instance with a PER-MODIFIER press/release threshold pair — the shape
    /// <see cref="BindingModifierDefinition"/> declares, where every modifier may pick its own feel.</summary>
    /// <param name="pressThresholds">Each modifier's press threshold, by index.</param>
    /// <param name="releaseThresholds">Each modifier's release threshold, by index (same length as <paramref name="pressThresholds"/>).</param>
    /// <exception cref="ArgumentException">The two threshold lists have different lengths.</exception>
    public HeldOrderTracker(IReadOnlyList<float> pressThresholds, IReadOnlyList<float> releaseThresholds) {
        ArgumentNullException.ThrowIfNull(argument: pressThresholds);
        ArgumentNullException.ThrowIfNull(argument: releaseThresholds);

        if (pressThresholds.Count != releaseThresholds.Count) {
            throw new ArgumentException(message: "pressThresholds and releaseThresholds must be the same length.", paramName: nameof(releaseThresholds));
        }

        m_latched = new bool[pressThresholds.Count];
        m_pressThresholds = [.. pressThresholds];
        m_releaseThresholds = [.. releaseThresholds];
    }

    /// <summary>Gets how many modifiers are currently held.</summary>
    public int Count => m_heldOrder.Count;

    /// <summary>Gets the held modifier indices, in press order (index 0 = pressed first).</summary>
    public ReadOnlySpan<int> HeldOrder => CollectionsMarshal.AsSpan(list: m_heldOrder);

    /// <summary>Feeds one modifier's raw value this frame — an analog trigger's magnitude or a digital button's 0/1.
    /// Latches held when unlatched and the value crosses <c>pressThreshold</c>; releases when latched and the value
    /// falls to/below <c>releaseThreshold</c>; otherwise holds its current state (the hysteresis band).</summary>
    /// <param name="index">The modifier's index (0-based, less than the count passed to the constructor).</param>
    /// <param name="value">This frame's raw value.</param>
    /// <returns><see langword="true"/> when the modifier's held/released membership changed (it joined or left
    /// <see cref="HeldOrder"/> this call).</returns>
    public bool Set(int index, float value) {
        if (!m_latched[index] && (value >= m_pressThresholds[index])) {
            m_latched[index] = true;
            m_heldOrder.Add(item: index);

            return true;
        }

        if (m_latched[index] && (value <= m_releaseThresholds[index])) {
            m_latched[index] = false;
            _ = m_heldOrder.Remove(item: index);

            return true;
        }

        return false;
    }

    /// <summary>Releases every modifier (focus loss, device disconnect, or a mode reset).</summary>
    public void Reset() {
        Array.Clear(array: m_latched);
        m_heldOrder.Clear();
    }

    private static float[] Fill(int count, float value) {
        var array = new float[count];

        Array.Fill(array: array, value: value);

        return array;
    }
}
