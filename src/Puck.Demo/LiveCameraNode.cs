using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions;
using Puck.DirectX;
using Puck.Hosting;

namespace Puck.Demo;

/// <summary>
/// A LIVE camera content source (the <c>--camera</c> / <c>camera</c> graph node): the running skeleton a real hardware
/// camera fills at M2. A bespoke Direct3D 12 device — standing in for a capture device's decode device (e.g. Media
/// Foundation's D3D device) — produces an animated "frame" (the neutral <c>sdf-child</c> test pattern) into a shared
/// storage image every frame and hands it back as a <see cref="Surface"/> carrying its shared NT handle. The Vulkan
/// host imports that handle zero-copy and presents it through the same path a cross-backend producer uses — so this node
/// needs no new host code; only its <em>content source</em> changes when the real camera lands.
/// <para>
/// The producer device is independent of the host render device, so this node never touches the host device capability:
/// it produces on its own device and returns only a shared handle. It drains the producer each frame via
/// <see cref="IGpuExportableStorageImage.FinalizeForExport"/> (the correctness-floor cadence; the keyed-mutex + ring
/// optimization the plan calls for is a later milestone).
/// </para>
/// </summary>
internal sealed class LiveCameraNode : IRenderNode {
    private const uint ChildOutputBinding = 0; // sdf-child.comp: Output at binding 0 (register u0)
    private const int ChildPushByteLength = (sizeof(uint) * 4); // ChildParams: uint2 extent + float time + uint pad
    private const uint WorkgroupEdge = 8;

    private readonly NodeDescriptor m_descriptor = new(
        Name: "live-camera",
        SurfaceId: SurfaceId.New()
    );
    private readonly uint m_height;
    private readonly byte[] m_push = new byte[ChildPushByteLength];
    private readonly IServiceProvider m_serviceProvider;
    private readonly uint m_width;

    private IGpuComputeCommandPool? m_commandPool;
    private nint m_deviceHandle;
    private DirectXComputeWorldDevice? m_directX;
    private bool m_disposed;
    private IGpuExportableStorageImage? m_exportable;
    private IGpuComputeServices? m_gpu;
    private bool m_imageInitialized;
    private IGpuComputePipeline? m_pipeline;
    private nint m_pool;
    private nint m_set;
    private IGpuShaderModule? m_shaderModule;
    private float m_time;
    private bool m_warnedUnsupported;

    /// <summary>Initializes a new instance of the <see cref="LiveCameraNode"/> class.</summary>
    /// <param name="serviceProvider">The application service provider (the LUID source for the bespoke Direct3D 12 producer device).</param>
    /// <param name="width">The frame width in pixels.</param>
    /// <param name="height">The frame height in pixels.</param>
    /// <exception cref="ArgumentNullException"><paramref name="serviceProvider"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A dimension is zero.</exception>
    public LiveCameraNode(IServiceProvider serviceProvider, uint width, uint height) {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        if ((0 == width) || (0 == height)) {
            throw new ArgumentException(message: "Camera dimensions must be non-zero.");
        }

        m_height = height;
        m_serviceProvider = serviceProvider;
        m_width = width;
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (m_disposed) {
            return default;
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            if (!m_warnedUnsupported) {
                Console.Error.WriteLine(value: "[camera] Direct3D 12 unavailable; the live camera source requires Windows 10.0.10240+ (presenting a blank surface).");

                m_warnedUnsupported = true;
            }

            return default;
        }

        return ProduceFrameWindows(deltaSeconds: context.DeltaSeconds);
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private Surface ProduceFrameWindows(double deltaSeconds) {
        EnsureResources();

        m_time += (float)deltaSeconds;

        Render();
        // Drain the producer so the frame's pixels are complete in the shared memory before the host imports and
        // presents them (producer and host share no timeline; the fence is the ordering).
        m_exportable!.FinalizeForExport();

        return new Surface(
            Format: SurfaceFormat.R8G8B8A8Unorm,
            Height: m_height,
            ImageViewHandle: 0,
            SharedHandle: m_exportable.SharedHandle,
            Width: m_width
        );
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private void EnsureResources() {
        if (m_directX is not null) {
            return;
        }

        // The bespoke Direct3D 12 device is LUID-matched to the host adapter so the Vulkan host can import its shared
        // handle; it stands in for the camera's decode device.
        m_directX = new DirectXComputeWorldDevice(hostProvider: m_serviceProvider);

        var device = m_directX.DeviceContext;

        m_deviceHandle = device.DeviceHandle;
        m_gpu = (IGpuComputeServices)m_directX.Services.GetService(serviceType: typeof(IGpuComputeServices))!;
        m_exportable = new DirectXGpuSurfaceExportFactory().CreateExportableStorageImage(
            deviceContext: device,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: m_height,
            width: m_width
        );

        var bytecode = File.ReadAllBytes(path: Path.Combine(
            path1: CrossBackendShowcase.ShaderDirectory,
            path2: "sdf-child.comp.dxil"
        ));

        // ChildParams: extent = the frame size (time is written per frame in Render).
        var pushWords = MemoryMarshal.Cast<byte, uint>(span: m_push.AsSpan());

        pushWords[0] = m_width;
        pushWords[1] = m_height;

        GpuComputeBinding[] bindings = [new GpuComputeBinding(Binding: ChildOutputBinding, Kind: GpuComputeBindingKind.StorageImage)];

        m_shaderModule = m_gpu.ShaderModuleFactory.Create(deviceContext: device, stage: GpuShaderStage.Compute, bytecode: bytecode);
        m_pipeline = m_gpu.ComputePipelineFactory.Create(
            bindings: bindings,
            computeShaderModule: m_shaderModule,
            deviceContext: device,
            pushConstantBinding: new GpuPushConstantBinding(data: m_push, offset: 0, stageFlags: GpuShaderStage.Compute)
        );

        var poolSizes = GpuDescriptorPoolSizes.ForSets(bindings);

        m_pool = m_gpu.DescriptorAllocator.CreatePool(
            combinedImageSamplerCount: poolSizes.CombinedImageSamplerCount,
            deviceHandle: m_deviceHandle,
            maxSets: poolSizes.MaxSets,
            storageBufferCount: poolSizes.StorageBufferCount,
            storageImageCount: poolSizes.StorageImageCount
        );
        m_set = m_gpu.DescriptorAllocator.AllocateSet(descriptorSetLayoutHandle: m_pipeline.DescriptorSetLayoutHandle, deviceHandle: m_deviceHandle, poolHandle: m_pool);
        m_gpu.DescriptorAllocator.WriteStorageImage(arrayElement: 0, binding: ChildOutputBinding, descriptorSetHandle: m_set, deviceHandle: m_deviceHandle, imageViewHandle: m_exportable.ImageViewHandle);
        m_commandPool = m_gpu.CommandPoolFactory.Create(deviceContext: device);
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private void Render() {
        var recorder = m_gpu!.ComputeRecorder;
        var device = m_directX!.DeviceContext;
        var commandBuffer = m_commandPool!.CommandBufferHandle;

        MemoryMarshal.Cast<byte, float>(span: m_push.AsSpan())[2] = m_time; // ChildParams.time

        // First frame: the fresh exportable rests in COMMON (Undefined to Vulkan/us). Afterwards it rests in External
        // (COMMON) from the previous frame's hand-off. The dispatch overwrites every pixel, so nothing is preserved.
        var oldLayout = (m_imageInitialized ? GpuImageLayout.External : GpuImageLayout.Undefined);
        var sourceAccess = (m_imageInitialized ? GpuComputeAccess.ShaderRead : GpuComputeAccess.None);
        var sourceStage = (m_imageInitialized ? GpuComputeStage.ComputeShader : GpuComputeStage.TopOfPipe);

        recorder.BeginCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);
        recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuComputeAccess.ShaderWrite,
            destinationStageMask: GpuComputeStage.ComputeShader,
            deviceHandle: m_deviceHandle,
            imageHandle: m_exportable!.ImageHandle,
            newLayout: GpuImageLayout.General,
            oldLayout: oldLayout,
            sourceAccessMask: sourceAccess,
            sourceStageMask: sourceStage
        );
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
        // Hand the resource to the host in the cross-API COMMON (External) state — the required rest state before
        // another API takes it over. FinalizeForExport then drains the fence.
        recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuComputeAccess.ShaderRead,
            destinationStageMask: GpuComputeStage.ComputeShader,
            deviceHandle: m_deviceHandle,
            imageHandle: m_exportable.ImageHandle,
            newLayout: GpuImageLayout.External,
            oldLayout: GpuImageLayout.General,
            sourceAccessMask: GpuComputeAccess.ShaderWrite,
            sourceStageMask: GpuComputeStage.ComputeShader
        );
        recorder.EndCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);
        m_gpu.QueueSubmitter.Submit(commandBufferHandles: [commandBuffer], deviceContext: device);

        m_imageInitialized = true;
    }

    /// <inheritdoc/>
    public void OnDeviceLost() {
        // The producer device is independent of the (lost) host device, but a full GPU reset may take it too; tear it
        // down and let the next ProduceFrame rebuild. A rebuild yields a NEW shared handle, which flows through the
        // returned Surface so the host re-imports it.
        ReleaseProducer();
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        ReleaseProducer();
    }

    // Producer teardown shared by Dispose and OnDeviceLost. Wait-free (FinalizeForExport drains every frame, so nothing
    // is in flight) and idempotent (fields nulled); the next EnsureResources rebuilds from scratch.
    private void ReleaseProducer() {
        m_commandPool?.Dispose();
        m_commandPool = null;
        m_pipeline?.Dispose();
        m_pipeline = null;

        if ((0 != m_pool) && (m_gpu is not null)) {
            m_gpu.DescriptorAllocator.DestroyPool(deviceHandle: m_deviceHandle, poolHandle: m_pool);
            m_pool = 0;
        }

        m_set = 0;
        m_shaderModule?.Dispose();
        m_shaderModule = null;
        m_exportable?.Dispose();
        m_exportable = null;

        // m_directX is only ever assigned on Windows (in the OS-guarded EnsureResources), so its Windows-only Dispose is
        // reachable only there; the explicit check satisfies the platform analyzer.
        if ((m_directX is not null) && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            m_directX.Dispose();
            m_directX = null;
        }

        m_gpu = null;
        m_deviceHandle = 0;
        m_imageInitialized = false;
    }
}
