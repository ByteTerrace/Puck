using Puck.Maths;

namespace Puck.Physics;

/// <summary>One already-perceived creature, expressed relative to the observer's frozen position.</summary>
/// <param name="Index">The stable slot used only to resolve coincident separation.</param>
/// <param name="Offset">Neighbor position minus observer position.</param>
/// <param name="Velocity">Neighbor world-space velocity from the same frozen image.</param>
/// <param name="CohesionAffinity">Attraction weight in [0,1], independent of alignment.</param>
/// <param name="AlignmentAffinity">Heading-influence weight in [0,1], independent of affection.</param>
public readonly record struct FixedFlockNeighbor(int Index, FixedVector3 Offset, FixedVector3 Velocity,
    FixedQ4816 CohesionAffinity, FixedQ4816 AlignmentAffinity);

/// <summary>Caller-authored relative steering weights and personal separation distance.</summary>
/// <param name="SeparationRadius">Nonnegative distance inside which repulsion increases linearly.</param>
/// <param name="Separation">Repulsion weight in [0,1].</param>
/// <param name="Alignment">Mean-velocity direction weight in [0,1].</param>
/// <param name="Cohesion">Weighted-centroid direction weight in [0,1].</param>
/// <param name="Goal">Goal-direction weight in [0,1].</param>
/// <param name="Inertia">Current-heading weight in [0,1].</param>
public readonly record struct FixedFlockWeights(FixedQ4816 SeparationRadius, FixedQ4816 Separation,
    FixedQ4816 Alignment, FixedQ4816 Cohesion, FixedQ4816 Goal, FixedQ4816 Inertia);

/// <summary>Independent steering terms and their bounded blend, all in world axes.</summary>
/// <param name="Separation">Mean local repulsion.</param>
/// <param name="Alignment">Direction of the affinity-weighted mean velocity.</param>
/// <param name="Cohesion">Direction toward the affinity-weighted centroid.</param>
/// <param name="Desired">Blended direction with magnitude capped at one, to fixed-point rounding precision.</param>
public readonly record struct FixedFlockSteeringResult(FixedVector3 Separation, FixedVector3 Alignment,
    FixedVector3 Cohesion, FixedVector3 Desired);

/// <summary>Policy-free flock steering over a frozen, bounded perception sample.</summary>
/// <remarks>
/// The caller owns perception, social affinities, route selection, locomotion, and collision correctness.
/// This kernel blends Reynolds-style separation, alignment and cohesion with goal and heading persistence.
/// It writes no body state and contains no clock or randomness. Grounded callers supply their support normal;
/// free/underwater callers supply zero and separately enforce obstacle and medium traversal constraints.
/// All accumulation is order-independent integer addition; weighted means round once through Puck.Maths.
/// </remarks>
public static class FixedFlockSteering {
    /// <summary>Builds a bounded movement preference without changing any supplied observation.</summary>
    /// <param name="selfIndex">Observer slot, used for coincident-pair antisymmetry and self exclusion.</param>
    /// <param name="velocity">Current world-space velocity.</param>
    /// <param name="goalDirection">Direction toward the selected target or route waypoint; zero means no goal.</param>
    /// <param name="planeNormal">Nonzero for tangent-plane motion, zero for unconstrained 3D.</param>
    /// <param name="neighbors">Already-perceived neighbors; the kernel performs no hidden world reads.</param>
    /// <param name="weights">Authored steering weights.</param>
    /// <returns>The component terms and movement preference.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A weight or affinity leaves [0,1], or separation radius is negative.</exception>
    public static FixedFlockSteeringResult Evaluate(int selfIndex, in FixedVector3 velocity,
        in FixedVector3 goalDirection, in FixedVector3 planeNormal, ReadOnlySpan<FixedFlockNeighbor> neighbors,
        in FixedFlockWeights weights) {
        ArgumentOutOfRangeException.ThrowIfNegative(weights.SeparationRadius);
        Unit(weights.Separation);
        Unit(weights.Alignment);
        Unit(weights.Cohesion);
        Unit(weights.Goal);
        Unit(weights.Inertia);
        var normal = planeNormal.Normalize();
        var separation = new Mean();
        var alignment = new Mean();
        var cohesion = new Mean();
        foreach (ref readonly var neighbor in neighbors) {
            Unit(neighbor.CohesionAffinity);
            Unit(neighbor.AlignmentAffinity);
            if (neighbor.Index == selfIndex) { continue; }
            cohesion.Add(neighbor.Offset, neighbor.CohesionAffinity);
            alignment.Add(neighbor.Velocity, neighbor.AlignmentAffinity);
            var repulsion = FixedVector3.Zero;
            if (weights.SeparationRadius > FixedQ4816.Zero) {
                var distance = neighbor.Offset.Length;
                if (distance < weights.SeparationRadius) {
                    var direction = neighbor.Offset == FixedVector3.Zero
                        ? CoincidentDirection(selfIndex, neighbor.Index, normal)
                        : -PlanarDirection(neighbor.Offset, normal);
                    repulsion = direction * ((weights.SeparationRadius - distance) / weights.SeparationRadius);
                }
            }
            separation.Add(repulsion, FixedQ4816.One);
        }
        var separate = separation.Value();
        var align = PlanarDirection(alignment.Value(), normal);
        var cohere = PlanarDirection(cohesion.Value(), normal);
        var preference = separate * weights.Separation
            + align * weights.Alignment
            + cohere * weights.Cohesion;
        var desired = BlendPreference(preference, velocity, goalDirection, normal, weights.Goal, weights.Inertia);
        return new FixedFlockSteeringResult(separate, align, cohere, desired);
    }

    /// <summary>Blends cached neighbor influence with the current goal and heading, then caps speed at one.</summary>
    /// <param name="neighborPreference">Unclamped weighted separation, alignment, and cohesion; magnitude at most three.</param>
    /// <param name="velocity">Current world-space travel direction.</param>
    /// <param name="goalDirection">Current goal or waypoint direction, or zero.</param>
    /// <param name="planeNormal">Current support normal, or zero for volume motion.</param>
    /// <param name="goalWeight">Goal weight in [0,1].</param>
    /// <param name="inertiaWeight">Heading weight in [0,1].</param>
    /// <returns>A world-space preference with magnitude capped at one, to fixed-point precision.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Either weight leaves [0,1].</exception>
    public static FixedVector3 BlendPreference(in FixedVector3 neighborPreference, in FixedVector3 velocity,
        in FixedVector3 goalDirection, in FixedVector3 planeNormal, FixedQ4816 goalWeight, FixedQ4816 inertiaWeight) {
        Unit(goalWeight);
        Unit(inertiaWeight);
        var normal = planeNormal.Normalize();
        var local = normal == FixedVector3.Zero ? neighborPreference :
            FixedVector3.Cross(normal, FixedVector3.Cross(neighborPreference, normal));
        var desired = local + PlanarDirection(goalDirection, normal) * goalWeight
            + PlanarDirection(velocity, normal) * inertiaWeight;
        return desired.LengthSquared > FixedQ4816.One ? desired.Normalize() : desired;
    }

    private static void Unit(FixedQ4816 value) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, FixedQ4816.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, FixedQ4816.One);
    }
    private static FixedVector3 PlanarDirection(in FixedVector3 direction, in FixedVector3 normal) {
        // Normalize before projection: even full-width positions/velocities cannot overflow the dot or subtraction.
        var unit = direction.Normalize();
        return normal == FixedVector3.Zero ? unit : FixedVector3.Cross(normal, FixedVector3.Cross(unit, normal)).Normalize();
    }
    private static FixedVector3 CoincidentDirection(int self, int other, in FixedVector3 normal) {
        var lower = Math.Min(self, other);
        var upper = Math.Max(self, other);
        var axis = unchecked((uint)lower * 0x9E3779B9u + (uint)upper * 0x85EBCA6Bu) % 3;
        var direction = axis switch {
            0 => new FixedVector3(FixedQ4816.One, FixedQ4816.Zero, FixedQ4816.Zero),
            1 => new FixedVector3(FixedQ4816.Zero, FixedQ4816.One, FixedQ4816.Zero),
            _ => new FixedVector3(FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.One),
        };
        direction = PlanarDirection(direction, normal);
        if (direction == FixedVector3.Zero) {
            FixedVector3.OrthonormalBasis(normal, out direction, out _);
        }
        return self < other ? direction : -direction;
    }

    // A component contributes at most 2^63 * 2^16. Even Int32.MaxValue observations fit within Int128.
    // Positive weights make the quotient a convex mean inside the original Int64 carrier, including MinValue.
    private struct Mean {
        private Int128 m_x;
        private Int128 m_y;
        private Int128 m_z;
        private ulong m_weight;

        public void Add(in FixedVector3 value, FixedQ4816 weight) {
            m_x += (Int128)value.X.Value * weight.Value;
            m_y += (Int128)value.Y.Value * weight.Value;
            m_z += (Int128)value.Z.Value * weight.Value;
            m_weight += (ulong)weight.Value;
        }
        public readonly FixedVector3 Value() => m_weight == 0 ? FixedVector3.Zero : new(Round(m_x), Round(m_y), Round(m_z));
        private readonly FixedQ4816 Round(Int128 sum) {
            if (!FusedArithmetic.TryDivideMagnitudeRounded((UInt128)Int128.Abs(sum), m_weight, 0, out var magnitude)) {
                throw new InvalidOperationException("A bounded weighted mean could not be represented.");
            }
            var signed = sum < 0 ? -(Int128)magnitude : (Int128)magnitude;
            return FixedQ4816.FromRawBits(checked((long)signed));
        }
    }
}
