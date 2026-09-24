using Puck.Maths;

namespace Puck.Commands;

/// <summary>What a mapped point landed on.</summary>
public enum SourceHitOutcome : byte {
    /// <summary>The point lies on the source: <see cref="SourceHit.Coordinate"/> is inside the crop.</summary>
    OnSource = 0,
    /// <summary>The ray runs parallel to the placement's plane, the plane lies behind the ray's origin, or the distance
    /// to it is past the fixed-point range; nothing else in the hit is meaningful.</summary>
    NoIntersection = 1,
    /// <summary>The point meets the placement's plane or the display outside the placement's face.
    /// <see cref="SourceHit.Face"/> and <see cref="SourceHit.Coordinate"/> still report where it lies, as a drag that
    /// leaves a pane reads it.</summary>
    OutsidePlacement = 2,
    /// <summary>The point lies on the face but outside the part a warp pass samples, such as on a bezel.</summary>
    OutsideWarp = 3,
    /// <summary>The point lies on a letterbox bar of a <see cref="SourceFit.Contain"/> fit.</summary>
    Letterbox = 4,
    /// <summary>The placement's warp pass declares no inverse, so it refuses as an input path; only
    /// <see cref="SourceHit.Distance"/> and <see cref="SourceHit.Face"/> are meaningful.</summary>
    WarpNotInvertible = 5,
}
/// <summary>A point mapped through a <see cref="SourceMapping"/>, in fixed point. The same mapping and the same input
/// produce a bit-identical hit on every run.</summary>
/// <param name="Outcome">What the point landed on.</param>
/// <param name="Distance">The ray parameter of a surface hit, in multiples of the ray's direction, or zero for a pane.</param>
/// <param name="Face">The point on the placement's face, <c>u</c> right and <c>v</c> down, in <c>[0, 1)</c> on the face.</param>
/// <param name="Coordinate">The point in source pixels, <c>x</c> right and <c>y</c> down from the source's top-left
/// corner; pixel <c>(i, j)</c> covers <c>[i, i + 1) × [j, j + 1)</c>.</param>
public readonly record struct SourceHit(SourceHitOutcome Outcome, FixedQ4816 Distance, FixedVector2 Face, FixedVector2 Coordinate) {
    /// <summary>Gets whether the point lies on the source.</summary>
    public bool IsOnSource => (Outcome == SourceHitOutcome.OnSource);
    /// <summary>Gets the column of the source pixel <see cref="Coordinate"/> lies in.</summary>
    public long PixelX => (Coordinate.X.Value >> FixedQ4816.FractionBitCount);
    /// <summary>Gets the row of the source pixel <see cref="Coordinate"/> lies in.</summary>
    public long PixelY => (Coordinate.Y.Value >> FixedQ4816.FractionBitCount);
}
