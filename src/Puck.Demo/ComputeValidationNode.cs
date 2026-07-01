using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Vulkan;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Messages;

namespace Puck.Demo;

/// <summary>
/// A one-shot root render node installed only under <c>--validate-compute</c>. On its first frame it exercises the
/// Phase-1 compute primitives on the live Vulkan device — create a compute pipeline from <c>gradient.comp</c>, bind
/// a STORAGE-image output, dispatch over a 64x64 extent, read the image back, and assert the kernel wrote the
/// expected UV gradient — then asks the terminal to exit (0 = pass, 2 = infra-fail). It never presents.
/// <para>
/// This proves the compute-pipeline + descriptor + dispatch + storage-image-write + readback plumbing end to end
/// against the real driver, the foundation the ported world renderer builds on.
/// </para>
/// </summary>
internal sealed class ComputeValidationNode : IRenderNode {
    private const uint DescriptorTypeStorageImage = 3; // VK_DESCRIPTOR_TYPE_STORAGE_IMAGE
    private const uint Format = 37; // VK_FORMAT_R8G8B8A8_UNORM
    private const uint RenderSize = 64;
    private const uint WorkgroupEdge = 8;

    private readonly NodeDescriptor m_descriptor = new(
        Name: "compute-validation",
        SurfaceId: SurfaceId.New()
    );
    private readonly ParityResult m_result;
    private readonly IServiceProvider m_serviceProvider;
    private bool m_done;

    /// <summary>Initializes a new instance of the <see cref="ComputeValidationNode"/> class.</summary>
    /// <param name="serviceProvider">The application service provider (resolves the live Vulkan device and compute APIs).</param>
    /// <param name="result">The shared result the exit code is written to.</param>
    public ComputeValidationNode(IServiceProvider serviceProvider, ParityResult result) {
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
            Validate();
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"COMPUTE infra-fail | {exception.Message}");
            m_result.ExitCode = 2;
        }

        if (context.Host.HoldsCapability<ITerminalControl>(capability: out var terminal)) {
            terminal.RequestExit();
        }

        return default;
    }

    private void Validate() {
        T Resolve<T>() => (T)m_serviceProvider.GetService(serviceType: typeof(T))!;

        var deviceContext = Resolve<IVulkanDeviceContext>();
        var gpuDeviceContext = (IGpuDeviceContext)deviceContext;
        var computePipelineApi = Resolve<IVulkanComputePipelineApi>();
        var offscreenImageApi = Resolve<IVulkanOffscreenImageApi>();
        var framebufferSetApi = Resolve<IVulkanFramebufferSetApi>();
        var descriptorApi = Resolve<IVulkanDescriptorApi>();
        var shaderModuleFactory = Resolve<IGpuShaderModuleFactory>();
        var recordingApi = Resolve<IVulkanCommandBufferRecordingApi>();
        var commandResourcesFactory = Resolve<IVulkanCommandResourcesFactory>();
        var queueSubmitter = Resolve<VulkanQueueSubmitter>();
        var transferFactory = Resolve<IGpuSurfaceTransferFactory>();

        var logicalDevice = deviceContext.LogicalDevice;
        var deviceHandle = logicalDevice.Handle;

        var spirvPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders", "Compute", "gradient.comp.spv");
        using var shaderModule = shaderModuleFactory.Create(
            deviceContext: gpuDeviceContext,
            stage: GpuShaderStage.Compute,
            bytecode: File.ReadAllBytes(path: spirvPath)
        );

        // STORAGE-usage offscreen image + a view to bind it as the kernel's output.
        var image = offscreenImageApi.CreateColorImage(request: new VulkanOffscreenImageCreateRequest(
            DeviceHandle: deviceHandle,
            Format: Format,
            Height: RenderSize,
            InstanceHandle: deviceContext.Instance.Handle,
            PhysicalDeviceHandle: logicalDevice.PhysicalDevice.Handle,
            UsageFlags: VulkanImageUsageFlags.Storage | VulkanImageUsageFlags.Sampled | VulkanImageUsageFlags.TransferSource,
            Width: RenderSize
        ));

        framebufferSetApi.CreateImageView(
            imageViewHandle: out var viewHandle,
            request: new VulkanImageViewCreateRequest(
                DeviceHandle: deviceHandle,
                Format: Format,
                ImageHandle: image.ImageHandle
            )
        ).ThrowIfFailed(operation: "vkCreateImageView");

        computePipelineApi.CreateComputePipeline(
            request: new VulkanComputePipelineCreateRequest(
                DeviceHandle: deviceHandle,
                ShaderModuleHandle: shaderModule.Handle,
                DescriptorBindings: [
                    new VkDescriptorSetLayoutBinding {
                        Binding = 0,
                        DescriptorCount = 1,
                        DescriptorType = DescriptorTypeStorageImage,
                        StageFlags = GpuShaderStage.Compute,
                    },
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
        var set = descriptorApi.AllocateSet(request: new VulkanDescriptorSetAllocateRequest(
            DescriptorSetLayoutHandle: setLayout,
            DeviceHandle: deviceHandle,
            PoolHandle: pool
        ));

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
                imageHandle: image.ImageHandle,
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
            // Leave the image in SHADER_READ_ONLY_OPTIMAL so the readback (which transitions from it) reads the result.
            recordingApi.TransitionImageLayout(
                baseMipLevel: 0,
                commandBufferHandle: commandBuffer,
                destinationAccessMask: VulkanAccessFlags.ShaderRead,
                destinationStageMask: VulkanPipelineStageFlags.FragmentShader,
                deviceHandle: deviceHandle,
                imageHandle: image.ImageHandle,
                mipLevelCount: 1,
                newLayout: VulkanImageLayout.ShaderReadOnlyOptimal,
                oldLayout: VulkanImageLayout.General,
                sourceAccessMask: VulkanAccessFlags.ShaderWrite,
                sourceStageMask: VulkanPipelineStageFlags.ComputeShader
            );
            recordingApi.EndCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle).ThrowIfFailed(operation: "vkEndCommandBuffer");

            queueSubmitter.SubmitAndWait(
                commandBufferHandles: [commandBuffer],
                deviceHandle: deviceHandle,
                graphicsQueue: logicalDevice.GraphicsQueue
            );

            using var readback = transferFactory.CreateReadback(deviceContext: gpuDeviceContext);
            var pixels = readback.Read(
                deviceContext: gpuDeviceContext,
                sourceImageHandle: image.ImageHandle,
                width: RenderSize,
                height: RenderSize,
                format: GpuPixelFormat.R8G8B8A8Unorm,
                bytesPerPixel: 4
            ).Span;

            var firstRed = pixels[0];
            var lastRed = pixels[(int)(((RenderSize * RenderSize) - 1) * 4)];
            var centerBlue = pixels[(int)((((RenderSize / 2) * RenderSize) + (RenderSize / 2)) * 4) + 2];

            if (lastRed <= (firstRed + 64)) {
                throw new InvalidOperationException(message: $"The compute dispatch did not produce the expected horizontal gradient (firstRed={firstRed}, lastRed={lastRed}).");
            }

            m_result.ExitCode = 0;
            Console.Out.WriteLine(value: $"COMPUTE pass | {RenderSize}x{RenderSize} | dispatched gradient.comp into a storage image, read back the R gradient {firstRed}->{lastRed} (blue {centerBlue})");
        } finally {
            commandResources.Dispose();
            computePipelineApi.DestroyPipeline(deviceHandle: deviceHandle, pipelineHandle: pipeline);
            computePipelineApi.DestroyPipelineLayout(deviceHandle: deviceHandle, pipelineLayoutHandle: pipelineLayout);
            computePipelineApi.DestroyDescriptorSetLayout(deviceHandle: deviceHandle, descriptorSetLayoutHandle: setLayout);
            descriptorApi.DestroyPool(deviceHandle: deviceHandle, poolHandle: pool);
            framebufferSetApi.DestroyImageView(deviceHandle: deviceHandle, imageViewHandle: viewHandle);
            offscreenImageApi.DestroyColorImage(deviceHandle: deviceHandle, imageHandle: image.ImageHandle, memoryHandle: image.MemoryHandle);
        }
    }
}
