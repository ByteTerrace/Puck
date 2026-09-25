using System.Runtime.InteropServices;
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
    // The FOUR first-party writers' draw-order table size — Console..Toast (OverlayChannel 0..3). OverlayChannel.Hud
    // (4) is DELIBERATELY excluded from this table: it is not a single fixed-position writer but the banded
    // pipeline's under/base/over sequence PLUS the unbanded player-scope seat-panel pass (see ProduceFrame), opened
    // as its own channel scope up to four times a frame rather than once through this table.
    // OverlayChannel.Cursor (5) and OverlayChannel.Wheel (6) are excluded too: they are the frame's LAST two
    // channel scopes (wheel, then cursor on top), drawn over everything and outside the replace-band suppression
    // (see ProduceFrame's tail).
    private const int FirstPartyChannelCount = 4;
    // Combined image-sampler binding layout (identical numbering on both backends — see StorageBufferBinding for the
    // storage buffer's matching binding): 0 the inner world image (SamplerBinding); 1..OverlayFrameSlots.SlotCount
    // the frame-slot table (FrameSlotFirstBinding..), one scalar Texture2D+SamplerState pair per binding (DXC's
    // vk::combinedImageSampler never fuses an array — see overlay-unified.frag.hlsl's frameTextureN/frameSamplerN
    // declarations); OverlayFrameSlots.SlotCount+1 the storage buffer, immediately after every sampler. All 1+SlotCount
    // samplers share ONE sampler configuration (m_sampler) — a bound slot's descriptor gets its lease's image view, an
    // unbound slot's gets the inner world image (see ProduceFrame's WriteFrameSlotDescriptors call), so every binding
    // the shader can reach through its slot-selecting switch is always valid.
    private const uint FrameSlotFirstBinding = (SamplerBinding + 1);
    // The glyph outline halo width, in encoded signed-distance units — the SDF contrast band that keeps overlay text
    // legible over any world content, kept clear of the atlas' saturation floor at the overlay's screenPxRange.
    private const float OutlineBand = 0.20f;
    // counts float4 + sdf float4 + misc float4 — KEEP IN SYNC with overlay-unified.frag.hlsl's OverlayPassData.
    private const int PushConstantByteLength = ((sizeof(float) * 4) * 3);
    private const uint SamplerBinding = 0;
    // The program storage buffer's binding, immediately after every sampler on both backends: Vulkan declares the
    // TextureSamplerCount scalar combined-image-sampler bindings followed by the storage buffer at the next binding
    // number (VulkanGraphicsPipelineFactory.BuildDescriptorBindings); the Direct3D 12 graphics root signature packs its
    // descriptor table [t0..tN-1 texture SRVs, then the storage SRV at tN] with an identity binding-to-slot map
    // (DirectXGpuPipelineFactory.BuildLayout).
    private const uint StorageBufferBinding = TextureSamplerCount;
    // 1 (SamplerBinding) + the frame-slot table — see FrameSlotFirstBinding's remarks.
    private const uint TextureSamplerCount = (1u + OverlayFrameSlots.SlotCount);
    private const uint VertexCount = 3;
    private const uint VertexStrideBytes = (sizeof(float) * 2);

    private readonly BindingBarWriter? m_bindingBarWriter;
    private readonly OverlayFrameBuilder m_builder;
    // THE DRAW-ORDER TABLE for the four FIRST-PARTY writers: indexed by (int)OverlayChannel, built once in the
    // constructor. ProduceFrame walks 0..FirstPartyChannelCount-1 and dispatches through this table — the enum's
    // declared order IS the draw order mechanically, never a hand-ordered if-chain a future reorder could silently
    // diverge from. A null entry is a source this instance simply has none of. Toast's extra renderTicks argument
    // rides m_currentFrameRenderTicks (set once per ProduceFrame) rather than widening this delegate's shape for one
    // caller. OverlayChannel.Hud is NOT in this table — see FirstPartyChannelCount's remarks.
    private readonly Action<OverlayFrameBuilder>?[] m_channelWriters;
    private readonly IGpuRecorder m_commandRecorder;
    private readonly IGpuCommandPoolFactory m_commandPoolFactory;
    private readonly ConsolePanelWriter? m_consoleWriter;
    private readonly CursorWriter? m_cursorWriter;
    private readonly NodeDescriptor m_descriptor;
    private readonly IGpuBindings m_bindings;
    private readonly IGpuDeviceContext m_deviceContext;
    private readonly ReadOnlyMemory<byte> m_fragmentBytecode;
    // The node-owned per-frame frame-slot table (see FrameSlotFirstBinding's remarks) — always constructed, even
    // when m_hudWriter is null, so BeginFrame/RetirePending/WriteFrameSlotDescriptors stay unconditional every
    // ProduceFrame call; with no HudWriter to call Bind, it simply never binds anything.
    private readonly OverlayFrameSlots m_frameSlots;
    private readonly uint m_height;
    // The authored world-scope HUD's banded writer, or null when the host wired no Hud/HudBindings source pair (see
    // UnifiedOverlaySources' remarks) — draws nothing rather than throwing.
    private readonly HudWriter? m_hudWriter;
    private readonly IGpuImageFactory m_imageFactory;
    private readonly IRenderNode m_inner;
    private readonly MarkerWriter? m_markerWriter;
    private readonly IGpuPipelineFactory m_pipelineFactory;
    private readonly IGpuQueueSubmitter m_queueSubmitter;
    private readonly IGpuRenderPassFactory m_renderPassFactory;
    private readonly IGpuShaderModuleFactory m_shaderModuleFactory;
    private readonly UnifiedOverlaySources m_sources;
    private readonly IGpuBufferFactory m_storageBufferFactory;
    private readonly IGpuSurfaceTransferFactory m_surfaceTransferFactory;
    private readonly OverlayThemeStore m_theme;
    private readonly ToastWriter? m_toastWriter;
    private readonly IGpuBufferFactory m_geometryBufferFactory;
    private readonly ReadOnlyMemory<byte> m_vertexBytecode;
    private readonly WheelWriter? m_wheelWriter;
    private readonly uint m_width;

    // Every counted service above counts into this ledger. One frame is in flight: the frame fence is waited before
    // the next frame's rewrites.
    private readonly GpuWorkLedger m_work = new(
        framesInFlight: 1,
        name: "gpu.overlay"
    );

    // This frame's continuous content clock, latched once per ProduceFrame — the Toast writer's channel-writer
    // delegate reads it (Emit needs renderTicks; the other writers don't) so the draw-order table's delegate shape
    // stays the same one param for every channel.
    private ulong m_currentFrameRenderTicks;
    private IGpuCommandPool? m_commandPool;
    private IGpuStorageBuffer? m_dataBuffer;
    private nint m_descriptorPool;
    private nint m_descriptorSet;
    private bool m_disposed;
    private IGpuShaderModule? m_fragmentShader;
    private IGpuSubmissionFence? m_frameFence;
    private IGpuFramebuffer? m_framebuffer;
    // The fixed frame-slot table's independent overflow episode. This can span world- and seat-scope HUD documents,
    // so each document's authoring ceiling cannot by itself prove the composed frame fits.
    private bool m_frameSlotOverflowEpisodeOpen;
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
    private readonly byte[] m_pushConstantData = new byte[PushConstantByteLength];
    // Per-channel RESERVATION-overflow episode latches: set when a channel starts losing records at its own
    // reservation, cleared the frame it renders clean again, so each EPISODE narrates exactly once.
    private readonly bool[] m_overflowEpisodeOpen = new bool[OverlayChannelLeases.Count];
    // Per-channel OWN-CAP-refusal episode latches — the parallel, independent latch for NoteRefused/maxChars
    // truncation narration (see OverlayFrameBuilder.Refused): a channel can open/close this episode with no
    // reservation overflow ever happening, so it cannot share state with m_overflowEpisodeOpen.
    private readonly bool[] m_refusalEpisodeOpen = new bool[OverlayChannelLeases.Count];

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

        m_theme = new OverlayThemeStore();
        m_theme.Publish(theme: in theme);
        m_builder = new OverlayFrameBuilder(
            glyphs: glyphs,
            height: height,
            leases: new OverlayChannelLeases(capacity: capacity),
            theme: in theme,
            width: width
        );
        m_bindingBarWriter = ((sources.BindingBar is { } bindingBar)
            ? new BindingBarWriter(
                source: bindingBar,
                theme: m_theme
            )
            : null
        );
        m_commandRecorder = GpuWorkCounting.Wrap(
            ledger: m_work,
            recorder: services.Recorder
        );
        m_consoleWriter = ((sources.Console is { } console)
            ? new ConsolePanelWriter(
                source: console,
                theme: m_theme
            )
            : null
        );
        m_cursorWriter = ((sources.Cursor is { } cursor)
            ? new CursorWriter(
                source: cursor,
                theme: m_theme
            )
            : null
        );
        m_wheelWriter = ((sources.Wheel is { } wheel)
            ? new WheelWriter(
                source: wheel,
                theme: m_theme
            )
            : null
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
        m_frameSlots = new OverlayFrameSlots(sources: frameSources);
        m_markerWriter = ((sources.Markers is { } markers)
            ? new MarkerWriter(
                maxChipsPerSeat: capacity.MarkerMaxChipsPerSeat,
                source: markers
            )
            : null
        );
        m_hudWriter = (((sources.Hud is { } hudSource) && (sources.HudBindings is { } hudBindings))
            ? new HudWriter(
                bindings: hudBindings,
                frameSlots: m_frameSlots,
                source: hudSource,
                theme: m_theme
            )
            : null
        );
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
        m_sources = sources;
        m_storageBufferFactory = GpuWorkCounting.Wrap(
            factory: services.BufferFactory,
            ledger: m_work
        );
        m_surfaceTransferFactory = services.SurfaceTransferFactory;
        m_toastWriter = ((sources.Toast is { } toast)
            ? new ToastWriter(
                source: toast,
                theme: m_theme
            )
            : null
        );
        m_geometryBufferFactory = services.BufferFactory;
        m_vertexBytecode = vertexBytecode;
        m_width = width;
        m_writeCapture = WriteCapture;
        m_work.Configure(
            passLabels: OverlayPassLabels,
            revision: 1L
        );

        // Built ONCE, after every writer field above is assigned: OverlayChannel's declared values are the array
        // index, so the enum order IS the draw order — see ProduceFrame's dispatch loop. Sized to the four
        // FIRST-PARTY channels only (FirstPartyChannelCount) — OverlayChannel.Hud is drawn through m_hudWriter's
        // own under/base/over calls, never through this table.
        m_channelWriters = new Action<OverlayFrameBuilder>?[FirstPartyChannelCount];
        m_channelWriters[((int)OverlayChannel.Console)] = ((m_consoleWriter is { } consoleForTable)
            ? (builder => consoleForTable.Emit(builder: builder))
            : null
        );
        m_channelWriters[((int)OverlayChannel.BindingBar)] = ((m_bindingBarWriter is { } bindingBarForTable)
            ? (builder => bindingBarForTable.Emit(builder: builder))
            : null
        );
        m_channelWriters[((int)OverlayChannel.Markers)] = ((m_markerWriter is { } markersForTable)
            ? (builder => markersForTable.Emit(builder: builder))
            : null
        );
        m_channelWriters[((int)OverlayChannel.Toast)] = ((m_toastWriter is { } toastForTable)
            ? (builder => toastForTable.Emit(
                builder: builder,
                renderTicks: m_currentFrameRenderTicks
            ))
            : null
        );
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;
    /// <summary>Gets the GPU work this node recorded for its newest completed overlay submission: the one
    /// <c>overlay</c> pass (the render pass and its draw), with the frame's uploads and descriptor writes outside it.
    /// Unavailable after a device loss until a rebuilt frame completes.</summary>
    public IGpuWorkSource Work => m_work;
    /// <summary>Gets the GPU objects this node has created, over its whole life.</summary>
    public IWorkCounterSource WorkLifetime => m_work;
    /// <inheritdoc/>
    public string? PendingCapturePath => (m_capture.PendingPath ?? (m_inner as ICaptureRequestTarget)?.PendingCapturePath);

    // Reads back this node's own render target (the overlay composited over the world — what the player actually
    // sees) and writes it as a PNG: a new, separately-fenced submit sequenced after the draw above on the same queue.
    private void CaptureIfPending() =>
        m_capture.Serve(
            failureLabel: "[capture] failed",
            writer: m_writeCapture
        );
    // The resources a channel actually lost this frame, each as {verb} ({written} of {reserved} written) — shared by
    // both narrations so a reservation-overflow "dropped" and an own-cap "refused" read in the same shape.
    private static string Describe(string verb, in OverlayChannelUsage counts, in OverlayChannelUsage written, in OverlayChannelReservation reservation) {
        var parts = new List<string>(capacity: 4);

        if (counts.Elements > 0) {
            parts.Add(item: $"{counts.Elements} elements {verb} ({written.Elements} of {reservation.Elements} written)");
        }

        if (counts.TextWords > 0) {
            parts.Add(item: $"{counts.TextWords} text words {verb} ({written.TextWords} of {reservation.TextWords} written)");
        }

        if (counts.Panels > 0) {
            parts.Add(item: $"{counts.Panels} panels {verb} ({written.Panels} of {reservation.Panels} written)");
        }

        if (counts.Clips > 0) {
            parts.Add(item: $"{counts.Clips} clips {verb} ({written.Clips} of {reservation.Clips} written)");
        }

        return string.Join(
            separator: ", ",
            values: parts
        );
    }
    private void EnsureResources() {
        if (m_resourcesReady) {
            return;
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
            sizeBytes: (((uint)m_builder.WordCount) * sizeof(uint)),
            usage: GpuBufferUsage.Storage
        );
        m_pipeline = m_pipelineFactory.Create(
            description: new GpuGraphicsPipelineDescription(
                Name: "overlay-unified",
                VertexInput: new GpuVertexInputLayout(
                    StrideBytes: VertexStrideBytes,
                    Attributes: [new GpuVertexAttribute(
                            Format: GpuVertexFormat.R32G32Float,
                            Location: 0,
                            OffsetBytes: 0
                        )]
                ),
                TextureSamplerCount: TextureSamplerCount,
                EnableStorageBuffer: true,
                PushConstantBinding: new GpuPushConstantBinding(
                    data: new byte[PushConstantByteLength],
                    offset: 0,
                    stageFlags: GpuShaderStage.Fragment
                )
            ),
            fragmentShaderModule: m_fragmentShader,
            renderPass: m_renderPass,
            vertexShaderModule: m_vertexShader
        );

        m_descriptorPool = m_bindings.CreatePool(
            sizes: new GpuDescriptorPoolSizes(
                CombinedImageSamplerCount: TextureSamplerCount,
                MaxSets: 1,
                StorageBufferCount: 1,
                StorageImageCount: 0
            )
        );
        m_descriptorSet = m_bindings.AllocateSet(
            descriptorSetLayoutHandle: m_pipeline.DescriptorSetLayoutHandle,
            poolHandle: m_descriptorPool
        );
        m_sampler = m_bindings.CreateSampler();
        m_bindings.WriteBuffer(
            binding: StorageBufferBinding,
            bufferHandle: m_dataBuffer.BufferHandle,
            bufferSize: (((uint)m_builder.WordCount) * sizeof(uint)),
            descriptorSetHandle: m_descriptorSet,
            elementStride: (4 * sizeof(uint)),
            kind: GpuBindingKind.ReadOnlyBuffer
        );
        // The token slab + glyph atlas are static — upload them ONCE now (the front PanelBaseWords uints); each
        // produced frame rewrites only the dynamic slice after them. A device-loss rebuild re-seeds them here.
        m_dataBuffer.Write<uint>(data: m_builder.Scratch[..m_builder.PanelBaseWords]);
        m_resourcesReady = true;
    }
    private void FillPushConstants() {
        var floats = MemoryMarshal.Cast<byte, float>(span: m_pushConstantData.AsSpan());

        // counts / sdf / misc — KEEP IN SYNC with the shader's OverlayPassData.
        floats[0] = m_builder.PanelCount;
        floats[1] = m_builder.ElementCount;
        floats[2] = m_builder.Glyphs.AtlasCellWidth;
        floats[3] = m_builder.Glyphs.AtlasCellHeight;
        floats[4] = m_builder.Glyphs.DistanceRange;
        floats[5] = OutlineBand;
        floats[6] = m_builder.PanelBaseWords;
        floats[7] = m_builder.ElementBaseWords;
        floats[8] = m_builder.TextBaseWords;
        floats[9] = OverlayTokenBlock.WordCount;   // the glyph pack's base word (the atlas sits after the token slab)
        floats[10] = m_builder.ClipBaseWords;
        floats[11] = m_builder.Glyphs.GlyphCount;  // the pack's total glyph count (ASCII + this boot's appended icons)
    }
    // Not drawing this frame: hand a pending capture down the chain (the shared decorator forwarding contract) so
    // the readback lands on whatever actually produced the shown frame. Keeping it armed when the inner cannot serve
    // it is what stops a request from vanishing silently — the request remains armed until a node serves it or disposal fails it, and a later frame this node does draw serves it here instead.
    private void ForwardPendingCapture() => m_capture.Forward(target: (m_inner as ICaptureRequestTarget));
    // A schema-valid world HUD and one or more independently valid seat HUDs can compose to more live sources than
    // the shader-backed frame-slot table holds. Keep that runtime-only aggregate failure loud and episode-latched.
    private void NarrateFrameSlotOverflow() {
        if (!m_frameSlots.CapacityExceeded) {
            m_frameSlotOverflowEpisodeOpen = false;

            return;
        }

        if (m_frameSlotOverflowEpisodeOpen) {
            return;
        }

        m_frameSlotOverflowEpisodeOpen = true;

        Console.Error.WriteLine(value: $"[unified-overlay] more than {OverlayFrameSlots.SlotCount} distinct HUD frame source bindings were requested this frame; the additional element was omitted because every shader-backed frame slot was occupied. Each HUD document is capped at {OverlayFrameSlots.SlotCount}, and a cross-fading element occupies two slots (its incoming and outgoing sources) until the fade completes; reduce the combined world-plus-seat source set.");
    }
    // Loud once per EPISODE, PER CHANNEL, PER CAUSE: the two loss causes OverlayFrameBuilder tracks — a channel
    // exceeding its own hard RESERVATION (OverlayFrameBuilder.Dropped) vs a writer refusing its own excess at a
    // self-declared cap (OverlayFrameBuilder.Refused, fed by NoteRefused and WriteText's maxChars clamp) — are
    // DIFFERENT FACTS and get DIFFERENT MESSAGES: a reservation overflow means the channel asked for more than its
    // lease and lost it; an own-cap refusal means the writer authored a smaller limit and never asked at all (e.g.
    // the binding bar's hint-line cap can refuse content while nowhere near its reservation). Each cause narrates
    // once per episode, independently, per channel — a channel can open one episode, both, or neither in a given
    // frame.
    private void NarrateOverflow() {
        NarrateFrameSlotOverflow();

        if (!m_builder.HasOverflow) {
            Array.Clear(array: m_overflowEpisodeOpen);
            Array.Clear(array: m_refusalEpisodeOpen);

            return;
        }

        for (var index = 0; (index < OverlayChannelLeases.Count); index++) {
            var channel = ((OverlayChannel)index);
            var reservation = m_builder.ReservationOf(channel: channel);
            var written = m_builder.Written(channel: channel);

            NarrateReservationOverflow(
                channel: channel,
                index: index,
                dropped: m_builder.Dropped(channel: channel),
                reservation: in reservation,
                written: in written
            );
            NarrateOwnCapRefusal(
                channel: channel,
                index: index,
                refused: m_builder.Refused(channel: channel),
                reservation: in reservation,
                written: in written
            );
        }
    }
    // CAUSE 2: the writer itself refused content before ever offering it to the builder (NoteRefused), or a
    // WriteText run was truncated by its own caller's maxChars — a deliberate, pinned limit the writer authored,
    // NOT a reservation overflow. The written/reserved figures below prove the distinction: the channel is fine.
    private void NarrateOwnCapRefusal(OverlayChannel channel, int index, in OverlayChannelUsage refused, in OverlayChannelReservation reservation, in OverlayChannelUsage written) {
        if (refused.IsEmpty) {
            m_refusalEpisodeOpen[index] = false;

            return;
        }

        if (m_refusalEpisodeOpen[index]) {
            return;
        }

        m_refusalEpisodeOpen[index] = true;

        Console.Error.WriteLine(value: $"[unified-overlay] channel \"{OverlayChannelLeases.NameOf(channel: channel)}\" refused its own excess at a writer-declared cap (NOT a reservation overflow — its reservation is fine): {Describe(
            counts: refused,
            reservation: reservation,
            verb: "refused",
            written: written
        )}. A deliberate, pinned truncation the writer authored; silent until this channel renders clean and refuses again.");
    }
    // CAUSE 1: the channel asked the builder for more than OverlayChannelLeases reserved it and the excess clipped —
    // a capacity failure, attributed, never touching another channel.
    private void NarrateReservationOverflow(OverlayChannel channel, int index, in OverlayChannelUsage dropped, in OverlayChannelReservation reservation, in OverlayChannelUsage written) {
        if (dropped.IsEmpty) {
            m_overflowEpisodeOpen[index] = false;

            return;
        }

        if (m_overflowEpisodeOpen[index]) {
            return;
        }

        m_overflowEpisodeOpen[index] = true;

        Console.Error.WriteLine(value: $"[unified-overlay] channel \"{OverlayChannelLeases.NameOf(channel: channel)}\" exceeded its own reservation and clipped: {Describe(
            counts: dropped,
            reservation: reservation,
            verb: "dropped",
            written: written
        )}. No other channel lost capacity; silent until this channel renders clean and overflows again.");
    }
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
            strideBytes: VertexStrideBytes
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
            pipelineLayoutHandle: m_pipeline.LayoutHandle
        );
        m_commandRecorder.Draw(
            commandBufferHandle: commandBufferHandle,
            parameters: new GpuDrawParameters(
                vertexCount: VertexCount,
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
            m_frameSlots.RetireAll();
        } else {
            m_frameSlots.RetireAllAfter(fence: m_frameFence);
        }
    }
    // Uploads only what THIS frame actually wrote, per region — never the capacity-sized region behind it. The
    // shader's loops are bounded by these same counts (delivered above as push constants), so a region's untouched
    // tail holds nothing it will ever read; uploading it would be pure waste. The four regions are NOT contiguous at
    // their used prefixes (each sits at a fixed capacity-sized offset regardless of how much of it this frame used),
    // so this is four small partial writes rather than one big one — cheap: IGpuStorageBuffer.Write is a memcpy into
    // an already-mapped upload buffer on both backends, no command-buffer recording.
    private void UploadFrameRegions() {
        if (m_builder.PanelCount > 0) {
            m_dataBuffer!.Write<uint>(
                data: m_builder.Scratch.Slice(
                    start: m_builder.PanelBaseWords,
                    length: (m_builder.PanelCount * OverlayFrameBuilder.PanelWords)
                ),
                destinationOffsetBytes: ((ulong)(m_builder.PanelBaseWords * sizeof(uint)))
            );
        }

        if (m_builder.ElementCount > 0) {
            m_dataBuffer!.Write<uint>(
                data: m_builder.Scratch.Slice(
                    start: m_builder.ElementBaseWords,
                    length: (m_builder.ElementCount * OverlayFrameBuilder.ElementWords)
                ),
                destinationOffsetBytes: ((ulong)(m_builder.ElementBaseWords * sizeof(uint)))
            );
        }

        if (m_builder.TextWordCount > 0) {
            m_dataBuffer!.Write<uint>(
                data: m_builder.Scratch.Slice(
                    start: m_builder.TextBaseWords,
                    length: m_builder.TextWordCount
                ),
                destinationOffsetBytes: ((ulong)(m_builder.TextBaseWords * sizeof(uint)))
            );
        }

        if (m_builder.ClipCount > 0) {
            m_dataBuffer!.Write<uint>(
                data: m_builder.Scratch.Slice(
                    start: m_builder.ClipBaseWords,
                    length: (m_builder.ClipCount * OverlayFrameBuilder.ClipWords)
                ),
                destinationOffsetBytes: ((ulong)(m_builder.ClipBaseWords * sizeof(uint)))
            );
        }
    }
    private void WriteCapture(string path) {
        m_capturePng.ThrowIfUnavailable(path: path);

        m_readback ??= m_surfaceTransferFactory.CreateReadback();

        var pixels = m_readback.Read(
            bytesPerPixel: 4,
            deviceContext: m_deviceContext,
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
        var boundCount = m_frameSlots.BoundCount;

        for (var slot = 0; (slot < OverlayFrameSlots.SlotCount); slot++) {
            var imageViewHandle = ((slot < boundCount)
                ? m_frameSlots.LeaseAt(slot: slot).ImageViewHandle
                : fallbackImageViewHandle
            );

            m_bindings.WriteCombinedImageSampler(
                arrayElement: 0,
                binding: (FrameSlotFirstBinding + ((uint)slot)),
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
            m_frameSlots.RetireAllAfter(fence: m_frameFence);
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

        // Freshen the pull-model feeds, then let each present writer pack this frame's records CPU-side. Nothing
        // visible = pass the frame through untouched (no extra pass). Each writer still emits inside its own
        // channel scope, so it writes against its own reservation and can never reach another channel's — no writer
        // here carries an ordering-sensitive side effect beyond its own emission.
        m_sources.FeedTick?.Invoke();
        m_builder.BeginFrame();
        // Moves the leases the PREVIOUS produced frame bound aside for retirement (RetirePending, below, once this
        // frame's fence wait proves that frame's sampling pass retired) and clears the slot table for this frame's
        // Bind calls, which the HUD writer's Frame-element emission makes below.
        m_frameSlots.BeginFrame();
        m_currentFrameRenderTicks = context.RenderTicks;
        m_hudWriter?.RefreshFrame();

        // THE BANDED PIPELINE (draw order, bottom to top): UNDER (document order) -> BASE -> OVER (document order).
        // BASE is the four FIRST-PARTY writers, MECHANICALLY drawn in OverlayChannel order (console at the bottom,
        // toast on top; markers sit under the HUD text so a chip near the panel never occludes a line) — UNLESS at
        // least one live authored panel declares the replace band, in which case the replace panels themselves
        // (document order) take the base slot instead and the four first-party writers do not run this frame.
        // Removing the last replace panel restores them on the very next produced frame (HasReplace is recomputed
        // from the fresh snapshot every RefreshFrame call above). Console mirror note: the on-screen console panel is
        // one of the four suppressed writers under replace, but the underlying stdin/stdout control plane
        // (Program.cs / ConsoleTape) is untouched — console verbs keep working exactly as before regardless
        // of what is drawn.
        if (m_hudWriter is { } hudUnder) {
            m_builder.BeginChannel(channel: OverlayChannel.Hud);
            hudUnder.EmitUnder(builder: m_builder);
            m_builder.EndChannel();
        }

        if (m_hudWriter is { HasReplace: true } replacingWriter) {
            m_builder.BeginChannel(channel: OverlayChannel.Hud);
            replacingWriter.EmitReplace(builder: m_builder);
            m_builder.EndChannel();
        } else {
            for (var index = 0; (index < m_channelWriters.Length); index++) {
                if (m_channelWriters[index] is not { } writer) {
                    continue;
                }

                m_builder.BeginChannel(channel: ((OverlayChannel)index));
                writer(m_builder);
                m_builder.EndChannel();
            }
        }

        if (m_hudWriter is { } hudOver) {
            m_builder.BeginChannel(channel: OverlayChannel.Hud);
            hudOver.EmitOver(builder: m_builder);
            m_builder.EndChannel();
        }

        // PLAYER-scope per-seat panels: unbanded (a seat panel has no base slot to take over, so under/base/over
        // ordering is meaningless for it) — drawn last, topmost, so a seat's private panel is never occluded by a
        // world-scope OVER panel or a first-party writer. Charged against the SAME Hud reservation as the three
        // world-scope passes above (OverlayChannelLeases' combined reservation covers all four).
        if (m_hudWriter is { } hudSeats) {
            m_builder.BeginChannel(channel: OverlayChannel.Hud);
            hudSeats.EmitSeatPanels(builder: m_builder);
            m_builder.EndChannel();
        }

        // The radial action menu, then the drawn cursor on top of it — the frame's last two scopes, both
        // deliberately OUTSIDE the replace-band suppression above: the wheel is the pointer's radial action menu
        // and the cursor its on-screen echo, neither of them content, and a fullscreen replace panel is exactly
        // what a pointer must still be able to point (and commit) at.
        if (m_wheelWriter is { } wheelWriter) {
            m_builder.BeginChannel(channel: OverlayChannel.Wheel);
            wheelWriter.Emit(builder: m_builder);
            m_builder.EndChannel();
        }

        if (m_cursorWriter is { } cursorWriter) {
            m_builder.BeginChannel(channel: OverlayChannel.Cursor);
            cursorWriter.Emit(builder: m_builder);
            m_builder.EndChannel();
        }

        NarrateOverflow();

        if (!m_builder.HasContent) {
            ForwardPendingCapture();
            // BeginFrame moved the previous pass's leases aside, and a writer may also have acquired a lease before
            // declining to emit. With no overlay submit, retire both sets after the prior pass's fence.
            RetireForExit(exit: OverlayFrameExit.NoOverlayContent);

            return inner;
        }

        EnsureResources();
        // The previous frame's pass must have retired before the descriptor/buffer/command-buffer rewrites below —
        // which is also what proves the leases OverlayFrameSlots.BeginFrame moved aside above safe to retire.
        m_frameFence!.Wait();
        m_frameSlots.RetirePending();

        if (inner.ImageViewHandle != m_lastImageViewHandle) {
            m_bindings.WriteCombinedImageSampler(
                arrayElement: 0,
                binding: SamplerBinding,
                descriptorSetHandle: m_descriptorSet,
                imageViewHandle: inner.ImageViewHandle,
                samplerHandle: m_sampler
            );

            m_lastImageViewHandle = inner.ImageViewHandle;
        }

        WriteFrameSlotDescriptors(fallbackImageViewHandle: inner.ImageViewHandle);
        FillPushConstants();
        UploadFrameRegions();

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
        m_theme.Publish(theme: in theme);
        m_builder.UpdateTokenBlock(theme: in theme);

        if (m_resourcesReady) {
            m_dataBuffer!.Write<uint>(data: m_builder.Scratch[..OverlayTokenBlock.WordCount]);
        }
    }
}
