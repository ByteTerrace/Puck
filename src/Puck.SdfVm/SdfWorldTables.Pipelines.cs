using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldTables {
    // The pipelines the views' passes record with. The owner built them and disposes them; a kernel reload swaps the
    // native objects behind the same slots, so a pass reads each handle when it records.
    private readonly SdfWorldPipelines m_pipelines;
    // The device's mesh pass pipeline and the render pass it draws in, which a view's mesh pass records with.
    private readonly IGpuPipeline m_meshPipeline;
    // The impostor card pipeline, which draws in the same render pass beside it.
    private readonly IGpuPipeline m_impostorPipeline;
    private readonly IGpuRenderPass m_meshRenderPass;

    // The mesh pass's graphics pipeline.
    internal IGpuPipeline MeshPipeline => m_meshPipeline;
    // The impostor card pipeline: the mesh pass's layout and render pass, with a fragment stage that searches the impostor's
    // views for each pixel's surface.
    internal IGpuPipeline ImpostorPipeline => m_impostorPipeline;
    // The render pass the mesh pass draws in, which a view's framebuffers are created for.
    internal IGpuRenderPass MeshRenderPass => m_meshRenderPass;

    // The views pass's pipeline: the variant UploadProgram selected for the live program (full ISA, core ops or folds;
    // SdfViewsKernelVariant), or, while that one builds, a fuller variant that is built, since a fuller variant renders
    // every program a stripped one does. All are of one layout, so a pass's set binds against whichever it is.
    internal IGpuComputePipeline ViewsPipelineFor(int fadeCapacity) => m_pipelines.Pipeline(kernel: (BuiltViews(fadeCapacity: fadeCapacity, variant: m_viewsVariant) ?? ViewsKernelOf(fadeCapacity: fadeCapacity, variant: m_viewsVariant)));
    // The views kernel a program waits on: null when the variant it selects (SdfViewsKernelVariants.Select), or a fuller
    // one, is built, so its views can render it; otherwise the narrowest of those still building or, when every one was
    // refused (PipelineRefusal), the selected variant's kernel. A null program asks for the live program's variant.
    internal SdfKernel? ViewsWaiting(SdfProgram? program, int? fadeCapacity = null) {
        var capacity = (fadeCapacity ?? m_shadowFadeCapacity);
        var variant = ((program is null)
            ? m_viewsVariant
            : SdfViewsKernelVariants.Select(program: program).Variant);

        if (BuiltViews(fadeCapacity: capacity, variant: variant) is not null) {
            return null;
        }

        foreach (var kernel in ViewsKernelsOf(fadeCapacity: capacity, variant: variant)) {
            if (m_pipelines.RefusalOf(kernel: kernel) is null) {
                return kernel;
            }
        }

        return ViewsKernelOf(fadeCapacity: capacity, variant: variant);
    }
    // A new capacity waits for its shadow pipeline as well as one usable shading variant. Poll every acquired shadow
    // slot so a device loss in a variant not selected by this frame still reaches recovery.
    internal SdfKernel? FrameWaiting(SdfProgram? program, int? fadeCapacity = null) {
        var shadow = SdfWorldPipelines.ShadowKernelOf(fadeCapacity: (fadeCapacity ?? m_shadowFadeCapacity));
        var ready = false;

        foreach (var kernel in ((ReadOnlySpan<SdfKernel>)[SdfKernel.Shadow, SdfKernel.ShadowFade1, SdfKernel.ShadowFade2])) {
            var built = m_pipelines.IsBuilt(kernel: kernel);

            if (kernel == shadow) { ready = built; }
        }
        var views = ViewsWaiting(fadeCapacity: fadeCapacity, program: program);

        return (ready ? views : shadow);
    }
    // Why a frame's kernel was refused (SdfWorldPipelines.IsBuilt), or null when it was not.
    internal Exception? PipelineRefusal(SdfKernel kernel) => m_pipelines.RefusalOf(kernel: kernel);

    // The first built views kernel that renders a program selecting the variant: the variant's own, then each fuller one.
    // A refused kernel is not built, so a fuller one that is renders the program. Every views slot is polled, even
    // one this program cannot use: a device loss in its background build must reach recovery while another is refused.
    private SdfKernel? BuiltViews(SdfViewsKernelVariant variant, int fadeCapacity) {
        SdfKernel? built = null;
        var candidates = ViewsKernelsOf(fadeCapacity: fadeCapacity, variant: variant);

        foreach (var kernel in AllViews) {
            var ready = m_pipelines.IsBuilt(kernel: kernel);

            if (ready && (built is null) && (Array.IndexOf(array: candidates, value: kernel) >= 0)) {
                built = kernel;
            }
        }

        return built;
    }
    private static SdfKernel ViewsKernelOf(SdfViewsKernelVariant variant, int fadeCapacity) => ViewsKernelsOf(fadeCapacity: fadeCapacity, variant: variant)[0];
    // The views kernels that render a program selecting the variant, narrowest first.
    private static SdfKernel[] ViewsKernelsOf(SdfViewsKernelVariant variant, int fadeCapacity) => ViewsByFade[fadeCapacity][variant switch {
        SdfViewsKernelVariant.CoreOps => 0,
        SdfViewsKernelVariant.Folds => 1,
        _ => 2,
    }];

    private static readonly SdfKernel[] ViewsForCore = [SdfKernel.ViewsCore, SdfKernel.ViewsFolds, SdfKernel.Views];
    private static readonly SdfKernel[] ViewsForFolds = [SdfKernel.ViewsFolds, SdfKernel.Views];
    private static readonly SdfKernel[] ViewsForFull = [SdfKernel.Views];
    private static readonly SdfKernel[][][] ViewsByFade = [
        [ViewsForCore, ViewsForFolds, ViewsForFull],
        [[SdfKernel.ViewsCoreFade1, SdfKernel.ViewsFoldsFade1, SdfKernel.ViewsFade1], [SdfKernel.ViewsFoldsFade1, SdfKernel.ViewsFade1], [SdfKernel.ViewsFade1]],
        [[SdfKernel.ViewsCoreFade2, SdfKernel.ViewsFoldsFade2, SdfKernel.ViewsFade2], [SdfKernel.ViewsFoldsFade2, SdfKernel.ViewsFade2], [SdfKernel.ViewsFade2]],
    ];
    private static readonly SdfKernel[] AllViews = [.. ViewsByFade.SelectMany(selector: static variants => variants[0])];

    // One of the per-view compute pipelines by its kernel.
    internal IGpuComputePipeline Pipeline(SdfKernel kernel) => m_pipelines.Pipeline(kernel: kernel);

    // Resolve joins the same reloadable slot table only when a view needs it.
    internal SdfWorldPipelines Pipelines => m_pipelines;

    // Whether a view's passes built against these tables can follow the other tables: the beam covers the common
    // compute layout, the current F its extended pass set, mesh its graphics layout and render pass, and an acquired
    // resolve requires a ready resolve with compatible groups in the destination. A different F requires replanning.
    internal bool SharesLayoutsWith(SdfWorldTables other) =>
        (
            ReferenceEquals(
                objA: this,
                objB: other
            ) ||
            (
                Pipeline(kernel: SdfKernel.Beam).GroupLayoutHandles.SequenceEqual(second: other.Pipeline(kernel: SdfKernel.Beam).GroupLayoutHandles) &&
                (m_shadowFadeCapacity == other.m_shadowFadeCapacity) &&
                Pipeline(kernel: SdfWorldPipelines.ShadowKernelOf(fadeCapacity: m_shadowFadeCapacity)).GroupLayoutHandles.SequenceEqual(
                    second: other.Pipeline(kernel: SdfWorldPipelines.ShadowKernelOf(fadeCapacity: other.m_shadowFadeCapacity)).GroupLayoutHandles) &&
                m_meshPipeline.GroupLayoutHandles.SequenceEqual(second: other.m_meshPipeline.GroupLayoutHandles) &&
                m_impostorPipeline.GroupLayoutHandles.SequenceEqual(second: other.m_impostorPipeline.GroupLayoutHandles) &&
                ((m_pipelines.OptionalPipeline(kernel: SdfKernel.Resolve) is not { } resolve) ||
                    ((other.m_pipelines.OptionalPipeline(kernel: SdfKernel.Resolve) is { } otherResolve) &&
                        resolve.GroupLayoutHandles.SequenceEqual(second: otherResolve.GroupLayoutHandles))) &&
                ReferenceEquals(
                    objA: m_meshRenderPass,
                    objB: other.m_meshRenderPass
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
    /// set, or a changed fade variant was first requested after preparation.</exception>
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
        // Resolve joins on demand through BuildResolve; BuildOrder filters the rest by reachable fade capacity.
        internal static readonly SdfKernel[] Leased = [.. SdfKernelSet.Kernels.Where(predicate: static kernel => (kernel is not (SdfKernel.Resolve or SdfKernel.IndirectClassify or SdfKernel.IndirectTrace or SdfKernel.IndirectShade or SdfKernel.LightPrimary or SdfKernel.LightDepth)))];

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
