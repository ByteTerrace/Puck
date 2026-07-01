using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
namespace Puck.Post;

/// <summary>
/// Tier-B stage B2. The SAMPLED-IMAGE compute binding, same-device: a read-only texture filtered through a sampler
/// inside a compute kernel (<see cref="GpuComputeBindingKind.SampledImage"/> — a combined image sampler on the Vulkan
/// host). It renders the deterministic <c>sdf-child</c> test pattern into a source image and runs <c>resample.comp</c>
/// over it three ways: a NEAREST identity resample (same extent, whole source), which samples each destination pixel
/// center exactly onto its source texel and so must reproduce the source BIT-FOR-BIT (direct rendering is its own
/// oracle — a broken sampler/descriptor would diverge); and a 2x upscale once LINEAR and once NEAREST, which must
/// DIFFER (the pipeline's filter mode demonstrably reaches the sampler). The cross-backend half of the demo's
/// <c>--validate-resample</c> gate (filtered outputs agreeing within ±1 LSB) lives in Tier C with the other parity
/// stages.
/// </summary>
internal sealed class ResampleStage : IPostStage {
    private const uint Format = GpuPixelFormat.R8G8B8A8Unorm;
    private const uint SourceSize = 64;
    private const uint UpscaleSize = 128;
    private const uint WorkgroupEdge = 8;
    private const int ChildPushByteLength = 16;    // ChildParams    { uint2 extent; float time; uint pad; }
    private const int ResamplePushByteLength = 32; // ResampleParams { uint2 outExtent; float2 srcOrigin; float2 srcSize; uint cellSize; uint quantizeLevels; }

    /// <inheritdoc/>
    public string Name => "resample";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.B;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var device = context.RequireGpuDevice(); if (0 != device.DeviceHandle) { throw new InvalidOperationException(message: "FAULT INJECTION"); }
        var gpu = context.Resolve<IGpuComputeServices>();
        var childBytecode = PostShaders.Read(folder: "Sdf", file: "sdf-child.comp.spv");
        var resampleBytecode = PostShaders.Read(folder: "Resample", file: "resample.comp.spv");

        var identity = Resample(childBytecode: childBytecode, deviceContext: device, filter: GpuSamplerFilter.Nearest, gpu: gpu, outSize: SourceSize, resampleBytecode: resampleBytecode);
        var linear = Resample(childBytecode: childBytecode, deviceContext: device, filter: GpuSamplerFilter.Linear, gpu: gpu, outSize: UpscaleSize, resampleBytecode: resampleBytecode);
        var nearest = Resample(childBytecode: childBytecode, deviceContext: device, filter: GpuSamplerFilter.Nearest, gpu: gpu, outSize: UpscaleSize, resampleBytecode: resampleBytecode);

        if (!identity.Source.AsSpan().SequenceEqual(other: identity.Output.AsSpan())) {
            return PostStageOutcome.Fail(detail: "a nearest identity resample did not reproduce its source bit-for-bit — the sampled-image binding is reading the wrong texels");
        }

        if (linear.Output.AsSpan().SequenceEqual(other: nearest.Output.AsSpan())) {
            return PostStageOutcome.Fail(detail: "the 2x LINEAR and NEAREST upscales are identical — the pipeline's sampler filter mode is not reaching the kernel");
        }

        return PostStageOutcome.Pass(detail: $"{SourceSize}x{SourceSize} sdf-child sampled in compute: nearest identity == source bit-for-bit; {UpscaleSize}x{UpscaleSize} linear vs nearest upscales differ (the filter mode is live)");
    }

    // Renders sdf-child into a SourceSize source image, then resamples it into an outSize output through the compute
    // sampler at the given filter, reading BOTH images back. One command buffer: fill source → make it shader-readable
    // → resample (sample source, write output) → make output readable → submit-and-wait → read both. Ported from the
    // demo's ResampleValidationNode (the worked reference); drives only the neutral compute seam.
    private static (byte[] Source, byte[] Output) Resample(IGpuComputeServices gpu, IGpuDeviceContext deviceContext, byte[] childBytecode, byte[] resampleBytecode, uint outSize, uint filter) {
        var deviceHandle = deviceContext.DeviceHandle;

        var childPush = new byte[ChildPushByteLength];
        var childWords = MemoryMarshal.Cast<byte, uint>(span: childPush.AsSpan());

        childWords[0] = SourceSize;
        childWords[1] = SourceSize; // ChildParams.extent; time (word 2) and pad (word 3) stay 0.

        var resamplePush = new byte[ResamplePushByteLength];
        var resampleWords = MemoryMarshal.Cast<byte, uint>(span: resamplePush.AsSpan());
        var resampleFloats = MemoryMarshal.Cast<byte, float>(span: resamplePush.AsSpan());

        resampleWords[0] = outSize;
        resampleWords[1] = outSize; // outExtent
        resampleFloats[2] = 0f;
        resampleFloats[3] = 0f;     // srcOrigin
        resampleFloats[4] = 1f;
        resampleFloats[5] = 1f;     // srcSize (whole source)
        resampleWords[6] = 1;       // cellSize: 1 = no pixelation
        resampleWords[7] = 0;       // quantizeLevels: 0 = no quantization

        GpuComputeBinding[] childBindings = [new GpuComputeBinding(Binding: 0, Kind: GpuComputeBindingKind.StorageImage)];
        GpuComputeBinding[] resampleBindings = [
            new GpuComputeBinding(Binding: 0, Kind: GpuComputeBindingKind.StorageImage),
            new GpuComputeBinding(Binding: 1, Kind: GpuComputeBindingKind.SampledImage),
        ];

        using var childModule = gpu.ShaderModuleFactory.Create(deviceContext: deviceContext, stage: GpuShaderStage.Compute, bytecode: childBytecode);
        using var resampleModule = gpu.ShaderModuleFactory.Create(deviceContext: deviceContext, stage: GpuShaderStage.Compute, bytecode: resampleBytecode);
        using var childPipeline = gpu.ComputePipelineFactory.Create(
            bindings: childBindings,
            computeShaderModule: childModule,
            deviceContext: deviceContext,
            pushConstantBinding: new GpuPushConstantBinding(data: childPush, offset: 0, stageFlags: GpuShaderStage.Compute)
        );
        using var resamplePipeline = gpu.ComputePipelineFactory.Create(
            bindings: resampleBindings,
            computeShaderModule: resampleModule,
            deviceContext: deviceContext,
            pushConstantBinding: new GpuPushConstantBinding(data: resamplePush, offset: 0, stageFlags: GpuShaderStage.Compute),
            samplerFilter: filter
        );
        using var sourceImage = gpu.StorageImageFactory.Create(deviceContext: deviceContext, format: Format, height: SourceSize, width: SourceSize);
        using var outputImage = gpu.StorageImageFactory.Create(deviceContext: deviceContext, format: Format, height: outSize, width: outSize);
        using var commandPool = gpu.CommandPoolFactory.Create(deviceContext: deviceContext);

        var sampler = gpu.DescriptorAllocator.CreateSampler(deviceHandle: deviceHandle, filter: filter);
        var poolSizes = GpuDescriptorPoolSizes.ForSets(childBindings, resampleBindings);
        var pool = gpu.DescriptorAllocator.CreatePool(deviceHandle: deviceHandle, sizes: poolSizes);

        try {
            var childSet = gpu.DescriptorAllocator.AllocateSet(descriptorSetLayoutHandle: childPipeline.DescriptorSetLayoutHandle, deviceHandle: deviceHandle, poolHandle: pool);
            var resampleSet = gpu.DescriptorAllocator.AllocateSet(descriptorSetLayoutHandle: resamplePipeline.DescriptorSetLayoutHandle, deviceHandle: deviceHandle, poolHandle: pool);

            gpu.DescriptorAllocator.WriteStorageImage(arrayElement: 0, binding: 0, descriptorSetHandle: childSet, deviceHandle: deviceHandle, imageViewHandle: sourceImage.ImageViewHandle);
            gpu.DescriptorAllocator.WriteStorageImage(arrayElement: 0, binding: 0, descriptorSetHandle: resampleSet, deviceHandle: deviceHandle, imageViewHandle: outputImage.ImageViewHandle);
            gpu.DescriptorAllocator.WriteCombinedImageSampler(arrayElement: 0, binding: 1, descriptorSetHandle: resampleSet, deviceHandle: deviceHandle, imageViewHandle: sourceImage.ImageViewHandle, samplerHandle: sampler);

            var recorder = gpu.ComputeRecorder;
            var commandBuffer = commandPool.CommandBufferHandle;
            var childGroups = ((SourceSize + (WorkgroupEdge - 1)) / WorkgroupEdge);
            var outputGroups = ((outSize + (WorkgroupEdge - 1)) / WorkgroupEdge);

            recorder.BeginCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle);

            // Fill the source with sdf-child (writes it in the General/UAV working layout).
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderWrite, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: deviceHandle, imageHandle: sourceImage.ImageHandle, newLayout: GpuImageLayout.General, oldLayout: GpuImageLayout.Undefined, sourceAccessMask: GpuComputeAccess.None, sourceStageMask: GpuComputeStage.TopOfPipe);
            recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle, pipelineHandle: childPipeline.Handle);
            recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: childSet, deviceHandle: deviceHandle, pipelineLayoutHandle: childPipeline.LayoutHandle);
            recorder.PushConstants(commandBufferHandle: commandBuffer, data: childPush, deviceHandle: deviceHandle, offset: 0, pipelineLayoutHandle: childPipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
            recorder.Dispatch(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle, groupCountX: childGroups, groupCountY: childGroups, groupCountZ: 1);

            // Make the source shader-readable so the resample kernel can SAMPLE it; this barrier also orders the fill's
            // writes before the sample's reads (compute → compute).
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderRead, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: deviceHandle, imageHandle: sourceImage.ImageHandle, newLayout: GpuImageLayout.ShaderReadOnly, oldLayout: GpuImageLayout.General, sourceAccessMask: GpuComputeAccess.ShaderWrite, sourceStageMask: GpuComputeStage.ComputeShader);

            // Bring the output into the General/UAV working layout for the resample to write.
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderWrite, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: deviceHandle, imageHandle: outputImage.ImageHandle, newLayout: GpuImageLayout.General, oldLayout: GpuImageLayout.Undefined, sourceAccessMask: GpuComputeAccess.None, sourceStageMask: GpuComputeStage.TopOfPipe);
            recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle, pipelineHandle: resamplePipeline.Handle);
            recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: resampleSet, deviceHandle: deviceHandle, pipelineLayoutHandle: resamplePipeline.LayoutHandle);
            recorder.PushConstants(commandBufferHandle: commandBuffer, data: resamplePush, deviceHandle: deviceHandle, offset: 0, pipelineLayoutHandle: resamplePipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
            recorder.Dispatch(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle, groupCountX: outputGroups, groupCountY: outputGroups, groupCountZ: 1);

            // Make the output readback-readable.
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderRead, destinationStageMask: GpuComputeStage.FragmentShader, deviceHandle: deviceHandle, imageHandle: outputImage.ImageHandle, newLayout: GpuImageLayout.ShaderReadOnly, oldLayout: GpuImageLayout.General, sourceAccessMask: GpuComputeAccess.ShaderWrite, sourceStageMask: GpuComputeStage.ComputeShader);
            recorder.EndCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle);

            gpu.QueueSubmitter.SubmitAndWait(commandBufferHandles: [commandBuffer], deviceContext: deviceContext);

            using var readback = gpu.SurfaceTransferFactory.CreateReadback(deviceContext: deviceContext);

            var source = readback.Read(bytesPerPixel: 4, deviceContext: deviceContext, format: Format, height: SourceSize, sourceImageHandle: sourceImage.ImageHandle, width: SourceSize).ToArray();
            var output = readback.Read(bytesPerPixel: 4, deviceContext: deviceContext, format: Format, height: outSize, sourceImageHandle: outputImage.ImageHandle, width: outSize).ToArray();

            return (source, output);
        } finally {
            gpu.DescriptorAllocator.DestroySampler(deviceHandle: deviceHandle, samplerHandle: sampler);
            gpu.DescriptorAllocator.DestroyPool(deviceHandle: deviceHandle, poolHandle: pool);
        }
    }
}
