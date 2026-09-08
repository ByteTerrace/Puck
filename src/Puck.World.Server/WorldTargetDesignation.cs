using Puck.Maths;

namespace Puck.World.Server;

/// <summary>One target-register slot's designated target: nothing, a concrete body, or a world-space point. The
/// cleared value is <see cref="None"/>, never <c>default</c> (whose <see cref="Index"/> of 0 would read as body 0) —
/// registers are initialized and reset through <c>WorldPopulation.ClearDesignations</c>.</summary>
/// <param name="Index">The designated body's 0-based entity index, <c>-1</c> for an empty slot, or
/// <see cref="PointIndex"/> when <paramref name="Point"/> carries the target.</param>
/// <param name="Point">The designated world-space point (meaningful only when <see cref="IsPoint"/>).</param>
public readonly record struct WorldTargetDesignation(int Index, FixedVector3 Point) {
    /// <summary>The <see cref="Index"/> sentinel marking a point designation.</summary>
    public const int PointIndex = -2;

    /// <summary>Gets the cleared slot.</summary>
    public static WorldTargetDesignation None { get; } = new(
        Index: -1,
        Point: default
    );
    /// <summary>Gets a value indicating whether this slot designates anything.</summary>
    public bool Exists => (HasBody || IsPoint);
    /// <summary>Gets a value indicating whether this slot designates a concrete body.</summary>
    public bool HasBody => (Index >= 0);
    /// <summary>Gets a value indicating whether this slot designates a world-space point.</summary>
    public bool IsPoint => (Index == PointIndex);

    /// <summary>Creates a point designation.</summary>
    public static WorldTargetDesignation AtPoint(FixedVector3 point) => new(
        Index: PointIndex,
        Point: point
    );
    /// <summary>Creates a body designation.</summary>
    public static WorldTargetDesignation Body(int index) => new(
        Index: index,
        Point: default
    );
}
