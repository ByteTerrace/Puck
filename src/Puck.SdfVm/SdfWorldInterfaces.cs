using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>
/// The pass interfaces the SDF engine's kernels are written against, the one statement of every binding, register and
/// block offset they read. Each kernel includes the declarations generated from its interface
/// (<see cref="ShaderInterfaceHlsl"/>, checked in beside the kernels and owned by <c>puck shaders generate</c>), and
/// the engine creates each pipeline from its interface's layout and binds by member name, so no binding number or
/// register is written by hand on either side.
/// <para><see cref="World"/> serves every per-view dispatch: sky, mask, beam, cull-args, primary, surface, ambient and
/// the three views variants. Its frame group is the standard frame block (<see cref="ShaderFrameInterface.FrameGroupMembers"/>),
/// written once a frame; its pass group is one set per ring slot and view, whose block holds the view's render extent
/// and names the view the dispatch renders. A buffer one pass writes and a later pass reads is two members over the one buffer: a read-write member
/// named with an <c>RW</c> suffix for its writer, and a read-only member for its readers.</para>
/// <para><see cref="BrickBake"/> serves the carve-bake baker: one pass set per brick slot binding that slot's request
/// buffer and the brick pool, with the slice ordinal pushed per dispatch.</para>
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
    /// <summary>The <see cref="World"/> pass-group value whose bit <c>s</c> is set when screen source <c>s</c> is
    /// bound this frame.</summary>
    public const string ScreenMask = "screenMask";
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
    /// the draw index plus one (zero where no mesh covers it) and the octahedral normal.</summary>
    public const string MeshVisibility = "meshVisibility";
    /// <summary>The glyph atlas.</summary>
    public const string GlyphAtlas = "sdfGlyphAtlas";
    /// <summary>The one nearest sampler the screen sources and the glyph atlas are sampled through.</summary>
    public const string ScreenSampler = "screenSampler";
    /// <summary>The <see cref="BrickBake"/> pass-group value holding the voxels one bake dispatch writes at most.</summary>
    public const string SliceVoxels = "sliceVoxels";
    /// <summary>The <see cref="BrickBake"/> request buffer: a three-row header, then the carves.</summary>
    public const string BakeRequest = "bakeRequest";
    /// <summary>The <see cref="BrickBake"/> brick pool the baker writes.</summary>
    public const string BakePool = "brickPool";
    /// <summary>The bit the view starts at in the index a <see cref="Mesh"/> draw call pushes; the bits below it name the
    /// draw.</summary>
    public const int MeshViewShift = 24;
    /// <summary>The directory, repository-relative, the kernels and their generated interface includes live in.</summary>
    public const string KernelDirectory = "src/Puck.SdfVm/Assets/Shaders/Sdf";

    /// <summary>Gets the frame data of every per-view SDF dispatch: the standard frame group, and a pass group whose block
    /// holds the view's render extent and the world values, followed by every resource the dispatches bind. Its frame
    /// block is written through <see cref="ShaderPipelineParameterLayout.WriteFrame"/>.</summary>
    public static ShaderPipelineParameterLayout WorldParameters { get; } = ShaderPipelineParameterLayout.Grouped(
        config: null,
        interfaceName: "sdf-world",
        members: [
            Value(name: ImageExtent, type: ShaderValueType.Uint2),
            Value(name: TileGrid, type: ShaderValueType.Uint2),
            Value(name: ViewportCount, type: ShaderValueType.Uint),
            Value(name: ScreenMask, type: ShaderValueType.Uint),
            Value(name: InstanceMaskWordCount, type: ShaderValueType.Uint),
            Value(name: SampleIndex, type: ShaderValueType.Uint),
            Value(name: ViewBase, type: ShaderValueType.Uint),
            Value(name: MeshDraws, type: ShaderValueType.Uint),
            Read(name: ProgramWords, element: ShaderValueType.Uint4),
            Read(name: Viewports, element: ShaderValueType.Float4),
            Read(name: DynamicTransforms, element: ShaderValueType.Float4),
            Read(name: FrameInstanceGrid, element: ShaderValueType.Uint),
            Read(name: InstanceMasks, element: ShaderValueType.Uint),
            Written(name: InstanceMasksWritten, element: ShaderValueType.Uint),
            Read(name: Tiles, element: ShaderValueType.Float),
            Written(name: TilesWritten, element: ShaderValueType.Float),
            Read(name: CullBounds, element: ShaderValueType.Uint),
            Written(name: CullBoundsWritten, element: ShaderValueType.Uint),
            Written(name: ViewsArgsWritten, element: ShaderValueType.Uint),
            Read(name: VisibilityRecords, element: ShaderValueType.Uint),
            Written(name: VisibilityRecordsWritten, element: ShaderValueType.Uint),
            ShaderInterfaceMember.StorageImage(
                format: GpuPixelFormat.R8G8B8A8Unorm,
                group: ShaderInterfaceGroup.Pass,
                name: Output,
                type: ShaderValueType.Float4
            ),
            Read(name: ScreenSurfaces, element: ShaderValueType.Float4),
            Read(name: ScreenLights, element: ShaderValueType.Float4),
            Read(name: DecalCells, element: ShaderValueType.Uint4),
            Read(name: BrickPool, element: ShaderValueType.Float),
            Read(name: Volumes, element: ShaderValueType.Float4),
            Read(name: MeshRegion, element: ShaderValueType.Uint),
            .. Enumerable.Range(count: SdfWorldEngine.MaxScreenSurfaces, start: 0).Select(selector: static screen => ShaderInterfaceMember.SampledImage(
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
        ]
    );
    /// <summary>Gets the interface every per-view SDF dispatch reads.</summary>
    public static ShaderInterface World => WorldParameters.Interface;
    /// <summary>Gets the interface the carve-bake baker reads.</summary>
    public static ShaderInterface BrickBake { get; } = new(
        members: [
            Value(name: SliceVoxels, type: ShaderValueType.Uint),
            Read(name: BakeRequest, element: ShaderValueType.Float4),
            Written(name: BakePool, element: ShaderValueType.Float),
        ],
        name: "sdf-brick-bake",
        pushesIndex: true
    );
    /// <summary>Gets the interface the mesh pass draws with: the viewport table and the mesh region, one set per ring slot,
    /// and the index each draw call pushes, whose bits from <see cref="MeshViewShift"/> up name the view and whose bits
    /// below it name the draw (<see cref="MeshPushedIndex"/>).</summary>
    public static ShaderInterface Mesh { get; } = new(
        members: [
            Read(name: Viewports, element: ShaderValueType.Float4),
            Read(name: MeshRegion, element: ShaderValueType.Uint),
        ],
        name: "sdf-mesh",
        pushesIndex: true
    );
    /// <summary>Gets the layout of <see cref="World"/>.</summary>
    public static ShaderInterfaceLayout WorldLayout => WorldParameters.Layout;
    /// <summary>Gets the layout of <see cref="BrickBake"/>.</summary>
    public static ShaderInterfaceLayout BrickBakeLayout { get; } = new(shaderInterface: BrickBake);
    /// <summary>Gets the layout of <see cref="Mesh"/>.</summary>
    public static ShaderInterfaceLayout MeshLayout { get; } = new(shaderInterface: Mesh);
    /// <summary>Gets each interface with the repository-relative path of the include generated from it.</summary>
    public static IReadOnlyList<(string Path, ShaderInterface Interface)> Includes { get; } = [
        (IncludePath(shaderInterface: World), World),
        (IncludePath(shaderInterface: BrickBake), BrickBake),
        (IncludePath(shaderInterface: Mesh), Mesh),
    ];

    private static string IncludePath(ShaderInterface shaderInterface) =>
        $"{KernelDirectory}/{ShaderFrameInterface.IncludeFileName(interfaceName: shaderInterface.Name)}";
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
        ((view << MeshViewShift) | draw);
    /// <summary>Returns the member name of a screen source: <c>screenSource</c> followed by its screen index.</summary>
    /// <param name="screen">The screen index, below <see cref="SdfWorldEngine.MaxScreenSurfaces"/>.</param>
    /// <returns>The member name.</returns>
    public static string ScreenSource(int screen) =>
        string.Create(
            provider: System.Globalization.CultureInfo.InvariantCulture,
            handler: $"screenSource{screen}"
        );
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
