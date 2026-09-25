using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Overlays;

/// <summary>The read seams the unified overlay consumes, each optional (an absent source simply contributes no
/// records), bundled so the constructor arity stays small.</summary>
/// <param name="Console">The console-panel source, or <see langword="null"/>.</param>
/// <param name="BindingBar">The per-seat binding-bar source, or <see langword="null"/>.</param>
/// <param name="Toast">The transient-echo source, or <see langword="null"/>.</param>
/// <param name="FeedTick">Invoked once per produced frame, before the sources are snapshotted — the host's hook to
/// freshen pull-model feeds (e.g. recomposing the per-seat binding frame). Runs on the render thread.</param>
/// <param name="Markers">The per-seat marker source (projected chips for authored <c>markers</c> rows), or
/// <see langword="null"/>.</param>
/// <param name="Hud">The authored world-scope and player-scope (per-seat) HUD structure source, or
/// <see langword="null"/>.</param>
/// <param name="HudBindings">The authored HUD's live binding resolver — required alongside <paramref name="Hud"/>
/// for either scope to draw anything (a <see langword="null"/> pairing on either side draws nothing).</param>
/// <param name="Cursor">The per-seat drawn-cursor source, or <see langword="null"/>.</param>
/// <param name="Wheel">The per-seat radial-action-menu source, or <see langword="null"/>.</param>
public sealed record UnifiedOverlaySources(
    IConsoleTapeSource? Console,
    IBindingBarSource? BindingBar,
    IOverlayToastSource? Toast,
    Action? FeedTick,
    IMarkerSource? Markers = null,
    IHudSource? Hud = null,
    IHudBindingResolver? HudBindings = null,
    ICursorSource? Cursor = null,
    IWheelSource? Wheel = null
);
/// <summary>
/// The one screen-space overlay decorator: wraps any same-device inner producer whose surface exposes a sampleable
/// image view, samples it in one fullscreen fragment pass, and draws every 2D surface on top from one storage
/// buffer — the design-token slab and the shared glyph SDF pack as a static prefix, then this frame's packed
/// records. Every surface is a writer: the console panel, the per-seat binding bars, and the toast are each a small CPU
/// writer emitting the shared record vocabulary (panel chrome / rect / fixed-cell text run / icon chip) through
/// <see cref="OverlayFrameBuilder"/>, so a future surface is a new writer, never a new node or shader.
/// Backend-neutral: only the neutral services of its device context (<see cref="IGpuDeviceContext.Services"/>), with
/// bytecode selected by the caller.
/// </summary>
/// <remarks>
/// The overlay decorator contract in full: the per-node submission fence (the previous frame's pass must
/// retire before the buffer/descriptor rewrites), the pass-through fast path (nothing visible = the inner frame
/// returns untouched, no extra pass), and <see cref="ICaptureRequestTarget"/> forwarding (a pending capture lands on
/// whichever node actually produced the shown frame). Zero steady-state allocation: one preallocated scratch, one
/// reused push-constant array, records packed with <see cref="BitConverter.SingleToUInt32Bits"/>.
/// </remarks>
public sealed class UnifiedOverlayNode : IRenderNode, ICaptureRequestTarget {
    // The CPU half: every writer, the builder and the frame-slot table.
    private readonly OverlayFrameComposer m_composer;
    private readonly IGpuRecorder m_commandRecorder;
    private readonly IGpuCommandPoolFactory m_commandPoolFactory;
    private readonly NodeDescriptor m_descriptor;
    private readonly IGpuBindings m_bindings;
    private readonly IGpuDeviceContext m_deviceContext;
    private readonly GpuCreationFaults? m_faults;
    private readonly ReadOnlyMemory<byte> m_fragmentBytecode;
    private readonly uint m_height;
    private readonly IGpuImageFactory m_imageFactory;
    private readonly IRenderNode m_inner;
    private readonly IGpuPipelineFactory m_pipelineFactory;
    private readonly IGpuQueueSubmitter m_queueSubmitter;
    private readonly IGpuRenderPassFactory m_renderPassFactory;
    private readonly IGpuShaderModuleFactory m_shaderModuleFactory;
    private readonly IGpuBufferFactory m_storageBufferFactory;
    private readonly IGpuSurfaceTransferFactory m_surfaceTransferFactory;
    private readonly IGpuBufferFactory m_geometryBufferFactory;
    private readonly ReadOnlyMemory<byte> m_vertexBytecode;
    private readonly uint m_width;

    // Every counted service above counts into this ledger. One frame is in flight: the frame fence is waited before
    // the next frame's rewrites.
    private readonly GpuWorkLedger m_work = new(
        framesInFlight: 1,
        name: "gpu.overlay"
    );

    // Whether the standing ResourceRefusal is the descriptor heap's, and the heap's release revision and the operator's
    // faults' revision when it was made.
    private bool m_heapRefused;
    private long m_refusedFaultsRevision;
    private long m_refusedHeapRevision;
    private IGpuCommandPool? m_commandPool;
    private IGpuStorageBuffer? m_dataBuffer;
    private nint m_descriptorPool;
    private nint m_descriptorSet;
    private bool m_disposed;
    private IGpuShaderModule? m_fragmentShader;
    private IGpuSubmissionFence? m_frameFence;
    private IGpuFramebuffer? m_framebuffer;
    private nint m_lastImageViewHandle;
    private IGpuPipeline? m_pipeline;
    private IGpuSurfaceReadback? m_readback;
    private IGpuRenderPass? m_renderPass;
    private IGpuImage? m_renderTarget;
    private bool m_resourcesReady;
    private nint m_sampler;
    private IGpuBuffer? m_vertexBuffer;
    private IGpuShaderModule? m_vertexShader;

    // The per-frame submission fence (frame-ring discipline): this node's single command buffer / host-visible data
    // buffer / descriptor set may only be rewritten once its PREVIOUS submission retired. This pass is queued ahead
    // of the frame's heavy world submit, so by the next frame it has long retired and the wait is ~free.
    private readonly CaptureRequestSlot m_capture = new();
    private readonly CapturePngWriter m_capturePng = new();

    // Converted once: a method group passed per frame would allocate a delegate on every drawn frame.
    private readonly Action<string> m_writeCapture;

    private static readonly byte[] FullscreenTriangleVertexData = FullscreenTriangle.CreateVertexData();
    private static readonly string[] OverlayPassLabels = ["overlay"];
    // Rewritten in place each frame (the draw command holds one binding over this array for the node's lifetime).
    private readonly byte[] m_pushConstantData = new byte[OverlayPassLayout.PushConstantBytes];

    /// <summary>Initializes a new instance of the <see cref="UnifiedOverlayNode"/> class.</summary>
    /// <param name="inner">The producer whose render the overlay is drawn over (its surface must be sampleable here).</param>
    /// <param name="sources">The per-surface read seams + the feed tick.</param>
    /// <param name="capacity">The host's declared counts the lease table is derived from (see
    /// <see cref="OverlayCapacity"/>).</param>
    /// <param name="glyphs">The shared SDF glyph pack (per-glyph signed-distance cells).</param>
    /// <param name="deviceContext">The device the overlay renders on, the one <paramref name="inner"/> renders on; the
    /// overlay records through its services.</param>
    /// <param name="frameSources">The host's <see cref="OverlayHudElementKind.Frame"/> content seam: every produced
    /// frame's <see cref="OverlayFrameSlots"/> table acquires each visible <c>Frame</c> element's lease through it. A host
    /// with no live frame content passes a null object that always answers <see langword="false"/>.</param>
    /// <param name="vertexBytecode">The fullscreen vertex shader, in the host backend's bytecode format.</param>
    /// <param name="fragmentBytecode">The unified overlay fragment shader, in the host backend's bytecode format.</param>
    /// <param name="width">The render width in pixels.</param>
    /// <param name="height">The render height in pixels.</param>
    /// <param name="theme">The boot-resolved theme (see <see cref="UpdateTheme"/> for live retheme).</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> derives a lease table that
    /// over-subscribes an <see cref="OverlayFrameBuilder"/> backstop (see <see cref="OverlayChannelLeases"/>).</exception>
    public UnifiedOverlayNode(
        IRenderNode inner,
        UnifiedOverlaySources sources,
        OverlayCapacity capacity,
        OverlayGlyphSdfPack glyphs,
        IGpuDeviceContext deviceContext,
        IOverlayFrameSources frameSources,
        ReadOnlyMemory<byte> vertexBytecode,
        ReadOnlyMemory<byte> fragmentBytecode,
        uint width,
        uint height,
        OverlayThemeValues theme = default
    ) {
        ArgumentNullException.ThrowIfNull(argument: glyphs);
        ArgumentNullException.ThrowIfNull(argument: inner);
        ArgumentNullException.ThrowIfNull(argument: deviceContext);
        ArgumentNullException.ThrowIfNull(argument: frameSources);
        ArgumentNullException.ThrowIfNull(argument: sources);

        var services = deviceContext.Services;

        m_composer = new OverlayFrameComposer(
            capacity: capacity,
            frameSources: frameSources,
            glyphs: glyphs,
            height: height,
            sources: sources,
            theme: in theme,
            width: width
        );
        m_commandRecorder = GpuWorkCounting.Wrap(
            ledger: m_work,
            recorder: services.Recorder
        );
        m_commandPoolFactory = services.CommandPoolFactory;
        m_descriptor = new NodeDescriptor(
            Name: "unified-overlay",
            SurfaceId: SurfaceId.New()
        );
        m_bindings = GpuWorkCounting.Wrap(
            bindings: services.Bindings,
            ledger: m_work
        );
        m_deviceContext = deviceContext;
        m_faults = services.Faults;
        m_fragmentBytecode = fragmentBytecode;
        m_height = height;
        m_imageFactory = services.ImageFactory;
        m_inner = inner;
        m_pipelineFactory = GpuWorkCounting.Wrap(
            factory: services.PipelineFactory,
            ledger: m_work
        );
        m_queueSubmitter = GpuWorkCounting.Wrap(
            ledger: m_work,
            submitter: services.QueueSubmitter
        );
        m_renderPassFactory = services.RenderPassFactory;
        m_shaderModuleFactory = GpuWorkCounting.Wrap(
            factory: services.ShaderModuleFactory,
            ledger: m_work
        );
        m_storageBufferFactory = GpuWorkCounting.Wrap(
            factory: services.BufferFactory,
            ledger: m_work
        );
        m_surfaceTransferFactory = services.SurfaceTransferFactory;
        m_geometryBufferFactory = services.BufferFactory;
        m_vertexBytecode = vertexBytecode;
        m_width = width;
        m_writeCapture = WriteCapture;
        m_work.Configure(
            passLabels: OverlayPassLabels,
            revision: 1L
        );

    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;

    /// <summary>Gets the one descriptor pool the overlay creates, the statement its construction creates the pool from
    /// and a device's heap admits it by: one set of the inner world image's and every frame slot's combined image
    /// samplers and the program storage buffer.</summary>
    public static GpuDescriptorPoolSizes DescriptorPoolSizes { get; } = new(
        CombinedImageSamplerCount: OverlayPassLayout.TextureSamplerCount,
        MaxSets: 1,
        StorageBufferCount: 1,
        StorageImageCount: 0
    );

    /// <summary>Gets the GPU work this node recorded for its newest completed overlay submission: the one
    /// <c>overlay</c> pass (the render pass and its draw), with the frame's uploads and descriptor writes outside it.
    /// Unavailable after a device loss until a rebuilt frame completes.</summary>
    public IGpuWorkSource Work => m_work;
    /// <summary>Gets the GPU objects this node has created, over its whole life.</summary>
    public IWorkCounterSource WorkLifetime => m_work;
    /// <inheritdoc/>
    public string? PendingCapturePath => (m_capture.PendingPath ?? (m_inner as ICaptureRequestTarget)?.PendingCapturePath);
    /// <summary>Gets why the overlay's GPU resources were refused, or <see langword="null"/> when they stand or have not
    /// been tried. While refused, a produced frame presents the inner frame unchanged and forwards any capture to it; the
    /// refusal holds until <see cref="OnDeviceLost"/>, after which the next frame with overlay content creates them
    /// again, or, for a refusal by the device's descriptor heap, until another owner returns heap space
    /// (<see cref="IGpuBindings.HeapReleaseRevision"/>). The operator's GPU faults are an input too: arming or clearing one
    /// (<see cref="GpuCreationFaults.Revision"/>) tries the creation once more.</summary>
    public string? ResourceRefusal { get; private set; }

    // Reads back this node's own render target (the overlay composited over the world — what the player actually
    // sees) and writes it as a PNG: a new, separately-fenced submit sequenced after the draw above on the same queue.
    private void CaptureIfPending() =>
        m_capture.Serve(
            failureLabel: "[capture] failed",
            writer: m_writeCapture
        );
    // Creates the node's resources once, or refuses them. A creation that throws partway releases what was created
    // before it (the one release, ReleaseGpuResources, clears each field it frees) and is refused rather than thrown: the
    // refusal is named once on the error stream and by ResourceRefusal, and holds until a device loss, the one event
    // that changes what the creation depends on (the device, the extent and the shaders are fixed at construction), so
    // a produced frame never retries it. A refusal by the device's descriptor heap (GpuDescriptorHeapRefusalException)
    // depends on heap space too, so it is tried again once the heap's release revision moves, as another owner returns
    // its pools. The operator's GPU faults are an input of every creation: when their revision moves (an arm, a disarm,
    // or a fault firing elsewhere) the creation is tried once more. A device loss is never refused: it reaches the
    // host's recovery.
    private bool EnsureResources() {
        if (m_resourcesReady) {
            return true;
        }

        var heapRevision = m_bindings.HeapReleaseRevision;

        if (ResourceRefusal is not null) {
            if (
                ((m_faults?.Revision ?? 0L) == m_refusedFaultsRevision) &&
                (
                    !m_heapRefused ||
                    (heapRevision == m_refusedHeapRevision)
                )
            ) {
                return false;
            }

            ResourceRefusal = null;
        }

        try {
            CreateResources();

            return true;
        } catch (Exception refusal) when ((refusal is not DeviceLostException)) {
            ReleaseGpuResources();
            m_heapRefused = (refusal is GpuDescriptorHeapRefusalException);
            // Read after the attempt: a creation fault that fired inside it moved the revision, which is no change a
            // retry could succeed on.
            m_refusedFaultsRevision = (m_faults?.Revision ?? 0L);
            m_refusedHeapRevision = heapRevision;
            ResourceRefusal = refusal.Message;
            Console.Error.WriteLine(value: $"[overlay] resources refused, presenting the inner frame until the device is recreated{(m_heapRefused
                ? " or the descriptor heap returns space"
                : string.Empty)}: {refusal.Message}");

            return false;
        } catch {
            ReleaseGpuResources();

            throw;
        }
    }
    private void CreateResources() {
        // The pool is admitted before anything is created, so an overlay that does not fit the device's heap is refused
        // by name (GPU_DESCRIPTOR_HEAP) with nothing to release, like any other refused creation.
        if (!m_bindings.CanAdmit(
            owner: "unified overlay",
            pools: [DescriptorPoolSizes],
            refusal: out var refusal
        )) {
            throw new GpuDescriptorHeapRefusalException(message: refusal);
        }

        // The overlay clears its image each frame and leaves it shader-readable for the presenter and any capture.
        m_renderTarget = m_imageFactory.Create(
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: m_height,
            usage: GpuImageUsage.ColorAttachment | GpuImageUsage.Sampled,
            width: m_width
        );
        m_renderPass = m_renderPassFactory.Create(
            description: new GpuRenderPassDescription(Colors: [new GpuColorAttachment(
                FinalLayout: GpuImageLayout.ShaderReadOnly,
                Format: GpuPixelFormat.R8G8B8A8Unorm,
                Load: GpuAttachmentLoad.Clear,
                Store: GpuAttachmentStore.Store
            )])
        );
        m_framebuffer = m_renderPassFactory.CreateFramebuffer(
            colors: [m_renderTarget],
            depth: null,
            renderPass: m_renderPass
        );
        m_commandPool = m_commandPoolFactory.Create();
        m_frameFence = m_queueSubmitter.CreateSubmissionFence();
        m_vertexShader = m_shaderModuleFactory.Create(
            bytecode: m_vertexBytecode,
            stage: GpuShaderStage.Vertex
        );
        m_fragmentShader = m_shaderModuleFactory.Create(
            bytecode: m_fragmentBytecode,
            stage: GpuShaderStage.Fragment
        );
        m_vertexBuffer = m_geometryBufferFactory.CreateHostVisible(
            data: FullscreenTriangleVertexData,
            usage: GpuBufferUsage.Vertex
        );
        m_dataBuffer = m_storageBufferFactory.CreateHostVisible(
            sizeBytes: (((uint)m_composer.Builder.WordCount) * sizeof(uint)),
            usage: GpuBufferUsage.Storage
        );
        m_pipeline = m_pipelineFactory.Create(
            description: OverlayPassLayout.PipelineDescription(),
            fragmentShaderModule: m_fragmentShader,
            renderPass: m_renderPass,
            vertexShaderModule: m_vertexShader
        );

        m_descriptorPool = m_bindings.CreatePool(sizes: DescriptorPoolSizes);
        m_descriptorSet = m_bindings.AllocateSet(
            descriptorSetLayoutHandle: m_pipeline.DescriptorSetLayoutHandle,
            poolHandle: m_descriptorPool
        );
        m_sampler = m_bindings.CreateSampler();
        m_bindings.WriteBuffer(
            binding: OverlayPassLayout.StorageBufferBinding,
            bufferHandle: m_dataBuffer.BufferHandle,
            bufferSize: (((uint)m_composer.Builder.WordCount) * sizeof(uint)),
            descriptorSetHandle: m_descriptorSet,
            elementStride: OverlayPassLayout.StorageElementStrideBytes,
            kind: GpuBindingKind.ReadOnlyBuffer
        );
        // The token slab + glyph atlas are static — upload them ONCE now (the front PanelBaseWords uints); each
        // produced frame rewrites only the dynamic slice after them. A device-loss rebuild re-seeds them here.
        m_dataBuffer.Write<uint>(data: m_composer.Builder.Scratch[..m_composer.Builder.PanelBaseWords]);
        m_resourcesReady = true;
    }
    // Not drawing this frame: hand a pending capture down the chain (the shared decorator forwarding contract) so
    // the readback lands on whatever actually produced the shown frame. Keeping it armed when the inner cannot serve
    // it is what stops a request from vanishing silently — the request remains armed until a node serves it or disposal fails it, and a later frame this node does draw serves it here instead.
    private void ForwardPendingCapture() => m_capture.Forward(target: (m_inner as ICaptureRequestTarget));
    // Records the node's single fullscreen pass into its command buffer. Returns the recorded command buffer handle,
    // ready to submit.
    private nint RecordOverlayPass() {
        var commandBufferHandle = m_commandPool!.CommandBufferHandle;

        m_commandRecorder.BeginCommandBuffer(
            commandBufferHandle: commandBufferHandle
        );

        // The counted pass spans the debug group, the render pass, and its draw.
        m_work.EnterPass(pass: 0);
        m_commandRecorder.BeginDebugGroup(
            commandBufferHandle: commandBufferHandle,
            label: "unified-overlay"
        );
        m_commandRecorder.BeginRenderPass(
            commandBufferHandle: commandBufferHandle,
            framebuffer: m_framebuffer!
        );

        m_commandRecorder.BindPipeline(
            bindPoint: GpuBindPoint.Graphics,
            commandBufferHandle: commandBufferHandle,
            pipelineHandle: m_pipeline!.Handle
        );
        m_commandRecorder.BindVertexBuffer(
            bufferHandle: m_vertexBuffer!.BufferHandle,
            commandBufferHandle: commandBufferHandle,
            sizeBytes: m_vertexBuffer.SizeBytes,
            strideBytes: FullscreenTriangle.StrideBytes
        );
        m_commandRecorder.PushConstants(
            bindPoint: GpuBindPoint.Graphics,
            commandBufferHandle: commandBufferHandle,
            data: m_pushConstantData,
            offset: 0,
            pipelineLayoutHandle: m_pipeline.LayoutHandle,
            stageFlags: GpuShaderStage.Fragment
        );
        m_commandRecorder.BindDescriptorSet(
            bindPoint: GpuBindPoint.Graphics,
            commandBufferHandle: commandBufferHandle,
            descriptorSetHandle: m_descriptorSet,
            group: 0,
            pipelineLayoutHandle: m_pipeline.LayoutHandle
        );
        m_commandRecorder.Draw(
            commandBufferHandle: commandBufferHandle,
            parameters: new GpuDrawParameters(
                vertexCount: FullscreenTriangle.VertexCount,
                instanceCount: 1
            )
        );
        m_commandRecorder.EndRenderPass(
            commandBufferHandle: commandBufferHandle
        );
        m_commandRecorder.EndDebugGroup(
            commandBufferHandle: commandBufferHandle
        );
        m_work.LeavePass();

        m_commandRecorder.EndCommandBuffer(
            commandBufferHandle: commandBufferHandle
        );

        return commandBufferHandle;
    }
    // Releases only what this node acquired: the device is reached solely to destroy a native handle the node holds,
    // so a node that never produced a frame touches no device context — including one whose device never came up,
    // where reaching it would throw or retry the failed bring-up.
    private void ReleaseGpuResources() {
        if (0 != m_sampler) {
            m_bindings.DestroySampler(
                samplerHandle: m_sampler
            );
            m_sampler = 0;
        }

        m_bindings.DestroyPool(
            poolHandle: m_descriptorPool
        );
        m_descriptorPool = 0;
        m_descriptorSet = 0;

        m_pipeline?.Dispose();
        m_pipeline = null;
        m_frameFence?.Dispose();
        m_frameFence = null;
        m_readback?.Dispose();
        m_readback = null;
        m_dataBuffer?.Dispose();
        m_dataBuffer = null;
        m_vertexBuffer?.Dispose();
        m_vertexBuffer = null;
        m_fragmentShader?.Dispose();
        m_fragmentShader = null;
        m_vertexShader?.Dispose();
        m_vertexShader = null;
        m_commandPool?.Dispose();
        m_commandPool = null;
        m_framebuffer?.Dispose();
        m_framebuffer = null;
        m_renderPass?.Dispose();
        m_renderPass = null;
        m_renderTarget?.Dispose();
        m_renderTarget = null;
        m_lastImageViewHandle = 0;
        m_resourcesReady = false;
        m_work.Invalidate();
    }
    // Routes every early-exit retirement through OverlayFrameRetirementPolicy's table so this method and the two
    // early returns in ProduceFrame below cannot drift out of step on which exit retires immediately.
    private void RetireForExit(OverlayFrameExit exit) {
        if (OverlayFrameRetirementPolicy.RetiresImmediately(exit: exit)) {
            m_composer.FrameSlots.RetireAll();
        } else {
            m_composer.FrameSlots.RetireAllAfter(fence: m_frameFence);
        }
    }
    private void WriteCapture(string path) {
        m_capturePng.ThrowIfUnavailable(path: path);

        m_readback ??= m_surfaceTransferFactory.CreateReadback();

        var pixels = m_readback.Read(
            bytesPerPixel: 4,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: m_height,
            sourceImageHandle: m_renderTarget!.ImageHandle,
            sourceLayout: GpuImageLayout.ShaderReadOnly,
            width: m_width
        );

        if (!m_capturePng.TryWrite(
            height: ((int)m_height),
            path: path,
            rgba: pixels,
            width: ((int)m_width)
        )) {
            throw new NotSupportedException(message: "PNG capture is unavailable.");
        }

        Console.Error.WriteLine(value: $"[capture] unified overlay -> {path}");
    }
    // Rewrites every one of the OverlayFrameSlots.SlotCount frame-slot descriptors, unconditionally, every produced
    // frame that draws: a bound slot's descriptor points at its lease's image view (bound content changes far more
    // often than the world image's own identity, so — unlike SamplerBinding above — this is never cached against a
    // last-written handle); an unbound slot's points at fallbackImageViewHandle (the inner world image), so every
    // binding the shader's slot-selecting switch can reach is always a valid, sampleable image.
    private void WriteFrameSlotDescriptors(nint fallbackImageViewHandle) {
        var boundCount = m_composer.FrameSlots.BoundCount;

        for (var slot = 0; (slot < OverlayFrameSlots.SlotCount); slot++) {
            var imageViewHandle = ((slot < boundCount)
                ? m_composer.FrameSlots.LeaseAt(slot: slot).ImageViewHandle
                : fallbackImageViewHandle
            );

            m_bindings.WriteCombinedImageSampler(
                arrayElement: 0,
                binding: (OverlayPassLayout.FrameSlotFirstBinding + ((uint)slot)),
                descriptorSetHandle: m_descriptorSet,
                imageViewHandle: imageViewHandle,
                samplerHandle: m_sampler
            );
        }
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_capture.Refuse(error: new ObjectDisposedException(objectName: GetType().Name));
        // A final wait proves no in-flight pass can still be sampling a held lease, so every one of them — bound
        // this frame or still pending retirement from the last — can retire safely.
        try {
            m_composer.FrameSlots.RetireAllAfter(fence: m_frameFence);
            ReleaseGpuResources();
        } finally {
            m_inner.Dispose();
        }
    }
    /// <inheritdoc/>
    public void OnDeviceLost() {
        // Device loss invalidates the submissions that could sample these host-owned images. Retire before dropping
        // the fence so capture/view producers do not retain acquisitions forever; waiting on a lost-device fence is
        // neither necessary nor safe.
        RetireForExit(exit: OverlayFrameExit.DeviceLost);
        ReleaseGpuResources();
        // The recreated device is the change a refused creation waits for.
        ResourceRefusal = null;
        m_capture.RefuseForDeviceLoss();
        m_inner.OnDeviceLost();
    }
    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (m_disposed) {
            return default;
        }

        // Publishes the last overlay submission once its fence has signaled, on frames that draw no overlay too.
        m_work.Poll();

        // The inner producer's same-device output is already transitioned shader-readable for the fragment stage
        // before its submit, so this same-queue pass samples it with no CPU wait.
        var inner = m_inner.ProduceFrame(context: context);

        if (
            inner.IsEmpty ||
            (0 == inner.ImageViewHandle)
        ) {
            ForwardPendingCapture();
            // No new overlay submit will provide the usual proving wait/retirement point. Drain the last submitted
            // pass before releasing its host-owned image handles.
            RetireForExit(exit: OverlayFrameExit.NoInnerFrame);

            return inner;
        }

        // Nothing visible passes the frame through untouched, with no extra pass.
        if (!m_composer.Compose(renderTicks: context.RenderTicks)) {
            ForwardPendingCapture();
            // BeginFrame moved the previous pass's leases aside, and a writer may also have acquired a lease before
            // declining to emit. With no overlay submit, retire both sets after the prior pass's fence.
            RetireForExit(exit: OverlayFrameExit.NoOverlayContent);

            return inner;
        }

        if (!EnsureResources()) {
            // Refused resources present the inner frame unchanged, and a capture reaches it the same way.
            ForwardPendingCapture();
            RetireForExit(exit: OverlayFrameExit.ResourcesRefused);

            return inner;
        }

        // The previous frame's pass must have retired before the descriptor/buffer/command-buffer rewrites below —
        // which is also what proves the leases OverlayFrameSlots.BeginFrame moved aside above safe to retire.
        m_frameFence!.Wait();
        m_composer.FrameSlots.RetirePending();

        if (inner.ImageViewHandle != m_lastImageViewHandle) {
            m_bindings.WriteCombinedImageSampler(
                arrayElement: 0,
                binding: OverlayPassLayout.SamplerBinding,
                descriptorSetHandle: m_descriptorSet,
                imageViewHandle: inner.ImageViewHandle,
                samplerHandle: m_sampler
            );

            m_lastImageViewHandle = inner.ImageViewHandle;
        }

        WriteFrameSlotDescriptors(fallbackImageViewHandle: inner.ImageViewHandle);
        m_composer.WritePushConstants(
            block: m_pushConstantData,
            shiftWords: 0
        );
        m_composer.UploadFrameRegions(
            buffer: m_dataBuffer!,
            shiftWords: 0
        );

        var commandBufferHandle = RecordOverlayPass();

        Span<nint> commandBuffers = [commandBufferHandle];

        m_queueSubmitter.Submit(
            commandBufferHandles: commandBuffers,
            fence: m_frameFence!
        );

        CaptureIfPending();

        return Surface.SameDeviceImage(
            imageHandle: m_renderTarget!.ImageHandle,
            imageViewHandle: m_renderTarget!.ImageViewHandle,
            width: m_width,
            height: m_height,
            format: SurfaceFormat.R8G8B8A8Unorm
        );
    }
    /// <inheritdoc/>
    public void RequestCapture(FrameCaptureRequest request) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        m_capture.Arm(
            pendingPath: PendingCapturePath,
            request: request
        );
    }
    /// <summary>Republishes the live theme every CPU writer reads (<see cref="OverlayThemeStore"/>) and re-fills
    /// the GPU token slab from it — the composition root's live-retheme call, at whatever cadence it resolves the
    /// document's authored <c>theme</c> section against live state (matching the sky's own per-revision cadence).
    /// A no-op on the CPU-writer side takes effect on the very next produced frame; the GPU token slab re-uploads
    /// immediately when resources are already standing (a call before the first produced frame just updates the
    /// value <see cref="EnsureResources"/> will upload on its own first pass).</summary>
    /// <param name="theme">The newly resolved theme.</param>
    public void UpdateTheme(in OverlayThemeValues theme) {
        m_composer.UpdateTheme(theme: in theme);

        if (m_resourcesReady) {
            m_dataBuffer!.Write<uint>(data: m_composer.Builder.Scratch[..OverlayTokenBlock.WordCount]);
        }
    }
}
