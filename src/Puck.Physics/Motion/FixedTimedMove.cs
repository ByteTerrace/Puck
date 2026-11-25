using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Maths;

namespace Puck.Physics.Motion;

/// <summary>How a timed move distributes its displacement and turn over its duration.</summary>
[JsonConverter(typeof(StrictEnumConverter<BodyMotionCurve>))]
public enum BodyMotionCurve : byte {
    Linear,
    Smoothstep,
}

/// <summary>A fixed-point translation and relative yaw sampled on an engine-tick clock.</summary>
/// <param name="Displacement">The whole move's travel, in world units.</param>
/// <param name="Yaw">The whole move's signed relative turn, in radians.</param>
/// <param name="DurationTicks">The move's duration in engine ticks.</param>
/// <param name="Curve">How progress runs over the duration.</param>
/// <param name="Arc">The offset the path stands off the straight line at its middle: the travel is the straight
/// line plus this vector times four times the progress times what is left of it, a parabola that leaves and
/// arrives on the line. Zero is a straight move.</param>
/// <param name="Exclusive">Whether the move owns the body's translation while it runs, so nothing else the body's
/// program integrates (its walk, its fall) is added to it.</param>
public readonly record struct FixedTimedMove(FixedVector3 Displacement, FixedQ4816 Yaw, ulong DurationTicks, BodyMotionCurve Curve, FixedVector3 Arc = default, bool Exclusive = false) {
    /// <summary>Reads the cumulative displacement and signed turn at a tick offset from the start.</summary>
    public (FixedVector3 Displacement, FixedQ4816 Yaw) Sample(ulong elapsedTicks) {
        if ((DurationTicks == 0) || (elapsedTicks == 0)) { return default; }
        if (elapsedTicks >= DurationTicks) { return (Displacement, Yaw); }

        var progress = FusedArithmetic.ClampedUnitQuotient(numerator: elapsedTicks, denominator: DurationTicks);
        if (Curve == BodyMotionCurve.Smoothstep) {
            progress = FixedQ4816.Smoothstep(edge0: FixedQ4816.Zero, edge1: FixedQ4816.One, value: progress);
        }

        var travel = (Displacement * progress);

        if (Arc != FixedVector3.Zero) {
            travel += (Arc * ((progress * (FixedQ4816.One - progress)) * FixedQ4816.FromInteger(value: 4L)));
        }
        return (travel, Yaw * progress);
    }
}
