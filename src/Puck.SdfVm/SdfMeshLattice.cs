using System.Numerics;

namespace Puck.SdfVm;

/// <summary>
/// The sizing of a regular sample lattice over a box: how many samples a cell takes along each axis, the margin a
/// contoured level widens its box by, and where a sample's index lies. The bake's field levels, their audit and the
/// skin's split estimate all lay lattices this way, so a change to the sizing is one change.
/// </summary>
internal static class SdfMeshLattice {
    /// <summary>The cells of margin a level's lattice keeps clear of its box on every side, beyond the level's offset.</summary>
    public const float MarginCells = 2f;
    /// <summary>The fewest samples a lattice takes along an axis: a cell needs both of its corners.</summary>
    public const int MinimumSamples = 2;

    /// <summary>Gets the samples a lattice takes along each axis to span an extent.</summary>
    /// <param name="extent">The box's extent, in metres.</param>
    /// <param name="cell">The lattice's cell, in metres; positive.</param>
    /// <returns>The samples per axis: the cells that cover the extent plus one, at least <see cref="MinimumSamples"/>.</returns>
    public static (int X, int Y, int Z) Dims(Vector3 extent, float cell) => (
        X: Math.Max(val1: MinimumSamples, val2: (((int)MathF.Ceiling(x: (extent.X / cell))) + 1)),
        Y: Math.Max(val1: MinimumSamples, val2: (((int)MathF.Ceiling(x: (extent.Y / cell))) + 1)),
        Z: Math.Max(val1: MinimumSamples, val2: (((int)MathF.Ceiling(x: (extent.Z / cell))) + 1))
    );
    /// <summary>Gets the lattice a contoured level lays over a box: the box widened on every side by the level's offset and
    /// <see cref="MarginCells"/> cells.</summary>
    /// <param name="low">The box's low corner, in metres.</param>
    /// <param name="high">The box's high corner, in metres.</param>
    /// <param name="cell">The level's cell, in metres; positive.</param>
    /// <param name="offset">How far outside the surface the level is contoured, in metres; at least zero.</param>
    /// <returns>The lattice's origin and the samples per axis.</returns>
    public static (Vector3 Origin, (int X, int Y, int Z) Dims) Over(Vector3 low, Vector3 high, float cell, float offset) {
        var margin = new Vector3(value: (offset + (MarginCells * cell)));
        var origin = (low - margin);

        return (origin, Dims(
            cell: cell,
            extent: ((high + margin) - origin)
        ));
    }
    /// <summary>Gets the position of a sample of a lattice laid out with the x axis fastest.</summary>
    /// <param name="dims">The samples per axis.</param>
    /// <param name="index">The sample's index, below the product of <paramref name="dims"/>.</param>
    /// <param name="origin">The lattice's origin, in metres.</param>
    /// <param name="cell">The lattice's cell, in metres.</param>
    /// <returns>The sample's position.</returns>
    public static Vector3 PointOf((int X, int Y, int Z) dims, int index, Vector3 origin, float cell) =>
        (origin + (new Vector3(x: (index % dims.X), y: ((index / dims.X) % dims.Y), z: (index / (dims.X * dims.Y))) * cell));
}
