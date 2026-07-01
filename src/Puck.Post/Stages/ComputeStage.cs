using Puck.Abstractions.Gpu;
namespace Puck.Post;

/// <summary>
/// Tier-B stage B1. The GPU smoke test: on the offscreen Vulkan host it creates a compute pipeline from the local
/// <c>gradient.comp</c> kernel, binds a STORAGE-image output, dispatches over a 64×64 extent, reads the image back, and
/// asserts the kernel wrote the expected horizontal UV gradient. It proves the neutral compute-pipeline + descriptor +
/// dispatch + storage-image-write + readback plumbing end to end — the foundation the world renderer builds on.
/// </summary>
internal sealed class ComputeStage : IPostStage {
    private const uint RenderSize = 64;
    private const uint WorkgroupEdge = 8;

    /// <inheritdoc/>
    public string Name => "compute";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.B;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var device = context.RequireGpuDevice();
        var services = context.Resolve<IGpuComputeServices>();
        var allocator = services.DescriptorAllocator;
        var recorder = services.ComputeRecorder;
        var deviceHandle = device.DeviceHandle;
        var spirvPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders", "Compute", "gradient.comp.spv");

        using var shaderModule = services.ShaderModuleFactory.Create(deviceContext: device, stage: GpuShaderStage.Compute, bytecode: File.ReadAllBytes(path: spirvPath));
        using var image = services.StorageImageFactory.Create(deviceContext: device, format: GpuPixelFormat.R8G8B8A8Unorm, height: RenderSize, width: RenderSize);

        GpuComputeBinding[] bindings = [new GpuComputeBinding(Binding: 0, Kind: GpuComputeBindingKind.StorageImage)];

        using var pipeline = services.ComputePipelineFactory.Create(bindings: bindings, computeShaderModule: shaderModule, deviceContext: device, pushConstantBinding: null);

        var poolSizes = GpuDescriptorPoolSizes.ForSets(bindings);
        var pool = allocator.CreatePool(deviceHandle: deviceHandle, sizes: poolSizes);

        try {
            using var commandPool = services.CommandPoolFactory.Create(deviceContext: device);

            var commandBuffer = commandPool.CommandBufferHandle;
            var set = allocator.AllocateSet(descriptorSetLayoutHandle: pipeline.DescriptorSetLayoutHandle, deviceHandle: deviceHandle, poolHandle: pool);

            allocator.WriteStorageImage(arrayElement: 0, binding: 0, descriptorSetHandle: set, deviceHandle: deviceHandle, imageViewHandle: image.ImageViewHandle);

            recorder.BeginCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle);
            recorder.TransitionImageLayout(
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuComputeAccess.ShaderWrite,
                destinationStageMask: GpuComputeStage.ComputeShader,
                deviceHandle: deviceHandle,
                imageHandle: image.ImageHandle,
                newLayout: GpuImageLayout.General,
                oldLayout: GpuImageLayout.Undefined,
                sourceAccessMask: GpuComputeAccess.None,
                sourceStageMask: GpuComputeStage.TopOfPipe
            );
            recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle, pipelineHandle: pipeline.Handle);
            recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: set, deviceHandle: deviceHandle, pipelineLayoutHandle: pipeline.LayoutHandle);
            recorder.Dispatch(
                commandBufferHandle: commandBuffer,
                deviceHandle: deviceHandle,
                groupCountX: ((RenderSize + (WorkgroupEdge - 1)) / WorkgroupEdge),
                groupCountY: ((RenderSize + (WorkgroupEdge - 1)) / WorkgroupEdge),
                groupCountZ: 1
            );
            // Order the shader writes before the readback, and hand the image off in the shader-readable layout the
            // readback copies from.
            recorder.MemoryBarrier(
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuComputeAccess.ShaderRead,
                destinationStageMask: GpuComputeStage.ComputeShader,
                deviceHandle: deviceHandle,
                sourceAccessMask: GpuComputeAccess.ShaderWrite,
                sourceStageMask: GpuComputeStage.ComputeShader
            );
            recorder.TransitionImageLayout(
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuComputeAccess.ShaderRead,
                destinationStageMask: GpuComputeStage.FragmentShader,
                deviceHandle: deviceHandle,
                imageHandle: image.ImageHandle,
                newLayout: GpuImageLayout.ShaderReadOnly,
                oldLayout: GpuImageLayout.General,
                sourceAccessMask: GpuComputeAccess.ShaderWrite,
                sourceStageMask: GpuComputeStage.ComputeShader
            );
            recorder.EndCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle);

            services.QueueSubmitter.SubmitAndWait(commandBufferHandles: [commandBuffer], deviceContext: device);

            using var readback = services.SurfaceTransferFactory.CreateReadback(deviceContext: device);
            var pixels = readback.Read(
                bytesPerPixel: 4,
                deviceContext: device,
                format: GpuPixelFormat.R8G8B8A8Unorm,
                height: RenderSize,
                sourceImageHandle: image.ImageHandle,
                width: RenderSize
            ).Span;

            var firstRed = pixels[0];
            var lastRed = pixels[(int)(((RenderSize * RenderSize) - 1) * 4)];

            if (lastRed <= (firstRed + 64)) {
                return PostStageOutcome.Fail(detail: $"the compute dispatch did not produce the expected horizontal gradient (firstRed={firstRed}, lastRed={lastRed})");
            }

            return PostStageOutcome.Pass(detail: $"{RenderSize}x{RenderSize} | dispatched gradient.comp into a storage image, read back the R gradient {firstRed}->{lastRed}");
        } finally {
            allocator.DestroyPool(deviceHandle: deviceHandle, poolHandle: pool);
        }
    }
}
