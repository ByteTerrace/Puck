using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SdfVm;

namespace Puck.Demo;

/// <summary>
/// The ray-query world producer — a BACKEND-NEUTRAL compute node that ray-traces the SDF world's primitives as a
/// per-frame TLAS of unit-AABB instances (one per finite primitive), lit and per-instance-tinted from the live
/// camera. It drives only the neutral seam: <see cref="IGpuComputeServices"/> plus an
/// <see cref="IGpuAccelerationStructure"/> (the acceleration-structure build and the TLAS descriptor are the
/// <see cref="GpuComputeBindingKind.AccelerationStructure"/> binding the seam now models), so the identical node runs
/// on whichever backend the host publishes — Vulkan ray-query or Direct3D 12 DXR 1.1, both from the one ray-query
/// kernel. It resolves the shared device from <see cref="FrameContext.Host"/> and the scene from an
/// <see cref="ISdfFrameSource"/>. The current kernel renders the instance BOUNDS (the SDF march integration is later).
/// </summary>
internal sealed class RtWorldProducerNode : IRenderNode {
    private const uint Format = GpuPixelFormat.R8G8B8A8Unorm;
    private const uint InstanceCapacity = 64; // the instance buffer / TLAS capacity; the scene fills as many as it has
    private const int PushConstantByteLength = sizeof(float) * 24; // RtParams: uint2 extent + uint2 pad + 4x float4 camera + float4 ground plane
    private const uint OutputBindingIndex = 0; // RWTexture2D at binding 0 / register(u0)
    private const uint ProgramBindingIndex = 1; // the SDF program at binding 1 / register(t0) (matches sdf-vm.hlsli)
    private const uint TlasBindingIndex = 2; // the acceleration structure at binding 2 / register(t1)
    private const uint WorkgroupEdge = 8;

    private readonly ReadOnlyMemory<byte> m_bytecode;
    private readonly string? m_capturePath;
    private readonly NodeDescriptor m_descriptor = new(
        Name: "rt-world",
        SurfaceId: SurfaceId.New()
    );
    private readonly ISdfFrameSource m_frameSource;
    private readonly uint m_height;
    private readonly byte[] m_pushConstant = new byte[PushConstantByteLength];
    private readonly IServiceProvider m_serviceProvider;
    private readonly uint m_width;
    private IGpuAccelerationStructure? m_acceleration;
    private bool m_blasBuilt;
    private bool m_captured;
    private Vector4 m_groundPlane;
    private IGpuStorageBuffer? m_programBuffer;
    private IGpuComputeCommandPool? m_commandPool;
    private IGpuComputePipeline? m_computePipeline;
    private IGpuComputeRecorder? m_computeRecorder;
    private IGpuDescriptorAllocator? m_descriptorAllocator;
    private IGpuDeviceContext? m_deviceContext;
    private nint m_deviceHandle;
    private bool m_disposed;
    private IGpuComputeServices? m_gpu;
    private bool m_imageInitialized;
    private uint m_instanceCount;
    private nint m_pool;
    private IGpuQueueSubmitter? m_queueSubmitter;
    private IGpuSurfaceReadback? m_readback;
    private bool m_resourcesReady;
    private nint m_set;
    private IGpuShaderModule? m_shaderModule;
    private IGpuStorageImage? m_storageImage;
    private bool m_unsupported;

    /// <summary>Initializes a new instance of the <see cref="RtWorldProducerNode"/> class.</summary>
    /// <param name="serviceProvider">The provider of the neutral GPU compute services and the acceleration-structure factory (the device comes from the host context).</param>
    /// <param name="frameSource">The per-frame source of the scene (decomposed into TLAS instances) and the camera.</param>
    /// <param name="bytecode">The compiled ray-query kernel for the host backend (SPIR-V for Vulkan, DXIL for Direct3D 12).</param>
    /// <param name="width">The render width in pixels.</param>
    /// <param name="height">The render height in pixels.</param>
    /// <param name="capturePath">An optional PNG path; when set, the first rendered frame is read back from the GPU and written there.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A dimension is zero.</exception>
    public RtWorldProducerNode(IServiceProvider serviceProvider, ISdfFrameSource frameSource, ReadOnlyMemory<byte> bytecode, uint width, uint height, string? capturePath = null) {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(frameSource);

        if (
            (0 == width) ||
            (0 == height)
        ) {
            throw new ArgumentException(message: "Ray-query world producer dimensions must be non-zero.");
        }

        m_bytecode = bytecode;
        m_capturePath = capturePath;
        m_frameSource = frameSource;
        m_height = height;
        m_serviceProvider = serviceProvider;
        m_width = width;
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (m_disposed) {
            return default;
        }

        // The shared device is an inherited host capability (the Vulkan host, or the Direct3D 12 host wrapper).
        if (!context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var gpuDevice)) {
            return default;
        }

        var frame = m_frameSource.CaptureFrame(width: m_width, height: m_height, deltaSeconds: (float)context.DeltaSeconds, interpolationAlpha: (float)context.InterpolationAlpha);

        EnsureResources(gpuDevice: gpuDevice, frame: frame);

        if (m_unsupported) {
            return default;
        }

        PackCamera(frame: frame);
        Render();

        if (
            (m_capturePath is not null) &&
            !m_captured
        ) {
            PngImage.Write(
                height: (int)m_height,
                path: m_capturePath,
                rgba: ReadPixels().ToArray(),
                width: (int)m_width
            );
            m_captured = true;
        }

        return new Surface(
            ImageViewHandle: m_storageImage!.ImageViewHandle,
            Width: m_width,
            Height: m_height,
            Format: SurfaceFormat.R8G8B8A8Unorm
        );
    }

    private void EnsureResources(IGpuDeviceContext gpuDevice, SdfFrame frame) {
        if (
            m_resourcesReady ||
            m_unsupported
        ) {
            return;
        }

        m_deviceContext = gpuDevice;
        m_deviceHandle = gpuDevice.DeviceHandle;
        m_acceleration = m_serviceProvider.GetRequiredService<IGpuAccelerationStructureFactory>().Create(deviceContext: gpuDevice);

        // Gate on inline ray tracing; an unsupported device makes the path a no-op (a blank surface) rather than a crash.
        if (!m_acceleration.IsSupported) {
            m_unsupported = true;

            Console.Error.WriteLine(value: "[world-rt] the host device has no inline ray-tracing support; the ray-traced world is unavailable on this adapter. On Vulkan, re-run with PUCK_RAY_QUERY=0 for the beam-path SDF world.");

            return;
        }

        m_gpu = m_serviceProvider.GetRequiredService<IGpuComputeServices>();
        m_computeRecorder = m_gpu.ComputeRecorder;
        m_descriptorAllocator = m_gpu.DescriptorAllocator;
        m_queueSubmitter = m_gpu.QueueSubmitter;

        // The acceleration structure, then one unit-AABB instance per finite SDF primitive (static scene → written
        // once; the TLAS is still rebuilt over them each frame). Each instance scales/places the shared unit box onto
        // its primitive's world bound, masked 0xFF.
        m_acceleration.EnsureCreated(maxInstanceCount: InstanceCapacity);

        var bounds = RtWorldInstances.Extract(program: frame.Program);

        m_groundPlane = RtWorldInstances.ExtractGroundPlane(program: frame.Program);
        m_instanceCount = (uint)Math.Min(bounds.Count, (int)InstanceCapacity);

        for (var index = 0; (index < (int)m_instanceCount); index++) {
            var bound = bounds[index];

            m_acceleration.WriteInstance(
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

        m_storageImage = m_gpu.StorageImageFactory.Create(deviceContext: gpuDevice, format: Format, height: m_height, width: m_width);
        m_shaderModule = m_gpu.ShaderModuleFactory.Create(deviceContext: gpuDevice, stage: GpuShaderStage.Compute, bytecode: m_bytecode);

        // The scene program the kernel marches (read-only; an upload buffer is fine since it is never GPU-written).
        m_programBuffer = m_gpu.StorageBufferFactory.Create(deviceContext: gpuDevice, sizeBytes: ((ulong)frame.Program.Words.Length * sizeof(uint)));
        m_programBuffer.Write<uint>(data: frame.Program.Words);

        // Output image (0) + SDF program (1) + the top-level acceleration structure (2). The seam maps the
        // AccelerationStructure kind to each backend's binding (a Vulkan AS descriptor, or a Direct3D 12 SRV). The
        // binding ORDER assigns the Direct3D 12 SRV registers: program → t0 (matches sdf-vm.hlsli), TLAS → t1.
        GpuComputeBinding[] bindings = [
            new GpuComputeBinding(Binding: OutputBindingIndex, Kind: GpuComputeBindingKind.StorageImage),
            new GpuComputeBinding(Binding: ProgramBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            new GpuComputeBinding(Binding: TlasBindingIndex, Kind: GpuComputeBindingKind.AccelerationStructure),
        ];

        m_computePipeline = m_gpu.ComputePipelineFactory.Create(
            bindings: bindings,
            computeShaderModule: m_shaderModule,
            deviceContext: gpuDevice,
            pushConstantBinding: new GpuPushConstantBinding(data: m_pushConstant, offset: 0, stageFlags: GpuShaderStage.Compute)
        );

        // One set (output image + program + acceleration structure); the pool capacity is derived from the bindings,
        // so adding a binding can't silently under-provision it.
        var poolSizes = GpuDescriptorPoolSizes.ForSets(bindings);

        m_pool = m_descriptorAllocator.CreatePool(deviceHandle: m_deviceHandle, sizes: poolSizes);
        m_set = m_descriptorAllocator.AllocateSet(descriptorSetLayoutHandle: m_computePipeline.DescriptorSetLayoutHandle, deviceHandle: m_deviceHandle, poolHandle: m_pool);

        m_descriptorAllocator.WriteStorageImage(arrayElement: 0, binding: OutputBindingIndex, descriptorSetHandle: m_set, deviceHandle: m_deviceHandle, imageViewHandle: m_storageImage.ImageViewHandle);
        m_descriptorAllocator.WriteStorageBuffer(binding: ProgramBindingIndex, bufferHandle: m_programBuffer.BufferHandle, bufferSize: m_programBuffer.SizeBytes, descriptorSetHandle: m_set, deviceHandle: m_deviceHandle);
        m_descriptorAllocator.WriteAccelerationStructure(accelerationStructureReference: m_acceleration.TlasReference, binding: TlasBindingIndex, descriptorSetHandle: m_set, deviceHandle: m_deviceHandle);

        m_commandPool = m_gpu.CommandPoolFactory.Create(deviceContext: gpuDevice);
        m_resourcesReady = true;
    }

    // Pack the frame's first-view camera into the push block the kernel reads — image extent plus the basis vectors,
    // with tan(fov/2) in right.w and aspect in up.w, matching the SDF kernels' cameraRayDirection.
    private void PackCamera(SdfFrame frame) {
        var camera = frame.Views[0].Camera;
        var words = MemoryMarshal.Cast<byte, uint>(span: m_pushConstant.AsSpan());

        words[0] = m_width; words[1] = m_height; words[2] = 0; words[3] = 0;

        var floats = MemoryMarshal.Cast<byte, float>(span: m_pushConstant.AsSpan());

        floats[4] = camera.Position.X; floats[5] = camera.Position.Y; floats[6] = camera.Position.Z; floats[7] = 0f;
        floats[8] = camera.Right.X; floats[9] = camera.Right.Y; floats[10] = camera.Right.Z; floats[11] = camera.TanHalfFieldOfView;
        floats[12] = camera.Up.X; floats[13] = camera.Up.Y; floats[14] = camera.Up.Z; floats[15] = camera.AspectRatio;
        floats[16] = camera.Forward.X; floats[17] = camera.Forward.Y; floats[18] = camera.Forward.Z; floats[19] = 0f;
        floats[20] = m_groundPlane.X; floats[21] = m_groundPlane.Y; floats[22] = m_groundPlane.Z; floats[23] = m_groundPlane.W;
    }
    private void Render() {
        var recorder = m_computeRecorder!;
        var commandBuffer = m_commandPool!.CommandBufferHandle;
        var outputOldLayout = m_imageInitialized ? GpuImageLayout.ShaderReadOnly : GpuImageLayout.Undefined;
        var outputSourceAccess = m_imageInitialized ? GpuComputeAccess.ShaderRead : GpuComputeAccess.None;
        var outputSourceStage = m_imageInitialized ? GpuComputeStage.FragmentShader : GpuComputeStage.TopOfPipe;

        recorder.BeginCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);

        // Build the TLAS over this frame's instance buffer (the build records its own barriers and publishes the fresh
        // TLAS to the dispatch that follows). The static unit-AABB BLAS is built only in the first recording.
        m_acceleration!.RecordBuild(
            commandBufferHandle: commandBuffer,
            includeBlasBuild: !m_blasBuilt,
            instanceCount: m_instanceCount
        );

        recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuComputeAccess.ShaderWrite,
            destinationStageMask: GpuComputeStage.ComputeShader,
            deviceHandle: m_deviceHandle,
            imageHandle: m_storageImage!.ImageHandle,
            newLayout: GpuImageLayout.General,
            oldLayout: outputOldLayout,
            sourceAccessMask: outputSourceAccess,
            sourceStageMask: outputSourceStage
        );

        recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, pipelineHandle: m_computePipeline!.Handle);
        recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: m_set, deviceHandle: m_deviceHandle, pipelineLayoutHandle: m_computePipeline.LayoutHandle);
        recorder.PushConstants(commandBufferHandle: commandBuffer, data: m_pushConstant, deviceHandle: m_deviceHandle, offset: 0, pipelineLayoutHandle: m_computePipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
        recorder.Dispatch(
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle,
            groupCountX: ((m_width + (WorkgroupEdge - 1)) / WorkgroupEdge),
            groupCountY: ((m_height + (WorkgroupEdge - 1)) / WorkgroupEdge),
            groupCountZ: 1
        );

        recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuComputeAccess.ShaderRead,
            destinationStageMask: GpuComputeStage.FragmentShader,
            deviceHandle: m_deviceHandle,
            imageHandle: m_storageImage.ImageHandle,
            newLayout: GpuImageLayout.ShaderReadOnly,
            oldLayout: GpuImageLayout.General,
            sourceAccessMask: GpuComputeAccess.ShaderWrite,
            sourceStageMask: GpuComputeStage.ComputeShader
        );
        recorder.EndCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);

        // Fire-and-forget: same-device, so the host draining the device between frames orders this frame's writes
        // against the previous frame's reads and the command-buffer reuse (the launcher's BeginFrame WaitIdle).
        m_queueSubmitter!.Submit(commandBufferHandles: [commandBuffer], deviceContext: m_deviceContext!);

        m_blasBuilt = true;
        m_imageInitialized = true;
    }
    private ReadOnlyMemory<byte> ReadPixels() {
        m_readback ??= m_gpu!.SurfaceTransferFactory.CreateReadback(deviceContext: m_deviceContext!);

        return m_readback.Read(
            bytesPerPixel: 4,
            deviceContext: m_deviceContext!,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: m_height,
            sourceImageHandle: m_storageImage!.ImageHandle,
            width: m_width
        );
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        // Drain before tearing down GPU resources: the per-frame submits are fire-and-forget (nothing fences them).
        m_deviceContext?.WaitIdle();

        m_readback?.Dispose();
        m_commandPool?.Dispose();
        m_computePipeline?.Dispose();

        if (
            (0 != m_pool) &&
            (m_descriptorAllocator is not null)
        ) {
            m_descriptorAllocator.DestroyPool(deviceHandle: m_deviceHandle, poolHandle: m_pool);
            m_pool = 0;
        }

        m_acceleration?.Dispose();
        m_programBuffer?.Dispose();
        m_storageImage?.Dispose();
        m_shaderModule?.Dispose();
    }
}
