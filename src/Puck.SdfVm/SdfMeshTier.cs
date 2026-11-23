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
    /// <summary>The frames a group keeps a tier finer than its demand asks while the budget has room
    /// (<see cref="SdfMeshPathBake"/>'s tier decision): half a minute of frames at a 60 Hz display. Within it a viewer
    /// who turns or walks back finds the detail standing; past it the group is planned again at the tier its demand
    /// asks, and a later demand brings the finer tier back from the library or the store.</summary>
    public const int SpareKeptFrames = 1800;
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
    /// soon after. A bias coarsens every group it reaches as far as its floor, so it is given back within seconds of
    /// the room returning, and the doubling is what holds a budget that stays short.</summary>
    public const int BiasQuietFrames = 120;
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
    public static float Biased(float pixel, int halfRungs) => (pixel * BiasScale(halfRungs: halfRungs));
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
    public static int Of(float pixel) => (float.IsFinite(f: pixel) ? SpacingFloor(spacing: (PixelShare * pixel)) : Last);
    /// <summary>Returns the finest tier that changes a group's mesh: the rung at or under the finest spacing any of its
    /// members asks, since a finer rung floors nothing.</summary>
    /// <param name="spacing">The finest spacing the group's refinement aims for, in metres.</param>
    /// <returns>The tier.</returns>
    public static int Finest(float spacing) => SpacingFloor(spacing: spacing);
    /// <summary>The fewest cells a group's coarsest rung spans along its longest extent: with fewer no lattice edge lies
    /// inside the box, and the contour closes no surface about the group. The ladder's rungs double, so the coarsest
    /// rung's cell fits from this many to twice as many times.</summary>
    public const int CoarsestCells = 2;
    /// <summary>The fewest samples a lattice past a group's floor takes along its longest axis: the corners of
    /// <see cref="CoarsestCells"/> cells.</summary>
    public const int CoarsestSamples = (CoarsestCells + 1);

    /// <summary>Returns a group's floor tier: the first rung at or past its unrefined final lattice's own cell, where
    /// every refinement factor is one. A group arriving with nothing to draw plans it first and keeps it beside a finer
    /// one (<see cref="SdfMeshPathBake"/>'s floor). It is the last rung the group's refinement decides: past it a tier
    /// coarsens the lattice itself (<see cref="LatticeSamples"/>), down to <see cref="Coarsest"/>.</summary>
    /// <param name="longestExtent">The longest extent of the group's planned box, in metres.</param>
    /// <returns>The tier.</returns>
    public static int Floor(float longestExtent) {
        var cell = MathF.Max(x: SdfMeshPathBake.MinimumCell, y: (longestExtent / (SdfWorldPackage.MeshBakeMaxSamplesPerAxis - 1)));

        return Math.Clamp(value: ((int)MathF.Ceiling(x: MathF.Log2(x: (cell / BaseCell)))), min: 0, max: Last);
    }
    /// <summary>Returns the coarsest tier that changes a group's mesh: the last rung whose cell fits
    /// <see cref="CoarsestCells"/> times along the group's longest extent, never finer than its floor
    /// (<see cref="Floor"/>). Each rung between the two halves the lattice's samples along every axis, so a group a view
    /// resolves at a few pixels holds a few cells, and the one bias has a rung to coarsen to at any distance.</summary>
    /// <param name="longestExtent">The longest extent of the group's planned box, in metres.</param>
    /// <returns>The tier.</returns>
    public static int Coarsest(float longestExtent) =>
        Math.Max(val1: Floor(longestExtent: longestExtent), val2: SpacingFloor(spacing: (longestExtent / CoarsestCells)));
    /// <summary>Returns the samples a group's final lattice takes along its longest axis at a tier: the unrefined
    /// lattice's own (<paramref name="most"/>) at or under the group's floor, and past it the corners of the cells the
    /// tier's cell fits along the extent, never fewer than <see cref="CoarsestSamples"/>.</summary>
    /// <param name="longestExtent">The longest extent of the box the lattice spans, in metres: the group's box as its
    /// bake has narrowed it.</param>
    /// <param name="tier">The tier the group bakes at.</param>
    /// <param name="floor">The group's floor tier (<see cref="Floor"/> of its planned box).</param>
    /// <param name="most">The samples of the unrefined lattice along its longest axis.</param>
    /// <returns>The samples, at most <paramref name="most"/>.</returns>
    public static int LatticeSamples(float longestExtent, int tier, int floor, int most) {
        if (tier <= floor) { return most; }

        return Math.Clamp(value: (((int)MathF.Ceiling(x: (longestExtent / Cell(tier: tier)))) + 1), min: Math.Min(val1: CoarsestSamples, val2: most), max: most);
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
        var scale = (1f / (PixelShare * BiasScale(halfRungs: halfRungs)));
        var below = ((tier > finest) ? (pixel - (Cell(tier: tier) * scale)) : float.PositiveInfinity);
        var above = ((tier < top) ? ((Cell(tier: (tier + 1)) * scale) - pixel) : float.PositiveInfinity);

        return MathF.Max(x: 0f, y: (MathF.Min(x: below, y: above) * 0.999f));
    }
    /// <summary>Returns the least pixel that still demands a group's coarsest held tier under a bias: a pixel at or past it
    /// demands what no point demanding the group at all does (<see cref="Of"/> of infinity, held to the rungs), so a
    /// point whose pixel cannot come under it cannot move the group's tier. Infinity for a group held at one rung, whose
    /// tier no pixel moves.</summary>
    /// <param name="halfRungs">The bias, from zero to <see cref="MaxBias"/>.</param>
    /// <param name="finest">The group's finest tier (<see cref="Finest"/>).</param>
    /// <param name="coarsest">The group's coarsest tier (<see cref="Coarsest"/>).</param>
    /// <returns>The pixel, in the group's units.</returns>
    public static float CoarsestEdge(int halfRungs, int finest, int coarsest) =>
        ((coarsest <= finest) ? float.PositiveInfinity : (Cell(tier: coarsest) / (PixelShare * BiasScale(halfRungs: halfRungs))));
    /// <summary>Returns the tier a group demands at a pixel under a bias, held to its rungs. The bias coarsens a demand
    /// as far as the group's floor and no further: a budget's pressure never takes a group a view resolves below its
    /// unrefined lattice, where lettering and thin parts are lost. Past the floor the tier is what the pixel asks with
    /// no bias, so only a group a view cannot resolve finer holds a coarser lattice.</summary>
    /// <param name="pixel">The metres one display pixel covers at the group's box, in the group's units.</param>
    /// <param name="halfRungs">The bias, from zero to <see cref="MaxBias"/>.</param>
    /// <param name="finest">The group's finest tier (<see cref="Finest"/>).</param>
    /// <param name="floor">The group's floor tier (<see cref="Floor"/>).</param>
    /// <param name="coarsest">The group's coarsest tier (<see cref="Coarsest"/>).</param>
    /// <returns>The tier.</returns>
    public static int Demanded(float pixel, int halfRungs, int finest, int floor, int coarsest) {
        var biased = Clamp(coarsest: coarsest, finest: finest, tier: Of(pixel: Biased(halfRungs: halfRungs, pixel: pixel)));

        return ((biased <= floor) ? biased : Math.Max(val1: floor, val2: Clamp(coarsest: coarsest, finest: finest, tier: Of(pixel: pixel))));
    }
    /// <summary>Returns how far a demand pixel may move before the tier <see cref="Demanded"/> answers changes, never
    /// more than it can: the nearer of the biased ladder's edges up to the floor and the unbiased ladder's past it
    /// (<see cref="Slack"/> of each).</summary>
    /// <param name="pixel">The metres one display pixel covers at the group's box, in the group's units.</param>
    /// <param name="halfRungs">The bias, from zero to <see cref="MaxBias"/>.</param>
    /// <param name="finest">The group's finest tier.</param>
    /// <param name="floor">The group's floor tier.</param>
    /// <param name="coarsest">The group's coarsest tier.</param>
    /// <returns>The distance, in the pixel's units; never negative.</returns>
    public static float DemandedSlack(float pixel, int halfRungs, int finest, int floor, int coarsest) => MathF.Min(
        x: Slack(coarsest: floor, finest: finest, halfRungs: halfRungs, pixel: pixel),
        y: Slack(coarsest: coarsest, finest: Math.Max(val1: finest, val2: floor), halfRungs: 0, pixel: pixel));
    /// <summary>Returns the least pixel that still demands a group's coarsest tier by <see cref="Demanded"/>: the
    /// unbiased ladder's edge where the coarsest rung lies past the floor, else the biased one's
    /// (<see cref="CoarsestEdge"/>).</summary>
    /// <param name="halfRungs">The bias, from zero to <see cref="MaxBias"/>.</param>
    /// <param name="finest">The group's finest tier.</param>
    /// <param name="floor">The group's floor tier.</param>
    /// <param name="coarsest">The group's coarsest tier.</param>
    /// <returns>The pixel, in the group's units.</returns>
    public static float DemandedCoarsestEdge(int halfRungs, int finest, int floor, int coarsest) => ((coarsest > Math.Max(val1: finest, val2: floor))
        ? CoarsestEdge(coarsest: coarsest, finest: Math.Max(val1: finest, val2: floor), halfRungs: 0)
        : CoarsestEdge(coarsest: coarsest, finest: finest, halfRungs: halfRungs));
    /// <summary>Returns a demanded tier held to the rungs that change a group's mesh.</summary>
    /// <param name="tier">The demanded tier.</param>
    /// <param name="finest">The group's finest tier (<see cref="Finest"/>).</param>
    /// <param name="coarsest">The group's coarsest tier (<see cref="Coarsest"/>).</param>
    /// <returns>The tier, between the two; the finest where the finest is past the coarsest.</returns>
    public static int Clamp(int tier, int finest, int coarsest) => Math.Clamp(value: tier, min: finest, max: Math.Max(val1: finest, val2: coarsest));

    // The coarsest tier whose cell is at most a spacing (the exponent of its ratio to the base cell, exact where a
    // rounded logarithm can land on the rung above); zero for a spacing under the base cell or none.
    private static int SpacingFloor(float spacing) =>
        (((spacing > 0f) && float.IsFinite(f: spacing)) ? Math.Clamp(value: MathF.ILogB(x: (spacing / BaseCell)), min: 0, max: Last) : 0);
    // The square root of two to a bias, from zero to MaxBias half rungs: what a bias scales a pixel by (Biased).
    private static readonly float[] BiasScales = [.. Enumerable.Range(start: 0, count: (MaxBias + 1)).Select(selector: static halfRungs => MathF.Pow(x: 2f, y: (0.5f * halfRungs)))];
    private static float BiasScale(int halfRungs) => (((uint)halfRungs <= MaxBias) ? BiasScales[halfRungs] : MathF.Pow(x: 2f, y: (0.5f * halfRungs)));
}
