using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

public sealed partial class SdfWorldEngine {
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

    // The pipelines this engine records with. The owner built them and disposes them; a kernel reload swaps the
    // native objects behind the same slots, so the fields below never change.
    private readonly SdfWorldPipelines m_pipelines;

    /// <summary>Installs a kernel reload prepared by <see cref="SdfWorldPipelines.PrepareReload"/> at a render-thread
    /// boundary, preserving buffers, images, baked bricks, descriptors and scene state. A reload that changed nothing
    /// installs without a GPU wait; otherwise the frame ring drains, the changed pipelines swap in, and the ISA
    /// handshake runs against the new set before the reload commits. A failed handshake restores the previous
    /// pipelines. The caller disposes the reload afterwards either way: a committed reload's pipelines belong to the set,
    /// and disposing one that did not commit releases them.</summary>
    /// <param name="reload">A reload prepared against this engine's pipeline set and its current kernels.</param>
    /// <returns>The number of changed pipelines installed.</returns>
    /// <remarks>Call serially with submission. A C# binding or buffer-layout change still requires rebuilding the host.
    /// This does not reload child engines or postprocessing decorators owned by other nodes.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="reload"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
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

        WaitForFrameRing();
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
        } finally {
            // The ISA probe temporarily borrows slot zero's view-0 descriptors and tile buffer. Force their normal
            // rebinding and a fresh render even on rollback; retained world/image ownership is unchanged.
            Array.Clear(array: m_boundOutputViews[0]);
            Array.Clear(array: m_boundScreenSourceViews[0][0]);
            Array.Clear(array: m_boundGlyphAtlasViews);
            m_hasPreviousFrameSignature = false;
        }

        m_pipelines.Commit(reload: reload);
        ReconfigureWork();

        return reload.ChangedPipelines;
    }

    // The pipeline descriptions every engine shares. A nested holder, so its initializers run after the engine's own
    // statics, whatever order the partial files are compiled in.
    internal static class PipelineLayouts {
        // Every per-view pipeline binds the sdf-world interface's groups, and the baker the sdf-brick-bake interface's, so
        // one frame set and one views set per ring slot and view bind against any of the ten per-view pipelines.
        internal static readonly GpuPipelineLayoutDescription World = SdfWorldInterfaces.WorldLayout.PipelineLayout(stages: GpuShaderStage.Compute);
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
            Spec(name: "sdf-brick-bake", layout: BrickBake, brick: true),
        ];
        // The order a build starts the pipelines in (SdfWorldPipelines.Build): the views variants, the longest driver
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
