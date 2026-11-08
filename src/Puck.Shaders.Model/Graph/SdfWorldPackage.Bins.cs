using Puck.Hosting;

namespace Puck.Shaders;

public static partial class SdfWorldPackage {
    /// <summary>The views filter's record as the binned views pass reads it: <see cref="ViewsFilterWords"/> words a
    /// render-extent pixel, the pixel's filtered occlusion and four shadow visibilities (<c>sdf-views-filter.hlsli</c>
    /// owns the packing), addressed like the visibility record.</summary>
    public const string ViewsFilterRecords = "sdfViewsFilterRecords";
    /// <summary>The views filter's record as the filter pass writes it.</summary>
    public const string ViewsFilterRecordsWritten = "sdfViewsFilterRecordsRW";
    /// <summary>The shade bins as the binned views pass reads them: per view a header of <see cref="ShadeTileHeaderWords"/>
    /// words (each bin's tile count, its first entry in the tile list and the scatter's cursor), each tile's bin, then
    /// the tile list sorted by bin (<c>sdf-views-filter.hlsli</c> owns the layout).</summary>
    public const string ShadeTiles = "sdfShadeTiles";
    /// <summary>The shade bins as the filter pass (tile bins and counts) and the bins pass (offsets and the tile list)
    /// write them.</summary>
    public const string ShadeTilesWritten = "sdfShadeTilesRW";
    /// <summary>The binned views pass's indirect dispatch arguments as the bins pass writes them: per view and bin a
    /// record of three group counts for every tile, a bin's jobs in order, since a bin of jobs one tile wide holds a job a
    /// tile.</summary>
    public const string ShadeArgumentsWritten = "sdfShadeArgumentsRW";
    /// <summary>The pass-group value holding each bin's job width in tiles (zero for a bin the frame does not record),
    /// one component a bin, which the bins pass's arguments and the binned views pass's jobs share.</summary>
    public const string ShadeBinWidths = "shadeBinWidths";
    /// <summary>The bins one binned views pass shades with, at most four (one component of
    /// <see cref="ShadeBinWidths"/> each): bin zero runs the plain kernel over tiles no pixel of which has a program,
    /// bin one the frame's shading selection over every other tile. <c>SdfIsaHlsl</c> generates it as
    /// <c>SDF_SHADE_BINS</c>.</summary>
    public const uint ShadeBins = 2;
    /// <summary>The words of a view's shade-bins header: each bin's tile count, its offset in the tile list and its
    /// scatter cursor. <c>SdfIsaHlsl</c> generates it as <c>SDF_SHADE_TILE_HEADER_WORDS</c>.</summary>
    public const uint ShadeTileHeaderWords = (3 * ShadeBins);
    /// <summary>The words of one pixel's views filter record. <c>SdfIsaHlsl</c> generates it as
    /// <c>SDF_VIEWS_FILTER_WORDS</c>.</summary>
    public const uint ViewsFilterWords = 2;

    /// <summary>Returns a fragment whose views pass shades in bins, for a view whose frame runs authored programs: the
    /// views filter's tile gather moves into <see cref="Parts.ViewsFilter"/>, which writes each pixel's filtered
    /// occlusion and shadows into a record of its own and each 8 by 8 tile's bin into the shade bins;
    /// <see cref="Parts.ViewsBins"/> counts the bins into offsets, the indirect arguments of each bin's jobs and the tile
    /// list sorted by bin; and <see cref="Parts.Views"/> dispatches each bin's jobs indirectly with that bin's kernel, so
    /// a tile with no program never carries the registers of one. A view of a frame with no program runs the fragment as
    /// it stands and allocates none of this.</summary>
    /// <param name="fragment">The fragment, any of the package's.</param>
    /// <returns>The fragment with the filter and bins passes and their storages.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fragment"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="fragment"/> has no views pass, or already shades in
    /// bins.</exception>
    public static RenderGraphPackageFragment WithBins(RenderGraphPackageFragment fragment) {
        ArgumentNullException.ThrowIfNull(argument: fragment);

        if (!fragment.Passes.Any(predicate: static pass => (pass.Name == Parts.Views))) {
            throw new ArgumentException(message: "The fragment has no views pass to bin.", paramName: nameof(fragment));
        }
        if (fragment.Passes.Any(predicate: static pass => (pass.Name == Parts.ViewsFilter))) {
            throw new ArgumentException(message: "The fragment already shades in bins.", paramName: nameof(fragment));
        }

        // Every storage follows the visibility record's extent basis, the render extent's in a reduced or temporal view.
        var visibility = fragment.Resources.Single(predicate: static resource => (resource.Name == Parts.Visibility));
        var pixels = visibility.Count!;
        ShaderPipelineCountTerm[] PerTile(ulong elements) => [.. pixels.Select(selector: term => term with {
            Elements = elements,
            Per = [.. term.Per.Select(selector: static basis => basis switch {
                ShaderPipelineCountBasis.Extent => ShaderPipelineCountBasis.ExtentTiles,
                ShaderPipelineCountBasis.RenderExtent => ShaderPipelineCountBasis.RenderExtentTiles,
                _ => basis,
            })],
        })];
        // Each tile's bin and its entry in the tile list.
        var tiles = PerTile(elements: 2);
        var passes = new List<RenderGraphFragmentPass>(capacity: (fragment.Passes.Count + 2));

        foreach (var pass in fragment.Passes) {
            if (pass.Name != Parts.Views) {
                passes.Add(item: pass);
                continue;
            }
            passes.Add(item: Pass(inputs: [Parts.Visibility], name: Parts.ViewsFilter, outputs: [Parts.Filter, Parts.ShadeTiles]));
            passes.Add(item: Pass(name: Parts.ViewsBins, outputs: [Parts.ShadeBinned, Parts.ShadeArguments]));
            passes.Add(item: pass with {
                Dispatch = ShaderPipelineDispatch.Indirect(arguments: Parts.ShadeArguments),
                InputAccesses = [.. pass.InputAccesses, RenderGraphPortAccess.ComputeRead, RenderGraphPortAccess.ComputeRead],
                Inputs = [.. pass.Inputs, new ResourceReference(Name: Parts.Filter), new ResourceReference(Name: Parts.ShadeBinned)],
            });
        }

        return fragment with {
            Passes = passes,
            Resources = [
                .. fragment.Resources,
                visibility with { Name = Parts.Filter, StrideBytes = sizeof(uint), Count = [.. pixels.Select(selector: static term => term with { Elements = ViewsFilterWords })] },
                visibility with { Name = Parts.ShadeTiles, StrideBytes = sizeof(uint), Count = [Term(ShadeTileHeaderWords, ShaderPipelineCountBasis.Viewports), .. tiles] },
                new ShaderPipelineResource(
                    Count: [Term(ShadeTileHeaderWords, ShaderPipelineCountBasis.Viewports), .. tiles],
                    From: Parts.ShadeTiles,
                    Kind: ShaderPipelineResourceKind.Buffer,
                    Name: Parts.ShadeBinned,
                    PreservesPredecessor: true,
                    StrideBytes: sizeof(uint)
                ),
                Buffer(
                    count: PerTile(elements: (ShadeBins * 3)),
                    name: Parts.ShadeArguments,
                    sizeBytes: null,
                    strideBytes: sizeof(uint)
                ),
            ],
        };
    }

    public static partial class Parts {
        /// <summary>The views filter pass of a binned view: the views filter's averages into the filter record and each
        /// tile's bin and count into the shade bins.</summary>
        public const string ViewsFilter = "views-filter";
        /// <summary>The bins pass of a binned view: each bin's offset, its jobs' indirect arguments and the tile list
        /// sorted by bin.</summary>
        public const string ViewsBins = "views-bins";
        /// <summary>The views filter record the filter pass writes.</summary>
        public const string Filter = "viewsFilter";
        /// <summary>The shade bins as the filter pass leaves them: each tile's bin and each bin's count.</summary>
        public const string ShadeTiles = "shadeTiles";
        /// <summary>The shade bins as the bins pass leaves them: with each bin's offset and the tile list.</summary>
        public const string ShadeBinned = "shadeBins";
        /// <summary>The binned views pass's indirect dispatch arguments.</summary>
        public const string ShadeArguments = "shadeArguments";
    }
}
