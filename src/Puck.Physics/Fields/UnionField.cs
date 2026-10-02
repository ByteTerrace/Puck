using Puck.Maths;

namespace Puck.Physics.Fields;

/// <summary>The union of two fields: the lesser distance, and that field's gradient and material.</summary>
public sealed class UnionField : IFieldEvaluator {
    private readonly IFieldEvaluator m_a;
    private readonly IFieldEvaluator m_b;

    public UnionField(IFieldEvaluator a, IFieldEvaluator b) {
        ArgumentNullException.ThrowIfNull(argument: a);
        ArgumentNullException.ThrowIfNull(argument: b);

        m_a = a;
        m_b = b;
    }

    /// <inheritdoc/>
    public FieldEvaluatorCapabilities Capabilities => new(WarpFree: (m_a.Capabilities.WarpFree && m_b.Capabilities.WarpFree));

    private bool Nearer(FixedPosition position, out bool useB) {
        var hasA = m_a.TryDistance(
            distance: out var da,
            material: out _,
            position: position
        );
        var hasB = m_b.TryDistance(
            distance: out var db,
            material: out _,
            position: position
        );

        useB = (hasB && (!hasA || (db < da)));

        return (
            hasA ||
            hasB
        );
    }

    /// <inheritdoc/>
    public bool TryDistance(FixedPosition position, out FixedQ4816 distance, out int material) {
        var hasA = m_a.TryDistance(
            distance: out var da,
            material: out var ma,
            position: position
        );
        var hasB = m_b.TryDistance(
            distance: out var db,
            material: out var mb,
            position: position
        );

        if (
            hasA &&
            hasB
        ) {
            var useB = (db < da);

            distance = (useB
                ? db
                : da
            );
            material = (useB
                ? mb
                : ma
            );

            return true;
        }

        distance = (hasA
            ? da
            : db
        );
        material = (hasA
            ? ma
            : mb
        );

        return (
            hasA ||
            hasB
        );
    }
    /// <inheritdoc/>
    public bool TryFieldGradient(FixedPosition position, out FixedVector3 gradient) {
        if (!Nearer(
            position: position,
            useB: out var useB
        )) {
            gradient = default;

            return false;
        }

        return (useB
            ? m_b
            : m_a).TryFieldGradient(
            gradient: out gradient,
            position: position
        );
    }
    /// <inheritdoc/>
    public bool TryFieldGradient(FixedPosition position, FixedQ4816 epsilon, out FixedVector3 gradient) {
        if (!Nearer(
            position: position,
            useB: out var useB
        )) {
            gradient = default;

            return false;
        }

        return (useB
            ? m_b
            : m_a).TryFieldGradient(
            epsilon: epsilon,
            gradient: out gradient,
            position: position
        );
    }
}
