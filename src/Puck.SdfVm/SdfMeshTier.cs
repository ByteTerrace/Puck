using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>
/// The ladder of cells a bake group's detail is budgeted on by demand: tier <c>t</c> has the cell <see cref="BaseCell"/> times two to the <c>t</c>, and a group's tier floors every spacing its
/// shapes ask of its refinement (<see cref="Puck.SignedDistance.SdfInstanceRange.Detail"/>, <see cref="SdfMeshPathBake.FineCell"/>, a union
/// member's own cell), so a view that cannot resolve a finer cell is never baked one. The tier is a fact of the group's
/// geometry (<see cref="SdfMeshGeometryBuilder.AppendTier"/>): each tier of a geometry is its own cache entry, identified
/// from inputs alone. A residency's bake plans each group at the coarsest tier whose cell stays under half a display pixel
/// at the nearest demand point (<see cref="SdfMeshDemand.PixelAt(SdfMeshBakeGroup, IReadOnlyList{Puck.SignedDistance.DynamicTransform})"/>), clamped to the rungs that change the group's mesh
/// (<see cref="Clamp"/>); an offline plan takes each group's finest.
/// </summary>
public static class SdfMeshTier {
    /// <summary>The cell of tier zero, in metres: a millimetre, under the finest spacing any shape declares.</summary>
    public const float BaseCell = 0.001f;
    /// <summary>The display pixels a tier's cell stays under at the nearest demand point: half a pixel.</summary>
    public const float PixelShare = 0.5f;
    /// <summary>The tier no group passes: its cell spans the longest lattice any group bakes.</summary>
    public const int Last = 24;
    /// <summary>The frames a finer tier must stay demanded before a group is planned again at it, so a view standing at a
    /// rung's boundary does not bake the group at both.</summary>
    public const int HoldFrames = 8;
    /// <summary>The most groups one residency replans for a finer tier at once; the rest wait, the most demanded first,
    /// so a cut that demands a thousand finer tiers does not stage them all beside their resident meshes.</summary>
    public const int MaxInFlight = 64;

    /// <summary>Returns a tier's cell, in metres.</summary>
    /// <param name="tier">The tier, from zero to <see cref="Last"/>.</param>
    /// <returns>The cell.</returns>
    public static float Cell(int tier) => (BaseCell * (1 << Math.Clamp(value: tier, min: 0, max: Last)));
    /// <summary>Returns the coarsest tier whose cell stays under <see cref="PixelShare"/> of a display pixel that covers
    /// <paramref name="pixel"/> metres: what a view at that distance demands.</summary>
    /// <param name="pixel">The metres one display pixel covers at the group's box from the finest demand point; infinity
    /// with no demand point, which demands <see cref="Last"/>.</param>
    /// <returns>The tier.</returns>
    public static int Of(float pixel) => (float.IsFinite(f: pixel) ? Floor(spacing: (PixelShare * pixel)) : Last);
    /// <summary>Returns the finest tier that changes a group's mesh: the rung at or under the finest spacing any of its
    /// members asks, since a finer rung floors nothing.</summary>
    /// <param name="spacing">The finest spacing the group's refinement aims for, in metres.</param>
    /// <returns>The tier.</returns>
    public static int Finest(float spacing) => Floor(spacing: spacing);
    /// <summary>Returns the coarsest tier that changes a group's mesh: the first rung at or past the final lattice's own
    /// cell, where every refinement factor is one.</summary>
    /// <param name="longestExtent">The longest extent of the group's planned box, in metres.</param>
    /// <returns>The tier.</returns>
    public static int Coarsest(float longestExtent) {
        var cell = MathF.Max(x: SdfMeshPathBake.MinimumCell, y: (longestExtent / (SdfWorldPackage.MeshBakeMaxSamplesPerAxis - 1)));

        return Math.Clamp(value: ((int)MathF.Ceiling(x: MathF.Log2(x: (cell / BaseCell)))), min: 0, max: Last);
    }
    /// <summary>Returns a demanded tier held to the rungs that change a group's mesh.</summary>
    /// <param name="tier">The demanded tier.</param>
    /// <param name="finest">The group's finest tier (<see cref="Finest"/>).</param>
    /// <param name="coarsest">The group's coarsest tier (<see cref="Coarsest"/>).</param>
    /// <returns>The tier, between the two; the finest where the finest is past the coarsest.</returns>
    public static int Clamp(int tier, int finest, int coarsest) => Math.Clamp(value: tier, min: finest, max: Math.Max(val1: finest, val2: coarsest));

    // The coarsest tier whose cell is at most a spacing; zero for a spacing under the base cell or none.
    private static int Floor(float spacing) =>
        (((spacing > 0f) && float.IsFinite(f: spacing)) ? Math.Clamp(value: ((int)MathF.Floor(x: MathF.Log2(x: (spacing / BaseCell)))), min: 0, max: Last) : 0);
}
