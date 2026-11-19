using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>
/// The lanes an instruction carries that the builder computes from its other lanes, so the kernels need not: a scale's
/// distance correction, a repeat's reciprocal spacing, a capsule's inverse squared length, a round cone's slope pair and
/// the rest. Each is one function here, which <see cref="SdfProgramBuilder"/> calls to pack the lane and
/// <see cref="SdfProgram"/>'s lane contract calls to check it: a program refuses an instruction whose derived lane is not
/// what this function answers for its other lanes, bit for bit, so a hand-packed program cannot hand the kernels or the
/// step analysis a lane the builder would never write.
/// </summary>
public static class SdfDerivedLanes {
    /// <summary>The smallest spacing a repeat, a bounded repeat or a cell jitter packs on each axis; a smaller one is
    /// raised to it, which keeps the reciprocal finite.</summary>
    public const float MinRepeatSpacing = 0.001f;
    /// <summary>The smallest shell ratio a log-spherical fold packs; at a ratio of one there are no shells and the baked
    /// reciprocal of the log cell divides by zero.</summary>
    public const float MinLogSphereRatio = 1.0001f;
    /// <summary>The smallest radius a superellipsoid packs on each axis, which keeps its inverse radii finite.</summary>
    public const float MinSuperellipsoidRadius = 0.0001f;
    /// <summary>The largest share of its radius a vesica's half-separation packs, which keeps its tip half-height real
    /// and positive.</summary>
    public const float MaxVesicaSeparationShare = 0.9999f;

    /// <summary>Returns the conservative distance correction of a scale: its smallest axis by magnitude. The field of a
    /// point divided by the scale, times this, is 1-Lipschitz whenever the field is, so it can only underestimate true
    /// distance. A mirror carries its sign in the scale, and the magnitude ignores it.</summary>
    /// <param name="scale">The scale.</param>
    /// <returns>The smallest axis magnitude.</returns>
    public static float ScaleCorrection(Vector3 scale) => MathF.Min(
        x: MathF.Abs(x: scale.X),
        y: MathF.Min(
            x: MathF.Abs(x: scale.Y),
            y: MathF.Abs(x: scale.Z)
        )
    );
    /// <summary>Returns a repeat's spacing as it packs: each axis raised to <see cref="MinRepeatSpacing"/>. The sign is
    /// not absorbed: a negative axis packs as the floor.</summary>
    /// <param name="spacing">The authored spacing.</param>
    /// <returns>The packed spacing.</returns>
    public static Vector3 RepeatSpacing(Vector3 spacing) => Vector3.Max(
        value1: spacing,
        value2: new Vector3(value: MinRepeatSpacing)
    );
    /// <summary>Returns the per-axis reciprocal a repeat, a cell jitter or a superellipsoid packs beside its extents.</summary>
    /// <param name="extents">The packed extents.</param>
    /// <returns>One over each axis.</returns>
    public static Vector3 Reciprocal(Vector3 extents) => (Vector3.One / extents);
    /// <summary>Returns the reciprocal of a log-spherical fold's log cell, which it packs beside the cell.</summary>
    /// <param name="logCell">The log cell, <c>ln(shellRatio)</c>.</param>
    /// <returns>One over the cell.</returns>
    public static float LogSphereInverseCell(float logCell) => (1f / logCell);
    /// <summary>Returns the three constants a polar repeat packs beside its sector count.</summary>
    /// <param name="sectors">The sector count, at least one.</param>
    /// <returns>The sector angle <c>2π/count</c>, its reciprocal, and one over the count.</returns>
    public static (float Angle, float InverseAngle, float InverseCount) PolarSectors(int sectors) {
        var angle = ((2f * MathF.PI) / sectors);

        return (angle, (1f / angle), (1f / sectors));
    }
    /// <summary>Returns the distance correction an axial profile packs: one over the largest scale its profile reaches
    /// (<see cref="SdfProgram.FlareExtrema"/>).</summary>
    /// <param name="amount">The linear flare rate.</param>
    /// <param name="bulge">The mid-span bulge.</param>
    /// <param name="startScale">The scale at the profile's start.</param>
    /// <returns>The correction.</returns>
    public static float AxialProfileCorrection(float amount, float bulge, float startScale) =>
        (1f / SdfProgram.FlareExtrema(amount: amount, bulge: bulge, startScale: startScale).MaxS);
    /// <summary>Returns the inverse squared length a capsule packs beside its endpoint, so its projection multiplies
    /// rather than divides: zero for a zero endpoint (a sphere), and capped at the largest finite float below it.</summary>
    /// <param name="endpoint">The capsule's endpoint.</param>
    /// <returns>The inverse squared length.</returns>
    public static float CapsuleInverseLengthSquared(Vector3 endpoint) {
        var dot = Vector3.Dot(vector1: endpoint, vector2: endpoint);

        return ((dot == 0f) ? 0f : ((float)Math.Min(val1: (1.0 / dot), val2: float.MaxValue)));
    }
    /// <summary>Returns the slope pair a round cone packs: the radius difference over the height (floored), and the
    /// cosine that pairs with it.</summary>
    /// <param name="lowerRadius">The lower radius.</param>
    /// <param name="upperRadius">The upper radius.</param>
    /// <param name="height">The height.</param>
    /// <returns>The slope and its cosine.</returns>
    public static (float Slope, float Cosine) RoundConeSlope(float lowerRadius, float upperRadius, float height) {
        var slope = ((lowerRadius - upperRadius) / MathF.Max(x: height, y: 0.0001f));

        return (slope, MathF.Sqrt(x: MathF.Max(x: (1f - (slope * slope)), y: 0f)));
    }
    /// <summary>Returns the tip half-height a vesica packs, <c>√(r² − d²)</c>.</summary>
    /// <param name="radius">The arcs' radius.</param>
    /// <param name="halfSeparation">The arcs' half-separation.</param>
    /// <returns>The tip half-height.</returns>
    public static float VesicaHalfHeight(float radius, float halfSeparation) =>
        MathF.Sqrt(x: ((radius * radius) - (halfSeparation * halfSeparation)));
    /// <summary>Returns the half-sector angle a regular polygon or a star packs, <c>π/n</c>.</summary>
    /// <param name="sides">The side or point count.</param>
    /// <returns>The half-sector angle.</returns>
    public static float HalfSector(int sides) => (MathF.PI / sides);
}
