using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

/// <summary>Construction options for <see cref="SdfWorldEngine"/>.</summary>
/// <param name="Program">The scene program; the GPU buffer is sized to it and it is uploaded ONCE at construction
/// (the "program uploaded once" seam the dynamic-transform channel rides). A host whose scene later changes calls
/// <see cref="SdfWorldEngine.UploadProgram"/> — the new program must fit the constructed buffer.</param>
/// <param name="ViewportCapacity">The number of viewport slots to provision (source textures + packed viewport rows).
/// Frames may carry fewer views than the capacity, never more; the kernels' source array caps it at 5.</param>
/// <param name="ChildMask">Bit <c>v</c> set means viewport <c>v</c> is backed by a hosted CHILD surface, not an SDF
/// camera: no source texture is allocated for it (the host binds the child's storage image each frame via
/// <see cref="SdfWorldEngine.SetChildSource"/>), and the beam prepass + Stage 1 skip the slot.</param>
/// <param name="DynamicTransformCapacity">The number of dynamic entity-transform slots to allocate (at least one slot
/// is always bound so the binding stays valid for a static scene). The engine automatically raises this floor to the
/// program's <see cref="SdfProgram.RequiredDynamicTransformCapacity"/>. A plain per-engine choice with no fixed ceiling —
/// hundreds of slots cost 32 bytes each and an O(capacity) per-frame upload; excess transforms in a frame beyond the
/// capacity are dropped.</param>
/// <param name="CreateOutputImage">An optional factory for the output image. When it returns an
/// <see cref="IGpuExportableStorageImage"/>, the engine runs in <em>export</em> mode: each submitted frame ends in the
/// cross-backend handoff layout and <see cref="SdfWorldEngine.SubmitFrame"/> drains the producer queue so the shared
/// handle may be consumed on another device. When <see langword="null"/>, a plain same-device storage image is
/// created from the resolved <see cref="IGpuStorageImageFactory"/>.</param>
/// <param name="TimingFactory">An optional GPU timing pool factory; with <paramref name="TimingRecorder"/>, enables
/// the per-pass timestamp marks (gated on the device reporting usable timestamps).</param>
/// <param name="TimingRecorder">An optional GPU timing recorder (see <paramref name="TimingFactory"/>).</param>
/// <param name="ProgramWordCapacity">An optional FLOOR on the program buffer's packed-word capacity (the engine
/// always provisions at least <paramref name="Program"/>'s length). A host that hot-swaps programs via
/// <see cref="SdfWorldEngine.UploadProgram"/> declares its envelope here instead of relying on every future program
/// staying within the first one's size.</param>
/// <param name="InstanceCapacity">An optional FLOOR on the instance count the per-tile mask buffer is sized for (the
/// engine always provisions at least <paramref name="Program"/>'s <see cref="SdfProgram.InstanceMaskWordCount"/>).
/// The hot-swap counterpart of <paramref name="ProgramWordCapacity"/> for instanced programs.</param>
public sealed record SdfWorldEngineOptions(
    SdfProgram Program,
    uint ViewportCapacity = SdfWorldEngine.MaxViewports,
    uint ChildMask = 0,
    int DynamicTransformCapacity = 1,
    Func<IGpuDeviceContext, IGpuStorageImage>? CreateOutputImage = null,
    IGpuTimingPoolFactory? TimingFactory = null,
    IGpuTimingRecorder? TimingRecorder = null,
    int ProgramWordCapacity = 0,
    int InstanceCapacity = 0
);

/// <summary>
/// The device-explicit core of the compute SDF WORLD pipeline — the one truth for its buffer/push/binding layouts.
/// One instance owns a scene program (uploaded to the GPU ONCE, at construction) plus every pipeline/buffer/image the
/// four kernels need, and runs the full chain per frame: <c>sdf-beam.comp</c> (tile-cull cone-march prepass) →
/// <c>sdf-cull-args.comp</c> (GPU-written INDIRECT dispatch args: the surviving-tile bbox) →
/// <c>sdf-world-views.comp</c> (per-view render, dispatched INDIRECTLY from those args) →
/// <c>sdf-world-composite.comp</c> (source-agnostic region composite, also dispatched indirectly). Fully
/// backend-neutral through the <see cref="IGpuComputeServices"/> seam.
/// <para>
/// THREE submission models, and they must never blur — nor run against ONE engine instance at overlapping times, since
/// all three re-record the single shared command buffer: <see cref="RenderFrame"/> is the deterministic harness path —
/// one submit-and-wait plus a readback (validation, headless render). <see cref="SubmitFrame"/> is the live node path —
/// fire-and-forget (the HOST's frame pacing orders this frame's writes against the previous frame's reads, e.g. the
/// launcher's BeginFrame WaitIdle), plus the export-mode queue drain when the output crosses a backend seam.
/// <see cref="SubmitFramePipelined"/> is the DEMO-PREVIEW path — a non-blocking FENCED readback (submit fire-and-forget,
/// poll <see cref="IsFramePixelsReady"/> on a LATER produced frame, then <see cref="AcquireFramePixels"/> maps it), so
/// the live in-editor bake preview never idles the shared present queue mid-sculpt. It stays frame-count driven
/// (determinism is a feature even here), and a single-in-flight guard forbids interleaving it with the other two on one
/// engine — <see cref="RenderFrame"/>, <see cref="SubmitFrame"/>, and <see cref="SubmitFramePipelined"/> each throw while
/// a pipelined frame is outstanding. Adding a wait to <see cref="SubmitFrame"/> is a frame-rate regression; removing the
/// wait from <see cref="RenderFrame"/> is a nondeterminism bug.
/// </para>
/// </summary>
public sealed class SdfWorldEngine : IDisposable {
    private const uint CompositeOutputBindingIndex = 0; // sdf-world-composite.comp: Output at binding 0
    private const int CompositePushByteLength = (16 + ((sizeof(float) * 4) * MaxViewports)); // CompositeParams2: uint2 extent + uint count + uint tileGridPacked + float4 rects[5]
    private const uint CompositeSourceBindingIndex = 1; // sdf-world-composite.comp: sources[] at binding 1
    private const uint CullArgsBindingIndex = 5; // sdf-cull-args.comp: views indirect dispatch args (register u0)
    private const uint CullBoundsBindingIndex = 6; // sdf-cull-args.comp: bbox group origin (register u1); read by sdf-world-views.comp at binding 8
    private const uint DynamicTransformBindingIndex = 9; // sdf-vm.hlsli's [[vk::binding(9, 0)]] / register(t2) (world path)
    private const int DynamicTransformByteLength = ((sizeof(float) * 4) * 2); // 32-byte rigid transform: float4 position + float4 orientation quaternion (KEEP IN SYNC with sdf-vm.hlsli sdfDynamicTransforms)
    private const GpuPixelFormat Format = GpuPixelFormat.R8G8B8A8Unorm;
    private const uint InstanceMaskBindingIndex = 7; // sdf-beam.comp (u1) / sdf-world-views.comp (t13): per-tile instance mask, written by the beam prepass, read by Stage 1; the per-tile word count is the LIVE uploaded program's InstanceMaskWordCount (pushed per frame, capped at the construction width the buffer was sized for)
    /// <summary>The kernels' source array length (<c>sources[5]</c>) — the most viewports one engine composites.</summary>
    public const int MaxViewports = 5;
    private const uint ProgramBindingIndex = 1; // matches sdf-vm.hlsli's [[vk::binding(1, 0)]] / register(t0)
    private const int PushConstantByteLength = ((sizeof(uint) * 4) * 2); // 32-byte CompositeParams (16-byte rounded); word 6 = screenMask, word 7 = instanceMaskWordCount
    /// <summary>The kernels' screen-source count — the most screen surfaces one program may declare (matches
    /// <see cref="SdfProgramBuilder.MaxScreenSurfaces"/>). EIGHT SEPARATE combined-image-sampler bindings (not one
    /// array binding): DXC's <c>vk::combinedImageSampler</c> only fuses a SCALAR Texture2D+SamplerState pair, so a
    /// true single Vulkan combined-image-sampler array isn't expressible in the shared HLSL — see
    /// <see cref="ScreenSourceBindingIndices"/>.</summary>
    public const int MaxScreenSurfaces = 8;
    // sdf-world-views.comp (Stage 1 ONLY): screenSource0..7, registers t5..t12 — one binding per screen index (KEEP IN
    // SYNC with sdf-world.hlsli's screenSource0..7 declarations).
    private static readonly uint[] ScreenSourceBindingIndices = [12, 13, 14, 15, 16, 17, 18, 19];
    private const int ScreenSurfaceByteLength = ((sizeof(float) * 4) * 3); // 48-byte ScreenSurfaceData: right.xyz+halfWidth, up.xyz+halfHeight, origin.xyz+pad (KEEP IN SYNC with sdf-world.hlsli)
    private const uint ScreenSurfaceBindingIndex = 10; // sdf-world-views.comp (Stage 1 ONLY): screenSurfaces, register t4
    private const uint ScreenLightBindingIndex = 11; // sdf-world-views.comp (Stage 1 ONLY): sdfScreenLights, register t14 — the LAST SRV (per-frame screen glow colors + environment; KEEP IN SYNC with sdf-world.hlsli)
    private const int ScreenLightByteLength = ((sizeof(float) * 4) * (MaxScreenSurfaces + 5)); // float4 rgb+intensity per screen (0..7) + env (8) + FOUR grid-lock rows (9..12: world grid, object origin+pitchX, object frame quat, object pitchZ+patchRadius) — KEEP IN SYNC with sdf-world.hlsli SdfGridWorld..SdfGridObjParams
    private const float ScreenLightIntensity = 2.5f; // room-glow gain applied to each screen's average color
    private const uint TileBindingIndex = 3; // matches sdf-world.hlsli's [[vk::binding(3, 0)]]
    // The tile cull buffer carries THREE planes per (viewport, tile), each of stride
    // (tileGrid.x * tileGrid.y * viewportCount): plane 0 = the march-start lower bound (the classic beam
    // output; the ONLY plane cull-args + the compositor read, so their indexing is unchanged), plane 1 =
    // firstExit, plane 2 = secondEntry — the four-bound teleport's proven-empty gap [firstExit, secondEntry]
    // (Larsson "The Gunk"). The extra planes are written by sdf-beam and read by sdf-world-views only; a tile
    // with no proven gap packs firstExit = MaxDistance (teleport disabled), so the plane is a total function.
    // KEEP IN SYNC with WorldTilePlaneCount + worldTilePlaneStride in sdf-world.hlsli / sdf-tile.hlsli.
    private const uint TilePlaneCount = 3;
    private const uint TileSize = 16; // KEEP IN SYNC with WorldTileSize in sdf-world.hlsli
    private const uint TimingCapacity = 8; // timestamp slots per pool (headroom over the marks; must stay >= TimingMarkCount)
    // The GPU timing marks: one frame-start mark (query 0, top of pipe), then one BOTTOM-OF-PIPE close per render pass,
    // in submission order. The PASS between mark i and i+1 is named PassLabels[i]; the whole frame is mark 0 .. mark
    // last. Adding a pass is TWO edits — append its label here AND its WriteTimingMark close in Record — after which the
    // sdf.info verb, the [world-timing] line, and the bench's per-pass feed all surface it with no further change (each
    // reads PassTimingLabels / TryReadPassTimings, never a hardcoded tuple). TimingCapacity (8) is the pool ceiling, so
    // at most 7 passes fit before the pools must be resized.
    private static readonly string[] PassLabels = ["mask", "beam", "views", "composite"];
    private static readonly uint TimingMarkCount = (uint)(PassLabels.Length + 1);
    private const int ViewportByteLength = ((sizeof(float) * 4) * 5); // 80-byte ViewportData (KEEP IN SYNC with sdf-world.hlsli)
    private const uint ViewportBindingIndex = 2; // matches sdf-world.hlsli's [[vk::binding(2, 0)]]
    private const uint ViewsCullBoundsBindingIndex = 8; // sdf-world-views.comp: the bbox origin (register t3); the source array is ONE binding number (4) whose 5 elements pack into derived heap slots, so 8 never collides
    private const uint ViewSourceBindingIndex = 4; // sdf-world-views.comp: sources[] LAST (after the fixed 1/2/3)
    private const uint WorkgroupEdge = 8;

    private readonly IGpuComputePipeline m_beamPipeline;
    private readonly nint m_beamSet;
    private readonly IGpuShaderModule m_beamShaderModule;
    private readonly nint[] m_boundScreenSourceViews = new nint[MaxScreenSurfaces];
    private readonly nint[] m_boundSourceViews = new nint[MaxViewports];
    private readonly uint m_childMask;
    private readonly nint[] m_childSourceViews = new nint[MaxViewports];
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
    private readonly int m_dynamicTransformCapacity;
    private readonly byte[] m_dynamicTransformScratch;
    private readonly IGpuExportableStorageImage? m_exportableImage;
    private readonly bool m_exportMode;
    private readonly IGpuComputeServices m_gpu;
    private readonly uint m_height;
    private readonly IGpuComputePipeline m_instanceCullPipeline;
    private readonly nint m_instanceCullSet;
    private readonly IGpuShaderModule m_instanceCullShaderModule;
    private readonly IGpuStorageBuffer m_instanceMaskBuffer;
    private readonly int m_instanceMaskWordCount;
    private readonly nint m_pool;
    private readonly IGpuStorageBuffer m_programBuffer;
    private readonly int m_programWordCapacity;
    private readonly byte[] m_pushConstant = new byte[PushConstantByteLength];
    private readonly nint m_screenSampler;
    private readonly IGpuStorageImage m_screenSourceFiller;
    private readonly nint[] m_screenSourceViews = new nint[MaxScreenSurfaces];
    private readonly IGpuStorageBuffer m_screenSurfaceBuffer;
    // The host-side mirror of the screen-surface table: UploadProgram seeds it from the program's declared surfaces;
    // SetScreenSurface patches one entry's slice for a per-frame transform (a screen riding a dynamic entity, e.g. a
    // slab riding a moving rig); PrepareFrame re-uploads the whole thing every frame, exactly like the viewport/dynamic-
    // transform/screen-light scratch buffers below — Write<T> always copies from the buffer's start, so there is no
    // partial-range GPU write to ride instead.
    private readonly byte[] m_screenSurfaceScratch = new byte[(MaxScreenSurfaces * ScreenSurfaceByteLength)];
    private readonly IGpuStorageBuffer m_screenLightBuffer;
    private readonly byte[] m_screenLightScratch = new byte[ScreenLightByteLength];
    private readonly Vector3[] m_screenLightColors = new Vector3[MaxScreenSurfaces];
    private readonly IGpuStorageImage?[] m_sourceTextures;
    private readonly IGpuStorageImage m_storageImage;
    private readonly IGpuStorageBuffer m_tileBuffer;
    private readonly uint m_tileGridX;
    private readonly uint m_tileGridY;
    private readonly GpuTimestampCapabilities m_timingCapabilities;
    private readonly bool m_timingEnabled;
    private readonly IGpuTimingPool[]? m_timingPools;
    private readonly IGpuTimingRecorder? m_timingRecorder;
    private readonly uint m_viewportCapacity;
    private readonly IGpuStorageBuffer m_viewportBuffer;
    private readonly byte[] m_viewportScratch;
    private readonly IGpuStorageBuffer m_viewsArgsBuffer;
    private readonly IGpuComputePipeline m_viewsPipeline;
    private readonly nint m_viewsSet;
    private readonly IGpuShaderModule m_viewsShaderModule;
    private readonly uint m_width;
    private bool m_disposed;
    private bool m_imageInitialized;
    private double? m_lastFrameGpuMilliseconds;
    private int m_liveInstanceMaskWordCount;
    private bool m_pipelinedFrameInFlight;
    private IGpuSurfaceReadback? m_readback;
    private int m_requiredDynamicTransformCapacity;
    private uint m_screenSourceMask;
    private ulong m_timingFrame;

    /// <summary>Initializes a new instance of the <see cref="SdfWorldEngine"/> class: builds the four pipelines,
    /// every buffer and image at the provisioned viewport capacity, and uploads the scene program ONCE.</summary>
    /// <param name="gpu">The neutral GPU compute services.</param>
    /// <param name="device">The GPU device the engine renders on.</param>
    /// <param name="kernels">The compiled world kernel set for the same backend as <paramref name="device"/>.</param>
    /// <param name="width">The composited output width in pixels.</param>
    /// <param name="height">The composited output height in pixels.</param>
    /// <param name="options">The construction options (scene program, capacities, child mask, export/timing seams).</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A dimension is zero, or the viewport capacity is 0 or above <see cref="MaxViewports"/>.</exception>
    public SdfWorldEngine(IGpuComputeServices gpu, IGpuDeviceContext device, SdfWorldKernels kernels, uint width, uint height, SdfWorldEngineOptions options) {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Program);

        if (
            (0 == width) ||
            (0 == height)
        ) {
            throw new ArgumentException(message: "World engine dimensions must be non-zero.");
        }

        if (
            (0 == options.ViewportCapacity) ||
            (options.ViewportCapacity > MaxViewports)
        ) {
            throw new ArgumentException(message: $"The world engine provisions 1 to {MaxViewports} viewport slots; the options ask for {options.ViewportCapacity}.");
        }

        m_childMask = options.ChildMask;
        m_descriptorAllocator = gpu.DescriptorAllocator;
        m_deviceContext = device;
        m_deviceHandle = device.DeviceHandle;
        m_dynamicTransformCapacity = Math.Max(Math.Max(1, options.DynamicTransformCapacity), options.Program.RequiredDynamicTransformCapacity);
        m_dynamicTransformScratch = new byte[(m_dynamicTransformCapacity * DynamicTransformByteLength)];
        m_gpu = gpu;
        m_height = height;
        m_tileGridX = ((width + (TileSize - 1)) / TileSize);
        m_tileGridY = ((height + (TileSize - 1)) / TileSize);
        m_viewportCapacity = options.ViewportCapacity;
        m_viewportScratch = new byte[((int)m_viewportCapacity * ViewportByteLength)];
        m_width = width;

        m_beamShaderModule = gpu.ShaderModuleFactory.Create(deviceContext: device, stage: GpuShaderStage.Compute, bytecode: kernels.Beam);
        m_instanceCullShaderModule = gpu.ShaderModuleFactory.Create(deviceContext: device, stage: GpuShaderStage.Compute, bytecode: kernels.InstanceCull);
        m_cullArgsShaderModule = gpu.ShaderModuleFactory.Create(deviceContext: device, stage: GpuShaderStage.Compute, bytecode: kernels.CullArgs);
        m_viewsShaderModule = gpu.ShaderModuleFactory.Create(deviceContext: device, stage: GpuShaderStage.Compute, bytecode: kernels.Views);
        m_compositeShaderModule = gpu.ShaderModuleFactory.Create(deviceContext: device, stage: GpuShaderStage.Compute, bytecode: kernels.Composite);

        // One FULL-SIZE source texture per NON-child viewport slot — Stage 1 renders the viewport's region-extent into
        // it, Stage 2 copies that into the screen region. Sized to the FULL frame extent (the largest any region can
        // reach), NOT any one frame's region: the regions animate every frame, so a frozen region-sized texture (e.g. a
        // half-width split) under-allocated the pane and blanked it when the layout grew. Writes/reads stay within the
        // live region (≤ full), so full-size is always in-bounds. Child slots stay null: their source is the hosted
        // child's storage image (bound per frame via SetChildSource), and the child owns that image's layout, so the
        // engine never creates or transitions one.
        m_sourceTextures = new IGpuStorageImage?[(int)m_viewportCapacity];

        for (var index = 0; (index < (int)m_viewportCapacity); index++) {
            if (IsChildSlot(slot: index)) {
                continue;
            }

            m_sourceTextures[index] = gpu.StorageImageFactory.Create(deviceContext: device, format: Format, height: height, width: width);
        }

        // A dedicated 1x1 ShaderReadOnly filler for an unbound screen-source slot: the per-viewport sources[] filler
        // (SourceViewForSlot(0)) is wrong here — it lives in the General (UAV) layout Stage 1/2 read/write it in,
        // while a combined-image-sampler binding requires ShaderReadOnly, so aliasing it trips Vulkan validation the
        // moment any viewport-source dispatch runs. This image is transitioned ONCE, below, and never written again.
        m_screenSourceFiller = gpu.StorageImageFactory.Create(deviceContext: device, format: Format, height: 1, width: 1);

        // The output image is either a plain same-device storage image (resolved from the neutral factory) or an
        // exportable one supplied by the host (cross-backend present). Only the FINAL output crosses the seam; the
        // per-view sources are always internal.
        m_storageImage = (options.CreateOutputImage is null)
            ? gpu.StorageImageFactory.Create(deviceContext: device, format: Format, height: height, width: width)
            : options.CreateOutputImage(device);
        m_exportableImage = m_storageImage as IGpuExportableStorageImage;
        m_exportMode = (m_exportableImage is not null);

        m_programWordCapacity = Math.Max(options.Program.Words.Length, options.ProgramWordCapacity);
        m_programBuffer = gpu.StorageBufferFactory.Create(deviceContext: device, sizeBytes: ((ulong)m_programWordCapacity * sizeof(uint)));
        m_viewportBuffer = gpu.StorageBufferFactory.Create(deviceContext: device, sizeBytes: (ulong)m_viewportScratch.Length);
        m_dynamicTransformBuffer = gpu.StorageBufferFactory.Create(deviceContext: device, sizeBytes: (ulong)m_dynamicTransformScratch.Length);
        // The screen-surface table: always allocated at MaxScreenSurfaces capacity, indexed directly by screen index
        // (like the always-bound dynamic-transform slot), so Stage 1's binding stays valid for a program with none —
        // an all-zero undeclared slot is never addressed (no material id in a consistent program points at it).
        m_screenSurfaceBuffer = gpu.StorageBufferFactory.Create(deviceContext: device, sizeBytes: (MaxScreenSurfaces * (ulong)ScreenSurfaceByteLength));
        // The per-frame screen-light buffer: 4 screen colors + 1 environment float4, uploaded each frame like the
        // dynamic-transform buffer. Bound to the views set only (Stage 1 shades; the beam prepass does not).
        m_screenLightBuffer = gpu.StorageBufferFactory.Create(deviceContext: device, sizeBytes: (ulong)m_screenLightScratch.Length);
        // The cull buffer is GPU-written by the beam prepass (a UAV), so it is device-local (a Direct3D 12 default heap).
        // Sized for TilePlaneCount planes (marchStart + firstExit + secondEntry — the four-bound teleport); cull-args and
        // the compositor read only plane 0, so their (viewport, tile) indexing is unaffected by the extra capacity.
        m_tileBuffer = gpu.StorageBufferFactory.CreateDeviceLocal(deviceContext: device, sizeBytes: ((ulong)TilePlaneCount * m_viewportCapacity * m_tileGridX * m_tileGridY * sizeof(float)));
        // The per-tile instance mask: same (viewport, tile) indexing as the cull buffer, GPU-written by the beam
        // prepass alongside it (a UAV, so device-local too), read by Stage 1 to gate its masked map() calls. The
        // buffer is sized for the CONSTRUCTION program's width (ceil(instanceCount/32) uints, at least 1 —
        // SdfProgram.InstanceMaskWordCount); the kernels index with the LIVE uploaded program's width, pushed per
        // frame (m_liveInstanceMaskWordCount), which UploadProgram caps at this construction width.
        m_instanceMaskWordCount = Math.Max(options.Program.InstanceMaskWordCount, SdfProgram.InstanceMaskWordCountFor(instanceCount: options.InstanceCapacity));
        m_instanceMaskBuffer = gpu.StorageBufferFactory.CreateDeviceLocal(deviceContext: device, sizeBytes: ((ulong)m_viewportCapacity * m_tileGridX * m_tileGridY * (uint)m_instanceMaskWordCount * sizeof(uint)));

        // GPU-driven cull: the cull-args pass reduces the cull buffer to the Stage-1 INDIRECT dispatch args (the
        // surviving-tile bbox, 3 group counts) and the bbox group origin (2 uints). Both are device-local — the GPU
        // writes them as UAVs, then a barrier orders the indirect read; the views dispatch reads the args (the
        // dispatch grid) and the bounds (its pixel offset). The all-empty margins are never dispatched.
        m_viewsArgsBuffer = gpu.StorageBufferFactory.CreateIndirectArgs(deviceContext: device, sizeBytes: (sizeof(uint) * 3), deviceLocal: true);
        m_cullBoundsBuffer = gpu.StorageBufferFactory.CreateDeviceLocal(deviceContext: device, sizeBytes: (sizeof(uint) * 2));

        // Stage 2's full-frame composite grid is constant for the run, so its dispatch is driven INDIRECTLY: the GPU
        // reads the (x, y, z) group counts from this host-written args buffer (vkCmdDispatchIndirect / ExecuteIndirect)
        // instead of the CPU supplying them. The counts equal the equivalent direct dispatch, so it is pixel-neutral
        // (the `world` parity gate is the guard). Host-written once + host-coherent, so the queue-submit host-write
        // visibility covers it with no indirect-read barrier.
        m_compositeArgsBuffer = gpu.StorageBufferFactory.CreateIndirectArgs(deviceContext: device, sizeBytes: (sizeof(uint) * 3));
        m_compositeArgsBuffer.Write<uint>(data: [
            ((width + (WorkgroupEdge - 1)) / WorkgroupEdge),
            ((height + (WorkgroupEdge - 1)) / WorkgroupEdge),
            1u,
        ]);

        var pushConstantBinding = new GpuPushConstantBinding(data: m_pushConstant, offset: 0, stageFlags: GpuShaderStage.Compute);
        var compositePushBinding = new GpuPushConstantBinding(data: m_compositePush, offset: 0, stageFlags: GpuShaderStage.Compute);

        // Beam prepass: program (1) + viewports (2) + dynamic entity transforms (9) + cull buffer written (3) + the
        // per-tile instance mask READ (7 — the MASK-FIRST order: the cone march evaluates the tile-masked field the
        // instance-cull pass wrote, so a march sample costs O(instances near the tile), not O(all instances)). No
        // output image. Direct3D 12 assigns registers from THIS order: t0 program, t1 viewports, t2 dynamicTransforms,
        // u0 tiles, t3 instanceMasks — the kernel's SDF_INSTANCE_MASKS_REGISTER override mirrors it.
        GpuComputeBinding[] beamBindings = [
            new GpuComputeBinding(Binding: ProgramBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            new GpuComputeBinding(Binding: ViewportBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            new GpuComputeBinding(Binding: DynamicTransformBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            new GpuComputeBinding(Binding: TileBindingIndex, Kind: GpuComputeBindingKind.StorageBufferReadWrite),
            new GpuComputeBinding(Binding: InstanceMaskBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
        ];

        // Instance-cull pass (sdf-instance-cull.comp — the frame's FIRST pass, and its OWN kernel so the cell walk's
        // register footprint never taxes the cone march's occupancy): program (1) + viewports (2) + dynamic entity
        // transforms (9, a DYNAMIC instance's bound resolves through it) + the per-tile instance mask written (7).
        // Direct3D 12 assigns registers from THIS order: t0 program, t1 viewports, t2 dynamicTransforms, u0
        // instanceMasks — the kernel's register() annotations mirror it exactly.
        GpuComputeBinding[] instanceCullBindings = [
            new GpuComputeBinding(Binding: ProgramBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            new GpuComputeBinding(Binding: ViewportBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            new GpuComputeBinding(Binding: DynamicTransformBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            new GpuComputeBinding(Binding: InstanceMaskBindingIndex, Kind: GpuComputeBindingKind.StorageBufferReadWrite),
        ];

        // Cull-args reduction: cull buffer read (3) + the views indirect args written (5) + the bbox origin written (6).
        GpuComputeBinding[] cullArgsBindings = [
            new GpuComputeBinding(Binding: TileBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            new GpuComputeBinding(Binding: CullArgsBindingIndex, Kind: GpuComputeBindingKind.StorageBufferReadWrite),
            new GpuComputeBinding(Binding: CullBoundsBindingIndex, Kind: GpuComputeBindingKind.StorageBufferReadWrite),
        ];

        // Stage 1 (per-view SDF): program (1) + viewports (2) + dynamic entity transforms (9) + cull buffer read (3) +
        // the source array (4) + the GPU-computed bbox origin (8) + the per-tile instance mask read (7) + the
        // screen-surface table (10) + EIGHT separate screen-source SampledImage bindings LAST (12..19 — DXC cannot
        // fuse an ARRAY texture into one Vulkan combined-image-sampler, so each screen index gets its own binding;
        // the pipeline factory still bakes in exactly ONE static nearest sampler on Direct3D 12, since all eight
        // share that one filter). dynamicTransforms is listed BEFORE cullBounds so the SRV registers resolve program
        // t0, viewport t1, dynamicTransforms t2, cullBounds t3, screenSurfaces t4, screenSources t5..t12, then
        // instanceMasks t13, screenLights t14 (matching the HLSL) — Direct3D 12 assigns t#/s# registers from THIS
        // array's order (DirectXGpuComputePipelineFactory), so the HLSL's explicit register(tN) annotations must
        // mirror this exact sequence; a reorder here without the matching HLSL edit desyncs the root signature.
        GpuComputeBinding[] viewsBindings = [
            new GpuComputeBinding(Binding: ProgramBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            new GpuComputeBinding(Binding: ViewportBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            new GpuComputeBinding(Binding: DynamicTransformBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            new GpuComputeBinding(Binding: TileBindingIndex, Kind: GpuComputeBindingKind.StorageBufferReadWrite),
            new GpuComputeBinding(Binding: ViewSourceBindingIndex, Kind: GpuComputeBindingKind.StorageImage, Count: MaxViewports),
            new GpuComputeBinding(Binding: ViewsCullBoundsBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            new GpuComputeBinding(Binding: ScreenSurfaceBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            new GpuComputeBinding(Binding: ScreenSourceBindingIndices[0], Kind: GpuComputeBindingKind.SampledImage),
            new GpuComputeBinding(Binding: ScreenSourceBindingIndices[1], Kind: GpuComputeBindingKind.SampledImage),
            new GpuComputeBinding(Binding: ScreenSourceBindingIndices[2], Kind: GpuComputeBindingKind.SampledImage),
            new GpuComputeBinding(Binding: ScreenSourceBindingIndices[3], Kind: GpuComputeBindingKind.SampledImage),
            new GpuComputeBinding(Binding: ScreenSourceBindingIndices[4], Kind: GpuComputeBindingKind.SampledImage),
            new GpuComputeBinding(Binding: ScreenSourceBindingIndices[5], Kind: GpuComputeBindingKind.SampledImage),
            new GpuComputeBinding(Binding: ScreenSourceBindingIndices[6], Kind: GpuComputeBindingKind.SampledImage),
            new GpuComputeBinding(Binding: ScreenSourceBindingIndices[7], Kind: GpuComputeBindingKind.SampledImage),
            new GpuComputeBinding(Binding: InstanceMaskBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            // Appended LAST so its SRV resolves to register t14 (after instanceMasks t13): the per-frame screen-light buffer.
            new GpuComputeBinding(Binding: ScreenLightBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
        ];

        // Stage 2 (source-agnostic composite): output image (0) + the source array (1) + the cull buffer read (3),
        // which the compositor uses to flatten every empty (culled) tile to a constant.
        GpuComputeBinding[] compositeBindings = [
            new GpuComputeBinding(Binding: CompositeOutputBindingIndex, Kind: GpuComputeBindingKind.StorageImage),
            new GpuComputeBinding(Binding: CompositeSourceBindingIndex, Kind: GpuComputeBindingKind.StorageImage, Count: MaxViewports),
            new GpuComputeBinding(Binding: TileBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
        ];

        m_beamPipeline = gpu.ComputePipelineFactory.Create(bindings: beamBindings, computeShaderModule: m_beamShaderModule, deviceContext: device, pushConstantBinding: pushConstantBinding);
        m_instanceCullPipeline = gpu.ComputePipelineFactory.Create(bindings: instanceCullBindings, computeShaderModule: m_instanceCullShaderModule, deviceContext: device, pushConstantBinding: pushConstantBinding);
        m_cullArgsPipeline = gpu.ComputePipelineFactory.Create(bindings: cullArgsBindings, computeShaderModule: m_cullArgsShaderModule, deviceContext: device, pushConstantBinding: pushConstantBinding);
        // Nearest filtering end to end: a bound screen source (an emulator/child's native pixels) magnifies as crisp
        // cells, never bilinear smears — the whole point of sampling instead of the flat material.
        m_viewsPipeline = gpu.ComputePipelineFactory.Create(bindings: viewsBindings, computeShaderModule: m_viewsShaderModule, deviceContext: device, pushConstantBinding: pushConstantBinding, samplerFilter: GpuSamplerFilter.Nearest);
        m_compositePipeline = gpu.ComputePipelineFactory.Create(bindings: compositeBindings, computeShaderModule: m_compositeShaderModule, deviceContext: device, pushConstantBinding: compositePushBinding);

        // One pool, FIVE independent sets — the Direct3D 12 allocator bump-allocates a non-overlapping heap region per
        // set (like a Vulkan pool), so they never clobber. The capacity is DERIVED from the five sets' binding lists
        // (an array binding contributes its full Count), so it can never drift out of sync when a binding is added or
        // MaxViewports changes.
        var poolSizes = GpuDescriptorPoolSizes.ForSets(beamBindings, instanceCullBindings, cullArgsBindings, viewsBindings, compositeBindings);

        m_pool = m_descriptorAllocator.CreatePool(deviceHandle: m_deviceHandle, sizes: poolSizes);

        m_beamSet = m_descriptorAllocator.AllocateSet(descriptorSetLayoutHandle: m_beamPipeline.DescriptorSetLayoutHandle, deviceHandle: m_deviceHandle, poolHandle: m_pool);
        WriteStorageBuffer(set: m_beamSet, binding: ProgramBindingIndex, buffer: m_programBuffer);
        WriteStorageBuffer(set: m_beamSet, binding: ViewportBindingIndex, buffer: m_viewportBuffer);
        WriteStorageBuffer(set: m_beamSet, binding: DynamicTransformBindingIndex, buffer: m_dynamicTransformBuffer);
        WriteStorageBufferReadWrite(set: m_beamSet, binding: TileBindingIndex, buffer: m_tileBuffer);
        WriteStorageBufferReadOnly(set: m_beamSet, binding: InstanceMaskBindingIndex, buffer: m_instanceMaskBuffer);

        // The instance-cull set: the mask buffer written (the frame's first pass — the beam then reads it).
        m_instanceCullSet = m_descriptorAllocator.AllocateSet(descriptorSetLayoutHandle: m_instanceCullPipeline.DescriptorSetLayoutHandle, deviceHandle: m_deviceHandle, poolHandle: m_pool);
        WriteStorageBuffer(set: m_instanceCullSet, binding: ProgramBindingIndex, buffer: m_programBuffer);
        WriteStorageBuffer(set: m_instanceCullSet, binding: ViewportBindingIndex, buffer: m_viewportBuffer);
        WriteStorageBuffer(set: m_instanceCullSet, binding: DynamicTransformBindingIndex, buffer: m_dynamicTransformBuffer);
        WriteStorageBufferReadWrite(set: m_instanceCullSet, binding: InstanceMaskBindingIndex, buffer: m_instanceMaskBuffer);

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
        WriteStorageBufferReadOnly(set: m_viewsSet, binding: InstanceMaskBindingIndex, buffer: m_instanceMaskBuffer);
        // The screen-surface table (48-byte ScreenSurfaceData, same stride-16-multiple SRV pattern as ViewportData).
        WriteStorageBuffer(set: m_viewsSet, binding: ScreenSurfaceBindingIndex, buffer: m_screenSurfaceBuffer);
        // The per-frame screen-light buffer (float4 stride — the plain 16-byte WriteStorageBuffer is correct).
        WriteStorageBuffer(set: m_viewsSet, binding: ScreenLightBindingIndex, buffer: m_screenLightBuffer);

        // The screen sources (bindings 12-15) are (re)bound per frame by BindScreenSources, mirroring the source array —
        // a filler view isn't known until the first frame's SDF source texture (or child surface) exists.
        m_screenSampler = m_descriptorAllocator.CreateSampler(deviceHandle: m_deviceHandle, filter: GpuSamplerFilter.Nearest);

        m_compositeSet = m_descriptorAllocator.AllocateSet(descriptorSetLayoutHandle: m_compositePipeline.DescriptorSetLayoutHandle, deviceHandle: m_deviceHandle, poolHandle: m_pool);
        m_descriptorAllocator.WriteStorageImage(arrayElement: 0, binding: CompositeOutputBindingIndex, descriptorSetHandle: m_compositeSet, deviceHandle: m_deviceHandle, imageViewHandle: m_storageImage.ImageViewHandle);
        WriteStorageBufferReadOnly(set: m_compositeSet, binding: TileBindingIndex, buffer: m_tileBuffer);

        // The source array (binding the SDF view textures and any hosted child surfaces) is (re)bound per frame by
        // BindSources — child image-views aren't known until their nodes have produced.
        m_commandPool = gpu.CommandPoolFactory.Create(deviceContext: device);

        // The "uploaded once" seam: the program (and its screen-surface table) is uploaded here and normally never
        // again — frames move entities by rewriting only the small dynamic-transform buffer. UploadProgram is the
        // single owner of per-program derived state (its capacity checks trivially pass for the construction program).
        UploadProgram(program: options.Program);

        // Opt-in GPU timing: when a timing factory + recorder are supplied AND the device supports timestamps, each
        // frame writes the four per-pass marks (frame-start, beam-close, views-close, composite-close). Two pools are
        // created so a fire-and-forget host can read the previous frame's results with no device-idle stall
        // (double-buffering); the waited path reads the just-submitted pool directly.
        if ((options.TimingFactory is not null) && (options.TimingRecorder is not null)) {
            m_timingCapabilities = options.TimingFactory.GetCapabilities(deviceContext: device);

            if (m_timingCapabilities.IsSupported) {
                m_timingPools = [
                    options.TimingFactory.CreateTimestampPool(deviceContext: device, queryCapacity: TimingCapacity),
                    options.TimingFactory.CreateTimestampPool(deviceContext: device, queryCapacity: TimingCapacity),
                ];
                m_timingRecorder = options.TimingRecorder;
                m_timingEnabled = true;
            }
        }
    }

    /// <summary>Gets whether the engine renders into an exportable image (cross-backend handoff layout + shared handle).</summary>
    public bool ExportMode => m_exportMode;
    /// <summary>Gets the exported image's shared NT handle (zero-copy cross-backend present); 0 outside export mode.</summary>
    public nint ExportSharedHandle => (m_exportableImage?.SharedHandle ?? 0);
    /// <summary>Gets the GPU time (in milliseconds) of the last <see cref="RenderFrame"/> when opt-in timing was
    /// enabled at construction — the frame-start → composite-close bracket of the four per-pass marks — or
    /// <see langword="null"/> when timing is disabled or the timestamps were not yet readable.</summary>
    public double? LastFrameGpuMilliseconds => m_lastFrameGpuMilliseconds;
    /// <summary>Gets or sets the SDF debug view mode packed into each viewport row (<c>forward.w</c>); 0 renders the
    /// final lit image.</summary>
    public int DebugMode { get; set; }
    /// <summary>Gets the GPU timestamp capabilities when opt-in timing was enabled (period/valid-bits for digests).</summary>
    public GpuTimestampCapabilities TimingCapabilities => m_timingCapabilities;
    /// <summary>Gets whether opt-in GPU timing is live (factory + recorder supplied and the device supports timestamps).</summary>
    public bool TimingEnabled => m_timingEnabled;
    /// <summary>Gets the native image handle of the composited output image. After a frame, the image rests in the
    /// <see cref="GpuImageLayout.ShaderReadOnly"/> layout (or the cross-backend <see cref="GpuImageLayout.External"/>
    /// layout in export mode) — a downstream pass may transition it and read it in place, zero-copy.</summary>
    public nint OutputImageHandle => m_storageImage.ImageHandle;
    /// <summary>Gets the native image-view handle of the composited output image (for binding it as a source in a
    /// downstream descriptor set).</summary>
    public nint OutputImageViewHandle => m_storageImage.ImageViewHandle;

    /// <summary>Re-uploads the scene program (the host's <c>ProgramChanged</c> path — e.g. a rebuilt overworld scene).
    /// The program must fit the buffers sized at construction (including its screen-surface table and its per-tile
    /// instance-mask width).</summary>
    /// <param name="program">The scene program to upload.</param>
    /// <exception cref="ArgumentException">The program's instance count derives a wider per-tile mask than the
    /// construction program's (the mask buffer cannot grow after construction).</exception>
    public void UploadProgram(SdfProgram program) {
        ArgumentNullException.ThrowIfNull(program);

        if (program.Words.Length > m_programWordCapacity) {
            throw new ArgumentException(message: $"The uploaded program has {program.Words.Length} packed words; the engine was constructed for {m_programWordCapacity} (construct the engine with the larger program).", paramName: nameof(program));
        }

        if (program.InstanceMaskWordCount > m_instanceMaskWordCount) {
            throw new ArgumentException(message: $"The uploaded program's instance count derives {program.InstanceMaskWordCount} mask words per tile; the engine was constructed for {m_instanceMaskWordCount} (construct the engine with the wider program).", paramName: nameof(program));
        }

        if (program.RequiredDynamicTransformCapacity > m_dynamicTransformCapacity) {
            throw new ArgumentException(message: $"The uploaded program requires {program.RequiredDynamicTransformCapacity} dynamic-transform slots; the engine was constructed for {m_dynamicTransformCapacity} (increase DynamicTransformCapacity or construct the engine with the larger program).", paramName: nameof(program));
        }

        m_programBuffer.Write<uint>(data: program.Words);
        // Seed the host-side mirror from the program's declared surfaces (the "program uploaded once" baseline);
        // PrepareFrame re-uploads it every frame, so any SetScreenSurface call made before the next produced frame
        // patches this same mirror before it goes out — a re-upload never resurrects the program's original frame
        // over a live SetScreenSurface write made in between.
        MemoryMarshal.Cast<uint, byte>(span: program.ScreenSurfaceWords).CopyTo(destination: m_screenSurfaceScratch);
        m_screenSurfaceBuffer.Write<byte>(data: m_screenSurfaceScratch);
        m_liveInstanceMaskWordCount = program.InstanceMaskWordCount;
        m_requiredDynamicTransformCapacity = program.RequiredDynamicTransformCapacity;
    }
    /// <summary>Supplies the storage-image view a hosted CHILD produced for its viewport slot this frame; the next
    /// frame binds it into the source arrays (deduplicated — rebinding the same view is free).</summary>
    /// <param name="slot">The child's viewport slot (a bit the construction <see cref="SdfWorldEngineOptions.ChildMask"/> set).</param>
    /// <param name="imageViewHandle">The child's same-device storage-image view (General layout; the child owns it).</param>
    public void SetChildSource(int slot, nint imageViewHandle) {
        if (
            (slot < 0) ||
            (slot >= MaxViewports) ||
            !IsChildSlot(slot: slot)
        ) {
            throw new ArgumentException(message: $"Viewport {slot} is not a child slot of this engine (mask 0x{m_childMask:X}).");
        }

        m_childSourceViews[slot] = imageViewHandle;
    }
    /// <summary>Supplies (or clears) the GPU image a declared screen surface (see <see cref="SdfProgramBuilder"/>'s
    /// screen-surface <c>ScreenSlab</c> overload) at <paramref name="screenIndex"/> samples this frame — a
    /// same-device storage-image view (General layout, shader-readable), typically a hosted child's or an emulator's
    /// NATIVE framebuffer image (not a pane-resampled one: Stage 1 samples it directly, so any fit/scale is the
    /// sampling itself). The next frame binds it into the screen-source array (deduplicated — rebinding the same view
    /// is free). Passing 0 clears the slot: a screen surface with no source bound falls back to the
    /// flat/procedural screen material.</summary>
    /// <param name="screenIndex">The screen source slot (0..7, matching a program's declared
    /// <see cref="SdfScreenSurface.ScreenIndex"/>).</param>
    /// <param name="imageViewHandle">The source's same-device storage-image view, or 0 to unbind.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="screenIndex"/> is outside <c>0..7</c>.</exception>
    public void SetScreenSource(int screenIndex, nint imageViewHandle) {
        if (
            (screenIndex < 0) ||
            (screenIndex >= MaxScreenSurfaces)
        ) {
            throw new ArgumentOutOfRangeException(paramName: nameof(screenIndex), message: $"A screen index must be 0..{MaxScreenSurfaces - 1}.");
        }

        m_screenSourceViews[screenIndex] = imageViewHandle;
        m_screenSourceMask = (0 != imageViewHandle)
            ? (m_screenSourceMask | (1u << screenIndex))
            : (m_screenSourceMask & ~(1u << screenIndex));
    }
    /// <summary>Overwrites screen <paramref name="screenIndex"/>'s world-space sampling frame for the NEXT produced
    /// frame — the per-frame counterpart of the screen-surface table <see cref="UploadProgram"/> otherwise writes only
    /// once, at program upload. A slab riding a moving rig must call
    /// this every frame its geometry moves, or its sampling frame goes stale relative to the geometry the dynamic
    /// transform already moved (a mismatched frame sizes/rotates/positions the sampled image wrong without affecting
    /// the geometry at all — see <see cref="SdfProgramBuilder.ScreenSlab(Vector3, float, Vector3, Vector3, Vector3, int, SdfBlendOp, float)"/>'s
    /// frame contract). Pure host-side buffer state: the shader's <c>screenSurfaces[screenIndex]</c> read
    /// (<c>sdf-world.hlsli</c>) already resolves at shading time with no HLSL change required for this seam — only the
    /// host-side table this call patches needed to become writable per frame.</summary>
    /// <param name="screenIndex">The screen slot (0..7, matching a program's declared <see cref="SdfScreenSurface.ScreenIndex"/>).</param>
    /// <param name="origin">The front face's world-space center this frame.</param>
    /// <param name="right">The unit world-space axis the UV's U increases along this frame (need not be pre-normalized —
    /// normalized here, matching <see cref="SdfProgramBuilder.ScreenSlab(Vector3, float, Vector3, Vector3, Vector3, int, SdfBlendOp, float)"/>'s contract).</param>
    /// <param name="up">The unit world-space axis the UV's V increases against this frame (V = 0 at the top; normalized here).</param>
    /// <param name="halfWidth">The half-extent along <paramref name="right"/> this frame.</param>
    /// <param name="halfHeight">The half-extent along <paramref name="up"/> this frame.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="screenIndex"/> is outside <c>0..7</c>.</exception>
    public void SetScreenSurface(int screenIndex, Vector3 origin, Vector3 right, Vector3 up, float halfWidth, float halfHeight) {
        if (
            (screenIndex < 0) ||
            (screenIndex >= MaxScreenSurfaces)
        ) {
            throw new ArgumentOutOfRangeException(paramName: nameof(screenIndex), message: $"A screen index must be 0..{MaxScreenSurfaces - 1}.");
        }

        var unitRight = Vector3.Normalize(value: right);
        var unitUp = Vector3.Normalize(value: up);
        var floats = MemoryMarshal.Cast<byte, float>(span: m_screenSurfaceScratch.AsSpan());
        // 3 float4 per entry (right.xyz+halfWidth, up.xyz+halfHeight, origin.xyz+pad) — KEEP IN SYNC with SdfProgram's
        // ScreenSurfaceWords packing and sdf-world.hlsli's ScreenSurfaceData.
        var b = (screenIndex * 12);

        floats[b + 0] = unitRight.X; floats[b + 1] = unitRight.Y; floats[b + 2] = unitRight.Z; floats[b + 3] = halfWidth;
        floats[b + 4] = unitUp.X; floats[b + 5] = unitUp.Y; floats[b + 6] = unitUp.Z; floats[b + 7] = halfHeight;
        floats[b + 8] = origin.X; floats[b + 9] = origin.Y; floats[b + 10] = origin.Z; floats[b + 11] = 0f;
    }
    /// <summary>Supplies the colored light a declared screen surface at <paramref name="screenIndex"/> emits into the
    /// room this frame — typically the average color of its framebuffer, so the room glows the game's dominant hue. The
    /// light's position/orientation/extent come from the program's screen-surface table (a screen is an area emitter);
    /// only its color is per-frame. Contributes nothing while the screen is unbound (the shader gates on the same
    /// screen mask <see cref="SetScreenSource"/> maintains) or while the color is zero (a dark screen).</summary>
    /// <param name="screenIndex">The screen slot (0..7, matching a program's declared <see cref="SdfScreenSurface.ScreenIndex"/>).</param>
    /// <param name="color">The emitted light color (linear RGB, typically 0..1).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="screenIndex"/> is outside <c>0..7</c>.</exception>
    public void SetScreenLight(int screenIndex, Vector3 color) {
        if (
            (screenIndex < 0) ||
            (screenIndex >= MaxScreenSurfaces)
        ) {
            throw new ArgumentOutOfRangeException(paramName: nameof(screenIndex), message: $"A screen index must be 0..{MaxScreenSurfaces - 1}.");
        }

        m_screenLightColors[screenIndex] = color;
    }

    /// <summary>Renders one frame — beam → cull-args → views (indirect) → composite in a single submit — against the
    /// uploaded program, WAITS for completion, and returns the composited RGBA readback. The deterministic harness
    /// path (validation stages, headless renders). Must not be called while a <see cref="SubmitFramePipelined"/> frame
    /// is outstanding on this engine (it would re-record the one shared command buffer under a live fence).</summary>
    /// <param name="frame">The per-frame data: views (cameras + regions), time, and the dynamic entity transforms.</param>
    /// <returns>The composited output, tightly packed RGBA8, row-major.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The frame has zero views or more than the provisioned capacity.</exception>
    /// <exception cref="InvalidOperationException">A pipelined preview frame is still in flight on this engine.</exception>
    public byte[] RenderFrame(SdfFrame frame) {
        ThrowIfPipelinedFrameInFlight();

        var viewportCount = PrepareFrame(frame: frame);

        Record(viewportCount: viewportCount);
        m_gpu.QueueSubmitter.SubmitAndWait(commandBufferHandles: [m_commandPool.CommandBufferHandle], deviceContext: m_deviceContext);

        // The wait above completed this frame's pool, so its marks are readable immediately.
        if (m_timingEnabled) {
            Span<ulong> ticks = stackalloc ulong[(int)TimingMarkCount];
            var pool = m_timingPools![(int)(m_timingFrame % 2UL)];

            m_lastFrameGpuMilliseconds = ((m_timingRecorder!.ReadTimestamps(deviceHandle: m_deviceHandle, firstQuery: 0, poolHandle: pool.PoolHandle, queryCount: TimingMarkCount, rawTicks: ticks) < TimingMarkCount)
                ? null
                : m_timingCapabilities.TicksToMilliseconds(startTicks: ticks[0], endTicks: ticks[(int)TimingMarkCount - 1]));
        }

        m_timingFrame++;

        return ReadPixels().ToArray();
    }

    /// <summary>Records and submits one frame FIRE-AND-FORGET — the live node path. Nothing fences the submit: the
    /// HOST's frame pacing (e.g. the launcher's BeginFrame WaitIdle) orders this frame's writes against the previous
    /// frame's reads and the command-buffer reuse. In export mode the consumer lives on another backend with no shared
    /// timeline, so this DOES drain the producer queue (<see cref="IGpuExportableStorageImage.FinalizeForExport"/>)
    /// before the shared handle is handed off.</summary>
    /// <param name="frame">The per-frame data: views (cameras + regions), time, and the dynamic entity transforms.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The frame has zero views or more than the provisioned capacity.</exception>
    /// <exception cref="InvalidOperationException">A pipelined preview frame is still in flight on this engine.</exception>
    public void SubmitFrame(SdfFrame frame) {
        ThrowIfPipelinedFrameInFlight();

        var viewportCount = PrepareFrame(frame: frame);

        Record(viewportCount: viewportCount);
        m_gpu.QueueSubmitter.Submit(commandBufferHandles: [m_commandPool.CommandBufferHandle], deviceContext: m_deviceContext);
        m_exportableImage?.FinalizeForExport();
        m_timingFrame++;
    }

    /// <summary>Records and submits one frame FIRE-AND-FORGET, then issues a NON-BLOCKING FENCED readback of the
    /// composited output — the demo bake-preview path. Neither the compute submit nor the readback copy waits: the
    /// caller polls <see cref="IsFramePixelsReady"/> on a LATER produced frame and, once it is ready, collects the
    /// pixels with <see cref="AcquireFramePixels"/>. This spreads the render + readback across produced frames so the
    /// live in-editor preview never idles the shared present queue mid-sculpt. SINGLE-IN-FLIGHT: only one pipelined
    /// frame may be outstanding, and this path must not be interleaved with <see cref="RenderFrame"/> or
    /// <see cref="SubmitFrame"/> on one engine (all three re-record the single shared command buffer) — mixing them
    /// while a fence is live corrupts the in-flight submission.</summary>
    /// <param name="frame">The per-frame data: views (cameras + regions), time, and the dynamic entity transforms.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The frame has zero views or more than the provisioned capacity.</exception>
    /// <exception cref="InvalidOperationException">A pipelined preview frame is already in flight on this engine.</exception>
    public void SubmitFramePipelined(SdfFrame frame) {
        ThrowIfPipelinedFrameInFlight();

        var viewportCount = PrepareFrame(frame: frame);

        Record(viewportCount: viewportCount);
        // Fire-and-forget compute submit (the SAME call SubmitFrame uses), then the fenced-but-unwaited readback copy.
        // The readback lives on this engine and tracks its own single outstanding fence; the timing path is not driven
        // here (this path is preview-only and never constructed with a timing pool).
        m_gpu.QueueSubmitter.Submit(commandBufferHandles: [m_commandPool.CommandBufferHandle], deviceContext: m_deviceContext);
        m_readback ??= m_gpu.SurfaceTransferFactory.CreateReadback(deviceContext: m_deviceContext);
        m_readback.SubmitRead(
            bytesPerPixel: 4,
            deviceContext: m_deviceContext,
            format: Format,
            height: m_height,
            sourceImageHandle: m_storageImage.ImageHandle,
            width: m_width
        );
        m_pipelinedFrameInFlight = true;
    }

    /// <summary>Polls, WITHOUT blocking, whether the outstanding <see cref="SubmitFramePipelined"/>'s readback has
    /// landed. Fail-safe on a torn-down device (returns <see langword="false"/>, never throws into the render loop).</summary>
    /// <returns>Whether the pipelined frame's pixels are ready to <see cref="AcquireFramePixels"/>.</returns>
    public bool IsFramePixelsReady() =>
        (m_readback?.IsReadComplete() ?? false);

    /// <summary>Collects the pixels the outstanding <see cref="SubmitFramePipelined"/> produced (call only once
    /// <see cref="IsFramePixelsReady"/> is <see langword="true"/>), and clears the single-in-flight guard so the next
    /// pipelined (or waited) frame may be submitted. The returned memory is the readback's reusable staging view —
    /// copy it before the next submit if it must outlive one.</summary>
    /// <returns>The composited output pixels, tightly packed RGBA8, row-major.</returns>
    public ReadOnlyMemory<byte> AcquireFramePixels() {
        var pixels = m_readback!.MapPixels();

        m_pipelinedFrameInFlight = false;

        return pixels;
    }

    /// <summary>Reads the composited output back from the GPU (tightly packed RGBA8, row-major). The returned memory
    /// is the readback's reusable staging view — copy it before the next frame if it must outlive one.</summary>
    /// <returns>The composited output pixels.</returns>
    public ReadOnlyMemory<byte> ReadPixels() {
        m_readback ??= m_gpu.SurfaceTransferFactory.CreateReadback(deviceContext: m_deviceContext);

        return m_readback.Read(
            bytesPerPixel: 4,
            deviceContext: m_deviceContext,
            format: Format,
            height: m_height,
            sourceImageHandle: m_storageImage.ImageHandle,
            width: m_width
        );
    }

    /// <summary>The render passes' labels, in submission order — the names a <see cref="TryReadPassTimings"/> read fills
    /// alongside their milliseconds (pass <c>i</c> spans timing mark <c>i</c>..<c>i+1</c>). A FIXED-COLUMN consumer (the
    /// bench) looks one up by name via <see cref="PassMilliseconds"/>; an ITERATING consumer (the <c>sdf.info</c> verb,
    /// the <c>[world-timing]</c> line) walks them in order, so a future pass surfaces everywhere with no consumer edit.</summary>
    public static ReadOnlySpan<string> PassTimingLabels => PassLabels;
    /// <summary>The number of render passes a <see cref="TryReadPassTimings"/> read reports — the width a caller sizes
    /// its milliseconds span to (<see cref="PassTimingLabels"/> has the same length).</summary>
    public static int PassTimingCount => PassLabels.Length;

    /// <summary>Reads the PREVIOUS frame's per-pass GPU times — for a fire-and-forget host whose frame pacing drains the
    /// device between frames, they are complete with no added stall (the double-buffered pools). Fills
    /// <paramref name="passMilliseconds"/> with one entry per <see cref="PassTimingLabels"/> (same order) and reports the
    /// whole-frame span separately.</summary>
    /// <param name="passMilliseconds">Receives each pass's milliseconds, in <see cref="PassTimingLabels"/> order; must be
    /// at least <see cref="PassTimingCount"/> long.</param>
    /// <param name="passCount">The number of pass entries written (equals <see cref="PassTimingCount"/> on success, 0 otherwise).</param>
    /// <param name="frame">The whole-frame (frame-start → last-close) milliseconds.</param>
    /// <returns>Whether timing is live and the previous frame's marks were readable.</returns>
    public bool TryReadPassTimings(Span<double> passMilliseconds, out int passCount, out double frame) {
        passCount = 0;
        frame = 0.0;

        if (
            !m_timingEnabled ||
            (m_timingFrame == 0UL)
        ) {
            return false;
        }

        var previousPool = m_timingPools![(int)((m_timingFrame + 1UL) % 2UL)];
        Span<ulong> ticks = stackalloc ulong[(int)TimingMarkCount];

        if (m_timingRecorder!.ReadTimestamps(deviceHandle: m_deviceHandle, firstQuery: 0, poolHandle: previousPool.PoolHandle, queryCount: TimingMarkCount, rawTicks: ticks) < TimingMarkCount) {
            return false;
        }

        var count = PassLabels.Length;

        for (var index = 0; (index < count); index++) {
            passMilliseconds[index] = m_timingCapabilities.TicksToMilliseconds(startTicks: ticks[index], endTicks: ticks[(index + 1)]);
        }

        passCount = count;
        frame = m_timingCapabilities.TicksToMilliseconds(startTicks: ticks[0], endTicks: ticks[(int)(TimingMarkCount - 1U)]);

        return (frame > 0.0);
    }
    /// <summary>Looks up a named pass's milliseconds in a <see cref="TryReadPassTimings"/> result. Returns 0 when the
    /// label is absent (a pass renamed or removed), so a FIXED-COLUMN consumer (the bench's beam/views/composite) keeps
    /// comparing across a pass-list change instead of hard-failing on a missing tuple element.</summary>
    /// <param name="passMilliseconds">A filled <see cref="TryReadPassTimings"/> result span.</param>
    /// <param name="passCount">The entry count that read reported.</param>
    /// <param name="label">One of <see cref="PassTimingLabels"/>.</param>
    /// <returns>The pass's milliseconds, or 0 when the label is not present.</returns>
    public static double PassMilliseconds(ReadOnlySpan<double> passMilliseconds, int passCount, string label) {
        for (var index = 0; ((index < passCount) && (index < PassLabels.Length)); index++) {
            if (string.Equals(PassLabels[index], label, StringComparison.Ordinal)) {
                return passMilliseconds[index];
            }
        }

        return 0.0;
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        // WAIT-FREE by contract, so it is safe against a lost device: a fire-and-forget host must drain the device
        // itself before disposing (its per-frame submits are unfenced); the waited path never has work in flight.
        if (m_timingPools is not null) {
            foreach (var pool in m_timingPools) {
                pool.Dispose();
            }
        }

        m_readback?.Dispose();
        m_commandPool.Dispose();
        m_compositeArgsBuffer.Dispose();
        m_cullBoundsBuffer.Dispose();
        m_viewsArgsBuffer.Dispose();
        m_tileBuffer.Dispose();
        m_instanceMaskBuffer.Dispose();
        m_dynamicTransformBuffer.Dispose();
        m_viewportBuffer.Dispose();
        m_programBuffer.Dispose();
        m_screenSurfaceBuffer.Dispose();
        m_screenLightBuffer.Dispose();
        m_beamPipeline.Dispose();
        m_instanceCullPipeline.Dispose();
        m_cullArgsPipeline.Dispose();
        m_viewsPipeline.Dispose();
        m_compositePipeline.Dispose();
        m_descriptorAllocator.DestroySampler(deviceHandle: m_deviceHandle, samplerHandle: m_screenSampler);
        m_descriptorAllocator.DestroyPool(deviceHandle: m_deviceHandle, poolHandle: m_pool);

        foreach (var source in m_sourceTextures) {
            source?.Dispose();
        }

        m_screenSourceFiller.Dispose();
        m_storageImage.Dispose();
        m_beamShaderModule.Dispose();
        m_instanceCullShaderModule.Dispose();
        m_cullArgsShaderModule.Dispose();
        m_viewsShaderModule.Dispose();
        m_compositeShaderModule.Dispose();
    }

    // The shared per-frame front half of both submission paths: validate, (re)bind sources, pack + upload the
    // viewport/transform buffers, and rebuild both push-constant blocks from the LIVE regions (the camera director
    // animates the split layout, so a frozen first-frame layout composited stale/blank rects mid-transition).
    private uint PrepareFrame(SdfFrame frame) {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        var viewportCount = (uint)frame.Views.Count;

        if (
            (0 == viewportCount) ||
            (viewportCount > m_viewportCapacity)
        ) {
            throw new ArgumentException(message: $"This world engine composites 1 to {m_viewportCapacity} viewports; the frame has {viewportCount}.");
        }

        if (frame.DynamicTransforms.Count < m_requiredDynamicTransformCapacity) {
            throw new ArgumentException(message: $"The uploaded SDF program requires {m_requiredDynamicTransformCapacity} dynamic-transform slots; the frame supplies {frame.DynamicTransforms.Count}.", paramName: nameof(frame));
        }

        BindSources(viewportCount: viewportCount);
        BindScreenSources();
        PackViewports(frame: frame, viewportCount: viewportCount);
        m_viewportBuffer.Write<byte>(data: m_viewportScratch);
        PackDynamicTransforms(frame: frame);
        m_dynamicTransformBuffer.Write<byte>(data: m_dynamicTransformScratch);
        // The screen-surface table: UploadProgram seeds the host mirror once; any SetScreenSurface call since the
        // last frame patched it in place — re-uploaded here every frame (like the buffers above) so a screen riding
        // a dynamic transform never renders one frame stale relative to its geometry.
        m_screenSurfaceBuffer.Write<byte>(data: m_screenSurfaceScratch);
        PackScreenLights(frame: frame);
        m_screenLightBuffer.Write<byte>(data: m_screenLightScratch);

        // CompositeParams { uint2 imageExtent; uint2 tileGrid; uint viewportCount; uint childMask; uint screenMask; uint instanceMaskWordCount; } — Stage 0/1 push.
        var pushWords = MemoryMarshal.Cast<byte, uint>(span: m_pushConstant.AsSpan());

        pushWords[0] = m_width; pushWords[1] = m_height; pushWords[2] = m_tileGridX; pushWords[3] = m_tileGridY; pushWords[4] = viewportCount; pushWords[5] = m_childMask; pushWords[6] = m_screenSourceMask; pushWords[7] = (uint)m_liveInstanceMaskWordCount;

        BuildCompositePush(frame: frame);

        return viewportCount;
    }

    // Bind (or rebind when a child's image-view changed) the source array in both the Stage 1 (views) and Stage 2
    // (composite) sets: an SDF source texture for a normal slot, the hosted child's storage image for a child slot.
    // Array elements past the live viewport count duplicate slot 0 (Vulkan requires every bound array element to be a
    // valid descriptor); the kernels never read them.
    private void BindSources(uint viewportCount) {
        var fillerView = SourceViewForSlot(slot: 0);

        for (var element = 0u; (element < MaxViewports); element++) {
            var view = (element < viewportCount) ? SourceViewForSlot(slot: (int)element) : fillerView;

            if (view == m_boundSourceViews[element]) {
                continue;
            }

            m_descriptorAllocator.WriteStorageImage(arrayElement: element, binding: ViewSourceBindingIndex, descriptorSetHandle: m_viewsSet, deviceHandle: m_deviceHandle, imageViewHandle: view);
            m_descriptorAllocator.WriteStorageImage(arrayElement: element, binding: CompositeSourceBindingIndex, descriptorSetHandle: m_compositeSet, deviceHandle: m_deviceHandle, imageViewHandle: view);
            m_boundSourceViews[element] = view;
        }
    }
    // (Re)bind the four screen-source bindings (Stage 1 only) — a slot with no host-supplied source this frame
    // duplicates the DEDICATED ShaderReadOnly filler (m_screenSourceFiller; NOT the sources[] filler BindSources
    // uses, which lives in the General/UAV layout Stage 1/2 read/write it in — aliasing that here would violate the
    // combined-image-sampler binding's required layout the instant any viewport-source dispatch ran). The shader
    // never samples an unbound slot (params.screenMask gates it), so the filler's content never reaches a pixel.
    // Four SCALAR bindings, not one array (see ScreenSourceBindingIndices), so each is written at arrayElement 0.
    private void BindScreenSources() {
        var fillerView = m_screenSourceFiller.ImageViewHandle;

        for (var element = 0u; (element < MaxScreenSurfaces); element++) {
            var view = (0 != m_screenSourceViews[element]) ? m_screenSourceViews[element] : fillerView;

            if (view == m_boundScreenSourceViews[element]) {
                continue;
            }

            m_descriptorAllocator.WriteCombinedImageSampler(arrayElement: 0, binding: ScreenSourceBindingIndices[(int)element], descriptorSetHandle: m_viewsSet, deviceHandle: m_deviceHandle, imageViewHandle: view, samplerHandle: m_screenSampler);
            m_boundScreenSourceViews[element] = view;
        }
    }
    private nint SourceViewForSlot(int slot) {
        if (IsChildSlot(slot: slot)) {
            var view = m_childSourceViews[slot];

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
            floats[b + 12] = camera.Forward.X; floats[b + 13] = camera.Forward.Y; floats[b + 14] = camera.Forward.Z; floats[b + 15] = DebugMode;           // forward.xyz, debug view mode
            floats[b + 16] = region.X; floats[b + 17] = region.Y; floats[b + 18] = region.Width; floats[b + 19] = region.Height;                           // region origin.xy, size.xy
        }
    }

    // Pack each moving entity's rigid transform into the dynamic-transform scratch — 2 float4 per slot: position.xyz
    // (+ pad) then the orientation quaternion (xyzw) — for upload into the buffer SDF_OP_TRANSFORM_DYNAMIC indexes by
    // slot. An empty list is only valid for a program with no dynamic slots (PrepareFrame throws otherwise); it still
    // writes the one always-present slot as identity so the binding stays valid (a static program never reads it).
    // Clamped to the slot capacity the construction options grew the buffer to.
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

    // Pack the per-frame screen-light buffer: entries 0..(MaxScreenSurfaces-1) = each screen's emitted color (the
    // framebuffer average set via SetScreenLight) with the room-glow intensity gain in w, the last entry = the
    // environment (ambient/sun dimming from the frame). KEEP IN SYNC with sdf-world.hlsli's sdfScreenLights layout
    // (SdfScreenLightEnv must equal MaxScreenSurfaces there).
    private void PackScreenLights(SdfFrame frame) {
        var floats = MemoryMarshal.Cast<byte, float>(span: m_screenLightScratch.AsSpan());

        for (var index = 0; (index < MaxScreenSurfaces); index++) {
            var color = m_screenLightColors[index];
            var b = (index * 4);

            floats[b + 0] = color.X; floats[b + 1] = color.Y; floats[b + 2] = color.Z; floats[b + 3] = ScreenLightIntensity;
        }

        var envBase = (MaxScreenSurfaces * 4);

        // The env entry's zw lanes carry the SLICE debug view's plane selector (axis + offset — see
        // SdfFrame.DebugSliceAxis); they were spare pads before, so a frame that never sets them uploads the same zeros.
        floats[envBase + 0] = frame.AmbientScale; floats[envBase + 1] = frame.SunScale; floats[envBase + 2] = frame.DebugSliceAxis; floats[envBase + 3] = frame.DebugSliceOffset;

        // The grid-lock overlay rows (grid-locking §4a): four float4 rows AFTER the env entry (env stays at
        // MaxScreenSurfaces, load-bearing as the shader's screen-count loop bound). Default 0 = no overlay, so a frame
        // that never sets the Grid* fields uploads the same zeros. KEEP IN SYNC with sdf-world.hlsli's SdfGridWorld..
        var gridWorldBase = ((MaxScreenSurfaces + 1) * 4);
        floats[gridWorldBase + 0] = frame.GridFlags; floats[gridWorldBase + 1] = frame.GridFloorY; floats[gridWorldBase + 2] = frame.GridWorldPitch.X; floats[gridWorldBase + 3] = frame.GridWorldPitch.Y;

        var gridObjOriginBase = ((MaxScreenSurfaces + 2) * 4);
        floats[gridObjOriginBase + 0] = frame.GridObjectOrigin.X; floats[gridObjOriginBase + 1] = frame.GridObjectOrigin.Y; floats[gridObjOriginBase + 2] = frame.GridObjectOrigin.Z; floats[gridObjOriginBase + 3] = frame.GridObjectPitch.X;

        var gridObjFrameBase = ((MaxScreenSurfaces + 3) * 4);
        floats[gridObjFrameBase + 0] = frame.GridObjectFrame.X; floats[gridObjFrameBase + 1] = frame.GridObjectFrame.Y; floats[gridObjFrameBase + 2] = frame.GridObjectFrame.Z; floats[gridObjFrameBase + 3] = frame.GridObjectFrame.W;

        // The .z lane is the analytic-normal A/B toggle (0 = the forward-mode dual normal, the default; 1 = the legacy
        // 4-tap finite-difference probe), read by sdf-world.hlsli's worldUseTapNormals. The .w lane is the soft-shadow
        // GRID-CULL toggle (0 = ON, the default grid-gathered shadow march; 1 = OFF, the flat all-instances reference),
        // read by worldShadowCullEnabled. Both were reserved before, so an unset frame uploads 0 = analytic normals +
        // cull ON. KEEP IN SYNC with SdfFrame.UseFiniteDifferenceNormals / SdfFrame.DisableShadowCull.
        var gridObjParamsBase = ((MaxScreenSurfaces + 4) * 4);
        floats[gridObjParamsBase + 0] = frame.GridObjectPitch.Y; floats[gridObjParamsBase + 1] = frame.GridObjectPatchRadius; floats[gridObjParamsBase + 2] = (frame.UseFiniteDifferenceNormals ? 1f : 0f); floats[gridObjParamsBase + 3] = (frame.DisableShadowCull ? 1f : 0f);
    }

    // Stage 2's CompositeParams2 { uint2 imageExtent; uint viewportCount; uint tileGridPacked; float4 rects[5]; }: the
    // LIVE regions drive the layout every frame. word[3] packs the tile grid ((y << 16) | x) so the compositor can
    // flatten empty tiles.
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

    // beam → barrier → cull-args → barrier + indirect-args transition → views (INDIRECT) → barrier → composite
    // (INDIRECT), with the output handed off in its consumer layout.
    private void Record(uint viewportCount) {
        var recorder = m_gpu.ComputeRecorder;
        var commandBuffer = m_commandPool.CommandBufferHandle;
        // After the first frame the OUTPUT rests in its handoff layout: shader-readable when a same-device consumer
        // sampled it, or the cross-backend External layout when it was exported. The first frame starts undefined.
        var restingLayout = m_exportMode ? GpuImageLayout.External : GpuImageLayout.ShaderReadOnly;
        var restingStage = m_exportMode ? GpuComputeStage.ComputeShader : GpuComputeStage.FragmentShader;
        var outputOldLayout = m_imageInitialized ? restingLayout : GpuImageLayout.Undefined;
        var outputSourceAccess = m_imageInitialized ? GpuComputeAccess.ShaderRead : GpuComputeAccess.None;
        var outputSourceStage = m_imageInitialized ? restingStage : GpuComputeStage.TopOfPipe;

        recorder.BeginCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);

        // GPU timing: this frame's double-buffered pool. Reset it, then mark frame-start (top of pipe). The marks are
        // pixel-neutral, so the determinism/capture-hash parity gates are unaffected.
        var timingPool = (m_timingEnabled ? m_timingPools![(int)(m_timingFrame % 2UL)].PoolHandle : 0);

        if (0 != timingPool) {
            m_timingRecorder!.ResetTimestamps(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, firstQuery: 0, poolHandle: timingPool, queryCount: TimingCapacity);
            m_timingRecorder.WriteTimestamp(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, poolHandle: timingPool, queryIndex: 0, stageFlags: GpuTimingStage.TopOfPipe);
        }

        // Instance-cull pass FIRST (mask-first): one invocation per (tile, viewport) — bins the program's instances
        // against each tile's cone into the per-tile mask (the uniform-grid walk, or the flat loop when the program
        // packs no grid). Its OWN kernel so its register footprint never taxes the cone march's occupancy.
        recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, pipelineHandle: m_instanceCullPipeline.Handle);
        recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: m_instanceCullSet, deviceHandle: m_deviceHandle, pipelineLayoutHandle: m_instanceCullPipeline.LayoutHandle);
        recorder.PushConstants(commandBufferHandle: commandBuffer, data: m_pushConstant, deviceHandle: m_deviceHandle, offset: 0, pipelineLayoutHandle: m_instanceCullPipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
        recorder.Dispatch(
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle,
            groupCountX: ((m_tileGridX + (WorkgroupEdge - 1)) / WorkgroupEdge),
            groupCountY: ((m_tileGridY + (WorkgroupEdge - 1)) / WorkgroupEdge),
            groupCountZ: viewportCount
        );

        WriteTimingMark(commandBuffer: commandBuffer, queryIndex: 1, timingPool: timingPool); // close: instance-mask cull

        // Make the instance-mask writes visible to the beam's cone march (it evaluates the tile-masked field).
        recorder.MemoryBarrier(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuComputeAccess.ShaderRead,
            destinationStageMask: GpuComputeStage.ComputeShader,
            deviceHandle: m_deviceHandle,
            sourceAccessMask: GpuComputeAccess.ShaderWrite,
            sourceStageMask: GpuComputeStage.ComputeShader
        );

        // Tile-cull prepass: one invocation per (tile, viewport), cone-marching the tile-MASKED field.
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

        WriteTimingMark(commandBuffer: commandBuffer, queryIndex: 2, timingPool: timingPool); // close: beam prepass

        // Make the beam's tile writes visible to the cull-args reduction's (and Stage 1's) reads — a global memory
        // barrier (the mask writes are already visible from the first barrier; a second global one costs nothing more).
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
        recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, pipelineHandle: m_cullArgsPipeline.Handle);
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
            bufferHandle: m_viewsArgsBuffer.BufferHandle,
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

            // The screen-source filler: ShaderReadOnly once, forever — it is never written, only sampled (or not
            // sampled at all, when every screen slot is bound to a real source).
            recorder.TransitionImageLayout(
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuComputeAccess.ShaderRead,
                destinationStageMask: GpuComputeStage.ComputeShader,
                deviceHandle: m_deviceHandle,
                imageHandle: m_screenSourceFiller.ImageHandle,
                newLayout: GpuImageLayout.ShaderReadOnly,
                oldLayout: GpuImageLayout.Undefined,
                sourceAccessMask: GpuComputeAccess.None,
                sourceStageMask: GpuComputeStage.TopOfPipe
            );
        }

        // Stage 1: render each viewport's SDF camera into its own source texture — dispatched INDIRECTLY from the
        // GPU-computed surviving-tile bbox; the all-empty margins are never dispatched; the kernel offsets each
        // invocation by the bbox origin (binding 8).
        recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, pipelineHandle: m_viewsPipeline.Handle);
        recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: m_viewsSet, deviceHandle: m_deviceHandle, pipelineLayoutHandle: m_viewsPipeline.LayoutHandle);
        recorder.PushConstants(commandBufferHandle: commandBuffer, data: m_pushConstant, deviceHandle: m_deviceHandle, offset: 0, pipelineLayoutHandle: m_viewsPipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
        recorder.DispatchIndirect(
            argumentBufferHandle: m_viewsArgsBuffer.BufferHandle,
            argumentBufferOffset: 0,
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle
        );

        WriteTimingMark(commandBuffer: commandBuffer, queryIndex: 3, timingPool: timingPool); // close: cull-args + Stage 1 views

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
            imageHandle: m_storageImage.ImageHandle,
            newLayout: GpuImageLayout.General,
            oldLayout: outputOldLayout,
            sourceAccessMask: outputSourceAccess,
            sourceStageMask: outputSourceStage
        );

        // Stage 2: composite each source into its screen region (indirect, from the host-written constant grid).
        recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, pipelineHandle: m_compositePipeline.Handle);
        recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: m_compositeSet, deviceHandle: m_deviceHandle, pipelineLayoutHandle: m_compositePipeline.LayoutHandle);
        recorder.PushConstants(commandBufferHandle: commandBuffer, data: m_compositePush, deviceHandle: m_deviceHandle, offset: 0, pipelineLayoutHandle: m_compositePipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
        recorder.DispatchIndirect(
            argumentBufferHandle: m_compositeArgsBuffer.BufferHandle,
            argumentBufferOffset: 0,
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle
        );

        WriteTimingMark(commandBuffer: commandBuffer, queryIndex: 4, timingPool: timingPool); // close: Stage 2 composite

        // Hand the output off in its consumer layout: shader-readable for a same-device consumer (compositor or
        // readback), or the cross-backend External handoff layout. Routing this through the recorder keeps its
        // per-resource state tracking the single source of truth.
        recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuComputeAccess.ShaderRead,
            destinationStageMask: restingStage,
            deviceHandle: m_deviceHandle,
            imageHandle: m_storageImage.ImageHandle,
            newLayout: restingLayout,
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

        m_imageInitialized = true;
    }

    // Writes a bottom-of-pipe closing timestamp for a pass, when timing is on.
    private void WriteTimingMark(nint timingPool, nint commandBuffer, uint queryIndex) {
        if (0 != timingPool) {
            m_timingRecorder!.WriteTimestamp(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, poolHandle: timingPool, queryIndex: queryIndex, stageFlags: GpuTimingStage.BottomOfPipe);
        }
    }
    // The single-in-flight guard shared by all three submission paths: a pipelined frame's fence is still outstanding,
    // so re-recording the one shared command buffer (any submit path) would corrupt the in-flight work. Drain it with
    // AcquireFramePixels first.
    private void ThrowIfPipelinedFrameInFlight() {
        if (m_pipelinedFrameInFlight) {
            throw new InvalidOperationException(message: "A pipelined preview frame is still in flight on this engine; complete it with AcquireFramePixels before submitting another frame (SubmitFramePipelined must not be interleaved with RenderFrame or SubmitFrame on one engine).");
        }
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
