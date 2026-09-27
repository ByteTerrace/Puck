using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>
/// The pass interfaces the SDF engine's kernels are written against, the one statement of every binding, register and
/// block offset they read. Each kernel includes the declarations generated from its interface
/// (<see cref="ShaderInterfaceHlsl"/>, checked in beside the kernels and owned by <c>puck shaders generate</c>), and
/// the engine creates each pipeline from its interface's layout and binds by member name, so no binding number or
/// register is written by hand on either side.
/// <para>Both pass interfaces are the <c>sdf.world</c> and <c>sdf.bricks</c> packages' (<see cref="RenderGraphPackageCatalog"/>),
/// laid out as <see cref="ShaderPipelineParameterLayout.ForPackage"/> lays every package's: the standard frame group
/// (<see cref="ShaderFrameInterface.FrameGroupMembers"/>), written once a frame, then a pass block holding the extent and
/// every value in ordinal name order, so a graph document whose config names the same values reads the same block.</para>
/// <para><see cref="World"/> serves every per-view dispatch: sky, mask, beam, cull-args, primary, surface, ambient and
/// the three views variants. Its pass group is one set per ring slot and view, whose block holds the view's render extent
/// and the world values, and names the view the dispatch renders. A buffer one pass writes and a later pass reads is two
/// members over the one buffer: a read-write member named with an <c>RW</c> suffix for its writer, and a read-only member
/// for its readers.</para>
/// <para><see cref="BrickBake"/> serves the carve-bake baker, the <c>sdf.bricks</c> pass: it binds the ring slot's frame
/// set and one pass set per brick slot binding that slot's request buffer and the brick pool, whose block's extent is one
/// slice as one row, the voxels one bake dispatch writes at most, with the slice ordinal pushed per dispatch.</para>
/// <para><see cref="Mesh"/> serves the mesh pass's graphics pipeline (<see cref="SdfMeshRasterPass"/>): one pass set per
/// ring slot binding the viewport table and the mesh region, with the view and the draw pushed per draw call.</para>
/// </summary>
public static class SdfWorldInterfaces {
    /// <summary>The <see cref="World"/> pass-group value holding the engine extent in pixels: the largest a view renders,
    /// and the per-view visibility record stride.</summary>
    public const string ImageExtent = "imageExtent";
    /// <summary>The <see cref="World"/> pass-group value holding the tiles per viewport, columns then rows: the cull
    /// buffer's per-viewport stride.</summary>
    public const string TileGrid = "tileGrid";
    /// <summary>The <see cref="World"/> pass-group value holding every view the frame renders.</summary>
    public const string ViewportCount = "viewportCount";
    /// <summary>The <see cref="World"/> pass-group value holding one past the highest screen whose source is bound this
    /// frame, or zero when none is: the bound the screen-light loop runs to.</summary>
    public const string ScreenCount = "screenCount";
    /// <summary>The <see cref="World"/> pass-group value holding the live program's per-tile instance-mask
    /// width.</summary>
    public const string InstanceMaskWordCount = "instanceMaskWordCount";
    /// <summary>The <see cref="World"/> pass-group value holding the deterministic tick clock star twinkle reads, or
    /// zero for a sky with no visible twinkle.</summary>
    public const string SampleIndex = "sampleIndex";
    /// <summary>The <see cref="World"/> pass-group value naming the view the set's dispatches render.</summary>
    public const string ViewBase = "viewBase";
    /// <summary>The <see cref="World"/> pass-group value holding the frame's mesh draws, or zero when no mesh draws: the
    /// hit passes read the mesh visibility target only when it is non-zero, and cull-args then covers the whole extent.</summary>
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
    /// <summary>The screen-mapping table, seven float4 rows per screen slot: the draw form of the mapping each screen
    /// publishes (<see cref="Puck.Commands.SourceMapping.Draw"/>), which the screen shading draws its face from, and the
    /// screen's state, whether its source is bound and the sampler it reads through.</summary>
    public const string ScreenMappings = "screenMappings";
    /// <summary>The screen-light and environment table.</summary>
    public const string ScreenLights = "sdfScreenLights";
    /// <summary>The glyph decal table.</summary>
    public const string DecalCells = "sdfDecalCells";
    /// <summary>The brick pool, read by the beam and the hit passes.</summary>
    public const string BrickPool = "sdfBrickPool";
    /// <summary>The bounded-volume table.</summary>
    public const string Volumes = "sdfVolumes";
    /// <summary>The mesh region (<see cref="SdfMeshRegion"/>'s raw layout) as a stream of words: the draw records, then
    /// the positions and indices the mesh pass reads.</summary>
    public const string MeshRegion = "sdfMeshRegion";
    /// <summary>The mesh visibility target the mesh pass draws and the hit passes read: per pixel the ray parameter,
    /// the draw index plus one (zero where no mesh covers it) and the triangle.</summary>
    public const string MeshVisibility = "meshVisibility";
    /// <summary>The glyph atlas.</summary>
    public const string GlyphAtlas = "sdfGlyphAtlas";
    /// <summary>The screen sources: one sampled image per screen, indexed by screen index.</summary>
    public const string ScreenSources = "screenSources";
    /// <summary>The engine's samplers, one per filter, indexed by the filter's value
    /// (<see cref="GpuSamplerFilter"/>): a screen samples its source through the one its row chooses, and the glyph atlas
    /// through the nearest one.</summary>
    public const string Samplers = "samplers";
    /// <summary>The length of <see cref="Samplers"/>: one sampler per <see cref="GpuSamplerFilter"/>.</summary>
    public const uint SamplerCount = 2;
    /// <summary>The <see cref="BrickBake"/> request buffer: a three-row header, then the carves.</summary>
    public const string BakeRequest = "bakeRequest";
    /// <summary>The <see cref="BrickBake"/> brick pool the baker writes.</summary>
    public const string BakePool = "brickPool";
    /// <summary>The bit the view starts at in the index a <see cref="Mesh"/> draw call pushes; the bits below it name the
    /// draw.</summary>
    public const int MeshViewShift = 24;
    /// <summary>The directory, repository-relative, the kernels' module tree lives in: the generated interface includes
    /// in its <c>isa</c> directory and the pass entry points in <c>passes</c>.</summary>
    public const string KernelDirectory = "src/Puck.SdfVm/Assets/Shaders/Sdf";

    /// <summary>Gets the frame data of every per-view SDF dispatch: the standard frame group, and a pass group whose block
    /// holds the view's render extent and the world values, followed by every resource the dispatches bind. Its frame
    /// block is written through <see cref="ShaderPipelineParameterLayout.WriteFrame"/>.</summary>
    public static ShaderPipelineParameterLayout WorldParameters { get; } = ShaderPipelineParameterLayout.ForPackage(
        config: null,
        package: RenderGraphPackageCatalog.SdfWorld,
        members: [
            Value(name: ImageExtent, type: ShaderValueType.Uint2),
            Value(name: InstanceMaskWordCount, type: ShaderValueType.Uint),
            Value(name: MeshDraws, type: ShaderValueType.Uint),
            Value(name: SampleIndex, type: ShaderValueType.Uint),
            Value(name: ScreenCount, type: ShaderValueType.Uint),
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
            Read(element: ShaderValueType.Float4, name: ScreenMappings),
            Read(element: ShaderValueType.Float4, name: ScreenLights),
            Read(element: ShaderValueType.Uint4, name: DecalCells),
            Read(element: ShaderValueType.Float, name: BrickPool),
            Read(element: ShaderValueType.Float4, name: Volumes),
            Read(element: ShaderValueType.Uint, name: MeshRegion),
            ShaderInterfaceMember.SampledImage(
                group: ShaderInterfaceGroup.Pass,
                length: SdfWorldEngine.MaxScreenSurfaces,
                name: ScreenSources,
                type: ShaderValueType.Float4
            ),
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
                length: SamplerCount,
                name: Samplers
            ),
        ]
    );

    /// <summary>Gets the interface every per-view SDF dispatch reads.</summary>
    public static ShaderInterface World => WorldParameters.Interface;

    /// <summary>Gets the frame data of the carve-bake baker, the <c>sdf.bricks</c> pass: the standard frame group, and a
    /// pass group whose block holds the extent one bake dispatch covers, a slice of voxels as one row, followed by the
    /// slot's request buffer and the brick pool, with the slice ordinal pushed: the kernel's first voxel is the ordinal
    /// times the extent's width.</summary>
    public static ShaderPipelineParameterLayout BrickBakeParameters { get; } = ShaderPipelineParameterLayout.ForPackage(
        config: null,
        members: [
            Read(element: ShaderValueType.Float4, name: BakeRequest),
            Written(element: ShaderValueType.Float, name: BakePool),
        ],
        package: RenderGraphPackageCatalog.SdfBricks,
        pushesIndex: true
    );

    /// <summary>Gets the interface the carve-bake baker reads.</summary>
    public static ShaderInterface BrickBake => BrickBakeParameters.Interface;

    /// <summary>Gets the interface the mesh pass draws with: the viewport table and the mesh region, one set per ring slot,
    /// and the index each draw call pushes, whose bits from <see cref="MeshViewShift"/> up name the view and whose bits
    /// below it name the draw (<see cref="MeshPushedIndex"/>).</summary>
    public static ShaderInterface Mesh { get; } = new(
        members: [
            Read(element: ShaderValueType.Float4, name: Viewports),
            Read(element: ShaderValueType.Uint, name: MeshRegion),
        ],
        name: "sdf-mesh",
        pushesIndex: true
    );

    /// <summary>Gets the layout of <see cref="World"/>.</summary>
    public static ShaderInterfaceLayout WorldLayout => WorldParameters.Layout;
    /// <summary>Gets the layout of <see cref="BrickBake"/>.</summary>
    public static ShaderInterfaceLayout BrickBakeLayout => BrickBakeParameters.Layout;

    /// <summary>Gets the layout of <see cref="Mesh"/>.</summary>
    public static ShaderInterfaceLayout MeshLayout { get; } = new(shaderInterface: Mesh);
    /// <summary>Gets each interface with the repository-relative path of the include generated from it.</summary>
    public static IReadOnlyList<(string Path, ShaderInterface Interface)> Includes { get; } = [
        (IncludePath(shaderInterface: World), World),
        (IncludePath(shaderInterface: BrickBake), BrickBake),
        (IncludePath(shaderInterface: Mesh), Mesh),
    ];

    private static string IncludePath(ShaderInterface shaderInterface) =>
        $"{KernelDirectory}/isa/{ShaderFrameInterface.IncludeFileName(interfaceName: shaderInterface.Name)}";
    private static ShaderInterfaceMember Read(string name, ShaderValueType element) =>
        ShaderInterfaceMember.ReadOnlyBuffer(
            element: element,
            group: ShaderInterfaceGroup.Pass,
            name: name
        );
    private static ShaderInterfaceMember Value(string name, ShaderValueType type) =>
        ShaderInterfaceMember.Value(
            group: ShaderInterfaceGroup.Pass,
            name: name,
            type: type
        );
    private static ShaderInterfaceMember Written(string name, ShaderValueType element) =>
        ShaderInterfaceMember.ReadWriteBuffer(
            element: element,
            group: ShaderInterfaceGroup.Pass,
            name: name
        );

    /// <summary>Returns the index a <see cref="Mesh"/> draw call pushes: the view from bit <see cref="MeshViewShift"/> up,
    /// the draw below it.</summary>
    /// <param name="view">The view the draw renders into.</param>
    /// <param name="draw">The draw, below <see cref="SdfMeshRegion.MaxDraws"/>.</param>
    /// <returns>The pushed index.</returns>
    public static uint MeshPushedIndex(uint view, uint draw) =>
        (view << MeshViewShift) | draw;
    /// <summary>Returns the pass-group binding number of a member of <see cref="World"/> or
    /// <see cref="BrickBake"/>.</summary>
    /// <param name="layout">The interface's layout.</param>
    /// <param name="member">The member's name.</param>
    /// <returns>The binding number.</returns>
    /// <exception cref="InvalidOperationException">The pass group declares no such resource.</exception>
    public static uint BindingOf(ShaderInterfaceLayout layout, string member) {
        ArgumentNullException.ThrowIfNull(argument: layout);

        return layout.Groups.Single(predicate: static group => (group.Group == ShaderInterfaceGroup.Pass)).Resources.Single(predicate: resource => string.Equals(
            a: resource.Member.Name,
            b: member,
            comparisonType: StringComparison.Ordinal
        )).Binding;
    }
}
