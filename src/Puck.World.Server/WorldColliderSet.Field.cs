using Puck.Maths;
using Puck.Physics;
using Puck.Physics.Fields;
using Puck.SignedDistance.Queries;

namespace Puck.World.Server;

public sealed partial class WorldColliderSet {
    // The union owners compose live posed views once; a zero-sized or unresolved root reads empty.
    internal IFieldEvaluator MovingField {
        get {
            if (m_movingField is not null) { return m_movingField; }
            IFieldEvaluator? combined = null;
            IFieldBounds? bounds = null;

            for (var row = 0; (row < m_attachedRows.Count); row++) {
                var view = new PosedField(owner: this, row: row);

                combined = ((combined is null) ? view : new UnionField(a: combined, b: view));
                bounds = ((bounds is null) ? view : new FieldBoundsUnion(a: bounds, b: view));
            }
            return m_movingField = new SampledField(bounds: bounds, field: combined);
        }
    }

    private SampledField? m_movingField;

    private readonly FixedStaticCollider?[] m_liveFields;

    private sealed class SampledField(IFieldEvaluator? field, IFieldBounds? bounds) : IFieldEvaluator, IFieldBounds {
        public FieldEvaluatorCapabilities Capabilities => @field?.Capabilities ?? new(true, FixedQ4816.One);
        public FixedQ4816 StepScale => Capabilities.StepScale;

        public bool TryDistance(FixedPosition position, out FixedQ4816 distance, out int material) {
            distance = FixedQ4816.MaxValue; material = 0;
            return (field?.TryDistance(distance: out distance, material: out material, position: position) ?? true);
        }
        public bool TryFieldGradient(FixedPosition position, out FixedVector3 gradient) => TryFieldGradient(position, FixedQ4816.Zero, out gradient);
        public bool TryFieldGradient(FixedPosition position, FixedQ4816 epsilon, out FixedVector3 gradient) {
            gradient = default;
            return (field?.TryFieldGradient(epsilon: epsilon, gradient: out gradient, position: position) ?? false);
        }
        public bool TryDistanceBounds(FixedPosition lower, FixedPosition upper, out FixedInterval distance) {
            distance = FixedInterval.FromPoint(value: FixedQ4816.MaxValue);
            return (bounds?.TryDistanceBounds(distance: out distance, lower: lower, upper: upper) ?? true);
        }
    }
    private sealed class PosedField(WorldColliderSet owner, int row) : IFieldEvaluator, IFieldBounds {
        public FieldEvaluatorCapabilities Capabilities => owner.m_attachedFields[row]?.Field?.Field.Capabilities ?? new(true, FixedQ4816.One);
        public FixedQ4816 StepScale => Capabilities.StepScale;

        public bool TryDistance(FixedPosition position, out FixedQ4816 distance, out int material) {
            distance = FixedQ4816.MaxValue; material = 0;
            if ((owner.m_rideExcludedRow == row) || owner.m_liveFields[row] is not { } collider) { return true; }
            material = -row - 1;
            return position.TryDelta(FixedPosition.Zero, out var point) && collider.Field!.Field.TryDistance(
                FixedPosition.FromLocal(FixedFieldColliderContact.ToLocal(collider, point)), out distance, out _);
        }
        public bool TryFieldGradient(FixedPosition position, out FixedVector3 gradient) => TryFieldGradient(position, FixedQ4816.Zero, out gradient);
        public bool TryFieldGradient(FixedPosition position, FixedQ4816 epsilon, out FixedVector3 gradient) {
            gradient = default;
            if ((owner.m_rideExcludedRow == row) || (owner.m_liveFields[row] is not { } collider) || !position.TryDelta(FixedPosition.Zero, out var point) ||
                !collider.Field!.Field.TryFieldGradient(FixedPosition.FromLocal(local: FixedFieldColliderContact.ToLocal(collider: collider, world: point)), epsilon, out var local)) { return false; }
            gradient = collider.Rotation.Rotate(vector: local);
            return true;
        }
        public bool TryDistanceBounds(FixedPosition lower, FixedPosition upper, out FixedInterval distance) {
            distance = FixedInterval.FromPoint(value: FixedQ4816.MaxValue);
            if ((owner.m_rideExcludedRow == row) || owner.m_liveFields[row] is not { } collider) { return true; }
            if (!lower.TryDelta(FixedPosition.Zero, out var lo) || !upper.TryDelta(FixedPosition.Zero, out var hi) ||
                (collider.Field?.Bounds is not { } bounds) ||
                !FixedVector3.TrySubtract(lo, collider.Center, out var localLo) || !FixedVector3.TrySubtract(hi, collider.Center, out var localHi) ||
                !SdfFieldEvaluator.TryInverseRotationBounds(collider.Rotation, localLo, localHi, out var box) ||
                !bounds.TryDistanceBounds(FixedPosition.FromLocal(local: box.Minimum), FixedPosition.FromLocal(local: box.Maximum), out var value)) { return false; }
            distance = value;
            return true;
        }
    }
}
