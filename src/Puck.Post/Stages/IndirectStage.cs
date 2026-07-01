using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Gpu;
namespace Puck.Post;

/// <summary>
/// Tier-C stage C4. INDIRECT compute dispatch — group counts the GPU reads from a buffer (Vulkan
/// <c>vkCmdDispatchIndirect</c> / Direct3D 12 <c>ExecuteIndirect</c> with a DISPATCH command signature) — must behave
/// IDENTICALLY to a direct dispatch on BOTH backends; this is the seam the world renderer's GPU-driven cull rides.
/// Ported from the demo's <c>IndirectDispatchValidationNode</c> (the worked reference): for each backend it renders
/// the neutral <c>sdf-child.comp</c> test pattern into a storage image twice — once via a direct
/// <see cref="IGpuComputeRecorder.Dispatch"/>, once via <see cref="IGpuComputeRecorder.DispatchIndirect"/> reading the
/// same group counts from an args buffer — and asserts the two readbacks are BIT-IDENTICAL (a broken signature,
/// stride, offset, or args buffer would change the launched grid and diverge). Direct dispatch is its own oracle, so
/// no hand-computed expected pixels are needed. The Direct3D 12 half runs on the shared Tier-C device.
/// </summary>
internal sealed class IndirectStage : IPostStage {
    private const uint Format = GpuPixelFormat.R8G8B8A8Unorm;
    private const uint RenderSize = 64;
    private const uint WorkgroupEdge = 8;
    private const uint Groups = ((RenderSize + (WorkgroupEdge - 1)) / WorkgroupEdge); // 8 — covers the 64x64 image
    private const int PushByteLength = 16; // ChildParams { uint2 extent; float time; uint pad; }

    /// <inheritdoc/>
    public string Name => "indirect";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the cross-backend tier (Direct3D 12) requires Windows 10.0.10240+");
        }

        return RunCore(context: context);
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        // Vulkan: the live host device + its neutral compute services (SPIR-V kernel).
        var vulkanMatch = DirectMatchesIndirect(
            bytecode: PostShaders.Read(folder: "Sdf", file: "sdf-child.comp.spv"),
            deviceContext: context.RequireGpuDevice(),
            gpu: context.Resolve<IGpuComputeServices>()
        );

        // Direct3D 12: the shared LUID-matched Tier-C device (DXIL kernel).
        var directX = context.RequireDirectXDevice();
        var directXMatch = DirectMatchesIndirect(
            bytecode: PostShaders.Read(folder: "Sdf", file: "sdf-child.comp.dxil"),
            deviceContext: directX.DeviceContext,
            gpu: directX.Services.GetRequiredService<IGpuComputeServices>()
        );

        if (
            !vulkanMatch ||
            !directXMatch
        ) {
            return PostStageOutcome.Fail(detail: $"DispatchIndirect did not reproduce the direct dispatch (vulkan match={vulkanMatch}, directX match={directXMatch})");
        }

        return PostStageOutcome.Pass(detail: $"{RenderSize}x{RenderSize} sdf-child | Dispatch == DispatchIndirect ({Groups}x{Groups}x1) bit-for-bit on Vulkan (vkCmdDispatchIndirect) AND Direct3D 12 (ExecuteIndirect)");
    }

    // Renders sdf-child once directly and once indirectly on one backend and reports whether the two readbacks match
    // exactly (same device, same kernel, same grid — so a correct indirect dispatch is bit-identical, no FP tolerance).
    private static bool DirectMatchesIndirect(IGpuComputeServices gpu, IGpuDeviceContext deviceContext, byte[] bytecode) {
        var direct = Render(bytecode: bytecode, deviceContext: deviceContext, gpu: gpu, indirect: false);
        var indirect = Render(bytecode: bytecode, deviceContext: deviceContext, gpu: gpu, indirect: true);

        return direct.AsSpan().SequenceEqual(other: indirect.AsSpan());
    }

    // One render of sdf-child into a fresh 64x64 storage image via either a direct or an indirect dispatch, read back
    // to host pixels. Drives only the neutral compute seam, so the identical code runs on whichever backend gpu wraps.
    private static byte[] Render(IGpuComputeServices gpu, IGpuDeviceContext deviceContext, byte[] bytecode, bool indirect) {
        var deviceHandle = deviceContext.DeviceHandle;
        var push = new byte[PushByteLength];
        var pushWords = MemoryMarshal.Cast<byte, uint>(span: push.AsSpan());

        // ChildParams: extent = (64, 64); time = 0 (bit pattern 0); pad = 0.
        pushWords[0] = RenderSize;
        pushWords[1] = RenderSize;

        GpuComputeBinding[] bindings = [new GpuComputeBinding(Binding: 0, Kind: GpuComputeBindingKind.StorageImage)];

        using var shaderModule = gpu.ShaderModuleFactory.Create(deviceContext: deviceContext, stage: GpuShaderStage.Compute, bytecode: bytecode);
        using var pipeline = gpu.ComputePipelineFactory.Create(
            bindings: bindings,
            computeShaderModule: shaderModule,
            deviceContext: deviceContext,
            pushConstantBinding: new GpuPushConstantBinding(data: push, offset: 0, stageFlags: GpuShaderStage.Compute)
        );
        using var image = gpu.StorageImageFactory.Create(deviceContext: deviceContext, format: Format, height: RenderSize, width: RenderSize);
        using var commandPool = gpu.CommandPoolFactory.Create(deviceContext: deviceContext);
        var argumentBuffer = (indirect ? gpu.StorageBufferFactory.CreateIndirectArgs(deviceContext: deviceContext, sizeBytes: 12) : null);

        var poolSizes = GpuDescriptorPoolSizes.ForSets(bindings);
        var pool = gpu.DescriptorAllocator.CreatePool(deviceHandle: deviceHandle, sizes: poolSizes);

        try {
            argumentBuffer?.Write<uint>(data: [Groups, Groups, 1]);

            var set = gpu.DescriptorAllocator.AllocateSet(descriptorSetLayoutHandle: pipeline.DescriptorSetLayoutHandle, deviceHandle: deviceHandle, poolHandle: pool);

            gpu.DescriptorAllocator.WriteStorageImage(arrayElement: 0, binding: 0, descriptorSetHandle: set, deviceHandle: deviceHandle, imageViewHandle: image.ImageViewHandle);

            var recorder = gpu.ComputeRecorder;
            var commandBuffer = commandPool.CommandBufferHandle;

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
            recorder.PushConstants(commandBufferHandle: commandBuffer, data: push, deviceHandle: deviceHandle, offset: 0, pipelineLayoutHandle: pipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);

            if (indirect) {
                recorder.DispatchIndirect(argumentBufferHandle: argumentBuffer!.BufferHandle, argumentBufferOffset: 0, commandBufferHandle: commandBuffer, deviceHandle: deviceHandle);
            } else {
                recorder.Dispatch(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle, groupCountX: Groups, groupCountY: Groups, groupCountZ: 1);
            }

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

            gpu.QueueSubmitter.SubmitAndWait(commandBufferHandles: [commandBuffer], deviceContext: deviceContext);

            using var readback = gpu.SurfaceTransferFactory.CreateReadback(deviceContext: deviceContext);

            // Retain a copy: the readback buffer is reused across calls (direct vs indirect read the same backend).
            return readback.Read(
                bytesPerPixel: 4,
                deviceContext: deviceContext,
                format: Format,
                height: RenderSize,
                sourceImageHandle: image.ImageHandle,
                width: RenderSize
            ).ToArray();
        } finally {
            gpu.DescriptorAllocator.DestroyPool(deviceHandle: deviceHandle, poolHandle: pool);
            argumentBuffer?.Dispose();
        }
    }
}
