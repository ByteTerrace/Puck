using System.Runtime.InteropServices;
using Puck.Abstractions;

namespace Puck.Post;

/// <summary>
/// Tier-B stage B4. The retro pixelation decorator, same-device: <c>pixelate.comp</c> reads a source pane as a
/// storage image and writes a cell-snapped (every pixel takes its cell CENTER's color) and posterized (per-channel
/// depth reduction to N levels) copy. The kernel's math is exactly CPU-replicable —
/// <c>cellCoord = (p/cell)*cell + cell/2</c> clamped, then <c>round(c*(levels-1))/(levels-1)</c> on unorm floats — so
/// the stage renders the deterministic <c>sdf-child</c> pattern, pixelates it (10 px cells, 6 levels), and asserts
/// the GPU output equals the CPU oracle computed from the same run's source readback within ±1 LSB (the tolerance
/// covers unorm store rounding at exact half-values).
/// </summary>
internal sealed class PixelateStage : IPostStage {
    private const uint CellSize = 10;       // chunky blocks (deliberately not dividing the extent: exercises the clamp)
    private const uint Format = GpuPixelFormat.R8G8B8A8Unorm;
    private const uint QuantizeLevels = 6;  // posterized palette
    private const uint RenderSize = 256;
    private const uint WorkgroupEdge = 8;
    private const int ChildPushByteLength = 16;    // ChildParams    { uint2 extent; float time; uint pad; }
    private const int PixelatePushByteLength = 16; // PixelateParams { uint2 extent; uint cellSize; uint quantizeLevels; }

    /// <inheritdoc/>
    public string Name => "pixelate";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.B;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var device = context.RequireGpuDevice();
        var gpu = context.Resolve<IGpuComputeServices>();
        var deviceHandle = device.DeviceHandle;

        var childBytecode = PostShaders.Read(folder: "Sdf", file: "sdf-child.comp.spv");
        var pixelateBytecode = PostShaders.Read(folder: "Viewport", file: "pixelate.comp.spv");

        var childPush = new byte[ChildPushByteLength];
        var childWords = MemoryMarshal.Cast<byte, uint>(span: childPush.AsSpan());

        childWords[0] = RenderSize;
        childWords[1] = RenderSize;

        var pixelatePush = new byte[PixelatePushByteLength];
        var pixelateWords = MemoryMarshal.Cast<byte, uint>(span: pixelatePush.AsSpan());

        pixelateWords[0] = RenderSize;
        pixelateWords[1] = RenderSize; // extent
        pixelateWords[2] = CellSize;
        pixelateWords[3] = QuantizeLevels;

        GpuComputeBinding[] childBindings = [new GpuComputeBinding(Binding: 0, Kind: GpuComputeBindingKind.StorageImage)];
        GpuComputeBinding[] pixelateBindings = [
            new GpuComputeBinding(Binding: 0, Kind: GpuComputeBindingKind.StorageImage),
            new GpuComputeBinding(Binding: 1, Kind: GpuComputeBindingKind.StorageImage),
        ];

        using var childModule = gpu.ShaderModuleFactory.Create(deviceContext: device, stage: GpuShaderStage.Compute, bytecode: childBytecode);
        using var pixelateModule = gpu.ShaderModuleFactory.Create(deviceContext: device, stage: GpuShaderStage.Compute, bytecode: pixelateBytecode);
        using var childPipeline = gpu.ComputePipelineFactory.Create(bindings: childBindings, computeShaderModule: childModule, deviceContext: device, pushConstantBinding: new GpuPushConstantBinding(data: childPush, offset: 0, stageFlags: GpuShaderStage.Compute));
        using var pixelatePipeline = gpu.ComputePipelineFactory.Create(bindings: pixelateBindings, computeShaderModule: pixelateModule, deviceContext: device, pushConstantBinding: new GpuPushConstantBinding(data: pixelatePush, offset: 0, stageFlags: GpuShaderStage.Compute));
        using var sourceImage = gpu.StorageImageFactory.Create(deviceContext: device, format: Format, height: RenderSize, width: RenderSize);
        using var outputImage = gpu.StorageImageFactory.Create(deviceContext: device, format: Format, height: RenderSize, width: RenderSize);
        using var commandPool = gpu.CommandPoolFactory.Create(deviceContext: device);

        var poolSizes = GpuDescriptorPoolSizes.ForSets(childBindings, pixelateBindings);
        var pool = gpu.DescriptorAllocator.CreatePool(deviceHandle: deviceHandle, sizes: poolSizes);

        try {
            var childSet = gpu.DescriptorAllocator.AllocateSet(descriptorSetLayoutHandle: childPipeline.DescriptorSetLayoutHandle, deviceHandle: deviceHandle, poolHandle: pool);
            var pixelateSet = gpu.DescriptorAllocator.AllocateSet(descriptorSetLayoutHandle: pixelatePipeline.DescriptorSetLayoutHandle, deviceHandle: deviceHandle, poolHandle: pool);

            gpu.DescriptorAllocator.WriteStorageImage(arrayElement: 0, binding: 0, descriptorSetHandle: childSet, deviceHandle: deviceHandle, imageViewHandle: sourceImage.ImageViewHandle);
            gpu.DescriptorAllocator.WriteStorageImage(arrayElement: 0, binding: 0, descriptorSetHandle: pixelateSet, deviceHandle: deviceHandle, imageViewHandle: outputImage.ImageViewHandle);
            gpu.DescriptorAllocator.WriteStorageImage(arrayElement: 0, binding: 1, descriptorSetHandle: pixelateSet, deviceHandle: deviceHandle, imageViewHandle: sourceImage.ImageViewHandle);

            var recorder = gpu.ComputeRecorder;
            var commandBuffer = commandPool.CommandBufferHandle;
            var groups = ((RenderSize + (WorkgroupEdge - 1)) / WorkgroupEdge);

            recorder.BeginCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle);

            // Fill the source, then pixelate it. Both kernels use storage access (General layout); one barrier orders
            // the fill's writes before the pixelate's reads.
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderWrite, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: deviceHandle, imageHandle: sourceImage.ImageHandle, newLayout: GpuImageLayout.General, oldLayout: GpuImageLayout.Undefined, sourceAccessMask: GpuComputeAccess.None, sourceStageMask: GpuComputeStage.TopOfPipe);
            recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle, pipelineHandle: childPipeline.Handle);
            recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: childSet, deviceHandle: deviceHandle, pipelineLayoutHandle: childPipeline.LayoutHandle);
            recorder.PushConstants(commandBufferHandle: commandBuffer, data: childPush, deviceHandle: deviceHandle, offset: 0, pipelineLayoutHandle: childPipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
            recorder.Dispatch(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle, groupCountX: groups, groupCountY: groups, groupCountZ: 1);

            recorder.MemoryBarrier(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderRead, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: deviceHandle, sourceAccessMask: GpuComputeAccess.ShaderWrite, sourceStageMask: GpuComputeStage.ComputeShader);
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderWrite, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: deviceHandle, imageHandle: outputImage.ImageHandle, newLayout: GpuImageLayout.General, oldLayout: GpuImageLayout.Undefined, sourceAccessMask: GpuComputeAccess.None, sourceStageMask: GpuComputeStage.TopOfPipe);
            recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle, pipelineHandle: pixelatePipeline.Handle);
            recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: pixelateSet, deviceHandle: deviceHandle, pipelineLayoutHandle: pixelatePipeline.LayoutHandle);
            recorder.PushConstants(commandBufferHandle: commandBuffer, data: pixelatePush, deviceHandle: deviceHandle, offset: 0, pipelineLayoutHandle: pixelatePipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
            recorder.Dispatch(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle, groupCountX: groups, groupCountY: groups, groupCountZ: 1);

            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderRead, destinationStageMask: GpuComputeStage.FragmentShader, deviceHandle: deviceHandle, imageHandle: outputImage.ImageHandle, newLayout: GpuImageLayout.ShaderReadOnly, oldLayout: GpuImageLayout.General, sourceAccessMask: GpuComputeAccess.ShaderWrite, sourceStageMask: GpuComputeStage.ComputeShader);
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderRead, destinationStageMask: GpuComputeStage.FragmentShader, deviceHandle: deviceHandle, imageHandle: sourceImage.ImageHandle, newLayout: GpuImageLayout.ShaderReadOnly, oldLayout: GpuImageLayout.General, sourceAccessMask: GpuComputeAccess.ShaderRead, sourceStageMask: GpuComputeStage.ComputeShader);
            recorder.EndCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle);

            gpu.QueueSubmitter.SubmitAndWait(commandBufferHandles: [commandBuffer], deviceContext: device);

            using var readback = gpu.SurfaceTransferFactory.CreateReadback(deviceContext: device);

            var source = readback.Read(bytesPerPixel: 4, deviceContext: device, format: Format, height: RenderSize, sourceImageHandle: sourceImage.ImageHandle, width: RenderSize).ToArray();
            var actual = readback.Read(bytesPerPixel: 4, deviceContext: device, format: Format, height: RenderSize, sourceImageHandle: outputImage.ImageHandle, width: RenderSize).ToArray();

            var artifactPath = Path.Combine(context.ArtifactsDirectory, "pixelate.png");

            _ = Directory.CreateDirectory(path: context.ArtifactsDirectory);
            PngImage.Write(height: (int)RenderSize, path: artifactPath, rgba: actual, width: (int)RenderSize);

            // The CPU oracle: replicate the kernel exactly from the same run's source readback.
            var maxDelta = 0;

            for (var y = 0u; (y < RenderSize); y++) {
                var cellY = Math.Min(((y / CellSize) * CellSize) + (CellSize / 2), (RenderSize - 1));

                for (var x = 0u; (x < RenderSize); x++) {
                    var cellX = Math.Min(((x / CellSize) * CellSize) + (CellSize / 2), (RenderSize - 1));
                    var sourceOffset = (int)(((cellY * RenderSize) + cellX) * 4);
                    var actualOffset = (int)(((y * RenderSize) + x) * 4);

                    for (var channel = 0; (channel < 3); channel++) {
                        var normalized = (source[sourceOffset + channel] / 255f);
                        var quantized = (MathF.Round(x: (normalized * (QuantizeLevels - 1f)), mode: MidpointRounding.AwayFromZero) / (QuantizeLevels - 1f));
                        var expected = (int)MathF.Round(x: (quantized * 255f), mode: MidpointRounding.AwayFromZero);

                        maxDelta = Math.Max(maxDelta, Math.Abs(value: (actual[actualOffset + channel] - expected)));
                    }

                    if (actual[actualOffset + 3] != 255) {
                        return PostStageOutcome.Fail(artifactPath: artifactPath, detail: $"pixelate({x},{y}) alpha {actual[actualOffset + 3]} != 255");
                    }
                }
            }

            if (maxDelta > 1) {
                return PostStageOutcome.Fail(artifactPath: artifactPath, detail: $"the pixelated output diverged from the CPU oracle by {maxDelta} LSB (> 1) — the cell snap or the posterize math broke");
            }

            return PostStageOutcome.Pass(artifactPath: artifactPath, detail: $"{RenderSize}x{RenderSize} child pixelated ({CellSize}px cells, {QuantizeLevels}-level palette) matches the CPU oracle within {maxDelta} LSB");
        } finally {
            gpu.DescriptorAllocator.DestroyPool(deviceHandle: deviceHandle, poolHandle: pool);
        }
    }
}
