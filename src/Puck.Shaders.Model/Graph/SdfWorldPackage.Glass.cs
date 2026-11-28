using Puck.Hosting;

namespace Puck.Shaders;

public static partial class SdfWorldPackage {
    /// <summary>The glass raster pass's writable binding of the glass sums. Each render pixel holds the signed
    /// path length, winding, RGB optical depth, the layer list of its nearest front faces, signed material key and
    /// nearest exit, laid out tile by tile (<see cref="GlassTilePixels"/> pixels a tile, a tile's pixels contiguous).
    /// The raster integrates oriented crossings up to the opaque hit in integer arithmetic so draw order does not
    /// change the result. See <c>frame/sdf-glass.hlsli</c> for the packing and clipping rules.</summary>
    public const string GlassSumsWritten = "sdfGlassSumsRW";
    /// <summary>The glass raster pass's 64-bit view of the glass sums' buffer, through which a front face enters the
    /// pixel's layer list (<see cref="GlassLayers"/> entries, each one atomically ordered key). See
    /// <c>frame/sdf-glass.hlsli</c>.</summary>
    public const string GlassLayersWritten = "sdfGlassLayersRW";
    /// <summary>The glass sums as the glass shading pass reads them.</summary>
    public const string GlassSums = "sdfGlassSums";
    /// <summary>The glass sums as the glass clear zeroes the tiles the preceding raster listed
    /// (<c>passes/sdf-glass-tiles.comp.hlsl</c>).</summary>
    public const string GlassSumsClearWritten = "sdfGlassSumsClearRW";
    /// <summary>The glass visibility target as the glass shading pass samples it: per pixel the nearest glass front
    /// face's ray parameter, its draw plus one (zero where no glass face lies before the opaque hit) and its triangle,
    /// in the mesh target's format.</summary>
    public const string GlassVisibility = "glassVisibility";
    /// <summary>The glass tiles as a pass reads them: the header (the tiles listed), the occupancy mask of the render
    /// grid's 8 by 8 tiles, the tile list and each listed tile's sky filter, <c>frame/sdf-glass-tiles.hlsli</c> owning
    /// the layout. The glass raster marks and lists every tile it writes sums in, the glass shading pass shades only the
    /// listed tiles, takes back the mark of a tile that shows no glass, and writes the sky filter of every pixel of a
    /// tile it keeps marked; the sky, the resolve and the composite read the glass image and the sky filter only where
    /// the mask marks the pixel's tile, and the lit image views wrote everywhere else. A reader binds them only in a frame
    /// whose glass passes ran (<c>SdfWorldPasses.GlassSeen</c>), the tables' dummy otherwise, which holds no tile.</summary>
    public const string GlassTiles = "sdfGlassTiles";
    /// <summary>The glass tiles as the glass clear, the glass raster and the glass shading pass write them
    /// (<see cref="GlassTiles"/>).</summary>
    public const string GlassTilesWritten = "sdfGlassTilesRW";
    /// <summary>The glass shading pass's indirect dispatch arguments as the glass tiles pass writes them: a record of
    /// three group counts for each job of the pass's plan, a job of tiles past the listed count dispatching nothing.</summary>
    public const string GlassArgumentsWritten = "sdfGlassArgumentsRW";
    /// <summary>The pass-group value holding the glass shading plan's jobs: the tiles one job dispatches at most, and the
    /// jobs it records, which the glass tiles pass writes the arguments of and the shading pass dispatches.</summary>
    public const string GlassJobs = "glassJobs";
    /// <summary>The glass image as the sky, the resolve and the composite read it: the lit image with every pixel of a
    /// tile the glass tiles mark shaded through the glass, written only in those tiles (<see cref="GlassTiles"/>).</summary>
    public const string GlassLitImage = "glassLit";
    /// <summary>The words one render-extent pixel holds in the glass sums: the sums, the layer list's
    /// <see cref="GlassLayers"/> 64-bit entries, the material key and the nearest exit. Even, so every pixel's layer list
    /// lies on 64-bit words. <c>SdfIsaHlsl</c> generates it as <c>SDF_GLASS_SUM_WORDS</c>.</summary>
    public const uint GlassSumWords = 18;
    /// <summary>The pixels of one glass tile, an 8 by 8 group of the render grid, as the views' groups are
    /// (<see cref="ShaderPipelineStorageCounts.TileEdge"/> a side).</summary>
    public const uint GlassTilePixels = (ShaderPipelineStorageCounts.TileEdge * ShaderPipelineStorageCounts.TileEdge);
    /// <summary>The words of the glass tiles' header, before the mask: the count of tiles listed, then spare words.
    /// <c>SdfIsaHlsl</c> generates it as <c>SDF_GLASS_TILE_HEADER_WORDS</c>.</summary>
    public const uint GlassTileHeaderWords = 4;
    /// <summary>The words the glass tiles hold for each tile of their capacity: a word of the mask's region (of which the
    /// mask uses one bit), its list entry and its pixels' sky filter. <c>SdfIsaHlsl</c> generates it as
    /// <c>SDF_GLASS_TILE_WORDS</c>.</summary>
    public const uint GlassTileWords = (2 + GlassTilePixels);
    /// <summary>The front faces a pixel's layer list holds: the nearest that many visible glass entries, ordered by
    /// distance whatever order they rasterized in, each of which the glass shading pass shades as an interface of its
    /// own. Every glass face still absorbs through the sums however many lie along the ray. <c>SdfIsaHlsl</c> generates
    /// it as <c>SDF_GLASS_LAYERS</c>.</summary>
    public const uint GlassLayers = 4;
    /// <summary>The steps a world unit a glass face's ray distance is accumulated at, as a whole number: a quarter of a
    /// millimetre, so sixty faces at the far distance's limit sum within a 32-bit word. <c>SdfIsaHlsl</c> generates it as
    /// <c>SDF_GLASS_SUM_SCALE</c>.</summary>
    public const float GlassSumScale = 4096f;
    /// <summary>The steps a world unit a glass face's distance to the opaque hit is quantized to in the optical depth
    /// sums: 2^15, so a distance up to 65535 world units (the clamp the raster applies) is below 2^31.
    /// <c>SdfIsaHlsl</c> generates it as <c>SDF_GLASS_DEPTH_DISTANCE_SCALE</c>.</summary>
    public const float GlassDepthDistanceScale = 32768f;
    /// <summary>The steps a unit of absorption (per world unit) is quantized to in the optical depth sums: 2^16, so an
    /// absorption up to 32767 a world unit (the clamp the raster applies; one millimetre of it passes e^-32) is below
    /// 2^31 and one face's product with its distance below 2^62. <c>SdfIsaHlsl</c> generates it as
    /// <c>SDF_GLASS_ABSORPTION_SCALE</c>.</summary>
    public const float GlassAbsorptionScale = 65536f;

    /// <summary>Returns the tiles a glass tiles buffer of <paramref name="bytes"/> holds room for: the one statement of
    /// its capacity, which every kernel derives the same way from the bound buffer's length (<c>sdfGlassTileCapacity</c>
    /// in <c>frame/sdf-glass-tiles.hlsli</c>).</summary>
    /// <param name="bytes">The buffer's length in bytes.</param>
    /// <returns>The tiles it lays out.</returns>
    public static ulong GlassTileCapacity(ulong bytes) {
        var words = (bytes / sizeof(uint));

        return ((words > GlassTileHeaderWords) ? ((words - GlassTileHeaderWords) / GlassTileWords) : 0UL);
    }
    /// <summary>Returns the words from a glass tiles buffer's start through its mask, for a buffer of
    /// <paramref name="capacity"/> tiles: what the glass clear zeroes so the raster marks and lists from nothing (the twin of
    /// <c>sdfGlassTileMaskEnd</c> in <c>frame/sdf-glass-tiles.hlsli</c>).</summary>
    /// <param name="capacity">The tiles the buffer lays out (<see cref="GlassTileCapacity"/>).</param>
    /// <returns>The header's words and the mask's.</returns>
    public static ulong GlassTileMaskEnd(ulong capacity) => (GlassTileHeaderWords + ((capacity + 31UL) / 32UL));

    /// <summary>The glass passes' interface: the common frame values, the lit image views wrote (sampled), the glass
    /// visibility target (sampled), the visibility records and the glass sums it reads, the reactivity it raises at a
    /// glass pixel in a temporal view (a filler elsewhere), the image it writes, the preceding render's glass probe
    /// gathers it reuses and this render's it keeps (<see cref="GlassProbeHistory"/>), the glass tiles it shades and
    /// marks, the work counters and the World group's tables. The glass clear and the glass tiles pass read the same
    /// interface (<c>passes/sdf-glass-tiles.comp.hlsl</c>): the sums they zero, the tiles they read and the arguments they
    /// write, with the plan's jobs (<see cref="GlassJobs"/>).</summary>
    public static IReadOnlyList<ShaderInterfaceMember> GlassMembers => GlassDeclaration.Members;

    private static class GlassDeclaration {
        internal static readonly IReadOnlyList<ShaderInterfaceMember> Members = [
            .. Values,
            Value(name: GlassJobs, type: ShaderValueType.Uint2),
            ShaderInterfaceMember.SampledImage(group: ShaderInterfaceGroup.Pass, name: LitImage, type: ShaderValueType.Float4),
            ShaderInterfaceMember.SampledImage(group: ShaderInterfaceGroup.Pass, name: GlassVisibility, type: ShaderValueType.Float4),
            Read(element: ShaderValueType.Uint, name: VisibilityRecords),
            Read(element: ShaderValueType.Uint, name: GlassSums),
            Written(element: ShaderValueType.Float, name: ReactivityWritten),
            ShaderInterfaceMember.StorageImage(format: RenderGraphPackageCatalog.WorkingFormat, group: ShaderInterfaceGroup.Pass, name: Output, type: ShaderValueType.Float4),
            Read(element: ShaderValueType.Uint, name: GlassProbeHistory),
            Written(element: ShaderValueType.Uint, name: GlassProbeHistoryWritten),
            Read(element: ShaderValueType.Uint, name: GlassTiles),
            Written(element: ShaderValueType.Uint, name: GlassTilesWritten),
            Written(element: ShaderValueType.Uint, name: GlassSumsClearWritten),
            Written(element: ShaderValueType.Uint, name: GlassArgumentsWritten),
            ShaderWorkCounters.BufferMember,
            .. Tables,
        ];
    }

    /// <summary>Returns a fragment with the glass contract added after its views pass, for a view of a program that
    /// holds a glass shape (<c>SdfProgram.HasGlass</c>); a view of any other program runs the fragment as it stands and
    /// pays nothing. Four passes follow views, each costing the tiles the glass covers rather than the image:
    /// <see cref="Parts.GlassClear"/> zeroes the sums of the tiles the preceding raster listed and the tiles' mask;
    /// <see cref="Parts.Glass"/> rasterizes the glass draws, every face accumulating its signed ray distance into the sums
    /// where it lies before the opaque hit, the nearest front face winning the glass target through a depth of its own,
    /// and every tile it writes in marked and listed in the glass tiles; <see cref="Parts.GlassTiles"/> writes the
    /// shading pass's indirect arguments from the listed count; and <see cref="Parts.GlassShade"/> shades each listed
    /// tile's pixels into the glass image: the opaque image sampled along the refracted ray and absorbed over the
    /// distance through the glass, the sky reflected by Fresnel, the lights' highlights, frosted by the material's
    /// roughness. The sky, a temporal view's resolve and a native view's composite read the glass image and the glass
    /// tiles' sky filter where the tiles mark a pixel's tile and the lit image views wrote elsewhere, so no pass copies
    /// the image; a temporal view's resolve reads the reactivity the glass pass raised. The sums' and the tiles' versions
    /// declare their predecessors preserved: the clear and the raster record on one signature and one input (the
    /// geometry's, and the visibility records), so a raster never accumulates onto a clear it did not see.</summary>
    /// <param name="fragment">The fragment, any of the package's, with or without its cascades.</param>
    /// <returns>The fragment with the glass passes and their storages.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fragment"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="fragment"/> has no views pass, or already holds the glass
    /// passes.</exception>
    public static RenderGraphPackageFragment WithGlass(RenderGraphPackageFragment fragment) {
        ArgumentNullException.ThrowIfNull(argument: fragment);

        var views = fragment.Passes.FirstOrDefault(predicate: static pass => (pass.Name == Parts.Views));

        if (views is null) {
            throw new ArgumentException(message: "The fragment has no views pass to shade glass after.", paramName: nameof(fragment));
        }
        if (fragment.Passes.Any(predicate: static pass => (pass.Name == Parts.GlassShade))) {
            throw new ArgumentException(message: "The fragment already holds the glass passes.", paramName: nameof(fragment));
        }

        var lit = views.Outputs[0].Name;
        var temporal = fragment.Resources.Any(predicate: static resource => (resource.Name == Parts.Reactivity));
        var litResource = fragment.Resources.Single(predicate: resource => (resource.Name == lit));
        var meshTarget = fragment.Resources.Single(predicate: static resource => (resource.Name == Parts.MeshTarget));
        var meshDepth = fragment.Resources.Single(predicate: static resource => (resource.Name == Parts.MeshDepth));
        var visibility = fragment.Resources.Single(predicate: static resource => (resource.Name == Parts.Visibility));
        // The glass shades one view, so its storages follow the render grid's tiles once, whatever the viewports.
        var sumCount = PerTile(pixels: visibility.Count!, elements: (GlassTilePixels * GlassSumWords), perViewport: false);
        // A count term scales with a basis, so the header scales with the viewports, as the shade bins' does: at one viewport
        // it is the header, and at more the capacity a kernel derives from the length only grows into the extra words.
        ShaderPipelineCountTerm[] tileCount = [Term(GlassTileHeaderWords, ShaderPipelineCountBasis.Viewports), .. PerTile(pixels: visibility.Count!, elements: GlassTileWords, perViewport: false)];
        // The glass pass runs on the render grid, the probe history's basis in every fragment.
        var probeHistory = fragment.Resources.Single(predicate: static resource => (resource.Name == Parts.ProbeHistory));
        var passes = new List<RenderGraphFragmentPass>(capacity: (fragment.Passes.Count + 4));
        var after = false;

        foreach (var pass in fragment.Passes) {
            if (pass.Name == Parts.Views) {
                after = true;
                passes.Add(item: pass);
                // The clear reads the visibility records only to record whenever the raster does: the raster's one input,
                // so the two stand or record together and the raster never accumulates onto sums the clear did not zero.
                passes.Add(item: new RenderGraphFragmentPass(
                    CountsKernelWork: true,
                    InputAccesses: [RenderGraphPortAccess.ComputeRead],
                    Inputs: [new ResourceReference(Name: Parts.Visibility)],
                    Members: GlassMembers,
                    Name: Parts.GlassClear,
                    OutputAccesses: [RenderGraphPortAccess.ComputeWrite, RenderGraphPortAccess.ComputeWrite],
                    Outputs: [new ResourceReference(Name: Parts.GlassSumsCleared), new ResourceReference(Name: Parts.GlassTilesCleared)]
                ));
                passes.Add(item: new RenderGraphFragmentPass(
                    CountsKernelWork: true,
                    InputAccesses: [RenderGraphPortAccess.FragmentSampled],
                    Inputs: [new ResourceReference(Name: Parts.Visibility)],
                    Name: Parts.Glass,
                    OutputAccesses: [RenderGraphPortAccess.ColorAttachmentWrite, RenderGraphPortAccess.ColorAttachmentWrite, RenderGraphPortAccess.FragmentWrite, RenderGraphPortAccess.FragmentWrite],
                    Outputs: [new ResourceReference(Name: Parts.GlassTarget), new ResourceReference(Name: Parts.GlassDepth), new ResourceReference(Name: Parts.GlassSums), new ResourceReference(Name: Parts.GlassTilesMarked)]
                ));
                passes.Add(item: Pass(inputs: [Parts.GlassTilesMarked], name: Parts.GlassTiles, outputs: [Parts.GlassArguments]) with { Members = GlassMembers });
                var shade = Pass(
                    inputs: [lit, Parts.Visibility, Parts.GlassTarget, Parts.GlassSums],
                    name: Parts.GlassShade,
                    outputs: (temporal ? [Parts.GlassLit, Parts.GlassReactivity] : [Parts.GlassLit])
                );

                // The glass pass reuses the preceding render's gathers at its nearest faces and keeps this render's, and
                // shades the listed tiles, writing their sky filter and taking back a tile that shows no glass.
                passes.Add(item: shade with {
                    Dispatch = ShaderPipelineDispatch.Indirect(arguments: Parts.GlassArguments),
                    InputAccesses = [.. shade.InputAccesses, RenderGraphPortAccess.ComputeRead],
                    Inputs = [.. shade.Inputs, new ResourceReference(Name: Parts.GlassProbeHistory, PreviousFrame: true)],
                    Members = GlassMembers,
                    OutputAccesses = [.. shade.OutputAccesses, RenderGraphPortAccess.ComputeWrite, RenderGraphPortAccess.ComputeWrite],
                    Outputs = [.. shade.Outputs, new ResourceReference(Name: Parts.GlassProbeHistory), new ResourceReference(Name: Parts.GlassTilesShaded)],
                });
                continue;
            }
            if (!after) {
                passes.Add(item: pass);
                continue;
            }

            // A pass after the glass that reads the lit image views wrote reads the glass image and the tiles beside it,
            // selecting the glass where the tiles mark a pixel's tile; a resolved composite reads the resolve's lit image,
            // which already holds the glass, and the tiles alone for the sky filter.
            var readsLit = pass.Inputs.Any(predicate: input => (input.Name == lit));
            var renamed = pass with {
                Inputs = [.. pass.Inputs.Select(selector: static input => ((input.Name == Parts.Reactivity) ? input with { Name = Parts.GlassReactivity } : input))],
            };
            ResourceReference[] glassReads = (readsLit
                ? [new ResourceReference(Name: Parts.GlassLit), new ResourceReference(Name: Parts.GlassTilesShaded)]
                : ((renamed.Name == Parts.Composite) ? [new ResourceReference(Name: Parts.GlassTilesShaded)] : []));

            passes.Add(item: ((glassReads.Length == 0)
                ? renamed
                : renamed with {
                    InputAccesses = [.. renamed.InputAccesses, .. glassReads.Select(selector: static _ => RenderGraphPortAccess.ComputeRead)],
                    Inputs = [.. renamed.Inputs, .. glassReads],
                }));
        }

        return fragment with {
            Passes = passes,
            Resources = [
                .. fragment.Resources,
                litResource with { Name = Parts.GlassLit },
                probeHistory with { Name = Parts.GlassProbeHistory },
                meshTarget with { Name = Parts.GlassTarget },
                meshDepth with { Name = Parts.GlassDepth },
                Buffer(count: sumCount, name: Parts.GlassSumsCleared, sizeBytes: null, strideBytes: sizeof(uint)),
                new ShaderPipelineResource(
                    Count: sumCount,
                    From: Parts.GlassSumsCleared,
                    Kind: ShaderPipelineResourceKind.Buffer,
                    Name: Parts.GlassSums,
                    PreservesPredecessor: true,
                    StrideBytes: sizeof(uint)
                ),
                Buffer(count: tileCount, name: Parts.GlassTilesCleared, sizeBytes: null, strideBytes: sizeof(uint)),
                new ShaderPipelineResource(
                    Count: tileCount,
                    From: Parts.GlassTilesCleared,
                    Kind: ShaderPipelineResourceKind.Buffer,
                    Name: Parts.GlassTilesMarked,
                    PreservesPredecessor: true,
                    StrideBytes: sizeof(uint)
                ),
                new ShaderPipelineResource(
                    Count: tileCount,
                    From: Parts.GlassTilesMarked,
                    Kind: ShaderPipelineResourceKind.Buffer,
                    Name: Parts.GlassTilesShaded,
                    PreservesPredecessor: true,
                    StrideBytes: sizeof(uint)
                ),
                // A record of three group counts a job, and a job holds at least one tile.
                Buffer(count: PerTile(pixels: visibility.Count!, elements: 3, perViewport: false), name: Parts.GlassArguments, sizeBytes: null, strideBytes: sizeof(uint)),
                .. (temporal
                    ? new[] {
                        new ShaderPipelineResource(
                            Count: fragment.Resources.Single(predicate: static resource => (resource.Name == Parts.Reactivity)).Count,
                            From: Parts.Reactivity,
                            Kind: ShaderPipelineResourceKind.Buffer,
                            Name: Parts.GlassReactivity,
                            PreservesPredecessor: true,
                            StrideBytes: sizeof(float)
                        ),
                    }
                    : []),
            ],
        };
    }

    public static partial class Parts {
        /// <summary>The pass zeroing the sums of the tiles the preceding glass raster listed, and the glass tiles' mask,
        /// ahead of the glass raster pass.</summary>
        public const string GlassClear = "glass-clear";
        /// <summary>The glass raster pass: the frame's glass draws into the glass target, the glass sums and the glass
        /// tiles.</summary>
        public const string Glass = "glass";
        /// <summary>The glass tiles pass: the glass shading pass's indirect arguments from the tiles the raster
        /// listed.</summary>
        public const string GlassTiles = "glass-tiles";
        /// <summary>The glass shading pass: each listed tile's pixels shaded over the opaque image behind them into the
        /// glass image.</summary>
        public const string GlassShade = "glass-shade";
        /// <summary>The glass visibility target the glass raster pass draws: the nearest glass front face before the
        /// opaque hit, as the mesh target holds a hit.</summary>
        public const string GlassTarget = "glassTarget";
        /// <summary>The glass raster pass's own reversed-Z depth, cleared each frame, which keeps the nearest front
        /// face.</summary>
        public const string GlassDepth = "glassDepth";
        /// <summary>The glass sums as the clear leaves them: zero in every word.</summary>
        public const string GlassSumsCleared = "glassSumsCleared";
        /// <summary>The glass sums as the raster pass leaves them: each pixel's signed distance sum and face count
        /// (<see cref="SdfWorldPackage.GlassSumsWritten"/>).</summary>
        public const string GlassSums = "glassSums";
        /// <summary>The glass tiles as the clear leaves them: no tile marked or listed.</summary>
        public const string GlassTilesCleared = "glassTilesCleared";
        /// <summary>The glass tiles as the raster pass leaves them: every tile it wrote sums in marked and listed.</summary>
        public const string GlassTilesMarked = "glassTilesMarked";
        /// <summary>The glass tiles as the glass shading pass leaves them: the tiles that show glass marked, with their
        /// pixels' sky filter, which the passes after it read (<see cref="SdfWorldPackage.GlassTiles"/>).</summary>
        public const string GlassTilesShaded = "glassTilesShaded";
        /// <summary>The glass shading pass's indirect dispatch arguments.</summary>
        public const string GlassArguments = "glassArguments";
        /// <summary>The glass image: the lit image with the pixels of each tile the glass tiles mark shaded through the
        /// glass, written only in those tiles.</summary>
        public const string GlassLit = "glassLit";
        /// <summary>The reactivity with each glass pixel's raised where its refracted sample comes from elsewhere on
        /// screen, which a temporal view's resolve reads in the reactivity's place.</summary>
        public const string GlassReactivity = "glassReactivity";
        /// <summary>The glass pass's history of probe gathers at its nearest faces (<see cref="GlassProbeHistory"/>).</summary>
        public const string GlassProbeHistory = "glassProbeHistory";
    }
}
