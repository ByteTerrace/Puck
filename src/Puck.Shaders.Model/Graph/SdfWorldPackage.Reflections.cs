using Puck.Maths;

namespace Puck.Shaders;

public static partial class SdfWorldPackage {
    /// <summary>The screen-space reflections as views reads them: <see cref="ViewsReflectionWords"/> words a cell of the
    /// render extent, the cell a square of the pixels one mirror ray serves (<see cref="ReflectionCellOf"/>), laid out by
    /// cell (<c>passes/sdf-views-reflections.hlsli</c> owns the packing): the colour the cell's ray reaches, at the stored
    /// light scale (<see cref="LightScale"/>), the shares of the reflection it answers by that colour and by the open sky,
    /// and the tangent plane of the pixel it was traced from, by which views blends each pixel's nearest cells over the
    /// irradiance volume's reflection.</summary>
    public const string ViewsReflections = "sdfViewsReflections";
    /// <summary>The screen-space reflections as the reflection pass writes them.</summary>
    public const string ViewsReflectionsWritten = "sdfViewsReflectionsRW";
    /// <summary>The preceding frame's resolved colour the reflection pass reads a mirror ray's hit from: the history colour
    /// (<see cref="HistoryColor"/>) of a temporal fragment, which holds the lit image premultiplied by coverage.</summary>
    public const string ReflectionHistory = "sdfReflectionHistory";
    /// <summary>The depth pyramid the reflection pass marches its mirror rays through, which the pyramid pass writes in a
    /// fragment that reflects (<see cref="ReflectionDepthWritten"/>): levels one to <see cref="ReflectionDepthLevels"/> of the
    /// render extent, each texel the inverse forward depth of the nearest surface of its block, zero where none is
    /// covered (<c>frame/sdf-reflection-depth.hlsli</c> owns the layout).</summary>
    public const string ReflectionDepth = "sdfReflectionDepth";
    /// <summary>The pyramid pass's writable binding of <see cref="ReflectionDepth"/>.</summary>
    public const string ReflectionDepthWritten = "sdfReflectionDepthRW";
    /// <summary>The pass-group value holding the edge, in render pixels, of the square cell one mirror ray serves where
    /// the view's fragment holds the reflection pass (<see cref="Parts.ViewsReflections"/>), so views reads the records
    /// and the reflection pass traces a cell a ray; zero, views reflects the irradiance volume alone (<c>uint</c>).</summary>
    public const string ScreenReflections = "screenReflections";
    /// <summary>The pass-group value holding the most reads of the reflection pyramid one mirror ray's march takes, the
    /// device's step budget (<c>uint</c>).</summary>
    public const string ReflectionSteps = "reflectionSteps";
    /// <summary>The words of one cell's reflection record. <c>SdfIsaHlsl</c> generates it as
    /// <c>SDF_VIEWS_REFLECTION_WORDS</c>.</summary>
    public const uint ViewsReflectionWords = 4;
    /// <summary>The levels of the reflection pyramid above the render extent, the coarsest a texel of 256 pixels a side.
    /// <c>SdfIsaHlsl</c> generates it as <c>SDF_REFLECTION_DEPTH_LEVELS</c>.</summary>
    public const uint ReflectionDepthLevels = 8;
    /// <summary>The level the pyramid pass reduces in each group's own memory, above which its groups gather the levels
    /// with atomics into words the recorder clears first: the level a group of 8 by 8 invocations of level one makes
    /// one texel of (<c>SdfReflectDepthGroupLevel</c>).</summary>
    public const uint ReflectionDepthGroupLevel = 4;
    // The words a tile of the render extent provides the pyramid: a level k texel spans 2^k pixels, so level one takes at
    // most sixteen a tile's ceiling, level two four, and every level from three one.
    private const uint ReflectionDepthWordsPerTile = (16 + 4 + (ReflectionDepthLevels - 2));

    /// <summary>Returns the words of the reflection pyramid's levels below <paramref name="level"/>, from level one, for a
    /// render extent: the first word of that level, as <c>sdfReflectDepthBase</c> lays the levels out. The recorder clears
    /// the levels the pyramid pass gathers with atomics by it.</summary>
    /// <param name="width">The render width, in pixels.</param>
    /// <param name="height">The render height, in pixels.</param>
    /// <param name="level">The level, from one to one past <see cref="ReflectionDepthLevels"/>.</param>
    /// <returns>The words below the level.</returns>
    public static ulong ReflectionDepthWordsBelow(uint width, uint height, uint level) {
        var words = 0UL;

        for (var below = 1; (below < level); below++) {
            words += (((ulong)width).CeilingDivide(divisor: (1UL << below)) * ((ulong)height).CeilingDivide(divisor: (1UL << below)));
        }
        return words;
    }
    /// <summary>Returns the edge, in render pixels, of the cell one mirror ray serves in a fragment whose reflection records
    /// are <paramref name="records"/>, read from the words it holds a tile (<see cref="WithReflections"/>), the one place the
    /// edge a graph allocated for is read back.</summary>
    /// <param name="records">The reflection records' resource as the fragment declares it.</param>
    /// <returns>The cell's edge in pixels.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="records"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="records"/> holds no cell's words a tile.</exception>
    public static uint ReflectionCellOf(ShaderPipelineResource records) {
        ArgumentNullException.ThrowIfNull(argument: records);

        var words = (records.Count is [var term, ..] ? term.Elements : 0UL);

        foreach (var cell in ReflectionCells) {
            if (words == ReflectionWordsPerTile(cell: cell)) {
                return cell;
            }
        }
        throw new ArgumentException(message: $"'{records.Name}' holds {words} words a tile, no reflection cell's.", paramName: nameof(records));
    }

    /// <summary>The cell edges a fragment may trace its mirror rays at, in render pixels: one ray a pixel, or one a 2 by 2
    /// block, whose pixels take turns casting it.</summary>
    public static IReadOnlyList<uint> ReflectionCells { get; } = [1, 2];

    // The words the records hold for a tile of the render extent at a cell edge.
    private static ulong ReflectionWordsPerTile(uint cell) =>
        (((ShaderPipelineStorageCounts.TileEdge / cell) * (ShaderPipelineStorageCounts.TileEdge / cell)) * ViewsReflectionWords);

    /// <summary>Returns a fragment whose views pass reads screen-space reflections: the pyramid pass also writes the
    /// reflection pyramid (<see cref="ReflectionDepth"/>), and <see cref="Parts.ViewsReflections"/> runs before views,
    /// tracing one mirror ray a cell of <paramref name="cell"/> pixels a side through it and reading what each reaches from
    /// the preceding frame's history colour, which views blends over the irradiance volume's reflection. Only a fragment
    /// that keeps the history colour (<see cref="IsTemporal"/>) has a picture to read.</summary>
    /// <param name="fragment">A temporal fragment.</param>
    /// <param name="cell">The cell's edge, one of <see cref="ReflectionCells"/>.</param>
    /// <returns>The fragment with the reflection pass, its pyramid and its records.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fragment"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="fragment"/> keeps no history colour, has no views or pyramid
    /// pass, or already reflects, or <paramref name="cell"/> is none of <see cref="ReflectionCells"/>.</exception>
    public static RenderGraphPackageFragment WithReflections(RenderGraphPackageFragment fragment, uint cell) {
        ArgumentNullException.ThrowIfNull(argument: fragment);

        if (!ReflectionCells.Contains(value: cell)) {
            throw new ArgumentException(message: $"A reflection cell is {string.Join(separator: " or ", values: ReflectionCells)} pixels a side, not {cell}.", paramName: nameof(cell));
        }
        if (!IsTemporal(fragment: fragment)) {
            throw new ArgumentException(message: "The fragment keeps no history colour to reflect.", paramName: nameof(fragment));
        }
        if (!fragment.Passes.Any(predicate: static pass => (pass.Name == Parts.Views)) || !fragment.Passes.Any(predicate: static pass => (pass.Name == Parts.Pyramid))) {
            throw new ArgumentException(message: "The fragment has no views or pyramid pass to reflect with.", paramName: nameof(fragment));
        }
        if (fragment.Passes.Any(predicate: static pass => (pass.Name == Parts.ViewsReflections))) {
            throw new ArgumentException(message: "The fragment already reflects.", paramName: nameof(fragment));
        }

        // The records and the pyramid follow the visibility record's extent basis, the render extent's in a temporal view,
        // counted per tile of it.
        var visibility = fragment.Resources.Single(predicate: static resource => (resource.Name == Parts.Visibility));
        var passes = new List<RenderGraphFragmentPass>(capacity: (fragment.Passes.Count + 1));

        foreach (var pass in fragment.Passes) {
            if (pass.Name == Parts.Pyramid) {
                passes.Add(item: pass with {
                    OutputAccesses = [.. pass.OutputAccesses, RenderGraphPortAccess.ComputeWrite],
                    Outputs = [.. pass.Outputs, new ResourceReference(Name: Parts.ReflectionDepth)],
                });
                continue;
            }
            if (pass.Name != Parts.Views) {
                passes.Add(item: pass);
                continue;
            }
            // The preceding frame's history surface names the receiver each history pixel shows, so a hit is read only
            // where the history shows that surface, and holds the air the composite added to it.
            passes.Add(item: new RenderGraphFragmentPass(
                CountsKernelWork: true,
                InputAccesses: [.. Enumerable.Repeat(count: 5, element: RenderGraphPortAccess.ComputeRead)],
                Inputs: [
                    new ResourceReference(Name: Parts.MeshTarget),
                    new ResourceReference(Name: Parts.Visibility),
                    new ResourceReference(Name: Parts.HistoryColor, PreviousFrame: true),
                    new ResourceReference(Name: Parts.HistorySurface, PreviousFrame: true),
                    new ResourceReference(Name: Parts.ReflectionDepth),
                ],
                Name: Parts.ViewsReflections,
                OutputAccesses: [RenderGraphPortAccess.ComputeWrite],
                Outputs: [new ResourceReference(Name: Parts.Reflections)]
            ));
            passes.Add(item: pass with {
                InputAccesses = [.. pass.InputAccesses, RenderGraphPortAccess.ComputeRead],
                Inputs = [.. pass.Inputs, new ResourceReference(Name: Parts.Reflections)],
            });
        }

        return fragment with {
            Passes = passes,
            Resources = [
                .. fragment.Resources,
                visibility with { Name = Parts.Reflections, StrideBytes = sizeof(uint), Count = PerTile(pixels: visibility.Count!, elements: ReflectionWordsPerTile(cell: cell)) },
                // One pyramid serves the view's every viewport, as the occlusion's pyramid does.
                visibility with { Name = Parts.ReflectionDepth, StrideBytes = sizeof(uint), Count = PerTile(pixels: visibility.Count!, elements: ReflectionDepthWordsPerTile, perViewport: false) },
            ],
        };
    }

    public static partial class Parts {
        /// <summary>The reflection pass: a mirror ray a cell, marched through the reflection pyramid, its hit read from
        /// the preceding frame's history colour, into the records views blends over the irradiance volume's
        /// reflection.</summary>
        public const string ViewsReflections = "views-reflections";
        /// <summary>The reflection records the reflection pass writes (<see cref="SdfWorldPackage.ViewsReflections"/>).</summary>
        public const string Reflections = "viewsReflections";
        /// <summary>The reflection pyramid the pyramid pass writes (<see cref="SdfWorldPackage.ReflectionDepth"/>).</summary>
        public const string ReflectionDepth = "reflectionDepth";
    }
}
