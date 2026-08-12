using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Recording.Capture;

namespace Puck.Overlays;

/// <summary>The read seams the unified overlay consumes, each optional (an absent source simply contributes no
/// records), bundled so the constructor arity stays small.</summary>
/// <param name="Console">The console-panel source, or <see langword="null"/>.</param>
/// <param name="BindingBar">The per-seat binding-bar source, or <see langword="null"/>.</param>
/// <param name="Toast">The transient-echo source, or <see langword="null"/>.</param>
/// <param name="FeedTick">Invoked once per produced frame, before the sources are snapshotted — the host's hook to
/// freshen pull-model feeds (e.g. recomposing the per-seat binding frame). Runs on the render thread.</param>
/// <param name="EditorHud">The per-seat editor-HUD source, or <see langword="null"/>.</param>
/// <param name="Gizmos">The per-seat editor-gizmo source (projected chips for geometry-less rows), or
/// <see langword="null"/>.</param>
/// <param name="Hud">The authored world-scope and player-scope (per-seat) HUD structure source, or
/// <see langword="null"/>.</param>
/// <param name="HudBindings">The authored HUD's live binding resolver — required alongside <paramref name="Hud"/>
/// for either scope to draw anything (a <see langword="null"/> pairing on either side draws nothing).</param>
/// <param name="Cursor">The per-seat drawn-cursor source, or <see langword="null"/>.</param>
/// <param name="Wheel">The per-seat radial-action-menu source, or <see langword="null"/>.</param>
public sealed record UnifiedOverlaySources(
    IConsolePanelSource? Console,
    IBindingBarSource? BindingBar,
    IOverlayToastSource? Toast,
    Action? FeedTick,
    IEditorHudSource? EditorHud = null,
    IEditorGizmoSource? Gizmos = null,
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
/// Backend-neutral: only neutral <c>IGpu*</c> services (<see cref="OverlayServices"/>), with bytecode selected by
/// the caller.
/// </summary>
/// <remarks>
/// The overlay decorator contract in full: the per-node submission fence (the previous frame's pass must
/// retire before the buffer/descriptor rewrites), the pass-through fast path (nothing visible = the inner frame
/// returns untouched, no extra pass), and <see cref="ICaptureRequestTarget"/> forwarding (a pending capture lands on
/// whichever node actually produced the shown frame). Zero steady-state allocation: one preallocated scratch, one
/// reused push-constant array, records packed with <see cref="BitConverter.SingleToUInt32Bits"/>.
/// </remarks>
public sealed class UnifiedOverlayNode : IRenderNode, ICaptureRequestTarget, IPassTimingSource {
    // counts float4 + sdf float4 + misc float4 — KEEP IN SYNC with overlay-unified.frag.hlsl's OverlayPassData.
    private const int PushConstantByteLength = ((sizeof(float) * 4) * 3);
    // The glyph outline halo width, in encoded signed-distance units — the SDF contrast band that keeps overlay text
    // legible over any world content, kept clear of the atlas' saturation floor at the overlay's screenPxRange.
    private const float OutlineBand = 0.20f;
    private const uint SamplerBinding = 0;
    private const uint VertexCount = 3;
    private const uint VertexStrideBytes = (sizeof(float) * 2);
    // The FIVE first-party writers' draw-order table size — Console..Toast (OverlayChannel 0..4). OverlayChannel.Hud
    // (5) is DELIBERATELY excluded from this table: it is not a single fixed-position writer but the banded
    // pipeline's under/base/over sequence PLUS the unbanded player-scope seat-panel pass (see ProduceFrame), opened
    // as its own channel scope up to four times a frame rather than once through this table.
    // OverlayChannel.Cursor (6) and OverlayChannel.Wheel (7) are excluded too: they are the frame's LAST two
    // channel scopes (wheel, then cursor on top), drawn over everything and outside the replace-band suppression
    // (see ProduceFrame's tail).
    private const int FirstPartyChannelCount = 5;

    // The one overlay pass's timestamp pair (a begin/end bracket around the fullscreen draw).
    private const uint TimingQueryCount = 2;

    private static readonly byte[] FullscreenTriangleVertexData = CreateFullscreenTriangleVertexData();
    private static readonly string[] OverlayPassLabels = ["overlay"];
    private readonly OverlayFrameBuilder m_builder;
    private readonly BindingBarWriter? m_bindingBarWriter;
    // THE DRAW-ORDER TABLE for the five FIRST-PARTY writers: indexed by (int)OverlayChannel, built once in the
    // constructor. ProduceFrame walks 0..FirstPartyChannelCount-1 and dispatches through this table — the enum's
    // declared order IS the draw order mechanically, never a hand-ordered if-chain a future reorder could silently
    // diverge from. A null entry is a source this instance simply has none of. Toast's extra renderTicks argument
    // rides m_currentFrameRenderTicks (set once per ProduceFrame) rather than widening this delegate's shape for one
    // caller. OverlayChannel.Hud is NOT in this table — see FirstPartyChannelCount's remarks.
    private readonly Action<OverlayFrameBuilder>?[] m_channelWriters;
    private readonly IGpuCommandRecorder m_commandRecorder;
    private readonly ConsolePanelWriter? m_consoleWriter;
    private readonly CursorWriter? m_cursorWriter;
    private readonly WheelWriter? m_wheelWriter;
    private readonly Func<uint, uint, IGpuRenderTarget> m_createRenderTarget;
    private readonly IGpuDescriptorAllocator m_descriptorAllocator;
    private readonly NodeDescriptor m_descriptor;
    private readonly IGpuDeviceContext m_deviceContext;
    private readonly EditorHudWriter? m_editorHudWriter;
    private readonly EditorGizmoWriter? m_gizmoWriter;
    // The authored world-scope HUD's banded writer, or null when the host wired no Hud/HudBindings source pair (see
    // UnifiedOverlaySources' remarks) — draws nothing rather than throwing.
    private readonly HudWriter? m_hudWriter;
    private readonly ReadOnlyMemory<byte> m_fragmentBytecode;
    private readonly uint m_height;
    private readonly IRenderNode m_inner;
    private readonly IGpuPipelineFactory m_pipelineFactory;
    // Rewritten in place each frame (the draw command holds one binding over this array for the node's lifetime).
    private readonly byte[] m_pushConstantData = new byte[PushConstantByteLength];
    private readonly IGpuQueueSubmitter m_queueSubmitter;
    private readonly IGpuShaderModuleFactory m_shaderModuleFactory;
    private readonly UnifiedOverlaySources m_sources;
    private readonly uint m_storageBufferBinding;
    private readonly IGpuStorageBufferFactory m_storageBufferFactory;
    private readonly IGpuSurfaceTransferFactory m_surfaceTransferFactory;
    private readonly IGpuTimingPoolFactory? m_timingPoolFactory;
    private readonly IGpuTimingRecorder? m_timingRecorder;
    private readonly ToastWriter? m_toastWriter;
    private readonly IGpuVertexBufferFactory m_vertexBufferFactory;
    private readonly ReadOnlyMemory<byte> m_vertexBytecode;
    private readonly uint m_width;
    private IGpuStorageBuffer? m_dataBuffer;
    private nint m_descriptorPool;
    private nint m_descriptorSet;
    private bool m_disposed;
    // The per-frame submission fence (frame-ring discipline): this node's single command buffer / host-visible data
    // buffer / descriptor set may only be rewritten once its PREVIOUS submission retired. This pass is queued ahead
    // of the frame's heavy world submit, so by the next frame it has long retired and the wait is ~free.
    private bool m_captureUnavailable;
    // This frame's continuous content clock, latched once per ProduceFrame — the Toast writer's channel-writer
    // delegate reads it (Emit needs renderTicks; the other writers don't) so the draw-order table's delegate shape
    // stays the same one param for every channel.
    private ulong m_currentFrameRenderTicks;
    private IGpuSubmissionFence? m_frameFence;
    private IGpuShaderModule? m_fragmentShader;
    private nint m_lastImageViewHandle;
    // Per-channel RESERVATION-overflow episode latches: set when a channel starts losing records at its own
    // reservation, cleared the frame it renders clean again, so each EPISODE narrates exactly once.
    private readonly bool[] m_overflowEpisodeOpen = new bool[OverlayChannelLeases.Count];
    // Per-channel OWN-CAP-refusal episode latches — the parallel, independent latch for NoteRefused/maxChars
    // truncation narration (see OverlayFrameBuilder.Refused): a channel can open/close this episode with no
    // reservation overflow ever happening, so it cannot share state with m_overflowEpisodeOpen.
    private readonly bool[] m_refusalEpisodeOpen = new bool[OverlayChannelLeases.Count];
    private string? m_pendingCapturePath;
    private IGpuPipeline? m_pipeline;
    // The previous drawn frame's overlay-pass GPU milliseconds (the IPassTimingSource readout).
    private double m_lastOverlayMilliseconds;
    private bool m_previousFrameTimed;
    private IGpuSurfaceReadback? m_readback;
    private IGpuRenderTarget? m_renderTarget;
    private bool m_resourcesReady;
    private nint m_sampler;
    private GpuTimestampCapabilities m_timingCapabilities;
    private IGpuTimingPool? m_timingPool;
    private bool m_timingProbed;
    private bool m_timingReadValid;
    private IGpuShaderModule? m_vertexShader;
    private IGpuVertexBuffer? m_vertexBuffer;

    /// <summary>Initializes a new instance of the <see cref="UnifiedOverlayNode"/> class.</summary>
    /// <param name="inner">The producer whose render the overlay is drawn over (its surface must be sampleable here).</param>
    /// <param name="sources">The per-surface read seams + the feed tick.</param>
    /// <param name="glyphs">The shared SDF glyph pack (per-glyph signed-distance cells).</param>
    /// <param name="services">The neutral GPU service bundle (same device as <paramref name="inner"/>).</param>
    /// <param name="vertexBytecode">The fullscreen vertex shader, in the host backend's bytecode format.</param>
    /// <param name="fragmentBytecode">The unified overlay fragment shader, in the host backend's bytecode format.</param>
    /// <param name="width">The render width in pixels.</param>
    /// <param name="height">The render height in pixels.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public UnifiedOverlayNode(
        IRenderNode inner,
        UnifiedOverlaySources sources,
        OverlayGlyphSdfPack glyphs,
        OverlayServices services,
        ReadOnlyMemory<byte> vertexBytecode,
        ReadOnlyMemory<byte> fragmentBytecode,
        uint width,
        uint height
    ) {
        ArgumentNullException.ThrowIfNull(argument: glyphs);
        ArgumentNullException.ThrowIfNull(argument: inner);
        ArgumentNullException.ThrowIfNull(argument: services);
        ArgumentNullException.ThrowIfNull(argument: sources);

        m_builder = new OverlayFrameBuilder(
            glyphs: glyphs,
            height: height,
            width: width
        );
        m_bindingBarWriter = ((sources.BindingBar is { } bindingBar)
            ? new BindingBarWriter(source: bindingBar)
            : null
        );
        m_commandRecorder = services.CommandRecorder;
        m_consoleWriter = ((sources.Console is { } console)
            ? new ConsolePanelWriter(source: console)
            : null
        );
        m_cursorWriter = ((sources.Cursor is { } cursor)
            ? new CursorWriter(source: cursor)
            : null
        );
        m_wheelWriter = ((sources.Wheel is { } wheel)
            ? new WheelWriter(source: wheel)
            : null
        );
        m_createRenderTarget = services.CreateRenderTarget;
        m_descriptor = new NodeDescriptor(
            Name: "unified-overlay",
            SurfaceId: SurfaceId.New()
        );
        m_descriptorAllocator = services.DescriptorAllocator;
        m_deviceContext = services.DeviceContext;
        m_editorHudWriter = ((sources.EditorHud is { } editorHud)
            ? new EditorHudWriter(source: editorHud)
            : null
        );
        m_gizmoWriter = ((sources.Gizmos is { } gizmos)
            ? new EditorGizmoWriter(source: gizmos)
            : null
        );
        m_hudWriter = (((sources.Hud is { } hudSource) && (sources.HudBindings is { } hudBindings))
            ? new HudWriter(
            source: hudSource,
            bindings: hudBindings
        )
            : null
        );
        m_fragmentBytecode = fragmentBytecode;
        m_height = height;
        m_inner = inner;
        m_pipelineFactory = services.PipelineFactory;
        m_queueSubmitter = services.QueueSubmitter;
        m_shaderModuleFactory = services.ShaderModuleFactory;
        m_sources = sources;
        m_storageBufferBinding = services.StorageBufferBinding;
        m_storageBufferFactory = services.StorageBufferFactory;
        m_surfaceTransferFactory = services.SurfaceTransferFactory;
        m_timingPoolFactory = services.TimingPoolFactory;
        m_timingRecorder = services.TimingRecorder;
        m_toastWriter = ((sources.Toast is { } toast)
            ? new ToastWriter(source: toast)
            : null
        );
        m_vertexBufferFactory = services.VertexBufferFactory;
        m_vertexBytecode = vertexBytecode;
        m_width = width;

        // Built ONCE, after every writer field above is assigned: OverlayChannel's declared values are the array
        // index, so the enum order IS the draw order — see ProduceFrame's dispatch loop. Sized to the five
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
        m_channelWriters[((int)OverlayChannel.Gizmos)] = ((m_gizmoWriter is { } gizmosForTable)
            ? (builder => gizmosForTable.Emit(builder: builder))
            : null
        );
        m_channelWriters[((int)OverlayChannel.EditorHud)] = ((m_editorHudWriter is { } editorHudForTable)
            ? (builder => editorHudForTable.Emit(builder: builder))
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

    /// <inheritdoc/>
    public string? PendingCapturePath => m_pendingCapturePath;

    /// <inheritdoc/>
    public void RequestCapture(string path) => m_pendingCapturePath = path;

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (m_disposed) {
            return default;
        }

        // The inner producer's same-device output is already transitioned shader-readable for the fragment stage
        // before its submit, so this same-queue pass samples it with no CPU wait.
        var inner = m_inner.ProduceFrame(context: context);

        if (
            inner.IsEmpty ||
            (0 == inner.ImageViewHandle)
        ) {
            ForwardPendingCapture();

            return inner;
        }

        // Freshen the pull-model feeds, then let each present writer pack this frame's records CPU-side. Nothing
        // visible = pass the frame through untouched (no extra pass). Each writer still emits inside its own
        // channel scope, so it writes against its own reservation and can never reach another channel's — no writer
        // here carries an ordering-sensitive side effect beyond its own emission.
        m_sources.FeedTick?.Invoke();
        m_builder.BeginFrame();
        m_currentFrameRenderTicks = context.RenderTicks;
        m_hudWriter?.RefreshFrame();

        // THE BANDED PIPELINE (draw order, bottom to top): UNDER (document order) -> BASE -> OVER (document order).
        // BASE is the five FIRST-PARTY writers, MECHANICALLY drawn in OverlayChannel order (console at the bottom,
        // toast on top; gizmos sit under the HUD text so a chip near the panel never occludes a line) — UNLESS at
        // least one live authored panel declares the replace band, in which case the replace panels themselves
        // (document order) take the base slot instead and the five first-party writers do not run this frame.
        // Removing the last replace panel restores them on the very next produced frame (HasReplace is recomputed
        // from the fresh snapshot every RefreshFrame call above). Console mirror note: the on-screen console panel is
        // one of the five suppressed writers under replace, but the underlying stdin/stdout control plane
        // (Program.cs / WorldConsoleMirror) is untouched — console verbs keep working exactly as before regardless
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

            return inner;
        }

        EnsureResources();
        // The previous frame's pass must have retired before the descriptor/buffer/command-buffer rewrites below.
        m_frameFence!.Wait();
        // The retired previous submission's timestamps are readable now — resolve them before this frame overwrites
        // the pool (non-stalling by construction: the fence above just proved retirement).
        ReadPreviousTiming();

        if (inner.ImageViewHandle != m_lastImageViewHandle) {
            m_descriptorAllocator.WriteCombinedImageSampler(
                arrayElement: 0,
                binding: SamplerBinding,
                descriptorSetHandle: m_descriptorSet,
                deviceHandle: m_deviceContext.DeviceHandle,
                imageViewHandle: inner.ImageViewHandle,
                samplerHandle: m_sampler
            );

            m_lastImageViewHandle = inner.ImageViewHandle;
        }

        FillPushConstants();
        UploadFrameRegions();

        var timed = (GpuTimingControl.Shared.Armed && EnsureTimingPool());
        var commandBufferHandle = RecordOverlayPass(timed: timed);

        Span<nint> commandBuffers = [commandBufferHandle];

        m_queueSubmitter.Submit(
            commandBufferHandles: commandBuffers,
            deviceContext: m_deviceContext,
            fence: m_frameFence!
        );
        m_previousFrameTimed = timed;

        CaptureIfPending();

        return new Surface(
            Format: SurfaceFormat.R8G8B8A8Unorm,
            Height: m_height,
            ImageViewHandle: m_renderTarget!.ImageViewHandle,
            Width: m_width
        );
    }

    /// <inheritdoc/>
    public ReadOnlySpan<string> PassLabels => OverlayPassLabels;

    /// <inheritdoc/>
    public int PassCount => 1;

    /// <inheritdoc/>
    public bool TryReadPassTimings(Span<double> passMilliseconds, out int passCount, out double frameMilliseconds) {
        if (
            !m_timingReadValid ||
            (passMilliseconds.Length < 1)
        ) {
            passCount = 0;
            frameMilliseconds = 0.0;

            return false;
        }

        passMilliseconds[0] = m_lastOverlayMilliseconds;
        passCount = 1;
        frameMilliseconds = m_lastOverlayMilliseconds;

        return true;
    }

    // Records the node's single fullscreen pass into the render target's command buffer, optionally bracketed by the
    // begin/end GPU timestamps (top-of-pipe before the pass, bottom-of-pipe + resolve after — outside the render
    // pass, which both backends allow). Returns the recorded command buffer handle, ready to submit.
    private nint RecordOverlayPass(bool timed) {
        var deviceHandle = m_deviceContext.DeviceHandle;
        var commandBufferHandle = m_renderTarget!.CommandBufferHandle;

        m_commandRecorder.BeginCommandBuffer(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle
        );

        if (timed) {
            var poolHandle = m_timingPool!.PoolHandle;

            m_timingRecorder!.ResetTimestamps(
                commandBufferHandle: commandBufferHandle,
                deviceHandle: deviceHandle,
                firstQuery: 0,
                poolHandle: poolHandle,
                queryCount: TimingQueryCount
            );
            m_timingRecorder.WriteTimestamp(
                commandBufferHandle: commandBufferHandle,
                deviceHandle: deviceHandle,
                poolHandle: poolHandle,
                queryIndex: 0,
                stageFlags: GpuTimingStage.TopOfPipe
            );
        }

        m_commandRecorder.BeginDebugGroup(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            label: "unified-overlay"
        );
        m_commandRecorder.BeginRenderPass(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            framebufferHandle: m_renderTarget.FramebufferHandle,
            height: m_renderTarget.Height,
            renderPassHandle: m_renderTarget.RenderPassHandle,
            width: m_renderTarget.Width
        );
        m_commandRecorder.SetScissor(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            height: m_renderTarget.Height,
            width: m_renderTarget.Width,
            x: 0,
            y: 0
        );
        m_commandRecorder.BindGraphicsPipeline(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            pipelineHandle: m_pipeline!.Handle
        );
        m_commandRecorder.BindVertexBuffer(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            vertexBufferHandle: m_vertexBuffer!.BufferHandle
        );
        m_commandRecorder.PushConstants(
            commandBufferHandle: commandBufferHandle,
            data: m_pushConstantData,
            deviceHandle: deviceHandle,
            offset: 0,
            pipelineLayoutHandle: m_pipeline.LayoutHandle,
            stageFlags: GpuShaderStage.Fragment
        );
        m_commandRecorder.BindDescriptorSet(
            commandBufferHandle: commandBufferHandle,
            descriptorSetHandle: m_descriptorSet,
            deviceHandle: deviceHandle,
            pipelineLayoutHandle: m_pipeline.LayoutHandle
        );
        m_commandRecorder.Draw(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            firstInstance: 0,
            firstVertex: 0,
            instanceCount: 1,
            vertexCount: VertexCount
        );
        m_commandRecorder.EndRenderPass(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle
        );
        m_commandRecorder.EndDebugGroup(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle
        );

        if (timed) {
            var poolHandle = m_timingPool!.PoolHandle;

            m_timingRecorder!.WriteTimestamp(
                commandBufferHandle: commandBufferHandle,
                deviceHandle: deviceHandle,
                poolHandle: poolHandle,
                queryIndex: 1,
                stageFlags: GpuTimingStage.BottomOfPipe
            );
            m_timingRecorder.ResolveTimestamps(
                commandBufferHandle: commandBufferHandle,
                deviceHandle: deviceHandle,
                firstQuery: 0,
                poolHandle: poolHandle,
                queryCount: TimingQueryCount
            );
        }

        m_commandRecorder.EndCommandBuffer(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle
        );

        return commandBufferHandle;
    }

    // Lazily stands the timestamp pool up on the first ARMED frame (GpuTimingControl.Shared flips live, the
    // engine-node idiom); false when the backend has no timing seam or the device reports unusable timestamps.
    private bool EnsureTimingPool() {
        if (m_timingPool is not null) {
            return true;
        }

        if (
            (m_timingPoolFactory is null) ||
            (m_timingRecorder is null)
        ) {
            return false;
        }

        if (!m_timingProbed) {
            m_timingProbed = true;
            m_timingCapabilities = m_timingPoolFactory.GetCapabilities(deviceContext: m_deviceContext);

            if (!m_timingCapabilities.IsSupported) {
                Console.Error.WriteLine(value: "[unified-overlay] the device reports no usable GPU timestamps; the overlay pass runs untimed.");
            }
        }

        if (!m_timingCapabilities.IsSupported) {
            return false;
        }

        m_timingPool = m_timingPoolFactory.CreateTimestampPool(
            deviceContext: m_deviceContext,
            queryCapacity: TimingQueryCount
        );

        return true;
    }

    // Reads the retired previous submission's timestamp pair into the published milliseconds (called right after the
    // frame fence wait, so the read never stalls).
    private void ReadPreviousTiming() {
        if (
            !m_previousFrameTimed ||
            (m_timingPool is null)
        ) {
            return;
        }

        Span<ulong> ticks = stackalloc ulong[((int)TimingQueryCount)];

        if (m_timingRecorder!.ReadTimestamps(
            deviceHandle: m_deviceContext.DeviceHandle,
            firstQuery: 0,
            poolHandle: m_timingPool.PoolHandle,
            queryCount: TimingQueryCount,
            rawTicks: ticks
        ) == TimingQueryCount) {
            m_lastOverlayMilliseconds = m_timingCapabilities.TicksToMilliseconds(
                startTicks: ticks[0],
                endTicks: ticks[1]
            );
            m_timingReadValid = true;
        }
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
            verb: "dropped",
            counts: dropped,
            reservation: reservation,
            written: written
        )}. No other channel lost capacity; silent until this channel renders clean and overflows again.");
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
            verb: "refused",
            counts: refused,
            reservation: reservation,
            written: written
        )}. A deliberate, pinned truncation the writer authored; silent until this channel renders clean and refuses again.");
    }

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

    // Not drawing this frame: hand a pending capture down the chain (the shared decorator forwarding contract) so
    // the readback lands on whatever actually produced the shown frame.
    private void ForwardPendingCapture() {
        if (m_pendingCapturePath is not { } path) {
            return;
        }

        m_pendingCapturePath = null;

        if (m_inner is ICaptureRequestTarget target) {
            target.RequestCapture(path: path);
        }
    }

    // Reads back this node's own render target (the overlay composited over the world — what the player actually
    // sees) and writes it as a PNG: a new, separately-fenced submit sequenced after the draw above on the same queue.
    private void CaptureIfPending() {
        if (m_pendingCapturePath is not { } path) {
            return;
        }

        m_pendingCapturePath = null;

        if (m_captureUnavailable) {
            // The latch spares a doomed assembly load per frame, but a request dropped for it still has to be said
            // out loud: the requester was told a path and no file is coming.
            Console.Error.WriteLine(value: $"[capture] skipped, Puck.Recording is unavailable — no file written to {path}");

            return;
        }

        m_readback ??= m_surfaceTransferFactory.CreateReadback(deviceContext: m_deviceContext);

        var pixels = m_readback.Read(
            bytesPerPixel: 4,
            deviceContext: m_deviceContext,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: m_height,
            sourceImageHandle: m_renderTarget!.ImageHandle,
            width: m_width
        );

        if (TryWriteCapturePng(
            path: path,
            rgba: pixels.Span,
            width: ((int)m_width),
            height: ((int)m_height)
        )) {
            Console.Error.WriteLine(value: $"[capture] unified overlay -> {path}");
        } else {
            m_captureUnavailable = true;
        }
    }

    // Puck.Recording is an optional subsystem (screenshots/recording): an environment that blocks or cannot load its
    // assembly (an Application Control / code-integrity policy, a missing deployment file) must not take the render
    // loop down with it. WriteCapturePngCore is the ONLY member touching the Puck.Recording-typed PngEncoder.Write
    // call, kept non-inlined so the CLR only needs to resolve and load Puck.Recording.dll when this exact method is
    // JITted — i.e. lazily, on the first actual capture request, not on every produced frame (CaptureIfPending runs
    // every frame; without this split, merely JITting it once would force the load). TryWriteCapturePng's try/catch
    // sits at the CALL SITE one frame up: a failure to load the assembly surfaces as an exception thrown BY that
    // call (the callee never got to run), which is exactly where a surrounding try/catch can observe and report it.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void WriteCapturePngCore(string path, ReadOnlySpan<byte> rgba, int width, int height) {
        PngEncoder.Write(
            height: height,
            path: path,
            rgba: rgba,
            width: width
        );
    }

    // Attempts one capture write, surviving (and loudly reporting) an environment that refuses to load Puck.Recording.
    // Returns false on any such failure so the caller can latch m_captureUnavailable and stop retrying a doomed load.
    private static bool TryWriteCapturePng(string path, ReadOnlySpan<byte> rgba, int width, int height) {
        try {
            WriteCapturePngCore(
                path: path,
                rgba: rgba,
                width: width,
                height: height
            );

            return true;
        } catch (Exception exception) when ((exception is FileLoadException or FileNotFoundException or TypeLoadException or BadImageFormatException or TypeInitializationException)) {
            Console.Error.WriteLine(value: $"[capture] WARNING: Puck.Recording is unavailable ({exception.GetType().Name}: {exception.Message}) — frame capture skipped, render continues without it.");

            return false;
        }
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
        floats[11] = 0f;
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
    private void EnsureResources() {
        if (m_resourcesReady) {
            return;
        }

        m_renderTarget = m_createRenderTarget(
            arg1: m_width,
            arg2: m_height
        );
        m_frameFence = m_queueSubmitter.CreateSubmissionFence(deviceContext: m_deviceContext);
        m_vertexShader = m_shaderModuleFactory.Create(
            bytecode: m_vertexBytecode,
            deviceContext: m_deviceContext,
            stage: GpuShaderStage.Vertex
        );
        m_fragmentShader = m_shaderModuleFactory.Create(
            bytecode: m_fragmentBytecode,
            deviceContext: m_deviceContext,
            stage: GpuShaderStage.Fragment
        );
        m_vertexBuffer = m_vertexBufferFactory.Create(
            deviceContext: m_deviceContext,
            strideBytes: VertexStrideBytes,
            vertexData: FullscreenTriangleVertexData
        );
        m_dataBuffer = m_storageBufferFactory.Create(
            deviceContext: m_deviceContext,
            sizeBytes: (((uint)m_builder.WordCount) * sizeof(uint))
        );
        m_pipeline = m_pipelineFactory.Create(
            deviceContext: m_deviceContext,
            enableStorageBuffer: true,
            fragmentShaderModule: m_fragmentShader,
            height: m_height,
            pushConstantBinding: new GpuPushConstantBinding(
                data: new byte[PushConstantByteLength],
                offset: 0,
                stageFlags: GpuShaderStage.Fragment
            ),
            renderTarget: m_renderTarget,
            textureSamplerCount: 1,
            vertexShaderModule: m_vertexShader,
            width: m_width
        );

        var deviceHandle = m_deviceContext.DeviceHandle;

        m_descriptorPool = m_descriptorAllocator.CreatePool(
            deviceHandle: deviceHandle,
            sizes: new GpuDescriptorPoolSizes(
                MaxSets: 1,
                CombinedImageSamplerCount: 1,
                StorageBufferCount: 1,
                StorageImageCount: 0,
                AccelerationStructureCount: 0
            )
        );
        m_descriptorSet = m_descriptorAllocator.AllocateSet(
            descriptorSetLayoutHandle: m_pipeline.DescriptorSetLayoutHandle,
            deviceHandle: deviceHandle,
            poolHandle: m_descriptorPool
        );
        m_sampler = m_descriptorAllocator.CreateSampler(deviceHandle: deviceHandle);
        m_descriptorAllocator.WriteStorageBuffer(
            binding: m_storageBufferBinding,
            bufferHandle: m_dataBuffer.BufferHandle,
            bufferSize: (((uint)m_builder.WordCount) * sizeof(uint)),
            descriptorSetHandle: m_descriptorSet,
            deviceHandle: deviceHandle
        );
        // The token slab + glyph atlas are static — upload them ONCE now (the front PanelBaseWords uints); each
        // produced frame rewrites only the dynamic slice after them. A device-loss rebuild re-seeds them here.
        m_dataBuffer.Write<uint>(data: m_builder.Scratch[..m_builder.PanelBaseWords]);
        m_resourcesReady = true;
    }
    private static byte[] CreateFullscreenTriangleVertexData() {
        var vertices = new (float X, float Y)[]
        {
            (-1f, -1f),
            (3f, -1f),
            (-1f, 3f),
        };
        var vertexData = new byte[((int)(VertexStrideBytes * vertices.Length))];

        for (var index = 0; (index < vertices.Length); index++) {
            var offset = (index * ((int)VertexStrideBytes));

            _ = BitConverter.TryWriteBytes(
                destination: vertexData.AsSpan(
                    length: sizeof(float),
                    start: offset
                ),
                value: vertices[index].X
            );
            _ = BitConverter.TryWriteBytes(
                destination: vertexData.AsSpan(
                    length: sizeof(float),
                    start: (offset + sizeof(float))
                ),
                value: vertices[index].Y
            );
        }

        return vertexData;
    }

    /// <inheritdoc/>
    public void OnDeviceLost() {
        ReleaseGpuResources();
        m_inner.OnDeviceLost();
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        ReleaseGpuResources();
        m_inner.Dispose();
    }

    private void ReleaseGpuResources() {
        var deviceHandle = m_deviceContext.DeviceHandle;

        if (0 != m_sampler) {
            m_descriptorAllocator.DestroySampler(
                deviceHandle: deviceHandle,
                samplerHandle: m_sampler
            );
            m_sampler = 0;
        }

        if (0 != m_descriptorPool) {
            m_descriptorAllocator.DestroyPool(
                deviceHandle: deviceHandle,
                poolHandle: m_descriptorPool
            );
            m_descriptorPool = 0;
            m_descriptorSet = 0;
        }

        m_pipeline?.Dispose();
        m_pipeline = null;
        m_timingPool?.Dispose();
        m_timingPool = null;
        m_timingProbed = false;
        m_previousFrameTimed = false;
        m_timingReadValid = false;
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
        m_renderTarget?.Dispose();
        m_renderTarget = null;
        m_lastImageViewHandle = 0;
        m_resourcesReady = false;
    }
}
