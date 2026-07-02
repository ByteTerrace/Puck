using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.DirectX;
using Puck.Hosting;
using Puck.Vulkan;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Messages;

namespace Puck.Demo;

/// <summary>
/// A one-shot root render node (installed under <c>--validate-reverse-share</c>) proving the OTHER cross-API
/// direction: <em>Vulkan produces into a Direct3D 12-owned resource</em>. Because a D3D12 shared handle is the only
/// NT handle both backends can open (Vulkan's OPAQUE_WIN32 export is Vulkan-only), the resource is always D3D12-owned
/// and the consumer-vs-producer roles are independent of who owns it. Here Direct3D 12 creates a shared storage
/// image (LUID-matched to the Vulkan host), Vulkan imports it as a STORAGE image and dispatches <c>gradient.comp</c>
/// INTO it, then Direct3D 12 reads its own resource back and asserts Vulkan's gradient landed — zero-copy, no host
/// round-trip. 0 = pass, 2 = infra-fail. It never presents.
/// </summary>
internal sealed class CrossShareReverseNode : IRenderNode {
    private const uint DescriptorTypeStorageImage = 3; // VK_DESCRIPTOR_TYPE_STORAGE_IMAGE
    private const uint Format = 37; // VK_FORMAT_R8G8B8A8_UNORM
    private const uint RenderSize = 64;
    private const uint WorkgroupEdge = 8;

    private readonly NodeDescriptor m_descriptor = new(
        Name: "cross-share-reverse",
        SurfaceId: SurfaceId.New()
    );
    private readonly ParityResult m_result;
    private readonly IServiceProvider m_serviceProvider;
    private bool m_done;

    /// <summary>Initializes a new instance of the <see cref="CrossShareReverseNode"/> class.</summary>
    /// <param name="serviceProvider">The application service provider (the live Vulkan device + APIs, and the LUID source for the bespoke Direct3D 12 device).</param>
    /// <param name="result">The shared result the exit code is written to.</param>
    public CrossShareReverseNode(IServiceProvider serviceProvider, ParityResult result) {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(result);

        m_result = result;
        m_serviceProvider = serviceProvider;
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;

    /// <inheritdoc/>
    public void Dispose() { }

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (m_done) {
            return default;
        }

        m_done = true;

        try {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
                Console.Out.WriteLine(value: "REVERSE-SHARE skip | Direct3D 12 requires Windows 10.0.10240+");
                m_result.ExitCode = 0;
            } else {
                Validate();
                m_result.ExitCode = 0;
            }
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"REVERSE-SHARE infra-fail | {exception}");
            m_result.ExitCode = 2;
        }

        if (context.Host.HoldsCapability<ITerminalControl>(capability: out var terminal)) {
            terminal.RequestExit();
        }

        return default;
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private void Validate() {
        // Direct3D 12 owns the shared resource (its CreateSharedHandle is openable by Vulkan; the reverse is not).
        using var directX = new DirectXComputeWorldDevice(hostProvider: m_serviceProvider);
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
        VulkanWriteGradient(sharedHandle: exportable.SharedHandle);
        RecordDirectXTransition(directX: directX, imageHandle: exportable.ImageHandle, newLayout: GpuImageLayout.ShaderReadOnly);

        // Direct3D 12 reads back ITS OWN resource — the pixels Vulkan wrote — and asserts the gradient survived.
        var pixels = DirectXReadback(directX: directX, imageHandle: exportable.ImageHandle).Span;
        var firstRed = pixels[0];
        var lastRed = pixels[(int)(((RenderSize * RenderSize) - 1) * 4)];

        if (lastRed <= (firstRed + 64)) {
            throw new InvalidOperationException(message: $"Direct3D 12 did not read back Vulkan's gradient from the shared resource (firstRed={firstRed}, lastRed={lastRed}).");
        }

        Console.Out.WriteLine(value: $"REVERSE-SHARE pass | {RenderSize}x{RenderSize} R8G8B8A8 | Vulkan wrote gradient.comp INTO a Direct3D 12-owned shared image (handle 0x{exportable.SharedHandle:X}); Direct3D 12 read it back {firstRed}->{lastRed}");
    }

    // Import the D3D12 shared handle as a Vulkan STORAGE image and dispatch gradient.comp into it (mirrors
    // ComputeValidationNode's compute path, but the output image is the foreign resource).
    private void VulkanWriteGradient(nint sharedHandle) {
        T Resolve<T>() => (T)m_serviceProvider.GetService(serviceType: typeof(T))!;

        var deviceContext = Resolve<IVulkanDeviceContext>();
        var gpuDeviceContext = (IGpuDeviceContext)deviceContext;
        var externalMemoryApi = Resolve<IVulkanExternalMemoryApi>();
        var computePipelineApi = Resolve<IVulkanComputePipelineApi>();
        var framebufferSetApi = Resolve<IVulkanFramebufferSetApi>();
        var descriptorApi = Resolve<IVulkanDescriptorApi>();
        var shaderModuleFactory = Resolve<IGpuShaderModuleFactory>();
        var recordingApi = Resolve<IVulkanCommandBufferRecordingApi>();
        var commandResourcesFactory = Resolve<IVulkanCommandResourcesFactory>();
        var queueSubmitter = Resolve<VulkanQueueSubmitter>();

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

        var spirvPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders", "Compute", "gradient.comp.spv");
        using var shaderModule = shaderModuleFactory.Create(deviceContext: gpuDeviceContext, stage: GpuShaderStage.Compute, bytecode: File.ReadAllBytes(path: spirvPath));

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
                groupCountX: ((RenderSize + WorkgroupEdge) - 1) / WorkgroupEdge,
                groupCountY: ((RenderSize + WorkgroupEdge) - 1) / WorkgroupEdge,
                groupCountZ: 1
            );
            // Leave it shader-readable so the Vulkan-side diagnostic readback can sample it.
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

            // DIAGNOSTIC: read the imported image back on Vulkan to confirm the write landed in the shared memory.
            using (var vkReadback = ((IGpuSurfaceTransferFactory)m_serviceProvider.GetService(serviceType: typeof(IGpuSurfaceTransferFactory))!).CreateReadback(deviceContext: gpuDeviceContext)) {
                var vkPixels = vkReadback.Read(bytesPerPixel: 4, deviceContext: gpuDeviceContext, format: GpuPixelFormat.R8G8B8A8Unorm, height: RenderSize, sourceImageHandle: imported.ImageHandle, width: RenderSize).Span;

                Console.Out.WriteLine(value: $"REVERSE-SHARE diag | Vulkan read its OWN write of the imported image: first R={vkPixels[0]}, last R={vkPixels[(int)(((RenderSize * RenderSize) - 1) * 4)]}");
            }
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
    private static void RecordDirectXTransition(DirectXComputeWorldDevice directX, nint imageHandle, GpuImageLayout newLayout) {
        var recorder = (IGpuComputeRecorder)directX.Services.GetService(serviceType: typeof(IGpuComputeRecorder))!;
        var commandPool = ((IGpuComputeCommandPoolFactory)directX.Services.GetService(serviceType: typeof(IGpuComputeCommandPoolFactory))!).Create(deviceContext: directX.DeviceContext);
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
            ((IGpuQueueSubmitter)directX.Services.GetService(serviceType: typeof(IGpuQueueSubmitter))!).SubmitAndWait(commandBufferHandles: [commandBuffer], deviceContext: directX.DeviceContext);
        } finally {
            commandPool.Dispose();
        }
    }

    // Read the D3D12-owned resource back on Direct3D 12 (it has already been transitioned to the shader-readable
    // state the readback expects).
    [SupportedOSPlatform("windows10.0.10240")]
    private static ReadOnlyMemory<byte> DirectXReadback(DirectXComputeWorldDevice directX, nint imageHandle) {
        using var readback = ((IGpuSurfaceTransferFactory)directX.Services.GetService(serviceType: typeof(IGpuSurfaceTransferFactory))!).CreateReadback(deviceContext: directX.DeviceContext);

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
