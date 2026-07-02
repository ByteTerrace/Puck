using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SdfVm;

namespace Puck.Demo;

/// <summary>
/// A generic multi-viewport SDF world compositor driven by COMPUTE, fully BACKEND-NEUTRAL (it depends only on the
/// neutral <c>IGpuCompute*</c> seam, so the identical node runs on whichever backend the host publishes). It is a
/// host-model <see cref="IRenderNode"/>: it resolves the shared device from <see cref="FrameContext.Host"/> and
/// pulls each frame's scene + cameras + regions from an <see cref="ISdfFrameSource"/>.
/// <para>
/// Rendering is TWO-STAGE so the compositor is SOURCE-AGNOSTIC. <c>sdf-beam.comp</c> cone-marches the field per tile
/// to a conservative march-start depth; <c>sdf-world-views.comp</c> (Stage 1) renders each viewport's SDF camera
/// into its own rect-sized <em>source</em> texture; <c>sdf-world-composite.comp</c> (Stage 2) places each source —
/// an SDF view, or (later) a child node's output bound into the same slot — into its screen region by a 1:1 copy.
/// The viewport count follows <see cref="SdfFrame.Views"/>; nothing about the scene, cameras, or layout is baked in.
/// </para>
/// </summary>
internal sealed class WorldProducerNode : IRenderNode {
    private const uint CompositeOutputBindingIndex = 0; // sdf-world-composite.comp: Output at binding 0
    private const int CompositePushByteLength = (16 + ((sizeof(float) * 4) * MaxViewports)); // CompositeParams2: uint2 extent + uint count + uint pad + float4 rects[4]
    private const uint CompositeSourceBindingIndex = 1; // sdf-world-composite.comp: sources[] at binding 1
    private const GpuPixelFormat Format = GpuPixelFormat.R8G8B8A8Unorm;
    private const int MaxViewports = 4; // the source array length in the kernels (sources[4])
    private const uint ProgramBindingIndex = 1; // matches sdf-vm.hlsli's [[vk::binding(1, 0)]] / register(t0)
    private const int PushConstantByteLength = ((sizeof(uint) * 4) * 2); // 32-byte CompositeParams (16-byte rounded)
    private const uint TileBindingIndex = 3; // matches sdf-world.hlsli's [[vk::binding(3, 0)]]
    private const uint TileSize = 16; // KEEP IN SYNC with WorldTileSize in sdf-world.hlsli
    private const uint ViewSourceBindingIndex = 4; // sdf-world-views.comp: sources[] LAST (after the fixed 1/2/3)
    private const uint CullArgsBindingIndex = 5; // sdf-cull-args.comp: views indirect dispatch args (register u0)
    private const uint CullBoundsBindingIndex = 6; // sdf-cull-args.comp: bbox group origin (register u1); read by sdf-world-views.comp at binding 7
    private const uint ViewsCullBoundsBindingIndex = 8; // sdf-world-views.comp: the bbox origin (register t3); slot 8 is PAST the source array at 4..7
    private const uint DynamicTransformBindingIndex = 9; // sdf-vm.hlsli's [[vk::binding(9, 0)]] / register(t2) (world path); listed BEFORE cullBounds so the SRV registers come out program t0, viewport t1, dynamicTransforms t2, cullBounds t3
    private const int DynamicTransformByteLength = ((sizeof(float) * 4) * 2); // 32-byte rigid transform: float4 position + float4 orientation quaternion (KEEP IN SYNC with sdf-vm.hlsli sdfDynamicTransforms)
    private const int ViewportByteLength = ((sizeof(float) * 4) * 5); // 80-byte ViewportData (KEEP IN SYNC with sdf-world.hlsli)
    private const uint ViewportBindingIndex = 2; // matches sdf-world.hlsli's [[vk::binding(2, 0)]]
    private const uint WorkgroupEdge = 8;
    private const uint TimingCapacity = 8; // timestamp slots per pool (headroom over the marks)
    private const uint TimingMarkCount = 4; // frameStart + beam-close + views-close + composite-close
    private const ulong TimingReportInterval = 60; // print the digest roughly once per second at 60 fps

    private static readonly IReadOnlyDictionary<int, IRenderNode> EmptyChildren = new Dictionary<int, IRenderNode>();
    private readonly ReadOnlyMemory<byte> m_beamBytecode;
    private readonly nint[] m_boundSourceViews = new nint[MaxViewports];
    private readonly ReadOnlyMemory<byte> m_cullArgsBytecode;
    private readonly string? m_capturePath;
    private readonly IReadOnlyDictionary<int, IRenderNode> m_children;
    private readonly ReadOnlyMemory<byte> m_compositeBytecode;
    private readonly byte[] m_compositePush = new byte[CompositePushByteLength];
    private readonly Func<IGpuDeviceContext, IGpuStorageImage>? m_createStorageImage;
    private readonly NodeDescriptor m_descriptor = new(
        Name: "compute-sdf-world",
        SurfaceId: SurfaceId.New()
    );
    private readonly ISdfFrameSource m_frameSource;
    private readonly uint m_height;
    private readonly byte[] m_pushConstant = new byte[PushConstantByteLength];
    private readonly IServiceProvider m_serviceProvider;
    private readonly uint m_tileGridX;
    private readonly uint m_tileGridY;
    private readonly ReadOnlyMemory<byte> m_viewsBytecode;
    private readonly uint m_width;
    private IGpuComputePipeline? m_beamPipeline;
    private nint m_beamSet;
    private IGpuShaderModule? m_beamShaderModule;
    private bool m_captured;
    private byte[]? m_capturedPixels;
    private uint m_childMask;
    private Surface[] m_childSurfaces = [];
    private IGpuComputeCommandPool? m_commandPool;
    private IGpuStorageBuffer? m_compositeArgsBuffer;
    private IGpuComputePipeline? m_compositePipeline;
    private nint m_compositeSet;
    private IGpuShaderModule? m_compositeShaderModule;
    private IGpuComputeRecorder? m_computeRecorder;
    private IGpuStorageBuffer? m_cullBoundsBuffer;
    private IGpuComputePipeline? m_cullArgsPipeline;
    private nint m_cullArgsSet;
    private IGpuShaderModule? m_cullArgsShaderModule;
    private IGpuStorageBuffer? m_viewsArgsBuffer;
    private IGpuComputeServices? m_gpu;
    private IGpuDescriptorAllocator? m_descriptorAllocator;
    private IGpuDeviceContext? m_deviceContext;
    private nint m_deviceHandle;
    private bool m_disposed;
    private IGpuExportableStorageImage? m_exportableImage;
    private bool m_exportMode;
    private bool m_imageInitialized;
    private uint m_maxRectHeight;
    private uint m_maxRectWidth;
    private nint m_pool;
    private IGpuStorageBuffer? m_programBuffer;
    private IGpuQueueSubmitter? m_queueSubmitter;
    private IGpuSurfaceReadback? m_readback;
    private bool m_resourcesReady;
    private bool m_programUploadPending;
    private int m_produceFrameIndex;

    private static int CaptureDelayFrames() {
        return (int.TryParse(Environment.GetEnvironmentVariable(variable: "PUCK_CAPTURE_FRAME"), out var frame) && (frame > 0)) ? frame : 0;
    }
    private IGpuStorageImage?[] m_sourceTextures = [];
    private IGpuStorageImage? m_storageImage;
    private GpuTimestampCapabilities m_timingCapabilities;
    private bool m_timingEnabled;
    private IGpuTimingPoolFactory? m_timingFactory;
    private ulong m_timingFrame;
    private IGpuTimingPool[]? m_timingPools;
    private IGpuTimingRecorder? m_timingRecorder;
    private IGpuStorageBuffer? m_tileBuffer;
    private IGpuComputePipeline? m_viewsPipeline;
    private nint m_viewsSet;
    private IGpuShaderModule? m_viewsShaderModule;
    private uint m_viewportCount;
    private IGpuStorageBuffer? m_viewportBuffer;
    private byte[] m_viewportScratch = [];
    private IGpuStorageBuffer? m_dynamicTransformBuffer;
    private byte[] m_dynamicTransformScratch = [];

    /// <summary>Initializes a new instance of the <see cref="WorldProducerNode"/> class.</summary>
    /// <param name="serviceProvider">The application service provider (resolves the neutral GPU compute factories; the device comes from the host context).</param>
    /// <param name="frameSource">The per-frame source of the scene, cameras, and viewport regions.</param>
    /// <param name="beamBytecode">The compiled tile-cull prepass kernel (SPIR-V for Vulkan, DXIL for Direct3D 12).</param>
    /// <param name="cullArgsBytecode">The compiled cull-args reduction kernel (computes the surviving-tile bbox into the views' indirect dispatch args) for the same backend.</param>
    /// <param name="viewsBytecode">The compiled Stage 1 per-view SDF kernel for the same backend.</param>
    /// <param name="compositeBytecode">The compiled Stage 2 source-agnostic compositor kernel for the same backend.</param>
    /// <param name="width">The render width in pixels.</param>
    /// <param name="height">The render height in pixels.</param>
    /// <param name="capturePath">An optional PNG path; when set, the first rendered frame is read back from the GPU and written there.</param>
    /// <param name="createStorageImage">An optional factory for the output image. When it returns an <see cref="IGpuExportableStorageImage"/>, the node runs in <em>export</em> mode: it ends each frame in the cross-backend handoff layout, drains the producer queue, and emits a shared-handle <see cref="Surface"/> (for zero-copy cross-backend present) instead of a same-device image-view one. When <see langword="null"/>, a plain same-device storage image is created from the resolved <see cref="IGpuStorageImageFactory"/>.</param>
    /// <param name="children">An optional map from viewport slot to a child <see cref="IRenderNode"/> that supplies that slot's surface instead of an SDF camera. Each child is produced every frame at its slot's pixel rect, its same-device storage image is bound straight into the source-agnostic compositor's <c>sources[]</c> slot, and the SDF render skips that slot. The child must produce a <em>compute source</em> (a same-device storage image left in the general layout) — e.g. a <see cref="ChildSurfaceNode"/>.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A dimension is zero.</exception>
    public WorldProducerNode(IServiceProvider serviceProvider, ISdfFrameSource frameSource, ReadOnlyMemory<byte> beamBytecode, ReadOnlyMemory<byte> cullArgsBytecode, ReadOnlyMemory<byte> viewsBytecode, ReadOnlyMemory<byte> compositeBytecode, uint width, uint height, string? capturePath = null, Func<IGpuDeviceContext, IGpuStorageImage>? createStorageImage = null, IReadOnlyDictionary<int, IRenderNode>? children = null) {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(frameSource);

        if (
            (0 == width) ||
            (0 == height)
        ) {
            throw new ArgumentException(message: "World producer dimensions must be non-zero.");
        }

        m_beamBytecode = beamBytecode;
        m_capturePath = capturePath;
        m_children = (children ?? EmptyChildren);
        m_compositeBytecode = compositeBytecode;
        m_cullArgsBytecode = cullArgsBytecode;
        m_createStorageImage = createStorageImage;
        m_frameSource = frameSource;
        m_height = height;
        m_serviceProvider = serviceProvider;
        m_tileGridX = ((width + (TileSize - 1)) / TileSize);
        m_tileGridY = ((height + (TileSize - 1)) / TileSize);
        m_viewsBytecode = viewsBytecode;
        m_width = width;
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;

    /// <summary>The RGBA pixels read back the first time this node captured (its <c>capturePath</c> was set);
    /// empty until then. Lets a parity gate diff two backends' renders without re-reading the GPU.</summary>
    internal ReadOnlyMemory<byte> CapturedPixels => m_capturedPixels;

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (m_disposed) {
            return default;
        }

        // The shared device is an inherited host capability (every node in the tree composites on one device).
        if (!context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var gpuDevice)) {
            return default;
        }

        var frame = m_frameSource.CaptureFrame(width: m_width, height: m_height, deltaSeconds: (float)context.DeltaSeconds, interpolationAlpha: (float)context.InterpolationAlpha);

        // Produce each child viewport's surface first (so its image-view is known before the source array is bound),
        // then build/refresh resources, then (re)bind the source array — SDF view textures and child surfaces alike.
        ProduceChildren(context: in context, frame: frame);
        EnsureResources(gpuDevice: gpuDevice, frame: frame);
        BindSources();

        if (frame.ProgramChanged || m_programUploadPending) {
            m_programBuffer!.Write<uint>(data: frame.Program.Words);
            m_programUploadPending = false;
        }

        PackViewports(frame: frame);
        m_viewportBuffer!.Write<byte>(data: m_viewportScratch);
        PackDynamicTransforms(frame: frame);
        m_dynamicTransformBuffer!.Write<byte>(data: m_dynamicTransformScratch);
        // Rebuild the Stage-2 composite rects from the LIVE regions every frame — the camera director animates the
        // split layout, so a frozen first-frame layout composited stale/blank rects mid-transition.
        BuildCompositePush(frame: frame);
        Render();

        // PUCK_CAPTURE_FRAME=N delays the one-shot --capture to the Nth produced frame (default 0 = first), so a capture
        // can grab a post-transition frame (e.g. an animated split-screen settled) instead of frame 1. Diagnostic aid.
        ++m_produceFrameIndex;

        if (
            (m_capturePath is not null) &&
            !m_captured &&
            (m_produceFrameIndex > CaptureDelayFrames())
        ) {
            // Retain a copy of the readback (the readback buffer is reused across calls) so a parity gate can diff
            // two backends' output without a second GPU read.
            m_capturedPixels = ReadPixels().ToArray();
            PngImage.Write(
                height: (int)m_height,
                path: m_capturePath,
                rgba: m_capturedPixels,
                width: (int)m_width
            );
            m_captured = true;
        }

        // Export mode hands the host a shared NT handle (zero-copy cross-backend present); same-device mode hands it
        // an image view to sample directly.
        return m_exportMode
            ? new Surface(
                ImageViewHandle: 0,
                Width: m_width,
                Height: m_height,
                Format: SurfaceFormat.R8G8B8A8Unorm,
                SharedHandle: m_exportableImage!.SharedHandle
            )
            : new Surface(
                ImageViewHandle: m_storageImage!.ImageViewHandle,
                Width: m_width,
                Height: m_height,
                Format: SurfaceFormat.R8G8B8A8Unorm
            );
    }

    // Render each hosted child viewport's surface at its slot's pixel rect. Children resolve the same shared device
    // from the forwarded host context; the parent passes each the slot's pixel extent (matching the SDF source
    // sizing) so Stage 2's 1:1 copy lands in bounds. Their submits are enqueued ahead of the compositor's.
    private void ProduceChildren(in FrameContext context, SdfFrame frame) {
        if (m_children.Count == 0) {
            return;
        }

        // Sized once to the first frame's view count (the layout is stable for the run, like m_sourceTextures); never
        // resized, so a frozen child slot index can never fall outside it.
        if (m_childSurfaces.Length == 0) {
            m_childSurfaces = new Surface[frame.Views.Count];
        }

        foreach (var (slot, child) in m_children) {
            if (
                (slot < 0) ||
                (slot >= frame.Views.Count)
            ) {
                continue;
            }

            var region = frame.Views[slot].Region;

            m_childSurfaces[slot] = child.ProduceFrame(context: context with {
                TargetHeight = Math.Max(1u, (uint)(region.Height * m_height)),
                TargetWidth = Math.Max(1u, (uint)(region.Width * m_width)),
            });
        }
    }

    // Bind (or rebind when a child's image-view changed) the source array in both the Stage 1 (views) and Stage 2
    // (composite) sets: an SDF source texture for a normal slot, the hosted child's storage image for a child slot.
    // Array elements past the live viewport count duplicate slot 0 (Vulkan requires every bound array element to be a
    // valid descriptor); the kernels never read them.
    private void BindSources() {
        var fillerView = SourceViewForSlot(slot: 0);

        for (var element = 0u; (element < MaxViewports); element++) {
            var view = (element < m_viewportCount) ? SourceViewForSlot(slot: (int)element) : fillerView;

            if (view == m_boundSourceViews[element]) {
                continue;
            }

            m_descriptorAllocator!.WriteStorageImage(arrayElement: element, binding: ViewSourceBindingIndex, descriptorSetHandle: m_viewsSet, deviceHandle: m_deviceHandle, imageViewHandle: view);
            m_descriptorAllocator!.WriteStorageImage(arrayElement: element, binding: CompositeSourceBindingIndex, descriptorSetHandle: m_compositeSet, deviceHandle: m_deviceHandle, imageViewHandle: view);
            m_boundSourceViews[element] = view;
        }
    }
    private nint SourceViewForSlot(int slot) {
        if (IsChildSlot(slot: slot)) {
            var view = m_childSurfaces[slot].ImageViewHandle;

            if (0 == view) {
                throw new InvalidOperationException(message: $"The child node for viewport {slot} did not produce a same-device storage-image surface (an integer-copy child must hand back a general-layout storage image view).");
            }

            return view;
        }

        return m_sourceTextures[slot]!.ImageViewHandle;
    }
    private bool IsChildSlot(int slot) =>
        (0u != (m_childMask & (1u << slot)));

    // Pack each frame's views (camera snapshot + region) into the 80-byte ViewportData rows the kernels read —
    // member-for-member from SdfFrame, no camera math (the snapshot already holds the basis + tan(fov/2) + aspect).
    private void PackViewports(SdfFrame frame) {
        var floats = MemoryMarshal.Cast<byte, float>(span: m_viewportScratch.AsSpan());

        for (var index = 0; (index < (int)m_viewportCount); index++) {
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

    // Pack each moving entity's rigid transform into the dynamic-transform scratch — 2 float4 per slot: position.xyz
    // (+ pad) then the orientation quaternion (xyzw) — for upload into the buffer SDF_OP_TRANSFORM_DYNAMIC indexes by
    // slot. An empty list still writes the one always-present slot as identity (a static scene never reads it). Clamped
    // to the buffer's slot capacity, which the count grew the buffer to at build time.
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
    private void EnsureResources(IGpuDeviceContext gpuDevice, SdfFrame frame) {
        if (m_resourcesReady) {
            return;
        }

        // One cohesive compute-services bundle instead of resolving each granular factory; the granular interfaces
        // are still registered for a node that needs only one of them.
        m_gpu ??= (IGpuComputeServices)m_serviceProvider.GetService(serviceType: typeof(IGpuComputeServices))!;

        m_deviceContext = gpuDevice;
        m_deviceHandle = gpuDevice.DeviceHandle;
        m_computeRecorder = m_gpu.ComputeRecorder;
        m_descriptorAllocator = m_gpu.DescriptorAllocator;
        m_queueSubmitter = m_gpu.QueueSubmitter;

        var computePipelineFactory = m_gpu.ComputePipelineFactory;
        var storageImageFactory = m_gpu.StorageImageFactory;
        var storageBufferFactory = m_gpu.StorageBufferFactory;
        var shaderModuleFactory = m_gpu.ShaderModuleFactory;
        var commandPoolFactory = m_gpu.CommandPoolFactory;

        m_viewportCount = (uint)frame.Views.Count;

        if (m_viewportCount > MaxViewports) {
            throw new ArgumentException(message: $"The world compositor supports at most {MaxViewports} viewports; the frame has {m_viewportCount}.");
        }

        m_viewportScratch = new byte[(int)m_viewportCount * ViewportByteLength];

        // The dynamic entity transform buffer: one rigid transform per moving entity, ≥1 slot so the binding is always
        // valid (a static scene gets a single identity slot its program never references). Sized at first build;
        // growing it (more entities) is a join/leave-time recreate, never a per-frame cost.
        m_dynamicTransformScratch = new byte[Math.Max(1, frame.DynamicTransforms.Count) * DynamicTransformByteLength];

        // Mark which live viewport slots a hosted child backs (the beam prepass and Stage 1 skip these); the source
        // for such a slot is the child's surface, bound in BindSources, not an SDF render.
        m_childMask = 0;

        foreach (var slot in m_children.Keys) {
            if (
                (slot >= 0) &&
                (slot < (int)m_viewportCount)
            ) {
                m_childMask |= (1u << slot);
            }
        }

        // CompositeParams { uint2 imageExtent; uint2 tileGrid; uint viewportCount; uint childMask; } — Stage 0/1 push, constant.
        var pushWords = MemoryMarshal.Cast<byte, uint>(span: m_pushConstant.AsSpan());

        pushWords[0] = m_width; pushWords[1] = m_height; pushWords[2] = m_tileGridX; pushWords[3] = m_tileGridY; pushWords[4] = m_viewportCount; pushWords[5] = m_childMask;

        // NOTE: the Stage-2 composite rects (BuildCompositePush) are rebuilt EVERY frame in ProduceFrame, not here — the
        // camera director ANIMATES the per-view regions, so freezing them to this first frame blanked panes mid-transition.

        m_beamShaderModule = shaderModuleFactory.Create(deviceContext: gpuDevice, stage: GpuShaderStage.Compute, bytecode: m_beamBytecode);
        m_cullArgsShaderModule = shaderModuleFactory.Create(deviceContext: gpuDevice, stage: GpuShaderStage.Compute, bytecode: m_cullArgsBytecode);
        m_viewsShaderModule = shaderModuleFactory.Create(deviceContext: gpuDevice, stage: GpuShaderStage.Compute, bytecode: m_viewsBytecode);
        m_compositeShaderModule = shaderModuleFactory.Create(deviceContext: gpuDevice, stage: GpuShaderStage.Compute, bytecode: m_compositeBytecode);

        // One FULL-SIZE source texture per NON-child viewport — Stage 1 renders the viewport's region-extent into it,
        // Stage 2 copies that into the screen region. Sized to the FULL frame extent (the largest any region can reach),
        // NOT this first frame's region: the camera director animates regions every frame, so a frozen region-sized
        // texture (e.g. 1x1 from a zero-area first frame, or a half-width split) under-allocated the pane and blanked it
        // when the layout grew. Writes/reads stay within the live region (≤ full), so full-size is always in-bounds.
        // Child slots stay null: their source is the hosted child's storage image (bound in BindSources), and the child
        // owns that image's layout, so the parent never creates or transitions one.
        m_sourceTextures = new IGpuStorageImage?[(int)m_viewportCount];
        m_maxRectWidth = m_width;
        m_maxRectHeight = m_height;

        for (var index = 0; (index < (int)m_viewportCount); index++) {
            if (IsChildSlot(slot: index)) {
                continue;
            }

            m_sourceTextures[index] = storageImageFactory.Create(deviceContext: gpuDevice, format: Format, height: m_height, width: m_width);
        }

        // The output image is either a plain same-device storage image (resolved from the neutral factory) or an
        // exportable one supplied by the host (cross-backend present). Only the FINAL output crosses the seam; the
        // per-view sources are always internal.
        m_storageImage = (m_createStorageImage is null)
            ? storageImageFactory.Create(deviceContext: gpuDevice, format: Format, height: m_height, width: m_width)
            : m_createStorageImage(gpuDevice);
        m_exportableImage = m_storageImage as IGpuExportableStorageImage;
        m_exportMode = (m_exportableImage is not null);

        m_programBuffer = storageBufferFactory.Create(deviceContext: gpuDevice, sizeBytes: ((ulong)frame.Program.Words.Length * sizeof(uint)));
        // The program is normally uploaded only when frame.ProgramChanged (it is static after the first frame). A freshly
        // (re)created buffer — first build OR a device-loss rebuild — is empty, so force the next ProduceFrame to upload
        // it even when ProgramChanged is false; otherwise a recovered device would render an empty scene (clear color).
        m_programUploadPending = true;
        m_viewportBuffer = storageBufferFactory.Create(deviceContext: gpuDevice, sizeBytes: (ulong)m_viewportScratch.Length);
        m_dynamicTransformBuffer = storageBufferFactory.Create(deviceContext: gpuDevice, sizeBytes: (ulong)m_dynamicTransformScratch.Length);
        // The cull buffer is GPU-written by the beam prepass (a UAV), so it is device-local (a Direct3D 12 default heap).
        m_tileBuffer = storageBufferFactory.CreateDeviceLocal(deviceContext: gpuDevice, sizeBytes: ((ulong)m_viewportCount * m_tileGridX * m_tileGridY * sizeof(float)));

        // GPU-driven cull: the cull-args pass (below) reduces the cull buffer to the Stage-1 INDIRECT dispatch args
        // (the surviving-tile bbox, 3 group counts) and the bbox group origin (2 uints). Both are device-local — the
        // GPU writes them as UAVs, then a barrier orders the indirect read; the views dispatch reads the args (the
        // dispatch grid) and the bounds (its pixel offset). The all-empty margins are never dispatched.
        m_viewsArgsBuffer = storageBufferFactory.CreateIndirectArgs(deviceContext: gpuDevice, sizeBytes: (sizeof(uint) * 3), deviceLocal: true);
        m_cullBoundsBuffer = storageBufferFactory.CreateDeviceLocal(deviceContext: gpuDevice, sizeBytes: (sizeof(uint) * 2));

        // Stage 2's full-frame composite grid is constant for the run, so its dispatch is driven INDIRECTLY: the GPU
        // reads the (x, y, z) group counts from this host-written args buffer (vkCmdDispatchIndirect / ExecuteIndirect)
        // instead of the CPU supplying them. The counts equal the equivalent direct dispatch, so it is pixel-neutral
        // (the `world` parity gate is the guard) — it exercises the indirect-dispatch seam in the live render path and
        // is the seam a future GPU-driven cull (the beam prepass writing these counts) would build on. Host-written
        // once + host-coherent, so the queue-submit host-write visibility covers it with no indirect-read barrier.
        m_compositeArgsBuffer = storageBufferFactory.CreateIndirectArgs(deviceContext: gpuDevice, sizeBytes: (sizeof(uint) * 3));
        m_compositeArgsBuffer.Write<uint>(data: [
            ((m_width + (WorkgroupEdge - 1)) / WorkgroupEdge),
            ((m_height + (WorkgroupEdge - 1)) / WorkgroupEdge),
            1u,
        ]);

        var pushConstantBinding = new GpuPushConstantBinding(data: m_pushConstant, offset: 0, stageFlags: GpuShaderStage.Compute);
        var compositePushBinding = new GpuPushConstantBinding(data: m_compositePush, offset: 0, stageFlags: GpuShaderStage.Compute);

        // Beam prepass: program (1) + viewports (2) + dynamic entity transforms (9) + cull buffer written (3). No output
        // image. The cone march runs map(), so it reads the dynamic transforms — listed after viewport so its SRV is t2.
        GpuComputeBinding[] beamBindings = [
            new GpuComputeBinding(Binding: ProgramBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            new GpuComputeBinding(Binding: ViewportBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            new GpuComputeBinding(Binding: DynamicTransformBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            new GpuComputeBinding(Binding: TileBindingIndex, Kind: GpuComputeBindingKind.StorageBufferReadWrite),
        ];

        m_beamPipeline = computePipelineFactory.Create(
            bindings: beamBindings,
            computeShaderModule: m_beamShaderModule,
            deviceContext: gpuDevice,
            pushConstantBinding: pushConstantBinding
        );

        // Cull-args reduction: cull buffer read (3) + the views indirect args written (5) + the bbox origin written (6).
        GpuComputeBinding[] cullArgsBindings = [
            new GpuComputeBinding(Binding: TileBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            new GpuComputeBinding(Binding: CullArgsBindingIndex, Kind: GpuComputeBindingKind.StorageBufferReadWrite),
            new GpuComputeBinding(Binding: CullBoundsBindingIndex, Kind: GpuComputeBindingKind.StorageBufferReadWrite),
        ];

        m_cullArgsPipeline = computePipelineFactory.Create(
            bindings: cullArgsBindings,
            computeShaderModule: m_cullArgsShaderModule,
            deviceContext: gpuDevice,
            pushConstantBinding: pushConstantBinding
        );

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

        m_viewsPipeline = computePipelineFactory.Create(
            bindings: viewsBindings,
            computeShaderModule: m_viewsShaderModule,
            deviceContext: gpuDevice,
            pushConstantBinding: pushConstantBinding
        );

        // Stage 2 (source-agnostic composite): output image (0) + the source array (1) + the cull buffer read (3),
        // which the compositor uses to flatten every empty (culled) tile to a constant.
        GpuComputeBinding[] compositeBindings = [
            new GpuComputeBinding(Binding: CompositeOutputBindingIndex, Kind: GpuComputeBindingKind.StorageImage),
            new GpuComputeBinding(Binding: CompositeSourceBindingIndex, Kind: GpuComputeBindingKind.StorageImage, Count: MaxViewports),
            new GpuComputeBinding(Binding: TileBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
        ];

        m_compositePipeline = computePipelineFactory.Create(
            bindings: compositeBindings,
            computeShaderModule: m_compositeShaderModule,
            deviceContext: gpuDevice,
            pushConstantBinding: compositePushBinding
        );

        // One pool, FOUR independent sets — the Direct3D 12 allocator bump-allocates a non-overlapping heap region per
        // set (like a Vulkan pool), so they never clobber. The capacity is DERIVED from the four sets' binding lists
        // (an array binding contributes its full Count), so it can never drift out of sync when a binding is added or
        // MaxViewports changes — replacing the hand-tallied count that previously had to be kept in step by comment.
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

        // The source array (binding the SDF view textures and any hosted child surfaces) is bound per frame by
        // BindSources, after the children have produced — their image-views aren't known until then.
        m_commandPool = commandPoolFactory.Create(deviceContext: gpuDevice);
        EnsureTiming(gpuDevice: gpuDevice);
        m_resourcesReady = true;
    }

    // GPU performance counters: opt-in via PUCK_TIMING=1, gated on the device reporting usable timestamps and on the
    // backend having registered the timing seam (resolved granularly — timing is not part of the always-on bundle).
    // Two pools are created so the previous frame's results can be read with no device-idle stall (double-buffering).
    private void EnsureTiming(IGpuDeviceContext gpuDevice) {
        if (!string.Equals(Environment.GetEnvironmentVariable(variable: "PUCK_TIMING"), "1", StringComparison.Ordinal)) {
            return;
        }

        m_timingFactory = m_serviceProvider.GetService(serviceType: typeof(IGpuTimingPoolFactory)) as IGpuTimingPoolFactory;
        m_timingRecorder = m_serviceProvider.GetService(serviceType: typeof(IGpuTimingRecorder)) as IGpuTimingRecorder;

        if (
            (m_timingFactory is null) ||
            (m_timingRecorder is null)
        ) {
            Console.Error.WriteLine(value: "[world-timing] the GPU timing seam is not registered on this backend; running untimed.");

            return;
        }

        m_timingCapabilities = m_timingFactory.GetCapabilities(deviceContext: gpuDevice);

        if (!m_timingCapabilities.IsSupported) {
            Console.Error.WriteLine(value: "[world-timing] the device reports no usable GPU timestamps; running untimed.");

            return;
        }

        m_timingPools = [
            m_timingFactory.CreateTimestampPool(deviceContext: gpuDevice, queryCapacity: TimingCapacity),
            m_timingFactory.CreateTimestampPool(deviceContext: gpuDevice, queryCapacity: TimingCapacity),
        ];
        m_timingEnabled = true;

        Console.Error.WriteLine(value: $"[world-timing] enabled | period {m_timingCapabilities.PeriodNanoseconds:0.###}ns | validBits {m_timingCapabilities.ValidBits}");
    }

    // Stage 2's CompositeParams2 { uint2 imageExtent; uint viewportCount; uint tileGridPacked; float4 rects[4]; }: the
    // regions drive the layout. word[3] packs the tile grid ((y << 16) | x) so the compositor can flatten empty tiles.
    // Sized once (the source textures match these regions for the run).
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
    private void Render() {
        var recorder = m_computeRecorder!;
        var commandBuffer = m_commandPool!.CommandBufferHandle;
        // After the first frame the OUTPUT rests in its handoff layout: shader-readable when a same-device compositor
        // sampled it, or the cross-backend External layout when it was exported. The first frame starts undefined.
        var restingLayout = m_exportMode ? GpuImageLayout.External : GpuImageLayout.ShaderReadOnly;
        var restingStage = m_exportMode ? GpuComputeStage.ComputeShader : GpuComputeStage.FragmentShader;
        var outputOldLayout = m_imageInitialized ? restingLayout : GpuImageLayout.Undefined;
        var outputSourceAccess = m_imageInitialized ? GpuComputeAccess.ShaderRead : GpuComputeAccess.None;
        var outputSourceStage = m_imageInitialized ? restingStage : GpuComputeStage.TopOfPipe;

        recorder.BeginCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);

        // GPU timing: this frame's double-buffered pool. Reset it, then mark frame-start (top of pipe). The marks are
        // pixel-neutral, so the determinism/capture-hash parity gate is unaffected.
        var timingPool = (m_timingEnabled ? m_timingPools![(int)(m_timingFrame % 2UL)].PoolHandle : 0);

        if (0 != timingPool) {
            m_timingRecorder!.ResetTimestamps(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, firstQuery: 0, poolHandle: timingPool, queryCount: TimingCapacity);
            m_timingRecorder.WriteTimestamp(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, poolHandle: timingPool, queryIndex: 0, stageFlags: GpuTimingStage.TopOfPipe);
        }

        // Tile-cull prepass: one invocation per (tile, viewport).
        recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, pipelineHandle: m_beamPipeline!.Handle);
        recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: m_beamSet, deviceHandle: m_deviceHandle, pipelineLayoutHandle: m_beamPipeline.LayoutHandle);
        recorder.PushConstants(commandBufferHandle: commandBuffer, data: m_pushConstant, deviceHandle: m_deviceHandle, offset: 0, pipelineLayoutHandle: m_beamPipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
        recorder.Dispatch(
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle,
            groupCountX: ((m_tileGridX + (WorkgroupEdge - 1)) / WorkgroupEdge),
            groupCountY: ((m_tileGridY + (WorkgroupEdge - 1)) / WorkgroupEdge),
            groupCountZ: m_viewportCount
        );

        WriteTimingMark(commandBuffer: commandBuffer, queryIndex: 1, timingPool: timingPool); // close: beam prepass

        // Make the prepass's tile writes visible to the cull-args reduction's (and Stage 1's) reads.
        recorder.MemoryBarrier(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuComputeAccess.ShaderRead,
            destinationStageMask: GpuComputeStage.ComputeShader,
            deviceHandle: m_deviceHandle,
            sourceAccessMask: GpuComputeAccess.ShaderWrite,
            sourceStageMask: GpuComputeStage.ComputeShader
        );

        // Cull-args reduction (a single invocation): reduce the cull buffer to the surviving-tile bbox, writing Stage
        // 1's INDIRECT dispatch group counts + the bbox group origin — so the GPU, not the CPU, sizes the views grid.
        recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, pipelineHandle: m_cullArgsPipeline!.Handle);
        recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: m_cullArgsSet, deviceHandle: m_deviceHandle, pipelineLayoutHandle: m_cullArgsPipeline.LayoutHandle);
        recorder.PushConstants(commandBufferHandle: commandBuffer, data: m_pushConstant, deviceHandle: m_deviceHandle, offset: 0, pipelineLayoutHandle: m_cullArgsPipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
        recorder.Dispatch(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, groupCountX: 1, groupCountY: 1, groupCountZ: 1);

        // Order the cull-args writes before Stage 1. The bbox ORIGIN (cullBounds) is an ordinary compute-shader read,
        // so a global memory barrier suffices.
        recorder.MemoryBarrier(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuComputeAccess.ShaderRead,
            destinationStageMask: GpuComputeStage.ComputeShader,
            deviceHandle: m_deviceHandle,
            sourceAccessMask: GpuComputeAccess.ShaderWrite,
            sourceStageMask: GpuComputeStage.ComputeShader
        );
        // The INDIRECT ARGS need a PER-RESOURCE transition into the indirect-argument state — a global barrier does not
        // prepare a specific buffer for ExecuteIndirect on Direct3D 12 (on Vulkan this is a memory barrier all the same).
        recorder.TransitionBuffer(
            bufferHandle: m_viewsArgsBuffer!.BufferHandle,
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuComputeAccess.IndirectCommandRead,
            destinationStageMask: GpuComputeStage.DrawIndirect,
            deviceHandle: m_deviceHandle,
            sourceAccessMask: GpuComputeAccess.ShaderWrite,
            sourceStageMask: GpuComputeStage.ComputeShader
        );

        // First frame: bring each per-view SDF source into the General (UAV) working layout Stage 1 writes it in.
        // After that they persist in General (written each frame, then read by Stage 2 — never sampled as
        // shader-readable). Child slots are null here — the child owns its image and already left it in General.
        if (!m_imageInitialized) {
            foreach (var source in m_sourceTextures) {
                if (source is null) {
                    continue;
                }

                recorder.TransitionImageLayout(
                    commandBufferHandle: commandBuffer,
                    destinationAccessMask: GpuComputeAccess.ShaderWrite,
                    destinationStageMask: GpuComputeStage.ComputeShader,
                    deviceHandle: m_deviceHandle,
                    imageHandle: source.ImageHandle,
                    newLayout: GpuImageLayout.General,
                    oldLayout: GpuImageLayout.Undefined,
                    sourceAccessMask: GpuComputeAccess.None,
                    sourceStageMask: GpuComputeStage.TopOfPipe
                );
            }
        }

        // Stage 1: render each viewport's SDF camera into its own source texture (one invocation per source pixel).
        recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, pipelineHandle: m_viewsPipeline!.Handle);
        recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: m_viewsSet, deviceHandle: m_deviceHandle, pipelineLayoutHandle: m_viewsPipeline.LayoutHandle);
        recorder.PushConstants(commandBufferHandle: commandBuffer, data: m_pushConstant, deviceHandle: m_deviceHandle, offset: 0, pipelineLayoutHandle: m_viewsPipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
        // INDIRECT: the GPU-computed surviving-tile bbox (from the cull-args pass) sizes this dispatch — the all-empty
        // margins are never dispatched; the kernel offsets each invocation by the bbox origin (binding 7).
        recorder.DispatchIndirect(
            argumentBufferHandle: m_viewsArgsBuffer!.BufferHandle,
            argumentBufferOffset: 0,
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle
        );

        WriteTimingMark(commandBuffer: commandBuffer, queryIndex: 2, timingPool: timingPool); // close: cull-args + Stage 1 views

        // Make Stage 1's source writes visible to Stage 2's reads.
        recorder.MemoryBarrier(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuComputeAccess.ShaderRead,
            destinationStageMask: GpuComputeStage.ComputeShader,
            deviceHandle: m_deviceHandle,
            sourceAccessMask: GpuComputeAccess.ShaderWrite,
            sourceStageMask: GpuComputeStage.ComputeShader
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

        // Stage 2: composite each source into its screen region (one invocation per output pixel).
        recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, pipelineHandle: m_compositePipeline!.Handle);
        recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: m_compositeSet, deviceHandle: m_deviceHandle, pipelineLayoutHandle: m_compositePipeline.LayoutHandle);
        recorder.PushConstants(commandBufferHandle: commandBuffer, data: m_compositePush, deviceHandle: m_deviceHandle, offset: 0, pipelineLayoutHandle: m_compositePipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
        recorder.DispatchIndirect(
            argumentBufferHandle: m_compositeArgsBuffer!.BufferHandle,
            argumentBufferOffset: 0,
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle
        );

        WriteTimingMark(commandBuffer: commandBuffer, queryIndex: 3, timingPool: timingPool); // close: Stage 2 composite

        // Hand the output off in its consumer layout: shader-readable for a same-device compositor, or the
        // cross-backend External handoff layout. Routing this through the recorder keeps its per-resource state
        // tracking the single source of truth.
        recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuComputeAccess.ShaderRead,
            destinationStageMask: m_exportMode ? GpuComputeStage.ComputeShader : GpuComputeStage.FragmentShader,
            deviceHandle: m_deviceHandle,
            imageHandle: m_storageImage.ImageHandle,
            newLayout: m_exportMode ? GpuImageLayout.External : GpuImageLayout.ShaderReadOnly,
            oldLayout: GpuImageLayout.General,
            sourceAccessMask: GpuComputeAccess.ShaderWrite,
            sourceStageMask: GpuComputeStage.ComputeShader
        );

        // Copy the marks into the pool's readback storage (a no-op on Vulkan; the D3D12 ResolveQueryData) so they
        // submit and drain atomically with the frame.
        if (0 != timingPool) {
            m_timingRecorder!.ResolveTimestamps(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, firstQuery: 0, poolHandle: timingPool, queryCount: TimingMarkCount);
        }

        recorder.EndCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);

        m_queueSubmitter!.Submit(commandBufferHandles: [commandBuffer], deviceContext: m_deviceContext!);

        // In export mode the consumer lives on another backend with no shared timeline, so block until this queue
        // has finished before the shared handle is handed off. In same-device mode this is a no-op and the node
        // instead relies on the host draining the device between frames (the launcher's BeginFrame WaitIdle) to
        // order this frame's source/output writes against the previous frame's reads and command-buffer reuse.
        m_exportableImage?.FinalizeForExport();

        m_imageInitialized = true;

        if (m_timingEnabled) {
            ReportTiming();
            m_timingFrame++;
        }
    }

    // Writes a bottom-of-pipe closing timestamp for a pass, when timing is on.
    private void WriteTimingMark(nint timingPool, nint commandBuffer, uint queryIndex) {
        if (0 != timingPool) {
            m_timingRecorder!.WriteTimestamp(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, poolHandle: timingPool, queryIndex: queryIndex, stageFlags: GpuTimingStage.BottomOfPipe);
        }
    }

    // Reads the PREVIOUS frame's marks (the launcher drains the device between frames, so they are complete with no
    // added stall) and prints a throttled per-pass digest: whole-frame GPU ms plus each pass's ms and share-of-frame.
    private void ReportTiming() {
        if (
            (m_timingFrame == 0UL) ||
            (0UL != (m_timingFrame % TimingReportInterval))
        ) {
            return;
        }

        var previousPool = m_timingPools![(int)((m_timingFrame + 1UL) % 2UL)];
        Span<ulong> ticks = stackalloc ulong[(int)TimingMarkCount];

        if (m_timingRecorder!.ReadTimestamps(deviceHandle: m_deviceHandle, firstQuery: 0, poolHandle: previousPool.PoolHandle, queryCount: TimingMarkCount, rawTicks: ticks) < TimingMarkCount) {
            return;
        }

        var capabilities = m_timingCapabilities;
        var beam = capabilities.TicksToMilliseconds(startTicks: ticks[0], endTicks: ticks[1]);
        var views = capabilities.TicksToMilliseconds(startTicks: ticks[1], endTicks: ticks[2]);
        var composite = capabilities.TicksToMilliseconds(startTicks: ticks[2], endTicks: ticks[3]);
        var frame = capabilities.TicksToMilliseconds(startTicks: ticks[0], endTicks: ticks[3]);

        if (frame <= 0.0) {
            return;
        }

        Console.Error.WriteLine(value: $"[world-timing] frame {frame:0.000}ms | beam {beam:0.000} ({Percent(part: beam, whole: frame)}%) views {views:0.000} ({Percent(part: views, whole: frame)}%) composite {composite:0.000} ({Percent(part: composite, whole: frame)}%)");
    }
    private static int Percent(double part, double whole) =>
        (int)Math.Round(a: ((100.0 * part) / whole));
    private void WriteStorageBuffer(nint set, uint binding, IGpuStorageBuffer buffer) {
        m_descriptorAllocator!.WriteStorageBuffer(
            binding: binding,
            bufferHandle: buffer.BufferHandle,
            bufferSize: buffer.SizeBytes,
            descriptorSetHandle: set,
            deviceHandle: m_deviceHandle
        );
    }
    // For 4-byte-element read-only structured buffers (the float cull buffer, the uint cull-bounds) — NOT the 16-byte
    // (uint4) program-word stride WriteStorageBuffer assumes; a stride-16 SRV over the 8-byte bounds buffer is a
    // zero-element view the indirect views dispatch page-faults reading on Direct3D 12.
    private void WriteStorageBufferReadOnly(nint set, uint binding, IGpuStorageBuffer buffer) {
        m_descriptorAllocator!.WriteStorageBufferReadOnly(
            binding: binding,
            bufferHandle: buffer.BufferHandle,
            bufferSize: buffer.SizeBytes,
            descriptorSetHandle: set,
            deviceHandle: m_deviceHandle
        );
    }
    private void WriteStorageBufferReadWrite(nint set, uint binding, IGpuStorageBuffer buffer) {
        m_descriptorAllocator!.WriteStorageBufferReadWrite(
            binding: binding,
            bufferHandle: buffer.BufferHandle,
            bufferSize: buffer.SizeBytes,
            descriptorSetHandle: set,
            deviceHandle: m_deviceHandle
        );
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
        // Drain before tearing down GPU resources: in same-device mode the per-frame submits are fire-and-forget
        // (nothing fences them), so a frame could still be in flight at teardown.
        m_deviceContext.TryWaitIdle();

        foreach (var child in m_children.Values) {
            child.Dispose();
        }

        ReleaseGpuResources();
    }

    /// <inheritdoc/>
    public void OnDeviceLost() {
        // Device-loss recovery: reset the subtree on the still-valid (lost) device, child-first (children are device
        // children too, and must be torn down before the device is). Unlike Dispose there is NO idle drain — the device
        // is lost, so nothing in flight will ever complete, and the host pump recreates the device immediately after.
        // The next ProduceFrame re-runs EnsureResources (the latch is cleared below) against the recreated device,
        // re-reading the device handle from the context, so every resource is rebuilt fresh.
        foreach (var child in m_children.Values) {
            child.OnDeviceLost();
        }

        ReleaseGpuResources();

        Array.Clear(array: m_boundSourceViews);
        m_deviceHandle = 0;
        m_imageInitialized = false;
        m_resourcesReady = false;
        // Re-arm the one-shot capture so a --capture run writes a POST-recovery frame (lets device-loss recovery be
        // visually verified from the readback; harmless when no capture path is set).
        m_captured = false;
    }

    // The device-resource teardown shared by Dispose and OnDeviceLost: the body of Dispose MINUS the idle drain and the
    // child handling (which differ — Dispose disposes children, OnDeviceLost resets them). Every field is nulled after
    // release so a rebuild starts clean and a repeat call is a harmless no-op. Wait-free, so it is safe to call against a
    // lost device. The descriptor pool is destroyed via the still-current m_deviceHandle BEFORE the caller clears it.
    private void ReleaseGpuResources() {
        if (m_timingPools is not null) {
            foreach (var pool in m_timingPools) {
                pool.Dispose();
            }

            m_timingPools = null;
        }

        m_readback?.Dispose();
        m_readback = null;
        m_commandPool?.Dispose();
        m_commandPool = null;
        m_tileBuffer?.Dispose();
        m_tileBuffer = null;
        m_compositeArgsBuffer?.Dispose();
        m_compositeArgsBuffer = null;
        m_viewsArgsBuffer?.Dispose();
        m_viewsArgsBuffer = null;
        m_cullBoundsBuffer?.Dispose();
        m_cullBoundsBuffer = null;
        m_viewportBuffer?.Dispose();
        m_viewportBuffer = null;
        m_dynamicTransformBuffer?.Dispose();
        m_dynamicTransformBuffer = null;
        m_programBuffer?.Dispose();
        m_programBuffer = null;
        m_beamPipeline?.Dispose();
        m_beamPipeline = null;
        m_cullArgsPipeline?.Dispose();
        m_cullArgsPipeline = null;
        m_viewsPipeline?.Dispose();
        m_viewsPipeline = null;
        m_compositePipeline?.Dispose();
        m_compositePipeline = null;

        if ((0 != m_pool) && (m_descriptorAllocator is not null)) {
            m_descriptorAllocator.DestroyPool(deviceHandle: m_deviceHandle, poolHandle: m_pool);
            m_pool = 0;
        }

        m_beamSet = 0;
        m_cullArgsSet = 0;
        m_viewsSet = 0;
        m_compositeSet = 0;

        foreach (var source in m_sourceTextures) {
            source?.Dispose();
        }

        m_sourceTextures = [];

        m_storageImage?.Dispose();
        m_storageImage = null;
        m_beamShaderModule?.Dispose();
        m_beamShaderModule = null;
        m_cullArgsShaderModule?.Dispose();
        m_cullArgsShaderModule = null;
        m_viewsShaderModule?.Dispose();
        m_viewsShaderModule = null;
        m_compositeShaderModule?.Dispose();
        m_compositeShaderModule = null;
    }
}
