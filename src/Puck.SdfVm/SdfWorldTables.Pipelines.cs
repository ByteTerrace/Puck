using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

public sealed partial class SdfWorldTables {
    // The pipelines the views' passes record with. The owner built them and disposes them; a kernel reload swaps the
    // native objects behind the same slots, so a pass reads each handle when it records.
    private readonly SdfWorldPipelines m_pipelines;
    // The device's mesh pass pipeline and the render pass it draws in, which a view's mesh pass records with.
    private readonly IGpuPipeline m_meshPipeline;
    private readonly IGpuRenderPass m_meshRenderPass;

    // The mesh pass's graphics pipeline.
    internal IGpuPipeline MeshPipeline => m_meshPipeline;
    // The render pass the mesh pass draws in, which a view's framebuffers are created for.
    internal IGpuRenderPass MeshRenderPass => m_meshRenderPass;
    // The views pass's pipeline: the variant UploadProgram selected for the live program (full ISA, core ops or folds;
    // SdfViewsKernelVariant), all of one layout, so a pass's set binds against whichever it is.
    internal IGpuComputePipeline ViewsPipeline => m_pipelines.Pipeline(kernel: m_viewsVariant switch {
        SdfViewsKernelVariant.CoreOps => SdfKernel.ViewsCore,
        SdfViewsKernelVariant.Folds => SdfKernel.ViewsFolds,
        _ => SdfKernel.Views,
    });

    // Temporal shading uses the same proven operation subset as the native view.
    internal IGpuComputePipeline TemporalViewsPipeline => m_pipelines.Pipeline(kernel: m_viewsVariant switch {
        SdfViewsKernelVariant.CoreOps => SdfKernel.TemporalViewsCore,
        SdfViewsKernelVariant.Folds => SdfKernel.TemporalViewsFolds,
        _ => SdfKernel.TemporalViews,
    });

    // One of the per-view compute pipelines by its kernel.
    internal IGpuComputePipeline Pipeline(SdfKernel kernel) => m_pipelines.Pipeline(kernel: kernel);

    // Resolve joins the same reloadable slot table only when a view needs it.
    internal SdfWorldPipelines Pipelines => m_pipelines;

    // Whether a view's passes built against these tables can follow the other tables: the beam covers the common
    // compute layouts, mesh keeps its graphics layout and render pass, and an acquired resolve requires a ready
    // resolve with compatible groups in the destination.
    internal bool SharesLayoutsWith(SdfWorldTables other) =>
        (
            ReferenceEquals(
                objA: this,
                objB: other
            ) ||
            (
                Pipeline(kernel: SdfKernel.Beam).GroupLayoutHandles.SequenceEqual(second: other.Pipeline(kernel: SdfKernel.Beam).GroupLayoutHandles) &&
                m_meshPipeline.GroupLayoutHandles.SequenceEqual(second: other.m_meshPipeline.GroupLayoutHandles) &&
                SharesOptionalLayoutsWith(other) &&
                ReferenceEquals(
                    objA: m_meshRenderPass,
                    objB: other.m_meshRenderPass
                )
            )
        );

    private bool SharesOptionalLayoutsWith(SdfWorldTables other) {
        foreach (var kernel in SdfKernelSet.Kernels) {
            if (SdfKernelSet.IsOptional(kernel) && m_pipelines.OptionalPipeline(kernel) is { } pipeline &&
                (other.m_pipelines.OptionalPipeline(kernel) is not { } target || !pipeline.GroupLayoutHandles.SequenceEqual(target.GroupLayoutHandles))) { return false; }
        }
        return true;
    }

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
        // New kernels render new pixels: every view renders again.
        m_programRevision++;
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
        // One per kernel in SdfKernel order, with the layout and name from the same immutable kernel set.
        internal static readonly PipelineSpec[] Specs = [.. SdfKernelSet.Kernels.Select(selector: static kernel => Spec(kernel: kernel))];
        // The order a set leases the pipelines in (SdfWorldPipelines.Acquire): the views variants, the longest driver
        // translations, start last, lightest first (core, folds, full), and the other native kernels before them.
        // Reconstruction and temporal shading join this same slot table on demand through BuildOptional.
        internal static readonly SdfKernel[] BuildOrder = [
            .. SdfKernelSet.Kernels.Where(predicate: static kernel => (kernel is not (SdfKernel.Views or SdfKernel.ViewsCore or SdfKernel.ViewsFolds) && !SdfKernelSet.IsOptional(kernel: kernel))),
            SdfKernel.ViewsCore,
            SdfKernel.ViewsFolds,
            SdfKernel.Views,
        ];

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
