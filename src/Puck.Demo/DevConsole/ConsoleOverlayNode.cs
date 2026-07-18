using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Assets;
using Puck.Capture;
using Puck.Compositing;
using Puck.Demo.Ui;
using Puck.Hosting;
using Puck.SdfVm;

namespace Puck.Demo.DevConsole;

/// <summary>
/// The on-screen developer-console overlay: wraps a same-device inner producer, samples it in one fullscreen
/// fragment pass, and draws the console panel (a translucent backing plus the input line and recent output) on top,
/// in a fixed monospace grid. Its glyph source GRADUATED from a GDI+ coverage bitmap to the ONE shared SDF glyph atlas
/// (<see cref="ConsoleGlyphAtlas"/> over <see cref="Text.SharedGlyphAtlas"/>) — the same distance field the world-glyph
/// op marches and the diegetic UI embosses. The per-glyph SDF cells AND the per-frame character grid both ride ONE
/// host-visible storage buffer (the atlas packed once at the front, the text cells after it), so the overlay keeps the
/// proven single-combined-image-sampler + one-storage-buffer shape of the binding bar — no second texture binding. The
/// fragment shader reconstructs each edge with bilinear sampling + a screenPxRange coverage ramp (crisp at any overlay
/// scale) and draws an outline band from the same field for readability over bright world content. When the console is
/// closed the pass is skipped entirely (the frame passes through untouched). Vulkan-only, exactly like the binding-bar
/// overlay it mirrors.
///
/// The panel is DRAGGABLE by its title band: each <see cref="ProduceFrame"/> polls the shared <see cref="PointerStore"/>
/// (reached lazily through the <c>appServices</c> escape — the same IServiceProvider-lookup shape
/// <c>AgbDebugService</c>/<c>TrackerModeState</c> use — so the overworld render node, already at its CA1506
/// class-coupling ceiling, never names <see cref="PointerStore"/> or <see cref="DemoConsole"/> itself). A left-press
/// landing inside the current title-band rect begins a drag; the resolved position (grab-offset subtracted, then
/// clamped so the title band always stays fully on screen) is written back through
/// <see cref="DemoConsole.SetPanelPosition"/> — the SAME seam the <c>console.move</c> verb uses — so the published
/// <see cref="ConsoleTextFrame.PanelPosition"/> stays the one source of truth. Pointer state is presentation/
/// session-only (see <see cref="PointerStore"/>'s own remarks), so none of this ever touches determinism.
/// </summary>
internal sealed class ConsoleOverlayNode : IRenderNode, ICaptureRequestTarget {
    // The panel's inset from the world edge — DesignTokens.Space.Space8 ("stage margin (floats inset from world
    // edge)"). KEEP IN SYNC with console-overlay.frag.hlsl's STAGE_MARGIN (used only by this file; the shader
    // receives the already-resolved panel rect through pc.panel).
    private const int StageMargin = (int)DesignTokens.Space.Space8;
    // The panel's inner padding rhythm — DesignTokens.Space.Space3 ("panel section pad") — reused for every gap:
    // left/right/bottom content inset, and the gap between the title-bar divider and the first content row. KEEP
    // IN SYNC with the shader's PANEL_PAD.
    private const int ContentPad = (int)DesignTokens.Space.Space3;
    // The title-bar band's height — DesignTokens.Space.HeightConsoleHead. KEEP IN SYNC with the shader's TITLE_BAND_HEIGHT.
    private const int TitleBandHeight = (int)DesignTokens.Space.HeightConsoleHead;
    private const string PromptPrefix = "> ";
    // The outline halo width behind each glyph, in encoded signed-distance units (0.5 = edge): pushes a second, dark
    // threshold band out from the letter edge so overlay text stays legible over any world content — the SDF contrast
    // toolkit the floats-over-a-lit-world overlay always needed, from the SAME distance field, at zero extra taps. Kept
    // small so the outline threshold (0.5 − band) stays clear of the atlas' saturation floor (encoded 0, deep outside)
    // at the console's downscaled screenPxRange (~2), or empty/space cells would pick up a faint wash.
    private const float OutlineBand = 0.20f;
    // Header float4s: (panelX, panelY, panelW, panelH) px, the OUTER rounded-rect bounds (title band + padded
    // content) · (cols, rows, cellW, cellH on-screen), the CONTENT grid only · (caretOn, dragging, reserved x2) ·
    // (cursorCol, cursorRow, textCellUintOffset, firstChar) · (atlasCellW, atlasCellH, screenPxRange, outlineBand) —
    // the last float4 carries the SDF reconstruction the graduated glyph source needs. `dragging` (0/1) is the
    // title-band-drag affordance: the shader brightens the divider hairline while the panel is being dragged (no
    // new buffer, one of the state float4's reserved lanes). Every color (scrim, hairline, title text, phosphor/
    // neutral row text, the accent caret) is a DesignTokens constant baked into the shader as an HLSL literal —
    // mirrored, not pushed, since none of them vary per frame.
    private const int PushConstantByteLength = ((sizeof(float) * 4) * 5);
    private const uint SamplerBinding = 0;
    private const uint VertexCount = 3;
    private const uint VertexStrideBytes = (sizeof(float) * 2);

    private static readonly byte[] FullscreenTriangleVertexData = CreateFullscreenTriangleVertexData();

    // The IServiceProvider escape (see the class remarks): resolved lazily so a headless/no-window run — where
    // PointerStore or DemoConsole may be absent — degrades to no dragging rather than throwing.
    private readonly IServiceProvider m_appServices;
    private readonly int m_cols;
    private readonly GpuCompositor m_compositor;
    private readonly Func<IGpuRenderTarget> m_createRenderTarget;
    private readonly uint m_dataUintCount;
    private readonly IGpuDescriptorAllocator m_descriptorAllocator;
    private readonly NodeDescriptor m_descriptor;
    private readonly IGpuDeviceContext m_deviceContext;
    private readonly ReadOnlyMemory<byte> m_fragmentBytecode;
    private readonly ConsoleGlyphAtlas m_font;
    private readonly uint m_height;
    private readonly IRenderNode m_inner;
    private readonly IGpuPipelineFactory m_pipelineFactory;
    // Reused across frames: the push-constant header is rewritten in place (all 20 floats each frame) rather than
    // reallocated, so the draw command can hold one binding over this array for the overlay's lifetime.
    private readonly byte[] m_pushConstantData = new byte[PushConstantByteLength];
    private readonly IGpuQueueSubmitter m_queueSubmitter;
    private readonly int m_rows;
    private readonly uint[] m_scratch;
    private readonly IGpuShaderModuleFactory m_shaderModuleFactory;
    private readonly IConsoleTextSource m_source;
    private readonly uint m_storageBufferBinding;
    private readonly IGpuStorageBufferFactory m_storageBufferFactory;
    private readonly IGpuSurfaceTransferFactory m_surfaceTransferFactory;
    private readonly int m_textOffsetUints;
    private readonly IGpuVertexBufferFactory m_vertexBufferFactory;
    private readonly ReadOnlyMemory<byte> m_vertexBytecode;
    private readonly uint m_width;
    private nint m_descriptorPool;
    private nint m_descriptorSet;
    private DemoConsole? m_demoConsole;
    private bool m_disposed;
    private IGpuStorageBuffer? m_dataBuffer;
    private Vector2 m_dragGrabOffset;
    private bool m_dragging;
    private GpuDrawCommand[]? m_drawCommands;
    private IGpuShaderModule? m_fragmentShader;
    private ulong m_lastLeftPressCount;
    private nint m_lastImageViewHandle;
    private string? m_pendingCapturePath;
    private IGpuPipeline? m_pipeline;
    private AssetContentHash m_pipelineId;
    private IReadOnlyDictionary<AssetContentHash, IGpuPipeline>? m_pipelines;
    private bool m_pointerSeamsResolved;
    private IPointerSource? m_pointerSource;
    private IGpuSurfaceReadback? m_readback;
    // The per-frame submission fence (frame-ring discipline): with the host's per-frame device drain gone, this
    // node's single command buffer / host-visible data buffer / descriptor set may only be rewritten once its
    // PREVIOUS submission retired. This pass is queued ahead of the frame's heavy world submit, so by the next
    // frame it has long retired and the wait is ~free.
    private IGpuSubmissionFence? m_frameFence;
    private IGpuRenderTarget? m_renderTarget;
    private bool m_resourcesReady;
    private nint m_sampler;
    private IGpuShaderModule? m_vertexShader;
    private IGpuVertexBuffer? m_vertexBuffer;

    /// <summary>Initializes a new instance of the <see cref="ConsoleOverlayNode"/> class.</summary>
    /// <param name="inner">The producer whose render the console is drawn over (its surface must be sampleable here).</param>
    /// <param name="source">The per-frame console state to draw.</param>
    /// <param name="font">The shared-SDF-atlas glyph pack (per-glyph signed-distance cells).</param>
    /// <param name="services">The producer's neutral GPU service bundle (same device).</param>
    /// <param name="appServices">The app's DI container — the escape this node uses to reach <see cref="PointerStore"/>
    /// and <see cref="DemoConsole"/> lazily for the title-band drag, without either becoming a compile-time dependency
    /// of whatever composes this node (see the class remarks).</param>
    /// <param name="vertexBytecode">The fullscreen vertex shader.</param>
    /// <param name="fragmentBytecode">The console fragment shader.</param>
    /// <param name="width">The render width in pixels.</param>
    /// <param name="height">The render height in pixels.</param>
    public ConsoleOverlayNode(
        IRenderNode inner,
        IConsoleTextSource source,
        ConsoleGlyphAtlas font,
        SdfProducerServices services,
        IServiceProvider appServices,
        ReadOnlyMemory<byte> vertexBytecode,
        ReadOnlyMemory<byte> fragmentBytecode,
        uint width,
        uint height
    ) {
        ArgumentNullException.ThrowIfNull(appServices);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(source);

        m_appServices = appServices;
        m_compositor = new GpuCompositor(commandRecorder: services.CommandRecorder);
        m_createRenderTarget = services.CreateRenderTarget;
        m_descriptor = new NodeDescriptor(Name: "console-overlay", SurfaceId: SurfaceId.New());
        m_descriptorAllocator = services.DescriptorAllocator;
        m_deviceContext = services.DeviceContext;
        m_font = font;
        m_fragmentBytecode = fragmentBytecode;
        m_height = height;
        m_inner = inner;
        m_pipelineFactory = services.PipelineFactory;
        m_queueSubmitter = services.QueueSubmitter;
        m_shaderModuleFactory = services.ShaderModuleFactory;
        m_source = source;
        m_storageBufferBinding = services.StorageBufferBinding;
        m_storageBufferFactory = services.StorageBufferFactory;
        m_surfaceTransferFactory = services.SurfaceTransferFactory;
        m_vertexBufferFactory = services.VertexBufferFactory;
        m_vertexBytecode = vertexBytecode;
        m_width = width;

        // The grid fills the top-left of the frame without overrunning it — cols across, up to ~55% of the height.
        // The available box is the stage-margin-inset panel MINUS the title band and the padding rhythm on every
        // side (left/right/top-of-content/bottom), so the panel's outer rect (title band + padded grid) still
        // lands flush at StageMargin from the world edge.
        var availableWidth = (((int)width - (2 * StageMargin)) - (2 * ContentPad));
        var availableHeight = ((((int)(height * 0.55f)) - TitleBandHeight) - (2 * ContentPad));

        m_cols = Math.Clamp(value: (availableWidth / font.CellWidth), min: 8, max: 120);
        m_rows = Math.Clamp(value: (availableHeight / font.CellHeight), min: 4, max: 40);

        // The storage buffer is the font atlas (packed per-glyph SDF cells) followed by the cols*rows character grid.
        m_textOffsetUints = font.PackedSdf.Count;
        m_dataUintCount = (uint)(m_textOffsetUints + (m_cols * m_rows));
        m_scratch = new uint[m_dataUintCount];

        for (var index = 0; (index < font.PackedSdf.Count); index++) {
            m_scratch[index] = font.PackedSdf[index];
        }
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;

    /// <inheritdoc/>
    public void RequestCapture(string path) => m_pendingCapturePath = path;

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (m_disposed) {
            return default;
        }

        var inner = m_inner.ProduceFrame(context: context);

        if (inner.IsEmpty || (0 == inner.ImageViewHandle)) {
            ForwardPendingCapture();

            return inner;
        }

        // Closed console (or nothing published yet): pass the frame through untouched — no extra pass. A pending
        // capture still needs to land on whatever actually produced this frame, so hand it down.
        if (!m_source.TrySnapshot(frame: out var frame) || !frame.Visible) {
            ForwardPendingCapture();

            return inner;
        }

        var panelPosition = UpdatePanelPosition(frame: in frame);

        EnsureResources();
        // The previous frame's pass must have retired before the descriptor/buffer/command-buffer rewrites below.
        m_frameFence!.Wait();

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

        var cursorColumn = PackText(frame: in frame);
        // caret.blink (steps(1) — a hard on/off toggle, never a fade): the phase comes from the DETERMINISTIC engine
        // clock (RenderTicks), never wall-clock, so replay stays bit-identical even though the caret is decorative.
        var blinkPeriodMs = DesignTokens.Motion.CaretBlink;
        var phaseMs = ((EngineTicks.ToSeconds(ticks: context.RenderTicks) * 1000.0) % blinkPeriodMs);
        var caretOn = (phaseMs < (blinkPeriodMs * 0.5));

        // The draw command already holds a binding over m_pushConstantData (set in EnsureResources); rewriting the
        // bytes in place updates what Record reads — no per-frame array, binding, or draw-command copy.
        FillPushConstants(caretOn: caretOn, cursorColumn: cursorColumn, dragging: m_dragging, panelPosition: panelPosition);

        // Only the character grid changed this frame; the static glyph atlas prefix was uploaded once in
        // EnsureResources, so write just the grid slice at its byte offset rather than re-uploading the ~13.7KB font.
        m_dataBuffer!.Write<uint>(data: m_scratch.AsSpan(start: m_textOffsetUints, length: (m_cols * m_rows)), destinationOffsetBytes: (ulong)(m_textOffsetUints * sizeof(uint)));

        var commandBufferHandle = m_compositor.Record(
            deviceContext: m_deviceContext,
            drawCommands: m_drawCommands!,
            pipelines: m_pipelines!,
            target: m_renderTarget!
        );

        Span<nint> commandBuffers = [commandBufferHandle];

        m_queueSubmitter.Submit(
            commandBufferHandles: commandBuffers,
            deviceContext: m_deviceContext,
            fence: m_frameFence!
        );

        // A pending capture reads back THIS node's own just-submitted render target (the console draws over the
        // world this frame, so this is the composited image the player actually sees) — the readback is a new,
        // separately-fenced submit sequenced after the draw above on the same queue, mirroring how SdfEngineNode
        // itself reads back after its own fire-and-forget submit.
        CaptureIfPending();

        return new Surface(
            Format: SurfaceFormat.R8G8B8A8Unorm,
            Height: m_height,
            ImageViewHandle: m_renderTarget!.ImageViewHandle,
            Width: m_width
        );
    }

    // Not drawing this frame (closed console, or nothing published yet): the frame this node returns is really its
    // inner producer's own frame, so a pending capture request is handed down to whichever node produced it — the
    // binding-bar overlay if present, else the bare SDF producer — rather than reading back a target this node
    // never wrote to this frame.
    private void ForwardPendingCapture() {
        if (m_pendingCapturePath is not { } path) {
            return;
        }

        m_pendingCapturePath = null;

        // The forward is worth a notice: a capture that expected the panel and got the pass-through frame reads as a
        // rendering bug, and "the panel was not drawing" is the first diagnostic answer.
        Console.Error.WriteLine(value: $"[capture] console overlay passed through (panel not drawing this frame) -> {path}");

        if (m_inner is ICaptureRequestTarget target) {
            target.RequestCapture(path: path);
        } else if (m_inner is SdfEngineNode producer) {
            producer.RequestCapture(path: path);
        }
    }

    // Reads back this node's own render target (the console panel composited over the world) and writes it as a
    // PNG — the runtime sibling of SdfEngineNode.RequestCapture's readback, at the OUTERMOST layer instead of the
    // pre-overlay producer.
    private void CaptureIfPending() {
        if (m_pendingCapturePath is not { } path) {
            return;
        }

        m_pendingCapturePath = null;
        m_readback ??= m_surfaceTransferFactory.CreateReadback(deviceContext: m_deviceContext);

        var pixels = m_readback.Read(
            bytesPerPixel: 4,
            deviceContext: m_deviceContext,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: m_height,
            sourceImageHandle: m_renderTarget!.ImageHandle,
            width: m_width
        );

        PngEncoder.Write(height: (int)m_height, path: path, rgba: pixels.Span, width: (int)m_width);
        Console.Error.WriteLine(value: $"[capture] console overlay -> {path}");
    }

    // Fills the text-cell region of the scratch buffer from the console frame: the trailing output lines fill the
    // rows above, and the bottom row is the prompt + input line. Returns the input caret's column on the bottom row.
    private int PackText(in ConsoleTextFrame frame) {
        // Clear the grid (0 = empty; the shader draws nothing for non-printable codes).
        Array.Clear(array: m_scratch, index: m_textOffsetUints, length: (m_cols * m_rows));

        var lines = frame.Lines;
        var historyRows = (m_rows - 1);
        var firstShown = Math.Max(val1: 0, val2: (lines.Count - historyRows));

        for (var row = 0; ((row < historyRows) && ((firstShown + row) < lines.Count)); row++) {
            WriteRow(row: row, column: 0, text: lines[(firstShown + row)]);
        }

        // The bottom row is the live prompt: the fixed prefix then the input, written straight into the grid (no concat).
        var input = frame.Input;

        WriteRow(row: (m_rows - 1), column: 0, text: PromptPrefix);
        WriteRow(row: (m_rows - 1), column: PromptPrefix.Length, text: input);

        return Math.Min(val1: (PromptPrefix.Length + input.Length), val2: (m_cols - 1));
    }
    private void WriteRow(int row, int column, string text) {
        var baseIndex = ((m_textOffsetUints + (row * m_cols)) + column);
        var count = Math.Min(val1: text.Length, val2: (m_cols - column));

        for (var index = 0; (index < count); index++) {
            m_scratch[(baseIndex + index)] = text[index];
        }
    }
    private void FillPushConstants(bool caretOn, int cursorColumn, bool dragging, Vector2 panelPosition) {
        var floats = MemoryMarshal.Cast<byte, float>(span: m_pushConstantData.AsSpan());

        var (panelWidth, panelHeight) = PanelSize();

        // The OUTER panel rect: the title band, the divider, and the padded content grid, all inside one rounded
        // shape. Defaults to flush at StageMargin from the world edge; a title-band drag or the console.move verb
        // overrides the top-left corner (ConsoleTextFrame.PanelPosition — see UpdatePanelPosition). KEEP the
        // shader's TITLE_BAND_HEIGHT/PANEL_PAD literals in sync with TitleBandHeight/ContentPad above — this is the
        // geometry contract between the two files.
        floats[0] = panelPosition.X;                          // panel x (px)
        floats[1] = panelPosition.Y;                          // panel y (px)
        floats[2] = panelWidth;                               // panel width (px)
        floats[3] = panelHeight;                              // panel height (px)
        floats[4] = m_cols;
        floats[5] = m_rows;
        floats[6] = m_font.CellWidth;
        floats[7] = m_font.CellHeight;
        floats[8] = (caretOn ? 1f : 0f);
        floats[9] = (dragging ? 1f : 0f);                      // the title-band-drag affordance (brightens the hairline)
        floats[10] = 0f;                                       // reserved
        floats[11] = 0f;                                       // reserved
        floats[12] = cursorColumn;
        floats[13] = (m_rows - 1);                // the caret rides the bottom (prompt) row
        floats[14] = m_textOffsetUints;           // where the character grid begins in the buffer
        floats[15] = ConsoleGlyphAtlas.FirstChar;
        floats[16] = m_font.AtlasCellWidth;       // one glyph block's texel width in the packed SDF pack
        floats[17] = m_font.AtlasCellHeight;      // one glyph block's texel height
        // screenPxRange: the encoded band width in DESTINATION pixels — distanceRange(texels) x screen-px-per-texel.
        // The on-screen cell height maps the atlas cell height, so the ratio is the pixels-per-texel scale.
        floats[18] = (m_font.DistanceRange * ((float)m_font.CellHeight / m_font.AtlasCellHeight));
        floats[19] = OutlineBand;
    }

    // The outer panel's on-screen size in pixels (title band + divider + padded grid) — fixed by the grid metrics
    // computed at construction, so it never varies per frame. Shared by FillPushConstants (the render rect) and
    // UpdatePanelPosition (the drag clamp bounds) so the two can never drift apart.
    private (float Width, float Height) PanelSize() {
        var gridWidth = (m_cols * m_font.CellWidth);
        var gridHeight = (m_rows * m_font.CellHeight);

        return ((gridWidth + (2 * ContentPad)), ((TitleBandHeight + (2 * ContentPad)) + gridHeight));
    }

    // Resolves this frame's effective panel top-left, advancing the title-band drag state machine along the way: a
    // left-press landing inside the CURRENT title band begins a drag; while held, the panel tracks the pointer
    // (grab-offset subtracted) CLAMPED so the title band always stays fully on screen; release ends it. Every
    // dragged frame writes the result back through DemoConsole.SetPanelPosition — the SAME seam the console.move
    // verb uses — so ConsoleTextFrame.PanelPosition stays the one published source of truth. Degrades to the
    // frame's already-published position (no dragging) when PointerStore or DemoConsole is unavailable
    // (headless/no window) — the caller only reaches here once frame.Visible is already true.
    private Vector2 UpdatePanelPosition(in ConsoleTextFrame frame) {
        var (panelWidth, _) = PanelSize();
        var currentPosition = (frame.PanelPosition ?? new Vector2(x: StageMargin, y: StageMargin));

        ResolvePointerSeams();

        if ((m_pointerSource is null) || (m_demoConsole is null) || !m_pointerSource.TrySnapshot(frame: out var pointer)) {
            m_dragging = false;

            return currentPosition;
        }

        // A monotonic-counter diff (rather than just reading Held) catches a press that lands between two
        // ProduceFrame polls without needing a frame-boundary reset — the same idea PointerButtonState documents.
        var pressedEdge = (pointer.Left.PressCount != m_lastLeftPressCount);

        m_lastLeftPressCount = pointer.Left.PressCount;

        if (!m_dragging && pressedEdge && pointer.Left.Held) {
            var insideTitleBand = (
                (pointer.Position.X >= currentPosition.X) &&
                (pointer.Position.X < (currentPosition.X + panelWidth)) &&
                (pointer.Position.Y >= currentPosition.Y) &&
                (pointer.Position.Y < (currentPosition.Y + TitleBandHeight))
            );

            if (insideTitleBand) {
                m_dragging = true;
                m_dragGrabOffset = (pointer.Position - currentPosition);
            }
        }

        if (!m_dragging) {
            return currentPosition;
        }

        if (!pointer.Left.Held) {
            // Released this frame: the position already published on the last held frame stands: nothing further
            // to write, and the NEXT frame reads frame.PanelPosition fresh (no override needed here).
            m_dragging = false;

            return currentPosition;
        }

        var maxX = Math.Max(val1: 0f, val2: ((float)m_width - panelWidth));
        var maxY = Math.Max(val1: 0f, val2: ((float)m_height - TitleBandHeight));
        var proposed = (pointer.Position - m_dragGrabOffset);
        var clamped = new Vector2(
            x: Math.Clamp(value: proposed.X, min: 0f, max: maxX),
            y: Math.Clamp(value: proposed.Y, min: 0f, max: maxY)
        );

        return m_demoConsole.SetPanelPosition(position: clamped);
    }

    // Lazily resolves the pointer/console seams through the DI escape (see the class remarks) — once per node
    // lifetime rather than once per frame, so a missing registration (headless/no window) costs one failed lookup,
    // not one every produced frame.
    private void ResolvePointerSeams() {
        if (m_pointerSeamsResolved) {
            return;
        }

        m_pointerSeamsResolved = true;
        m_pointerSource = (m_appServices.GetService(serviceType: typeof(PointerStore)) as IPointerSource);
        m_demoConsole = (m_appServices.GetService(serviceType: typeof(DemoConsole)) as DemoConsole);
    }
    private void EnsureResources() {
        if (m_resourcesReady) {
            return;
        }

        m_renderTarget = m_createRenderTarget();
        m_frameFence = m_queueSubmitter.CreateSubmissionFence(deviceContext: m_deviceContext);
        m_pipelineId = AssetContentHash.Compute(content: m_fragmentBytecode.Span);
        m_vertexShader = m_shaderModuleFactory.Create(bytecode: m_vertexBytecode, deviceContext: m_deviceContext, stage: GpuShaderStage.Vertex);
        m_fragmentShader = m_shaderModuleFactory.Create(bytecode: m_fragmentBytecode, deviceContext: m_deviceContext, stage: GpuShaderStage.Fragment);
        m_vertexBuffer = m_vertexBufferFactory.Create(deviceContext: m_deviceContext, strideBytes: VertexStrideBytes, vertexData: FullscreenTriangleVertexData);
        m_dataBuffer = m_storageBufferFactory.Create(deviceContext: m_deviceContext, sizeBytes: (m_dataUintCount * sizeof(uint)));
        m_pipeline = m_pipelineFactory.Create(
            deviceContext: m_deviceContext,
            enableStorageBuffer: true,
            fragmentShaderModule: m_fragmentShader,
            height: m_height,
            pushConstantBinding: new GpuPushConstantBinding(data: new byte[PushConstantByteLength], offset: 0, stageFlags: GpuShaderStage.Fragment),
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
        m_descriptorSet = m_descriptorAllocator.AllocateSet(descriptorSetLayoutHandle: m_pipeline.DescriptorSetLayoutHandle, deviceHandle: deviceHandle, poolHandle: m_descriptorPool);
        m_sampler = m_descriptorAllocator.CreateSampler(deviceHandle: deviceHandle);
        m_descriptorAllocator.WriteStorageBuffer(binding: m_storageBufferBinding, bufferHandle: m_dataBuffer.BufferHandle, bufferSize: (m_dataUintCount * sizeof(uint)), descriptorSetHandle: m_descriptorSet, deviceHandle: deviceHandle);
        // The glyph atlas is static — upload it ONCE now (the front m_textOffsetUints uints of the scratch buffer).
        // Each produced frame then rewrites only the character-grid slice after it, never re-uploading the font. A
        // device-loss rebuild re-runs EnsureResources, so the atlas is re-seeded then too.
        m_dataBuffer.Write<uint>(data: m_scratch.AsSpan(start: 0, length: m_textOffsetUints));
        m_pipelines = new Dictionary<AssetContentHash, IGpuPipeline> {
            [m_pipelineId] = m_pipeline,
        };
        m_drawCommands = [
            new GpuDrawCommand(
                DescriptorSetHandle: m_descriptorSet,
                DrawParameters: new GpuDrawParameters(instanceCount: 1, vertexCount: VertexCount),
                PipelineId: m_pipelineId,
                PushConstants: new GpuPushConstantBinding(data: m_pushConstantData, offset: 0, stageFlags: GpuShaderStage.Fragment),
                VertexBufferHandle: m_vertexBuffer.BufferHandle
            ),
        ];
        m_resourcesReady = true;
    }
    private static byte[] CreateFullscreenTriangleVertexData() {
        var vertices = new (float X, float Y)[]
        {
            (-1f, -1f),
            (3f, -1f),
            (-1f, 3f),
        };
        var vertexData = new byte[(int)(VertexStrideBytes * vertices.Length)];

        for (var index = 0; (index < vertices.Length); index++) {
            var offset = (index * (int)VertexStrideBytes);

            _ = BitConverter.TryWriteBytes(destination: vertexData.AsSpan(length: sizeof(float), start: offset), value: vertices[index].X);
            _ = BitConverter.TryWriteBytes(destination: vertexData.AsSpan(length: sizeof(float), start: (offset + sizeof(float))), value: vertices[index].Y);
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
            m_descriptorAllocator.DestroySampler(deviceHandle: deviceHandle, samplerHandle: m_sampler);
            m_sampler = 0;
        }

        if (0 != m_descriptorPool) {
            m_descriptorAllocator.DestroyPool(deviceHandle: deviceHandle, poolHandle: m_descriptorPool);
            m_descriptorPool = 0;
            m_descriptorSet = 0;
        }

        m_pipeline?.Dispose();
        m_pipeline = null;
        m_pipelines = null;
        m_drawCommands = null;
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
