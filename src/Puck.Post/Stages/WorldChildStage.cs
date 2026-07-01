using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions;

namespace Puck.Post;

/// <summary>
/// Tier-C stage C6. The per-viewport CHILD-PANE seam, cross-backend — the demo's <c>--validate-world --with-child</c>
/// flavour expressed through the POST's harness. <see cref="PostWorldRenderer"/> deliberately has no hosted-child
/// slot (its composite always runs childMask 0), so the stage builds the hosted-pane layout the way the viewport
/// compositor hosts ANY foreign source: on EACH backend it renders the shared hero world into the left pane (the
/// world renderer's own output image, read in place — zero-copy), dispatches <c>sdf-child.comp</c> (the neutral
/// child-surface kernel) into the right pane, and composites both with <c>viewport-composite.comp</c>. Every pixel of
/// the compared image therefore flows through the same three kernels (world chain, child fill, composite) on both
/// backends — SPIR-V on the Vulkan host, DXIL on the shared Tier-C Direct3D 12 device — and the two composites must
/// agree within the calibrated <c>WorldComposite</c> thresholds. Artifacts: both backend composites and the diff
/// heatmap.
/// </summary>
internal sealed class WorldChildStage : IPostStage {
    private const int ChildPushByteLength = 16; // ChildParams { uint2 extent; float time; uint pad; }
    private const int CompositePushByteLength = (16 + ((sizeof(float) * 4) * MaxViewports));
    private const uint Format = GpuPixelFormat.R8G8B8A8Unorm;
    private const int MaxViewports = 4; // viewport-composite.comp: sources[4]
    private const uint OutputHeight = 256;
    private const uint OutputWidth = 512;
    private const uint PaneSize = 256; // each half pane is 256x256 (region extent == source extent, the 1:1 copy)
    private const uint WorkgroupEdge = 8;

    /// <inheritdoc/>
    public string Name => "world-child";

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
        var program = WorldStage.BuildHeroScene();

        var vulkanPixels = RenderComposite(
            bytecodeExtension: ".spv",
            device: context.RequireGpuDevice(),
            gpu: context.Resolve<IGpuComputeServices>(),
            program: program
        );

        var directX = context.RequireDirectXDevice();
        var directXPixels = WorldStage.RenderDirectXDiagnosed(directX: directX, render: () => RenderComposite(
            bytecodeExtension: ".dxil",
            device: directX.DeviceContext,
            gpu: directX.Services.GetRequiredService<IGpuComputeServices>(),
            program: program
        ));

        _ = Directory.CreateDirectory(path: context.ArtifactsDirectory);

        var diffPath = Path.Combine(context.ArtifactsDirectory, "world-child-diff.png");

        PngImage.Write(height: (int)OutputHeight, path: Path.Combine(context.ArtifactsDirectory, "world-child-vulkan.png"), rgba: vulkanPixels, width: (int)OutputWidth);
        PngImage.Write(height: (int)OutputHeight, path: Path.Combine(context.ArtifactsDirectory, "world-child-directx.png"), rgba: directXPixels, width: (int)OutputWidth);
        ParityCheck.WriteDiffImage(comparand: directXPixels, height: (int)OutputHeight, path: diffPath, reference: vulkanPixels, width: (int)OutputWidth);

        var metrics = ParityMetrics.Compute(reference: vulkanPixels, comparand: directXPixels, width: (int)OutputWidth, height: (int)OutputHeight);
        var failures = ParityThresholds.WorldComposite.Evaluate(metrics: metrics);

        if (failures.Count != 0) {
            return PostStageOutcome.Fail(artifactPath: diffPath, detail: $"{ParityCheck.Describe(metrics: metrics)} — {string.Join(separator: "; ", values: failures)}");
        }

        return PostStageOutcome.Pass(artifactPath: diffPath, detail: $"{OutputWidth}x{OutputHeight} world|child composite | Vulkan vs Direct3D 12 within WorldComposite thresholds | {ParityCheck.Describe(metrics: metrics)}");
    }

    // The full hosted-pane chain on ONE backend: world → left pane (the world renderer's own output image, read in
    // place), sdf-child → right pane, viewport-composite → the compared output. All three passes ride the SAME
    // neutral services (and thus the same layout-tracking recorder) the world renderer used.
    private static byte[] RenderComposite(IGpuComputeServices gpu, IGpuDeviceContext device, string bytecodeExtension, Puck.SdfVm.SdfProgram program) {
        var deviceHandle = device.DeviceHandle;

        // The world half: render the shared hero scene at pane size; the output image rests ShaderReadOnly after.
        using var worldRenderer = new PostWorldRenderer(
            bytecodeExtension: bytecodeExtension,
            device: device,
            gpu: gpu,
            height: PaneSize,
            program: program,
            width: PaneSize
        );

        _ = worldRenderer.RenderFrame(frame: WorldStage.BuildHeroFrame(program: program, width: PaneSize, height: PaneSize));

        // The child half + the composite.
        var childPush = new byte[ChildPushByteLength];
        var childWords = MemoryMarshal.Cast<byte, uint>(span: childPush.AsSpan());

        childWords[0] = PaneSize;
        childWords[1] = PaneSize; // ChildParams.extent; time (word 2) and pad (word 3) stay 0.

        var compositePush = new byte[CompositePushByteLength];
        var compositeWords = MemoryMarshal.Cast<byte, uint>(span: compositePush.AsSpan());
        var compositeFloats = MemoryMarshal.Cast<byte, float>(span: compositePush.AsSpan());

        compositeWords[0] = OutputWidth;
        compositeWords[1] = OutputHeight; // imageExtent
        compositeWords[2] = 2;            // viewportCount
        compositeFloats[4] = 0f; compositeFloats[5] = 0f; compositeFloats[6] = 0.5f; compositeFloats[7] = 1f;    // left half: the world pane
        compositeFloats[8] = 0.5f; compositeFloats[9] = 0f; compositeFloats[10] = 0.5f; compositeFloats[11] = 1f; // right half: the child pane

        GpuComputeBinding[] childBindings = [new GpuComputeBinding(Binding: 0, Kind: GpuComputeBindingKind.StorageImage)];
        GpuComputeBinding[] compositeBindings = [
            new GpuComputeBinding(Binding: 0, Kind: GpuComputeBindingKind.StorageImage),
            new GpuComputeBinding(Binding: 1, Kind: GpuComputeBindingKind.StorageImage, Count: MaxViewports),
        ];

        using var childModule = gpu.ShaderModuleFactory.Create(deviceContext: device, stage: GpuShaderStage.Compute, bytecode: PostShaders.Read(folder: "Sdf", file: $"sdf-child.comp{bytecodeExtension}"));
        using var compositeModule = gpu.ShaderModuleFactory.Create(deviceContext: device, stage: GpuShaderStage.Compute, bytecode: PostShaders.Read(folder: "Viewport", file: $"viewport-composite.comp{bytecodeExtension}"));
        using var childPipeline = gpu.ComputePipelineFactory.Create(bindings: childBindings, computeShaderModule: childModule, deviceContext: device, pushConstantBinding: new GpuPushConstantBinding(data: childPush, offset: 0, stageFlags: GpuShaderStage.Compute));
        using var compositePipeline = gpu.ComputePipelineFactory.Create(bindings: compositeBindings, computeShaderModule: compositeModule, deviceContext: device, pushConstantBinding: new GpuPushConstantBinding(data: compositePush, offset: 0, stageFlags: GpuShaderStage.Compute));
        using var childPane = gpu.StorageImageFactory.Create(deviceContext: device, format: Format, height: PaneSize, width: PaneSize);
        using var output = gpu.StorageImageFactory.Create(deviceContext: device, format: Format, height: OutputHeight, width: OutputWidth);
        using var commandPool = gpu.CommandPoolFactory.Create(deviceContext: device);

        var poolSizes = GpuDescriptorPoolSizes.ForSets(childBindings, compositeBindings);
        var pool = gpu.DescriptorAllocator.CreatePool(deviceHandle: deviceHandle, sizes: poolSizes);

        try {
            var childSet = gpu.DescriptorAllocator.AllocateSet(descriptorSetLayoutHandle: childPipeline.DescriptorSetLayoutHandle, deviceHandle: deviceHandle, poolHandle: pool);
            var compositeSet = gpu.DescriptorAllocator.AllocateSet(descriptorSetLayoutHandle: compositePipeline.DescriptorSetLayoutHandle, deviceHandle: deviceHandle, poolHandle: pool);

            gpu.DescriptorAllocator.WriteStorageImage(arrayElement: 0, binding: 0, descriptorSetHandle: childSet, deviceHandle: deviceHandle, imageViewHandle: childPane.ImageViewHandle);
            gpu.DescriptorAllocator.WriteStorageImage(arrayElement: 0, binding: 0, descriptorSetHandle: compositeSet, deviceHandle: deviceHandle, imageViewHandle: output.ImageViewHandle);
            // Source array: slot 0 = the world renderer's OWN output image (the zero-copy hosted-pane seam), slot 1 =
            // the child pane; the unused elements duplicate slot 1 (every bound array element must be a valid
            // descriptor; the kernel never reads past viewportCount).
            gpu.DescriptorAllocator.WriteStorageImage(arrayElement: 0, binding: 1, descriptorSetHandle: compositeSet, deviceHandle: deviceHandle, imageViewHandle: worldRenderer.OutputImageViewHandle);
            gpu.DescriptorAllocator.WriteStorageImage(arrayElement: 1, binding: 1, descriptorSetHandle: compositeSet, deviceHandle: deviceHandle, imageViewHandle: childPane.ImageViewHandle);
            gpu.DescriptorAllocator.WriteStorageImage(arrayElement: 2, binding: 1, descriptorSetHandle: compositeSet, deviceHandle: deviceHandle, imageViewHandle: childPane.ImageViewHandle);
            gpu.DescriptorAllocator.WriteStorageImage(arrayElement: 3, binding: 1, descriptorSetHandle: compositeSet, deviceHandle: deviceHandle, imageViewHandle: childPane.ImageViewHandle);

            var recorder = gpu.ComputeRecorder;
            var commandBuffer = commandPool.CommandBufferHandle;

            recorder.BeginCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle);

            // Fill the child pane with the deterministic child pattern.
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderWrite, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: deviceHandle, imageHandle: childPane.ImageHandle, newLayout: GpuImageLayout.General, oldLayout: GpuImageLayout.Undefined, sourceAccessMask: GpuComputeAccess.None, sourceStageMask: GpuComputeStage.TopOfPipe);
            recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle, pipelineHandle: childPipeline.Handle);
            recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: childSet, deviceHandle: deviceHandle, pipelineLayoutHandle: childPipeline.LayoutHandle);
            recorder.PushConstants(commandBufferHandle: commandBuffer, data: childPush, deviceHandle: deviceHandle, offset: 0, pipelineLayoutHandle: childPipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
            recorder.Dispatch(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle, groupCountX: Groups(size: PaneSize), groupCountY: Groups(size: PaneSize), groupCountZ: 1);

            // Bring the world output back from its ShaderReadOnly resting layout into the General (UAV) layout the
            // compositor's storage reads want, order the child writes before the composite's reads, and open the output.
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderRead, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: deviceHandle, imageHandle: worldRenderer.OutputImageHandle, newLayout: GpuImageLayout.General, oldLayout: GpuImageLayout.ShaderReadOnly, sourceAccessMask: GpuComputeAccess.ShaderRead, sourceStageMask: GpuComputeStage.FragmentShader);
            recorder.MemoryBarrier(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderRead, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: deviceHandle, sourceAccessMask: GpuComputeAccess.ShaderWrite, sourceStageMask: GpuComputeStage.ComputeShader);
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderWrite, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: deviceHandle, imageHandle: output.ImageHandle, newLayout: GpuImageLayout.General, oldLayout: GpuImageLayout.Undefined, sourceAccessMask: GpuComputeAccess.None, sourceStageMask: GpuComputeStage.TopOfPipe);

            // Composite the two panes into the compared output.
            recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle, pipelineHandle: compositePipeline.Handle);
            recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: compositeSet, deviceHandle: deviceHandle, pipelineLayoutHandle: compositePipeline.LayoutHandle);
            recorder.PushConstants(commandBufferHandle: commandBuffer, data: compositePush, deviceHandle: deviceHandle, offset: 0, pipelineLayoutHandle: compositePipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
            recorder.Dispatch(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle, groupCountX: Groups(size: OutputWidth), groupCountY: Groups(size: OutputHeight), groupCountZ: 1);

            // Hand the composite off readback-readable.
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderRead, destinationStageMask: GpuComputeStage.FragmentShader, deviceHandle: deviceHandle, imageHandle: output.ImageHandle, newLayout: GpuImageLayout.ShaderReadOnly, oldLayout: GpuImageLayout.General, sourceAccessMask: GpuComputeAccess.ShaderWrite, sourceStageMask: GpuComputeStage.ComputeShader);
            recorder.EndCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle);

            gpu.QueueSubmitter.SubmitAndWait(commandBufferHandles: [commandBuffer], deviceContext: device);

            using var readback = gpu.SurfaceTransferFactory.CreateReadback(deviceContext: device);

            return readback.Read(
                bytesPerPixel: 4,
                deviceContext: device,
                format: Format,
                height: OutputHeight,
                sourceImageHandle: output.ImageHandle,
                width: OutputWidth
            ).ToArray();
        } finally {
            gpu.DescriptorAllocator.DestroyPool(deviceHandle: deviceHandle, poolHandle: pool);
        }
    }

    private static uint Groups(uint size) => ((size + (WorkgroupEdge - 1)) / WorkgroupEdge);
}
