using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.DirectX;
using Puck.Hosting;
using Puck.Vulkan;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Messages;

namespace Puck.Demo;

/// <summary>
/// A one-shot root render node (installed under <c>--validate-camera</c>) proving the CAMERA zero-copy path in the
/// direction a live capture source uses it: a FOREIGN device produces a frame into shared GPU memory and the host
/// render device imports it zero-copy. A bespoke Direct3D 12 device stands in for a camera's decode device (e.g. Media
/// Foundation's D3D device): it owns a shared storage image (only a D3D12 shared handle is cross-openable by Vulkan),
/// dispatches <c>sdf-child</c> to produce a recognizable "frame" into it, and hands it off in the cross-API COMMON
/// state. The Vulkan host then imports that shared handle — no host round-trip — and reads it back, asserting the
/// foreign-device content survived. This is <see cref="CrossShareReverseNode"/>'s machinery with the producer/consumer
/// roles flipped to the camera direction (foreign produces, host consumes). 0 = pass/skip, 2 = infra-fail. It never
/// presents.
/// </summary>
internal sealed class CameraValidationNode : IRenderNode {
    private const uint ChildOutputBinding = 0; // sdf-child.comp: Output at binding 0 (register u0)
    private const int ChildPushByteLength = (sizeof(uint) * 4); // ChildParams: uint2 extent + float time + uint pad
    private const uint RenderSize = 64;
    private const uint VulkanFormatR8G8B8A8Unorm = 37; // VK_FORMAT_R8G8B8A8_UNORM
    private const uint WorkgroupEdge = 8;

    private readonly NodeDescriptor m_descriptor = new(
        Name: "camera-validation",
        SurfaceId: SurfaceId.New()
    );
    private readonly ParityResult m_result;
    private readonly IServiceProvider m_serviceProvider;
    private bool m_done;

    /// <summary>Initializes a new instance of the <see cref="CameraValidationNode"/> class.</summary>
    /// <param name="serviceProvider">The application service provider (the live Vulkan host device + APIs, and the LUID source for the bespoke Direct3D 12 "camera" device).</param>
    /// <param name="result">The shared result the exit code is written to.</param>
    public CameraValidationNode(IServiceProvider serviceProvider, ParityResult result) {
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
                Console.Out.WriteLine(value: "CAMERA skip | Direct3D 12 requires Windows 10.0.10240+");
                m_result.ExitCode = 0;
            } else {
                Validate();
                m_result.ExitCode = 0;
            }
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"CAMERA infra-fail | {exception}");
            m_result.ExitCode = 2;
        }

        if (context.Host.HoldsCapability<ITerminalControl>(capability: out var terminal)) {
            terminal.RequestExit();
        }

        return default;
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private void Validate() {
        // The bespoke Direct3D 12 device stands in for the camera's decode device; it owns the shared texture (its
        // CreateSharedHandle is openable by Vulkan; the reverse is not) and produces the "frame" into it.
        using var directX = new DirectXComputeWorldDevice(hostProvider: m_serviceProvider);
        using var exportable = new DirectXGpuSurfaceExportFactory().CreateExportableStorageImage(
            deviceContext: directX.DeviceContext,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: RenderSize,
            width: RenderSize
        );

        DirectXProduceFrame(directX: directX, exportable: exportable);
        exportable.FinalizeForExport();

        var (varied, firstPixel, sampledPixel) = VulkanImportAndReadback(sharedHandle: exportable.SharedHandle);

        if (!varied) {
            throw new InvalidOperationException(message: $"the Vulkan host imported the Direct3D 12-produced frame but it was spatially flat (every pixel 0x{firstPixel:X8}) — no content crossed the shared handle.");
        }

        Console.Out.WriteLine(value: $"CAMERA pass | {RenderSize}x{RenderSize} R8G8B8A8 | Direct3D 12 produced a frame (sdf-child) into a shared image (handle 0x{exportable.SharedHandle:X}); the Vulkan host imported it zero-copy and read back varying content (px0=0x{firstPixel:X8}, px_center=0x{sampledPixel:X8})");
    }

    // The foreign (Direct3D 12) device produces a frame into its own exportable shared image: dispatch sdf-child (the
    // neutral test-pattern kernel) into it, then transition it to the cross-API COMMON (External) state so the Vulkan
    // host may take it over. Mirrors ChildSurfaceNode's dispatch, but targets the exportable image on the D3D12 device.
    [SupportedOSPlatform("windows10.0.10240")]
    private static void DirectXProduceFrame(DirectXComputeWorldDevice directX, IGpuExportableStorageImage exportable) {
        var gpu = (IGpuComputeServices)directX.Services.GetService(serviceType: typeof(IGpuComputeServices))!;
        var device = directX.DeviceContext;
        var deviceHandle = device.DeviceHandle;
        var bytecode = File.ReadAllBytes(path: Path.Combine(
            path1: CrossBackendShowcase.ShaderDirectory,
            path2: "sdf-child.comp.dxil"
        ));

        // ChildParams: extent = the frame size; time/pad = 0 (a fixed, deterministic pattern).
        var push = new byte[ChildPushByteLength];
        var pushWords = MemoryMarshal.Cast<byte, uint>(span: push.AsSpan());

        pushWords[0] = RenderSize;
        pushWords[1] = RenderSize;

        GpuComputeBinding[] bindings = [new GpuComputeBinding(Binding: ChildOutputBinding, Kind: GpuComputeBindingKind.StorageImage)];

        using var shaderModule = gpu.ShaderModuleFactory.Create(deviceContext: device, stage: GpuShaderStage.Compute, bytecode: bytecode);
        using var pipeline = gpu.ComputePipelineFactory.Create(
            bindings: bindings,
            computeShaderModule: shaderModule,
            deviceContext: device,
            pushConstantBinding: new GpuPushConstantBinding(data: push, offset: 0, stageFlags: GpuShaderStage.Compute)
        );

        var poolSizes = GpuDescriptorPoolSizes.ForSets(bindings);
        var pool = gpu.DescriptorAllocator.CreatePool(deviceHandle: deviceHandle, sizes: poolSizes);

        try {
            using var commandPool = gpu.CommandPoolFactory.Create(deviceContext: device);

            var set = gpu.DescriptorAllocator.AllocateSet(descriptorSetLayoutHandle: pipeline.DescriptorSetLayoutHandle, deviceHandle: deviceHandle, poolHandle: pool);

            gpu.DescriptorAllocator.WriteStorageImage(arrayElement: 0, binding: ChildOutputBinding, descriptorSetHandle: set, deviceHandle: deviceHandle, imageViewHandle: exportable.ImageViewHandle);

            var recorder = gpu.ComputeRecorder;
            var commandBuffer = commandPool.CommandBufferHandle;

            recorder.BeginCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle);
            recorder.TransitionImageLayout(
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuComputeAccess.ShaderWrite,
                destinationStageMask: GpuComputeStage.ComputeShader,
                deviceHandle: deviceHandle,
                imageHandle: exportable.ImageHandle,
                newLayout: GpuImageLayout.General,
                oldLayout: GpuImageLayout.Undefined,
                sourceAccessMask: GpuComputeAccess.None,
                sourceStageMask: GpuComputeStage.TopOfPipe
            );
            recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle, pipelineHandle: pipeline.Handle);
            recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: set, deviceHandle: deviceHandle, pipelineLayoutHandle: pipeline.LayoutHandle);
            recorder.PushConstants(commandBufferHandle: commandBuffer, data: push, deviceHandle: deviceHandle, offset: 0, pipelineLayoutHandle: pipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
            recorder.Dispatch(
                commandBufferHandle: commandBuffer,
                deviceHandle: deviceHandle,
                groupCountX: ((RenderSize + (WorkgroupEdge - 1)) / WorkgroupEdge),
                groupCountY: ((RenderSize + (WorkgroupEdge - 1)) / WorkgroupEdge),
                groupCountZ: 1
            );
            // Hand the resource to Vulkan in the cross-API COMMON state (a shared resource must rest in External when
            // another API takes it over).
            recorder.TransitionImageLayout(
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuComputeAccess.ShaderRead,
                destinationStageMask: GpuComputeStage.ComputeShader,
                deviceHandle: deviceHandle,
                imageHandle: exportable.ImageHandle,
                newLayout: GpuImageLayout.External,
                oldLayout: GpuImageLayout.General,
                sourceAccessMask: GpuComputeAccess.ShaderWrite,
                sourceStageMask: GpuComputeStage.ComputeShader
            );
            recorder.EndCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle);
            gpu.QueueSubmitter.SubmitAndWait(commandBufferHandles: [commandBuffer], deviceContext: device);
        } finally {
            gpu.DescriptorAllocator.DestroyPool(deviceHandle: deviceHandle, poolHandle: pool);
        }
    }

    // Import the Direct3D 12-produced shared handle on the Vulkan host (no host round-trip), bring it into a
    // shader-readable layout, and read it back. Returns whether the imported frame carries spatial variation (proving
    // real content crossed the boundary) plus two sample pixels for the diagnostic line.
    private (bool Varied, uint FirstPixel, uint SampledPixel) VulkanImportAndReadback(nint sharedHandle) {
        T Resolve<T>() => (T)m_serviceProvider.GetService(serviceType: typeof(T))!;

        var deviceContext = Resolve<IVulkanDeviceContext>();
        var gpuDeviceContext = (IGpuDeviceContext)deviceContext;
        var externalMemoryApi = Resolve<IVulkanExternalMemoryApi>();
        var recordingApi = Resolve<IVulkanCommandBufferRecordingApi>();
        var commandResourcesFactory = Resolve<IVulkanCommandResourcesFactory>();
        var queueSubmitter = Resolve<VulkanQueueSubmitter>();

        var logicalDevice = deviceContext.LogicalDevice;
        var deviceHandle = logicalDevice.Handle;

        var imported = externalMemoryApi.ImportImage(request: new VulkanExternalImageImportRequest(
            DeviceHandle: deviceHandle,
            Format: VulkanFormatR8G8B8A8Unorm,
            Height: RenderSize,
            InstanceHandle: deviceContext.Instance.Handle,
            PhysicalDeviceHandle: logicalDevice.PhysicalDevice.Handle,
            SharedHandle: sharedHandle,
            UsageFlags: VulkanImageUsageFlags.Sampled | VulkanImageUsageFlags.TransferSource,
            Width: RenderSize
        ));

        var commandResources = commandResourcesFactory.Create(commandBufferCount: 1, logicalDevice: logicalDevice);
        var commandBuffer = commandResources.CommandBufferHandles[0];

        try {
            // Vulkan sees the freshly imported image as Undefined (it has no knowledge of Direct3D 12's layout); the
            // content is preserved by the external-memory semantics. Bring it into the shader-read layout the readback
            // copies from.
            recordingApi.BeginCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle).ThrowIfFailed(operation: "vkBeginCommandBuffer");
            recordingApi.TransitionImageLayout(
                baseMipLevel: 0,
                commandBufferHandle: commandBuffer,
                destinationAccessMask: VulkanAccessFlags.ShaderRead,
                destinationStageMask: VulkanPipelineStageFlags.FragmentShader,
                deviceHandle: deviceHandle,
                imageHandle: imported.ImageHandle,
                mipLevelCount: 1,
                newLayout: VulkanImageLayout.ShaderReadOnlyOptimal,
                oldLayout: VulkanImageLayout.Undefined,
                sourceAccessMask: 0,
                sourceStageMask: VulkanPipelineStageFlags.TopOfPipe
            );
            recordingApi.EndCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle).ThrowIfFailed(operation: "vkEndCommandBuffer");

            Span<nint> commandBuffers = [commandBuffer];

            queueSubmitter.SubmitAndWait(commandBufferHandles: commandBuffers, deviceHandle: deviceHandle, graphicsQueue: logicalDevice.GraphicsQueue);

            using var readback = ((IGpuSurfaceTransferFactory)m_serviceProvider.GetService(serviceType: typeof(IGpuSurfaceTransferFactory))!).CreateReadback(deviceContext: gpuDeviceContext);
            var pixels = readback.Read(bytesPerPixel: 4, deviceContext: gpuDeviceContext, format: GpuPixelFormat.R8G8B8A8Unorm, height: RenderSize, sourceImageHandle: imported.ImageHandle, width: RenderSize).Span;
            var pixels32 = MemoryMarshal.Cast<byte, uint>(span: pixels);
            var firstPixel = pixels32[0];
            var sampledPixel = pixels32[(int)(((RenderSize / 2) * RenderSize) + (RenderSize / 2))];
            var varied = false;

            // Any pixel differing from pixel 0 proves the frame is not a flat fill — i.e. real produced content crossed
            // the shared handle (an all-same buffer, e.g. a failed/blank import, would not vary).
            for (var index = 0; (index < pixels32.Length); index++) {
                if (pixels32[index] != firstPixel) {
                    varied = true;

                    break;
                }
            }

            return (varied, firstPixel, sampledPixel);
        } finally {
            commandResources.Dispose();
            externalMemoryApi.DestroyImage(deviceHandle: deviceHandle, imageHandle: imported.ImageHandle, memoryHandle: imported.MemoryHandle);
        }
    }
}
