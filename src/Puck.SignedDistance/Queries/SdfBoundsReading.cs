namespace Puck.SignedDistance.Queries;

/// <summary>How <see cref="SdfFieldEvaluator.TryDistanceBounds(Puck.Maths.FixedPosition, Puck.Maths.FixedPosition, SdfBoundsReading, out Puck.Maths.FixedInterval, out int)"/>
/// encloses a box's distances: both readings are certified, the affine one tighter at more work an instruction.</summary>
public enum SdfBoundsReading {
    /// <summary>The interval rules alone (<see cref="Puck.Maths.FixedInterval"/>), every coordinate its own range.</summary>
    Interval,
    /// <summary>The interval rules met at every step with the field formulas' affine lowering
    /// (<see cref="Puck.Maths.FixedAffineForm"/>), which keeps the box's three axes as shared symbols: a rotated box stays
    /// rotated and a blend reads its operands' difference narrow, so a box far from the surface proves so more often.</summary>
    Affine,
}
