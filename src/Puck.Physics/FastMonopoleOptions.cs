using Puck.Maths;

namespace Puck.Physics;

/// <summary>Accuracy and tree-shape controls for <see cref="FastMonopoleGravitySolver"/>.</summary>
public sealed record FastMonopoleOptions {
    /// <summary>Creates the balanced default: eight sources per leaf, depth 32, and opening angle 0.5.</summary>
    public FastMonopoleOptions()
        : this(
        leafCapacity: 8,
        maximumDepth: 32,
        openingAngle: UnitInterval32.FromDouble(value: 0.5d)
    ) {
    }
    /// <summary>Creates an option set.</summary>
    /// <param name="leafCapacity">The positive maximum source count evaluated directly in an ordinary leaf.</param>
    /// <param name="maximumDepth">The octree depth limit in <c>1..64</c>.</param>
    /// <param name="openingAngle">The Barnes-Hut opening angle in <c>[0, 1]</c>. Zero selects the exact pairwise oracle.</param>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside its documented range.</exception>
    public FastMonopoleOptions(int leafCapacity, int maximumDepth, UnitInterval32 openingAngle) {
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

    /// <summary>Gets the maximum source count evaluated directly in an ordinary leaf.</summary>
    public int LeafCapacity { get; }
    /// <summary>Gets the octree depth limit.</summary>
    public int MaximumDepth { get; }
    /// <summary>Gets the opening angle. Zero selects exact pairwise evaluation.</summary>
    public UnitInterval32 OpeningAngle { get; }
}
