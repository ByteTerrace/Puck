using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Assets;
using Puck.Compositing;
using Puck.Hosting;

namespace Puck.Demo.DevConsole;

/// <summary>
/// The on-screen developer-console overlay: wraps a same-device inner producer, samples it in one fullscreen
/// fragment pass, and draws the console panel (a translucent backing plus the input line and recent output) on top,
/// in a fixed monospace grid. The glyph atlas AND the per-frame character grid both ride ONE host-visible storage
/// buffer (the font packed once at the front, the text cells after it), so the overlay keeps the proven
/// single-combined-image-sampler + one-storage-buffer shape of the binding bar — no second texture binding. When the
/// console is closed the pass is skipped entirely (the frame passes through untouched). Vulkan-only, exactly like the
/// binding-bar overlay it mirrors.
/// </summary>
internal sealed class ConsoleOverlayNode : IRenderNode {
    private const int Margin = 14;
    // Header float4s: (panelX, panelY, panelW, panelH) px · (cols, rows, cellW, cellH) · (textColor.rgb, panelAlpha) ·
    // (cursorCol, cursorRow, textCellUintOffset, firstChar).
    private const int PushConstantByteLength = ((sizeof(float) * 4) * 4);
    private const uint SamplerBinding = 0;
    private const uint VertexCount = 3;
    private const uint VertexStrideBytes = (sizeof(float) * 2);

    private static readonly byte[] FullscreenTriangleVertexData = CreateFullscreenTriangleVertexData();

    private readonly int m_cols;
    private readonly GpuCompositor m_compositor;
    private readonly Func<IGpuRenderTarget> m_createRenderTarget;
    private readonly uint m_dataUintCount;
    private readonly IGpuDescriptorAllocator m_descriptorAllocator;
    private readonly NodeDescriptor m_descriptor;
    private readonly IGpuDeviceContext m_deviceContext;
    private readonly ReadOnlyMemory<byte> m_fragmentBytecode;
    private readonly ConsoleGlyphFont m_font;
    private readonly uint m_height;
    private readonly IRenderNode m_inner;
    private readonly IGpuPipelineFactory m_pipelineFactory;
    private readonly IGpuQueueSubmitter m_queueSubmitter;
    private readonly int m_rows;
    private readonly uint[] m_scratch;
    private readonly IGpuShaderModuleFactory m_shaderModuleFactory;
    private readonly IConsoleTextSource m_source;
    private readonly uint m_storageBufferBinding;
    private readonly IGpuStorageBufferFactory m_storageBufferFactory;
    private readonly int m_textOffsetUints;
    private readonly IGpuVertexBufferFactory m_vertexBufferFactory;
    private readonly ReadOnlyMemory<byte> m_vertexBytecode;
    private readonly uint m_width;
    private nint m_descriptorPool;
    private nint m_descriptorSet;
    private bool m_disposed;
    private IGpuStorageBuffer? m_dataBuffer;
    private GpuDrawCommand[]? m_drawCommands;
    private IGpuShaderModule? m_fragmentShader;
    private nint m_lastImageViewHandle;
    private IGpuPipeline? m_pipeline;
    private AssetContentHash m_pipelineId;
    private IReadOnlyDictionary<AssetContentHash, IGpuPipeline>? m_pipelines;
    private IGpuRenderTarget? m_renderTarget;
    private bool m_resourcesReady;
    private nint m_sampler;
    private IGpuShaderModule? m_vertexShader;
    private IGpuVertexBuffer? m_vertexBuffer;

    /// <summary>Initializes a new instance of the <see cref="ConsoleOverlayNode"/> class.</summary>
    /// <param name="inner">The producer whose render the console is drawn over (its surface must be sampleable here).</param>
    /// <param name="source">The per-frame console state to draw.</param>
    /// <param name="font">The rasterized monospace glyph atlas.</param>
    /// <param name="services">The producer's neutral GPU service bundle (same device).</param>
    /// <param name="vertexBytecode">The fullscreen vertex shader.</param>
    /// <param name="fragmentBytecode">The console fragment shader.</param>
    /// <param name="width">The render width in pixels.</param>
    /// <param name="height">The render height in pixels.</param>
    public ConsoleOverlayNode(
        IRenderNode inner,
        IConsoleTextSource source,
        ConsoleGlyphFont font,
        SdfProducerServices services,
        ReadOnlyMemory<byte> vertexBytecode,
        ReadOnlyMemory<byte> fragmentBytecode,
        uint width,
        uint height
    ) {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(source);

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
        m_vertexBufferFactory = services.VertexBufferFactory;
        m_vertexBytecode = vertexBytecode;
        m_width = width;

        // The grid fills the top-left of the frame without overrunning it — cols across, up to ~55% of the height.
        m_cols = Math.Clamp(value: (((int)width - (2 * Margin)) / font.CellWidth), max: 120, min: 8);
        m_rows = Math.Clamp(value: ((int)(height * 0.55f) / font.CellHeight), max: 40, min: 4);

        // The storage buffer is the font atlas (packed coverage) followed by the cols*rows character grid.
        m_textOffsetUints = font.PackedCoverage.Count;
        m_dataUintCount = (uint)(m_textOffsetUints + (m_cols * m_rows));
        m_scratch = new uint[m_dataUintCount];

        for (var index = 0; (index < font.PackedCoverage.Count); index++) {
            m_scratch[index] = font.PackedCoverage[index];
        }
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (m_disposed) {
            return default;
        }

        var inner = m_inner.ProduceFrame(context: context);

        if (inner.IsEmpty || (0 == inner.ImageViewHandle)) {
            return inner;
        }

        // Closed console (or nothing published yet): pass the frame through untouched — no extra pass.
        if (!m_source.TrySnapshot(frame: out var frame) || !frame.Visible) {
            return inner;
        }

        EnsureResources();

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

        m_dataBuffer!.Write<uint>(data: m_scratch);
        m_drawCommands![0] = m_drawCommands[0] with {
            PushConstants = new GpuPushConstantBinding(data: BuildPushConstants(cursorColumn: cursorColumn), offset: 0, stageFlags: GpuShaderStage.Fragment),
        };

        var commandBufferHandle = m_compositor.Record(
            deviceContext: m_deviceContext,
            drawCommands: m_drawCommands!,
            pipelines: m_pipelines!,
            target: m_renderTarget!
        );

        Span<nint> commandBuffers = [commandBufferHandle];

        m_queueSubmitter.Submit(
            commandBufferHandles: commandBuffers,
            deviceContext: m_deviceContext
        );

        return new Surface(
            Format: SurfaceFormat.R8G8B8A8Unorm,
            Height: m_height,
            ImageViewHandle: m_renderTarget!.ImageViewHandle,
            Width: m_width
        );
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
            WriteRow(row: row, text: lines[firstShown + row]);
        }

        // The bottom row is the live prompt.
        var prompt = ("> " + frame.Input);

        WriteRow(row: (m_rows - 1), text: prompt);

        return Math.Min(val1: prompt.Length, val2: (m_cols - 1));
    }

    private void WriteRow(int row, string text) {
        var baseIndex = (m_textOffsetUints + (row * m_cols));
        var count = Math.Min(val1: text.Length, val2: m_cols);

        for (var column = 0; (column < count); column++) {
            m_scratch[baseIndex + column] = text[column];
        }
    }

    private byte[] BuildPushConstants(int cursorColumn) {
        var data = new byte[PushConstantByteLength];
        var floats = MemoryMarshal.Cast<byte, float>(span: data.AsSpan());

        floats[0] = Margin;                       // panel x (px)
        floats[1] = Margin;                       // panel y (px)
        floats[2] = (m_cols * m_font.CellWidth);  // panel width (px)
        floats[3] = (m_rows * m_font.CellHeight); // panel height (px)
        floats[4] = m_cols;
        floats[5] = m_rows;
        floats[6] = m_font.CellWidth;
        floats[7] = m_font.CellHeight;
        floats[8] = 0.66f;                        // text color r
        floats[9] = 0.95f;                        // text color g
        floats[10] = 0.72f;                       // text color b (a soft phosphor green)
        floats[11] = 0.82f;                       // panel backing alpha
        floats[12] = cursorColumn;
        floats[13] = (m_rows - 1);                // the caret rides the bottom (prompt) row
        floats[14] = m_textOffsetUints;           // where the character grid begins in the buffer
        floats[15] = ConsoleGlyphFont.FirstChar;

        return data;
    }

    private void EnsureResources() {
        if (m_resourcesReady) {
            return;
        }

        m_renderTarget = m_createRenderTarget();
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
        m_pipelines = new Dictionary<AssetContentHash, IGpuPipeline> {
            [m_pipelineId] = m_pipeline,
        };
        m_drawCommands = [
            new GpuDrawCommand(
                DescriptorSetHandle: m_descriptorSet,
                DrawParameters: new GpuDrawParameters(instanceCount: 1, vertexCount: VertexCount),
                PipelineId: m_pipelineId,
                PushConstants: new GpuPushConstantBinding(data: new byte[PushConstantByteLength], offset: 0, stageFlags: GpuShaderStage.Fragment),
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
