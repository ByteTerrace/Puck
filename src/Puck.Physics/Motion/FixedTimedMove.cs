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
public readonly record struct FixedTimedMove(FixedVector3 Displacement, FixedQ4816 Yaw, ulong DurationTicks, BodyMotionCurve Curve) {
    /// <summary>Reads the cumulative displacement and signed turn at a tick offset from the start.</summary>
    public (FixedVector3 Displacement, FixedQ4816 Yaw) Sample(ulong elapsedTicks) {
        if ((DurationTicks == 0) || (elapsedTicks == 0)) { return default; }
        if (elapsedTicks >= DurationTicks) { return (Displacement, Yaw); }

        var progress = FusedArithmetic.ClampedUnitQuotient(numerator: elapsedTicks, denominator: DurationTicks);
        if (Curve == BodyMotionCurve.Smoothstep) {
            progress = FixedQ4816.Smoothstep(edge0: FixedQ4816.Zero, edge1: FixedQ4816.One, value: progress);
        }
        return (Displacement * progress, Yaw * progress);
    }
}
