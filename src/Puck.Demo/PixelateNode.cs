using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Demo;

/// <summary>
/// A retro-pixelation post-effect DECORATOR: a compute <see cref="IRenderNode"/> that wraps another node and hands
/// back its output blocked into cells and (optionally) reduced in color depth — the "8/16-bit" look. It produces the
/// wrapped source at the pane's pixel extent (a General-layout storage image, the integer-copy contract), reads it
/// as a storage image, and writes a same-size output via <c>pixelate.comp</c>. The output is itself a General-layout
/// pane, so a <see cref="PixelateNode"/> drops into the same <see cref="ViewportCompositorNode"/> slot any other pane
/// source would — it is the runtime shape of the Stage-6 <c>{ "$type": "pixelate", "source": {...} }</c> decorator,
/// so ANY source can be retro-ified.
/// </summary>
internal sealed class PixelateNode : IRenderNode {
    private const uint Format = GpuPixelFormat.R8G8B8A8Unorm;
    private const uint OutputBindingIndex = 0; // pixelate.comp: Output at binding 0 (register u0)
    private const uint SourceBindingIndex = 1; // pixelate.comp: Source at binding 1 (register u1)
    private const uint WorkgroupEdge = 8;
    private const int PixelatePushByteLength = 16; // PixelateParams { uint2 extent; uint cellSize; uint quantizeLevels; }

    private readonly uint m_cellSize;
    private readonly NodeDescriptor m_descriptor = new(
        Name: "pixelate",
        SurfaceId: SurfaceId.New()
    );
    private readonly ReadOnlyMemory<byte> m_pixelateBytecode;
    private readonly byte[] m_pixelatePush = new byte[PixelatePushByteLength];
    private readonly uint m_quantizeLevels;
    private readonly IServiceProvider m_serviceProvider;
    private readonly IRenderNode m_source;
    private nint m_boundSourceView;
    private IGpuComputeCommandPool? m_commandPool;
    private IGpuComputePipeline? m_pipeline;
    private IGpuComputeRecorder? m_computeRecorder;
    private IGpuDescriptorAllocator? m_descriptorAllocator;
    private IGpuDeviceContext? m_deviceContext;
    private nint m_deviceHandle;
    private bool m_disposed;
    private IGpuComputeServices? m_gpu;
    private uint m_height;
    private bool m_outputInitialized;
    private IGpuStorageImage? m_outputImage;
    private nint m_pool;
    private IGpuQueueSubmitter? m_queueSubmitter;
    private nint m_set;
    private IGpuShaderModule? m_shaderModule;
    private Surface m_sourceSurface;
    private bool m_resourcesReady;
    private uint m_width;

    /// <summary>Initializes a new instance of the <see cref="PixelateNode"/> class.</summary>
    /// <param name="serviceProvider">The service provider that resolves the neutral GPU compute services (the device comes from the host context).</param>
    /// <param name="source">The wrapped node whose pane is retro-ified; it must hand back a General-layout storage-image surface.</param>
    /// <param name="pixelateBytecode">The compiled <c>pixelate</c> kernel for the host backend (SPIR-V for Vulkan, DXIL for Direct3D 12).</param>
    /// <param name="cellSize">The block size in pixels (1 = off).</param>
    /// <param name="quantizeLevels">The per-channel color levels (0 = off).</param>
    public PixelateNode(IServiceProvider serviceProvider, IRenderNode source, ReadOnlyMemory<byte> pixelateBytecode, uint cellSize, uint quantizeLevels = 0) {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(source);

        m_cellSize = cellSize;
        m_pixelateBytecode = pixelateBytecode;
        m_quantizeLevels = quantizeLevels;
        m_serviceProvider = serviceProvider;
        m_source = source;
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

        // Produce the wrapped source at the pane's pixel extent (so the source and output are the same size; the cell
        // snap downsamples within that). The source submits its writes fire-and-forget ahead of this node's.
        m_sourceSurface = m_source.ProduceFrame(context: in context);

        if (0 == m_sourceSurface.ImageViewHandle) {
            return default;
        }

        EnsureResources(gpuDevice: gpuDevice, height: context.TargetHeight, width: context.TargetWidth);
        BindSource();
        Render();

        return new Surface(
            Format: SurfaceFormat.R8G8B8A8Unorm,
            Height: m_height,
            ImageViewHandle: m_outputImage!.ImageViewHandle,
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

        m_gpu ??= (IGpuComputeServices)m_serviceProvider.GetService(serviceType: typeof(IGpuComputeServices))!;

        if (!m_resourcesReady) {
            m_deviceContext = gpuDevice;
            m_deviceHandle = gpuDevice.DeviceHandle;
            m_computeRecorder = m_gpu.ComputeRecorder;
            m_descriptorAllocator = m_gpu.DescriptorAllocator;
            m_queueSubmitter = m_gpu.QueueSubmitter;

            GpuComputeBinding[] bindings = [
                new GpuComputeBinding(Binding: OutputBindingIndex, Kind: GpuComputeBindingKind.StorageImage),
                new GpuComputeBinding(Binding: SourceBindingIndex, Kind: GpuComputeBindingKind.StorageImage),
            ];

            m_shaderModule = m_gpu.ShaderModuleFactory.Create(deviceContext: gpuDevice, stage: GpuShaderStage.Compute, bytecode: m_pixelateBytecode);
            m_pipeline = m_gpu.ComputePipelineFactory.Create(
                bindings: bindings,
                computeShaderModule: m_shaderModule,
                deviceContext: gpuDevice,
                pushConstantBinding: new GpuPushConstantBinding(data: m_pixelatePush, offset: 0, stageFlags: GpuShaderStage.Compute)
            );

            var poolSizes = GpuDescriptorPoolSizes.ForSets(bindings);

            m_pool = m_descriptorAllocator.CreatePool(deviceHandle: m_deviceHandle, sizes: poolSizes);
            m_set = m_descriptorAllocator.AllocateSet(descriptorSetLayoutHandle: m_pipeline.DescriptorSetLayoutHandle, deviceHandle: m_deviceHandle, poolHandle: m_pool);
            m_commandPool = m_gpu.CommandPoolFactory.Create(deviceContext: gpuDevice);
        }

        m_outputImage?.Dispose();
        m_outputImage = m_gpu.StorageImageFactory.Create(deviceContext: gpuDevice, format: Format, height: height, width: width);
        m_descriptorAllocator!.WriteStorageImage(arrayElement: 0, binding: OutputBindingIndex, descriptorSetHandle: m_set, deviceHandle: m_deviceHandle, imageViewHandle: m_outputImage.ImageViewHandle);
        m_outputInitialized = false; // the freshly created output starts Undefined; Render brings it into General.

        // PixelateParams: source/output extent + the retro knobs.
        var words = MemoryMarshal.Cast<byte, uint>(span: m_pixelatePush.AsSpan());

        words[0] = width;
        words[1] = height;
        words[2] = m_cellSize;
        words[3] = m_quantizeLevels;

        m_height = height;
        m_width = width;
        m_resourcesReady = true;
    }
    private void BindSource() {
        if (m_sourceSurface.ImageViewHandle == m_boundSourceView) {
            return;
        }

        m_descriptorAllocator!.WriteStorageImage(arrayElement: 0, binding: SourceBindingIndex, descriptorSetHandle: m_set, deviceHandle: m_deviceHandle, imageViewHandle: m_sourceSurface.ImageViewHandle);
        m_boundSourceView = m_sourceSurface.ImageViewHandle;
    }
    private void Render() {
        var recorder = m_computeRecorder!;
        var commandBuffer = m_commandPool!.CommandBufferHandle;

        recorder.BeginCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);

        // Order the wrapped source's writes (its own earlier submit on the shared queue) before this node reads them.
        recorder.MemoryBarrier(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuComputeAccess.ShaderRead,
            destinationStageMask: GpuComputeStage.ComputeShader,
            deviceHandle: m_deviceHandle,
            sourceAccessMask: GpuComputeAccess.ShaderWrite,
            sourceStageMask: GpuComputeStage.ComputeShader
        );

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

            m_outputInitialized = true;
        }

        recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, pipelineHandle: m_pipeline!.Handle);
        recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: m_set, deviceHandle: m_deviceHandle, pipelineLayoutHandle: m_pipeline.LayoutHandle);
        recorder.PushConstants(commandBufferHandle: commandBuffer, data: m_pixelatePush, deviceHandle: m_deviceHandle, offset: 0, pipelineLayoutHandle: m_pipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
        recorder.Dispatch(
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle,
            groupCountX: ((m_width + (WorkgroupEdge - 1)) / WorkgroupEdge),
            groupCountY: ((m_height + (WorkgroupEdge - 1)) / WorkgroupEdge),
            groupCountZ: 1
        );

        recorder.EndCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);

        // Fire-and-forget on the shared queue: enqueued ahead of the parent compositor's submit (which barriers this
        // node's output write before its composite read), and after the wrapped source's submit.
        m_queueSubmitter!.Submit(commandBufferHandles: [commandBuffer], deviceContext: m_deviceContext!);
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_deviceContext?.WaitIdle();
        m_source.Dispose();
        m_commandPool?.Dispose();
        m_pipeline?.Dispose();

        if ((0 != m_pool) && (m_descriptorAllocator is not null)) {
            m_descriptorAllocator.DestroyPool(deviceHandle: m_deviceHandle, poolHandle: m_pool);
            m_pool = 0;
        }

        m_outputImage?.Dispose();
        m_shaderModule?.Dispose();
    }
}
