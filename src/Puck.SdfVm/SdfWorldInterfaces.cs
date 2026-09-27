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
/// <para><see cref="World"/> serves every per-view dispatch: sky, mask, beam, cull-args, primary, surface, ambient,
/// shadow and the three views variants. Its members are the <c>sdf.world</c> package's (<see cref="SdfWorldPackage.Members"/>): its
/// World group is the residency's tables (<see cref="SdfWorldPackage.Tables"/>), one set per upload ring slot that every
/// pass of every view binds, and its pass group one set per frame slot and pass, whose block holds the view's render
/// extent and the frame's values.</para>
/// <para><see cref="BrickBake"/> serves the carve-bake baker, the <c>sdf.bricks</c> pass: it binds the ring slot's frame
/// set and one pass set per brick slot binding that slot's request buffer and the brick pool, whose block's extent is one
/// slice as one row, the voxels one bake dispatch writes at most, with the slice ordinal pushed per dispatch.</para>
/// <para><see cref="World"/> and <see cref="BrickBake"/> carry the instruction set's stamp (<see cref="SdfIsaHlsl.Stamp"/>)
/// in their pass blocks' variable names, so every kernel's bytecode reflects the instruction set it was compiled against,
/// and a reload holds each kernel to its interface's layout, stamp included
/// (<see cref="SdfKernelSet.LayoutOf"/>, <see cref="SdfWorldPipelines.PrepareReload"/>).</para>
/// <para><see cref="Mesh"/> serves the mesh pass's graphics pipeline (<see cref="SdfMeshRasterPass"/>): one pass set per
/// frame slot binding the pass block the world interface lays out and the mesh region, with the view and the draw pushed
/// per draw call.</para>
/// </summary>
public static class SdfWorldInterfaces {
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

    /// <summary>Gets the frame data of every per-view SDF dispatch: the standard frame group, the World group of the
    /// residency's tables, and a pass group whose block holds the view's render extent and the frame's values, followed by
    /// the view's own resources. Its frame block is written through
    /// <see cref="ShaderPipelineParameterLayout.WriteFrame"/>.</summary>
    public static ShaderPipelineParameterLayout WorldParameters { get; } = ShaderPipelineParameterLayout.ForPackage(
        config: null,
        package: RenderGraphPackageCatalog.SdfWorld,
        members: SdfWorldPackage.Members
    ).Stamped(stamp: SdfIsaHlsl.Stamp);

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
    ).Stamped(stamp: SdfIsaHlsl.Stamp);

    /// <summary>Gets the interface the carve-bake baker reads.</summary>
    public static ShaderInterface BrickBake => BrickBakeParameters.Interface;

    /// <summary>Gets the interface the mesh pass draws with: the pass block <see cref="World"/> declares, member for member
    /// at the same offsets, so the mesh pass binds the block its node writes for it, then the mesh region; and the index
    /// each draw call pushes, whose bits from <see cref="MeshViewShift"/> up name the view and whose bits below it name
    /// the draw (<see cref="MeshPushedIndex"/>).</summary>
    public static ShaderInterface Mesh { get; } = new(
        members: [
            .. World.Members.Where(predicate: static member => (
                (member.Group == ShaderInterfaceGroup.Pass) &&
                member.IsBlockMember
            )),
            Read(element: ShaderValueType.Uint, name: SdfWorldPackage.MeshRegion),
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

    // The resource an interface member binds, in whichever group declares it, since member names are unique across an
    // interface. Binding updates run every frame, so this walks the immutable layout by index and allocates no predicate
    // or enumerator.
    internal static ShaderInterfaceResourceLayout ResourceOf(ShaderInterfaceLayout layout, string member) {
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
