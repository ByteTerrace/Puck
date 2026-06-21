using System.Runtime.InteropServices;
using Puck.Abstractions;
using Puck.Hosting;

namespace Puck.Demo;

/// <summary>
/// A minimal backend-neutral compute <see cref="IRenderNode"/> that renders an animated test pattern into a storage
/// image, so a viewport of the world compositor can show ANOTHER node's output instead of an SDF camera.
/// <see cref="WorldProducerNode"/>
/// hosts one of these per child viewport slot, forwards it the slot's pixel extent through <see cref="FrameContext"/>,
/// and binds the surface it returns straight into that slot of the source-agnostic compositor's <c>sources[]</c>
/// array (an integer-copy, same-device handoff — no sampler).
/// <para>
/// Unlike a presentable node, it leaves its output in the compute working layout (<see cref="GpuImageLayout.General"/>)
/// rather than a shader-readable one: the consumer is a compute compositor that reads it as a storage image (a UAV),
/// and the parent's per-frame compute memory barrier makes this node's writes visible to that read. It is therefore a
/// <em>compute source</em>, intended to be composited by <see cref="WorldProducerNode"/>, not sampled directly.
/// </para>
/// </summary>
internal sealed class ChildSurfaceNode : IRenderNode {
    private const uint Format = GpuPixelFormat.R8G8B8A8Unorm;
    private const uint OutputBindingIndex = 0; // sdf-child.comp: Output at binding 0 (register u0)
    private const int PushConstantByteLength = (sizeof(uint) * 4); // ChildParams: uint2 extent + float time + uint pad
    private const uint WorkgroupEdge = 8;

    private readonly ReadOnlyMemory<byte> m_bytecode;
    private readonly NodeDescriptor m_descriptor = new(
        Name: "compute-child-surface",
        SurfaceId: SurfaceId.New()
    );
    private readonly byte[] m_pushConstant = new byte[PushConstantByteLength];
    private readonly IServiceProvider m_serviceProvider;

    private IGpuComputeCommandPool? m_commandPool;
    private IGpuComputeRecorder? m_computeRecorder;
    private IGpuComputeServices? m_gpu;
    private IGpuDescriptorAllocator? m_descriptorAllocator;
    private IGpuDeviceContext? m_deviceContext;
    private nint m_deviceHandle;
    private bool m_disposed;
    private uint m_height;
    private bool m_imageInitialized;
    private IGpuComputePipeline? m_pipeline;
    private nint m_pool;
    private IGpuQueueSubmitter? m_queueSubmitter;
    private bool m_resourcesReady;
    private nint m_set;
    private IGpuShaderModule? m_shaderModule;
    private IGpuStorageImage? m_storageImage;
    private float m_time;
    private uint m_width;

    /// <summary>Initializes a new instance of the <see cref="ChildSurfaceNode"/> class.</summary>
    /// <param name="serviceProvider">The service provider that resolves the neutral GPU compute factories (the device is taken from the host context).</param>
    /// <param name="bytecode">The compiled child kernel for the host's backend (SPIR-V for Vulkan, DXIL for Direct3D 12).</param>
    /// <exception cref="ArgumentNullException"><paramref name="serviceProvider"/> is <see langword="null"/>.</exception>
    public ChildSurfaceNode(IServiceProvider serviceProvider, ReadOnlyMemory<byte> bytecode) {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        m_bytecode = bytecode;
        m_serviceProvider = serviceProvider;
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;

    /// <summary>
    /// Builds the demo's hosted child viewports for the world compositor: a single <see cref="ChildSurfaceNode"/> in
    /// the bottom-right slot of the 2x2 split, so that quadrant shows this node's animated surface instead of an SDF
    /// camera — the integer-copy child seam end to end.
    /// </summary>
    /// <param name="serviceProvider">The neutral compute service provider for the target backend.</param>
    /// <param name="directX">Whether to load the Direct3D 12 (DXIL) child kernel rather than the Vulkan (SPIR-V) one.</param>
    public static IReadOnlyDictionary<int, IRenderNode> CreateWorldChildren(IServiceProvider serviceProvider, bool directX) {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var bytecode = File.ReadAllBytes(path: Path.Combine(
            path1: CrossBackendShowcase.ShaderDirectory,
            path2: (directX ? "sdf-child.comp.dxil" : "sdf-child.comp.spv")
        ));

        return new Dictionary<int, IRenderNode> {
            [3] = new ChildSurfaceNode(serviceProvider: serviceProvider, bytecode: bytecode),
        };
    }

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

        EnsureResources(gpuDevice: gpuDevice, height: context.TargetHeight, width: context.TargetWidth);
        // (named arguments above; the declaration orders height before width to match the demo's factory-call style)

        m_time += (float)context.DeltaSeconds;

        Render();

        return new Surface(
            Format: SurfaceFormat.R8G8B8A8Unorm,
            Height: m_height,
            ImageViewHandle: m_storageImage!.ImageViewHandle,
            Width: m_width
        );
    }

    private void EnsureResources(IGpuDeviceContext gpuDevice, uint height, uint width) {
        if (
            m_resourcesReady &&
            (m_width == width) &&
            (m_height == height)
        ) {
            return;
        }

        // One cohesive compute-services bundle instead of resolving each granular factory; the granular interfaces
        // are still registered for a node that needs only one of them.
        m_gpu ??= (IGpuComputeServices)m_serviceProvider.GetService(serviceType: typeof(IGpuComputeServices))!;

        if (!m_resourcesReady) {
            m_deviceContext = gpuDevice;
            m_deviceHandle = gpuDevice.DeviceHandle;
            m_computeRecorder = m_gpu.ComputeRecorder;
            m_descriptorAllocator = m_gpu.DescriptorAllocator;
            m_queueSubmitter = m_gpu.QueueSubmitter;

            m_shaderModule = m_gpu.ShaderModuleFactory.Create(deviceContext: gpuDevice, stage: GpuShaderStage.Compute, bytecode: m_bytecode);

            GpuComputeBinding[] bindings = [new GpuComputeBinding(Binding: OutputBindingIndex, Kind: GpuComputeBindingKind.StorageImage)];

            m_pipeline = m_gpu.ComputePipelineFactory.Create(
                bindings: bindings,
                computeShaderModule: m_shaderModule,
                deviceContext: gpuDevice,
                pushConstantBinding: new GpuPushConstantBinding(data: m_pushConstant, offset: 0, stageFlags: GpuShaderStage.Compute)
            );

            // Pool capacity derived from the bindings, not hand-counted.
            var poolSizes = GpuDescriptorPoolSizes.ForSets(bindings);

            m_pool = m_descriptorAllocator.CreatePool(
                combinedImageSamplerCount: poolSizes.CombinedImageSamplerCount,
                deviceHandle: m_deviceHandle,
                maxSets: poolSizes.MaxSets,
                storageBufferCount: poolSizes.StorageBufferCount,
                storageImageCount: poolSizes.StorageImageCount
            );
            m_set = m_descriptorAllocator.AllocateSet(descriptorSetLayoutHandle: m_pipeline.DescriptorSetLayoutHandle, deviceHandle: m_deviceHandle, poolHandle: m_pool);
            m_commandPool = m_gpu.CommandPoolFactory.Create(deviceContext: gpuDevice);
        }

        // (Re)create the extent-sized output and rebind it; the device-local pipeline and pool are extent-independent.
        m_storageImage?.Dispose();
        m_storageImage = m_gpu.StorageImageFactory.Create(deviceContext: gpuDevice, format: Format, height: height, width: width);
        m_descriptorAllocator!.WriteStorageImage(arrayElement: 0, binding: OutputBindingIndex, descriptorSetHandle: m_set, deviceHandle: m_deviceHandle, imageViewHandle: m_storageImage.ImageViewHandle);

        m_height = height;
        m_imageInitialized = false;
        m_resourcesReady = true;
        m_width = width;
    }

    private void Render() {
        var recorder = m_computeRecorder!;
        var commandBuffer = m_commandPool!.CommandBufferHandle;

        var pushWords = MemoryMarshal.Cast<byte, uint>(span: m_pushConstant.AsSpan());

        pushWords[0] = m_width;
        pushWords[1] = m_height;
        MemoryMarshal.Cast<byte, float>(span: m_pushConstant.AsSpan())[2] = m_time;
        pushWords[3] = 0;

        recorder.BeginCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);

        // First frame brings the freshly created image into the General (UAV) working layout; afterwards it persists
        // there (written each frame, read by the parent compositor — never sampled as a shader-readable image).
        if (!m_imageInitialized) {
            recorder.TransitionImageLayout(
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuComputeAccess.ShaderWrite,
                destinationStageMask: GpuComputeStage.ComputeShader,
                deviceHandle: m_deviceHandle,
                imageHandle: m_storageImage!.ImageHandle,
                newLayout: GpuImageLayout.General,
                oldLayout: GpuImageLayout.Undefined,
                sourceAccessMask: GpuComputeAccess.None,
                sourceStageMask: GpuComputeStage.TopOfPipe
            );
        }

        recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, pipelineHandle: m_pipeline!.Handle);
        recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: m_set, deviceHandle: m_deviceHandle, pipelineLayoutHandle: m_pipeline.LayoutHandle);
        recorder.PushConstants(commandBufferHandle: commandBuffer, data: m_pushConstant, deviceHandle: m_deviceHandle, offset: 0, pipelineLayoutHandle: m_pipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
        recorder.Dispatch(
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle,
            groupCountX: ((m_width + (WorkgroupEdge - 1)) / WorkgroupEdge),
            groupCountY: ((m_height + (WorkgroupEdge - 1)) / WorkgroupEdge),
            groupCountZ: 1
        );

        recorder.EndCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);

        // Fire-and-forget on the shared queue: this submit is enqueued before the parent's compositor submit, and the
        // parent's compute memory barrier orders this node's writes ahead of the composite read.
        m_queueSubmitter!.Submit(commandBufferHandles: [commandBuffer], deviceContext: m_deviceContext!);

        m_imageInitialized = true;
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        // The per-frame submits are fire-and-forget, so a frame may still be in flight at teardown.
        m_deviceContext?.WaitIdle();
        m_commandPool?.Dispose();
        m_pipeline?.Dispose();

        if ((0 != m_pool) && (m_descriptorAllocator is not null)) {
            m_descriptorAllocator.DestroyPool(deviceHandle: m_deviceHandle, poolHandle: m_pool);
            m_pool = 0;
        }

        m_storageImage?.Dispose();
        m_shaderModule?.Dispose();
    }
}
