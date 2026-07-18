using Puck.Capture;
using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
namespace Puck.Post;

/// <summary>
/// Tier-B stage B3. The source-agnostic viewport compositor, same-device: <c>viewport-composite.comp</c> places each
/// pane's rect-sized source into its normalized screen region by 1:1 copy. The stage composites a HETEROGENEOUS
/// layout — a raw <c>sdf-child</c> pane at native pane resolution (left half) beside the same pattern rendered small
/// (64×64) and NEAREST-resampled up 4x (right half) — then proves the composite against a CPU oracle built from the
/// same run's readbacks: every left-half pixel must equal the raw pane's texel and every right-half pixel must equal
/// the small source's texel under the exact 4x nearest mapping, bit-for-bit. That pins region placement, the 1:1
/// copy, the pane-count/rect push layout, and the sampled upscale feeding a composited pane, without any
/// cross-backend dependency (Tier C carries that half).
/// </summary>
internal sealed class ViewportsStage : IPostStage {
    private const GpuPixelFormat Format = GpuPixelFormat.R8G8B8A8Unorm;
    private const int MaxViewports = 4;   // viewport-composite.comp: sources[4]
    private const uint OutputHeight = 256;
    private const uint OutputWidth = 512;
    private const uint PaneSize = 256;    // each half pane is 256×256
    private const uint SmallSize = 64;    // the right pane's pre-upscale source (exact 4x)
    private const int ChildPushByteLength = 16;
    private const int CompositePushByteLength = (16 + ((sizeof(float) * 4) * MaxViewports));
    private const int ResamplePushByteLength = 32;
    private const uint WorkgroupEdge = 8;

    /// <inheritdoc/>
    public string Name => "viewports";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.B;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var device = context.RequireGpuDevice();
        var gpu = context.Resolve<IGpuComputeServices>();
        var deviceHandle = device.DeviceHandle;

        var childBytecode = PostShaders.Read(folder: "Sdf", file: "sdf-child.comp.spv");
        var resampleBytecode = PostShaders.Read(folder: "Resample", file: "resample.comp.spv");
        var compositeBytecode = PostShaders.Read(folder: "Viewport", file: "viewport-composite.comp.spv");

        // Pushes: the raw pane's child fill (256²), the small source's child fill (64²), the 4x nearest resample, and
        // the two-pane composite (left = raw, right = resampled).
        var childAPush = ChildPush(extent: PaneSize);
        var childBPush = ChildPush(extent: SmallSize);
        var resamplePush = new byte[ResamplePushByteLength];
        var resampleWords = MemoryMarshal.Cast<byte, uint>(span: resamplePush.AsSpan());
        var resampleFloats = MemoryMarshal.Cast<byte, float>(span: resamplePush.AsSpan());

        resampleWords[0] = PaneSize;
        resampleWords[1] = PaneSize; // outExtent
        resampleFloats[4] = 1f;
        resampleFloats[5] = 1f;      // srcSize (whole source; origin stays 0)
        resampleWords[6] = 1;        // no pixelation
        resampleWords[7] = 0;        // no quantization

        var compositePush = new byte[CompositePushByteLength];
        var compositeWords = MemoryMarshal.Cast<byte, uint>(span: compositePush.AsSpan());
        var compositeFloats = MemoryMarshal.Cast<byte, float>(span: compositePush.AsSpan());

        compositeWords[0] = OutputWidth;
        compositeWords[1] = OutputHeight; // imageExtent
        compositeWords[2] = 2;            // viewportCount
        compositeFloats[4] = 0f; compositeFloats[5] = 0f; compositeFloats[6] = 0.5f; compositeFloats[7] = 1f;  // left half
        compositeFloats[8] = 0.5f; compositeFloats[9] = 0f; compositeFloats[10] = 0.5f; compositeFloats[11] = 1f; // right half

        GpuComputeBinding[] childBindings = [new GpuComputeBinding(Binding: 0, Kind: GpuComputeBindingKind.StorageImage)];
        GpuComputeBinding[] resampleBindings = [
            new GpuComputeBinding(Binding: 0, Kind: GpuComputeBindingKind.StorageImage),
            new GpuComputeBinding(Binding: 1, Kind: GpuComputeBindingKind.SampledImage),
        ];
        GpuComputeBinding[] compositeBindings = [
            new GpuComputeBinding(Binding: 0, Kind: GpuComputeBindingKind.StorageImage),
            new GpuComputeBinding(Binding: 1, Kind: GpuComputeBindingKind.StorageImage, Count: MaxViewports),
        ];

        using var childModule = gpu.ShaderModuleFactory.Create(deviceContext: device, stage: GpuShaderStage.Compute, bytecode: childBytecode);
        using var resampleModule = gpu.ShaderModuleFactory.Create(deviceContext: device, stage: GpuShaderStage.Compute, bytecode: resampleBytecode);
        using var compositeModule = gpu.ShaderModuleFactory.Create(deviceContext: device, stage: GpuShaderStage.Compute, bytecode: compositeBytecode);
        using var childPipeline = gpu.ComputePipelineFactory.Create(bindings: childBindings, computeShaderModule: childModule, deviceContext: device, pushConstantBinding: new GpuPushConstantBinding(data: childAPush, offset: 0, stageFlags: GpuShaderStage.Compute));
        using var resamplePipeline = gpu.ComputePipelineFactory.Create(bindings: resampleBindings, computeShaderModule: resampleModule, deviceContext: device, pushConstantBinding: new GpuPushConstantBinding(data: resamplePush, offset: 0, stageFlags: GpuShaderStage.Compute), samplerFilter: GpuSamplerFilter.Nearest);
        using var compositePipeline = gpu.ComputePipelineFactory.Create(bindings: compositeBindings, computeShaderModule: compositeModule, deviceContext: device, pushConstantBinding: new GpuPushConstantBinding(data: compositePush, offset: 0, stageFlags: GpuShaderStage.Compute));

        using var rawPane = gpu.StorageImageFactory.Create(deviceContext: device, format: Format, height: PaneSize, width: PaneSize);
        using var smallSource = gpu.StorageImageFactory.Create(deviceContext: device, format: Format, height: SmallSize, width: SmallSize);
        using var resampledPane = gpu.StorageImageFactory.Create(deviceContext: device, format: Format, height: PaneSize, width: PaneSize);
        using var output = gpu.StorageImageFactory.Create(deviceContext: device, format: Format, height: OutputHeight, width: OutputWidth);
        using var commandPool = gpu.CommandPoolFactory.Create(deviceContext: device);

        var sampler = gpu.DescriptorAllocator.CreateSampler(deviceHandle: deviceHandle, filter: GpuSamplerFilter.Nearest);
        var poolSizes = GpuDescriptorPoolSizes.ForSets(childBindings, childBindings, resampleBindings, compositeBindings);
        var pool = gpu.DescriptorAllocator.CreatePool(deviceHandle: deviceHandle, sizes: poolSizes);

        try {
            var rawSet = gpu.DescriptorAllocator.AllocateSet(descriptorSetLayoutHandle: childPipeline.DescriptorSetLayoutHandle, deviceHandle: deviceHandle, poolHandle: pool);
            var smallSet = gpu.DescriptorAllocator.AllocateSet(descriptorSetLayoutHandle: childPipeline.DescriptorSetLayoutHandle, deviceHandle: deviceHandle, poolHandle: pool);
            var resampleSet = gpu.DescriptorAllocator.AllocateSet(descriptorSetLayoutHandle: resamplePipeline.DescriptorSetLayoutHandle, deviceHandle: deviceHandle, poolHandle: pool);
            var compositeSet = gpu.DescriptorAllocator.AllocateSet(descriptorSetLayoutHandle: compositePipeline.DescriptorSetLayoutHandle, deviceHandle: deviceHandle, poolHandle: pool);

            gpu.DescriptorAllocator.WriteStorageImage(arrayElement: 0, binding: 0, descriptorSetHandle: rawSet, deviceHandle: deviceHandle, imageViewHandle: rawPane.ImageViewHandle);
            gpu.DescriptorAllocator.WriteStorageImage(arrayElement: 0, binding: 0, descriptorSetHandle: smallSet, deviceHandle: deviceHandle, imageViewHandle: smallSource.ImageViewHandle);
            gpu.DescriptorAllocator.WriteStorageImage(arrayElement: 0, binding: 0, descriptorSetHandle: resampleSet, deviceHandle: deviceHandle, imageViewHandle: resampledPane.ImageViewHandle);
            gpu.DescriptorAllocator.WriteCombinedImageSampler(arrayElement: 0, binding: 1, descriptorSetHandle: resampleSet, deviceHandle: deviceHandle, imageViewHandle: smallSource.ImageViewHandle, samplerHandle: sampler);
            gpu.DescriptorAllocator.WriteStorageImage(arrayElement: 0, binding: 0, descriptorSetHandle: compositeSet, deviceHandle: deviceHandle, imageViewHandle: output.ImageViewHandle);
            // Source array: slot 0 = raw pane, slot 1 = resampled pane; the unused elements duplicate slot 0 (every
            // bound array element must be a valid descriptor; the kernel never reads past viewportCount).
            gpu.DescriptorAllocator.WriteStorageImage(arrayElement: 0, binding: 1, descriptorSetHandle: compositeSet, deviceHandle: deviceHandle, imageViewHandle: rawPane.ImageViewHandle);
            gpu.DescriptorAllocator.WriteStorageImage(arrayElement: 1, binding: 1, descriptorSetHandle: compositeSet, deviceHandle: deviceHandle, imageViewHandle: resampledPane.ImageViewHandle);
            gpu.DescriptorAllocator.WriteStorageImage(arrayElement: 2, binding: 1, descriptorSetHandle: compositeSet, deviceHandle: deviceHandle, imageViewHandle: rawPane.ImageViewHandle);
            gpu.DescriptorAllocator.WriteStorageImage(arrayElement: 3, binding: 1, descriptorSetHandle: compositeSet, deviceHandle: deviceHandle, imageViewHandle: rawPane.ImageViewHandle);

            var recorder = gpu.ComputeRecorder;
            var commandBuffer = commandPool.CommandBufferHandle;

            recorder.BeginCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle);

            // Fill the raw pane (256²) and the small source (64²) with the deterministic child pattern.
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderWrite, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: deviceHandle, imageHandle: rawPane.ImageHandle, newLayout: GpuImageLayout.General, oldLayout: GpuImageLayout.Undefined, sourceAccessMask: GpuComputeAccess.None, sourceStageMask: GpuComputeStage.TopOfPipe);
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderWrite, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: deviceHandle, imageHandle: smallSource.ImageHandle, newLayout: GpuImageLayout.General, oldLayout: GpuImageLayout.Undefined, sourceAccessMask: GpuComputeAccess.None, sourceStageMask: GpuComputeStage.TopOfPipe);
            recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle, pipelineHandle: childPipeline.Handle);
            recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: rawSet, deviceHandle: deviceHandle, pipelineLayoutHandle: childPipeline.LayoutHandle);
            recorder.PushConstants(commandBufferHandle: commandBuffer, data: childAPush, deviceHandle: deviceHandle, offset: 0, pipelineLayoutHandle: childPipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
            recorder.Dispatch(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle, groupCountX: Groups(size: PaneSize), groupCountY: Groups(size: PaneSize), groupCountZ: 1);
            recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: smallSet, deviceHandle: deviceHandle, pipelineLayoutHandle: childPipeline.LayoutHandle);
            recorder.PushConstants(commandBufferHandle: commandBuffer, data: childBPush, deviceHandle: deviceHandle, offset: 0, pipelineLayoutHandle: childPipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
            recorder.Dispatch(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle, groupCountX: Groups(size: SmallSize), groupCountY: Groups(size: SmallSize), groupCountZ: 1);

            // 4x nearest-upscale the small source into the right pane (sample it shader-readable; the resampled pane
            // stays in the General working layout the composite reads).
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderRead, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: deviceHandle, imageHandle: smallSource.ImageHandle, newLayout: GpuImageLayout.ShaderReadOnly, oldLayout: GpuImageLayout.General, sourceAccessMask: GpuComputeAccess.ShaderWrite, sourceStageMask: GpuComputeStage.ComputeShader);
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderWrite, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: deviceHandle, imageHandle: resampledPane.ImageHandle, newLayout: GpuImageLayout.General, oldLayout: GpuImageLayout.Undefined, sourceAccessMask: GpuComputeAccess.None, sourceStageMask: GpuComputeStage.TopOfPipe);
            recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle, pipelineHandle: resamplePipeline.Handle);
            recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: resampleSet, deviceHandle: deviceHandle, pipelineLayoutHandle: resamplePipeline.LayoutHandle);
            recorder.PushConstants(commandBufferHandle: commandBuffer, data: resamplePush, deviceHandle: deviceHandle, offset: 0, pipelineLayoutHandle: resamplePipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
            recorder.Dispatch(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle, groupCountX: Groups(size: PaneSize), groupCountY: Groups(size: PaneSize), groupCountZ: 1);

            // Order the pane writes before the composite's storage reads, then composite the two halves.
            recorder.MemoryBarrier(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderRead, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: deviceHandle, sourceAccessMask: GpuComputeAccess.ShaderWrite, sourceStageMask: GpuComputeStage.ComputeShader);
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderWrite, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: deviceHandle, imageHandle: output.ImageHandle, newLayout: GpuImageLayout.General, oldLayout: GpuImageLayout.Undefined, sourceAccessMask: GpuComputeAccess.None, sourceStageMask: GpuComputeStage.TopOfPipe);
            recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle, pipelineHandle: compositePipeline.Handle);
            recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: compositeSet, deviceHandle: deviceHandle, pipelineLayoutHandle: compositePipeline.LayoutHandle);
            recorder.PushConstants(commandBufferHandle: commandBuffer, data: compositePush, deviceHandle: deviceHandle, offset: 0, pipelineLayoutHandle: compositePipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
            recorder.Dispatch(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle, groupCountX: Groups(size: OutputWidth), groupCountY: Groups(size: OutputHeight), groupCountZ: 1);

            // Hand the composite and the oracle inputs off readback-readable.
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderRead, destinationStageMask: GpuComputeStage.FragmentShader, deviceHandle: deviceHandle, imageHandle: output.ImageHandle, newLayout: GpuImageLayout.ShaderReadOnly, oldLayout: GpuImageLayout.General, sourceAccessMask: GpuComputeAccess.ShaderWrite, sourceStageMask: GpuComputeStage.ComputeShader);
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderRead, destinationStageMask: GpuComputeStage.FragmentShader, deviceHandle: deviceHandle, imageHandle: rawPane.ImageHandle, newLayout: GpuImageLayout.ShaderReadOnly, oldLayout: GpuImageLayout.General, sourceAccessMask: GpuComputeAccess.ShaderRead, sourceStageMask: GpuComputeStage.ComputeShader);
            recorder.EndCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle);

            gpu.QueueSubmitter.SubmitAndWait(commandBufferHandles: [commandBuffer], deviceContext: device);

            using var readback = gpu.SurfaceTransferFactory.CreateReadback(deviceContext: device);

            var composite = readback.Read(bytesPerPixel: 4, deviceContext: device, format: Format, height: OutputHeight, sourceImageHandle: output.ImageHandle, width: OutputWidth).ToArray();
            var raw = readback.Read(bytesPerPixel: 4, deviceContext: device, format: Format, height: PaneSize, sourceImageHandle: rawPane.ImageHandle, width: PaneSize).ToArray();
            var small = readback.Read(bytesPerPixel: 4, deviceContext: device, format: Format, height: SmallSize, sourceImageHandle: smallSource.ImageHandle, width: SmallSize).ToArray();

            var artifactPath = Path.Combine(path1: context.ArtifactsDirectory, path2: "viewports.png");

            _ = Directory.CreateDirectory(path: context.ArtifactsDirectory);
            PngEncoder.Write(height: (int)OutputHeight, path: artifactPath, rgba: composite, width: (int)OutputWidth);

            // The CPU oracle: left half = the raw pane texel; right half = the small source under the exact 4x nearest
            // mapping (destination pixel centers land strictly inside source texels, so the mapping is x/4 integer).
            for (var y = 0u; (y < OutputHeight); y++) {
                for (var x = 0u; (x < OutputWidth); x++) {
                    var actual = (int)(((y * OutputWidth) + x) * 4);
                    int expected;
                    string half;

                    if (x < PaneSize) {
                        expected = (int)(((y * PaneSize) + x) * 4);
                        half = "left(raw)";

                        if ((composite[actual] != raw[expected]) || (composite[(actual + 1)] != raw[(expected + 1)]) || (composite[(actual + 2)] != raw[(expected + 2)])) {
                            return PostStageOutcome.Fail(artifactPath: artifactPath, detail: $"composite({x},{y}) {half} != oracle — region placement or the 1:1 copy broke");
                        }
                    } else {
                        expected = (int)(((((y / 4) * SmallSize) + ((x - PaneSize) / 4))) * 4);
                        half = "right(4x nearest)";

                        if ((composite[actual] != small[expected]) || (composite[(actual + 1)] != small[(expected + 1)]) || (composite[(actual + 2)] != small[(expected + 2)])) {
                            return PostStageOutcome.Fail(artifactPath: artifactPath, detail: $"composite({x},{y}) {half} != oracle — the resampled pane or its placement broke");
                        }
                    }

                    if (composite[(actual + 3)] != 255) {
                        return PostStageOutcome.Fail(artifactPath: artifactPath, detail: $"composite({x},{y}) alpha {composite[(actual + 3)]} != 255");
                    }
                }
            }

            return PostStageOutcome.Pass(artifactPath: artifactPath, detail: $"{OutputWidth}x{OutputHeight} heterogeneous composite (raw child | 4x-nearest resampled pane) matches the CPU oracle bit-for-bit");
        } finally {
            gpu.DescriptorAllocator.DestroySampler(deviceHandle: deviceHandle, samplerHandle: sampler);
            gpu.DescriptorAllocator.DestroyPool(deviceHandle: deviceHandle, poolHandle: pool);
        }
    }

    private static uint Groups(uint size) => ((size + (WorkgroupEdge - 1)) / WorkgroupEdge);
    private static byte[] ChildPush(uint extent) {
        var push = new byte[ChildPushByteLength];
        var words = MemoryMarshal.Cast<byte, uint>(span: push.AsSpan());

        words[0] = extent;
        words[1] = extent; // ChildParams.extent; time (word 2) and pad (word 3) stay 0.

        return push;
    }
}
