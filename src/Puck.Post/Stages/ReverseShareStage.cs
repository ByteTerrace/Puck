using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Gpu;
using Puck.DirectX;
using Puck.Vulkan;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Messages;

namespace Puck.Post;

/// <summary>
/// Tier-C stage C3. The REVERSE cross-API direction: <em>Vulkan produces into a Direct3D 12-owned resource</em>.
/// Because a D3D12 shared handle is the only
/// NT handle both backends can open (Vulkan's OPAQUE_WIN32 export is Vulkan-only), the resource is always D3D12-owned
/// and the consumer-vs-producer roles are independent of who owns it. The shared Tier-C Direct3D 12 device creates a
/// shared storage image (LUID-matched to the Vulkan host), hands it off in the cross-API COMMON state, Vulkan imports
/// it as a STORAGE image (on NVIDIA the import of a D3D12 resource rides external-memory handle type 0x40, which the
/// engine's import path encodes) and dispatches <c>gradient.comp</c> INTO it, then Direct3D 12 reads its own resource
/// back and asserts Vulkan's gradient landed — zero-copy, no host round-trip.
/// </summary>
internal sealed class ReverseShareStage : IPostStage {
    private const uint DescriptorTypeStorageImage = 3; // VK_DESCRIPTOR_TYPE_STORAGE_IMAGE
    private const uint Format = 37; // VK_FORMAT_R8G8B8A8_UNORM
    private const uint RenderSize = 64;
    private const uint WorkgroupEdge = 8;

    /// <inheritdoc/>
    public string Name => "reverse-share";

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
        // Direct3D 12 owns the shared resource (its CreateSharedHandle is openable by Vulkan; the reverse is not).
        var directX = context.RequireDirectXDevice();

        using var exportable = new DirectXGpuSurfaceExportFactory().CreateExportableStorageImage(
            deviceContext: directX.DeviceContext,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: RenderSize,
            width: RenderSize
        );

        // Hand the resource to Vulkan in the cross-API COMMON state (D3D12 created it in UNORDERED_ACCESS; a shared
        // resource must rest in COMMON when another API takes it over). Vulkan imports it as a STORAGE image and
        // writes a gradient into it; then Direct3D 12 takes it back into a shader-readable state to copy out.
        RecordDirectXTransition(directX: directX, imageHandle: exportable.ImageHandle, newLayout: GpuImageLayout.External);
        VulkanWriteGradient(context: context, sharedHandle: exportable.SharedHandle);
        RecordDirectXTransition(directX: directX, imageHandle: exportable.ImageHandle, newLayout: GpuImageLayout.ShaderReadOnly);

        // Direct3D 12 reads back ITS OWN resource — the pixels Vulkan wrote — and asserts the gradient survived.
        var pixels = DirectXReadback(directX: directX, imageHandle: exportable.ImageHandle).Span;
        var mismatch = GradientCheck.FirstMismatch(pixels: pixels, height: RenderSize, width: RenderSize);

        if (mismatch is not null) {
            return PostStageOutcome.Fail(detail: $"Direct3D 12 did not read back Vulkan's gradient from the shared resource: {mismatch}");
        }

        return PostStageOutcome.Pass(detail: $"{RenderSize}x{RenderSize} R8G8B8A8 | Vulkan wrote gradient.comp INTO a Direct3D 12-owned shared image (handle 0x{exportable.SharedHandle:X}); Direct3D 12 read back the full gradient matching the CPU oracle within 1 LSB");
    }

    // Import the D3D12 shared handle as a Vulkan STORAGE image and dispatch gradient.comp into it (the compute-stage
    // smoke path, but the output image is the foreign resource).
    private static void VulkanWriteGradient(PostContext context, nint sharedHandle) {
        var deviceContext = context.Resolve<IVulkanDeviceContext>();
        var gpuDeviceContext = (IGpuDeviceContext)deviceContext;
        var externalMemoryApi = context.Resolve<IVulkanExternalMemoryApi>();
        var computePipelineApi = context.Resolve<IVulkanComputePipelineApi>();
        var framebufferSetApi = context.Resolve<IVulkanFramebufferSetApi>();
        var descriptorApi = context.Resolve<IVulkanDescriptorApi>();
        var shaderModuleFactory = context.Resolve<IGpuShaderModuleFactory>();
        var recordingApi = context.Resolve<IVulkanCommandBufferRecordingApi>();
        var commandResourcesFactory = context.Resolve<IVulkanCommandResourcesFactory>();
        var queueSubmitter = context.Resolve<VulkanQueueSubmitter>();

        var logicalDevice = deviceContext.LogicalDevice;
        var deviceHandle = logicalDevice.Handle;

        var imported = externalMemoryApi.ImportImage(request: new VulkanExternalImageImportRequest(
            DeviceHandle: deviceHandle,
            Format: Format,
            Height: RenderSize,
            InstanceHandle: deviceContext.Instance.Handle,
            PhysicalDeviceHandle: logicalDevice.PhysicalDevice.Handle,
            SharedHandle: sharedHandle,
            UsageFlags: VulkanImageUsageFlags.Storage | VulkanImageUsageFlags.Sampled | VulkanImageUsageFlags.TransferSource,
            Width: RenderSize
        ));

        using var shaderModule = shaderModuleFactory.Create(
            bytecode: PostShaders.Read(folder: "Compute", file: "gradient.comp.spv"),
            deviceContext: gpuDeviceContext,
            stage: GpuShaderStage.Compute
        );

        framebufferSetApi.CreateImageView(
            imageViewHandle: out var viewHandle,
            request: new VulkanImageViewCreateRequest(DeviceHandle: deviceHandle, Format: Format, ImageHandle: imported.ImageHandle)
        ).ThrowIfFailed(operation: "vkCreateImageView");

        computePipelineApi.CreateComputePipeline(
            request: new VulkanComputePipelineCreateRequest(
                DeviceHandle: deviceHandle,
                ShaderModuleHandle: shaderModule.Handle,
                DescriptorBindings: [
                    new VkDescriptorSetLayoutBinding { Binding = 0, DescriptorCount = 1, DescriptorType = DescriptorTypeStorageImage, StageFlags = (uint)GpuShaderStage.Compute, },
                ],
                PushConstantSize: 0,
                PushConstantStageFlags: 0
            ),
            descriptorSetLayoutHandle: out var setLayout,
            pipelineLayoutHandle: out var pipelineLayout,
            pipelineHandle: out var pipeline
        ).ThrowIfFailed(operation: "vkCreateComputePipelines");

        var pool = descriptorApi.CreatePool(request: new VulkanDescriptorPoolCreateRequest(
            DeviceHandle: deviceHandle,
            Flags: 0,
            MaxSets: 1,
            PoolSizes: new[] { new VulkanDescriptorPoolSize(DescriptorCount: 1, DescriptorType: DescriptorTypeStorageImage) }
        ));
        var set = descriptorApi.AllocateSet(request: new VulkanDescriptorSetAllocateRequest(DescriptorSetLayoutHandle: setLayout, DeviceHandle: deviceHandle, PoolHandle: pool));

        descriptorApi.WriteImage(request: new VulkanDescriptorImageWriteRequest(
            ArrayElement: 0,
            Binding: 0,
            DescriptorSetHandle: set,
            DescriptorType: DescriptorTypeStorageImage,
            DeviceHandle: deviceHandle,
            ImageLayout: VulkanImageLayout.General,
            ImageViewHandle: viewHandle,
            SamplerHandle: 0
        ));

        var commandResources = commandResourcesFactory.Create(commandBufferCount: 1, logicalDevice: logicalDevice);
        var commandBuffer = commandResources.CommandBufferHandles[0];

        try {
            recordingApi.BeginCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle).ThrowIfFailed(operation: "vkBeginCommandBuffer");
            recordingApi.TransitionImageLayout(
                baseMipLevel: 0,
                commandBufferHandle: commandBuffer,
                destinationAccessMask: VulkanAccessFlags.ShaderWrite,
                destinationStageMask: VulkanPipelineStageFlags.ComputeShader,
                deviceHandle: deviceHandle,
                imageHandle: imported.ImageHandle,
                mipLevelCount: 1,
                newLayout: VulkanImageLayout.General,
                oldLayout: VulkanImageLayout.Undefined,
                sourceAccessMask: 0,
                sourceStageMask: VulkanPipelineStageFlags.TopOfPipe
            );
            recordingApi.BindComputePipeline(deviceHandle: deviceHandle, commandBufferHandle: commandBuffer, pipelineHandle: pipeline);
            recordingApi.BindComputeDescriptorSets(deviceHandle: deviceHandle, commandBufferHandle: commandBuffer, pipelineLayoutHandle: pipelineLayout, descriptorSetHandles: [set]);
            recordingApi.Dispatch(
                commandBufferHandle: commandBuffer,
                deviceHandle: deviceHandle,
                groupCountX: ((RenderSize + (WorkgroupEdge - 1)) / WorkgroupEdge),
                groupCountY: ((RenderSize + (WorkgroupEdge - 1)) / WorkgroupEdge),
                groupCountZ: 1
            );
            // Leave it shader-readable — the resting state Vulkan releases the foreign resource in.
            recordingApi.TransitionImageLayout(
                baseMipLevel: 0,
                commandBufferHandle: commandBuffer,
                destinationAccessMask: VulkanAccessFlags.ShaderRead,
                destinationStageMask: VulkanPipelineStageFlags.FragmentShader,
                deviceHandle: deviceHandle,
                imageHandle: imported.ImageHandle,
                mipLevelCount: 1,
                newLayout: VulkanImageLayout.ShaderReadOnlyOptimal,
                oldLayout: VulkanImageLayout.General,
                sourceAccessMask: VulkanAccessFlags.ShaderWrite,
                sourceStageMask: VulkanPipelineStageFlags.ComputeShader
            );
            recordingApi.EndCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle).ThrowIfFailed(operation: "vkEndCommandBuffer");

            // Drain Vulkan: the two backends share no timeline, so the gradient must be fully written before
            // Direct3D 12 reads the same memory.
            queueSubmitter.SubmitAndWait(commandBufferHandles: [commandBuffer], deviceHandle: deviceHandle, graphicsQueue: logicalDevice.GraphicsQueue);
        } finally {
            commandResources.Dispose();
            computePipelineApi.DestroyPipeline(deviceHandle: deviceHandle, pipelineHandle: pipeline);
            computePipelineApi.DestroyPipelineLayout(deviceHandle: deviceHandle, pipelineLayoutHandle: pipelineLayout);
            computePipelineApi.DestroyDescriptorSetLayout(deviceHandle: deviceHandle, descriptorSetLayoutHandle: setLayout);
            descriptorApi.DestroyPool(deviceHandle: deviceHandle, poolHandle: pool);
            framebufferSetApi.DestroyImageView(deviceHandle: deviceHandle, imageViewHandle: viewHandle);
            externalMemoryApi.DestroyImage(deviceHandle: deviceHandle, imageHandle: imported.ImageHandle, memoryHandle: imported.MemoryHandle);
        }
    }

    // Record a single Direct3D 12 image-layout transition (through the compute recorder, whose tracked state is the
    // single source of truth) and drain — used both to hand the resource to Vulkan in COMMON and to take it back.
    [SupportedOSPlatform("windows10.0.10240")]
    private static void RecordDirectXTransition(PostDirectXDevice directX, nint imageHandle, GpuImageLayout newLayout) {
        var recorder = directX.Services.GetRequiredService<IGpuComputeRecorder>();
        var commandPool = directX.Services.GetRequiredService<IGpuComputeCommandPoolFactory>().Create(deviceContext: directX.DeviceContext);
        var deviceHandle = directX.DeviceContext.DeviceHandle;
        var commandBuffer = commandPool.CommandBufferHandle;

        try {
            recorder.BeginCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle);
            recorder.TransitionImageLayout(
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuComputeAccess.ShaderRead,
                destinationStageMask: GpuComputeStage.ComputeShader,
                deviceHandle: deviceHandle,
                imageHandle: imageHandle,
                newLayout: newLayout,
                oldLayout: GpuImageLayout.General,
                sourceAccessMask: GpuComputeAccess.ShaderWrite,
                sourceStageMask: GpuComputeStage.ComputeShader
            );
            recorder.EndCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle);
            directX.Services.GetRequiredService<IGpuQueueSubmitter>().SubmitAndWait(commandBufferHandles: [commandBuffer], deviceContext: directX.DeviceContext);
        } finally {
            commandPool.Dispose();
        }
    }

    // Read the D3D12-owned resource back on Direct3D 12 (it has already been transitioned to the shader-readable
    // state the readback expects).
    [SupportedOSPlatform("windows10.0.10240")]
    private static ReadOnlyMemory<byte> DirectXReadback(PostDirectXDevice directX, nint imageHandle) {
        using var readback = directX.Services.GetRequiredService<IGpuSurfaceTransferFactory>().CreateReadback(deviceContext: directX.DeviceContext);

        return readback.Read(
            bytesPerPixel: 4,
            deviceContext: directX.DeviceContext,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: RenderSize,
            sourceImageHandle: imageHandle,
            width: RenderSize
        );
    }
}
