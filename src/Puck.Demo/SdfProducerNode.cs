using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Assets;
using Puck.Compositing;
using Puck.Hosting;
using Puck.SdfVm;

namespace Puck.Demo;

/// <summary>
/// A backend-neutral producer node that runs the <em>SDF engine</em> on whichever backend supplies its GPU
/// services: it raymarches a signed-distance scene through the SDF VM into an offscreen render target and hands
/// the result to its host. Because it only touches the neutral <c>IGpu*</c> seam, the identical node renders on
/// Vulkan (SPIR-V shaders) or Direct3D 12 (DXIL shaders) — the caller injects one backend's factories, render
/// target, and compiled bytecode.
/// <para>
/// If the render target is an <see cref="IGpuExportableRenderTarget"/>, the emitted surface carries its shared
/// NT handle for zero-copy cross-backend import; otherwise it carries the same-device image view. The optional
/// capture reads the actual render target back to host memory (via the backend's <see cref="IGpuSurfaceReadback"/>)
/// and writes a PNG — the <em>same</em> capability on every backend, no window or desktop scrape involved.
/// </para>
/// </summary>
public sealed class SdfProducerNode : IRenderNode, IDebugViewTarget {
    private const int CameraPushConstantByteLength = ((sizeof(float) * 4) * 5);
    private const uint VertexCount = 3;
    private const uint VertexStrideBytes = (sizeof(float) * 2);

    private static readonly byte[] FullscreenTriangleVertexData = CreateFullscreenTriangleVertexData();
    private readonly string? m_capturePath;
    private readonly GpuCompositor m_compositor;
    private readonly Func<IGpuRenderTarget> m_createRenderTarget;
    private readonly IGpuDescriptorAllocator m_descriptorAllocator;
    private readonly NodeDescriptor m_descriptor;
    private readonly IGpuDeviceContext m_deviceContext;
    private readonly ReadOnlyMemory<byte> m_fragmentBytecode;
    private readonly uint m_height;
    private readonly bool m_ownsDeviceContext;
    private readonly IGpuPipelineFactory m_pipelineFactory;
    private readonly IGpuQueueSubmitter m_queueSubmitter;
    private readonly IGpuShaderModuleFactory m_shaderModuleFactory;
    private readonly uint m_storageBufferBinding;
    private readonly IGpuStorageBufferFactory m_storageBufferFactory;
    private readonly bool m_submitAndWait;
    private readonly IGpuSurfaceTransferFactory m_surfaceTransferFactory;
    private readonly IGpuVertexBufferFactory m_vertexBufferFactory;
    private readonly ReadOnlyMemory<byte> m_vertexBytecode;
    private readonly uint m_width;
    private bool m_captured;
    private int m_debugMode;
    private bool m_pushConstantsDirty;
    private nint m_descriptorPool;
    private nint m_descriptorSet;
    private bool m_disposed;
    private GpuDrawCommand[]? m_drawCommands;
    private IGpuShaderModule? m_fragmentShader;
    private IGpuPipeline? m_pipeline;
    private IReadOnlyDictionary<AssetContentHash, IGpuPipeline>? m_pipelines;
    private IGpuStorageBuffer? m_programBuffer;
    private IGpuSurfaceReadback? m_readback;
    private IGpuRenderTarget? m_renderTarget;
    private bool m_resourcesReady;
    private IGpuShaderModule? m_vertexShader;
    private IGpuVertexBuffer? m_vertexBuffer;

    /// <summary>Initializes a new instance of the <see cref="SdfProducerNode"/> class.</summary>
    /// <param name="services">The backend's GPU services and the render target to draw into.</param>
    /// <param name="vertexBytecode">The compiled vertex shader (SPIR-V for Vulkan, DXIL for Direct3D 12).</param>
    /// <param name="fragmentBytecode">The compiled fragment/pixel shader for the same backend.</param>
    /// <param name="width">The render width in pixels.</param>
    /// <param name="height">The render height in pixels.</param>
    /// <param name="capturePath">An optional PNG path; when set, the first rendered frame is read back from the GPU and written there.</param>
    /// <param name="submitAndWait">When <see langword="true"/>, the render submit blocks until the GPU finishes — required when a downstream same-device pass (the cursor overlay) samples this node's render target right after.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A dimension is zero.</exception>
    public SdfProducerNode(SdfProducerServices services, ReadOnlyMemory<byte> vertexBytecode, ReadOnlyMemory<byte> fragmentBytecode, uint width, uint height, string? capturePath = null, bool submitAndWait = false) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(services.CreateRenderTarget);

        if (
            (0 == width) ||
            (0 == height)
        ) {
            throw new ArgumentException(message: "Producer dimensions must be non-zero.");
        }

        m_capturePath = capturePath;
        m_compositor = new GpuCompositor(commandRecorder: services.CommandRecorder);
        m_createRenderTarget = services.CreateRenderTarget;
        m_descriptor = new NodeDescriptor(
            Name: "sdf-producer",
            SurfaceId: SurfaceId.New()
        );
        m_descriptorAllocator = services.DescriptorAllocator;
        m_deviceContext = services.DeviceContext;
        m_fragmentBytecode = fragmentBytecode;
        m_height = height;
        m_ownsDeviceContext = services.OwnsDeviceContext;
        m_pipelineFactory = services.PipelineFactory;
        m_queueSubmitter = services.QueueSubmitter;
        m_shaderModuleFactory = services.ShaderModuleFactory;
        m_storageBufferBinding = services.StorageBufferBinding;
        m_storageBufferFactory = services.StorageBufferFactory;
        m_submitAndWait = submitAndWait;
        m_surfaceTransferFactory = services.SurfaceTransferFactory;
        m_vertexBufferFactory = services.VertexBufferFactory;
        m_vertexBytecode = vertexBytecode;
        m_width = width;
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;

    /// <summary>Gets the render width in pixels.</summary>
    public uint Width => m_width;
    /// <summary>Gets the render height in pixels.</summary>
    public uint Height => m_height;

    /// <inheritdoc/>
    /// <remarks>The mode rides the camera push constant (<c>forward.w</c>); a change is applied on the next
    /// render with no resource churn.</remarks>
    public int DebugMode {
        get => m_debugMode;

        set {
            if (m_debugMode != value) {
                m_debugMode = value;
                m_pushConstantsDirty = true;
            }
        }
    }

    /// <summary>Renders one frame and reads the render target back to tightly packed host RGBA — the same GPU
    /// readback the capture uses, with no PNG round-trip. For the parity harness, which compares the two
    /// backends' pixels directly.</summary>
    /// <returns>The render target's pixels (RGBA8, top-left origin, no row padding).</returns>
    /// <exception cref="ObjectDisposedException">The node has been disposed.</exception>
    public ReadOnlyMemory<byte> RenderToBuffer() {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        Render();

        return ReadPixels();
    }

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (m_disposed) {
            return default;
        }

        Render();

        // Capture the actual render target — a GPU readback to host memory, the same on every backend — before
        // any export hand-off, while the texture is still in the readback's expected state.
        if (
            (m_capturePath is not null) &&
            !m_captured
        ) {
            CaptureRenderTarget(path: m_capturePath);
            m_captured = true;
        }

        // An exportable target hands its shared NT handle to a host on another backend (zero-copy); a plain
        // target hands its same-device image view to a host on this backend.
        if (m_renderTarget is IGpuExportableRenderTarget exportable) {
            exportable.FinalizeForExport();

            return new Surface(
                ImageViewHandle: 0,
                Width: m_width,
                Height: m_height,
                Format: SurfaceFormat.R8G8B8A8Unorm,
                SharedHandle: exportable.SharedHandle
            );
        }

        return new Surface(
            ImageViewHandle: m_renderTarget!.ImageViewHandle,
            Width: m_width,
            Height: m_height,
            Format: SurfaceFormat.R8G8B8A8Unorm
        );
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
    // A small fixed scene: a ground plane with three primitives smooth-melded above it.
    private static SdfProgram BuildScene() {
        var builder = new SdfProgramBuilder();
        var ground = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.46f, 0.48f, 0.54f)));
        var red = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.90f, 0.27f, 0.21f)));
        var green = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.30f, 0.80f, 0.40f)));
        var blue = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.27f, 0.45f, 0.92f)));

        _ = builder.ResetPoint().Plane(normal: new Vector3(0f, 1f, 0f), offset: 1f, material: ground);
        _ = builder.ResetPoint().Translate(offset: new Vector3(-1.3f, 0f, 0f)).Sphere(radius: 0.85f, material: red, blend: SdfBlendOp.SmoothUnion, smooth: 0.35f);
        _ = builder.ResetPoint().Translate(offset: new Vector3(1.3f, 0f, 0.2f)).Box(halfExtents: new Vector3(0.65f, 0.65f, 0.65f), round: 0.12f, material: blue, blend: SdfBlendOp.SmoothUnion, smooth: 0.35f);
        _ = builder.ResetPoint().Translate(offset: new Vector3(0f, 0.1f, -1.6f)).Torus(majorRadius: 0.9f, minorRadius: 0.3f, material: green, blend: SdfBlendOp.SmoothUnion, smooth: 0.35f);

        return builder.Build();
    }
    private byte[] BuildCameraPushConstants() {
        var eye = new Vector3(0f, 1.6f, 5.2f);
        var target = new Vector3(0f, 0.1f, 0f);
        var forward = Vector3.Normalize(target - eye);
        var right = Vector3.Normalize(Vector3.Cross(forward, new Vector3(0f, 1f, 0f)));
        var up = Vector3.Cross(right, forward);
        var tanHalfFov = MathF.Tan((60f * (MathF.PI / 180f)) * 0.5f);
        var aspect = ((float)m_width / m_height);
        var bytes = new byte[CameraPushConstantByteLength];
        var floats = MemoryMarshal.Cast<byte, float>(span: bytes.AsSpan());

        floats[0] = eye.X; floats[1] = eye.Y; floats[2] = eye.Z; floats[3] = 0f;            // position.xyz, time
        floats[4] = right.X; floats[5] = right.Y; floats[6] = right.Z; floats[7] = tanHalfFov; // right.xyz, tan(fov/2)
        floats[8] = up.X; floats[9] = up.Y; floats[10] = up.Z; floats[11] = aspect;          // up.xyz, aspect
        floats[12] = forward.X; floats[13] = forward.Y; floats[14] = forward.Z; floats[15] = m_debugMode; // forward.xyz, debug view mode
        floats[16] = m_width; floats[17] = m_height; floats[18] = 0f; floats[19] = 0f;        // resolution.xy

        return bytes;
    }
    private void CaptureRenderTarget(string path) {
        PngImage.Write(
            height: (int)m_height,
            path: path,
            rgba: ReadPixels().Span,
            width: (int)m_width
        );
    }
    // The single render path: realize resources, record the draw, and submit it. Idempotent for resources;
    // re-records and re-submits on every call so a changed push constant (e.g. a debug view mode) takes effect.
    private void Render() {
        EnsureResources();

        // A changed debug view mode only rewrites the push constant (forward.w); resources are untouched.
        if (m_pushConstantsDirty) {
            m_drawCommands![0] = m_drawCommands[0] with {
                PushConstants = new GpuPushConstantBinding(data: BuildCameraPushConstants(), offset: 0, stageFlags: GpuShaderStage.Fragment),
            };
            m_pushConstantsDirty = false;
        }

        var commandBufferHandle = m_compositor.Record(
            deviceContext: m_deviceContext,
            drawCommands: m_drawCommands!,
            pipelines: m_pipelines!,
            target: m_renderTarget!
        );

        Span<nint> commandBuffers = [commandBufferHandle];

        // When a same-device overlay samples this render target immediately after, block until the GPU is done so
        // the sampled result is complete (the plain Submit is fire-and-forget with no fence between submits).
        if (m_submitAndWait) {
            m_queueSubmitter.SubmitAndWait(
                commandBufferHandles: commandBuffers,
                deviceContext: m_deviceContext
            );
        } else {
            m_queueSubmitter.Submit(
                commandBufferHandles: commandBuffers,
                deviceContext: m_deviceContext
            );
        }
    }
    // The single readback path: copy the render target to host memory as tightly packed RGBA. Backs both the
    // PNG capture and the parity harness's buffer comparison.
    private ReadOnlyMemory<byte> ReadPixels() {
        m_readback ??= m_surfaceTransferFactory.CreateReadback(deviceContext: m_deviceContext);

        return m_readback.Read(
            bytesPerPixel: 4,
            deviceContext: m_deviceContext,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: m_height,
            sourceImageHandle: m_renderTarget!.ImageHandle,
            width: m_width
        );
    }
    private void EnsureResources() {
        if (m_resourcesReady) {
            return;
        }

        // Realize the render target now that the backend's device exists (the DirectX device is created lazily
        // on its LUID-matched adapter; the Vulkan device is the host's, ready after activation).
        m_renderTarget = m_createRenderTarget();

        var pipelineId = AssetContentHash.Compute(content: m_fragmentBytecode.Span);

        m_vertexShader = m_shaderModuleFactory.Create(bytecode: m_vertexBytecode, deviceContext: m_deviceContext, stage: GpuShaderStage.Vertex);
        m_fragmentShader = m_shaderModuleFactory.Create(bytecode: m_fragmentBytecode, deviceContext: m_deviceContext, stage: GpuShaderStage.Fragment);
        m_vertexBuffer = m_vertexBufferFactory.Create(deviceContext: m_deviceContext, strideBytes: VertexStrideBytes, vertexData: FullscreenTriangleVertexData);

        var program = BuildScene();
        var programWords = program.Words;
        var programByteLength = (ulong)(programWords.Length * sizeof(uint));

        m_programBuffer = m_storageBufferFactory.Create(deviceContext: m_deviceContext, sizeBytes: programByteLength);
        m_programBuffer.Write(data: programWords);
        m_pipeline = m_pipelineFactory.Create(
            deviceContext: m_deviceContext,
            enableStorageBuffer: true,
            fragmentShaderModule: m_fragmentShader,
            height: m_height,
            pushConstantBinding: new GpuPushConstantBinding(data: new byte[CameraPushConstantByteLength], offset: 0, stageFlags: GpuShaderStage.Fragment),
            renderTarget: m_renderTarget,
            textureSamplerCount: 0,
            vertexShaderModule: m_vertexShader,
            width: m_width
        );

        var deviceHandle = m_deviceContext.DeviceHandle;

        m_descriptorPool = m_descriptorAllocator.CreatePool(
            deviceHandle: deviceHandle,
            sizes: new GpuDescriptorPoolSizes(
                MaxSets: 1,
                CombinedImageSamplerCount: 0,
                StorageBufferCount: 1,
                StorageImageCount: 0,
                AccelerationStructureCount: 0
            )
        );
        m_descriptorSet = m_descriptorAllocator.AllocateSet(descriptorSetLayoutHandle: m_pipeline.DescriptorSetLayoutHandle, deviceHandle: deviceHandle, poolHandle: m_descriptorPool);
        m_descriptorAllocator.WriteStorageBuffer(binding: m_storageBufferBinding, bufferHandle: m_programBuffer.BufferHandle, bufferSize: programByteLength, descriptorSetHandle: m_descriptorSet, deviceHandle: deviceHandle);

        m_pipelines = new Dictionary<AssetContentHash, IGpuPipeline> {
            [pipelineId] = m_pipeline,
        };
        m_drawCommands = [
            new GpuDrawCommand(
                DescriptorSetHandle: m_descriptorSet,
                DrawParameters: new GpuDrawParameters(instanceCount: 1, vertexCount: VertexCount),
                PipelineId: pipelineId,
                PushConstants: new GpuPushConstantBinding(data: BuildCameraPushConstants(), offset: 0, stageFlags: GpuShaderStage.Fragment),
                VertexBufferHandle: m_vertexBuffer.BufferHandle
            ),
        ];
        m_resourcesReady = true;
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        if (0 != m_descriptorPool) {
            m_descriptorAllocator.DestroyPool(deviceHandle: m_deviceContext.DeviceHandle, poolHandle: m_descriptorPool);
            m_descriptorPool = 0;
            m_descriptorSet = 0;
        }

        m_readback?.Dispose();
        m_readback = null;
        m_pipeline?.Dispose();
        m_pipeline = null;
        m_programBuffer?.Dispose();
        m_programBuffer = null;
        m_vertexBuffer?.Dispose();
        m_vertexBuffer = null;
        m_fragmentShader?.Dispose();
        m_fragmentShader = null;
        m_vertexShader?.Dispose();
        m_vertexShader = null;
        m_renderTarget?.Dispose();
        m_renderTarget = null;

        if (
            m_ownsDeviceContext &&
            (m_deviceContext is IDisposable disposableContext)
        ) {
            disposableContext.Dispose();
        }
    }
}
