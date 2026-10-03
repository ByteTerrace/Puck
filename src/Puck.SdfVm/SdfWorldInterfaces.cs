using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>
/// The SDF kernels' pass interfaces (<see cref="SdfKernelInterfaces"/>) as this build's engine binds them: stamped with
/// the instruction set <c>puck shaders generate</c> recorded (<see cref="SdfIsaFingerprint.Value"/>) when it wrote the
/// kernels' declarations, so a host names its instruction set without describing the encoding. A reload holds each
/// kernel to its interface's layout, stamp included (<see cref="SdfKernelSet.LayoutOf"/>,
/// <see cref="SdfWorldPipelines.PrepareReload"/>), and the mesh pass draws with <see cref="Mesh"/>
/// (<see cref="SdfMeshRasterPass"/>).
/// </summary>
public static class SdfWorldInterfaces {
    private static readonly SdfKernelInterfaces Recorded = new(stamp: SdfIsaHlsl.StampOf(fingerprint: SdfIsaFingerprint.Value));

    /// <summary>Gets the stamp the SDF kernels' interfaces carry for this build's instruction set:
    /// <see cref="SdfIsaHlsl.StampOf"/> of <see cref="SdfIsaFingerprint.Value"/>.</summary>
    public static string Stamp => Recorded.Stamp;
    /// <summary>Gets <see cref="SdfKernelInterfaces.WorldParameters"/> for this build's instruction set.</summary>
    public static ShaderPipelineParameterLayout WorldParameters => Recorded.WorldParameters;
    /// <summary>Gets the world interface for each configured fade capacity.</summary>
    public static IReadOnlyList<ShaderPipelineParameterLayout> WorldFadeParameters => Recorded.WorldFadeParameters;
    /// <summary>Gets <see cref="SdfKernelInterfaces.ResolveParameters"/> for this build's instruction set.</summary>
    public static ShaderPipelineParameterLayout ResolveParameters => Recorded.ResolveParameters;
    /// <summary>Gets <see cref="SdfKernelInterfaces.SkyParameters"/> for this build's instruction set.</summary>
    public static ShaderPipelineParameterLayout SkyParameters => Recorded.SkyParameters;
    /// <summary>Gets <see cref="SdfKernelInterfaces.EnvironmentParameters"/> for this build's instruction set.</summary>
    public static ShaderPipelineParameterLayout EnvironmentParameters => Recorded.EnvironmentParameters;
    /// <summary>Gets <see cref="SdfKernelInterfaces.World"/> for this build's instruction set.</summary>
    public static ShaderInterface World => Recorded.World;
    /// <summary>Gets <see cref="SdfKernelInterfaces.BrickBakeParameters"/> for this build's instruction set.</summary>
    public static ShaderPipelineParameterLayout BrickBakeParameters => Recorded.BrickBakeParameters;
    /// <summary>Gets <see cref="SdfKernelInterfaces.BrickBake"/> for this build's instruction set.</summary>
    public static ShaderInterface BrickBake => Recorded.BrickBake;
    /// <summary>Gets <see cref="SdfKernelInterfaces.Mesh"/>.</summary>
    public static ShaderInterface Mesh => Recorded.Mesh;
    /// <summary>Gets <see cref="SdfKernelInterfaces.WorldLayout"/> for this build's instruction set.</summary>
    public static ShaderInterfaceLayout WorldLayout => Recorded.WorldLayout;
    /// <summary>Gets <see cref="SdfKernelInterfaces.BrickBakeLayout"/> for this build's instruction set.</summary>
    public static ShaderInterfaceLayout BrickBakeLayout => Recorded.BrickBakeLayout;
    /// <summary>Gets <see cref="SdfKernelInterfaces.MeshLayout"/>.</summary>
    public static ShaderInterfaceLayout MeshLayout => Recorded.MeshLayout;
    /// <summary>Gets <see cref="SdfKernelInterfaces.Includes"/> for this build's instruction set.</summary>
    public static IReadOnlyList<(string Path, ShaderInterface Interface)> Includes => Recorded.Includes;
}
