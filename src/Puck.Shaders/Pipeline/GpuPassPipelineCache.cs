using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>
/// The pass pipelines of a composition: one <see cref="GpuPassPipeline"/> per device and <see cref="GpuPassPipelineKey"/>,
/// however many graph nodes, package passes and region owners record with it. Every pass pipeline a
/// <see cref="ShaderPipelineRenderNode"/> installs comes from here — its document passes, its float preview, and each
/// package pass its package builds.
/// It is a <see cref="GpuBuildCache{TKey, T}"/>: the first lease on a key builds the pipeline on the thread pool, a
/// second node or a reinstall of the same graph joins it, and the pipeline is disposed when its last lease is released,
/// so a reload of a changed shader makes a new entry while the replaced graph's lease keeps the old one until that graph
/// retires after its submissions.
/// <para>
/// The cache counts the shader modules, render passes and pipelines it creates into <see cref="Work"/>, named
/// <see cref="WorkSourceName"/>; no node's ledger counts them. It names each object it creates
/// <c>gpu.pass-pipelines/&lt;name&gt;/&lt;content key&gt;</c>, from the key alone, so a shared object is named alike
/// whichever holder built it.
/// </para>
/// </summary>
public sealed class GpuPassPipelineCache {
    /// <summary>The name a counters report heads <see cref="Work"/>'s section with, and the owner every object the cache
    /// creates is named by.</summary>
    public const string WorkSourceName = "gpu.pass-pipelines";

    private readonly GpuBuildCache<GpuPassPipelineKey, GpuPassPipeline> m_entries = new(
        build: static (request, token) => Build(
            cancellationToken: token,
            device: request.Device,
            key: request.Key,
            ledger: request.Ledger
        ),
        workSourceName: WorkSourceName
    );

    /// <summary>Gets the number of pipelines a new lease can join, built or building.</summary>
    public int SharedPipelines => m_entries.SharedEntries;
    /// <summary>Gets the shader modules, render passes and pipelines the cache has created, over its whole life.</summary>
    public IWorkCounterSource Work => m_entries.Work;

    /// <summary>Takes a lease on <paramref name="key"/>'s pipeline on <paramref name="device"/>, joining the entry
    /// another holder already leases or starting its build on the thread pool. Safe on any thread.</summary>
    /// <param name="device">The device the pipeline is created on.</param>
    /// <param name="key">The pipeline's key.</param>
    /// <returns>The lease, which the caller releases once nothing it recorded with the pipeline is in flight.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> or <paramref name="key"/> is
    /// <see langword="null"/>.</exception>
    public GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline> Acquire(IGpuDeviceContext device, GpuPassPipelineKey key) =>
        m_entries.Acquire(
            device: device,
            key: key
        );
    /// <summary>Creates a key's pipeline on a device, on the calling thread: its shader modules, then for a graphics
    /// pipeline its render pass, then the pipeline, each counted into <paramref name="ledger"/>, and asks a device that
    /// keeps a persistent pipeline cache to write it. The cache builds through it on the thread pool; a harness that
    /// drives an owner directly calls it itself.</summary>
    /// <param name="device">The device the pipeline is created on, through its services.</param>
    /// <param name="key">The pipeline's key.</param>
    /// <param name="ledger">The ledger that counts every object created.</param>
    /// <param name="cancellationToken">The token checked between creations, never during one.</param>
    /// <returns>The pipeline, owned by the caller.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="device"/>, <paramref name="key"/> or
    /// <paramref name="ledger"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The build was canceled between two creations; what it created is
    /// released.</exception>
    public static GpuPassPipeline Build(IGpuDeviceContext device, GpuPassPipelineKey key, GpuWorkLedger ledger, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(argument: device);
        ArgumentNullException.ThrowIfNull(argument: key);
        ArgumentNullException.ThrowIfNull(argument: ledger);

        var gpu = GpuWorkCounting.Wrap(
            ledger: ledger,
            services: device.Services
        );
        var name = new GpuObjectName(
            detail: key.ContentKey,
            owner: WorkSourceName,
            part: key.Name
        );
        var built = new GpuPassPipeline();

        try {
            cancellationToken.ThrowIfCancellationRequested();

            if (key.Compute is { } compute) {
                built.Primary = gpu.ShaderModuleFactory.Create(
                    bytecode: key.Primary,
                    stage: GpuShaderStage.Compute
                );
                cancellationToken.ThrowIfCancellationRequested();
                built.Compute = gpu.PipelineFactory.Create(
                    computeShaderModule: built.Primary,
                    description: compute,
                    name: name
                );
            } else {
                built.Primary = gpu.ShaderModuleFactory.Create(
                    bytecode: key.Primary,
                    stage: GpuShaderStage.Vertex
                );
                cancellationToken.ThrowIfCancellationRequested();
                built.Secondary = gpu.ShaderModuleFactory.Create(
                    bytecode: key.Secondary,
                    stage: GpuShaderStage.Fragment
                );
                cancellationToken.ThrowIfCancellationRequested();
                built.RenderPass = gpu.RenderPassFactory.Create(
                    description: key.RenderPass!,
                    name: name
                );
                cancellationToken.ThrowIfCancellationRequested();
                built.Graphics = gpu.PipelineFactory.Create(
                    description: key.Graphics!,
                    fragmentShaderModule: built.Secondary,
                    name: name,
                    renderPass: built.RenderPass,
                    vertexShaderModule: built.Primary
                );
            }
        } catch {
            built.Dispose();

            throw;
        }

        (device as IGpuPipelineCache)?.Persist();

        return built;
    }
}
/// <summary>
/// One pass pipeline and what it was created from, released together: a compute pipeline and its shader module, or a
/// graphics pipeline, its two shader modules and the render pass it draws in. Its owner is the
/// <see cref="GpuPassPipelineCache"/> or the harness that built it, never a holder recording with it; a holder creates
/// its own framebuffers against <see cref="RenderPass"/>.
/// </summary>
public sealed class GpuPassPipeline : IDisposable {
    internal GpuPassPipeline() {
    }

    /// <summary>Gets the compute pipeline, or <see langword="null"/> for a graphics pass.</summary>
    public IGpuComputePipeline? Compute { get; internal set; }
    /// <summary>Gets the graphics pipeline, or <see langword="null"/> for a compute pass.</summary>
    public IGpuPipeline? Graphics { get; internal set; }
    /// <summary>Gets the group layout handles of whichever pipeline the pass has, which its sets are allocated
    /// against.</summary>
    public IReadOnlyList<nint> GroupLayoutHandles => (Compute?.GroupLayoutHandles ?? Graphics!.GroupLayoutHandles);
    /// <summary>Gets the handle of whichever pipeline the pass has, which a recording binds.</summary>
    public nint Handle => (Compute?.Handle ?? Graphics!.Handle);
    /// <summary>Gets the pipeline layout handle of whichever pipeline the pass has, which a recording binds sets
    /// through.</summary>
    public nint LayoutHandle => (Compute?.LayoutHandle ?? Graphics!.LayoutHandle);
    /// <summary>Gets the render pass a graphics pipeline draws in, or <see langword="null"/> for a compute pass.</summary>
    public IGpuRenderPass? RenderPass { get; internal set; }

    internal IGpuShaderModule? Primary { get; set; }
    internal IGpuShaderModule? Secondary { get; set; }

    /// <summary>Releases the pipeline, its render pass and its shader modules. Disposing twice does nothing.</summary>
    public void Dispose() {
        Compute?.Dispose();
        Compute = null;
        Graphics?.Dispose();
        Graphics = null;
        RenderPass?.Dispose();
        RenderPass = null;
        Primary?.Dispose();
        Primary = null;
        Secondary?.Dispose();
        Secondary = null;
    }
}
