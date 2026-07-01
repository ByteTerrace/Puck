using System.Runtime.InteropServices;
using Puck.Abstractions;
using Puck.Hosting;
using Puck.Platform;

namespace Puck.Demo;

/// <summary>
/// A per-viewport LIVE-CAMERA pane: the first-class hardware-camera content source hosted by
/// <see cref="WorldProducerNode"/> in the slot a <c>live-camera</c> viewport declares. It runs on the HOST device (like
/// <see cref="ChildSurfaceNode"/>), so it composites into the world's <c>sources[]</c> array with no new host code — but
/// where the child node generates a test pattern, this node opens the platform capture device through the neutral
/// <see cref="ICameraCaptureService"/> seam, uploads each frame's CPU pixels onto the host device
/// (<see cref="IGpuSurfaceUpload"/>), and SAMPLES that upload through <c>resample.comp</c> into the pane's exact pixel
/// rect. That sampling is the "camera as a sampled input to the SDF/effects pipeline" the plan calls for: the source's
/// authored <c>pixelSize</c>/<c>quantize</c> knobs drive the same retro pixelation + color-quantization the pixelate
/// decorator uses, applied to the live feed.
/// <para>
/// When no camera (or no Media Foundation) is available it feeds an animated CPU test pattern through the SAME
/// upload+resample path, so a <c>live-camera</c> viewport is always demoable. The CPU-upload transport is the
/// correctness-floor tier — <see cref="IGpuSurfaceUpload.Upload"/> serializes on the device each frame; the GPU-resident
/// zero-copy tier is a later milestone.
/// </para>
/// </summary>
internal sealed class CameraPaneNode : IRenderNode {
    private const uint Format = GpuPixelFormat.R8G8B8A8Unorm;
    private const int FallbackHeight = 96;
    private const int FallbackWidth = 128;
    private const uint OutputBindingIndex = 0; // resample.comp: Output at binding 0 (register u0)
    private const int ResamplePushByteLength = 32; // ResampleParams { uint2 outExtent; float2 srcOrigin; float2 srcSize; uint cellSize; uint quantizeLevels; }
    private const uint SourceBindingIndex = 1;  // resample.comp: Source combined-image-sampler at binding 1 (t0/s0)
    private const uint WorkgroupEdge = 8;

    private readonly uint m_cellSize;
    private readonly NodeDescriptor m_descriptor = new(
        Name: "camera-pane",
        SurfaceId: SurfaceId.New()
    );
    private readonly byte[] m_fallbackPixels = new byte[FallbackWidth * FallbackHeight * 4];
    private readonly byte[] m_push = new byte[ResamplePushByteLength];
    private readonly uint m_quantizeLevels;
    private readonly ReadOnlyMemory<byte> m_resampleBytecode;
    private readonly IServiceProvider m_serviceProvider;

    private bool m_cameraChecked;
    private ICameraCaptureSession? m_cameraSession;
    private IGpuComputeCommandPool? m_commandPool;
    private IGpuComputeRecorder? m_computeRecorder;
    private IGpuDescriptorAllocator? m_descriptorAllocator;
    private IGpuDeviceContext? m_deviceContext;
    private nint m_deviceHandle;
    private bool m_disposed;
    private IGpuComputeServices? m_gpu;
    private uint m_height;
    private nint m_lastInputView;
    private IGpuStorageImage? m_outputImage;
    private bool m_outputInitialized;
    private IGpuComputePipeline? m_pipeline;
    private nint m_pool;
    private IGpuQueueSubmitter? m_queueSubmitter;
    private bool m_resourcesReady;
    private nint m_sampler;
    private nint m_set;
    private IGpuShaderModule? m_shaderModule;
    private float m_time;
    private IGpuSurfaceUpload? m_upload;
    private uint m_width;

    /// <summary>Initializes a new instance of the <see cref="CameraPaneNode"/> class.</summary>
    /// <param name="serviceProvider">The service provider resolving the neutral GPU compute factories and the camera-capture service.</param>
    /// <param name="resampleBytecode">The compiled <c>resample.comp</c> kernel for the host backend (SPIR-V or DXIL).</param>
    /// <param name="pixelSize">The retro pixelation cell size (<c>&lt;= 1</c> disables it).</param>
    /// <param name="quantize">The per-channel color levels (<c>&lt;= 1</c> disables it).</param>
    /// <exception cref="ArgumentNullException"><paramref name="serviceProvider"/> is <see langword="null"/>.</exception>
    public CameraPaneNode(IServiceProvider serviceProvider, ReadOnlyMemory<byte> resampleBytecode, int pixelSize, int quantize) {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        m_cellSize = ((pixelSize > 1) ? (uint)pixelSize : 0);
        m_quantizeLevels = ((quantize > 1) ? (uint)quantize : 0);
        m_resampleBytecode = resampleBytecode;
        m_serviceProvider = serviceProvider;
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (
            m_disposed ||
            (0 == context.TargetWidth) ||
            (0 == context.TargetHeight)
        ) {
            return default;
        }

        if (!context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var gpuDevice)) {
            return default;
        }

        EnsureCamera();
        EnsureResources(gpuDevice: gpuDevice, height: context.TargetHeight, width: context.TargetWidth);

        m_time += (float)context.DeltaSeconds;

        // The frame's source pixels: the newest camera frame if one is ready, otherwise the animated fallback pattern —
        // both B8G8R8A8 CPU pixels fed through the identical upload+sample path.
        ReadOnlyMemory<byte> pixels;
        uint sourceHeight;
        uint sourceWidth;

        if ((m_cameraSession is not null) && m_cameraSession.TryCapture(out var frame) && !frame.Pixels.IsEmpty) {
            pixels = frame.Pixels;
            sourceHeight = frame.Height;
            sourceWidth = frame.Width;
        } else {
            FillFallback();

            pixels = m_fallbackPixels;
            sourceHeight = FallbackHeight;
            sourceWidth = FallbackWidth;
        }

        // Materialize the CPU pixels onto the host device (a shader-readable image), rebind the sampler only when the
        // upload's view handle changes (stable once the extent settles), then sample it into the pane rect.
        var inputView = m_upload!.Upload(deviceContext: gpuDevice, pixels: pixels, width: sourceWidth, height: sourceHeight, format: GpuPixelFormat.B8G8R8A8Unorm);

        if (inputView != m_lastInputView) {
            m_descriptorAllocator!.WriteCombinedImageSampler(arrayElement: 0, binding: SourceBindingIndex, descriptorSetHandle: m_set, deviceHandle: m_deviceHandle, imageViewHandle: inputView, samplerHandle: m_sampler);

            m_lastInputView = inputView;
        }

        Render();

        // A rect-sized, General-layout storage image: the integer-copy source contract WorldProducerNode composites.
        return new Surface(
            Format: SurfaceFormat.R8G8B8A8Unorm,
            Height: m_height,
            ImageViewHandle: m_outputImage!.ImageViewHandle,
            Width: m_width
        );
    }

    // Resolve the camera service once and try to open the default device; on failure the node feeds the fallback
    // pattern. Logs which tier is active (the plan's tier telemetry).
    private void EnsureCamera() {
        if (m_cameraChecked) {
            return;
        }

        m_cameraChecked = true;

        if ((m_serviceProvider.GetService(serviceType: typeof(ICameraCaptureService)) is ICameraCaptureService service)
            && service.IsSupported
            && service.TryOpenDefault(requestedWidth: 1280, requestedHeight: 720, session: out var session)) {
            m_cameraSession = session;

            Console.Out.WriteLine(value: $"[camera-pane] live capture device '{session.Name}' {session.Width}x{session.Height} (CPU-upload tier).");

            return;
        }

        Console.Out.WriteLine(value: "[camera-pane] no capture device (or Media Foundation unavailable); sampling the animated test pattern.");
    }

    private void EnsureResources(IGpuDeviceContext gpuDevice, uint height, uint width) {
        if (
            m_resourcesReady &&
            (m_width == width) &&
            (m_height == height)
        ) {
            return;
        }

        m_gpu ??= (IGpuComputeServices)m_serviceProvider.GetService(serviceType: typeof(IGpuComputeServices))!;

        if (!m_resourcesReady) {
            m_deviceContext = gpuDevice;
            m_deviceHandle = gpuDevice.DeviceHandle;
            m_computeRecorder = m_gpu.ComputeRecorder;
            m_descriptorAllocator = m_gpu.DescriptorAllocator;
            m_queueSubmitter = m_gpu.QueueSubmitter;
            m_upload = m_gpu.SurfaceTransferFactory.CreateUpload(deviceContext: gpuDevice);

            GpuComputeBinding[] bindings = [
                new GpuComputeBinding(Binding: OutputBindingIndex, Kind: GpuComputeBindingKind.StorageImage),
                new GpuComputeBinding(Binding: SourceBindingIndex, Kind: GpuComputeBindingKind.SampledImage),
            ];

            m_shaderModule = m_gpu.ShaderModuleFactory.Create(deviceContext: gpuDevice, stage: GpuShaderStage.Compute, bytecode: m_resampleBytecode);
            m_pipeline = m_gpu.ComputePipelineFactory.Create(
                bindings: bindings,
                computeShaderModule: m_shaderModule,
                deviceContext: gpuDevice,
                pushConstantBinding: new GpuPushConstantBinding(data: m_push, offset: 0, stageFlags: GpuShaderStage.Compute),
                samplerFilter: ((m_cellSize > 1) ? GpuSamplerFilter.Nearest : GpuSamplerFilter.Linear)
            );

            var poolSizes = GpuDescriptorPoolSizes.ForSets(bindings);

            m_pool = m_descriptorAllocator.CreatePool(
                combinedImageSamplerCount: poolSizes.CombinedImageSamplerCount,
                deviceHandle: m_deviceHandle,
                maxSets: poolSizes.MaxSets,
                storageBufferCount: poolSizes.StorageBufferCount,
                storageImageCount: poolSizes.StorageImageCount
            );
            m_sampler = m_descriptorAllocator.CreateSampler(deviceHandle: m_deviceHandle, filter: ((m_cellSize > 1) ? GpuSamplerFilter.Nearest : GpuSamplerFilter.Linear));
            m_set = m_descriptorAllocator.AllocateSet(descriptorSetLayoutHandle: m_pipeline.DescriptorSetLayoutHandle, deviceHandle: m_deviceHandle, poolHandle: m_pool);
            m_commandPool = m_gpu.CommandPoolFactory.Create(deviceContext: gpuDevice);
        }

        // (Re)create the pane-rect output and rebind it; the pipeline, sampler, upload, and pool are extent-independent.
        m_outputImage?.Dispose();
        m_outputImage = m_gpu.StorageImageFactory.Create(deviceContext: gpuDevice, format: Format, height: height, width: width);
        m_descriptorAllocator!.WriteStorageImage(arrayElement: 0, binding: OutputBindingIndex, descriptorSetHandle: m_set, deviceHandle: m_deviceHandle, imageViewHandle: m_outputImage.ImageViewHandle);
        m_outputInitialized = false; // the freshly created output starts Undefined; Render brings it into General.

        // ResampleParams: sample the whole source into the pane rect, with the authored pixelation + quantization.
        var words = MemoryMarshal.Cast<byte, uint>(span: m_push.AsSpan());
        var floats = MemoryMarshal.Cast<byte, float>(span: m_push.AsSpan());

        words[0] = width;
        words[1] = height; // outExtent
        floats[2] = 0f;
        floats[3] = 0f;    // srcOrigin
        floats[4] = 1f;
        floats[5] = 1f;    // srcSize (whole source)
        words[6] = m_cellSize;
        words[7] = m_quantizeLevels;

        m_height = height;
        m_resourcesReady = true;
        m_width = width;
    }

    // Fill the fallback pattern: animated diagonal bands, so a missing camera reads as "test pattern" (never a frozen
    // frame). B8G8R8A8 byte order in memory; the GPU treats it as B8G8R8A8Unorm and samples the channels correctly.
    private void FillFallback() {
        var shift = (int)(m_time * 40f);

        for (var y = 0; (y < FallbackHeight); y++) {
            for (var x = 0; (x < FallbackWidth); x++) {
                var offset = ((y * FallbackWidth) + x) * 4;
                var band = (((x + y + shift) >> 3) & 1);

                m_fallbackPixels[offset + 0] = (byte)(band * 90);            // B
                m_fallbackPixels[offset + 1] = (byte)((x * 255) / FallbackWidth); // G
                m_fallbackPixels[offset + 2] = (byte)((y * 255) / FallbackHeight); // R
                m_fallbackPixels[offset + 3] = 0xFF;                          // A
            }
        }
    }

    private void Render() {
        var recorder = m_computeRecorder!;
        var commandBuffer = m_commandPool!.CommandBufferHandle;

        recorder.BeginCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);

        // First frame brings the fresh output into the General (UAV) working layout; afterwards it persists there
        // (written each frame, read by the parent compositor), like ChildSurfaceNode.
        if (!m_outputInitialized) {
            recorder.TransitionImageLayout(
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuComputeAccess.ShaderWrite,
                destinationStageMask: GpuComputeStage.ComputeShader,
                deviceHandle: m_deviceHandle,
                imageHandle: m_outputImage!.ImageHandle,
                newLayout: GpuImageLayout.General,
                oldLayout: GpuImageLayout.Undefined,
                sourceAccessMask: GpuComputeAccess.None,
                sourceStageMask: GpuComputeStage.TopOfPipe
            );
        }

        recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, pipelineHandle: m_pipeline!.Handle);
        recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: m_set, deviceHandle: m_deviceHandle, pipelineLayoutHandle: m_pipeline.LayoutHandle);
        recorder.PushConstants(commandBufferHandle: commandBuffer, data: m_push, deviceHandle: m_deviceHandle, offset: 0, pipelineLayoutHandle: m_pipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
        recorder.Dispatch(
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle,
            groupCountX: ((m_width + (WorkgroupEdge - 1)) / WorkgroupEdge),
            groupCountY: ((m_height + (WorkgroupEdge - 1)) / WorkgroupEdge),
            groupCountZ: 1
        );
        recorder.EndCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);

        // Fire-and-forget on the shared queue: enqueued before the parent compositor submit, ordered ahead of the
        // composite read by the parent's compute memory barrier (the ChildSurfaceNode contract).
        m_queueSubmitter!.Submit(commandBufferHandles: [commandBuffer], deviceContext: m_deviceContext!);

        m_outputInitialized = true;
    }

    /// <inheritdoc/>
    public void OnDeviceLost() {
        // Reset on the still-valid (lost) device, wait-free. The next ProduceFrame re-runs EnsureResources and rebuilds.
        ReleaseGpuResources();
        m_deviceHandle = 0;
        m_lastInputView = 0;
        m_outputInitialized = false;
        m_resourcesReady = false;
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_cameraSession?.Dispose();
        m_cameraSession = null;
        // The per-frame submits are fire-and-forget, so a frame may still be in flight at teardown.
        m_deviceContext?.WaitIdle();
        ReleaseGpuResources();
    }

    // Device-resource teardown shared by Dispose and OnDeviceLost. Wait-free, idempotent (fields nulled), safe against a
    // lost device; destroys the pool via the still-current m_deviceHandle.
    private void ReleaseGpuResources() {
        m_upload?.Dispose();
        m_upload = null;
        m_commandPool?.Dispose();
        m_commandPool = null;
        m_pipeline?.Dispose();
        m_pipeline = null;

        if ((0 != m_pool) && (m_descriptorAllocator is not null)) {
            m_descriptorAllocator.DestroyPool(deviceHandle: m_deviceHandle, poolHandle: m_pool);
            m_pool = 0;
        }

        if ((0 != m_sampler) && (m_descriptorAllocator is not null)) {
            m_descriptorAllocator.DestroySampler(deviceHandle: m_deviceHandle, samplerHandle: m_sampler);
            m_sampler = 0;
        }

        m_lastInputView = 0;
        m_set = 0;
        m_outputImage?.Dispose();
        m_outputImage = null;
        m_shaderModule?.Dispose();
        m_shaderModule = null;
    }
}
