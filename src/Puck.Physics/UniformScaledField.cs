using Puck.Maths;

namespace Puck.Physics;

/// <summary>A field in a uniformly scaled frame, retaining certified bounds over the rounded inverse transform.</summary>
public sealed class UniformScaledField : IFieldEvaluator, IFieldBounds {
    private readonly IFieldEvaluator m_field;
    private readonly IFieldBounds? m_bounds;

    /// <summary>The positive scale of the frame.</summary>
    public FixedQ4816 Scale { get; }

    /// <summary>Creates an immutable scaled view of a field and its optional bounds.</summary>
    public UniformScaledField(IFieldEvaluator field, IFieldBounds? bounds, FixedQ4816 scale) {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(scale, FixedQ4816.Zero);
        m_field = field; m_bounds = bounds; Scale = scale;
    }

    /// <inheritdoc/>
    public FieldEvaluatorCapabilities Capabilities => m_field.Capabilities;
    /// <inheritdoc/>
    public FixedQ4816 StepScale => Capabilities.StepScale;

    private bool Local(FixedPosition point, out FixedPosition local) {
        local = default;
        if (!point.TryDelta(FixedPosition.Zero, out var delta)) { return false; }
        var x = (FixedInterval.FromPoint(value: delta.X) / FixedInterval.FromPoint(value: Scale));
        var y = (FixedInterval.FromPoint(value: delta.Y) / FixedInterval.FromPoint(value: Scale));
        var z = (FixedInterval.FromPoint(value: delta.Z) / FixedInterval.FromPoint(value: Scale));

        if (x.IsUnbounded || y.IsUnbounded || z.IsUnbounded) { return false; }
        local = FixedPosition.FromLocal(local: (delta / Scale));
        return true;
    }

    /// <inheritdoc/>
    public bool TryDistance(FixedPosition position, out FixedQ4816 distance, out int material) {
        distance = default; material = 0;
        if (!Local(local: out var local, point: position) || !m_field.TryDistance(distance: out var value, material: out material, position: local) ||
            (FixedInterval.FromPoint(value: value) * FixedInterval.FromPoint(value: Scale)).IsUnbounded) { return false; }
        distance = (value * Scale);
        return true;
    }
    /// <inheritdoc/>
    public bool TryFieldGradient(FixedPosition position, out FixedVector3 gradient) => TryFieldGradient(position, FixedQ4816.Zero, out gradient);
    /// <inheritdoc/>
    public bool TryFieldGradient(FixedPosition position, FixedQ4816 epsilon, out FixedVector3 gradient) {
        gradient = default;
        return (Local(local: out var local, point: position) && m_field.TryFieldGradient(local, (epsilon / Scale), out gradient));
    }
    /// <inheritdoc/>
    public bool TryDistanceBounds(FixedPosition lower, FixedPosition upper, out FixedInterval distance) {
        distance = FixedInterval.Entire;
        if ((m_bounds is null) || !lower.TryDelta(FixedPosition.Zero, out var lo) || !upper.TryDelta(FixedPosition.Zero, out var hi)) { return false; }
        var scale = FixedInterval.FromPoint(value: Scale);
        var x = (new FixedInterval(lower: lo.X, upper: hi.X) / scale);
        var y = (new FixedInterval(lower: lo.Y, upper: hi.Y) / scale);
        var z = (new FixedInterval(lower: lo.Z, upper: hi.Z) / scale);

        if (x.IsUnbounded || y.IsUnbounded || z.IsUnbounded) { return false; }
        if (!m_bounds.TryDistanceBounds(FixedPosition.FromLocal(local: new FixedVector3(X: x.Lower, Y: y.Lower, Z: z.Lower)),
            FixedPosition.FromLocal(local: new FixedVector3(X: x.Upper, Y: y.Upper, Z: z.Upper)), out var value)) { return false; }
        distance = (value * scale);
        return true;
    }
}
