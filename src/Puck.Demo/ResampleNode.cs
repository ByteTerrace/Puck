using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Demo;

/// <summary>
/// A reusable compute <see cref="IRenderNode"/> that produces a pane by SAMPLING a small source image into the
/// pane's pixel rect — the first consumer of the sampled-image compute binding in the compositing path. It fills a
/// fixed small input with the neutral <c>sdf-child.comp</c> test pattern once, leaves it shader-readable, then runs
/// <c>resample.comp</c> to scale/filter that input into a (potentially much larger) rect-sized output left in the
/// General (UAV) working layout — exactly the integer-copy contract <see cref="ViewportCompositorNode"/> consumes.
/// <para>
/// The filter and cell size make this both the "fit an arbitrary-resolution source to a pane" node (LINEAR, no
/// cells) and the retro PIXELATION node (NEAREST + a cell size &gt; 1): a low-res source NEAREST-upscaled into a big
/// pane is unmistakably blocky next to a native-resolution neighbour.
/// </para>
/// </summary>
internal sealed class ResampleNode : IRenderNode {
    private const uint Format = GpuPixelFormat.R8G8B8A8Unorm;
    private const uint OutputBindingIndex = 0;  // both kernels: Output at binding 0 (register u0)
    private const uint SourceBindingIndex = 1;  // resample.comp: Source combined-image-sampler at binding 1 (t0/s0)
    private const uint WorkgroupEdge = 8;
    private const int ChildPushByteLength = 16;    // ChildParams    { uint2 extent; float time; uint pad; }
    private const int ResamplePushByteLength = 32; // ResampleParams { uint2 outExtent; float2 srcOrigin; float2 srcSize; uint cellSize; uint quantizeLevels; }

    private readonly ReadOnlyMemory<byte> m_childBytecode;
    private readonly byte[] m_childPush = new byte[ChildPushByteLength];
    private readonly uint m_cellSize;
    private readonly NodeDescriptor m_descriptor = new(
        Name: "resample",
        SurfaceId: SurfaceId.New()
    );
    private readonly uint m_filter;
    private readonly uint m_inputSize;
    private readonly uint m_quantizeLevels;
    private readonly ReadOnlyMemory<byte> m_resampleBytecode;
    private readonly byte[] m_resamplePush = new byte[ResamplePushByteLength];
    private readonly IServiceProvider m_serviceProvider;
    private IGpuComputeCommandPool? m_commandPool;
    private IGpuComputeRecorder? m_computeRecorder;
    private IGpuComputePipeline? m_childPipeline;
    private nint m_childSet;
    private IGpuShaderModule? m_childShaderModule;
    private IGpuDescriptorAllocator? m_descriptorAllocator;
    private IGpuDeviceContext? m_deviceContext;
    private nint m_deviceHandle;
    private bool m_disposed;
    private IGpuComputeServices? m_gpu;
    private bool m_inputFilled;
    private uint m_height;
    private IGpuStorageImage? m_inputImage;
    private bool m_outputInitialized;
    private IGpuStorageImage? m_outputImage;
    private nint m_pool;
    private IGpuQueueSubmitter? m_queueSubmitter;
    private IGpuComputePipeline? m_resamplePipeline;
    private nint m_resampleSet;
    private IGpuShaderModule? m_resampleShaderModule;
    private bool m_resourcesReady;
    private nint m_sampler;
    private uint m_width;

    /// <summary>Initializes a new instance of the <see cref="ResampleNode"/> class.</summary>
    /// <param name="serviceProvider">The service provider that resolves the neutral GPU compute services (the device comes from the host context).</param>
    /// <param name="childBytecode">The compiled <c>sdf-child</c> kernel (fills the input) for the host backend.</param>
    /// <param name="resampleBytecode">The compiled <c>resample</c> kernel (samples the input into the output) for the host backend.</param>
    /// <param name="filter">The sampler filter (<see cref="GpuSamplerFilter.Linear"/> fit, or <see cref="GpuSamplerFilter.Nearest"/> retro).</param>
    /// <param name="cellSize">The pixelation cell size in output pixels (1 = off).</param>
    /// <param name="quantizeLevels">The per-channel color levels (0 = off).</param>
    /// <param name="inputSize">The square source extent the pattern is rendered at before resampling (small to make scaling visible).</param>
    public ResampleNode(IServiceProvider serviceProvider, ReadOnlyMemory<byte> childBytecode, ReadOnlyMemory<byte> resampleBytecode, uint filter, uint cellSize = 1, uint quantizeLevels = 0, uint inputSize = 48) {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        m_cellSize = cellSize;
        m_childBytecode = childBytecode;
        m_filter = filter;
        m_inputSize = Math.Max(1u, inputSize);
        m_quantizeLevels = quantizeLevels;
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

        EnsureResources(gpuDevice: gpuDevice, height: context.TargetHeight, width: context.TargetWidth);
        Render();

        // A rect-sized, General-layout storage image: the integer-copy source contract the compositor reads.
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

            GpuComputeBinding[] childBindings = [new GpuComputeBinding(Binding: OutputBindingIndex, Kind: GpuComputeBindingKind.StorageImage)];
            GpuComputeBinding[] resampleBindings = [
                new GpuComputeBinding(Binding: OutputBindingIndex, Kind: GpuComputeBindingKind.StorageImage),
                new GpuComputeBinding(Binding: SourceBindingIndex, Kind: GpuComputeBindingKind.SampledImage),
            ];

            m_childShaderModule = m_gpu.ShaderModuleFactory.Create(deviceContext: gpuDevice, stage: GpuShaderStage.Compute, bytecode: m_childBytecode);
            m_resampleShaderModule = m_gpu.ShaderModuleFactory.Create(deviceContext: gpuDevice, stage: GpuShaderStage.Compute, bytecode: m_resampleBytecode);
            m_childPipeline = m_gpu.ComputePipelineFactory.Create(
                bindings: childBindings,
                computeShaderModule: m_childShaderModule,
                deviceContext: gpuDevice,
                pushConstantBinding: new GpuPushConstantBinding(data: m_childPush, offset: 0, stageFlags: GpuShaderStage.Compute)
            );
            m_resamplePipeline = m_gpu.ComputePipelineFactory.Create(
                bindings: resampleBindings,
                computeShaderModule: m_resampleShaderModule,
                deviceContext: gpuDevice,
                pushConstantBinding: new GpuPushConstantBinding(data: m_resamplePush, offset: 0, stageFlags: GpuShaderStage.Compute),
                samplerFilter: m_filter
            );
            m_inputImage = m_gpu.StorageImageFactory.Create(deviceContext: gpuDevice, format: Format, height: m_inputSize, width: m_inputSize);
            m_sampler = m_descriptorAllocator.CreateSampler(deviceHandle: m_deviceHandle, filter: m_filter);

            var poolSizes = GpuDescriptorPoolSizes.ForSets(childBindings, resampleBindings);

            m_pool = m_descriptorAllocator.CreatePool(deviceHandle: m_deviceHandle, sizes: poolSizes);
            m_childSet = m_descriptorAllocator.AllocateSet(descriptorSetLayoutHandle: m_childPipeline.DescriptorSetLayoutHandle, deviceHandle: m_deviceHandle, poolHandle: m_pool);
            m_resampleSet = m_descriptorAllocator.AllocateSet(descriptorSetLayoutHandle: m_resamplePipeline.DescriptorSetLayoutHandle, deviceHandle: m_deviceHandle, poolHandle: m_pool);
            m_descriptorAllocator.WriteStorageImage(arrayElement: 0, binding: OutputBindingIndex, descriptorSetHandle: m_childSet, deviceHandle: m_deviceHandle, imageViewHandle: m_inputImage.ImageViewHandle);
            m_descriptorAllocator.WriteCombinedImageSampler(arrayElement: 0, binding: SourceBindingIndex, descriptorSetHandle: m_resampleSet, deviceHandle: m_deviceHandle, imageViewHandle: m_inputImage.ImageViewHandle, samplerHandle: m_sampler);
            m_commandPool = m_gpu.CommandPoolFactory.Create(deviceContext: gpuDevice);

            // ChildParams: extent = inputSize; time/pad = 0 (a fixed, deterministic source pattern).
            var childWords = MemoryMarshal.Cast<byte, uint>(span: m_childPush.AsSpan());

            childWords[0] = m_inputSize;
            childWords[1] = m_inputSize;
        }

        // (Re)create the rect-sized output and rebind it; the device-local pipelines, input, sampler, and pool are
        // extent-independent. A size change re-runs the first-frame input fill (m_firstRender is left true on the
        // initial build; a later resize re-inits the output layout below).
        m_outputImage?.Dispose();
        m_outputImage = m_gpu.StorageImageFactory.Create(deviceContext: gpuDevice, format: Format, height: height, width: width);
        m_descriptorAllocator!.WriteStorageImage(arrayElement: 0, binding: OutputBindingIndex, descriptorSetHandle: m_resampleSet, deviceHandle: m_deviceHandle, imageViewHandle: m_outputImage.ImageViewHandle);
        m_outputInitialized = false; // the freshly created output starts Undefined; Render brings it into General.

        // ResampleParams: sample the whole input into the output rect, with the configured cell size + quantization.
        var resampleWords = MemoryMarshal.Cast<byte, uint>(span: m_resamplePush.AsSpan());
        var resampleFloats = MemoryMarshal.Cast<byte, float>(span: m_resamplePush.AsSpan());

        resampleWords[0] = width;
        resampleWords[1] = height; // outExtent
        resampleFloats[2] = 0f;
        resampleFloats[3] = 0f;    // srcOrigin
        resampleFloats[4] = 1f;
        resampleFloats[5] = 1f;    // srcSize (whole source)
        resampleWords[6] = m_cellSize;
        resampleWords[7] = m_quantizeLevels;

        m_height = height;
        m_width = width;
        m_resourcesReady = true;
    }
    private void Render() {
        var recorder = m_computeRecorder!;
        var commandBuffer = m_commandPool!.CommandBufferHandle;

        recorder.BeginCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);

        if (!m_inputFilled) {
            // Fill the fixed input ONCE with the test pattern, then leave it shader-readable so the resample can SAMPLE
            // it every frame (the pattern is time-0 and static, so it never needs re-filling — even across a resize,
            // which only recreates the output).
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderWrite, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: m_deviceHandle, imageHandle: m_inputImage!.ImageHandle, newLayout: GpuImageLayout.General, oldLayout: GpuImageLayout.Undefined, sourceAccessMask: GpuComputeAccess.None, sourceStageMask: GpuComputeStage.TopOfPipe);
            recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, pipelineHandle: m_childPipeline!.Handle);
            recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: m_childSet, deviceHandle: m_deviceHandle, pipelineLayoutHandle: m_childPipeline.LayoutHandle);
            recorder.PushConstants(commandBufferHandle: commandBuffer, data: m_childPush, deviceHandle: m_deviceHandle, offset: 0, pipelineLayoutHandle: m_childPipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
            recorder.Dispatch(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, groupCountX: ((m_inputSize + (WorkgroupEdge - 1)) / WorkgroupEdge), groupCountY: ((m_inputSize + (WorkgroupEdge - 1)) / WorkgroupEdge), groupCountZ: 1);
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderRead, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: m_deviceHandle, imageHandle: m_inputImage.ImageHandle, newLayout: GpuImageLayout.ShaderReadOnly, oldLayout: GpuImageLayout.General, sourceAccessMask: GpuComputeAccess.ShaderWrite, sourceStageMask: GpuComputeStage.ComputeShader);

            m_inputFilled = true;
        }

        if (!m_outputInitialized) {
            // Bring the (possibly just-recreated) output into the General (UAV) working layout the resample writes it
            // in and the compositor reads.
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderWrite, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: m_deviceHandle, imageHandle: m_outputImage!.ImageHandle, newLayout: GpuImageLayout.General, oldLayout: GpuImageLayout.Undefined, sourceAccessMask: GpuComputeAccess.None, sourceStageMask: GpuComputeStage.TopOfPipe);

            m_outputInitialized = true;
        }

        // Resample the input into the output (samples the shader-readable input, writes the General output).
        recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, pipelineHandle: m_resamplePipeline!.Handle);
        recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: m_resampleSet, deviceHandle: m_deviceHandle, pipelineLayoutHandle: m_resamplePipeline.LayoutHandle);
        recorder.PushConstants(commandBufferHandle: commandBuffer, data: m_resamplePush, deviceHandle: m_deviceHandle, offset: 0, pipelineLayoutHandle: m_resamplePipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
        recorder.Dispatch(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, groupCountX: ((m_width + (WorkgroupEdge - 1)) / WorkgroupEdge), groupCountY: ((m_height + (WorkgroupEdge - 1)) / WorkgroupEdge), groupCountZ: 1);

        recorder.EndCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);

        // Fire-and-forget on the shared queue: enqueued ahead of the parent compositor's submit, which barriers this
        // node's output writes before its composite read.
        m_queueSubmitter!.Submit(commandBufferHandles: [commandBuffer], deviceContext: m_deviceContext!);
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_deviceContext?.WaitIdle();
        m_commandPool?.Dispose();
        m_childPipeline?.Dispose();
        m_resamplePipeline?.Dispose();

        if ((0 != m_sampler) && (m_descriptorAllocator is not null)) {
            m_descriptorAllocator.DestroySampler(deviceHandle: m_deviceHandle, samplerHandle: m_sampler);
            m_sampler = 0;
        }

        if ((0 != m_pool) && (m_descriptorAllocator is not null)) {
            m_descriptorAllocator.DestroyPool(deviceHandle: m_deviceHandle, poolHandle: m_pool);
            m_pool = 0;
        }

        m_inputImage?.Dispose();
        m_outputImage?.Dispose();
        m_childShaderModule?.Dispose();
        m_resampleShaderModule?.Dispose();
    }
}
