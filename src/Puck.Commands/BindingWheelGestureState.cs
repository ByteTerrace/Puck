using System.Numerics;

namespace Puck.Commands;

/// <summary>The input state whose lifetime is exactly one open radial gesture. Presenters share this state machine
/// so cancellation cannot be undone by a later presentation frame and a selector sample cannot leak into the next
/// gesture.</summary>
public sealed class BindingWheelGestureState {
    /// <summary>The most recent authored Axis2D selector sample in this gesture.</summary>
    public Vector2 Axis { get; private set; }

    /// <summary>Whether <see cref="Axis"/> was sampled during this gesture.</summary>
    public bool AxisKnown { get; private set; }

    /// <summary>The selector arbitration sequence carried by <see cref="Axis"/>.</summary>
    public long AxisSequence { get; private set; }

    /// <summary>The first spatial position observed during this gesture. Pointer selection uses this as the
    /// device-relative origin independently from the displayed hub.</summary>
    public Vector2 SpatialNeutral { get; private set; }

    /// <summary>Whether <see cref="SpatialNeutral"/> has been captured during this gesture.</summary>
    public bool SpatialNeutralKnown { get; private set; }

    /// <summary>Whether this gesture was explicitly or synthetically cancelled. The latch survives close so a
    /// release dispatched after presentation closes can still report cancellation; the next <see cref="Open"/>
    /// clears it.</summary>
    public bool Cancelled { get; private set; }

    /// <summary>Whether the presenter currently considers this gesture open.</summary>
    public bool Opened { get; private set; }

    /// <summary>Whether the current presentation frame may arm a sector commit.</summary>
    public bool CanArm => (Opened && !Cancelled);

    /// <summary>Begins a fresh gesture, clearing cancellation and every selector sample from the prior gesture.</summary>
    public void Open() {
        Opened = true;
        Cancelled = false;
        ClearAxis();
        ClearSpatialNeutral();
    }

    /// <summary>Ends presentation and clears transient selector input. Cancellation remains latched until the next
    /// open so a delayed release cannot be mistaken for an ordinary unarmed commit.</summary>
    public void Close() {
        Opened = false;
        ClearAxis();
        ClearSpatialNeutral();
    }

    /// <summary>Latches cancellation for the remainder of this gesture.</summary>
    public void Cancel() => Cancelled = true;

    /// <summary>Records one authored Axis2D selector sample.</summary>
    public void Select(Vector2 axis, long sequence) {
        Axis = axis;
        AxisKnown = true;
        AxisSequence = sequence;
    }

    /// <summary>Captures the first available spatial position as this gesture's device-relative origin. A
    /// position arriving after the opening frame is valid; later positions cannot move the origin.</summary>
    /// <param name="position">The spatial input position in its presenter's coordinate space.</param>
    /// <returns><see langword="true"/> only when this call captured the origin.</returns>
    public bool TryCaptureSpatialNeutral(Vector2 position) {
        if (!Opened || SpatialNeutralKnown) {
            return false;
        }

        SpatialNeutral = position;
        SpatialNeutralKnown = true;

        return true;
    }

    private void ClearAxis() {
        Axis = Vector2.Zero;
        AxisKnown = false;
        AxisSequence = 0L;
    }

    private void ClearSpatialNeutral() {
        SpatialNeutral = Vector2.Zero;
        SpatialNeutralKnown = false;
    }
}
