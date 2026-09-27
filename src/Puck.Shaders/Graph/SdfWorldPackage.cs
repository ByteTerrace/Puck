using System.Globalization;
using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>
/// The <see cref="RenderGraphPackageCatalog.SdfWorld"/> package's declaration: the values and resources every one of its
/// kernels reads from its pass group, the layout facts its storages are sized by, and the fragment it runs as.
/// <para>Every compute pass of the fragment reads one interface, <see cref="Members"/>, whose pass block holds the
/// extent and the world values in ordinal name order and whose resources follow. A buffer one pass writes and a later
/// pass reads is two members over the one buffer: a read-write member named with an <c>RW</c> suffix for its writer, and a
/// read-only member for its readers.</para>
/// <para>The fragment (<see cref="Fragment"/>) is one view: the sky, the instance masks, the beam, the cull arguments, the
/// mesh pass, primary traversal, surface and ambient resolution, and shading into the output. Its scratch is transient,
/// one allocation shared by every frame slot, and scales with the counts its host resolves: the view's extent, one
/// viewport, its tiles at <see cref="TileSize"/>, and the program's instances and instance-mask words.</para>
/// </summary>
public static class SdfWorldPackage {
    /// <summary>The pass-group value holding the render extent in pixels: the per-view visibility record stride.</summary>
    public const string ImageExtent = "imageExtent";
    /// <summary>The pass-group value holding the tiles per viewport, columns then rows: the cull buffer's per-viewport
    /// stride.</summary>
    public const string TileGrid = "tileGrid";
    /// <summary>The pass-group value holding every view the dispatch set renders.</summary>
    public const string ViewportCount = "viewportCount";
    /// <summary>The pass-group value whose bit <c>s</c> is set when screen source <c>s</c> is bound this frame.</summary>
    public const string ScreenMask = "screenMask";
    /// <summary>The pass-group value holding the live program's per-tile instance-mask width.</summary>
    public const string InstanceMaskWordCount = "instanceMaskWordCount";
    /// <summary>The pass-group value holding the deterministic tick clock star twinkle reads, or zero for a sky with no
    /// visible twinkle.</summary>
    public const string SampleIndex = "sampleIndex";
    /// <summary>The pass-group value naming the viewport row the dispatch set renders.</summary>
    public const string ViewBase = "viewBase";
    /// <summary>The pass-group value holding the frame's mesh draws, or zero when no mesh draws: the hit passes read the
    /// mesh visibility target only when it is non-zero, and cull-args then covers the whole extent.</summary>
    public const string MeshDraws = "meshDraws";
    /// <summary>The program word stream.</summary>
    public const string ProgramWords = "sdfWords";
    /// <summary>The viewport table, six float4 rows per view.</summary>
    public const string Viewports = "viewports";
    /// <summary>The dynamic-transform table, three float4 rows per slot.</summary>
    public const string DynamicTransforms = "sdfDynamicTransforms";
    /// <summary>The frame-local instance grid.</summary>
    public const string FrameInstanceGrid = "sdfFrameInstanceGrid";
    /// <summary>The per-tile instance masks, read by the beam and the hit passes.</summary>
    public const string InstanceMasks = "sdfInstanceMasks";
    /// <summary>The per-tile instance masks, written by the mask pass.</summary>
    public const string InstanceMasksWritten = "sdfInstanceMasksRW";
    /// <summary>The cull buffer, read by cull-args and the hit passes.</summary>
    public const string Tiles = "tiles";
    /// <summary>The cull buffer, written by the beam.</summary>
    public const string TilesWritten = "tilesRW";
    /// <summary>The dispatch box, read by the hit passes.</summary>
    public const string CullBounds = "cullBounds";
    /// <summary>The dispatch box, written by cull-args.</summary>
    public const string CullBoundsWritten = "cullBoundsRW";
    /// <summary>The hit passes' indirect dispatch arguments, written by cull-args.</summary>
    public const string ViewsArgsWritten = "viewsArgsRW";
    /// <summary>The visibility records, read by views.</summary>
    public const string VisibilityRecords = "sdfVisibilityRecords";
    /// <summary>The visibility records, written by primary, surface and ambient.</summary>
    public const string VisibilityRecordsWritten = "sdfVisibilityRecordsRW";
    /// <summary>The view's output image, written by sky and views.</summary>
    public const string Output = "output";
    /// <summary>The screen-surface table, three float4 rows per screen slot.</summary>
    public const string ScreenSurfaces = "screenSurfaces";
    /// <summary>The screen-light and environment table.</summary>
    public const string ScreenLights = "sdfScreenLights";
    /// <summary>The glyph decal table.</summary>
    public const string DecalCells = "sdfDecalCells";
    /// <summary>The brick pool, read by the beam and the hit passes.</summary>
    public const string BrickPool = "sdfBrickPool";
    /// <summary>The bounded-volume table.</summary>
    public const string Volumes = "sdfVolumes";
    /// <summary>The mesh region as a stream of words: the draw records, then the positions and indices the mesh pass
    /// reads.</summary>
    public const string MeshRegion = "sdfMeshRegion";
    /// <summary>The mesh visibility target the mesh pass draws and the hit passes read: per pixel the ray parameter, the
    /// draw index plus one (zero where no mesh covers it) and the octahedral normal.</summary>
    public const string MeshVisibility = "meshVisibility";
    /// <summary>The glyph atlas.</summary>
    public const string GlyphAtlas = "sdfGlyphAtlas";
    /// <summary>The one nearest sampler the screen sources and the glyph atlas are sampled through.</summary>
    public const string ScreenSampler = "screenSampler";
    /// <summary>The screen sources the pass group binds, one sampled image each: the most screen surfaces one program
    /// declares. The per-frame bound-slot bitmask (<see cref="ScreenMask"/>) is one <c>uint</c>, so it is at most
    /// 32.</summary>
    public const int ScreenSources = 32;
    /// <summary>The edge of one screen tile in pixels, the unit the beam, the instance masks and the cull buffer count in.
    /// KEEP IN SYNC with <c>WorldTileSize</c> in <c>frame/sdf-tile.hlsli</c>.</summary>
    public const uint TileSize = 16;
    /// <summary>The bytes of one visibility record: the fifteen words in its V, C, L, N and S rows that
    /// <c>sdf-visibility.hlsli</c> lays out (<c>SdfVisibilityWords</c>).</summary>
    public const int VisibilityRecordByteLength = (15 * sizeof(uint));
    /// <summary>The planes the cull buffer holds per tile: the march start, the first exit, the second entry and the far
    /// bound. KEEP IN SYNC with <c>WorldTilePlaneCount</c> in <c>frame/sdf-frame.hlsli</c>.</summary>
    public const uint TilePlaneCount = 4;
    /// <summary>The floats the cull buffer holds per live instance after its tile planes: two float3 corners in each of
    /// the primary and ambient part-bound bands. KEEP IN SYNC with <c>SdfPartBoundFloatCount</c> in
    /// <c>march/sdf-part-bounds.hlsli</c>.</summary>
    public const uint PartBoundFloatCount = 12;
    /// <summary>The bytes of the dispatch box cull-args writes: the surviving tiles' group origin, then their exclusive
    /// group end, four <c>uint</c>s.</summary>
    public const ulong CullBoundsByteLength = (4 * sizeof(uint));
    /// <summary>The format of the mesh visibility target: per pixel the ray parameter, the draw index plus one and the
    /// octahedral normal.</summary>
    public const GpuPixelFormat MeshTargetFormat = GpuPixelFormat.R32G32B32A32Float;
    /// <summary>The format of the mesh pass's reversed-Z depth attachment.</summary>
    public const GpuPixelFormat MeshDepthFormat = GpuPixelFormat.D32Float;
    /// <summary>The depth the mesh pass clears its depth attachment to: zero, the reversed-Z far plane at infinity.</summary>
    public const float MeshClearDepth = 0f;

    /// <summary>Gets the mesh pass's depth attachment: cleared to <see cref="MeshClearDepth"/> and discarded, the one
    /// statement of its clear that the pass's render pass and the depth image the node creates for it share.</summary>
    public static GpuDepthAttachment MeshDepthAttachment { get; } = new(
        ClearDepth: MeshClearDepth,
        Format: MeshDepthFormat,
        Load: GpuAttachmentLoad.Clear,
        Store: GpuAttachmentStore.Discard
    );

    /// <summary>The name the fragment's output port version takes: the view's color, which the sky writes and views shades
    /// over.</summary>
    public const string Color = "color";

    /// <summary>Gets what every compute pass of the fragment reads from its pass group beside the extent: the world values,
    /// then every table, buffer and image its dispatches bind.</summary>
    public static IReadOnlyList<ShaderInterfaceMember> Members { get; } = [
        Value(name: ImageExtent, type: ShaderValueType.Uint2),
        Value(name: InstanceMaskWordCount, type: ShaderValueType.Uint),
        Value(name: MeshDraws, type: ShaderValueType.Uint),
        Value(name: SampleIndex, type: ShaderValueType.Uint),
        Value(name: ScreenMask, type: ShaderValueType.Uint),
        Value(name: TileGrid, type: ShaderValueType.Uint2),
        Value(name: ViewBase, type: ShaderValueType.Uint),
        Value(name: ViewportCount, type: ShaderValueType.Uint),
        Read(element: ShaderValueType.Uint4, name: ProgramWords),
        Read(element: ShaderValueType.Float4, name: Viewports),
        Read(element: ShaderValueType.Float4, name: DynamicTransforms),
        Read(element: ShaderValueType.Uint, name: FrameInstanceGrid),
        Read(element: ShaderValueType.Uint, name: InstanceMasks),
        Written(element: ShaderValueType.Uint, name: InstanceMasksWritten),
        Read(element: ShaderValueType.Float, name: Tiles),
        Written(element: ShaderValueType.Float, name: TilesWritten),
        Read(element: ShaderValueType.Uint, name: CullBounds),
        Written(element: ShaderValueType.Uint, name: CullBoundsWritten),
        Written(element: ShaderValueType.Uint, name: ViewsArgsWritten),
        Read(element: ShaderValueType.Uint, name: VisibilityRecords),
        Written(element: ShaderValueType.Uint, name: VisibilityRecordsWritten),
        ShaderInterfaceMember.StorageImage(
            format: GpuPixelFormat.R8G8B8A8Unorm,
            group: ShaderInterfaceGroup.Pass,
            name: Output,
            type: ShaderValueType.Float4
        ),
        Read(element: ShaderValueType.Float4, name: ScreenSurfaces),
        Read(element: ShaderValueType.Float4, name: ScreenLights),
        Read(element: ShaderValueType.Uint4, name: DecalCells),
        Read(element: ShaderValueType.Float, name: BrickPool),
        Read(element: ShaderValueType.Float4, name: Volumes),
        Read(element: ShaderValueType.Uint, name: MeshRegion),
        .. Enumerable.Range(count: ScreenSources, start: 0).Select(selector: static screen => ShaderInterfaceMember.SampledImage(
            group: ShaderInterfaceGroup.Pass,
            name: ScreenSource(screen: screen),
            type: ShaderValueType.Float4
        )),
        ShaderInterfaceMember.SampledImage(
            group: ShaderInterfaceGroup.Pass,
            name: GlyphAtlas,
            type: ShaderValueType.Float4
        ),
        ShaderInterfaceMember.SampledImage(
            group: ShaderInterfaceGroup.Pass,
            name: MeshVisibility,
            type: ShaderValueType.Float4
        ),
        ShaderInterfaceMember.Sampler(
            group: ShaderInterfaceGroup.Pass,
            name: ScreenSampler
        ),
    ];
    /// <summary>Gets the fragment the package runs as: one view's dispatch set, its scratch transient and counted, its
    /// one output the view's color.</summary>
    public static RenderGraphPackageFragment Fragment { get; } = new(
        InputVersions: [],
        OutputVersions: [Color],
        Passes: [
            Pass(name: Parts.Sky, outputs: [Parts.SkyImage]),
            Pass(name: Parts.Mask, outputs: [Parts.InstanceMasks]),
            Pass(inputs: [Parts.InstanceMasks], name: Parts.Beam, outputs: [Parts.Tiles]),
            Pass(inputs: [Parts.Tiles], name: Parts.CullArgs, outputs: [Parts.Arguments, Parts.CullBounds]),
            new RenderGraphFragmentPass(
                InputAccesses: [],
                Inputs: [],
                Name: Parts.Mesh,
                OutputAccesses: [RenderGraphPortAccess.ColorAttachmentWrite, RenderGraphPortAccess.ColorAttachmentWrite],
                Outputs: [Parts.MeshTarget, Parts.MeshDepth]
            ),
            Hit(mesh: true, name: Parts.Primary, visibility: null, written: Parts.Visibility),
            Hit(mesh: true, name: Parts.Surface, visibility: null, written: Parts.SurfaceVisibility),
            Hit(mesh: false, name: Parts.Ambient, visibility: null, written: Parts.AmbientVisibility),
            Hit(mesh: false, name: Parts.Views, visibility: Parts.AmbientVisibility, written: Color),
        ],
        Resources: [
            Image(format: GpuPixelFormat.R8G8B8A8Unorm, from: null, name: Parts.SkyImage, transient: false),
            Image(format: GpuPixelFormat.R8G8B8A8Unorm, from: Parts.SkyImage, name: Color, transient: false),
            Buffer(
                count: [Term(1, ShaderPipelineCountBasis.Viewports, ShaderPipelineCountBasis.Tiles, ShaderPipelineCountBasis.InstanceMaskWords)],
                name: Parts.InstanceMasks,
                sizeBytes: null,
                strideBytes: sizeof(uint)
            ),
            Buffer(
                count: [
                    Term(TilePlaneCount, ShaderPipelineCountBasis.Viewports, ShaderPipelineCountBasis.Tiles),
                    Term(PartBoundFloatCount, ShaderPipelineCountBasis.Viewports, ShaderPipelineCountBasis.Instances),
                ],
                name: Parts.Tiles,
                sizeBytes: null,
                strideBytes: sizeof(float)
            ),
            Buffer(count: null, name: Parts.Arguments, sizeBytes: ShaderPipelineDispatch.ArgumentBytes, strideBytes: sizeof(uint)),
            Buffer(count: null, name: Parts.CullBounds, sizeBytes: CullBoundsByteLength, strideBytes: sizeof(uint)),
            Image(format: MeshTargetFormat, from: null, name: Parts.MeshTarget, transient: true),
            new ShaderPipelineResource(
                Dimensions: ShaderPipelineDimensions.Relative(),
                Format: MeshDepthFormat.ToString(),
                ClearDepth: MeshClearDepth,
                Kind: ShaderPipelineResourceKind.Depth,
                Name: Parts.MeshDepth,
                Transient: true
            ),
            Visibility(from: null, name: Parts.Visibility),
            Visibility(from: Parts.Visibility, name: Parts.SurfaceVisibility),
            Visibility(from: Parts.SurfaceVisibility, name: Parts.AmbientVisibility),
        ]
    );

    /// <summary>Returns the member name of a screen source: <c>screenSource</c> followed by its screen index.</summary>
    /// <param name="screen">The screen index, below <see cref="ScreenSources"/>.</param>
    /// <returns>The member name.</returns>
    public static string ScreenSource(int screen) => string.Create(
        provider: CultureInfo.InvariantCulture,
        handler: $"screenSource{screen}"
    );

    private static ShaderInterfaceMember Read(string name, ShaderValueType element) => ShaderInterfaceMember.ReadOnlyBuffer(
        element: element,
        group: ShaderInterfaceGroup.Pass,
        name: name
    );
    private static ShaderInterfaceMember Value(string name, ShaderValueType type) => ShaderInterfaceMember.Value(
        group: ShaderInterfaceGroup.Pass,
        name: name,
        type: type
    );
    private static ShaderInterfaceMember Written(string name, ShaderValueType element) => ShaderInterfaceMember.ReadWriteBuffer(
        element: element,
        group: ShaderInterfaceGroup.Pass,
        name: name
    );
    private static ShaderPipelineCountTerm Term(ulong elements, params ShaderPipelineCountBasis[] per) => new(
        Elements: elements,
        Per: per
    );
    private static ShaderPipelineResource Image(string name, GpuPixelFormat format, string? from, bool transient) => new(
        Dimensions: ShaderPipelineDimensions.Relative(),
        Format: format.ToString(),
        From: from,
        Name: name,
        Transient: transient
    );
    // A transient buffer counted by its bases, or of a fixed size.
    private static ShaderPipelineResource Buffer(string name, uint strideBytes, ulong? sizeBytes, IReadOnlyList<ShaderPipelineCountTerm>? count) => new(
        Count: count,
        Kind: ShaderPipelineResourceKind.Buffer,
        Name: name,
        SizeBytes: sizeBytes,
        StrideBytes: strideBytes,
        Transient: true
    );
    // A version of the visibility records forwarding another: the storage's declaration, less the transient class only its
    // first version declares.
    private static ShaderPipelineResource Visibility(string name, string? from) => new(
        Count: [Term(1, ShaderPipelineCountBasis.Extent, ShaderPipelineCountBasis.Viewports)],
        From: from,
        Kind: ShaderPipelineResourceKind.Buffer,
        Name: name,
        StrideBytes: VisibilityRecordByteLength,
        Transient: (from is null)
    );
    private static RenderGraphFragmentPass Pass(string name, string[] outputs, string[]? inputs = null) => new(
        InputAccesses: [.. (inputs ?? []).Select(selector: static _ => RenderGraphPortAccess.ComputeRead)],
        Inputs: [.. (inputs ?? []).Select(selector: static input => new ResourceReference(Name: input))],
        Name: name,
        OutputAccesses: [.. outputs.Select(selector: static _ => RenderGraphPortAccess.ComputeWrite)],
        Outputs: [.. outputs.Select(selector: static output => new ResourceReference(Name: output))]
    );
    // A pass dispatched indirectly over the cull arguments' surviving tiles: it reads the dispatch box, the masks and the
    // tiles, the mesh visibility target when it resolves mesh pixels, and the visibility records when it shades them.
    private static RenderGraphFragmentPass Hit(string name, bool mesh, string? visibility, string written) {
        string[] inputs = [
            Parts.CullBounds,
            Parts.InstanceMasks,
            Parts.Tiles,
            .. (mesh ? (string[])[Parts.MeshTarget] : []),
            .. ((visibility is null) ? [] : (string[])[visibility]),
        ];

        return Pass(
            inputs: inputs,
            name: name,
            outputs: [written]
        ) with {
            Dispatch = ShaderPipelineDispatch.Indirect(arguments: Parts.Arguments),
        };
    }

    /// <summary>The names of the fragment's passes and versions.</summary>
    public static class Parts {
        /// <summary>The sky pre-pass.</summary>
        public const string Sky = "sky";
        /// <summary>The instance-cull pass building each tile's instance mask.</summary>
        public const string Mask = "mask";
        /// <summary>The beam prepass writing the tile planes and part bounds.</summary>
        public const string Beam = "beam";
        /// <summary>The reduction to the indirect dispatch arguments and the dispatch box.</summary>
        public const string CullArgs = "cull-args";
        /// <summary>The mesh pass drawing the frame's mesh draws into the mesh visibility target.</summary>
        public const string Mesh = "mesh";
        /// <summary>Primary traversal, writing the visibility records.</summary>
        public const string Primary = "primary";
        /// <summary>Surface resolution, continuing the visibility records.</summary>
        public const string Surface = "surface";
        /// <summary>Ambient resolution, continuing the visibility records.</summary>
        public const string Ambient = "ambient";
        /// <summary>Shading into the view's color.</summary>
        public const string Views = "views";
        /// <summary>The sky's version of the view's color.</summary>
        public const string SkyImage = "sky";
        /// <summary>The per-tile instance masks.</summary>
        public const string InstanceMasks = "instanceMasks";
        /// <summary>The cull buffer: the tile planes and the part bounds.</summary>
        public const string Tiles = "tiles";
        /// <summary>The hit passes' indirect dispatch arguments.</summary>
        public const string Arguments = "arguments";
        /// <summary>The dispatch box.</summary>
        public const string CullBounds = "cullBounds";
        /// <summary>The mesh visibility target.</summary>
        public const string MeshTarget = "meshTarget";
        /// <summary>The mesh pass's depth attachment.</summary>
        public const string MeshDepth = "meshDepth";
        /// <summary>Primary's visibility records.</summary>
        public const string Visibility = "visibility";
        /// <summary>Surface's visibility records, forwarding primary's.</summary>
        public const string SurfaceVisibility = "surfaceVisibility";
        /// <summary>Ambient's visibility records, forwarding surface's.</summary>
        public const string AmbientVisibility = "ambientVisibility";
    }
}
