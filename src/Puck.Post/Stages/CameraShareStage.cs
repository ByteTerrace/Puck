using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Gpu;
using Puck.DirectX;
using Puck.Platform;
using Puck.Vulkan;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Messages;

namespace Puck.Post;

/// <summary>
/// Tier-C stage C9. The CAMERA zero-copy import seam with SYNTHETIC frames (no webcam), environment-lenient like the
/// capture stage: it proves the zero-copy path in the direction a live capture source uses it — a FOREIGN device
/// produces a frame into shared GPU memory and the host render device imports it zero-copy. The shared Tier-C
/// Direct3D 12 device stands in for a camera's decode device (e.g. Media Foundation's D3D device): it owns a shared
/// storage image (only a D3D12 shared handle is cross-openable by Vulkan), dispatches <c>sdf-child</c> to produce a
/// recognizable synthetic "frame" into it, and hands it off in the cross-API COMMON state. The Vulkan host imports
/// that shared handle — no host round-trip — and reads it back, asserting the foreign-device content survived
/// (spatial variation = real content crossed the handle). When the platform camera-capture service is the null
/// implementation (or unsupported) the stage returns <see cref="PostVerdict.Skip"/>, never Fail — the live-webcam and
/// DXVA-GPU tiers stay demo-only hardware bring-up gates.
/// </summary>
internal sealed class CameraShareStage : IPostStage {
    private const uint ChildOutputBinding = 0; // sdf-child.comp: Output at binding 0 (register u0)
    private const int ChildPushByteLength = (sizeof(uint) * 4); // ChildParams: uint2 extent + float time + uint pad
    private const int MinDistinctColors = 16; // a shaded sdf-child render has hundreds; a flat/two-tone glitch fails
    private const uint RenderSize = 64;
    private const uint VulkanFormatR8G8B8A8Unorm = 37; // VK_FORMAT_R8G8B8A8_UNORM
    private const uint WorkgroupEdge = 8;

    /// <inheritdoc/>
    public string Name => "camera-share";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            return PostStageOutcome.Skip(detail: "the camera zero-copy seam (Direct3D 12 shared handles) requires Windows 10.0.10240+");
        }

        // Environment-lenient: the seam under test is what a live camera source rides, so a platform whose
        // camera-capture service is the null implementation has nothing to prove here (per the plan's C9 charter).
        var cameraService = context.Resolve<ICameraCaptureService>();

        if (
            (cameraService is NullCameraCaptureService) ||
            !cameraService.IsSupported
        ) {
            return PostStageOutcome.Skip(detail: $"the platform camera-capture service is unavailable ({cameraService.GetType().Name}, IsSupported={cameraService.IsSupported})");
        }

        return RunCore(context: context);
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private static PostStageOutcome RunCore(PostContext context) {
        // The shared Tier-C Direct3D 12 device stands in for the camera's decode device; it owns the shared texture
        // (its CreateSharedHandle is openable by Vulkan; the reverse is not) and produces the synthetic frame into it.
        var directX = context.RequireDirectXDevice();

        using var exportable = new DirectXGpuSurfaceExportFactory().CreateExportableStorageImage(
            deviceContext: directX.DeviceContext,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: RenderSize,
            width: RenderSize
        );

        DirectXProduceFrame(directX: directX, exportable: exportable);
        exportable.FinalizeForExport();

        var (distinctColors, firstPixel, sampledPixel) = VulkanImportAndReadback(context: context, sharedHandle: exportable.SharedHandle);

        // A real sdf-child frame is a rendered, shaded shape: it must carry genuine spatial structure — many distinct
        // colors AND a centre (on the shape) that differs from the corner (background) — not merely "some pixel differs
        // from pixel 0", which a single stuck pixel would satisfy. This distinguishes the produced pattern surviving the
        // zero-copy import from a garbled-but-non-uniform buffer.
        if ((distinctColors < MinDistinctColors) || (sampledPixel == firstPixel)) {
            return PostStageOutcome.Fail(detail: $"the Vulkan host imported the Direct3D 12-produced frame but it lacks the expected sdf-child structure ({distinctColors} distinct colors < {MinDistinctColors}, or centre 0x{sampledPixel:X8} == corner 0x{firstPixel:X8}) — no real content crossed the shared handle");
        }

        return PostStageOutcome.Pass(detail: $"{RenderSize}x{RenderSize} R8G8B8A8 | Direct3D 12 produced a synthetic frame (sdf-child) into a shared image (handle 0x{exportable.SharedHandle:X}); the Vulkan host imported it zero-copy and read back the produced pattern ({distinctColors} distinct colors; px0=0x{firstPixel:X8}, px_center=0x{sampledPixel:X8})");
    }

    // The foreign (Direct3D 12) device produces a frame into its own exportable shared image: dispatch sdf-child (the
    // neutral test-pattern kernel) into it, then transition it to the cross-API COMMON (External) state so the Vulkan
    // host may take it over.
    [SupportedOSPlatform("windows10.0.10240")]
    private static void DirectXProduceFrame(PostDirectXDevice directX, IGpuExportableStorageImage exportable) {
        var gpu = directX.Services.GetRequiredService<IGpuComputeServices>();
        var device = directX.DeviceContext;
        var deviceHandle = device.DeviceHandle;

        // ChildParams: extent = the frame size; time/pad = 0 (a fixed, deterministic pattern).
        var push = new byte[ChildPushByteLength];
        var pushWords = MemoryMarshal.Cast<byte, uint>(span: push.AsSpan());

        pushWords[0] = RenderSize;
        pushWords[1] = RenderSize;

        GpuComputeBinding[] bindings = [new GpuComputeBinding(Binding: ChildOutputBinding, Kind: GpuComputeBindingKind.StorageImage)];

        using var shaderModule = gpu.ShaderModuleFactory.Create(deviceContext: device, stage: GpuShaderStage.Compute, bytecode: PostShaders.Read(folder: "Sdf", file: "sdf-child.comp.dxil"));
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
    // real content crossed the boundary) plus two sample pixels for the diagnostic detail.
    private static (int DistinctColors, uint FirstPixel, uint SampledPixel) VulkanImportAndReadback(PostContext context, nint sharedHandle) {
        var deviceContext = context.Resolve<IVulkanDeviceContext>();
        var gpuDeviceContext = (IGpuDeviceContext)deviceContext;
        var externalMemoryApi = context.Resolve<IVulkanExternalMemoryApi>();
        var recordingApi = context.Resolve<IVulkanCommandBufferRecordingApi>();
        var commandResourcesFactory = context.Resolve<IVulkanCommandResourcesFactory>();
        var queueSubmitter = context.Resolve<VulkanQueueSubmitter>();

        var logicalDevice = deviceContext.LogicalDevice;
        var deviceHandle = logicalDevice.Handle;

        var imported = externalMemoryApi.ImportImage(request: new VulkanExternalImageImportRequest(
            DeviceHandle: deviceHandle,
            Format: VulkanFormatR8G8B8A8Unorm,
            Height: RenderSize,
            InstanceHandle: deviceContext.Instance.Handle,
            PhysicalDeviceHandle: logicalDevice.PhysicalDevice.Handle,
            SharedHandle: sharedHandle,
            UsageFlags: (VulkanImageUsageFlags.Sampled | VulkanImageUsageFlags.TransferSource),
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
            queueSubmitter.SubmitAndWait(commandBufferHandles: [commandBuffer], deviceHandle: deviceHandle, graphicsQueue: logicalDevice.GraphicsQueue);

            using var readback = context.Resolve<IGpuSurfaceTransferFactory>().CreateReadback(deviceContext: gpuDeviceContext);
            var pixels = readback.Read(bytesPerPixel: 4, deviceContext: gpuDeviceContext, format: GpuPixelFormat.R8G8B8A8Unorm, height: RenderSize, sourceImageHandle: imported.ImageHandle, width: RenderSize).Span;
            var pixels32 = MemoryMarshal.Cast<byte, uint>(span: pixels);
            var firstPixel = pixels32[0];
            var sampledPixel = pixels32[(int)(((RenderSize / 2) * RenderSize) + (RenderSize / 2))];

            // Count distinct colors — a rendered, shaded pattern has many; a flat fill or a two-tone glitch one or two.
            // This is the content signature RunCore keys on (an all-same buffer, e.g. a failed/blank import, has 1).
            var distinct = new HashSet<uint>();

            for (var index = 0; (index < pixels32.Length); index++) {
                _ = distinct.Add(item: pixels32[index]);
            }

            return (distinct.Count, firstPixel, sampledPixel);
        } finally {
            commandResources.Dispose();
            externalMemoryApi.DestroyImage(deviceHandle: deviceHandle, imageHandle: imported.ImageHandle, memoryHandle: imported.MemoryHandle);
        }
    }
}
