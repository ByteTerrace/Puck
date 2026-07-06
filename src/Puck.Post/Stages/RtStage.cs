using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Gpu;
using Puck.DirectX;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// Tier-C stage C8. Hardware ray-tracing world parity — the POST's cross-backend check of the inline ray-query path,
/// built from the demo's retired <c>RtWorldProducerNode</c> (provenance; the demo has no RT parity gate — its
/// <c>--world-rt</c> is a live producer — so this stage IS the first cross-backend diff of the path). The shared hero
/// scene is decomposed into a TLAS of unit-AABB instances (one per finite primitive, via
/// <see cref="RtWorldInstances"/>), and the one SM 6.5 ray-query kernel (<c>sdf-world-rt-debug.rq.comp</c>) renders it
/// on both backends — Vulkan ray-query on the host device (SPIR-V) and DXR 1.1 on the shared LUID-matched Tier-C
/// Direct3D 12 device (DXIL). The kernel RAY-TRACES the cull (TLAS entry + analytic floor crossing set the march
/// start; misses are sky) then SDF-MARCHES the full <c>map()</c> with RT-accelerated soft shadows, so both backends
/// shade the identical continuous image and must agree within the calibrated <c>WorldComposite</c> thresholds.
/// The plan's explicit skip-with-note path: when <see cref="IGpuAccelerationStructure.IsSupported"/> is false on
/// either backend's device the stage returns <see cref="PostStageOutcome.Skip"/> naming the unsupported side — a
/// missing hardware/driver capability is NEVER a failure. Artifacts: both backend renders and the diff heatmap.
/// </summary>
internal sealed class RtStage : IPostStage {
    private const GpuPixelFormat Format = GpuPixelFormat.R8G8B8A8Unorm;
    private const uint InstanceCapacity = 64; // the instance buffer / TLAS capacity (matches the reference); the scene fills as many as it has
    private const uint OutputBindingIndex = 0; // RWTexture2D at binding 0 / register(u0)
    private const uint ProgramBindingIndex = 1; // the SDF program at binding 1 / register(t0) (matches sdf-vm.hlsli)
    private const int PushConstantByteLength = (sizeof(float) * 24); // RtParams: uint2 extent + uint2 pad + 4x float4 camera + float4 ground plane
    private const uint TlasBindingIndex = 2; // the acceleration structure at binding 2 / register(t1)
    private const uint WorkgroupEdge = 8;
    private const uint WorldHeight = 600;
    private const uint WorldWidth = 960;

    /// <inheritdoc/>
    public string Name => "rt";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        // DXR 1.1 (the Direct3D 12 side's inline ray tracing) requires Windows 10 1809 — a step past the tier's base.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) {
            return PostStageOutcome.Skip(detail: "the ray-tracing tier (DXR 1.1) requires Windows 10.0.17763+");
        }

        return RunCore(context: context);
    }

    [SupportedOSPlatform("windows10.0.17763")]
    private static PostStageOutcome RunCore(PostContext context) {
        // The skip-with-note probes, BEFORE any RT resource is built. Vulkan: the host registration's neutral factory
        // (the logical-device factory enables VK_KHR_ray_query + the acceleration-structure bundle whenever the
        // physical device supports them, so IsSupported reflects the adapter, not a host-creation gap).
        var vulkanDevice = context.RequireGpuDevice();
        var vulkanFactory = context.Resolve<IGpuAccelerationStructureFactory>();

        using (var vulkanProbe = vulkanFactory.Create(deviceContext: vulkanDevice)) {
            if (!vulkanProbe.IsSupported) {
                return PostStageOutcome.Skip(detail: "the Vulkan host device lacks inline ray-query support (VK_KHR_ray_query); hardware RT has nothing to prove on this adapter");
            }
        }

        // Direct3D 12: the DXR 1.1 factory over the shared Tier-C device.
        var directX = context.RequireDirectXDevice();
        var directXFactory = new DirectXGpuAccelerationStructureFactory();

        using (var directXProbe = directXFactory.Create(deviceContext: directX.DeviceContext)) {
            if (!directXProbe.IsSupported) {
                return PostStageOutcome.Skip(detail: "the shared Direct3D 12 device lacks DXR 1.1 inline ray tracing; hardware RT has nothing to prove on this adapter");
            }
        }

        // Both sides support it — the live path. The same deterministic hero scene + camera the compute-world
        // stages diff, so the RT image is directly comparable to the C5 baseline.
        var program = WorldStage.BuildHeroScene();
        var frame = WorldStage.BuildHeroFrame(program: program, width: WorldWidth, height: WorldHeight);

        var vulkanPixels = RenderRt(
            accelerationFactory: vulkanFactory,
            bytecodeExtension: ".spv",
            device: vulkanDevice,
            frame: frame,
            gpu: context.Resolve<IGpuComputeServices>()
        );

        var directXPixels = WorldStage.RenderDirectXDiagnosed(directX: directX, render: () => RenderRt(
            accelerationFactory: directXFactory,
            bytecodeExtension: ".dxil",
            device: directX.DeviceContext,
            frame: frame,
            gpu: directX.Services.GetRequiredService<IGpuComputeServices>()
        ));

        // The RT kernel compiles the WHOLE VM interpreter, so its ±1 residual redistributes on every codegen roll
        // (see WorldLsbExact's doc) — the guard is that every delta is exactly ±1, not where the noise lands.
        return ParityCheck.WriteEvaluateReport(
            artifactsDirectory: context.ArtifactsDirectory,
            prefix: "rt",
            referencePixels: vulkanPixels,
            comparandPixels: directXPixels,
            width: (int)WorldWidth,
            height: (int)WorldHeight,
            thresholds: ParityThresholds.WorldLsbExact,
            passLabel: $"{WorldWidth}x{WorldHeight} ray-query hero view | Vulkan (ray-query) vs Direct3D 12 (DXR 1.1) within WorldLsbExact thresholds"
        );
    }

    // The full ray-query render on ONE backend — the reference's EnsureResources + PackCamera + Render, collapsed to
    // a one-shot: build the TLAS instances from the program, create the pipeline over the output/program/TLAS
    // bindings, record build + dispatch in one submit-and-wait, and read the image back.
    private static byte[] RenderRt(IGpuComputeServices gpu, IGpuDeviceContext device, IGpuAccelerationStructureFactory accelerationFactory, string bytecodeExtension, SdfFrame frame) {
        var deviceHandle = device.DeviceHandle;
        var program = frame.Program;

        using var acceleration = accelerationFactory.Create(deviceContext: device);

        // One unit-AABB instance per finite SDF primitive (the infinite floor is the kernel's analytic plane).
        acceleration.EnsureCreated(maxInstanceCount: InstanceCapacity);

        var bounds = RtWorldInstances.Extract(program: program);
        var groundPlane = RtWorldInstances.ExtractGroundPlane(program: program);
        var instanceCount = (uint)Math.Min(bounds.Count, (int)InstanceCapacity);

        for (var index = 0; (index < (int)instanceCount); index++) {
            var bound = bounds[index];

            acceleration.WriteInstance(
                centerX: bound.Center.X,
                centerY: bound.Center.Y,
                centerZ: bound.Center.Z,
                halfExtentX: bound.HalfExtent.X,
                halfExtentY: bound.HalfExtent.Y,
                halfExtentZ: bound.HalfExtent.Z,
                index: index,
                instanceIndex: bound.CustomIndex,
                visibilityMask: 0xFF
            );
        }

        // RtParams: image extent + the frame's first-view camera basis (tan(fov/2) in right.w, aspect in up.w,
        // matching the SDF kernels' cameraRayDirection) + the world-space ground plane.
        var pushConstant = new byte[PushConstantByteLength];
        var words = MemoryMarshal.Cast<byte, uint>(span: pushConstant.AsSpan());

        words[0] = WorldWidth; words[1] = WorldHeight; words[2] = 0; words[3] = 0;

        var camera = frame.Views[0].Camera;
        var floats = MemoryMarshal.Cast<byte, float>(span: pushConstant.AsSpan());

        floats[4] = camera.Position.X; floats[5] = camera.Position.Y; floats[6] = camera.Position.Z; floats[7] = 0f;
        floats[8] = camera.Right.X; floats[9] = camera.Right.Y; floats[10] = camera.Right.Z; floats[11] = camera.TanHalfFieldOfView;
        floats[12] = camera.Up.X; floats[13] = camera.Up.Y; floats[14] = camera.Up.Z; floats[15] = camera.AspectRatio;
        floats[16] = camera.Forward.X; floats[17] = camera.Forward.Y; floats[18] = camera.Forward.Z; floats[19] = 0f;
        floats[20] = groundPlane.X; floats[21] = groundPlane.Y; floats[22] = groundPlane.Z; floats[23] = groundPlane.W;

        // Output image (0) + SDF program (1) + the top-level acceleration structure (2). The seam maps the
        // AccelerationStructure kind to each backend's binding (a Vulkan AS descriptor, or a Direct3D 12 SRV); the
        // binding ORDER assigns the Direct3D 12 SRV registers: program → t0 (matches sdf-vm.hlsli), TLAS → t1.
        GpuComputeBinding[] bindings = [
            new GpuComputeBinding(Binding: OutputBindingIndex, Kind: GpuComputeBindingKind.StorageImage),
            new GpuComputeBinding(Binding: ProgramBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            new GpuComputeBinding(Binding: TlasBindingIndex, Kind: GpuComputeBindingKind.AccelerationStructure),
        ];

        using var shaderModule = gpu.ShaderModuleFactory.Create(deviceContext: device, stage: GpuShaderStage.Compute, bytecode: PostShaders.Read(folder: "Sdf", file: $"sdf-world-rt-debug.rq.comp{bytecodeExtension}"));
        using var computePipeline = gpu.ComputePipelineFactory.Create(
            bindings: bindings,
            computeShaderModule: shaderModule,
            deviceContext: device,
            pushConstantBinding: new GpuPushConstantBinding(data: pushConstant, offset: 0, stageFlags: GpuShaderStage.Compute)
        );
        using var storageImage = gpu.StorageImageFactory.Create(deviceContext: device, format: Format, height: WorldHeight, width: WorldWidth);
        using var programBuffer = gpu.StorageBufferFactory.Create(deviceContext: device, sizeBytes: ((ulong)program.Words.Length * sizeof(uint)));
        using var commandPool = gpu.CommandPoolFactory.Create(deviceContext: device);

        programBuffer.Write<uint>(data: program.Words);

        // One set (output image + program + acceleration structure); the pool capacity is derived from the bindings.
        var poolSizes = GpuDescriptorPoolSizes.ForSets(bindings);
        var pool = gpu.DescriptorAllocator.CreatePool(deviceHandle: deviceHandle, sizes: poolSizes);

        try {
            var set = gpu.DescriptorAllocator.AllocateSet(descriptorSetLayoutHandle: computePipeline.DescriptorSetLayoutHandle, deviceHandle: deviceHandle, poolHandle: pool);

            gpu.DescriptorAllocator.WriteStorageImage(arrayElement: 0, binding: OutputBindingIndex, descriptorSetHandle: set, deviceHandle: deviceHandle, imageViewHandle: storageImage.ImageViewHandle);
            gpu.DescriptorAllocator.WriteStorageBuffer(binding: ProgramBindingIndex, bufferHandle: programBuffer.BufferHandle, bufferSize: programBuffer.SizeBytes, descriptorSetHandle: set, deviceHandle: deviceHandle);
            gpu.DescriptorAllocator.WriteAccelerationStructure(accelerationStructureReference: acceleration.TlasReference, binding: TlasBindingIndex, descriptorSetHandle: set, deviceHandle: deviceHandle);

            var recorder = gpu.ComputeRecorder;
            var commandBuffer = commandPool.CommandBufferHandle;

            recorder.BeginCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle);

            // Build the BLAS + TLAS over the instance buffer (the build records its own barriers and publishes the
            // fresh TLAS to the dispatch that follows).
            acceleration.RecordBuild(
                commandBufferHandle: commandBuffer,
                includeBlasBuild: true,
                instanceCount: instanceCount
            );

            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderWrite, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: deviceHandle, imageHandle: storageImage.ImageHandle, newLayout: GpuImageLayout.General, oldLayout: GpuImageLayout.Undefined, sourceAccessMask: GpuComputeAccess.None, sourceStageMask: GpuComputeStage.TopOfPipe);
            recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle, pipelineHandle: computePipeline.Handle);
            recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: set, deviceHandle: deviceHandle, pipelineLayoutHandle: computePipeline.LayoutHandle);
            recorder.PushConstants(commandBufferHandle: commandBuffer, data: pushConstant, deviceHandle: deviceHandle, offset: 0, pipelineLayoutHandle: computePipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
            recorder.Dispatch(
                commandBufferHandle: commandBuffer,
                deviceHandle: deviceHandle,
                groupCountX: ((WorldWidth + (WorkgroupEdge - 1)) / WorkgroupEdge),
                groupCountY: ((WorldHeight + (WorkgroupEdge - 1)) / WorkgroupEdge),
                groupCountZ: 1
            );

            // Hand the output off readback-readable.
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderRead, destinationStageMask: GpuComputeStage.FragmentShader, deviceHandle: deviceHandle, imageHandle: storageImage.ImageHandle, newLayout: GpuImageLayout.ShaderReadOnly, oldLayout: GpuImageLayout.General, sourceAccessMask: GpuComputeAccess.ShaderWrite, sourceStageMask: GpuComputeStage.ComputeShader);
            recorder.EndCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle);

            gpu.QueueSubmitter.SubmitAndWait(commandBufferHandles: [commandBuffer], deviceContext: device);

            using var readback = gpu.SurfaceTransferFactory.CreateReadback(deviceContext: device);

            return readback.Read(
                bytesPerPixel: 4,
                deviceContext: device,
                format: Format,
                height: WorldHeight,
                sourceImageHandle: storageImage.ImageHandle,
                width: WorldWidth
            ).ToArray();
        } finally {
            gpu.DescriptorAllocator.DestroyPool(deviceHandle: deviceHandle, poolHandle: pool);
        }
    }
}
