using System.Numerics;
using Puck.SignedDistance;
using Puck.World.Authoring;

namespace Puck.World.Client;

public static partial class WorldPlacementStamper {
    // A static relieved creation (terrain) larger than one tile bakes as source tiles: one static instance per cell of a
    // world-axis grid, each the creation's field cut
    // by the cell's box grown on every face by TileOverlap, so each is its own bake group with its own tier ladder, demand
    // box and identity, and a tile near the eye refines while a far one stays coarse. A tile is closed on its own: its box
    // closes the cut with a face that lies inside its neighbour's solid, so two neighbours at any two tiers draw
    // overlapping closed solids and no ray from outside passes between them.

    // A tile's edge in relief spacings. A tile's union lattice holds about a hundred samples along the cube its bound
    // plans (about 1.7 edges), so its relief part reaches the relief's spacing with a refinement of three or four, well
    // inside one lattice's fine-sample budget (SdfSampledLattice.MaxFineSamples), where a whole sector in one lattice
    // could not; and a valley sector holds tens of tiles rather than hundreds of groups.
    private const int TileReliefCells = 128;
    // The least tile edge, in metres: a relief finer than this over a large creation would otherwise ask thousands of groups.
    private const float MinimumTileEdge = 32f;
    // How far, as a share of its edge, a tile's box reaches into each neighbour: the cut face of one tile lies this far
    // inside its neighbour's solid, so it must exceed the error of the coarsest tier either neighbour draws while the seam
    // is in view. The strip both draw is kept narrow, since the two tiers' surfaces coincide there.
    private const int TileOverlapShare = 32;

    /// <summary>One source tile of a static relieved creation: its box (the grid cell grown by the overlap, held to the
    /// relieved surface's box), the shapes it leaves out because their boxes cannot reach it, its parts clipped to its
    /// box, and the material its closing face takes.</summary>
    /// <param name="Low">The box's low corner, in world axes.</param>
    /// <param name="High">The box's high corner, in world axes.</param>
    /// <param name="Hidden">The shapes the tile emits nothing of, indexed like <see cref="CreationDocument.Shapes"/>.</param>
    /// <param name="Parts">The tile's parts (<see cref="SdfInstanceRange.Parts"/>), each held to the tile's box.</param>
    /// <param name="Material">The palette slot of the first shape that adds solid to the tile.</param>
    internal readonly record struct SourceTile(Vector3 Low, Vector3 High, bool[] Hidden, List<SdfInstancePart> Parts, int Material);

    /// <summary>Returns the source tiles a static relieved creation bakes as, or <see langword="null"/> when it bakes as
    /// one instance: no relief, or a relieved surface no larger than one tile.</summary>
    /// <param name="document">The creation document.</param>
    /// <param name="hidden">The shapes left out of the opaque emission; <see langword="null"/> for none.</param>
    /// <param name="transform">The stamp transform of the copy.</param>
    /// <returns>The tiles holding surface, in grid order, or <see langword="null"/>.</returns>
    /// <remarks>A shape is left out of a tile only where it cannot change the sign of the tile's field: a shape that adds
    /// solid (a union-family blend) or carves (a subtraction) whose box, grown by its blend's radius and the relief's
    /// amplitude, misses the tile's box. An intersection-family shape reaches everything accumulated before it, so it is
    /// never left out. A tile no solid-adding shape reaches holds no surface and is not emitted.</remarks>
    internal static List<SourceTile>? SourceTilesOf(CreationDocument document, bool[]? hidden, CreationStampTransform transform) {
        if ((document.Noise is not { } relief) || (document.Shapes is not { Count: > 0 } shapes)) { return null; }

        var spacing = (relief.DetailSpacing() * transform.Scale);

        if (!(spacing > 0f)) { return null; }

        // The parts of every shown shape in shape order, then the relief's over the relieved surface's box.
        var parts = WorldShapeDetail.PartsOf(document: document, hidden: hidden, relief: relief, slot: SdfInstancePart.StaticSlot, transform: transform);
        var shown = 0;

        for (var index = 0; (index < shapes.Count); index++) {
            if ((hidden is null) || !hidden[index]) { shown++; }
        }
        if (parts.Count <= shown) { return null; }

        var region = parts[shown];
        var edge = MathF.Max(x: MinimumTileEdge, y: (spacing * TileReliefCells));
        var extent = (region.High - region.Low);

        if (MathF.Max(x: extent.X, y: MathF.Max(x: extent.Y, y: extent.Z)) <= edge) { return null; }

        var overlap = new Vector3(value: (edge / TileOverlapShare));
        // The grid is anchored at the world origin, so an edit that moves the surface's box keeps every cell it still covers.
        var (firstX, lastX) = CellsAlong(low: region.Low.X, high: region.High.X, edge: edge);
        var (firstY, lastY) = CellsAlong(low: region.Low.Y, high: region.High.Y, edge: edge);
        var (firstZ, lastZ) = CellsAlong(low: region.Low.Z, high: region.High.Z, edge: edge);
        var tiles = new List<SourceTile>();

        for (var z = firstZ; (z <= lastZ); z++) {
            for (var y = firstY; (y <= lastY); y++) {
                for (var x = firstX; (x <= lastX); x++) {
                    var cell = new Vector3(x: x, y: y, z: z);
                    var low = Vector3.Max(value1: region.Low, value2: ((cell * edge) - overlap));
                    var high = Vector3.Min(value1: region.High, value2: (((cell + Vector3.One) * edge) + overlap));

                    if (SourceTileOf(document: document, hidden: hidden, low: low, high: high, parts: parts, shown: shown) is { } tile) {
                        tiles.Add(item: tile);
                    }
                }
            }
        }

        return ((tiles.Count > 1) ? tiles : null);
    }
    // One tile of the grid: the shapes whose boxes reach its box, and its parts held to it; null when no shape that adds
    // solid reaches it.
    private static SourceTile? SourceTileOf(CreationDocument document, bool[]? hidden, Vector3 low, Vector3 high, List<SdfInstancePart> parts, int shown) {
        var shapes = document.Shapes!;
        var tileHidden = new bool[shapes.Count];
        var tileParts = new List<SdfInstancePart>(capacity: (shown + 1));
        var material = -1;
        var part = 0;

        for (var index = 0; (index < shapes.Count); index++) {
            if ((hidden is not null) && hidden[index]) {
                tileHidden[index] = true;
                continue;
            }

            var shape = shapes[index];
            var box = parts[part++];
            var blend = (shape.Blend ?? SdfBlendOp.Union);
            var reaches = SdfBoundAlgebra.BoxesMeet(low: low, high: high, otherLow: box.Low, otherHigh: box.High);

            if (!reaches && !SdfBoundAlgebra.IsIntersection(blend: blend)) {
                tileHidden[index] = true;
                continue;
            }
            if (reaches) {
                tileParts.Add(item: box with { Low = Vector3.Max(value1: low, value2: box.Low), High = Vector3.Min(value1: high, value2: box.High) });
            }
            if ((material < 0) && reaches && !SdfBoundAlgebra.Carves(blend: blend)) {
                material = (shape.Material ?? 0);
            }
        }

        if (material < 0) { return null; }

        var relief = parts[shown];

        if (SdfBoundAlgebra.BoxesMeet(low: low, high: high, otherLow: relief.Low, otherHigh: relief.High)) {
            tileParts.Add(item: relief with { Low = Vector3.Max(value1: low, value2: relief.Low), High = Vector3.Min(value1: high, value2: relief.High) });
        }

        return new SourceTile(Low: low, High: high, Hidden: tileHidden, Parts: tileParts, Material: material);
    }
    private static (int First, int Last) CellsAlong(float low, float high, float edge) =>
        (((int)MathF.Floor(x: (low / edge))), (((int)MathF.Ceiling(x: (high / edge))) - 1));
}
