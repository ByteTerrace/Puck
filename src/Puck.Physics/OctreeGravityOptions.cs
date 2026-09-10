using Puck.Maths;

namespace Puck.Physics;

/// <summary>The accuracy and tree-shape controls shared by every octree gravity solver. A derived option set
/// contributes only its defaults.</summary>
public abstract record OctreeGravityOptions {
    /// <summary>Validates and stores the shared controls.</summary>
    /// <param name="leafCapacity">The positive maximum body count evaluated directly in an ordinary leaf.</param>
    /// <param name="maximumDepth">The octree depth limit in <c>1..64</c>.</param>
    /// <param name="openingAngle">The cell-pair opening angle in <c>[0, 1]</c>. Zero selects the exact pairwise oracle.</param>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside its documented range.</exception>
    protected OctreeGravityOptions(int leafCapacity, int maximumDepth, UnitInterval32 openingAngle) {
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
    /// <summary>Gets the octree depth limit.</summary>
    public int MaximumDepth { get; }
    /// <summary>Gets the cell-pair opening angle. Zero selects exact pairwise evaluation.</summary>
    public UnitInterval32 OpeningAngle { get; }
}
