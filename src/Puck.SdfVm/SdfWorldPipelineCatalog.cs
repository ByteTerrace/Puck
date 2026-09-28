using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>
/// Where every SDF residency of a composition takes its pipelines from: the composition's pass-pipeline cache
/// (<see cref="GpuPassPipelineCache"/>), whose entries are the engine's kernel variants (<see cref="SdfWorldPipelines"/>),
/// its region copy and its mesh pass, keyed like any pass, so a kernel shared by several residencies on a device is
/// created once; and the deployed kernel set each backend reads once (<see cref="LoadDeployed"/>). The cache counts
/// every pipeline and shader module it creates under <c>gpu.pass-pipelines</c>, never a residency's ledger.
/// </summary>
/// <param name="regionCopy">The composition's region copy, whose pipeline every residency leases for its device.</param>
/// <param name="meshRaster">The composition's mesh pass, whose pipeline every residency leases for its device.</param>
public sealed class SdfWorldPipelineCatalog(GpuRegionCopyPass regionCopy, SdfMeshRasterPass meshRaster) {
    private readonly Dictionary<string, SdfKernelSet> m_deployed = new(comparer: StringComparer.Ordinal);
    private readonly Lock m_deployedGate = new();

    /// <summary>Gets the composition's region copy, one pipeline a device in the pass pipelines, which a residency's table
    /// upload and mesh region record with.</summary>
    public GpuRegionCopyPass RegionCopy { get; } = (regionCopy ?? throw new ArgumentNullException(paramName: nameof(regionCopy)));
    /// <summary>Gets the composition's mesh pass, one graphics pipeline a device in the pass pipelines, which a view's mesh
    /// pass draws with.</summary>
    public SdfMeshRasterPass MeshRaster { get; } = (meshRaster ?? throw new ArgumentNullException(paramName: nameof(meshRaster)));

    /// <summary>Gets the composition's pass-pipeline cache, the region copy's and the mesh pass's own, whose entries the
    /// engine's kernel variants are too.</summary>
    public GpuPassPipelineCache Pipelines => RegionCopy.Pipelines;

    /// <summary>Returns the deployed kernel set for a backend, reading it from <see cref="SdfKernelSet.DefaultDirectory"/>
    /// on the first call for that backend and returning the same set on every later call. Safe on any thread; the first
    /// call reads files, so a holder on the frame thread calls it from its own background work.</summary>
    /// <param name="bytecodeExtension">The compiled-kernel extension (<c>".spv"</c> for Vulkan, <c>".dxil"</c> for Direct3D 12).</param>
    /// <returns>The deployed kernel set.</returns>
    /// <exception cref="ArgumentException"><paramref name="bytecodeExtension"/> is empty.</exception>
    /// <exception cref="IOException">A kernel file is missing or cannot be read.</exception>
    public SdfKernelSet LoadDeployed(string bytecodeExtension) {
        ArgumentException.ThrowIfNullOrEmpty(bytecodeExtension);

        lock (m_deployedGate) {
            if (!m_deployed.TryGetValue(
                key: bytecodeExtension,
                value: out var kernels
            )) {
                kernels = SdfKernelSet.Load(bytecodeExtension: bytecodeExtension);
                m_deployed.Add(
                    key: bytecodeExtension,
                    value: kernels
                );
            }

            return kernels;
        }
    }
}
