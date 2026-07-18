using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Assets;
using Puck.Capture;
using Puck.Compositing;
using Puck.Hosting;
using Puck.SdfVm;

namespace Puck.Overlays;

/// <summary>The read seams the unified overlay consumes, each optional (an absent source simply contributes no
/// records), bundled so the node's constructor stays at the proven decorator arity.</summary>
/// <param name="Console">The console-panel source, or <see langword="null"/>.</param>
/// <param name="BindingBar">The per-seat binding-bar source, or <see langword="null"/>.</param>
/// <param name="Toast">The transient-echo source, or <see langword="null"/>.</param>
/// <param name="FeedTick">Invoked once per produced frame, before the sources are snapshotted — the host's hook to
/// freshen pull-model feeds (e.g. recomposing the per-seat binding frame). Runs on the render thread.</param>
/// <param name="EditorHud">The per-seat editor-HUD source, or <see langword="null"/>.</param>
public sealed record UnifiedOverlaySources(
    IConsolePanelSource? Console,
    IBindingBarSource? BindingBar,
    IOverlayToastSource? Toast,
    Action? FeedTick,
    IEditorHudSource? EditorHud = null
);

/// <summary>
/// The ONE screen-space overlay decorator: wraps any same-device inner producer whose surface exposes a sampleable
/// image view, samples it in one fullscreen fragment pass, and draws every 2D surface on top from ONE storage
/// buffer — the design-token slab and the shared glyph SDF pack as a static prefix, then this frame's packed
/// records. SURFACES ARE WRITERS: the console panel, the per-seat binding bars, and the toast are each a small CPU
/// writer emitting the shared record vocabulary (panel chrome / rect / fixed-cell text run / icon chip) through
/// <see cref="OverlayFrameBuilder"/>, so a future surface is a new writer, never a new node or shader (the editor
/// HUD proved the seam). Backend-neutral from its first commit: only neutral <c>IGpu*</c> services (<see cref="OverlayServices"/>),
/// with bytecode selected by the caller.
/// </summary>
/// <remarks>
/// Keeps the proven overlay decorator contract whole: the per-node submission fence (the previous frame's pass must
/// retire before the buffer/descriptor rewrites), the pass-through fast path (nothing visible = the inner frame
/// returns untouched, no extra pass), and <see cref="ICaptureRequestTarget"/> forwarding (a pending capture lands on
/// whichever node actually produced the shown frame). Zero steady-state allocation: one preallocated scratch, one
/// reused push-constant array, records packed with <see cref="BitConverter.SingleToUInt32Bits"/>.
/// </remarks>
public sealed class UnifiedOverlayNode : IRenderNode, ICaptureRequestTarget {
    // counts float4 + sdf float4 + misc float4 — KEEP IN SYNC with overlay-unified.frag.hlsl's OverlayPassData.
    private const int PushConstantByteLength = ((sizeof(float) * 4) * 3);
    // The glyph outline halo width, in encoded signed-distance units — the SDF contrast band that keeps overlay text
    // legible over any world content, kept clear of the atlas' saturation floor at the overlay's screenPxRange.
    private const float OutlineBand = 0.20f;
    private const uint SamplerBinding = 0;
    private const uint VertexCount = 3;
    private const uint VertexStrideBytes = (sizeof(float) * 2);

    private static readonly byte[] FullscreenTriangleVertexData = CreateFullscreenTriangleVertexData();

    private readonly OverlayFrameBuilder m_builder;
    private readonly BindingBarWriter? m_bindingBarWriter;
    private readonly GpuCompositor m_compositor;
    private readonly ConsolePanelWriter? m_consoleWriter;
    private readonly Func<uint, uint, IGpuRenderTarget> m_createRenderTarget;
    private readonly IGpuDescriptorAllocator m_descriptorAllocator;
    private readonly NodeDescriptor m_descriptor;
    private readonly IGpuDeviceContext m_deviceContext;
    private readonly EditorHudWriter? m_editorHudWriter;
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
    private readonly ToastWriter? m_toastWriter;
    private readonly IGpuVertexBufferFactory m_vertexBufferFactory;
    private readonly ReadOnlyMemory<byte> m_vertexBytecode;
    private readonly uint m_width;
    private IGpuStorageBuffer? m_dataBuffer;
    private nint m_descriptorPool;
    private nint m_descriptorSet;
    private bool m_disposed;
    private GpuDrawCommand[]? m_drawCommands;
    // The per-frame submission fence (frame-ring discipline): this node's single command buffer / host-visible data
    // buffer / descriptor set may only be rewritten once its PREVIOUS submission retired. This pass is queued ahead
    // of the frame's heavy world submit, so by the next frame it has long retired and the wait is ~free.
    private IGpuSubmissionFence? m_frameFence;
    private IGpuShaderModule? m_fragmentShader;
    private nint m_lastImageViewHandle;
    private string? m_pendingCapturePath;
    private IGpuPipeline? m_pipeline;
    private AssetContentHash m_pipelineId;
    private IReadOnlyDictionary<AssetContentHash, IGpuPipeline>? m_pipelines;
    private IGpuSurfaceReadback? m_readback;
    private IGpuRenderTarget? m_renderTarget;
    private bool m_resourcesReady;
    private nint m_sampler;
    private IGpuShaderModule? m_vertexShader;
    private IGpuVertexBuffer? m_vertexBuffer;

    /// <summary>Initializes a new instance of the <see cref="UnifiedOverlayNode"/> class.</summary>
    /// <param name="inner">The producer whose render the overlay is drawn over (its surface must be sampleable here).</param>
    /// <param name="sources">The per-surface read seams + the feed tick.</param>
    /// <param name="glyphs">The ONE shared SDF glyph pack (per-glyph signed-distance cells).</param>
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

        m_builder = new OverlayFrameBuilder(glyphs: glyphs, height: height, width: width);
        m_bindingBarWriter = ((sources.BindingBar is { } bindingBar) ? new BindingBarWriter(source: bindingBar) : null);
        m_compositor = new GpuCompositor(commandRecorder: services.CommandRecorder);
        m_consoleWriter = ((sources.Console is { } console) ? new ConsolePanelWriter(source: console) : null);
        m_createRenderTarget = services.CreateRenderTarget;
        m_descriptor = new NodeDescriptor(Name: "unified-overlay", SurfaceId: SurfaceId.New());
        m_descriptorAllocator = services.DescriptorAllocator;
        m_deviceContext = services.DeviceContext;
        m_editorHudWriter = ((sources.EditorHud is { } editorHud) ? new EditorHudWriter(source: editorHud) : null);
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
        m_toastWriter = ((sources.Toast is { } toast) ? new ToastWriter(source: toast) : null);
        m_vertexBufferFactory = services.VertexBufferFactory;
        m_vertexBytecode = vertexBytecode;
        m_width = width;
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

        // The inner producer's same-device output is already transitioned shader-readable for the fragment stage
        // before its submit, so this same-queue pass samples it with no CPU wait.
        var inner = m_inner.ProduceFrame(context: context);

        if (inner.IsEmpty || (0 == inner.ImageViewHandle)) {
            ForwardPendingCapture();

            return inner;
        }

        // Freshen the pull-model feeds, then let each present writer pack this frame's records CPU-side. Nothing
        // visible = pass the frame through untouched (no extra pass).
        m_sources.FeedTick?.Invoke();
        m_builder.BeginFrame();
        m_consoleWriter?.Emit(builder: m_builder);
        m_bindingBarWriter?.Emit(builder: m_builder);
        m_editorHudWriter?.Emit(builder: m_builder);
        m_toastWriter?.Emit(builder: m_builder, renderTicks: context.RenderTicks);

        if (!m_builder.HasContent) {
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

        // Only the dynamic region changed this frame; the static token+glyph prefix was uploaded once in
        // EnsureResources — write just the panel/element/text slice at its byte offset.
        m_dataBuffer!.Write<uint>(
            data: m_builder.Scratch[m_builder.PanelBaseWords..],
            destinationOffsetBytes: (ulong)(m_builder.PanelBaseWords * sizeof(uint))
        );

        var commandBufferHandle = m_compositor.Record(
            debugLabel: "unified-overlay",
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

    // Not drawing this frame: hand a pending capture down the chain (the shared decorator forwarding contract) so
    // the readback lands on whatever actually produced the shown frame.
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

    // Reads back this node's own render target (the overlay composited over the world — what the player actually
    // sees) and writes it as a PNG: a new, separately-fenced submit sequenced after the draw above on the same queue.
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
        Console.Error.WriteLine(value: $"[capture] unified overlay -> {path}");
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
        floats[10] = 0f;
        floats[11] = 0f;
    }
    private void EnsureResources() {
        if (m_resourcesReady) {
            return;
        }

        m_renderTarget = m_createRenderTarget(arg1: m_width, arg2: m_height);
        m_frameFence = m_queueSubmitter.CreateSubmissionFence(deviceContext: m_deviceContext);
        m_pipelineId = AssetContentHash.Compute(content: m_fragmentBytecode.Span);
        m_vertexShader = m_shaderModuleFactory.Create(bytecode: m_vertexBytecode, deviceContext: m_deviceContext, stage: GpuShaderStage.Vertex);
        m_fragmentShader = m_shaderModuleFactory.Create(bytecode: m_fragmentBytecode, deviceContext: m_deviceContext, stage: GpuShaderStage.Fragment);
        m_vertexBuffer = m_vertexBufferFactory.Create(deviceContext: m_deviceContext, strideBytes: VertexStrideBytes, vertexData: FullscreenTriangleVertexData);
        m_dataBuffer = m_storageBufferFactory.Create(deviceContext: m_deviceContext, sizeBytes: ((uint)m_builder.WordCount * sizeof(uint)));
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
        m_descriptorAllocator.WriteStorageBuffer(binding: m_storageBufferBinding, bufferHandle: m_dataBuffer.BufferHandle, bufferSize: ((uint)m_builder.WordCount * sizeof(uint)), descriptorSetHandle: m_descriptorSet, deviceHandle: deviceHandle);
        // The token slab + glyph atlas are static — upload them ONCE now (the front PanelBaseWords uints); each
        // produced frame rewrites only the dynamic slice after them. A device-loss rebuild re-seeds them here.
        m_dataBuffer.Write<uint>(data: m_builder.Scratch[..m_builder.PanelBaseWords]);
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
