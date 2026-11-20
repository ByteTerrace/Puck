using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

public sealed partial class SdfWorldTables {
    // The pipelines the views' passes record with. The owner built them and disposes them; a kernel reload swaps the
    // native objects behind the same slots, so a pass reads each handle when it records.
    private readonly SdfWorldPipelines m_pipelines;
    // The pass pipelines a view's passes record with, each read only once built (SdfPassPipelineSlot.Built), which
    // FrameWaiting finds before any view records: the mesh pass's (and the render pass it draws in), the shadow cascades'
    // (and their depth-only render pass), the cascades' exit layer, drawing the faces turned from the light in the same
    // render pass, and their glass extinction, the glass pass's, drawing in the mesh render pass, and the mesh pass's
    // occlusion kernel, which binds the mesh pass's set.
    private readonly SdfPassPipelineSlot m_meshRaster;
    private readonly SdfPassPipelineSlot m_shadowRaster;
    private readonly SdfPassPipelineSlot m_shadowExitRaster;
    private readonly SdfPassPipelineSlot m_shadowGlassRaster;
    private readonly SdfPassPipelineSlot m_glassRaster;
    private readonly SdfPassPipelineSlot m_occlusion;

    // The mesh pass's occlusion kernel (SdfMeshOcclusion).
    internal IGpuComputePipeline OcclusionPipeline => (m_occlusion.Built.Compute ?? throw new InvalidOperationException(message: "The mesh pass occlusion kernel is not a compute pipeline."));

    // The glass pass's graphics pipeline: the mesh pass's layout, vertex stage and render pass with the glass fragment
    // stage.
    internal IGpuPipeline GlassPipeline => GraphicsOf(slot: m_glassRaster);

    // The mesh pass's graphics pipeline.
    internal IGpuPipeline MeshPipeline => GraphicsOf(slot: m_meshRaster);
    // The render pass the mesh pass draws in, which a view's framebuffers are created for.
    internal IGpuRenderPass MeshRenderPass => RenderPassOf(slot: m_meshRaster);
    // The shadow cascades' pipeline: the mesh pass's layout and vertex stage, writing depth alone.
    internal IGpuPipeline ShadowPipeline => GraphicsOf(slot: m_shadowRaster);
    // The shadow cascades' exit-layer pipeline: the shadow pipeline with the faces toward the light culled.
    internal IGpuPipeline ShadowExitPipeline => GraphicsOf(slot: m_shadowExitRaster);
    internal IGpuPipeline ShadowGlassPipeline => GraphicsOf(slot: m_shadowGlassRaster);
    // The depth-only render pass a view's cascades draw in, which its cascade framebuffers are created for.
    internal IGpuRenderPass ShadowRenderPass => RenderPassOf(slot: m_shadowRaster);

    private static IGpuPipeline GraphicsOf(SdfPassPipelineSlot slot) => (slot.Built.Graphics ?? throw new InvalidOperationException(message: $"The '{slot.Name}' pass pipeline is not a graphics pipeline."));
    private static IGpuRenderPass RenderPassOf(SdfPassPipelineSlot slot) => (slot.Built.RenderPass ?? throw new InvalidOperationException(message: $"The '{slot.Name}' pass pipeline names no render pass."));

    // A frame waits for every pipeline its views record with, which the tables are built without
    // (SdfWorldPipelines.PollRequired): null once every one is built, else the first still building or refused, kernels in
    // their order and then the pass pipelines. Takes each build that has just finished.
    internal SdfPipelineWait? FrameWaiting() {
        if (m_pipelines.FirstUnbuilt() is { } kernel) { return new SdfPipelineWait(Name: SdfKernelSet.StemOf(kernel: kernel), Refusal: m_pipelines.RefusalOf(kernel: kernel)); }
        foreach (var slot in (ReadOnlySpan<SdfPassPipelineSlot>)[m_meshRaster, m_shadowRaster, m_shadowExitRaster, m_shadowGlassRaster, m_glassRaster, m_occlusion]) {
            if (!slot.PollRefusing()) { return new SdfPipelineWait(Name: slot.Name, Refusal: slot.Refusal); }
        }
        return null;
    }

    // One of the per-view compute pipelines by its kernel.
    internal IGpuComputePipeline Pipeline(SdfKernel kernel) => m_pipelines.Pipeline(kernel: kernel);

    // Resolve joins the same reloadable slot table only when a view needs it.
    internal SdfWorldPipelines Pipelines => m_pipelines;
    // The device the tables were created on.
    internal IGpuDeviceContext Device => m_deviceContext;

    // Whether a view's passes built against these tables can follow the other tables: the surface kernel covers the
    // compute layout every per-view pass shares, mesh its graphics layout and render pass, and an acquired resolve
    // requires a ready resolve with compatible groups in the destination. Tables whose frame still waits on a pipeline
    // share nothing yet (FrameWaiting).
    internal bool SharesLayoutsWith(SdfWorldTables other) =>
        (
            ReferenceEquals(
                objA: this,
                objB: other
            ) ||
            (
                (FrameWaiting() is null) &&
                (other.FrameWaiting() is null) &&
                Pipeline(kernel: SdfKernel.Surface).GroupLayoutHandles.SequenceEqual(second: other.Pipeline(kernel: SdfKernel.Surface).GroupLayoutHandles) &&
                MeshPipeline.GroupLayoutHandles.SequenceEqual(second: other.MeshPipeline.GroupLayoutHandles) &&
                ShadowPipeline.GroupLayoutHandles.SequenceEqual(second: other.ShadowPipeline.GroupLayoutHandles) &&
                ShadowExitPipeline.GroupLayoutHandles.SequenceEqual(second: other.ShadowExitPipeline.GroupLayoutHandles) &&
                ShadowGlassPipeline.GroupLayoutHandles.SequenceEqual(second: other.ShadowGlassPipeline.GroupLayoutHandles) &&
                GlassPipeline.GroupLayoutHandles.SequenceEqual(second: other.GlassPipeline.GroupLayoutHandles) &&
                ((m_pipelines.OptionalPipeline(kernel: SdfKernel.Resolve) is not { } resolve) ||
                    ((other.m_pipelines.OptionalPipeline(kernel: SdfKernel.Resolve) is { } otherResolve) &&
                        resolve.GroupLayoutHandles.SequenceEqual(second: otherResolve.GroupLayoutHandles))) &&
                ReferenceEquals(
                    objA: MeshRenderPass,
                    objB: other.MeshRenderPass
                ) &&
                ReferenceEquals(
                    objA: ShadowRenderPass,
                    objB: other.ShadowRenderPass
                )
            )
        );

    /// <summary>Installs a kernel reload prepared by <see cref="SdfWorldPipelines.PrepareReload"/> at a render-thread
    /// boundary, preserving buffers, images, descriptors and scene state. Nothing waits for the GPU: the changed
    /// pipelines swap in, and the ones they replace are retired with the tables' other replaced objects, released once a
    /// device checkpoint queued after every submission that recorded with them has completed. The caller disposes the
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
            m_pipelines.Commit(reload: reload)?.Dispose();

            return 0;
        }

        // Every view's submission in flight may record with the pipelines being replaced; the next upload's checkpoint
        // follows them on the one queue, so the replaced leases are released once it completes (ServiceRetirements).
        m_pipelines.Exchange(reload: reload);
        if (m_pipelines.Commit(reload: reload) is { } replaced) {
            Retire(replaced: replaced);
        }
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
        // The native per-view pipelines share the sdf-world groups. Optional resolve uses
        // its own pass group. Mesh draws through sdf-mesh, with one set per frame slot and the view and draw pushed; the
        // mesh pass's occlusion kernel reads the same interface through a compute layout of its own, with sets of its own
        // binding the same resources.
        internal static readonly GpuPipelineLayoutDescription World = SdfWorldInterfaces.WorldLayout.PipelineLayout(stages: GpuShaderStage.Compute);
        internal const GpuShaderStage MeshStages = (GpuShaderStage.Vertex | GpuShaderStage.Fragment);
        internal static readonly GpuPipelineLayoutDescription Mesh = SdfWorldInterfaces.MeshLayout.PipelineLayout(stages: MeshStages);
        internal static readonly GpuPipelineLayoutDescription MeshOcclusion = SdfWorldInterfaces.MeshLayout.PipelineLayout(stages: GpuShaderStage.Compute);
        // One per kernel in SdfKernel order, with the layout and name from the same immutable kernel set.
        internal static readonly PipelineSpec[] Specs = [.. SdfKernelSet.Kernels.Select(selector: static kernel => Spec(kernel: kernel))];
        // The on-demand kernels join through SdfWorldPipelines.BuildOnDemandAsync; every other kernel is leased up front.
        internal static readonly SdfKernel[] Leased = [.. SdfKernelSet.Kernels.Where(predicate: static kernel => !SdfWorldPipelines.AcquiredOnDemand(kernel: kernel))];

        private static PipelineSpec Spec(SdfKernel kernel) =>
            new(
                Description: new GpuComputePipelineDescription(
                    Bindings: [],
                    Layout: SdfKernelSet.LayoutOf(kernel: kernel).PipelineLayout(stages: GpuShaderStage.Compute),
                    Name: SdfKernelSet.StemOf(kernel: kernel),
                    PushConstantBinding: null
                )
            );
    }
    /// <summary>One engine pipeline: its description, named by its kernel's stem.</summary>
    internal readonly record struct PipelineSpec(GpuComputePipelineDescription Description);
}
// The pipeline a frame waits on (SdfWorldTables.FrameWaiting): its name, a kernel's stem or a pass pipeline's, and why
// its build was refused, or null while it builds.
internal readonly record struct SdfPipelineWait(string Name, Exception? Refusal);
