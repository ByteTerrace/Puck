using System.Numerics;

namespace Puck.Commands;

/// <summary>The input state whose lifetime is exactly one open radial gesture. Presenters share this state machine
/// so cancellation cannot be undone by a later presentation frame and a selector sample cannot leak into the next
/// gesture.</summary>
public sealed class BindingWheelGestureState {
    /// <summary>The latest live directional Axis2D selector sample, or the retained commit-safe direction after
    /// the selector returns to neutral.</summary>
    public Vector2 Axis { get; private set; }
    /// <summary>Whether an Axis2D selector has crossed the wheel's authored dead zone during this gesture.</summary>
    public bool AxisKnown { get; private set; }
    /// <summary>Whether the latest accepted selector state is inside the authored dead zone. The directional
    /// <see cref="Axis"/> remains available so a throw and return occurring between presentation frames can still
    /// resolve its intended sector.</summary>
    public bool AxisNeutral { get; private set; }
    /// <summary>The selector arbitration sequence carried by the latest accepted directional or neutral state.</summary>
    public long AxisSequence { get; private set; }
    /// <summary>Whether the current presentation frame may arm a sector commit.</summary>
    public bool CanArm => (Opened && !Cancelled);
    /// <summary>Whether this gesture was explicitly or synthetically cancelled. The latch survives close so a
    /// release dispatched after presentation closes can still report cancellation; the next <see cref="Open"/>
    /// clears it.</summary>
    public bool Cancelled { get; private set; }
    /// <summary>Whether the presenter currently considers this gesture open.</summary>
    public bool Opened { get; private set; }
    /// <summary>The first spatial position observed during this gesture. Pointer selection uses this as the
    /// device-relative origin independently from the displayed hub.</summary>
    public Vector2 SpatialNeutral { get; private set; }
    /// <summary>Whether <see cref="SpatialNeutral"/> has been captured during this gesture.</summary>
    public bool SpatialNeutralKnown { get; private set; }

    private Vector2 m_peakAxis;
    private float m_peakAxisMagnitudeSquared;
    private Vector2 m_stableAxis;
    private bool m_stableAxisKnown;

    private void ClearAxis() {
        Axis = Vector2.Zero;
        AxisKnown = false;
        AxisNeutral = false;
        AxisSequence = 0L;
        m_peakAxis = Vector2.Zero;
        m_stableAxis = Vector2.Zero;
        m_peakAxisMagnitudeSquared = 0f;
        m_stableAxisKnown = false;
    }
    private void ClearSpatialNeutral() {
        SpatialNeutral = Vector2.Zero;
        SpatialNeutralKnown = false;
    }

    /// <summary>Latches cancellation for the remainder of this gesture.</summary>
    public void Cancel() => Cancelled = true;
    /// <summary>Ends presentation and clears transient selector input. Cancellation remains latched until the next
    /// open so a delayed release cannot be mistaken for an ordinary unarmed commit.</summary>
    public void Close() {
        Opened = false;
        ClearAxis();
        ClearSpatialNeutral();
    }
    /// <summary>Begins a fresh gesture, clearing cancellation and every selector sample from the prior gesture.</summary>
    public void Open() {
        Opened = true;
        Cancelled = false;
        ClearAxis();
        ClearSpatialNeutral();
    }
    /// <summary>Captures the first available spatial position as this gesture's device-relative origin. A
    /// position arriving after the opening frame is valid; later positions cannot move the origin.</summary>
    /// <param name="position">The spatial input position in its presenter's coordinate space.</param>
    /// <returns><see langword="true"/> only when this call captured the origin.</returns>
    public bool TryCaptureSpatialNeutral(Vector2 position) {
        if (
            !Opened ||
            SpatialNeutralKnown
        ) {
            return false;
        }

        SpatialNeutral = position;
        SpatialNeutralKnown = true;

        return true;
    }
    /// <summary>Accepts a finite Axis2D selector sample and tracks every deliberate directional update during an
    /// active excursion. The last sample above the switch threshold is retained when the selector returns to
    /// neutral; an excursion that never reaches that threshold retains its peak instead. A weak opposite-side
    /// sample is rejected both during an excursion and after neutral, so a missed center sample cannot let spring
    /// rebound masquerade as rotation or another throw.</summary>
    /// <param name="axis">The normalized selector sample.</param>
    /// <param name="sequence">The monotonically increasing input-arbitration sequence.</param>
    /// <param name="deadZoneSquared">The inclusive squared selector dead-zone magnitude.</param>
    /// <param name="switchThresholdSquared">The squared magnitude an opposite-side sample must reach to begin a
    /// new excursion after neutral.</param>
    /// <returns><see langword="true"/> when the sample becomes the gesture's selected axis.</returns>
    public bool TrySelect(Vector2 axis, long sequence, float deadZoneSquared, float switchThresholdSquared) {
        var magnitudeSquared = axis.LengthSquared();

        if (
            !Opened ||
            !float.IsFinite(f: magnitudeSquared) ||
            !float.IsFinite(f: deadZoneSquared) ||
            (deadZoneSquared < 0f) ||
            !float.IsFinite(f: switchThresholdSquared) ||
            (switchThresholdSquared < 0f)
        ) {
            return false;
        }

        if (magnitudeSquared <= deadZoneSquared) {
            if (
                !AxisKnown ||
                AxisNeutral
            ) {
                return false;
            }

            Axis = (m_stableAxisKnown
                ? m_stableAxis
                : m_peakAxis
            );
            AxisNeutral = true;
            AxisSequence = sequence;

            return true;
        }

        if (AxisKnown) {
            var retainedAxis = (m_stableAxisKnown
                ? m_stableAxis
                : m_peakAxis
            );

            if (AxisNeutral) {
                if (
                    (Vector2.Dot(
                    value1: retainedAxis,
                    value2: axis
                ) <= 0f) &&
                    (magnitudeSquared < switchThresholdSquared)
                ) {
                    return false;
                }

                m_peakAxis = Vector2.Zero;
                m_stableAxis = Vector2.Zero;
                m_peakAxisMagnitudeSquared = 0f;
                m_stableAxisKnown = false;
            } else if (
                (Vector2.Dot(
                value1: retainedAxis,
                value2: axis
            ) <= 0f) &&
                (magnitudeSquared < switchThresholdSquared)
            ) {
                return false;
            }
        }

        Axis = axis;
        AxisKnown = true;
        AxisNeutral = false;
        AxisSequence = sequence;

        if (magnitudeSquared >= m_peakAxisMagnitudeSquared) {
            m_peakAxis = axis;
            m_peakAxisMagnitudeSquared = magnitudeSquared;
        }

        if (magnitudeSquared >= switchThresholdSquared) {
            m_stableAxis = axis;
            m_stableAxisKnown = true;
        }

        return true;
    }
}
