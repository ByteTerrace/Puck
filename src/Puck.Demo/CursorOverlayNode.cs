using System.Runtime.InteropServices;
using Puck.Abstractions;
using Puck.Assets;
using Puck.Compositing;
using Puck.Hosting;

namespace Puck.Demo;

/// <summary>
/// A demo-owned render node that draws each controller's cursor on top of an inner <see cref="SdfProducerNode"/>.
/// It samples the inner node's SDF render (same device) and blends a colored disc per active cursor in a single
/// fullscreen pass, then hands the result on. The cursor concept lives entirely here — the reusable SDF engine
/// is untouched. Requires a same-device producer whose surface exposes a sampleable image view (the Vulkan
/// same-device producer); the inner node is configured to block on submit so its result is complete before this
/// pass samples it.
/// </summary>
internal sealed class CursorOverlayNode : IRenderNode {
    private const int MaxCursors = 4;
    // Push constant: float4 header (count, radius, gizmoLength, lineWidth) + per cursor a float4 (x, y, packed
    // color, _) and a float4 orientation quaternion = (1 + 2*MaxCursors) float4s = 144 bytes.
    private const int PushConstantByteLength = ((sizeof(float) * 4) * (1 + (2 * MaxCursors)));
    private const float CursorRadius = 0.045f;     // fraction of the target height
    private const float NeedleLength = 0.13f;      // length of each orientation needle (pitch/yaw/roll)
    private const float NeedleLineWidth = 0.008f;
    private const uint SamplerBinding = 0;
    private const uint VertexCount = 3;
    private const uint VertexStrideBytes = (sizeof(float) * 2);

    private static readonly byte[] FullscreenTriangleVertexData = CreateFullscreenTriangleVertexData();
    private readonly GpuCompositor m_compositor;
    private readonly Func<IGpuRenderTarget> m_createRenderTarget;
    private readonly CursorStore m_cursors;
    private readonly CursorStore.Cursor[] m_cursorScratch = new CursorStore.Cursor[MaxCursors];
    private readonly IGpuDescriptorAllocator m_descriptorAllocator;
    private readonly NodeDescriptor m_descriptor;
    private readonly IGpuDeviceContext m_deviceContext;
    private readonly ReadOnlyMemory<byte> m_fragmentBytecode;
    private readonly uint m_height;
    private readonly SdfProducerNode m_inner;
    private readonly IGpuPipelineFactory m_pipelineFactory;
    private readonly IGpuQueueSubmitter m_queueSubmitter;
    private readonly IGpuShaderModuleFactory m_shaderModuleFactory;
    private readonly IGpuVertexBufferFactory m_vertexBufferFactory;
    private readonly ReadOnlyMemory<byte> m_vertexBytecode;
    private readonly uint m_width;
    private nint m_descriptorPool;
    private nint m_descriptorSet;
    private bool m_disposed;
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

    /// <summary>Initializes a new instance of the <see cref="CursorOverlayNode"/> class.</summary>
    /// <param name="inner">The SDF producer whose render the cursors are drawn over.</param>
    /// <param name="cursors">The per-controller cursor state to draw.</param>
    /// <param name="commandRecorder">The backend's command recorder.</param>
    /// <param name="deviceContext">The producer's device context (the overlay runs on the same device).</param>
    /// <param name="descriptorAllocator">The backend's descriptor allocator.</param>
    /// <param name="pipelineFactory">The backend's pipeline factory.</param>
    /// <param name="queueSubmitter">The backend's queue submitter.</param>
    /// <param name="shaderModuleFactory">The backend's shader module factory.</param>
    /// <param name="vertexBufferFactory">The backend's vertex buffer factory.</param>
    /// <param name="createRenderTarget">Creates the overlay's output render target.</param>
    /// <param name="vertexBytecode">The fullscreen vertex shader (shared with the SDF stage).</param>
    /// <param name="fragmentBytecode">The cursor-overlay fragment shader.</param>
    /// <param name="width">The render width in pixels.</param>
    /// <param name="height">The render height in pixels.</param>
    public CursorOverlayNode(
        SdfProducerNode inner,
        CursorStore cursors,
        IGpuCommandRecorder commandRecorder,
        IGpuDeviceContext deviceContext,
        IGpuDescriptorAllocator descriptorAllocator,
        IGpuPipelineFactory pipelineFactory,
        IGpuQueueSubmitter queueSubmitter,
        IGpuShaderModuleFactory shaderModuleFactory,
        IGpuVertexBufferFactory vertexBufferFactory,
        Func<IGpuRenderTarget> createRenderTarget,
        ReadOnlyMemory<byte> vertexBytecode,
        ReadOnlyMemory<byte> fragmentBytecode,
        uint width,
        uint height
    ) {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(cursors);
        ArgumentNullException.ThrowIfNull(commandRecorder);
        ArgumentNullException.ThrowIfNull(createRenderTarget);
        ArgumentNullException.ThrowIfNull(deviceContext);
        ArgumentNullException.ThrowIfNull(descriptorAllocator);
        ArgumentNullException.ThrowIfNull(pipelineFactory);
        ArgumentNullException.ThrowIfNull(queueSubmitter);
        ArgumentNullException.ThrowIfNull(shaderModuleFactory);
        ArgumentNullException.ThrowIfNull(vertexBufferFactory);

        m_compositor = new GpuCompositor(commandRecorder: commandRecorder);
        m_createRenderTarget = createRenderTarget;
        m_cursors = cursors;
        m_descriptor = new NodeDescriptor(Name: "cursor-overlay", SurfaceId: SurfaceId.New());
        m_descriptorAllocator = descriptorAllocator;
        m_deviceContext = deviceContext;
        m_fragmentBytecode = fragmentBytecode;
        m_height = height;
        m_inner = inner;
        m_pipelineFactory = pipelineFactory;
        m_queueSubmitter = queueSubmitter;
        m_shaderModuleFactory = shaderModuleFactory;
        m_vertexBufferFactory = vertexBufferFactory;
        m_vertexBytecode = vertexBytecode;
        m_width = width;
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (m_disposed) {
            return default;
        }

        // The inner node renders the SDF and (configured to wait) returns a completed, sampleable same-device view.
        var sdf = m_inner.ProduceFrame(context: context);

        if (sdf.IsEmpty || (0 == sdf.ImageViewHandle)) {
            return sdf;
        }

        EnsureResources();

        // Point the sampler at the SDF result. Stable across frames (the inner target is reused), so this only
        // writes when the view actually changes — never re-binding a set an in-flight command buffer may use.
        if (sdf.ImageViewHandle != m_lastImageViewHandle) {
            m_descriptorAllocator.WriteCombinedImageSampler(
                arrayElement: 0,
                binding: SamplerBinding,
                descriptorSetHandle: m_descriptorSet,
                deviceHandle: m_deviceContext.DeviceHandle,
                imageViewHandle: sdf.ImageViewHandle,
                samplerHandle: m_sampler
            );

            m_lastImageViewHandle = sdf.ImageViewHandle;
        }

        m_drawCommands![0] = m_drawCommands[0] with {
            PushConstants = new GpuPushConstantBinding(data: BuildCursorPushConstants(), offset: 0, stageFlags: GpuShaderStage.Fragment),
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

    private byte[] BuildCursorPushConstants() {
        var data = new byte[PushConstantByteLength];
        var floats = MemoryMarshal.Cast<byte, float>(span: data.AsSpan());
        var count = m_cursors.Snapshot(destination: m_cursorScratch);

        floats[0] = count;          // cursor count
        floats[1] = CursorRadius;   // disc radius (fraction of height)
        floats[2] = NeedleLength;   // orientation-needle length
        floats[3] = NeedleLineWidth;

        for (var index = 0; (index < count); ++index) {
            var cursor = m_cursorScratch[index];
            var cursorSlot = (4 + (index * 4));
            var orientationSlot = (4 + (MaxCursors * 4) + (index * 4));

            floats[cursorSlot] = cursor.Position.X;
            floats[cursorSlot + 1] = cursor.Position.Y;
            floats[cursorSlot + 2] = BitConverter.UInt32BitsToSingle(value: PackColor(color: cursor.Color));
            floats[orientationSlot] = cursor.Orientation.X;
            floats[orientationSlot + 1] = cursor.Orientation.Y;
            floats[orientationSlot + 2] = cursor.Orientation.Z;
            floats[orientationSlot + 3] = cursor.Orientation.W;
        }

        return data;
    }
    private static uint PackColor(System.Numerics.Vector3 color) {
        static uint Channel(float value) => (uint)(Math.Clamp(value: value, max: 1f, min: 0f) * 255f);

        return ((Channel(color.X) << 16) | (Channel(color.Y) << 8) | Channel(color.Z));
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
        m_pipeline = m_pipelineFactory.Create(
            deviceContext: m_deviceContext,
            enableStorageBuffer: false,
            fragmentShaderModule: m_fragmentShader,
            height: m_height,
            pushConstantBinding: new GpuPushConstantBinding(data: new byte[PushConstantByteLength], offset: 0, stageFlags: GpuShaderStage.Fragment),
            renderTarget: m_renderTarget,
            textureSamplerCount: 1,
            vertexShaderModule: m_vertexShader,
            width: m_width
        );

        var deviceHandle = m_deviceContext.DeviceHandle;

        m_descriptorPool = m_descriptorAllocator.CreatePool(combinedImageSamplerCount: 1, deviceHandle: deviceHandle, maxSets: 1, storageBufferCount: 0, storageImageCount: 0);
        m_descriptorSet = m_descriptorAllocator.AllocateSet(descriptorSetLayoutHandle: m_pipeline.DescriptorSetLayoutHandle, deviceHandle: deviceHandle, poolHandle: m_descriptorPool);
        m_sampler = m_descriptorAllocator.CreateSampler(deviceHandle: deviceHandle);
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
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

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
        m_vertexBuffer?.Dispose();
        m_vertexBuffer = null;
        m_fragmentShader?.Dispose();
        m_fragmentShader = null;
        m_vertexShader?.Dispose();
        m_vertexShader = null;
        m_renderTarget?.Dispose();
        m_renderTarget = null;
        m_inner.Dispose();
    }
}
