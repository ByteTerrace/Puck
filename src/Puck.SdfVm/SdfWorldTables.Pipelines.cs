using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

public sealed partial class SdfWorldTables {
    // Indices into PipelineLayouts.Specs and SdfWorldPipelines; PipelineLayouts.BuildOrder is the creation order.
    internal const int BeamPipelineIndex = 0;
    internal const int InstanceCullPipelineIndex = 1;
    internal const int CullArgsPipelineIndex = 2;
    internal const int PrimaryPipelineIndex = 3;
    internal const int SurfacePipelineIndex = 4;
    internal const int AmbientPipelineIndex = 5;
    internal const int ViewsPipelineIndex = 6;
    internal const int ViewsCorePipelineIndex = 7;
    internal const int ViewsFoldsPipelineIndex = 8;
    internal const int SkyPipelineIndex = 9;
    internal const int BrickBakePipelineIndex = 10;

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
    internal IGpuComputePipeline ViewsPipeline => m_pipelines.Pipeline(index: m_viewsVariant switch {
        SdfViewsKernelVariant.CoreOps => ViewsCorePipelineIndex,
        SdfViewsKernelVariant.Folds => ViewsFoldsPipelineIndex,
        _ => ViewsPipelineIndex,
    });

    // One of the per-view compute pipelines by its index.
    internal IGpuComputePipeline Pipeline(int index) => m_pipelines.Pipeline(index: index);
    // Whether a view's passes built against these tables record against the other tables as they are: every compute
    // part's set layouts agree (the parts share one interface, so the beam's stand for all), and the mesh pass draws
    // through the same pipeline layout into the same render pass its framebuffers were created for.
    internal bool SharesLayoutsWith(SdfWorldTables other) =>
        (
            ReferenceEquals(
                objA: this,
                objB: other
            ) ||
            (
                Pipeline(index: BeamPipelineIndex).GroupLayoutHandles.SequenceEqual(second: other.Pipeline(index: BeamPipelineIndex).GroupLayoutHandles) &&
                m_meshPipeline.GroupLayoutHandles.SequenceEqual(second: other.m_meshPipeline.GroupLayoutHandles) &&
                ReferenceEquals(
                    objA: m_meshRenderPass,
                    objB: other.m_meshRenderPass
                )
            )
        );

    /// <summary>Installs a kernel reload prepared by <see cref="SdfWorldPipelines.PrepareReload"/> at a render-thread
    /// boundary, preserving buffers, images, baked bricks, descriptors and scene state. A reload that changed nothing
    /// installs without a GPU wait; otherwise the device drains, the changed pipelines swap in, and the ISA
    /// handshake runs against the new set before the reload commits. A failed handshake restores the previous
    /// pipelines. The caller disposes the reload afterwards either way: a committed reload's pipelines belong to the set,
    /// and disposing one that did not commit releases them.</summary>
    /// <param name="reload">A reload prepared against these tables' pipeline set and its current kernels.</param>
    /// <returns>The number of changed pipelines installed.</returns>
    /// <remarks>Call serially with submission. A C# binding or buffer-layout change still requires rebuilding the host.
    /// This does not reload another residency's pipelines or postprocessing decorators owned by other nodes.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="reload"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The tables have been disposed.</exception>
    /// <exception cref="InvalidOperationException">The reload was prepared for a different pipeline set or kernel set,
    /// or the ISA handshake fails.</exception>
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

        try {
            SdfShaderSetVerification.VerifyShaderSet(
                device: m_deviceContext,
                kernels: reload.Kernels,
                verify: VerifyIsaVersion
            );
        } catch {
            m_pipelines.Rollback(reload: reload);
            throw;
        }

        m_pipelines.Commit(reload: reload);
        // New kernels render new pixels: every view renders again.
        m_programRevision++;
        ReconfigureWork();

        return reload.ChangedPipelines;
    }

    // The pipeline descriptions every residency shares. A nested holder, so its initializers run after the tables' own
    // statics, whatever order the partial files are compiled in.
    internal static class PipelineLayouts {
        // Every per-view pipeline binds the sdf-world interface's groups, so a pass's frame and pass sets bind against any
        // of the ten, and the baker the sdf-bricks interface's. The mesh pass draws with the sdf-mesh interface's: one set
        // per frame slot, the view and draw pushed.
        internal static readonly GpuPipelineLayoutDescription World = SdfWorldInterfaces.WorldLayout.PipelineLayout(stages: GpuShaderStage.Compute);
        internal static readonly GpuPipelineLayoutDescription Mesh = SdfWorldInterfaces.MeshLayout.PipelineLayout(stages: GpuShaderStage.Vertex | GpuShaderStage.Fragment);
        internal static readonly GpuPipelineLayoutDescription BrickBake = SdfWorldInterfaces.BrickBakeLayout.PipelineLayout(stages: GpuShaderStage.Compute);
        // Indexed by the *PipelineIndex constants.
        internal static readonly PipelineSpec[] Specs = [
            Spec(name: "sdf-beam", layout: World),
            Spec(name: "sdf-instance-cull", layout: World),
            Spec(name: "sdf-cull-args", layout: World),
            Spec(name: "sdf-world-primary", layout: World),
            Spec(name: "sdf-world-surface", layout: World),
            Spec(name: "sdf-world-ambient", layout: World),
            Spec(name: "sdf-world-views", layout: World),
            Spec(name: "sdf-world-views-core", layout: World),
            Spec(name: "sdf-world-views-folds", layout: World),
            Spec(name: "sdf-sky", layout: World),
            Spec(brick: true, layout: BrickBake, name: "sdf-brick-bake"),
        ];
        // The order a set leases the pipelines in (SdfWorldPipelines.Acquire): the views variants, the longest driver
        // translations, start last, lightest first (core, folds, full), and every other pipeline starts before them in
        // index order. Each Specs index appears exactly once.
        internal static readonly int[] BuildOrder = [
            BeamPipelineIndex,
            InstanceCullPipelineIndex,
            CullArgsPipelineIndex,
            PrimaryPipelineIndex,
            SurfacePipelineIndex,
            AmbientPipelineIndex,
            SkyPipelineIndex,
            BrickBakePipelineIndex,
            ViewsCorePipelineIndex,
            ViewsFoldsPipelineIndex,
            ViewsPipelineIndex,
        ];

        private static PipelineSpec Spec(string name, GpuPipelineLayoutDescription layout, bool brick = false) =>
            new(
                Brick: brick,
                Description: new GpuComputePipelineDescription(
                    Bindings: [],
                    Layout: layout,
                    Name: name,
                    PushConstantBinding: null
                )
            );
    }
    /// <summary>One engine pipeline: its description (the name selects its kernel) and whether only a brick pool
    /// needs it.</summary>
    internal readonly record struct PipelineSpec(GpuComputePipelineDescription Description, bool Brick);
}
