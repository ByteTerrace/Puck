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
    /// <summary>The press threshold a held control latches at when its declaration names none: a modifier's, an
    /// activator member's and a binding session's analog press.</summary>
    public const float DefaultPressThreshold = 0.5f;
    /// <summary>The release threshold paired with <see cref="DefaultPressThreshold"/>.</summary>
    public const float DefaultReleaseThreshold = 0.4f;

    private readonly List<int> m_heldOrder = [];
    private readonly bool[] m_latched;
    private readonly float[] m_pressThresholds;
    private readonly float[] m_releaseThresholds;

    /// <summary>Initializes a new instance with the SAME press/release threshold for every modifier.</summary>
    /// <param name="modifierCount">How many independent modifier slots to track.</param>
    /// <param name="pressThreshold">The value at or above which an unlatched modifier latches held.</param>
    /// <param name="releaseThreshold">The value at or below which a latched modifier releases; at most <paramref name="pressThreshold"/>.</param>
    public HeldOrderTracker(int modifierCount, float pressThreshold, float releaseThreshold)
        : this(
        pressThresholds: Fill(
            count: modifierCount,
            value: pressThreshold
        ),
        releaseThresholds: Fill(
            count: modifierCount,
            value: releaseThreshold
        )
    ) {
    }
    /// <summary>Initializes a new instance with a PER-MODIFIER press/release threshold pair — the shape
    /// <see cref="BindingModifierDefinition"/> declares, where every modifier may pick its own feel.</summary>
    /// <param name="pressThresholds">Each modifier's press threshold, by index.</param>
    /// <param name="releaseThresholds">Each modifier's release threshold, by index (same length as <paramref name="pressThresholds"/>).</param>
    /// <exception cref="ArgumentException">The two threshold lists have different lengths, or a threshold pair is
    /// non-finite or has release above press.</exception>
    public HeldOrderTracker(IReadOnlyList<float> pressThresholds, IReadOnlyList<float> releaseThresholds) {
        ArgumentNullException.ThrowIfNull(argument: pressThresholds);
        ArgumentNullException.ThrowIfNull(argument: releaseThresholds);

        if (pressThresholds.Count != releaseThresholds.Count) {
            throw new ArgumentException(
                message: "pressThresholds and releaseThresholds must be the same length.",
                paramName: nameof(releaseThresholds)
            );
        }

        for (var index = 0; (index < pressThresholds.Count); index++) {
            if (!IsValidThresholdPair(
                pressThreshold: pressThresholds[index],
                releaseThreshold: releaseThresholds[index]
            )) {
                throw new ArgumentException(
                    message: $"Threshold pair {index} must be finite with release at or below press.",
                    paramName: nameof(releaseThresholds)
                );
            }
        }

        m_latched = new bool[pressThresholds.Count];
        m_pressThresholds = [.. pressThresholds];
        m_releaseThresholds = [.. releaseThresholds];
    }

    /// <summary>Gets how many modifiers are currently held.</summary>
    public int Count => m_heldOrder.Count;
    /// <summary>Gets the held modifier indices, in press order (index 0 = pressed first).</summary>
    public ReadOnlySpan<int> HeldOrder => CollectionsMarshal.AsSpan(list: m_heldOrder);

    /// <summary>Returns whether a press/release threshold pair forms a hysteresis band: both finite, release at or
    /// below press. The one rule every declaration of a held control's thresholds is checked by.</summary>
    /// <param name="pressThreshold">The value at or above which the control latches held.</param>
    /// <param name="releaseThreshold">The value at or below which a held control releases.</param>
    /// <returns><see langword="true"/> when the pair is valid.</returns>
    public static bool IsValidThresholdPair(float pressThreshold, float releaseThreshold) => (
        float.IsFinite(f: pressThreshold) &&
        float.IsFinite(f: releaseThreshold) &&
        (releaseThreshold <= pressThreshold)
    );
    /// <summary>Returns the level a signal drives a held control's hysteresis band with: zero for a release or
    /// cancel edge, whatever value it carries; otherwise its <see cref="CommandValue.AsAxis1D"/> magnitude (an analog
    /// trigger's level or a digital button's 0/1).</summary>
    /// <param name="signal">The signal.</param>
    /// <returns>The level.</returns>
    public static float LevelOf(in InputSignal signal) => ((signal.Phase is CommandPhase.Completed or CommandPhase.Canceled)
        ? 0f
        : signal.Value.AsAxis1D
    );
    private static float[] Fill(int count, float value) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: count);

        var array = new float[count];

        Array.Fill(
            array: array,
            value: value
        );

        return array;
    }

    /// <summary>Releases every modifier (focus loss, device disconnect, or a mode reset).</summary>
    public void Reset() {
        Array.Clear(array: m_latched);
        m_heldOrder.Clear();
    }
    /// <summary>Feeds one modifier's raw value this frame — an analog trigger's magnitude or a digital button's 0/1.
    /// Latches held when unlatched and the value crosses <c>pressThreshold</c>; releases when latched and the value
    /// falls to/below <c>releaseThreshold</c>; otherwise holds its current state (the hysteresis band).</summary>
    /// <param name="index">The modifier's index (0-based, less than the count passed to the constructor).</param>
    /// <param name="value">This frame's raw value.</param>
    /// <returns><see langword="true"/> when the modifier's held/released membership changed (it joined or left
    /// <see cref="HeldOrder"/> this call).</returns>
    public bool Set(int index, float value) {
        if (
            !m_latched[index] &&
            (value >= m_pressThresholds[index])
        ) {
            m_latched[index] = true;
            m_heldOrder.Add(item: index);

            return true;
        }

        if (
            m_latched[index] &&
            (value <= m_releaseThresholds[index])
        ) {
            m_latched[index] = false;
            _ = m_heldOrder.Remove(item: index);

            return true;
        }

        return false;
    }
    /// <summary>Feeds one modifier's <see cref="InputSignal"/> this frame at its <see cref="LevelOf"/> level.</summary>
    /// <param name="index">The modifier's index (0-based, less than the count passed to the constructor).</param>
    /// <param name="signal">This frame's signal for the modifier.</param>
    /// <returns><see langword="true"/> when the modifier's held/released membership changed.</returns>
    public bool Set(int index, in InputSignal signal) => Set(
        index: index,
        value: LevelOf(signal: in signal)
    );
}
