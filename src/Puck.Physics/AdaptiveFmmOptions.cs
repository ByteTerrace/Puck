using Puck.Maths;

namespace Puck.Physics;

/// <summary>Accuracy and adaptive-tree controls for <see cref="AdaptiveFmmGravitySolver"/>.</summary>
public sealed record AdaptiveFmmOptions {
    /// <summary>Creates the default: sixteen bodies per leaf, depth 32, and opening angle 0.4.</summary>
    public AdaptiveFmmOptions()
        : this(
        leafCapacity: 16,
        maximumDepth: 32,
        openingAngle: UnitInterval32.FromDouble(value: 0.4d)
    ) {
    }
    /// <summary>Creates an option set.</summary>
    /// <param name="leafCapacity">The positive maximum body count evaluated directly in an ordinary leaf.</param>
    /// <param name="maximumDepth">The adaptive octree depth limit in <c>1..64</c>.</param>
    /// <param name="openingAngle">The cell-pair opening angle in <c>[0, 1]</c>. Zero selects the exact pairwise oracle.</param>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside its documented range.</exception>
    public AdaptiveFmmOptions(int leafCapacity, int maximumDepth, UnitInterval32 openingAngle) {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            value: leafCapacity,
            other: 1
        );
        ArgumentOutOfRangeException.ThrowIfLessThan(
            value: maximumDepth,
            other: 1
        );
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            value: maximumDepth,
            other: 64
        );

        LeafCapacity = leafCapacity;
        MaximumDepth = maximumDepth;
        OpeningAngle = openingAngle;
    }

    /// <summary>Gets the maximum body count evaluated directly in an ordinary leaf.</summary>
    public int LeafCapacity { get; }
    /// <summary>Gets the adaptive octree depth limit.</summary>
    public int MaximumDepth { get; }
    /// <summary>Gets the cell-pair opening angle. Zero selects exact pairwise evaluation.</summary>
    public UnitInterval32 OpeningAngle { get; }
}
