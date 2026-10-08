using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>
/// The pass interfaces the SDF engine's kernels are written against for one instruction-set stamp, the one statement of every binding, register and
/// block offset they read. Each kernel includes the declarations generated from its interface
/// (<see cref="ShaderInterfaceHlsl"/>, checked in beside the kernels and owned by <c>puck shaders generate</c>), and
/// the engine creates each pipeline from its interface's layout and binds by member name, so no binding number or
/// register is written by hand on either side.
/// <para>The native pass interfaces are the <c>sdf.world</c> and <c>sdf.bricks</c> packages' (<see cref="RenderGraphPackageCatalog"/>),
/// laid out as <see cref="ShaderPipelineParameterLayout.ForPackage"/> lays every package's: the standard frame group
/// (<see cref="ShaderFrameInterface.FrameGroupMembers"/>), written once a frame, then a pass block holding the extent and
/// every value in ordinal name order, so a graph document whose config names the same values reads the same block.</para>
/// <para><see cref="ResolveParameters"/> serves the optional output reconstruction pass with its own resource bindings
/// and the same frame values. Its common values copy by declared member offsets; the native pass block stays unchanged.</para>
/// <para><see cref="SkyParameters"/> serves the sky's field runs and the composite, with their own resource bindings and the
/// same frame values, copied by declared member offsets as the resolve's are.</para>
/// <para><see cref="EnvironmentParameters"/> serves the residency's shared environment producer: its map dispatch reads
/// acquired panorama images, and its reduction publishes the coefficients after the map's graph dependency.</para>
/// <para><see cref="World"/> serves the native per-view dispatches: mask, beam, cull-args, primary, surface, ambient,
/// shadow and the three views variants. Its members are the <c>sdf.world</c> package's (<see cref="SdfWorldPackage.Members"/>)
/// and the lights and sky tables (<see cref="LightAndSkyTables"/>): its World group is the residency's tables
/// (<see cref="SdfWorldPackage.Tables"/> and those), one set per upload ring slot that every pass of every view binds,
/// and its pass group one set per frame slot and pass, whose block holds the view's render extent and the frame's
/// values.</para>
/// <para><see cref="BrickBake"/> serves the carve-bake baker, the <c>sdf.bricks</c> pass: it binds the ring slot's frame
/// set and one pass set per brick slot binding that slot's request buffer and the brick pool, whose block's extent is one
/// slice as one row, the voxels one bake dispatch writes at most, with the slice ordinal pushed per dispatch.</para>
/// <para><see cref="World"/>, <see cref="BrickBake"/> and <see cref="ResolveParameters"/> carry an instruction set's stamp
/// (<see cref="SdfIsaHlsl.StampOf"/>, <see cref="Stamp"/>) in their pass blocks' variable names, so every kernel's bytecode
/// reflects the instruction set it was compiled against, and a reload holds each kernel to its interface's layout, stamp
/// included. The interfaces are declared for any stamp: <c>puck shaders generate</c> declares them for the model's own
/// instruction set, and the engine for the one its build recorded (<c>SdfWorldInterfaces</c> in <c>Puck.SdfVm</c>).</para>
/// <para><see cref="Mesh"/> serves the mesh pass's graphics pipeline: one pass set per
/// frame slot binding the pass block the world interface lays out, the mesh region and the impostor depth atlas, with the
/// view and the draw pushed per draw call.</para>
/// </summary>
public sealed class SdfKernelInterfaces {
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
    /// <summary>The lights table: <see cref="SdfLights.MaxLights"/> <see cref="SdfLight"/> records, read by the shadow
    /// and views passes.</summary>
    public const string Lights = "sdfLights";
    /// <summary>The active shadow handoffs, one generated 16-byte record per incoming march.</summary>
    public const string ShadowHandoffs = "sdfShadowHandoffs";
    /// <summary>The sky block: one <see cref="SdfSkyBlock"/> record, read by the sky and composite passes and by the
    /// environment map's kernel (<see cref="EnvironmentParameters"/>).</summary>
    public const string Sky = "sdfSky";
    /// <summary>The sky's environment map as the composite reads it: <see cref="SdfSkyEnvironment.Texels"/> texels of two
    /// words, four half floats each (<see cref="SdfSkyEnvironment"/>), a World-group table the residency renders.</summary>
    public const string SkyEnvironment = "sdfSkyEnvironment";
    /// <summary>The sky's environment map as its kernel writes it; the reduction reads <see cref="SkyEnvironment"/>.</summary>
    public const string SkyEnvironmentWritten = "sdfSkyEnvironmentRW";
    /// <summary>The environment map's coefficients as its reduction writes them: <see cref="SdfSkyEnvironment.CoefficientCount"/>
    /// records of four floats, the three channels and a zero.</summary>
    public const string SkyCoefficientsWritten = "sdfSkyCoefficientsRW";
    /// <summary>The acquired screen images' cell and whole-image means, written by the shared environment producer.</summary>
    public const string ScreenEmissionWritten = "sdfScreenEmissionRW";
    /// <summary>The screen slots whose images were actually acquired for the reduction.</summary>
    public const string ScreenEmissionMask = "screenEmissionMask";
    /// <summary>The records this reduction may rewrite; other records retain the preceding frozen input.</summary>
    public const string ScreenEmissionWriteMask = "screenEmissionWriteMask";
    /// <summary>The sky's layer table: <see cref="SdfSky.MaxLayers"/> <see cref="SdfSkyLayer"/> records, read by the sky
    /// and composite passes and the environment map's kernel.</summary>
    public const string SkyLayers = "sdfSkyLayers";
    /// <summary>The sky's nine radiance coefficients read by the views pass.</summary>
    public const string SkyCoefficients = "sdfSkyCoefficients";

    /// <summary>Initializes a new instance of the <see cref="SdfKernelInterfaces"/> class.</summary>
    /// <param name="stamp">The instruction set's stamp (<see cref="SdfIsaHlsl.StampOf"/>).</param>
    /// <exception cref="ArgumentException"><paramref name="stamp"/> is null or empty.</exception>
    public SdfKernelInterfaces(string stamp) {
        ArgumentException.ThrowIfNullOrEmpty(argument: stamp);

        Stamp = stamp;
        IndirectParameters = ShaderPipelineParameterLayout.ForPackage(
            config: null,
            package: RenderGraphPackageCatalog.Indirect,
            members: [.. SdfWorldPackage.IndirectMembers, .. LightAndSkyTables]
        ).Stamped(stamp: stamp);
        WorldParameters = ShaderPipelineParameterLayout.ForPackage(
            config: null,
            package: RenderGraphPackageCatalog.SdfWorld,
            members: [.. SdfWorldPackage.Members, .. LightAndSkyTables]
        ).Stamped(stamp: stamp);
        // The resolve pass binds the residency's World set, so its World group is the world interface's, the lights
        // and sky tables included.
        ResolveParameters = ShaderPipelineParameterLayout.ForPackage(
            config: null,
            members: [.. SdfWorldPackage.ResolveMembers, .. LightAndSkyTables],
            package: "sdf-resolve"
        ).Stamped(stamp: stamp);
        // The sky and composite passes bind the residency's World set too, the sky's block and layers among its tables.
        SkyParameters = ShaderPipelineParameterLayout.ForPackage(
            config: null,
            members: [.. SdfWorldPackage.SkyMembers, .. LightAndSkyTables],
            package: "sdf-sky"
        ).Stamped(stamp: stamp);
        // The environment has no World set. Its graph passes bind the current sky records and acquired panorama images
        // beside the map and coefficients; all buffer barriers follow the fragment's declared ports.
        EnvironmentParameters = ShaderPipelineParameterLayout.ForPackage(
            config: null,
            members: EnvironmentMembers,
            package: "sdf-sky-environment"
        ).Stamped(stamp: stamp);
        BrickBakeParameters = ShaderPipelineParameterLayout.ForPackage(
            config: null,
            members: [
                Read(element: ShaderValueType.Float4, name: BakeRequest),
                Written(element: ShaderValueType.Float, name: BakePool),
            ],
            package: RenderGraphPackageCatalog.SdfBricks,
            pushesIndex: true
        ).Stamped(stamp: stamp);
        Mesh = new(
            members: [
                .. World.Members.Where(predicate: static member => (
                    (member.Group == ShaderInterfaceGroup.Pass) &&
                    member.IsBlockMember
                )),
                Read(element: ShaderValueType.Uint, name: SdfWorldPackage.MeshRegion),
                ShaderInterfaceMember.SampledImage(
                    group: ShaderInterfaceGroup.Pass,
                    name: SdfWorldPackage.ImpostorDepth,
                    type: ShaderValueType.Float4
                ),
                ShaderWorkCounters.BufferMember,
            ],
            name: "sdf-mesh",
            pushesIndex: true
        );
        MeshLayout = new(shaderInterface: Mesh);
        Includes = [
            (IncludePath(shaderInterface: IndirectParameters.Interface), IndirectParameters.Interface),
            (IncludePath(shaderInterface: World), World),
            (IncludePath(shaderInterface: BrickBake), BrickBake),
            (IncludePath(shaderInterface: Mesh), Mesh),
            (IncludePath(shaderInterface: ResolveParameters.Interface), ResolveParameters.Interface),
            (IncludePath(shaderInterface: SkyParameters.Interface), SkyParameters.Interface),
            (IncludePath(shaderInterface: EnvironmentParameters.Interface), EnvironmentParameters.Interface),
        ];
    }

    /// <summary>Gets the World-group tables <see cref="World"/> adds to the <c>sdf.world</c> package's members: the lights
    /// table and the sky's block and tables, each a structured buffer of a record whose declaration is generated from
    /// its C# type (<see cref="ShaderInterfaceStructure.From{T}"/>), which the residency writes as a region of its
    /// tables, and the sky's environment map, which its graph producer renders. A kernel binds one only when it reads it.</summary>
    public static IReadOnlyList<ShaderInterfaceMember> LightAndSkyTables { get; } = [
        ShaderInterfaceMember.ReadOnlyBuffer(group: ShaderInterfaceGroup.World, name: Lights, structure: ShaderInterfaceStructure.From<SdfLight>()),
        ShaderInterfaceMember.ReadOnlyBuffer(group: ShaderInterfaceGroup.World, name: Sky, structure: ShaderInterfaceStructure.From<SdfSkyBlock>()),
        ShaderInterfaceMember.ReadOnlyBuffer(group: ShaderInterfaceGroup.World, name: SkyLayers, structure: ShaderInterfaceStructure.From<SdfSkyLayer>()),
        ShaderInterfaceMember.ReadOnlyBuffer(element: ShaderValueType.Float4, group: ShaderInterfaceGroup.World, name: SkyCoefficients),
        ShaderInterfaceMember.ReadOnlyBuffer(group: ShaderInterfaceGroup.World, name: ShadowHandoffs, structure: ShaderInterfaceStructure.From<SdfShadowHandoff>()),
        ShaderInterfaceMember.ReadOnlyBuffer(element: ShaderValueType.Uint2, group: ShaderInterfaceGroup.World, name: SkyEnvironment),
    ];
    /// <summary>Gets the shared environment producer's typed interface. The map writes through its UAV; the reduction
    /// reads the same allocation through a read-only descriptor, matching its declared graph input access. The screen
    /// reduction reads acquired image descriptors and writes its separate cell and whole-image means.</summary>
    public static IReadOnlyList<ShaderInterfaceMember> EnvironmentMembers { get; } = [
        ShaderInterfaceMember.Value(group: ShaderInterfaceGroup.Pass, name: ScreenEmissionMask, type: ShaderValueType.Uint),
        ShaderInterfaceMember.Value(group: ShaderInterfaceGroup.Pass, name: ScreenEmissionWriteMask, type: ShaderValueType.Uint),
        ShaderWorkCounters.RowMember,
        ShaderWorkCounters.DetailRowMember,
        ShaderInterfaceMember.ReadOnlyBuffer(group: ShaderInterfaceGroup.Pass, name: Sky, structure: ShaderInterfaceStructure.From<SdfSkyBlock>()),
        ShaderInterfaceMember.ReadOnlyBuffer(group: ShaderInterfaceGroup.Pass, name: SkyLayers, structure: ShaderInterfaceStructure.From<SdfSkyLayer>()),
        ShaderInterfaceMember.ReadOnlyBuffer(element: ShaderValueType.Float4, group: ShaderInterfaceGroup.Pass, name: SdfWorldPackage.ScreenMappings),
        ShaderInterfaceMember.SampledImage(group: ShaderInterfaceGroup.Pass, length: SdfWorldPackage.ScreenSourceCount, name: SdfWorldPackage.ScreenSources, type: ShaderValueType.Float4),
        ShaderInterfaceMember.Sampler(group: ShaderInterfaceGroup.Pass, length: SdfWorldPackage.SamplerCount, name: SdfWorldPackage.Samplers),
        ShaderInterfaceMember.ReadOnlyBuffer(element: ShaderValueType.Uint2, group: ShaderInterfaceGroup.Pass, name: SkyEnvironment),
        Written(element: ShaderValueType.Uint2, name: SkyEnvironmentWritten),
        Written(element: ShaderValueType.Float4, name: SkyCoefficientsWritten),
        Written(element: ShaderValueType.Float4, name: ScreenEmissionWritten),
        ShaderWorkCounters.BufferMember,
    ];

    /// <summary>Gets the instruction set's stamp the interfaces carry.</summary>
    public string Stamp { get; }
    /// <summary>Gets the frame data of every per-view SDF dispatch: the standard frame group, the World group of the
    /// residency's tables, and a pass group whose block holds the view's render extent and the frame's values, followed by
    /// the view's own resources. Its frame block is written through
    /// <see cref="ShaderPipelineParameterLayout.WriteFrame"/>.</summary>
    public ShaderPipelineParameterLayout WorldParameters { get; }
    /// <summary>Gets the residency cache's classify and trace interface.</summary>
    public ShaderPipelineParameterLayout IndirectParameters { get; }
    /// <summary>Gets the reconstruction pass's interface, with the same frame values as the traversal passes.</summary>
    public ShaderPipelineParameterLayout ResolveParameters { get; }
    /// <summary>Gets the interface the sky and composite passes read, with the same frame values as the traversal passes
    /// (<see cref="SdfWorldPackage.SkyMembers"/>).</summary>
    public ShaderPipelineParameterLayout SkyParameters { get; }
    /// <summary>Gets the interface the environment map's kernel and its reduction read: the standard frame group, and a
    /// pass group whose block holds the map's extent and the pass's work-counter rows, followed by the sky block and layer
    /// table of the upload's ring slot, acquired panorama images and their mappings, the map and coefficients, and work counters.</summary>
    public ShaderPipelineParameterLayout EnvironmentParameters { get; }
    /// <summary>Gets the interface every per-view SDF dispatch reads.</summary>
    public ShaderInterface World => WorldParameters.Interface;
    /// <summary>Gets the frame data of the carve-bake baker, the <c>sdf.bricks</c> pass: the standard frame group, and a
    /// pass group whose block holds the extent one bake dispatch covers, a slice of voxels as one row, followed by the
    /// slot's request buffer and the brick pool, with the slice ordinal pushed: the kernel's first voxel is the ordinal
    /// times the extent's width.</summary>
    public ShaderPipelineParameterLayout BrickBakeParameters { get; }
    /// <summary>Gets the interface the carve-bake baker reads.</summary>
    public ShaderInterface BrickBake => BrickBakeParameters.Interface;
    /// <summary>Gets the interface the mesh pass draws with: the pass block <see cref="World"/> declares, member for member
    /// at the same offsets, so the mesh pass binds the block its node writes for it, then the mesh region and the work
    /// counters its fragments count the texels they write into (<see cref="ShaderWorkCounters"/>), and the impostor depth atlas
    /// (<see cref="SdfWorldPackage.ImpostorDepth"/>) an impostor card's fragments find their surface in; and the index
    /// each draw call pushes, whose bits from <see cref="MeshViewShift"/> up name the view and whose bits below it name
    /// the draw (<see cref="MeshPushedIndex"/>).</summary>
    public ShaderInterface Mesh { get; }
    /// <summary>Gets the layout of <see cref="World"/>.</summary>
    public ShaderInterfaceLayout WorldLayout => WorldParameters.Layout;
    /// <summary>Gets the layout of <see cref="BrickBake"/>.</summary>
    public ShaderInterfaceLayout BrickBakeLayout => BrickBakeParameters.Layout;
    /// <summary>Gets the layout of <see cref="Mesh"/>.</summary>
    public ShaderInterfaceLayout MeshLayout { get; }
    /// <summary>Gets each interface with the repository-relative path of the include generated from it.</summary>
    public IReadOnlyList<(string Path, ShaderInterface Interface)> Includes { get; }

    private static string IncludePath(ShaderInterface shaderInterface) =>
        $"{KernelDirectory}/isa/{ShaderFrameInterface.IncludeFileName(interfaceName: shaderInterface.Name)}";
    private static ShaderInterfaceMember Read(string name, ShaderValueType element) =>
        ShaderInterfaceMember.ReadOnlyBuffer(
            element: element,
            group: ShaderInterfaceGroup.Pass,
            name: name
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
    /// <param name="draw">The draw.</param>
    /// <returns>The pushed index.</returns>
    public static uint MeshPushedIndex(uint view, uint draw) =>
        (view << MeshViewShift) | draw;
    /// <summary>Returns the binding number, in the group that declares it, of a resource member of <see cref="World"/>,
    /// <see cref="BrickBake"/> or <see cref="Mesh"/>.</summary>
    /// <param name="layout">The interface's layout.</param>
    /// <param name="member">The member's name.</param>
    /// <returns>The binding number.</returns>
    /// <exception cref="InvalidOperationException">No group declares such a resource.</exception>
    public static uint BindingOf(ShaderInterfaceLayout layout, string member) {
        ArgumentNullException.ThrowIfNull(argument: layout);

        return ResourceOf(layout: layout, member: member).Binding;
    }
    /// <summary>Returns the resource an interface member binds, in whichever group declares it, since member names are
    /// unique across an interface. Binding updates run every frame, so this walks the immutable layout by index and
    /// allocates no predicate or enumerator.</summary>
    /// <param name="layout">The interface's layout.</param>
    /// <param name="member">The member's name.</param>
    /// <returns>The resource.</returns>
    /// <exception cref="InvalidOperationException">No group declares such a resource.</exception>
    public static ShaderInterfaceResourceLayout ResourceOf(ShaderInterfaceLayout layout, string member) {
        var groups = layout.Groups;

        for (var groupIndex = 0; (groupIndex < groups.Count); groupIndex++) {
            var resources = groups[groupIndex].Resources;

            for (var index = 0; (index < resources.Count); index++) {
                var resource = resources[index];

                if (string.Equals(a: resource.Member.Name, b: member, comparisonType: StringComparison.Ordinal)) {
                    return resource;
                }
            }
        }

        throw new InvalidOperationException(message: $"No group declares a resource '{member}'.");
    }
}
