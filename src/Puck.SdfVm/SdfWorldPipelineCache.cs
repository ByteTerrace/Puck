using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>
/// The <see cref="SdfWorldPipelines"/> sets the SDF engine nodes and views of one composition share: one set per
/// device, kernel set (<see cref="SdfWorldKernels.ContentKey"/>) and brick-pipeline choice, however many nodes and views
/// render with it. It is a <see cref="GpuBuildCache{TKey, T}"/> keyed by <see cref="SdfWorldPipelineKey"/>: a holder
/// takes a lease, the first lease on a key starts the set's build on the thread pool, every lease polls the same build
/// from the frame thread, and the set is disposed when its last lease is released. That release cancels a build still in
/// flight and waits, outside the cache's lock, only for the pipelines already in the driver.
/// <para>
/// The cache counts the shader modules and pipelines it creates into <see cref="Work"/>, a
/// <see cref="GpuWorkLedger"/> named <see cref="WorkSourceName"/>, including those a reload creates for a set; the
/// holders' own ledgers count none of them. It also reads each backend's deployed kernel set once
/// (<see cref="LoadDeployed"/>) and keeps it for its whole life.
/// </para>
/// <para>
/// Acquiring and loading are safe on any thread; the holders poll and release on the thread that renders. A device
/// recreated in place after a loss keeps its identity, so every holder releases its lease on device loss, and a set
/// is never handed to a lease on the recreated device.
/// </para>
/// </summary>
public sealed class SdfWorldPipelineCache {
    /// <summary>The name a counters report heads <see cref="Work"/>'s section with.</summary>
    public const string WorkSourceName = "gpu.sdf-pipelines";

    private readonly Dictionary<string, SdfWorldKernels> m_deployed = new(comparer: StringComparer.Ordinal);
    private readonly Lock m_deployedGate = new();
    private readonly GpuBuildCache<SdfWorldPipelineKey, SdfWorldPipelines> m_sets = new(
        build: static (request, token) => SdfWorldPipelines.Build(
            cancellationToken: token,
            device: request.Device,
            includeBrickPipelines: request.Key.IncludesBrickPipelines,
            kernels: request.Key.Kernels,
            ledger: request.Ledger,
            progress: request.Key.Progress
        ),
        workSourceName: WorkSourceName
    );

    /// <summary>Initializes a new instance of the <see cref="SdfWorldPipelineCache"/> class.</summary>
    /// <param name="regionCopy">The composition's region copy, whose pipeline every holder of a set also leases for its
    /// device.</param>
    /// <param name="meshRaster">The composition's mesh pass, whose pipeline every holder of a set also leases for its
    /// device.</param>
    /// <exception cref="ArgumentNullException"><paramref name="regionCopy"/> or <paramref name="meshRaster"/> is
    /// <see langword="null"/>.</exception>
    public SdfWorldPipelineCache(GpuRegionCopyPass regionCopy, SdfMeshRasterPass meshRaster) {
        ArgumentNullException.ThrowIfNull(argument: regionCopy);
        ArgumentNullException.ThrowIfNull(argument: meshRaster);

        MeshRaster = meshRaster;
        RegionCopy = regionCopy;
    }

    /// <summary>Gets the composition's region copy, one pipeline a device in its pass pipelines, which an engine's table
    /// upload and mesh region record with.</summary>
    public GpuRegionCopyPass RegionCopy { get; }
    /// <summary>Gets the composition's mesh pass, one graphics pipeline a device in its pass pipelines, which an engine's
    /// mesh pass draws with.</summary>
    public SdfMeshRasterPass MeshRaster { get; }
    /// <summary>Gets the number of sets a new lease can join: every set with a lease, less any a reload made private
    /// to its node.</summary>
    public int SharedSets => m_sets.SharedEntries;
    /// <summary>Gets the shader modules and pipelines the cache's sets have created, over the cache's whole life.</summary>
    public IWorkCounterSource Work => m_sets.Work;

    /// <summary>Takes a lease on the set for <paramref name="kernels"/> on <paramref name="device"/>, joining the set
    /// another holder already leases or starting its build on the thread pool. Safe on any thread; it hashes the kernel
    /// set, so a holder on the frame thread calls it from its own background work.</summary>
    /// <param name="device">The device the set is created on, through its services.</param>
    /// <param name="kernels">The compiled kernel set for the device's backend.</param>
    /// <param name="includeBrickPipelines">Whether the set includes the brick bake and upload pipelines, for an engine
    /// with a brick pool. A set with them and one without are different sets.</param>
    /// <returns>The lease, which the caller releases once no engine records with its set; its key's
    /// <see cref="SdfWorldPipelineKey.Progress"/> reports how far the set's build has come.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is <see langword="null"/>.</exception>
    public GpuBuildLease<SdfWorldPipelineKey, SdfWorldPipelines> Acquire(IGpuDeviceContext device, SdfWorldKernels kernels, bool includeBrickPipelines) {
        ArgumentNullException.ThrowIfNull(argument: device);

        return m_sets.Acquire(
            device: device,
            key: new SdfWorldPipelineKey(
                includesBrickPipelines: includeBrickPipelines,
                kernels: kernels
            )
        );
    }
    /// <summary>Returns the deployed kernel set for a backend, reading it from <see cref="SdfWorldKernels.DefaultDirectory"/>
    /// on the first call for that backend and returning the same set on every later call. Safe on any thread; the first
    /// call reads files, so a holder on the frame thread calls it from its own background work.</summary>
    /// <param name="bytecodeExtension">The compiled-kernel extension (<c>".spv"</c> for Vulkan, <c>".dxil"</c> for Direct3D 12).</param>
    /// <returns>The deployed kernel set.</returns>
    /// <exception cref="ArgumentException"><paramref name="bytecodeExtension"/> is empty.</exception>
    /// <exception cref="IOException">A kernel file is missing or cannot be read.</exception>
    public SdfWorldKernels LoadDeployed(string bytecodeExtension) {
        ArgumentException.ThrowIfNullOrEmpty(bytecodeExtension);

        lock (m_deployedGate) {
            if (!m_deployed.TryGetValue(
                key: bytecodeExtension,
                value: out var kernels
            )) {
                kernels = SdfWorldKernels.Load(bytecodeExtension: bytecodeExtension);
                m_deployed.Add(
                    key: bytecodeExtension,
                    value: kernels
                );
            }

            return kernels;
        }
    }
}
/// <summary>
/// What one <see cref="SdfWorldPipelines"/> set is built from: its kernel set and whether it includes the brick
/// pipelines. Two keys are equal when their kernels' <see cref="SdfWorldKernels.ContentKey"/> and brick choice are; the
/// key also carries the set's <see cref="Progress"/>, which the build writes and a holder reads.
/// </summary>
public sealed class SdfWorldPipelineKey : IEquatable<SdfWorldPipelineKey> {
    /// <summary>Initializes a new instance of the <see cref="SdfWorldPipelineKey"/> class, hashing the kernel set.</summary>
    /// <param name="kernels">The compiled kernel set for the device's backend.</param>
    /// <param name="includesBrickPipelines">Whether the set includes the brick bake and upload pipelines.</param>
    public SdfWorldPipelineKey(SdfWorldKernels kernels, bool includesBrickPipelines) {
        ContentKey = kernels.ContentKey();
        IncludesBrickPipelines = includesBrickPipelines;
        Kernels = kernels;
    }

    /// <summary>Gets the kernel set's content key.</summary>
    public string ContentKey { get; }
    /// <summary>Gets whether the set includes the brick bake and upload pipelines.</summary>
    public bool IncludesBrickPipelines { get; }
    /// <summary>Gets the kernel set the set is built from.</summary>
    public SdfWorldKernels Kernels { get; }
    /// <summary>Gets how far the set's latest build has come; a ready set reads every pipeline created.</summary>
    public SdfWorldPipelineBuildProgress Progress { get; } = new();

    /// <inheritdoc/>
    public bool Equals(SdfWorldPipelineKey? other) =>
        (
            (other is not null) &&
            (IncludesBrickPipelines == other.IncludesBrickPipelines) &&
            string.Equals(
                a: ContentKey,
                b: other.ContentKey,
                comparisonType: StringComparison.Ordinal
            )
        );
    /// <inheritdoc/>
    public override bool Equals(object? obj) =>
        Equals(other: (obj as SdfWorldPipelineKey));
    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(
            value1: StringComparer.Ordinal.GetHashCode(obj: ContentKey),
            value2: IncludesBrickPipelines
        );
}
