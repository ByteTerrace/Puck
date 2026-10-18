using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

public sealed partial class SdfWorldTables {
    // The pipelines the views' passes record with. The owner built them and disposes them; a kernel reload swaps the
    // native objects behind the same slots, so a pass reads each handle when it records.
    private readonly SdfWorldPipelines m_pipelines;
    // The device's mesh pass pipeline and the render pass it draws in, which a view's mesh pass records with.
    private readonly IGpuPipeline m_meshPipeline;
    private readonly IGpuRenderPass m_meshRenderPass;
    // The shadow cascades' pipeline and the depth-only render pass it draws in.
    private readonly IGpuPipeline m_shadowPipeline;
    private readonly IGpuRenderPass m_shadowRenderPass;

    // The mesh pass's graphics pipeline.
    internal IGpuPipeline MeshPipeline => m_meshPipeline;
    // The render pass the mesh pass draws in, which a view's framebuffers are created for.
    internal IGpuRenderPass MeshRenderPass => m_meshRenderPass;
    // The shadow cascades' pipeline: the mesh pass's layout and vertex stage, writing depth alone.
    internal IGpuPipeline ShadowPipeline => m_shadowPipeline;
    // The depth-only render pass a view's cascades draw in, which its cascade framebuffers are created for.
    internal IGpuRenderPass ShadowRenderPass => m_shadowRenderPass;
    // A frame waits for the views kernel, the one pipeline the tables are built without (SdfWorldPipelines.PollRequired):
    // null once it is built, the kernel otherwise, refused (PipelineRefusal) or still building.
    internal SdfKernel? FrameWaiting() =>
        (m_pipelines.IsBuilt(kernel: SdfKernel.Views) ? null : SdfKernel.Views);

    // Why a frame's kernel was refused (SdfWorldPipelines.IsBuilt), or null when it was not.
    internal Exception? PipelineRefusal(SdfKernel kernel) => m_pipelines.RefusalOf(kernel: kernel);

    // One of the per-view compute pipelines by its kernel.
    internal IGpuComputePipeline Pipeline(SdfKernel kernel) => m_pipelines.Pipeline(kernel: kernel);

    // Resolve joins the same reloadable slot table only when a view needs it.
    internal SdfWorldPipelines Pipelines => m_pipelines;

    // Whether a view's passes built against these tables can follow the other tables: the surface kernel covers the
    // compute layout every per-view pass shares, mesh its graphics layout and render pass, and an acquired resolve
    // requires a ready resolve with compatible groups in the destination.
    internal bool SharesLayoutsWith(SdfWorldTables other) =>
        (
            ReferenceEquals(
                objA: this,
                objB: other
            ) ||
            (
                Pipeline(kernel: SdfKernel.Surface).GroupLayoutHandles.SequenceEqual(second: other.Pipeline(kernel: SdfKernel.Surface).GroupLayoutHandles) &&
                m_meshPipeline.GroupLayoutHandles.SequenceEqual(second: other.m_meshPipeline.GroupLayoutHandles) &&
                m_shadowPipeline.GroupLayoutHandles.SequenceEqual(second: other.m_shadowPipeline.GroupLayoutHandles) &&
                ((m_pipelines.OptionalPipeline(kernel: SdfKernel.Resolve) is not { } resolve) ||
                    ((other.m_pipelines.OptionalPipeline(kernel: SdfKernel.Resolve) is { } otherResolve) &&
                        resolve.GroupLayoutHandles.SequenceEqual(second: otherResolve.GroupLayoutHandles))) &&
                ReferenceEquals(
                    objA: m_meshRenderPass,
                    objB: other.m_meshRenderPass
                ) &&
                ReferenceEquals(
                    objA: m_shadowRenderPass,
                    objB: other.m_shadowRenderPass
                )
            )
        );

    /// <summary>Installs a kernel reload prepared by <see cref="SdfWorldPipelines.PrepareReload"/> at a render-thread
    /// boundary, preserving buffers, images, baked bricks, descriptors and scene state. A reload that changed nothing
    /// installs without a GPU wait; otherwise the device drains and the changed pipelines swap in. The caller disposes the
    /// reload afterwards: a committed reload's pipelines belong to the set.</summary>
    /// <param name="reload">A reload prepared against these tables' pipeline set and its current kernels.</param>
    /// <returns>The number of changed pipelines installed.</returns>
    /// <remarks>Call serially with submission. A C# binding or buffer-layout change still requires rebuilding the host.
    /// This does not reload another residency's pipelines or postprocessing decorators owned by other nodes.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="reload"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The tables have been disposed.</exception>
    /// <exception cref="InvalidOperationException">The reload was prepared for a different pipeline set or kernel
    /// set.</exception>
    public int InstallReload(SdfWorldPipelineReload reload) {
        ArgumentNullException.ThrowIfNull(reload);
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        m_pipelines.ThrowIfNotCurrent(reload: reload);

        if (reload.ChangedPipelines == 0) {
            m_pipelines.Commit(reload: reload);

            return 0;
        }

        // Every view's submission in flight records with the pipelines about to be replaced.
        m_deviceContext.TryWaitIdle();
        m_pipelines.Exchange(reload: reload);
        m_pipelines.Commit(reload: reload);
        // The environment map's kernels may be among them: its graph producer renders again when a consumer needs it.
        m_skyEnvironment.Forget();
        m_screenEmission.Forget();
        // New kernels render new pixels: every view renders again.
        m_programRevision++;
        m_programGeometryRevision++;
        ReconfigureWork();

        return reload.ChangedPipelines;
    }

    // The pipeline descriptions every residency shares. A nested holder, so its initializers run after the tables' own
    // statics, whatever order the partial files are compiled in.
    internal static class PipelineLayouts {
        // The native per-view pipelines share the sdf-world groups; the baker uses sdf-bricks. Optional resolve uses
        // its own pass group. Mesh draws through sdf-mesh, with one set per frame slot and the view and draw pushed.
        internal static readonly GpuPipelineLayoutDescription World = SdfWorldInterfaces.WorldLayout.PipelineLayout(stages: GpuShaderStage.Compute);
        internal static readonly GpuPipelineLayoutDescription Mesh = SdfWorldInterfaces.MeshLayout.PipelineLayout(stages: GpuShaderStage.Vertex | GpuShaderStage.Fragment);
        internal static readonly GpuPipelineLayoutDescription BrickBake = SdfWorldInterfaces.BrickBakeLayout.PipelineLayout(stages: GpuShaderStage.Compute);
        internal static readonly GpuPipelineLayoutDescription Environment = SdfWorldInterfaces.EnvironmentParameters.Layout.PipelineLayout(stages: GpuShaderStage.Compute);
        // One per kernel in SdfKernel order, with the layout and name from the same immutable kernel set.
        internal static readonly PipelineSpec[] Specs = [.. SdfKernelSet.Kernels.Select(selector: static kernel => Spec(kernel: kernel))];
        // Resolve joins on demand through BuildResolve; every other kernel is leased up front.
        internal static readonly SdfKernel[] Leased = [.. SdfKernelSet.Kernels.Where(predicate: static kernel => (kernel is not SdfKernel.Resolve))];

        private static PipelineSpec Spec(SdfKernel kernel) =>
            new(
                Brick: (kernel == SdfKernel.BrickBake),
                Description: new GpuComputePipelineDescription(
                    Bindings: [],
                    Layout: SdfKernelSet.LayoutOf(kernel: kernel).PipelineLayout(stages: GpuShaderStage.Compute),
                    Name: SdfKernelSet.StemOf(kernel: kernel),
                    PushConstantBinding: null
                )
            );
    }
    /// <summary>One engine pipeline: its description, named by its kernel's stem, and whether only a brick pool needs
    /// it.</summary>
    internal readonly record struct PipelineSpec(GpuComputePipelineDescription Description, bool Brick);
}
