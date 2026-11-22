using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>
/// The ladder of cells a bake group's detail is budgeted on by demand: tier <c>t</c> has the cell <see cref="BaseCell"/> times two to the <c>t</c>, and a group's tier floors every spacing its
/// shapes ask of its refinement (<see cref="Puck.SignedDistance.SdfInstanceRange.Detail"/>, <see cref="SdfMeshPathBake.FineCell"/>, a union
/// member's own cell), so a view that cannot resolve a finer cell is never baked one. The tier is a fact of the group's
/// geometry (<see cref="SdfMeshGeometryBuilder.AppendTier"/>): each tier of a geometry is its own cache entry, identified
/// from inputs alone. A residency's bake plans each group at the coarsest tier whose cell stays under half a display pixel
/// at the nearest demand point (<see cref="SdfMeshDemand.PixelAt(SdfMeshBakeGroup, IReadOnlyList{Puck.SignedDistance.DynamicTransform})"/>), clamped to the rungs that change the group's mesh
/// (<see cref="Clamp"/>); an offline plan takes each group's finest. A group arriving with nothing to draw plans its
/// coarsest rung first, its floor, and its demanded tier once the floor has landed (<see cref="SdfMeshPathBake"/>).
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
    /// <summary>The frames a residency's bake spreads one measure of every group's demand over: each frame measures
    /// that share of the boxes its groups stand at, so the measure's cost a frame is that share of the resident
    /// world's, and a group's demanded tier is at most this many frames behind its demand points. A group is
    /// measured once in every hold (<see cref="HoldFrames"/>).</summary>
    public const int MeasureFrames = 8;
    /// <summary>The fewest boxes a frame measures, so a small world is measured whole each frame.</summary>
    public const int MeasureBoxes = 256;
    /// <summary>The mesh pieces a frame reads, at least, looking for a finer tier the mesh region refuses while the floor
    /// the group keeps stands (<see cref="SdfMeshPathBake"/>'s eviction sweep), so a region that refuses meshes costs
    /// each frame that share, and a refused draw waits one sweep at most.</summary>
    public const int EvictionPieces = 2048;
    /// <summary>The frames a residency's bake must have left its detail alone, nothing replaced, landing, waiting its
    /// hold or pressed by the budget, before its detail is settled (<see cref="SdfMeshPathBake.DetailSettled"/>): two
    /// whole measures (<see cref="MeasureFrames"/>), the one under way when the demand last moved and one whole
    /// measure after it, so every group has been read against the demand as it stands.</summary>
    public const int SettleFrames = (2 * MeasureFrames);
    /// <summary>The most groups one residency replans for a finer tier at once; the rest wait, the most demanded first,
    /// so a cut that demands a thousand finer tiers does not stage them all beside their resident meshes.</summary>
    public const int MaxInFlight = 64;
    /// <summary>The most first details (a landed floor's demanded tier) one residency's bake starts in a frame: each is
    /// planned and begun on the frame's thread, so an arrival whose floors land together starts their details over a few
    /// frames, the finest demand first, rather than in one long frame.</summary>
    public const int FirstsPerFrame = 16;
    /// <summary>The most frames a product whose words stand in the mesh region waits for the products still landing
    /// beside it before it takes the draws (<see cref="SdfMeshPathBake.ObserveResidency"/>): a wave of replacements
    /// takes the draws in one frame, so every view renders the wave once, and a stream of them that never pauses
    /// still shows this often.</summary>
    public const int HandFrames = 30;

    /// <summary>The most half rungs a residency's bake coarsens every group's demand by to hold its meshes inside the
    /// mesh region's budget (<see cref="Biased"/>): eight rungs, a cell 256 times the demanded one.</summary>
    public const int MaxBias = 16;
    /// <summary>The frames the meshes a residency's demand would name half a rung finer must have fit its mesh region's
    /// budget before its bake takes one half rung of bias back; the wait doubles each time the budget presses again
    /// soon after.</summary>
    public const int BiasQuietFrames = 600;
    /// <summary>The eighths of the mesh region's limit a residency's bake budgets its meshes to; the rest is the room a
    /// replacement lands in beside the mesh it replaces.</summary>
    public const int BudgetEighths = 7;
    /// <summary>The part of the mesh region's budget, one in this many, a finer replacement is expected to add at
    /// most before it lands: what a surface's scaling says of a tier never baked is a guess, and the budget counts
    /// as full within one such part of its end.</summary>
    public const int ReplacementShare = 16;

    /// <summary>Returns the pixel a bias reads a demand at: <paramref name="pixel"/> times the square root of two to
    /// the <paramref name="halfRungs"/>, so two half rungs demand one tier coarser.</summary>
    /// <param name="pixel">The metres one display pixel covers at the group's box.</param>
    /// <param name="halfRungs">The bias, from zero to <see cref="MaxBias"/>.</param>
    /// <returns>The biased pixel.</returns>
    public static float Biased(float pixel, int halfRungs) => (pixel * MathF.Pow(x: 2f, y: (0.5f * halfRungs)));
    /// <summary>Returns the bytes a mesh is expected to take at another tier of its geometry: a rung doubles the cell,
    /// and a surface's triangles go with the inverse square of it.</summary>
    /// <param name="bytes">The bytes at <paramref name="from"/>.</param>
    /// <param name="from">The tier the bytes were measured at.</param>
    /// <param name="to">The tier asked about.</param>
    /// <returns>The expected bytes.</returns>
    public static long BytesAt(long bytes, int from, int to) => ((to >= from)
        ? (bytes >> Math.Min(val1: 62, val2: (2 * (to - from))))
        : ((bytes > (long.MaxValue >> Math.Min(val1: 62, val2: (2 * (from - to))))) ? long.MaxValue : (bytes << Math.Min(val1: 62, val2: (2 * (from - to))))));
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
    /// <summary>Returns how far a demand pixel may move before the tier it demands under a bias, held to a group's rungs,
    /// changes: its distance to the nearer edge of the band of pixels that demand the same held tier
    /// (<see cref="Of"/>, <see cref="Biased"/>, <see cref="Clamp"/>), taken a hair short so a pixel that moves by exactly
    /// the answer cannot reach the edge. Infinity where neither edge exists (a tier held at both the group's finest and
    /// coarsest rungs) or the pixel is not finite.</summary>
    /// <param name="pixel">The metres one display pixel covers at the group's box, in the group's units.</param>
    /// <param name="halfRungs">The bias, from zero to <see cref="MaxBias"/>.</param>
    /// <param name="finest">The group's finest tier (<see cref="Finest"/>).</param>
    /// <param name="coarsest">The group's coarsest tier (<see cref="Coarsest"/>).</param>
    /// <returns>The distance, in the pixel's units; never negative.</returns>
    public static float Slack(float pixel, int halfRungs, int finest, int coarsest) {
        if (!float.IsFinite(f: pixel)) { return float.PositiveInfinity; }

        var top = Math.Max(val1: finest, val2: coarsest);
        var tier = Clamp(coarsest: coarsest, finest: finest, tier: Of(pixel: Biased(halfRungs: halfRungs, pixel: pixel)));
        // The pixel at which the raw tier reaches a rung: its cell over the pixel share, unbiased.
        var scale = (1f / (PixelShare * MathF.Pow(x: 2f, y: (0.5f * halfRungs))));
        var below = ((tier > finest) ? (pixel - (Cell(tier: tier) * scale)) : float.PositiveInfinity);
        var above = ((tier < top) ? ((Cell(tier: (tier + 1)) * scale) - pixel) : float.PositiveInfinity);

        return MathF.Max(x: 0f, y: (MathF.Min(x: below, y: above) * 0.999f));
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
