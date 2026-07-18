using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Assets;
using Puck.Capture;
using Puck.Compositing;
using Puck.Demo.DevConsole;
using Puck.Demo.Text;
using Puck.Hosting;
using Puck.SdfVm;

namespace Puck.Demo.Ui;

/// <summary>
/// The color-role table the overlay panels' packed records index — one entry per semantic role the surfaces use,
/// resolved to actual RGBA values inside the fragment shader. KEEP IN SYNC with ui-panels-overlay.frag.hlsl's
/// <c>ROLE_COLORS</c> table (the shader mirrors <see cref="DesignTokens.Color"/> as HLSL literals; this enum is the
/// shared index vocabulary between the two files).
/// </summary>
internal enum PanelColorRole : uint {
    TextPrimary = 0,
    TextDim = 1,
    TextMute = 2,
    Accent = 3,
    Positive = 4,
    Warning = 5,
    Danger = 6,
    Phosphor = 7,
    AccentInk = 8,
    SurfaceRaised = 9,
    SurfaceInset = 10,
    AccentQuiet = 11,
    /// <summary>The gallery plaque's kicker — the one sanctioned non-diegetic phosphor quote (docs/ui-design-tokens.md §4).</summary>
    PhosphorCyan = 12,
}

/// <summary>The read seams and control surfaces <see cref="OverlayPanelsNode"/> consumes, bundled so the node's
/// constructor stays at the proven decorator arity. <paramref name="FeedTick"/> is the per-frame publisher hook —
/// the composition wires <see cref="Overworld.OverlayPanelsFeed"/>'s tick here so the hub/tracker/gallery stores
/// are fresh before the node snapshots them (the toast store is event-driven and needs no tick).</summary>
/// <param name="Toast">The toast (last verb result) source.</param>
/// <param name="Hub">The hub-picker source.</param>
/// <param name="Tracker">The tracker-transport source.</param>
/// <param name="Gallery">The gallery-plaque source.</param>
/// <param name="Console">The console state source — the toast is suppressed while the console panel is open
/// (the console already shows results). Null = never suppress.</param>
/// <param name="Pointer">The pointer source driving title-band drags. Null = panels are not draggable.</param>
/// <param name="Control">The control store: master visibility + per-panel position overrides (verbs AND drags).</param>
/// <param name="FeedTick">Invoked once per produced frame, before the stores are snapshotted.</param>
internal sealed record OverlayPanelsSources(
    IToastSource Toast,
    IHubPanelSource Hub,
    ITrackerPanelSource Tracker,
    IGalleryPanelSource Gallery,
    IConsoleTextSource? Console,
    IPointerSource? Pointer,
    OverlayPanelsControlStore Control,
    Action? FeedTick
);

/// <summary>
/// The overlay-panels node — ONE Vulkan decorator that draws every north-star mockup surface that is not the console
/// or the binding bar: the toast/verb-echo chip, the hub picker, the tracker transport strip, and the gallery plaque
/// card. It keeps the proven single-pass overlay shape (one fullscreen fragment pass, ONE storage buffer, push
/// constants; the shared SDF glyph atlas packed at the buffer front with median-of-3 reconstruction) — the exact
/// architectural skeleton of <see cref="ConsoleOverlayNode"/>/<see cref="BindingBar.BindingBarOverlayNode"/>,
/// including the <see cref="ICaptureRequestTarget"/> forwarding chain. PANELS ARE DATA, SURFACES ARE CONTENT: the
/// node packs N panel records (token chrome — scrim fill, rounded rect, hairline, optional title band, optional
/// Tier-1 status ring/bloom) plus a flat element list (text runs into the shared atlas + rounded-rect cells); each
/// surface is a small CPU writer over its store snapshot, so a future surface is a new writer, not a new node or
/// shader. Every value routes through <see cref="DesignTokens"/> (the shader mirrors the colors as HLSL literals —
/// KEEP IN SYNC). Panels with a title band drag with the mouse (left-press in the band, clamped on screen) and
/// mirror to the <c>ui.panel.move</c>/<c>ui.panel.reset</c> verbs through the same override store.
/// </summary>
internal sealed class OverlayPanelsNode : IRenderNode, ICaptureRequestTarget {
    /// <summary>The draggable panels' stable names — the <c>ui.panel.*</c> verbs' vocabulary.</summary>
    public static readonly string[] DraggablePanelNames = ["hub", "tracker", "plaque"];

    // The packed-record geometry. KEEP IN SYNC with ui-panels-overlay.frag.hlsl (PANEL_WORDS/ELEMENT_WORDS and the
    // per-word layouts documented at its loops).
    private const int MaxPanels = 4;
    private const int ElementWords = 12;
    private const int MaxElements = 96;
    private const int PanelWords = 12;
    private const int TextWordCapacity = 1024;
    // counts float4 + sdf float4 + misc float4.
    private const int PushConstantByteLength = ((sizeof(float) * 4) * 3);
    // The glyph outline halo width, in encoded signed-distance units — same rationale as the console overlay's.
    private const float OutlineBand = 0.20f;
    // The toast's lifetime and fade, in DETERMINISTIC engine ticks (content tick, never wall clock): ~3 seconds
    // equivalent, with the trailing dur.med (DesignTokens.Motion.DurMed ms) as an opacity-only fade (text never
    // translates — motion stays calm per section 8).
    private const ulong ToastDurationTicks = (3UL * EngineTicks.PerSecond);

    private static readonly ulong ToastFadeTicks = (ulong)((DesignTokens.Motion.DurMed / 1000f) * EngineTicks.PerSecond);

    private const uint SamplerBinding = 0;
    private const uint VertexCount = 3;
    private const uint VertexStrideBytes = (sizeof(float) * 2);

    private static readonly byte[] FullscreenTriangleVertexData = CreateFullscreenTriangleVertexData();
    private readonly GpuCompositor m_compositor;
    private readonly Func<IGpuRenderTarget> m_createRenderTarget;
    private readonly uint m_dataUintCount;
    private readonly IGpuDescriptorAllocator m_descriptorAllocator;
    private readonly NodeDescriptor m_descriptor;
    private readonly IGpuDeviceContext m_deviceContext;
    private readonly int m_elementBaseWords;
    private readonly ReadOnlyMemory<byte> m_fragmentBytecode;
    private readonly SharedGlyphSdfPack m_glyphs;
    private readonly uint m_height;
    private readonly IRenderNode m_inner;
    // The live rects of this frame's draggable panels (name -> position/size/band height), used by NEXT frame's
    // drag hit-test — one frame of hit-target latency is imperceptible and avoids a layout/drag ordering cycle.
    private readonly Dictionary<string, PanelRect> m_liveRects = new(comparer: StringComparer.OrdinalIgnoreCase);
    private readonly int m_panelBaseWords;
    private readonly IGpuPipelineFactory m_pipelineFactory;
    // Rewritten in place each frame (the draw command holds one binding over this array for the node's lifetime).
    private readonly byte[] m_pushConstantData = new byte[PushConstantByteLength];
    private readonly IGpuQueueSubmitter m_queueSubmitter;
    private readonly uint[] m_scratch;
    private readonly IGpuShaderModuleFactory m_shaderModuleFactory;
    private readonly OverlayPanelsSources m_sources;
    private readonly uint m_storageBufferBinding;
    private readonly IGpuStorageBufferFactory m_storageBufferFactory;
    private readonly IGpuSurfaceTransferFactory m_surfaceTransferFactory;
    private readonly int m_textBaseWords;
    private readonly IGpuVertexBufferFactory m_vertexBufferFactory;
    private readonly ReadOnlyMemory<byte> m_vertexBytecode;
    private readonly uint m_width;
    private nint m_descriptorPool;
    private nint m_descriptorSet;
    private bool m_disposed;
    private string? m_dragName;
    private Vector2 m_dragOffset;
    private IGpuStorageBuffer? m_dataBuffer;
    private GpuDrawCommand[]? m_drawCommands;
    private int m_elementCount;
    private IGpuShaderModule? m_fragmentShader;
    private ulong m_lastLeftPressCount;
    private nint m_lastImageViewHandle;
    private int m_panelCount;
    private string? m_pendingCapturePath;
    private IGpuPipeline? m_pipeline;
    private AssetContentHash m_pipelineId;
    private IReadOnlyDictionary<AssetContentHash, IGpuPipeline>? m_pipelines;
    private IGpuSurfaceReadback? m_readback;
    // The per-frame submission fence (frame-ring discipline): with the host's per-frame device drain gone, this
    // node's single command buffer / host-visible data buffer / descriptor set may only be rewritten once its
    // PREVIOUS submission retired. This pass is queued ahead of the frame's heavy world submit, so by the next
    // frame it has long retired and the wait is ~free.
    private IGpuSubmissionFence? m_frameFence;
    private IGpuRenderTarget? m_renderTarget;
    private bool m_resourcesReady;
    private nint m_sampler;
    private int m_textWordCount;
    private ulong m_toastFirstTicks;
    private int m_toastSequenceSeen;
    private IGpuShaderModule? m_vertexShader;
    private IGpuVertexBuffer? m_vertexBuffer;

    private readonly record struct PanelRect(Vector2 Position, float Width, float Height, float BandHeight);

    /// <summary>Initializes a new instance of the <see cref="OverlayPanelsNode"/> class.</summary>
    /// <param name="inner">The producer whose render the panels are drawn over (its surface must be sampleable here).</param>
    /// <param name="sources">The per-surface read seams + the control store + the feed tick.</param>
    /// <param name="glyphs">The ONE shared SDF glyph pack (per-glyph signed-distance cells).</param>
    /// <param name="services">The producer's neutral GPU service bundle (same device).</param>
    /// <param name="vertexBytecode">The fullscreen vertex shader.</param>
    /// <param name="fragmentBytecode">The overlay-panels fragment shader.</param>
    /// <param name="width">The render width in pixels.</param>
    /// <param name="height">The render height in pixels.</param>
    public OverlayPanelsNode(
        IRenderNode inner,
        OverlayPanelsSources sources,
        SharedGlyphSdfPack glyphs,
        SdfProducerServices services,
        ReadOnlyMemory<byte> vertexBytecode,
        ReadOnlyMemory<byte> fragmentBytecode,
        uint width,
        uint height
    ) {
        ArgumentNullException.ThrowIfNull(glyphs);
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(sources);

        m_compositor = new GpuCompositor(commandRecorder: services.CommandRecorder);
        m_createRenderTarget = services.CreateRenderTarget;
        m_descriptor = new NodeDescriptor(Name: "overlay-panels", SurfaceId: SurfaceId.New());
        m_descriptorAllocator = services.DescriptorAllocator;
        m_deviceContext = services.DeviceContext;
        m_fragmentBytecode = fragmentBytecode;
        m_glyphs = glyphs;
        m_height = height;
        m_inner = inner;
        m_pipelineFactory = services.PipelineFactory;
        m_queueSubmitter = services.QueueSubmitter;
        m_shaderModuleFactory = services.ShaderModuleFactory;
        m_sources = sources;
        m_storageBufferBinding = services.StorageBufferBinding;
        m_storageBufferFactory = services.StorageBufferFactory;
        m_surfaceTransferFactory = services.SurfaceTransferFactory;
        m_vertexBufferFactory = services.VertexBufferFactory;
        m_vertexBytecode = vertexBytecode;
        m_width = width;

        // ONE storage buffer: the static atlas pack at the front (uploaded once), then the per-frame panel records,
        // element records, and glyph-code words — the console overlay's atlas-prefix shape.
        m_panelBaseWords = glyphs.PackedSdf.Count;
        m_elementBaseWords = (m_panelBaseWords + (MaxPanels * PanelWords));
        m_textBaseWords = (m_elementBaseWords + (MaxElements * ElementWords));
        m_dataUintCount = (uint)(m_textBaseWords + TextWordCapacity);
        m_scratch = new uint[m_dataUintCount];

        for (var index = 0; (index < glyphs.PackedSdf.Count); index++) {
            m_scratch[index] = glyphs.PackedSdf[index];
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

        // Freshen the store snapshots (the hub/tracker/gallery feed polls their live state), then pack this frame's
        // panels CPU-side. Nothing visible (master off, all surfaces idle) = pass the frame through untouched.
        m_sources.FeedTick?.Invoke();
        PackPanels(renderTicks: context.RenderTicks);

        if (m_panelCount == 0) {
            ForwardPendingCapture();

            return inner;
        }

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

        FillPushConstants();

        // Only the dynamic region changed this frame; the static glyph atlas prefix was uploaded once in
        // EnsureResources — write just the panel/element/text slice at its byte offset.
        m_dataBuffer!.Write<uint>(
            data: m_scratch.AsSpan(start: m_panelBaseWords, length: ((int)m_dataUintCount - m_panelBaseWords)),
            destinationOffsetBytes: (ulong)(m_panelBaseWords * sizeof(uint))
        );

        var commandBufferHandle = m_compositor.Record(
            debugLabel: "overlay",
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

        CaptureIfPending();

        return new Surface(
            Format: SurfaceFormat.R8G8B8A8Unorm,
            Height: m_height,
            ImageViewHandle: m_renderTarget!.ImageViewHandle,
            Width: m_width
        );
    }

    // Not drawing this frame: hand a pending capture down the chain (the same forwarding contract the console and
    // binding-bar decorators keep) so the readback lands on whatever actually produced the shown frame.
    private void ForwardPendingCapture() {
        if (m_pendingCapturePath is not { } path) {
            return;
        }

        m_pendingCapturePath = null;

        if (m_inner is ICaptureRequestTarget target) {
            target.RequestCapture(path: path);
        } else if (m_inner is SdfEngineNode producer) {
            producer.RequestCapture(path: path);
        }
    }

    // Reads back this node's own render target (the panels composited over the world) and writes it as a PNG —
    // the runtime sibling of the console overlay's readback, one decorator lower.
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
        Console.Error.WriteLine(value: $"[capture] overlay panels -> {path}");
    }

    // ---- per-frame packing (panels are data; each surface below is a WRITER into the shared records) --------------

    private void PackPanels(ulong renderTicks) {
        // The drag runs against LAST frame's rects, then the builders lay out fresh (reading any override the drag
        // just wrote), so a dragged panel follows the pointer with at most one frame of hit-target latency.
        UpdateDrag();

        m_panelCount = 0;
        m_elementCount = 0;
        m_textWordCount = 0;
        m_liveRects.Clear();
        Array.Clear(array: m_scratch, index: m_panelBaseWords, length: ((int)m_dataUintCount - m_panelBaseWords));

        if (!m_sources.Control.PanelsVisible) {
            return;
        }

        BuildToast(renderTicks: renderTicks);
        BuildHub();
        BuildTracker();
        BuildGallery();
    }

    // TOAST / VERB ECHO (mid-right): the last console verb result as a transient Tier-1 chip — scrim.chip fill,
    // bloom ring+halo and a 2px state rail in the [OK]/[ERR] hue, an inset icon square, one line of text. Expires on
    // CONTENT TICK (~3s equivalent), fades over dur.med, and is suppressed while the console panel is open.
    private void BuildToast(ulong renderTicks) {
        if (!m_sources.Toast.TrySnapshot(frame: out var toast) || (toast.Message.Length == 0)) {
            return;
        }

        if ((m_sources.Console is { } consoleSource) && consoleSource.TrySnapshot(frame: out var consoleFrame) && consoleFrame.Visible) {
            return;
        }

        if (toast.Sequence != m_toastSequenceSeen) {
            m_toastSequenceSeen = toast.Sequence;
            m_toastFirstTicks = renderTicks;
        }

        var age = (renderTicks - m_toastFirstTicks);

        if (age >= ToastDurationTicks) {
            return;
        }

        // Opacity-only exit (text never translates): full until the trailing dur.med window, then a linear fade.
        var remaining = (ToastDurationTicks - age);
        var alpha = ((remaining >= ToastFadeTicks) ? 1f : ((float)remaining / ToastFadeTicks));
        var stateRole = (toast.IsError ? PanelColorRole.Danger : PanelColorRole.Positive);
        var monoCell = CellHeight(sizePx: DesignTokens.Type.TypeMonoSize);
        var microCell = CellHeight(sizePx: DesignTokens.Type.TypeMicroSize);
        var message = Clip(text: toast.Message, maxChars: 44);
        var icon = DesignTokens.Space.HeightBadge;
        var panelH = DesignTokens.Space.HeightChip;
        var panelW = ((((DesignTokens.Space.Space3 + icon) + DesignTokens.Space.Space2) + TextWidth(chars: message.Length, cellHeight: monoCell)) + DesignTokens.Space.Space3);
        var x = ((m_width - panelW) - DesignTokens.Space.Space8);
        var y = ((m_height * 0.5f) - (panelH * 0.5f));   // mid-right anchor per the mockup

        WritePanel(x: x, y: y, w: panelW, h: panelH, titleBand: false, bandHeight: 0f, styleKind: 2u, ringRole: (uint)stateRole, alpha: alpha);
        // The 2px state rail hugging the left edge, inset past the corner radius (the edge-width law's third
        // sanctioned 2px signal).
        WriteRect(x: x, y: (y + DesignTokens.Radius.Radius2), w: DesignTokens.Elevation.RingStatusWidth, h: (panelH - (2f * DesignTokens.Radius.Radius2)), role: stateRole, radius: 0f, alpha: alpha);

        var iconX = (x + DesignTokens.Space.Space3);
        var iconY = (y + ((panelH - icon) * 0.5f));

        WriteRect(x: iconX, y: iconY, w: icon, h: icon, role: PanelColorRole.SurfaceInset, radius: DesignTokens.Radius.Radius1, alpha: alpha);

        var label = (toast.IsError ? "ER" : "OK");

        WriteText(
            alpha: alpha,
            cellHeight: microCell,
            role: stateRole,
            text: label,
            x: (iconX + ((icon - TextWidth(chars: label.Length, cellHeight: microCell)) * 0.5f)),
            y: (iconY + ((icon - microCell) * 0.5f))
        );
        WriteText(
            alpha: alpha,
            cellHeight: monoCell,
            role: PanelColorRole.TextPrimary,
            text: message,
            x: ((iconX + icon) + DesignTokens.Space.Space2),
            y: (y + ((panelH - monoCell) * 0.5f))
        );
    }

    // HUB PICKER (top-right): 'HUB · <MODE>' title band + one row per authoring mode, the selection carrying the
    // surface.raised fill, the 2px accent leading tick, and an accent index digit; index digits right-aligned.
    private void BuildHub() {
        if (!m_sources.Hub.TrySnapshot(frame: out var hub) || !hub.Active || (hub.Labels.Count == 0)) {
            return;
        }

        var titleCell = CellHeight(sizePx: DesignTokens.Type.TypeTitleSize);
        var bodyCell = CellHeight(sizePx: DesignTokens.Type.TypeBodySize);
        var microCell = CellHeight(sizePx: DesignTokens.Type.TypeMicroSize);
        var bandH = DesignTokens.Space.HeightConsoleHead;
        var rowH = DesignTokens.Space.HeightModeRow;
        var selection = Math.Clamp(value: hub.Selection, min: 0, max: (hub.Labels.Count - 1));
        var maxLabelChars = 0;

        for (var index = 0; (index < hub.Labels.Count); index++) {
            maxLabelChars = Math.Max(val1: maxLabelChars, val2: hub.Labels[index].Length);
        }

        var rowContentW = ((((((DesignTokens.Space.Space3 + DesignTokens.Elevation.RingStatusWidth) + DesignTokens.Space.Space2)
            + TextWidth(chars: maxLabelChars, cellHeight: bodyCell)) + DesignTokens.Space.Space5) + TextWidth(chars: 1, cellHeight: bodyCell)) + DesignTokens.Space.Space3);
        var bandContentW = ((((DesignTokens.Space.Space3 + TextWidth(chars: 3, cellHeight: microCell)) + DesignTokens.Space.Space2)
            + TextWidth(chars: hub.Labels[selection].Length, cellHeight: titleCell)) + DesignTokens.Space.Space3);
        var w = MathF.Max(x: rowContentW, y: bandContentW);
        var h = (((bandH + DesignTokens.Space.Space3) + (hub.Labels.Count * rowH)) + DesignTokens.Space.Space3);
        var position = ResolvePosition(name: "hub", defaultX: ((m_width - w) - DesignTokens.Space.Space8), defaultY: DesignTokens.Space.Space8, w: w, h: h, bandHeight: bandH);

        WritePanel(x: position.X, y: position.Y, w: w, h: h, titleBand: true, bandHeight: bandH, styleKind: 0u, ringRole: 0u, alpha: 1f);
        // The band: 'HUB' eyebrow (micro/dim) then the selected mode's label (title/primary).
        WriteText(alpha: 1f, cellHeight: microCell, role: PanelColorRole.TextDim, text: "HUB", x: (position.X + DesignTokens.Space.Space3), y: (position.Y + ((bandH - microCell) * 0.5f)));
        WriteText(
            alpha: 1f,
            cellHeight: titleCell,
            role: PanelColorRole.TextPrimary,
            text: hub.Labels[selection],
            x: (((position.X + DesignTokens.Space.Space3) + TextWidth(chars: 3, cellHeight: microCell)) + DesignTokens.Space.Space2),
            y: (position.Y + ((bandH - titleCell) * 0.5f))
        );

        for (var index = 0; (index < hub.Labels.Count); index++) {
            var rowY = (((position.Y + bandH) + DesignTokens.Space.Space3) + (index * rowH));
            var selected = (index == selection);

            if (selected) {
                // Hub mode selected (Tier 1 recipe): surface.raised fill + the 2px accent leading tick.
                WriteRect(
                    alpha: 1f,
                    h: (rowH - 2f),
                    radius: DesignTokens.Radius.Radius1,
                    role: PanelColorRole.SurfaceRaised,
                    w: (w - (2f * DesignTokens.Space.Space2)),
                    x: (position.X + DesignTokens.Space.Space2),
                    y: (rowY + 1f)
                );
                WriteRect(
                    alpha: 1f,
                    h: (rowH - (2f * DesignTokens.Space.Space1)),
                    radius: 0f,
                    role: PanelColorRole.Accent,
                    w: DesignTokens.Elevation.RingStatusWidth,
                    x: (position.X + DesignTokens.Space.Space2),
                    y: (rowY + DesignTokens.Space.Space1)
                );
            }

            WriteText(
                alpha: 1f,
                cellHeight: bodyCell,
                role: (selected ? PanelColorRole.TextPrimary : PanelColorRole.TextDim),
                text: hub.Labels[index],
                x: (((position.X + DesignTokens.Space.Space3) + DesignTokens.Elevation.RingStatusWidth) + DesignTokens.Space.Space2),
                y: (rowY + ((rowH - bodyCell) * 0.5f))
            );

            var digit = index.ToString(provider: System.Globalization.CultureInfo.InvariantCulture);

            WriteText(
                alpha: 1f,
                cellHeight: bodyCell,
                role: (selected ? PanelColorRole.Accent : PanelColorRole.TextMute),
                text: digit,
                x: (((position.X + w) - DesignTokens.Space.Space3) - TextWidth(chars: digit.Length, cellHeight: bodyCell)),
                y: (rowY + ((rowH - bodyCell) * 0.5f))
            );
        }
    }

    // TRACKER TRANSPORT STRIP (above the binding bar): play-state glyph, 'PTN xx - row:rows' position readout
    // (mono-readout size), BPM, and the working document's name.
    private void BuildTracker() {
        if (!m_sources.Tracker.TrySnapshot(frame: out var tracker) || !tracker.Active) {
            return;
        }

        var readoutCell = CellHeight(sizePx: DesignTokens.Type.TypeMonoReadoutSize);
        var microCell = CellHeight(sizePx: DesignTokens.Type.TypeMicroSize);
        var monoCell = CellHeight(sizePx: DesignTokens.Type.TypeMonoSize);
        var bandH = DesignTokens.Space.HeightModeRow;
        var stripH = DesignTokens.Space.HeightTrackerBar;
        var h = (bandH + stripH);
        // Frames-per-row -> beats/min: a row is an eighth note (half a beat) at the framework's 60 fps, so
        // BPM = 3600 frames-per-minute / (2 rows x tempo frames) = 1800 / tempo.
        var bpm = (1800 / Math.Max(val1: 1, val2: tracker.Tempo));
        var playGlyph = (tracker.Playing ? ">" : "#");
        var readout = $"PTN {(tracker.Pattern + 1).ToString(format: "00", provider: System.Globalization.CultureInfo.InvariantCulture)} - {tracker.Row.ToString(format: "00", provider: System.Globalization.CultureInfo.InvariantCulture)}:{tracker.RowCount.ToString(format: "00", provider: System.Globalization.CultureInfo.InvariantCulture)}";
        var bpmValue = bpm.ToString(provider: System.Globalization.CultureInfo.InvariantCulture);
        var name = Clip(text: tracker.Name, maxChars: 16);
        var w = ((((((((((DesignTokens.Space.Space3
            + TextWidth(chars: playGlyph.Length, cellHeight: readoutCell)) + DesignTokens.Space.Space3)
            + TextWidth(chars: readout.Length, cellHeight: readoutCell)) + DesignTokens.Space.Space5)
            + TextWidth(chars: 3, cellHeight: microCell)) + DesignTokens.Space.Space2) + TextWidth(chars: bpmValue.Length, cellHeight: readoutCell)) + DesignTokens.Space.Space5)
            + TextWidth(chars: name.Length, cellHeight: monoCell)) + DesignTokens.Space.Space3);
        // Above the binding-bar band: the bar cluster hugs the bottom stage margin, so the strip sits one gutter
        // above the bar's token height.
        var position = ResolvePosition(
            bandHeight: bandH,
            defaultX: DesignTokens.Space.Space8,
            defaultY: ((((m_height - DesignTokens.Space.HeightBindBar) - DesignTokens.Space.Space8) - DesignTokens.Space.Space5) - h),
            h: h,
            name: "tracker",
            w: w
        );

        WritePanel(x: position.X, y: position.Y, w: w, h: h, titleBand: true, bandHeight: bandH, styleKind: 1u, ringRole: 0u, alpha: 1f);
        WriteText(alpha: 1f, cellHeight: microCell, role: PanelColorRole.TextDim, text: "TRACKER", x: (position.X + DesignTokens.Space.Space3), y: (position.Y + ((bandH - microCell) * 0.5f)));

        var contentY = (position.Y + bandH);
        var textY = (contentY + ((stripH - readoutCell) * 0.5f));
        var cursor = (position.X + DesignTokens.Space.Space3);

        // Play state (Tier-1 accent while playing — the tracker play/hit is one of the accent's sanctioned homes).
        WriteText(alpha: 1f, cellHeight: readoutCell, role: (tracker.Playing ? PanelColorRole.Accent : PanelColorRole.TextDim), text: playGlyph, x: cursor, y: textY);
        cursor += (TextWidth(chars: playGlyph.Length, cellHeight: readoutCell) + DesignTokens.Space.Space3);
        WriteText(alpha: 1f, cellHeight: readoutCell, role: PanelColorRole.TextPrimary, text: readout, x: cursor, y: textY);
        cursor += (TextWidth(chars: readout.Length, cellHeight: readoutCell) + DesignTokens.Space.Space5);
        WriteText(alpha: 1f, cellHeight: microCell, role: PanelColorRole.TextDim, text: "BPM", x: cursor, y: (contentY + ((stripH - microCell) * 0.5f)));
        cursor += (TextWidth(chars: 3, cellHeight: microCell) + DesignTokens.Space.Space2);
        WriteText(alpha: 1f, cellHeight: readoutCell, role: PanelColorRole.TextPrimary, text: bpmValue, x: cursor, y: textY);
        cursor += (TextWidth(chars: bpmValue.Length, cellHeight: readoutCell) + DesignTokens.Space.Space5);
        WriteText(alpha: 1f, cellHeight: monoCell, role: PanelColorRole.TextDim, text: name, x: cursor, y: (contentY + ((stripH - monoCell) * 0.5f)));
    }

    // GALLERY PLAQUE CARD (lower-right): the current exhibit's kicker band (phosphor.cyan — the sanctioned gallery
    // quote), title, and metadata line.
    private void BuildGallery() {
        if (!m_sources.Gallery.TrySnapshot(frame: out var gallery) || !gallery.Active) {
            return;
        }

        var titleCell = CellHeight(sizePx: DesignTokens.Type.TypeTitleSize);
        var microCell = CellHeight(sizePx: DesignTokens.Type.TypeMicroSize);
        var monoCell = CellHeight(sizePx: DesignTokens.Type.TypeMonoSize);
        var bandH = DesignTokens.Space.HeightModeRow;
        var kicker = "GALLERY";
        var title = Clip(text: gallery.Title, maxChars: 36);
        var meta = Clip(text: gallery.Meta, maxChars: 40);
        var w = ((2f * DesignTokens.Space.Space3) + MathF.Max(
            x: TextWidth(chars: kicker.Length, cellHeight: microCell),
            y: MathF.Max(x: TextWidth(chars: title.Length, cellHeight: titleCell), y: TextWidth(chars: meta.Length, cellHeight: monoCell))
        ));
        var h = (((((bandH + DesignTokens.Space.Space3) + titleCell) + DesignTokens.Space.Space1) + monoCell) + DesignTokens.Space.Space3);
        var position = ResolvePosition(
            bandHeight: bandH,
            defaultX: ((m_width - w) - DesignTokens.Space.Space8),
            defaultY: MathF.Round(x: (m_height * 0.60f)),   // lower-right anchor per the mockup
            h: h,
            name: "plaque",
            w: w
        );

        WritePanel(x: position.X, y: position.Y, w: w, h: h, titleBand: true, bandHeight: bandH, styleKind: 1u, ringRole: 0u, alpha: 1f);
        WriteText(alpha: 1f, cellHeight: microCell, role: PanelColorRole.PhosphorCyan, text: kicker, x: (position.X + DesignTokens.Space.Space3), y: (position.Y + ((bandH - microCell) * 0.5f)));
        WriteText(alpha: 1f, cellHeight: titleCell, role: PanelColorRole.TextPrimary, text: title, x: (position.X + DesignTokens.Space.Space3), y: ((position.Y + bandH) + DesignTokens.Space.Space3));
        WriteText(alpha: 1f, cellHeight: monoCell, role: PanelColorRole.TextDim, text: meta, x: (position.X + DesignTokens.Space.Space3), y: ((((position.Y + bandH) + DesignTokens.Space.Space3) + titleCell) + DesignTokens.Space.Space1));
    }

    // ---- drag (title-band panels only; verbs and drags share the SAME override store) -----------------------------

    private void UpdateDrag() {
        if ((m_sources.Pointer is not { } pointer) || !pointer.TrySnapshot(frame: out var frame)) {
            return;
        }

        if (m_dragName is { } dragging) {
            if (!frame.Left.Held) {
                m_dragName = null;
            } else if (m_liveRects.TryGetValue(key: dragging, value: out var rect)) {
                var next = (frame.Position - m_dragOffset);

                m_sources.Control.SetOverride(panelName: dragging, position: ClampToScreen(position: next, w: rect.Width, h: rect.Height));
            }
        } else if (frame.Left.PressCount > m_lastLeftPressCount) {
            // A fresh left press: hit-test the visible title bands (last frame's layout — one frame of latency).
            foreach (var (name, rect) in m_liveRects) {
                if ((frame.Position.X >= rect.Position.X) && (frame.Position.X < (rect.Position.X + rect.Width))
                    && (frame.Position.Y >= rect.Position.Y) && (frame.Position.Y < (rect.Position.Y + rect.BandHeight))) {
                    m_dragName = name;
                    m_dragOffset = (frame.Position - rect.Position);

                    break;
                }
            }
        }

        m_lastLeftPressCount = frame.Left.PressCount;
    }

    // A panel's live position: its verb/drag override when one is set, else its anchored default — always clamped on
    // screen, and recorded for the next frame's drag hit-test.
    private Vector2 ResolvePosition(string name, float defaultX, float defaultY, float w, float h, float bandHeight) {
        var position = (m_sources.Control.TryGetOverride(panelName: name, position: out var moved)
            ? moved
            : new Vector2(x: defaultX, y: defaultY));

        position = ClampToScreen(position: position, w: w, h: h);
        m_liveRects[name] = new PanelRect(BandHeight: bandHeight, Height: h, Position: position, Width: w);

        return position;
    }
    private Vector2 ClampToScreen(Vector2 position, float w, float h) => new(
        x: Math.Clamp(value: position.X, min: 0f, max: MathF.Max(x: 0f, y: (m_width - w))),
        y: Math.Clamp(value: position.Y, min: 0f, max: MathF.Max(x: 0f, y: (m_height - h)))
    );

    // ---- record writers (word layouts: KEEP IN SYNC with ui-panels-overlay.frag.hlsl) -----------------------------

    private void WritePanel(float x, float y, float w, float h, bool titleBand, float bandHeight, uint styleKind, uint ringRole, float alpha) {
        if (m_panelCount >= MaxPanels) {
            return;
        }

        var offset = (m_panelBaseWords + (m_panelCount * PanelWords));

        m_scratch[(offset + 0)] = BitConverter.SingleToUInt32Bits(value: x);
        m_scratch[(offset + 1)] = BitConverter.SingleToUInt32Bits(value: y);
        m_scratch[(offset + 2)] = BitConverter.SingleToUInt32Bits(value: w);
        m_scratch[(offset + 3)] = BitConverter.SingleToUInt32Bits(value: h);
        m_scratch[(offset + 4)] = (titleBand ? 1u : 0u);
        m_scratch[(offset + 5)] = styleKind;
        m_scratch[(offset + 6)] = ringRole;
        m_scratch[(offset + 7)] = BitConverter.SingleToUInt32Bits(value: bandHeight);
        m_scratch[(offset + 8)] = BitConverter.SingleToUInt32Bits(value: alpha);
        m_panelCount++;
    }
    private void WriteRect(float x, float y, float w, float h, PanelColorRole role, float radius, float alpha) {
        if (m_elementCount >= MaxElements) {
            return;
        }

        var offset = (m_elementBaseWords + (m_elementCount * ElementWords));

        m_scratch[(offset + 0)] = BitConverter.SingleToUInt32Bits(value: x);
        m_scratch[(offset + 1)] = BitConverter.SingleToUInt32Bits(value: y);
        m_scratch[(offset + 2)] = BitConverter.SingleToUInt32Bits(value: w);
        m_scratch[(offset + 3)] = BitConverter.SingleToUInt32Bits(value: h);
        m_scratch[(offset + 4)] = 1u | ((uint)role << 4);
        m_scratch[(offset + 6)] = BitConverter.SingleToUInt32Bits(value: radius);
        m_scratch[(offset + 7)] = BitConverter.SingleToUInt32Bits(value: alpha);
        m_elementCount++;
    }
    private void WriteText(float x, float y, string text, int cellHeight, PanelColorRole role, float alpha) {
        if ((m_elementCount >= MaxElements) || (text.Length == 0) || ((m_textWordCount + text.Length) > TextWordCapacity)) {
            return;
        }

        var start = m_textWordCount;

        // Codes are stored PRE-RESOLVED as atlas glyph indices; anything outside printable ASCII renders as the
        // blank space cell (index 0) rather than a wrong glyph.
        for (var index = 0; (index < text.Length); index++) {
            var glyph = SharedGlyphSdfPack.GlyphIndex(codePoint: text[index]);

            m_scratch[(m_textBaseWords + m_textWordCount++)] = (uint)Math.Max(val1: 0, val2: glyph);
        }

        var offset = (m_elementBaseWords + (m_elementCount * ElementWords));

        m_scratch[(offset + 0)] = BitConverter.SingleToUInt32Bits(value: x);
        m_scratch[(offset + 1)] = BitConverter.SingleToUInt32Bits(value: y);
        m_scratch[(offset + 2)] = BitConverter.SingleToUInt32Bits(value: CellWidth(cellHeight: cellHeight));
        m_scratch[(offset + 3)] = BitConverter.SingleToUInt32Bits(value: cellHeight);
        m_scratch[(offset + 4)] = ((uint)role << 4);
        m_scratch[(offset + 5)] = (uint)start;
        m_scratch[(offset + 6)] = (uint)text.Length;
        m_scratch[(offset + 7)] = BitConverter.SingleToUInt32Bits(value: alpha);
        m_elementCount++;
    }

    // On-screen glyph cell height for a token type SIZE: the console overlay's proven size->cell ratio
    // (TypeMonoLine / TypeMonoSize = 1.5), so a 12px mono run gets the console's exact 18px cell.
    private static int CellHeight(float sizePx) =>
        Math.Max(val1: 1, val2: (int)MathF.Round(x: (sizePx * (DesignTokens.Type.TypeMonoLine / DesignTokens.Type.TypeMonoSize))));

    // On-screen glyph cell width for a cell height, preserving the shared atlas' cell aspect.
    private float CellWidth(int cellHeight) =>
        MathF.Max(x: 1f, y: MathF.Round(x: ((cellHeight * (float)m_glyphs.AtlasCellWidth) / m_glyphs.AtlasCellHeight)));
    private float TextWidth(int chars, int cellHeight) => (chars * CellWidth(cellHeight: cellHeight));
    private static string Clip(string text, int maxChars) {
        var line = text;
        var newline = line.IndexOfAny(anyOf: ['\r', '\n']);

        if (newline >= 0) {
            line = line[..newline];
        }

        return ((line.Length <= maxChars) ? line : (line[..(maxChars - 2)] + ".."));
    }
    private void FillPushConstants() {
        var floats = MemoryMarshal.Cast<byte, float>(span: m_pushConstantData.AsSpan());

        // counts / sdf / misc — KEEP IN SYNC with the shader's PanelPassData.
        floats[0] = m_panelCount;
        floats[1] = m_elementCount;
        floats[2] = m_glyphs.AtlasCellWidth;
        floats[3] = m_glyphs.AtlasCellHeight;
        floats[4] = m_glyphs.DistanceRange;
        floats[5] = OutlineBand;
        floats[6] = m_panelBaseWords;
        floats[7] = m_elementBaseWords;
        floats[8] = m_textBaseWords;
        floats[9] = 0f;
        floats[10] = 0f;
        floats[11] = 0f;
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
        // The glyph atlas is static — upload it ONCE now (the front m_panelBaseWords uints of the scratch buffer);
        // each produced frame rewrites only the dynamic slice after it. A device-loss rebuild re-seeds it here.
        m_dataBuffer.Write<uint>(data: m_scratch.AsSpan(start: 0, length: m_panelBaseWords));
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
