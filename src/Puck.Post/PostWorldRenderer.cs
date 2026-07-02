using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.SdfVm;

namespace Puck.Post;

/// <summary>
/// A reusable, self-contained harness for the compute SDF WORLD pipeline — the POST's from-scratch port of the demo's
/// <c>WorldProducerNode</c> (the worked reference; its buffer/push/binding layouts are reproduced here EXACTLY). One
/// instance owns a scene program (uploaded to the GPU ONCE, at construction — the "program uploaded once" seam the
/// dynamic-transform channel rides) plus every pipeline/buffer/image the four kernels need, and each
/// <see cref="RenderFrame"/> call runs the full chain in one submit-and-wait:
/// <c>sdf-beam.comp</c> (tile-cull cone-march prepass) → <c>sdf-cull-args.comp</c> (GPU-written INDIRECT dispatch args:
/// the surviving-tile bbox) → <c>sdf-world-views.comp</c> (per-view render, dispatched INDIRECTLY from those args) →
/// <c>sdf-world-composite.comp</c> (source-agnostic region composite, also dispatched indirectly), then reads the
/// composited RGBA back. Per-frame data — cameras, regions, time, and the dynamic entity transforms — comes from the
/// <see cref="SdfFrame"/> each call, so viewport counts and layouts may change frame to frame (capacity is always the
/// kernels' 4-slot source array). Backend-neutral through the <see cref="IGpuComputeServices"/> seam; the kernel
/// bytecode extension (<c>".spv"</c> / <c>".dxil"</c>) is a parameter so the cross-backend tier can reuse it.
/// </summary>
internal sealed class PostWorldRenderer : IDisposable {
    private const uint CompositeOutputBindingIndex = 0; // sdf-world-composite.comp: Output at binding 0
    private const int CompositePushByteLength = (16 + ((sizeof(float) * 4) * MaxViewports)); // CompositeParams2: uint2 extent + uint count + uint tileGridPacked + float4 rects[4]
    private const uint CompositeSourceBindingIndex = 1; // sdf-world-composite.comp: sources[] at binding 1
    private const uint CullArgsBindingIndex = 5; // sdf-cull-args.comp: views indirect dispatch args (register u0)
    private const uint CullBoundsBindingIndex = 6; // sdf-cull-args.comp: bbox group origin (register u1); read by sdf-world-views.comp at binding 8
    private const uint DynamicTransformBindingIndex = 9; // sdf-vm.hlsli's [[vk::binding(9, 0)]] / register(t2) (world path)
    private const int DynamicTransformByteLength = ((sizeof(float) * 4) * 2); // 32-byte rigid transform: float4 position + float4 orientation quaternion (KEEP IN SYNC with sdf-vm.hlsli sdfDynamicTransforms)
    private const GpuPixelFormat Format = GpuPixelFormat.R8G8B8A8Unorm;
    private const int MaxViewports = 4; // the source array length in the kernels (sources[4])
    private const uint ProgramBindingIndex = 1; // matches sdf-vm.hlsli's [[vk::binding(1, 0)]] / register(t0)
    private const int PushConstantByteLength = ((sizeof(uint) * 4) * 2); // 32-byte CompositeParams (16-byte rounded)
    private const uint TileBindingIndex = 3; // matches sdf-world.hlsli's [[vk::binding(3, 0)]]
    private const uint TileSize = 16; // KEEP IN SYNC with WorldTileSize in sdf-world.hlsli
    private const int ViewportByteLength = ((sizeof(float) * 4) * 5); // 80-byte ViewportData (KEEP IN SYNC with sdf-world.hlsli)
    private const uint ViewportBindingIndex = 2; // matches sdf-world.hlsli's [[vk::binding(2, 0)]]
    private const uint ViewsCullBoundsBindingIndex = 8; // sdf-world-views.comp: the bbox origin (register t3); slot 8 is PAST the source array at 4..7
    private const uint ViewSourceBindingIndex = 4; // sdf-world-views.comp: sources[] LAST (after the fixed 1/2/3)
    private const uint WorkgroupEdge = 8;

    private readonly IGpuComputePipeline m_beamPipeline;
    private readonly nint m_beamSet;
    private readonly IGpuShaderModule m_beamShaderModule;
    private readonly IGpuComputeCommandPool m_commandPool;
    private readonly IGpuStorageBuffer m_compositeArgsBuffer;
    private readonly IGpuComputePipeline m_compositePipeline;
    private readonly byte[] m_compositePush = new byte[CompositePushByteLength];
    private readonly nint m_compositeSet;
    private readonly IGpuShaderModule m_compositeShaderModule;
    private readonly IGpuComputePipeline m_cullArgsPipeline;
    private readonly nint m_cullArgsSet;
    private readonly IGpuShaderModule m_cullArgsShaderModule;
    private readonly IGpuStorageBuffer m_cullBoundsBuffer;
    private readonly IGpuDescriptorAllocator m_descriptorAllocator;
    private readonly IGpuDeviceContext m_deviceContext;
    private readonly nint m_deviceHandle;
    private readonly IGpuStorageBuffer m_dynamicTransformBuffer;
    private readonly byte[] m_dynamicTransformScratch;
    private readonly IGpuComputeServices m_gpu;
    private readonly uint m_height;
    private readonly nint m_pool;
    private readonly IGpuStorageBuffer m_programBuffer;
    private readonly byte[] m_pushConstant = new byte[PushConstantByteLength];
    private readonly IGpuSurfaceReadback m_readback;
    private readonly IGpuStorageImage[] m_sourceTextures;
    private readonly IGpuStorageImage m_storageImage;
    private readonly IGpuStorageBuffer m_tileBuffer;
    private readonly uint m_tileGridX;
    private readonly uint m_tileGridY;
    private readonly IGpuStorageBuffer m_viewportBuffer;
    private readonly byte[] m_viewportScratch = new byte[(MaxViewports * ViewportByteLength)];
    private readonly IGpuStorageBuffer m_viewsArgsBuffer;
    private readonly IGpuComputePipeline m_viewsPipeline;
    private readonly nint m_viewsSet;
    private readonly IGpuShaderModule m_viewsShaderModule;
    private readonly uint m_width;
    private bool m_disposed;
    private bool m_imageInitialized;

    /// <summary>Initializes a new instance of the <see cref="PostWorldRenderer"/> class: builds the four pipelines,
    /// every buffer and image at 4-viewport capacity, and uploads <paramref name="program"/> ONCE.</summary>
    /// <param name="gpu">The neutral GPU compute services.</param>
    /// <param name="device">The GPU device the harness renders on.</param>
    /// <param name="bytecodeExtension">The compiled-kernel extension (<c>".spv"</c> for Vulkan, <c>".dxil"</c> for Direct3D 12).</param>
    /// <param name="program">The scene program, uploaded once; frames render against it.</param>
    /// <param name="width">The composited output width in pixels.</param>
    /// <param name="height">The composited output height in pixels.</param>
    /// <param name="dynamicTransformCapacity">The number of dynamic entity-transform slots to allocate (at least one slot is always bound so the binding stays valid for a static scene).</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A dimension is zero.</exception>
    public PostWorldRenderer(IGpuComputeServices gpu, IGpuDeviceContext device, string bytecodeExtension, SdfProgram program, uint width, uint height, int dynamicTransformCapacity = 1) {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrEmpty(bytecodeExtension);
        ArgumentNullException.ThrowIfNull(program);

        if (
            (0 == width) ||
            (0 == height)
        ) {
            throw new ArgumentException(message: "World renderer dimensions must be non-zero.");
        }

        m_deviceContext = device;
        m_deviceHandle = device.DeviceHandle;
        m_descriptorAllocator = gpu.DescriptorAllocator;
        m_gpu = gpu;
        m_height = height;
        m_tileGridX = ((width + (TileSize - 1)) / TileSize);
        m_tileGridY = ((height + (TileSize - 1)) / TileSize);
        m_width = width;
        m_dynamicTransformScratch = new byte[(Math.Max(1, dynamicTransformCapacity) * DynamicTransformByteLength)];

        var beamBytecode = PostShaders.Read(folder: "Sdf", file: $"sdf-beam.comp{bytecodeExtension}");
        var cullArgsBytecode = PostShaders.Read(folder: "Sdf", file: $"sdf-cull-args.comp{bytecodeExtension}");
        var viewsBytecode = PostShaders.Read(folder: "Sdf", file: $"sdf-world-views.comp{bytecodeExtension}");
        var compositeBytecode = PostShaders.Read(folder: "Sdf", file: $"sdf-world-composite.comp{bytecodeExtension}");

        m_beamShaderModule = gpu.ShaderModuleFactory.Create(deviceContext: device, stage: GpuShaderStage.Compute, bytecode: beamBytecode);
        m_cullArgsShaderModule = gpu.ShaderModuleFactory.Create(deviceContext: device, stage: GpuShaderStage.Compute, bytecode: cullArgsBytecode);
        m_viewsShaderModule = gpu.ShaderModuleFactory.Create(deviceContext: device, stage: GpuShaderStage.Compute, bytecode: viewsBytecode);
        m_compositeShaderModule = gpu.ShaderModuleFactory.Create(deviceContext: device, stage: GpuShaderStage.Compute, bytecode: compositeBytecode);

        // One FULL-SIZE source texture per viewport slot — Stage 1 renders each viewport's region-extent into it,
        // Stage 2 copies that into the screen region. Full-size (the largest any region can reach), never region-sized:
        // the split layouts animate every frame, so writes/reads always stay within the live region (≤ full).
        m_sourceTextures = new IGpuStorageImage[MaxViewports];

        for (var index = 0; (index < MaxViewports); index++) {
            m_sourceTextures[index] = gpu.StorageImageFactory.Create(deviceContext: device, format: Format, height: height, width: width);
        }

        m_storageImage = gpu.StorageImageFactory.Create(deviceContext: device, format: Format, height: height, width: width);
        m_programBuffer = gpu.StorageBufferFactory.Create(deviceContext: device, sizeBytes: ((ulong)program.Words.Length * sizeof(uint)));
        m_viewportBuffer = gpu.StorageBufferFactory.Create(deviceContext: device, sizeBytes: (ulong)m_viewportScratch.Length);
        m_dynamicTransformBuffer = gpu.StorageBufferFactory.Create(deviceContext: device, sizeBytes: (ulong)m_dynamicTransformScratch.Length);
        // The cull buffer is GPU-written by the beam prepass (a UAV), so it is device-local (a Direct3D 12 default heap).
        m_tileBuffer = gpu.StorageBufferFactory.CreateDeviceLocal(deviceContext: device, sizeBytes: ((ulong)MaxViewports * m_tileGridX * m_tileGridY * sizeof(float)));
        // GPU-driven cull: the cull-args pass reduces the cull buffer to the Stage-1 INDIRECT dispatch args (the
        // surviving-tile bbox, 3 group counts) and the bbox group origin (2 uints). Both are device-local UAVs.
        m_viewsArgsBuffer = gpu.StorageBufferFactory.CreateIndirectArgs(deviceContext: device, sizeBytes: (sizeof(uint) * 3), deviceLocal: true);
        m_cullBoundsBuffer = gpu.StorageBufferFactory.CreateDeviceLocal(deviceContext: device, sizeBytes: (sizeof(uint) * 2));
        // Stage 2's full-frame composite grid is constant for the run, so its dispatch is INDIRECT from this
        // host-written args buffer — the same seam the reference exercises. Host-written once + host-coherent.
        m_compositeArgsBuffer = gpu.StorageBufferFactory.CreateIndirectArgs(deviceContext: device, sizeBytes: (sizeof(uint) * 3));
        m_compositeArgsBuffer.Write<uint>(data: [
            ((width + (WorkgroupEdge - 1)) / WorkgroupEdge),
            ((height + (WorkgroupEdge - 1)) / WorkgroupEdge),
            1u,
        ]);

        var pushConstantBinding = new GpuPushConstantBinding(data: m_pushConstant, offset: 0, stageFlags: GpuShaderStage.Compute);
        var compositePushBinding = new GpuPushConstantBinding(data: m_compositePush, offset: 0, stageFlags: GpuShaderStage.Compute);

        // Beam prepass: program (1) + viewports (2) + dynamic entity transforms (9) + cull buffer written (3). The cone
        // march runs map(), so it reads the dynamic transforms — listed after viewport so its SRV is t2.
        GpuComputeBinding[] beamBindings = [
            new GpuComputeBinding(Binding: ProgramBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            new GpuComputeBinding(Binding: ViewportBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            new GpuComputeBinding(Binding: DynamicTransformBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            new GpuComputeBinding(Binding: TileBindingIndex, Kind: GpuComputeBindingKind.StorageBufferReadWrite),
        ];

        // Cull-args reduction: cull buffer read (3) + the views indirect args written (5) + the bbox origin written (6).
        GpuComputeBinding[] cullArgsBindings = [
            new GpuComputeBinding(Binding: TileBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            new GpuComputeBinding(Binding: CullArgsBindingIndex, Kind: GpuComputeBindingKind.StorageBufferReadWrite),
            new GpuComputeBinding(Binding: CullBoundsBindingIndex, Kind: GpuComputeBindingKind.StorageBufferReadWrite),
        ];

        // Stage 1 (per-view SDF): program (1) + viewports (2) + dynamic entity transforms (9) + cull buffer read (3) +
        // the source array (4) + the GPU-computed bbox origin LAST (8). dynamicTransforms is listed BEFORE cullBounds so
        // the SRV registers resolve program t0, viewport t1, dynamicTransforms t2, cullBounds t3 (matching the HLSL).
        GpuComputeBinding[] viewsBindings = [
            new GpuComputeBinding(Binding: ProgramBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            new GpuComputeBinding(Binding: ViewportBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            new GpuComputeBinding(Binding: DynamicTransformBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            new GpuComputeBinding(Binding: TileBindingIndex, Kind: GpuComputeBindingKind.StorageBufferReadWrite),
            new GpuComputeBinding(Binding: ViewSourceBindingIndex, Kind: GpuComputeBindingKind.StorageImage, Count: MaxViewports),
            new GpuComputeBinding(Binding: ViewsCullBoundsBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
        ];

        // Stage 2 (source-agnostic composite): output image (0) + the source array (1) + the cull buffer read (3),
        // which the compositor uses to flatten every empty (culled) tile to a constant.
        GpuComputeBinding[] compositeBindings = [
            new GpuComputeBinding(Binding: CompositeOutputBindingIndex, Kind: GpuComputeBindingKind.StorageImage),
            new GpuComputeBinding(Binding: CompositeSourceBindingIndex, Kind: GpuComputeBindingKind.StorageImage, Count: MaxViewports),
            new GpuComputeBinding(Binding: TileBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
        ];

        m_beamPipeline = gpu.ComputePipelineFactory.Create(bindings: beamBindings, computeShaderModule: m_beamShaderModule, deviceContext: device, pushConstantBinding: pushConstantBinding);
        m_cullArgsPipeline = gpu.ComputePipelineFactory.Create(bindings: cullArgsBindings, computeShaderModule: m_cullArgsShaderModule, deviceContext: device, pushConstantBinding: pushConstantBinding);
        m_viewsPipeline = gpu.ComputePipelineFactory.Create(bindings: viewsBindings, computeShaderModule: m_viewsShaderModule, deviceContext: device, pushConstantBinding: pushConstantBinding);
        m_compositePipeline = gpu.ComputePipelineFactory.Create(bindings: compositeBindings, computeShaderModule: m_compositeShaderModule, deviceContext: device, pushConstantBinding: compositePushBinding);

        // One pool, FOUR independent sets, capacity DERIVED from the four binding lists (never hand-counted).
        var poolSizes = GpuDescriptorPoolSizes.ForSets(beamBindings, cullArgsBindings, viewsBindings, compositeBindings);

        m_pool = m_descriptorAllocator.CreatePool(deviceHandle: m_deviceHandle, sizes: poolSizes);

        m_beamSet = m_descriptorAllocator.AllocateSet(descriptorSetLayoutHandle: m_beamPipeline.DescriptorSetLayoutHandle, deviceHandle: m_deviceHandle, poolHandle: m_pool);
        WriteStorageBuffer(set: m_beamSet, binding: ProgramBindingIndex, buffer: m_programBuffer);
        WriteStorageBuffer(set: m_beamSet, binding: ViewportBindingIndex, buffer: m_viewportBuffer);
        WriteStorageBuffer(set: m_beamSet, binding: DynamicTransformBindingIndex, buffer: m_dynamicTransformBuffer);
        WriteStorageBufferReadWrite(set: m_beamSet, binding: TileBindingIndex, buffer: m_tileBuffer);

        // The cull buffer is read-only here (a stride-4 SRV on Direct3D 12); the args + bounds are written (UAVs).
        m_cullArgsSet = m_descriptorAllocator.AllocateSet(descriptorSetLayoutHandle: m_cullArgsPipeline.DescriptorSetLayoutHandle, deviceHandle: m_deviceHandle, poolHandle: m_pool);
        WriteStorageBufferReadOnly(set: m_cullArgsSet, binding: TileBindingIndex, buffer: m_tileBuffer);
        WriteStorageBufferReadWrite(set: m_cullArgsSet, binding: CullArgsBindingIndex, buffer: m_viewsArgsBuffer);
        WriteStorageBufferReadWrite(set: m_cullArgsSet, binding: CullBoundsBindingIndex, buffer: m_cullBoundsBuffer);

        m_viewsSet = m_descriptorAllocator.AllocateSet(descriptorSetLayoutHandle: m_viewsPipeline.DescriptorSetLayoutHandle, deviceHandle: m_deviceHandle, poolHandle: m_pool);
        WriteStorageBuffer(set: m_viewsSet, binding: ProgramBindingIndex, buffer: m_programBuffer);
        WriteStorageBuffer(set: m_viewsSet, binding: ViewportBindingIndex, buffer: m_viewportBuffer);
        WriteStorageBuffer(set: m_viewsSet, binding: DynamicTransformBindingIndex, buffer: m_dynamicTransformBuffer);
        WriteStorageBufferReadWrite(set: m_viewsSet, binding: TileBindingIndex, buffer: m_tileBuffer);
        WriteStorageBufferReadOnly(set: m_viewsSet, binding: ViewsCullBoundsBindingIndex, buffer: m_cullBoundsBuffer);

        m_compositeSet = m_descriptorAllocator.AllocateSet(descriptorSetLayoutHandle: m_compositePipeline.DescriptorSetLayoutHandle, deviceHandle: m_deviceHandle, poolHandle: m_pool);
        m_descriptorAllocator.WriteStorageImage(arrayElement: 0, binding: CompositeOutputBindingIndex, descriptorSetHandle: m_compositeSet, deviceHandle: m_deviceHandle, imageViewHandle: m_storageImage.ImageViewHandle);
        WriteStorageBufferReadOnly(set: m_compositeSet, binding: TileBindingIndex, buffer: m_tileBuffer);

        // The source array is static for the harness's lifetime (no hosted children): every element is a full-size
        // SDF source texture, so both sets bind all four once here rather than per frame.
        for (var element = 0u; (element < MaxViewports); element++) {
            var view = m_sourceTextures[element].ImageViewHandle;

            m_descriptorAllocator.WriteStorageImage(arrayElement: element, binding: ViewSourceBindingIndex, descriptorSetHandle: m_viewsSet, deviceHandle: m_deviceHandle, imageViewHandle: view);
            m_descriptorAllocator.WriteStorageImage(arrayElement: element, binding: CompositeSourceBindingIndex, descriptorSetHandle: m_compositeSet, deviceHandle: m_deviceHandle, imageViewHandle: view);
        }

        m_commandPool = gpu.CommandPoolFactory.Create(deviceContext: device);
        m_readback = gpu.SurfaceTransferFactory.CreateReadback(deviceContext: device);

        // The "uploaded once" seam: the program is written here and never again — frames move entities by rewriting
        // only the small dynamic-transform buffer.
        m_programBuffer.Write<uint>(data: program.Words);
    }

    /// <summary>Gets the native image handle of the composited output image. After <see cref="RenderFrame"/> returns,
    /// the image rests in the <see cref="GpuImageLayout.ShaderReadOnly"/> layout — a downstream pass (e.g. the
    /// world-child stage's viewport composite) may transition it and read it in place, zero-copy.</summary>
    public nint OutputImageHandle => m_storageImage.ImageHandle;
    /// <summary>Gets the native image-view handle of the composited output image (for binding it as a source in a
    /// downstream descriptor set).</summary>
    public nint OutputImageViewHandle => m_storageImage.ImageViewHandle;

    /// <summary>Renders one frame — beam → cull-args → views (indirect) → composite in a single submit — against the
    /// program uploaded at construction, waits for completion, and returns the composited RGBA readback.</summary>
    /// <param name="frame">The per-frame data: views (cameras + regions), time, and the dynamic entity transforms.</param>
    /// <returns>The composited output, tightly packed RGBA8, row-major.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The frame has zero views or more than the kernels' four source slots.</exception>
    public byte[] RenderFrame(SdfFrame frame) {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        var viewportCount = (uint)frame.Views.Count;

        if (
            (0 == viewportCount) ||
            (viewportCount > MaxViewports)
        ) {
            throw new ArgumentException(message: $"The world compositor supports 1 to {MaxViewports} viewports; the frame has {viewportCount}.");
        }

        PackViewports(frame: frame, viewportCount: viewportCount);
        m_viewportBuffer.Write<byte>(data: m_viewportScratch);
        PackDynamicTransforms(frame: frame);
        m_dynamicTransformBuffer.Write<byte>(data: m_dynamicTransformScratch);

        // CompositeParams { uint2 imageExtent; uint2 tileGrid; uint viewportCount; uint childMask; } — Stage 0/1 push.
        var pushWords = MemoryMarshal.Cast<byte, uint>(span: m_pushConstant.AsSpan());

        pushWords[0] = m_width; pushWords[1] = m_height; pushWords[2] = m_tileGridX; pushWords[3] = m_tileGridY; pushWords[4] = viewportCount; pushWords[5] = 0u; // no child slots in the POST harness

        BuildCompositePush(frame: frame);
        Record(viewportCount: viewportCount);
        m_gpu.QueueSubmitter.SubmitAndWait(commandBufferHandles: [m_commandPool.CommandBufferHandle], deviceContext: m_deviceContext);

        return m_readback.Read(
            bytesPerPixel: 4,
            deviceContext: m_deviceContext,
            format: Format,
            height: m_height,
            sourceImageHandle: m_storageImage.ImageHandle,
            width: m_width
        ).ToArray();
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        // Every RenderFrame submit-and-waits, so nothing is in flight at teardown.
        m_readback.Dispose();
        m_commandPool.Dispose();
        m_compositeArgsBuffer.Dispose();
        m_cullBoundsBuffer.Dispose();
        m_viewsArgsBuffer.Dispose();
        m_tileBuffer.Dispose();
        m_dynamicTransformBuffer.Dispose();
        m_viewportBuffer.Dispose();
        m_programBuffer.Dispose();
        m_beamPipeline.Dispose();
        m_cullArgsPipeline.Dispose();
        m_viewsPipeline.Dispose();
        m_compositePipeline.Dispose();
        m_descriptorAllocator.DestroyPool(deviceHandle: m_deviceHandle, poolHandle: m_pool);

        foreach (var source in m_sourceTextures) {
            source.Dispose();
        }

        m_storageImage.Dispose();
        m_beamShaderModule.Dispose();
        m_cullArgsShaderModule.Dispose();
        m_viewsShaderModule.Dispose();
        m_compositeShaderModule.Dispose();
    }

    // Stage 2's CompositeParams2 { uint2 imageExtent; uint viewportCount; uint tileGridPacked; float4 rects[4]; }: the
    // LIVE regions drive the layout every frame. word[3] packs the tile grid ((y << 16) | x) for empty-tile flattening.
    private void BuildCompositePush(SdfFrame frame) {
        var words = MemoryMarshal.Cast<byte, uint>(span: m_compositePush.AsSpan());

        words[0] = m_width; words[1] = m_height; words[2] = (uint)frame.Views.Count; words[3] = ((m_tileGridY << 16) | m_tileGridX);

        var floats = MemoryMarshal.Cast<byte, float>(span: m_compositePush.AsSpan());

        for (var index = 0; (index < frame.Views.Count); index++) {
            var region = frame.Views[index].Region;
            var b = (4 + (index * 4));

            floats[b + 0] = region.X; floats[b + 1] = region.Y; floats[b + 2] = region.Width; floats[b + 3] = region.Height;
        }
    }

    // Pack each frame's views (camera snapshot + region) into the 80-byte ViewportData rows the kernels read —
    // member-for-member from SdfFrame, no camera math (the snapshot already holds the basis + tan(fov/2) + aspect).
    private void PackViewports(SdfFrame frame, uint viewportCount) {
        var floats = MemoryMarshal.Cast<byte, float>(span: m_viewportScratch.AsSpan());

        for (var index = 0; (index < (int)viewportCount); index++) {
            var view = frame.Views[index];
            var camera = view.Camera;
            var region = view.Region;
            var b = (index * 20);

            floats[b + 0] = camera.Position.X; floats[b + 1] = camera.Position.Y; floats[b + 2] = camera.Position.Z; floats[b + 3] = frame.Time;          // position.xyz, time
            floats[b + 4] = camera.Right.X; floats[b + 5] = camera.Right.Y; floats[b + 6] = camera.Right.Z; floats[b + 7] = camera.TanHalfFieldOfView;     // right.xyz, tan(fov/2)
            floats[b + 8] = camera.Up.X; floats[b + 9] = camera.Up.Y; floats[b + 10] = camera.Up.Z; floats[b + 11] = camera.AspectRatio;                   // up.xyz, aspect
            floats[b + 12] = camera.Forward.X; floats[b + 13] = camera.Forward.Y; floats[b + 14] = camera.Forward.Z; floats[b + 15] = 0f;                  // forward.xyz, view mode (final)
            floats[b + 16] = region.X; floats[b + 17] = region.Y; floats[b + 18] = region.Width; floats[b + 19] = region.Height;                           // region origin.xy, size.xy
        }
    }

    // Pack each moving entity's rigid transform — 2 float4 per slot: position.xyz (+ pad) then the orientation
    // quaternion (xyzw) — for upload into the buffer SDF_OP_TRANSFORM_DYNAMIC indexes by slot. An empty list still
    // writes the one always-present slot as identity (a static scene never reads it). Clamped to the slot capacity.
    private void PackDynamicTransforms(SdfFrame frame) {
        var floats = MemoryMarshal.Cast<byte, float>(span: m_dynamicTransformScratch.AsSpan());
        var transforms = frame.DynamicTransforms;
        var capacity = (m_dynamicTransformScratch.Length / DynamicTransformByteLength);
        var count = Math.Min(transforms.Count, capacity);

        if (count == 0) {
            floats[0] = 0f; floats[1] = 0f; floats[2] = 0f; floats[3] = 0f;   // position.xyz, pad
            floats[4] = 0f; floats[5] = 0f; floats[6] = 0f; floats[7] = 1f;   // identity quaternion

            return;
        }

        for (var index = 0; (index < count); index++) {
            var transform = transforms[index];
            var b = (index * 8);

            floats[b + 0] = transform.Position.X; floats[b + 1] = transform.Position.Y; floats[b + 2] = transform.Position.Z; floats[b + 3] = 0f;
            floats[b + 4] = transform.Orientation.X; floats[b + 5] = transform.Orientation.Y; floats[b + 6] = transform.Orientation.Z; floats[b + 7] = transform.Orientation.W;
        }
    }

    // The reference's Render() minus timing/export/children: beam → barrier → cull-args → barrier + indirect-args
    // transition → views (INDIRECT) → barrier → composite (INDIRECT), with the output handed off readback-readable.
    private void Record(uint viewportCount) {
        var recorder = m_gpu.ComputeRecorder;
        var commandBuffer = m_commandPool.CommandBufferHandle;
        var outputOldLayout = m_imageInitialized ? GpuImageLayout.ShaderReadOnly : GpuImageLayout.Undefined;
        var outputSourceAccess = m_imageInitialized ? GpuComputeAccess.ShaderRead : GpuComputeAccess.None;
        var outputSourceStage = m_imageInitialized ? GpuComputeStage.FragmentShader : GpuComputeStage.TopOfPipe;

        recorder.BeginCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);

        // Tile-cull prepass: one invocation per (tile, viewport).
        recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, pipelineHandle: m_beamPipeline.Handle);
        recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: m_beamSet, deviceHandle: m_deviceHandle, pipelineLayoutHandle: m_beamPipeline.LayoutHandle);
        recorder.PushConstants(commandBufferHandle: commandBuffer, data: m_pushConstant, deviceHandle: m_deviceHandle, offset: 0, pipelineLayoutHandle: m_beamPipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
        recorder.Dispatch(
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle,
            groupCountX: ((m_tileGridX + (WorkgroupEdge - 1)) / WorkgroupEdge),
            groupCountY: ((m_tileGridY + (WorkgroupEdge - 1)) / WorkgroupEdge),
            groupCountZ: viewportCount
        );

        // Make the prepass's tile writes visible to the cull-args reduction's (and Stage 1's) reads.
        recorder.MemoryBarrier(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderRead, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: m_deviceHandle, sourceAccessMask: GpuComputeAccess.ShaderWrite, sourceStageMask: GpuComputeStage.ComputeShader);

        // Cull-args reduction (a single invocation): the GPU, not the CPU, sizes the Stage-1 views grid.
        recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, pipelineHandle: m_cullArgsPipeline.Handle);
        recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: m_cullArgsSet, deviceHandle: m_deviceHandle, pipelineLayoutHandle: m_cullArgsPipeline.LayoutHandle);
        recorder.PushConstants(commandBufferHandle: commandBuffer, data: m_pushConstant, deviceHandle: m_deviceHandle, offset: 0, pipelineLayoutHandle: m_cullArgsPipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
        recorder.Dispatch(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, groupCountX: 1, groupCountY: 1, groupCountZ: 1);

        // Order the cull-args writes before Stage 1. The bbox ORIGIN (cullBounds) is an ordinary compute-shader read,
        // so a global memory barrier suffices.
        recorder.MemoryBarrier(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderRead, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: m_deviceHandle, sourceAccessMask: GpuComputeAccess.ShaderWrite, sourceStageMask: GpuComputeStage.ComputeShader);
        // The INDIRECT ARGS need a PER-RESOURCE transition into the indirect-argument state — a global barrier does not
        // prepare a specific buffer for ExecuteIndirect on Direct3D 12 (on Vulkan this is a memory barrier all the same).
        recorder.TransitionBuffer(
            bufferHandle: m_viewsArgsBuffer.BufferHandle,
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuComputeAccess.IndirectCommandRead,
            destinationStageMask: GpuComputeStage.DrawIndirect,
            deviceHandle: m_deviceHandle,
            sourceAccessMask: GpuComputeAccess.ShaderWrite,
            sourceStageMask: GpuComputeStage.ComputeShader
        );

        // First frame: bring each per-view SDF source into the General (UAV) working layout Stage 1 writes it in.
        // After that they persist in General (written each frame, then read by Stage 2 — never sampled).
        if (!m_imageInitialized) {
            foreach (var source in m_sourceTextures) {
                recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderWrite, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: m_deviceHandle, imageHandle: source.ImageHandle, newLayout: GpuImageLayout.General, oldLayout: GpuImageLayout.Undefined, sourceAccessMask: GpuComputeAccess.None, sourceStageMask: GpuComputeStage.TopOfPipe);
            }
        }

        // Stage 1: render each viewport's SDF camera into its own source texture — dispatched INDIRECTLY from the
        // GPU-computed surviving-tile bbox; the all-empty margins are never dispatched.
        recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, pipelineHandle: m_viewsPipeline.Handle);
        recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: m_viewsSet, deviceHandle: m_deviceHandle, pipelineLayoutHandle: m_viewsPipeline.LayoutHandle);
        recorder.PushConstants(commandBufferHandle: commandBuffer, data: m_pushConstant, deviceHandle: m_deviceHandle, offset: 0, pipelineLayoutHandle: m_viewsPipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
        recorder.DispatchIndirect(argumentBufferHandle: m_viewsArgsBuffer.BufferHandle, argumentBufferOffset: 0, commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);

        // Make Stage 1's source writes visible to Stage 2's reads.
        recorder.MemoryBarrier(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderRead, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: m_deviceHandle, sourceAccessMask: GpuComputeAccess.ShaderWrite, sourceStageMask: GpuComputeStage.ComputeShader);
        recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderWrite, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: m_deviceHandle, imageHandle: m_storageImage.ImageHandle, newLayout: GpuImageLayout.General, oldLayout: outputOldLayout, sourceAccessMask: outputSourceAccess, sourceStageMask: outputSourceStage);

        // Stage 2: composite each source into its screen region (indirect, from the host-written constant grid).
        recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, pipelineHandle: m_compositePipeline.Handle);
        recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: m_compositeSet, deviceHandle: m_deviceHandle, pipelineLayoutHandle: m_compositePipeline.LayoutHandle);
        recorder.PushConstants(commandBufferHandle: commandBuffer, data: m_compositePush, deviceHandle: m_deviceHandle, offset: 0, pipelineLayoutHandle: m_compositePipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
        recorder.DispatchIndirect(argumentBufferHandle: m_compositeArgsBuffer.BufferHandle, argumentBufferOffset: 0, commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);

        // Hand the output off readback-readable (the shader-readable resting layout the next frame reopens from).
        recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderRead, destinationStageMask: GpuComputeStage.FragmentShader, deviceHandle: m_deviceHandle, imageHandle: m_storageImage.ImageHandle, newLayout: GpuImageLayout.ShaderReadOnly, oldLayout: GpuImageLayout.General, sourceAccessMask: GpuComputeAccess.ShaderWrite, sourceStageMask: GpuComputeStage.ComputeShader);
        recorder.EndCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);

        m_imageInitialized = true;
    }

    private void WriteStorageBuffer(nint set, uint binding, IGpuStorageBuffer buffer) {
        m_descriptorAllocator.WriteStorageBuffer(binding: binding, bufferHandle: buffer.BufferHandle, bufferSize: buffer.SizeBytes, descriptorSetHandle: set, deviceHandle: m_deviceHandle);
    }
    // For 4-byte-element read-only structured buffers (the float cull buffer, the uint cull-bounds) — NOT the 16-byte
    // (uint4) program-word stride WriteStorageBuffer assumes; a stride-16 SRV over the 8-byte bounds buffer is a
    // zero-element view the indirect views dispatch page-faults reading on Direct3D 12.
    private void WriteStorageBufferReadOnly(nint set, uint binding, IGpuStorageBuffer buffer) {
        m_descriptorAllocator.WriteStorageBufferReadOnly(binding: binding, bufferHandle: buffer.BufferHandle, bufferSize: buffer.SizeBytes, descriptorSetHandle: set, deviceHandle: m_deviceHandle);
    }
    private void WriteStorageBufferReadWrite(nint set, uint binding, IGpuStorageBuffer buffer) {
        m_descriptorAllocator.WriteStorageBufferReadWrite(binding: binding, bufferHandle: buffer.BufferHandle, bufferSize: buffer.SizeBytes, descriptorSetHandle: set, deviceHandle: m_deviceHandle);
    }
}
