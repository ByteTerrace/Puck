using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

public sealed partial class SdfWorldEngine {
    // Indices into PipelineLayouts.Specs and SdfWorldPipelines, in creation order.
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
    internal const int CompositePipelineIndex = 10;
    internal const int BrickBakePipelineIndex = 11;
    internal const int BrickUploadPipelineIndex = 12;
    internal const int FrameUploadPipelineIndex = 13;

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
    /// <exception cref="InvalidOperationException">A preview readback is outstanding, the reload was prepared for a
    /// different pipeline set or kernel set, or the ISA handshake fails.</exception>
    public int InstallReload(SdfWorldPipelineReload reload) {
        ArgumentNullException.ThrowIfNull(reload);
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        ThrowIfPipelinedFrameInFlight();
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
            // The ISA probe temporarily borrows slot zero's source descriptors and tile buffer. Force their normal
            // rebinding and a fresh render even on rollback; retained world/image ownership is unchanged.
            Array.Clear(array: m_boundSourceViews[0]);
            Array.Clear(array: m_boundScreenSourceViews[0]);
            Array.Clear(array: m_boundGlyphAtlasViews);
            m_hasPreviousFrameSignature = false;
        }

        m_pipelines.Commit(reload: reload);
        ReconfigureWork();

        return reload.ChangedPipelines;
    }

    // The binding layouts and pipeline descriptions every engine shares. A nested holder, so its initializers run after
    // the engine's own statics (ScreenSourceBindingIndices above all), whatever order the partial files are compiled in.
    internal static class PipelineLayouts {
        // Beam prepass: program (1) + viewports (2) + dynamic entity transforms (9) + cull buffer written (3) + the
        // per-tile instance mask READ (7 — the MASK-FIRST order: the cone march evaluates the tile-masked field the
        // instance-cull pass wrote, so a march sample costs O(instances near the tile), not O(all instances)). No
        // output image. Direct3D 12 assigns registers from THIS order: t0 program, t1 viewports, t2 dynamicTransforms,
        // u0 tiles, t3 instanceMasks — the kernel's SDF_INSTANCE_MASKS_REGISTER override mirrors it.
        internal static readonly GpuComputeBinding[] Beam = [
            new GpuComputeBinding(
                Binding: ProgramBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            new GpuComputeBinding(
                Binding: ViewportBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            new GpuComputeBinding(
                Binding: DynamicTransformBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            new GpuComputeBinding(
                Binding: TileBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferReadWrite
            ),
            new GpuComputeBinding(
                Binding: InstanceMaskBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            // The brick pool (sdfBrickPool), APPENDED LAST so its SRV resolves to register t4 (after instanceMasks t3) —
            // the cone march samples baked SampledRegion carves. Always present (a filler when the pool is disabled).
            new GpuComputeBinding(
                Binding: BrickPoolBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
        ];
        // Instance-cull pass (sdf-instance-cull.comp — the frame's FIRST pass, and its OWN kernel so the cell walk's
        // register footprint never taxes the cone march's occupancy): program (1) + viewports (2) + dynamic entity
        // transforms (9, a DYNAMIC instance's bound resolves through it) + the per-tile instance mask written (7).
        // Direct3D 12 assigns registers from THIS order: t0 program, t1 viewports, t2 dynamicTransforms, u0
        // instanceMasks — the kernel's register() annotations mirror it exactly.
        internal static readonly GpuComputeBinding[] InstanceCull = [
            new GpuComputeBinding(
                Binding: ProgramBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            new GpuComputeBinding(
                Binding: ViewportBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            new GpuComputeBinding(
                Binding: DynamicTransformBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            new GpuComputeBinding(
                Binding: InstanceMaskBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferReadWrite
            ),
            new GpuComputeBinding(
                Binding: FrameInstanceGridBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
        ];
        // Cull-args reduction: cull buffer read (3) + the views indirect args written (5) + the dispatch box written (6).
        internal static readonly GpuComputeBinding[] CullArgs = [
            new GpuComputeBinding(
                Binding: TileBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            new GpuComputeBinding(
                Binding: CullArgsBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferReadWrite
            ),
            new GpuComputeBinding(
                Binding: CullBoundsBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferReadWrite
            ),
        ];
        // Stage 1 (per-view SDF): program (1) + viewports (2) + dynamic entity transforms (9) + the source array (4) +
        // the GPU-computed dispatch box (8) + the screen-surface table (10) + THIRTY-TWO separate screen-source
        // SampledImage bindings (12..43 — DXC cannot fuse an ARRAY texture into one Vulkan combined-image-sampler, so
        // each screen index gets its own binding; the pipeline factory bakes in ONE static nearest sampler PER
        // SampledImage binding on Direct3D 12, all sharing that one filter) + the per-tile instance mask read (7) and
        // the later read-only tables, then the cull buffer read (3), the visibility records written (49) and the same
        // records read (50). The SRV registers resolve program t0, viewport t1, dynamicTransforms t2, cullBounds t3,
        // screenSurfaces t4, screenSources t5..t36, instanceMasks t37, screenLights t38, glyph atlas t39, decals t40,
        // brick pool t41, frame instance grid t42, volumes t43, cull buffer t44, visibility records t45; the UAVs
        // resolve sources u0..u4, visibility records u5 (matching the HLSL) — Direct3D 12 assigns t#/u#/s# registers
        // from THIS array's order (DirectXGpuPipelineFactory), so the HLSL's explicit register annotations must mirror
        // this exact sequence; a reorder here without the matching HLSL edit desyncs the root signature. Every buffer
        // a hit pass only reads binds read-only, so SdfFrameBufferPlan declares plain reads for it. The 32
        // screen-source bindings are SPREAD from a MaxScreenSurfaces-derived list — never a hand-listed run.
        internal static readonly GpuComputeBinding[] Views = [
            new GpuComputeBinding(
                Binding: ProgramBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            new GpuComputeBinding(
                Binding: ViewportBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            new GpuComputeBinding(
                Binding: DynamicTransformBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            new GpuComputeBinding(
                Binding: ViewSourceBindingIndex,
                Count: MaxViewports,
                Kind: GpuComputeBindingKind.StorageImage
            ),
            new GpuComputeBinding(
                Binding: ViewsCullBoundsBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            new GpuComputeBinding(
                Binding: ScreenSurfaceBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            .. BuildScreenSourceBindings(),
            new GpuComputeBinding(
                Binding: InstanceMaskBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            // The per-frame screen-light buffer — its SRV resolves to register t38 (after instanceMasks t37).
            new GpuComputeBinding(
                Binding: ScreenLightBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            // The SDF_SHAPE_GLYPH font atlas: its SRV resolves to register t39 (after screenLights t38) and its
            // static sampler to s32 (after the 32 screen samplers). One more SampledImage on this set, (re)bound per
            // frame by BindScreenSources to the atlas view or the neutral 1×1 filler when none is set.
            new GpuComputeBinding(
                Binding: GlyphAtlasBindingIndex,
                Kind: GpuComputeBindingKind.SampledImage
            ),
            // The GLYPH DECAL buffer, so its SRV resolves to register t40 (after the glyph atlas t39) — the
            // material-level text tier the decal-mode screens sample (see sdf-world.hlsli's sdfDecalCells).
            new GpuComputeBinding(
                Binding: DecalCellsBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            // The brick pool (sdfBrickPool), APPENDED LAST so its SRV resolves to register t41 (after sdfDecalCells t40) —
            // Stage 1 samples baked SampledRegion carves O(1). Always present (a filler when the pool is disabled); the
            // core-ops variant shares this bindings array, so both Stage 1 pipelines bind the pool identically.
            new GpuComputeBinding(
                Binding: BrickPoolBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            // The frame-local instance grid resolves to t42, after the brick pool's t41.
            new GpuComputeBinding(
                Binding: FrameInstanceGridBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            // The bounded-volume buffer, APPENDED LAST so its SRV resolves to t43 (after the frame instance grid t42) —
            // shade-volumes.hlsli's renderView and sky-prepass call sites.
            new GpuComputeBinding(
                Binding: VolumeBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            // The beam's cull buffer, read-only in every hit pass: t44, after the bounded volumes.
            new GpuComputeBinding(
                Binding: TileBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            // The visibility records, written by primary, surface and ambient (u5, after the five source images) and
            // read by views through a read-only binding of the same buffer (t45).
            new GpuComputeBinding(
                Binding: PrimaryHitBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferReadWrite
            ),
            new GpuComputeBinding(
                Binding: PrimaryHitReadBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
        ];
        // Stage 2 (source-agnostic composite): output image (0) + the source array (1). The sky pre-pass fills every
        // source pixel, so the compositor needs no cull buffer.
        internal static readonly GpuComputeBinding[] Composite = [
            new GpuComputeBinding(
                Binding: CompositeOutputBindingIndex,
                Kind: GpuComputeBindingKind.StorageImage
            ),
            new GpuComputeBinding(
                Binding: CompositeSourceBindingIndex,
                Count: MaxViewports,
                Kind: GpuComputeBindingKind.StorageImage
            ),
        ];
        // The carve-bake baker's set: the per-slot request buffer (a float4 SRV at t0) + the shared pool WRITTEN as a UAV
        // (u0). One set per brick slot, each binding that slot's request buffer + the pool; only used when the pool is
        // enabled. Direct3D 12 assigns registers from THIS order: t0 bakeRequest, u0 brickPool.
        internal static readonly GpuComputeBinding[] BrickBake = [
            new GpuComputeBinding(
                Binding: BrickBakeRequestBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            new GpuComputeBinding(
                Binding: BrickBakePoolBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferReadWrite
            ),
        ];
        // The per-frame table uploader (sdf-frame-upload.comp): t0 a ring slot's host-visible table, u0 its device-local
        // twin. Direct3D 12 assigns registers from THIS order.
        internal static readonly GpuComputeBinding[] FrameUpload = [
            new GpuComputeBinding(
                Binding: FrameUploadSourceBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            new GpuComputeBinding(
                Binding: FrameUploadDestinationBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferReadWrite
            ),
        ];

        // A pipeline factory reads a push-constant range's size and stages; each engine records its own payload.
        private static readonly GpuPushConstantBinding PassPush = Push(length: PushConstantByteLength);
        private static readonly GpuPushConstantBinding CompositePush = Push(length: CompositePushByteLength);
        private static readonly GpuPushConstantBinding BrickPush = Push(length: BrickBakePushByteLength);
        private static readonly GpuPushConstantBinding FrameUploadPush = Push(length: FrameUploadPushByteLength);

        // Indexed by the *PipelineIndex constants. The hit passes, the sky and the three views variants share Views,
        // so their descriptor-set layouts are identically defined and one per-slot views set binds against each of them.
        // Nearest filtering end to end on those: a bound screen source (an emulator or child's native pixels) magnifies
        // as crisp cells, never bilinear smears.
        internal static readonly PipelineSpec[] Specs = [
            Spec(name: "sdf-beam", bindings: Beam, push: PassPush),
            Spec(name: "sdf-instance-cull", bindings: InstanceCull, push: PassPush),
            Spec(name: "sdf-cull-args", bindings: CullArgs, push: PassPush),
            Spec(name: "sdf-world-primary", bindings: Views, push: PassPush, filter: GpuSamplerFilter.Nearest),
            Spec(name: "sdf-world-surface", bindings: Views, push: PassPush, filter: GpuSamplerFilter.Nearest),
            Spec(name: "sdf-world-ambient", bindings: Views, push: PassPush, filter: GpuSamplerFilter.Nearest),
            Spec(name: "sdf-world-views", bindings: Views, push: PassPush, filter: GpuSamplerFilter.Nearest),
            Spec(name: "sdf-world-views-core", bindings: Views, push: PassPush, filter: GpuSamplerFilter.Nearest),
            Spec(name: "sdf-world-views-folds", bindings: Views, push: PassPush, filter: GpuSamplerFilter.Nearest),
            Spec(name: "sdf-sky", bindings: Views, push: PassPush, filter: GpuSamplerFilter.Nearest),
            Spec(name: "sdf-world-composite", bindings: Composite, push: CompositePush),
            Spec(name: "sdf-brick-bake", bindings: BrickBake, push: BrickPush, brick: true),
            Spec(name: "sdf-brick-upload", bindings: BrickBake, push: BrickPush, brick: true),
            Spec(name: "sdf-frame-upload", bindings: FrameUpload, push: FrameUploadPush),
        ];

        private static GpuPushConstantBinding Push(int length) =>
            new(
                data: new byte[length],
                offset: 0,
                stageFlags: GpuShaderStage.Compute
            );
        private static PipelineSpec Spec(string name, GpuComputeBinding[] bindings, GpuPushConstantBinding push, GpuSamplerFilter filter = GpuSamplerFilter.Linear, bool brick = false) =>
            new(
                Brick: brick,
                Description: new GpuComputePipelineDescription(
                    Bindings: bindings,
                    Name: name,
                    PushConstantBinding: push,
                    Registers: GpuRegisterNumbering.PackedByClass,
                    SamplerFilter: filter
                )
            );
    }
    /// <summary>One engine pipeline: its description (the name selects its kernel) and whether only a brick pool
    /// needs it.</summary>
    internal readonly record struct PipelineSpec(GpuComputePipelineDescription Description, bool Brick);
}
