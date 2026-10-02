using System.Numerics;
using Puck.Commands;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>
/// The device-resident half of one SDF frame source (<see cref="SdfWorldResidency"/>): every table the
/// <c>sdf.world</c> kernels read that the frame source writes — the program words, dynamic transforms, frame instance
/// grid, screen surfaces, screen mappings, screen lights, bounded volumes, glyph decals and mesh draws, each a
/// <see cref="GpuRegion"/> — with the glyph atlas, the carve-bake brick pool, the samplers and the pipelines a view's
/// passes record with. A view's scratch and output are its render-graph instance's (<see cref="SdfWorldPasses"/>); the
/// tables are shared by every view of the frame source. Fully backend-neutral through its device's
/// <see cref="IGpuDeviceContext.Services"/>.
/// <para>
/// Each frame the residency packs the frame's rows into the regions host-side (<see cref="Pack"/>), and the first pass
/// recording of the frame submits one upload (<see cref="SubmitUpload"/>): the owed words of every region, a queued
/// host-baked brick and the carve bake's slices, fenced, into the next of <see cref="FrameRingSize"/> upload slots, whose
/// buffers every pass of the frame binds. Before an upload rewrites a slot it waits the previous upload's fence, which
/// signals once every submission queued before it has finished, the views that read the slot two uploads earlier among
/// them. A rewrite of what every slot shares — a grown program or instance grid, a grown mesh region, the glyph atlas, a
/// reloaded kernel — waits the device idle first, since other nodes' submissions read it.
/// </para>
/// </summary>
public sealed partial class SdfWorldTables : IDisposable, ISdfBrickBakeService {
    private const int BrickBakeRequestHeaderFloat4Count = 3; // (boxMin+cellSize), (dims+carveCount), (destWordOffset+invLambda) — KEEP IN SYNC with sdf-brick-bake.comp
    private const uint BrickBakeWorkgroupSize = 64; // sdf-brick-bake.comp's [numthreads(64, 1, 1)]
    private const int DecalBufferCells = (DecalDescriptorCount + (MaxScreenSurfaces * MaxScreenDecalCells));
    // The decal buffer's leading DESCRIPTOR band (one uint4 per screen slot) precedes the shared cell region; a screen's
    // cell run starts at DecalDescriptorCount + screenIndex * MaxScreenDecalCells (KEEP IN SYNC with sdfSampleGlyphDecal).
    private const int DecalDescriptorCount = MaxScreenSurfaces;
    private const int DecalWordsPerCell = 4; // one uint4 per cell/descriptor (KEEP IN SYNC with shade/sdf-environment.hlsli's sdfDecalCells)
    private const int DynamicTransformByteLength = ((sizeof(float) * 4) * 3); // 48-byte rigid transform: float4 position (xyz + .w = soft-shadow participation: 0 casts / 1 shadow-suppressed) + float4 orientation quaternion + float4 anonymous Lanes (DynamicTransform.Lanes) (KEEP IN SYNC with isa/sdf-world.interface.hlsli sdfDynamicTransforms: position.w is read by sdfShadowParticipationActive's per-instance skip in field/sdf-layout.hlsli, the third row by SDF_OP_LANE_ERODE's currentLanes and shade-volumes.hlsli's selected intensity lane)
    // The fillers' format: the views' color format, which the storage image they stand in for declares.
    private const GpuPixelFormat Format = RenderGraphPackageCatalog.WorkingFormat;
    // The glyph atlas's format: RGBA8 coverage and color, as the host rasterizes it.
    private const GpuPixelFormat GlyphAtlasFormat = GpuPixelFormat.R8G8B8A8Unorm;
    private const int MaxBrickBakeVoxelsPerSlice = (256 * 1024); // <= 256K voxels per brick per produced frame: ~1-2 ms background-budget
    private const int MaxBrickCarvesPerBake = 4096; // request-buffer carve capacity per slot (the debug pool's MaxCarves ceiling)
    private const int ScreenLightByteLength = ((sizeof(float) * 4) * MaxScreenSurfaces); // float4 rgb+intensity per screen slot (KEEP IN SYNC with frame/sdf-environment.hlsli sdfScreenLights)
    private const float ScreenLightIntensity = 2.5f; // room-glow gain applied to each screen's average color
    private const int ScreenMappingByteLength = ((sizeof(float) * 4) * 7);
    // The seventh ScreenMappingData row: the bound flag at its first float, the sampler at its second.
    private const int ScreenBoundOffset = ((sizeof(float) * 4) * 6);
    private const int ScreenSamplerOffset = (ScreenBoundOffset + sizeof(float));
    private const int ScreenStateFloats = 4;
    private const int ScreenSurfaceByteLength = ((sizeof(float) * 4) * 3); // 48-byte ScreenSurfaceData: right.xyz+halfWidth, up.xyz+halfHeight, origin.xyz+pad (KEEP IN SYNC with frame/sdf-environment.hlsli)
    // Packed flow/cloud volume stride; paired with shade-volumes.hlsli.
    private const int VolumeByteLength = ((sizeof(float) * 4) * SdfVolume.VectorsPerEntry);
    // The bytes of the dummy buffer a pass binds at a member whose storage it does not touch: one element of the widest
    // member.
    private const ulong DummyBufferBytes = (sizeof(uint) * 4);

    /// <summary>The primary (camera) march's per-pixel step budget. KEEP IN SYNC with <c>MaxSteps</c> in
    /// march/sdf-march-constants.hlsli. Exposed so a host's cost sheet can quote an authored <see cref="SdfFrame.FarDistance"/>
    /// against the budget that has to reach it (a ray skimming open ground at height <c>h</c> takes roughly one step
    /// per <c>h</c> units of depth, so the far distance is that ray's step count per unit of height).</summary>
    public const int PrimaryMarchSteps = 128;
    /// <summary>The default carve-bake brick pool capacity in voxels (f32 words) — <see cref="SdfBrickPoolLayout.TotalVoxels"/>
    /// = 16.7M voxels = 64 MB, i.e. <see cref="SdfBrickPoolLayout.MaxBricks"/> slots at full resolution.</summary>
    public const int DefaultBrickPoolVoxelCapacity = SdfBrickPoolLayout.TotalVoxels;
    /// <summary>The upload ring's depth: how many uploads' regions are resident at once. Each region keeps one buffer per
    /// slot, the frame's passes read the slot its upload wrote, and an upload waits the previous upload's fence before it
    /// rewrites a slot, so the views of the frame before the previous are done with it.</summary>
    public const int FrameRingSize = 2;
    /// <summary>The per-screen glyph decal cell budget: the most glyph cells one screen slot's decal grid may carry
    /// (columns × rows). The authoritative ceiling lives in <see cref="SdfScreenDecalLayout"/>; the decal buffer
    /// partitions its cell region into <see cref="MaxScreenSurfaces"/> equal per-screen runs of this size, so a decal
    /// on one screen never collides with another's cells.</summary>
    public const int MaxScreenDecalCells = SdfScreenDecalLayout.MaxScreenDecalCells;
    /// <summary>The kernels' screen count: the most screen surfaces one program may declare, the same ceiling as
    /// <see cref="Puck.SignedDistance.SdfProgramBuilder.MaxScreenSurfaces"/>, which this reads. It is the length of the
    /// <see cref="SdfWorldPackage.ScreenSources"/> array of <see cref="SdfWorldInterfaces.World"/> and of every
    /// per-screen table, and the kernels read it as the generated <c>SDF_MAX_SCREEN_SURFACES</c>, so it is stated once.
    /// Each screen samples its source through the <see cref="SdfWorldPackage.Samplers"/> element its row's filter
    /// names.</summary>
    public const int MaxScreenSurfaces = Puck.SignedDistance.SdfProgramBuilder.MaxScreenSurfaces;
    /// <summary>The most bounded emissive volumes (<see cref="Puck.SignedDistance.SdfVolume"/>) one rendered frame
    /// carries — the same ceiling as <see cref="Puck.SignedDistance.SdfProgramBuilder.MaxVolumes"/>, which this
    /// reads rather than hand-syncing a second literal.</summary>
    public const int MaxVolumes = Puck.SignedDistance.SdfProgramBuilder.MaxVolumes;

    // The bake pipeline + per-slot request buffers/sets — created ONLY when the pool is enabled (nothing bakes into a
    // filler). Each slot owns a host-visible request buffer (header + carve list) and a static descriptor set binding
    // that buffer + the shared pool (as a UAV). The per-slot state advances one slice per upload (RecordBrickBakeSlices).
    private readonly IGpuComputePipeline? m_brickBakePipeline;
    // The host-baked brick path: a region staging one brick at a time into the brick pool, its external destination,
    // and a queue of pending uploads drained one per upload (RecordBrickUpload). Null without a brick pool.
    private readonly GpuRegion? m_brickRegion;

    private readonly Queue<(int Slot, int Count, float[] Voxels)> m_brickUploads = new();

    // The device's region-copy pipeline, leased from the pass-pipeline cache's GpuRegionCopyPass, which every staged
    // region records its copy with; the tables never own it.
    private readonly IGpuComputePipeline m_regionCopyPipeline;
    // The carve-bake brick pool: one persistent device-local f32 buffer the sliced bake writes and the beam + views
    // kernels sample. Always allocated (a 1-float filler when the pool is disabled), always bound, since both kernels
    // compile the sdfBrickPool binding unconditionally (SDF_SAMPLED_REGIONS).
    private readonly IGpuBuffer m_brickPoolBuffer;
    private readonly bool m_brickPoolEnabled;
    private readonly int m_brickPoolVoxelCapacity;
    private readonly IGpuBindings m_bindings;
    private readonly IGpuDeviceContext m_deviceContext;
    // What a pass binds at a member whose storage it does not touch: a tiny buffer, a 1x1 storage image resting General,
    // and a 1x1 sampled image resting shader-readable, which also stands for an unset glyph atlas.
    private readonly IGpuBuffer m_dummyBuffer;
    private readonly IGpuImage m_storageFiller;
    private readonly IGpuImage m_sampledFiller;
    private readonly int m_dynamicTransformCapacity;
    private readonly GpuDeviceServices m_gpu;

    private int m_instanceCapacity;
    private SdfInstanceGridInput[] m_instanceGridInputScratch;
    private int m_instanceGridWordCapacity;
    private SdfInstanceGrid.Workspace m_instanceGridWorkspace;

    // The tables' descriptor pool: the World sets and the bake sets.
    private readonly nint m_pool;

    // The program region's words, and the words the options provisioned for, which ProgramWordCapacity reports when
    // larger.
    private int m_programWordCapacity;

    private readonly int m_programWordReserve;

    // One sampler per filter, indexed by the filter's value, as SdfWorldPackage.Samplers binds them.
    private readonly nint[] m_samplers = new nint[SdfWorldPackage.SamplerCount];
    private int m_currentSlot = -1;

    private ulong m_decalRevision;
    private bool m_disposed;
    private bool m_fillersInitialized;
    // The SDF_SHAPE_GLYPH font atlas: a STATIC texture uploaded once via SetGlyphAtlas (a re-set re-uploads). Held as an
    // IGpuSurfaceUpload (owns the image + staging + the returned view), and the current sampleable view, or 0 until set:
    // the glyph binding then samples the neutral sampled filler, and every SDF_SHAPE_GLYPH reads the saturated band, so a
    // glyph-free program with no atlas is safe.
    private IGpuSurfaceUpload? m_glyphAtlasUpload;
    private nint m_glyphAtlasView;
    private int m_liveInstanceMaskWordCount;
    // Whether the LIVE uploaded program declares any ScreenSlab shape (bound or not), computed once at UploadProgram. A
    // bound slab's image changes in place under the same view handle, which no packed span the signature hashes sees, so
    // a view showing one renders every frame (TablesSignature).
    private bool m_programDeclaresScreenSlab;
    // Monotonic revisions folded into the signature so a change to a resource NOT re-hashed each frame still invalidates
    // it: m_programRevision bumps on every UploadProgram (program words, live mask width, kernel variant, screen-surface
    // reseed), m_decalRevision on every SetScreenDecal/ClearScreenDecal call that ACTUALLY changes the stored bytes.
    private ulong m_programRevision;
    private bool m_rebuildInstanceGridPerFrame;
    private int m_requiredDynamicTransformCapacity;
    // The uploads submitted, which selects each upload's ring slot.
    private ulong m_uploads;
    private SdfViewsKernelVariant m_viewsVariant;

    private readonly IGpuCommandPool[] m_commandPools = new IGpuCommandPool[FrameRingSize];
    // One per-upload fence per ring slot: an upload waits the previous upload's fence before it rewrites its slot.
    private readonly IGpuSubmissionFence[] m_frameFences = new IGpuSubmissionFence[FrameRingSize];
    // The mapping each screen was last drawn from, compared by reference: a published mapping is republished unchanged
    // while its handle and extent hold, so an unchanged screen packs nothing.
    private readonly SourceMapping?[] m_screenMappings = new SourceMapping?[MaxScreenSurfaces];
    private readonly bool[] m_screenBound = new bool[MaxScreenSurfaces];
    // The screen-light table (each screen's glow color and gain) and the bounded-volume table (views and sky), each packed
    // here every frame and written into its region.
    private readonly byte[] m_screenLightScratch = new byte[ScreenLightByteLength];
    private readonly Vector3[] m_screenLightColors = new Vector3[MaxScreenSurfaces];
    private readonly byte[] m_volumeScratch = new byte[(MaxVolumes * VolumeByteLength)];
    private readonly IGpuStorageBuffer[] m_brickRequestBuffers = new IGpuStorageBuffer[SdfBrickPoolLayout.MaxBricks];
    private readonly nint[] m_brickBakeSets = new nint[SdfBrickPoolLayout.MaxBricks];
    private readonly BrickBakeState[] m_brickStates = new BrickBakeState[SdfBrickPoolLayout.MaxBricks];
    private readonly ulong[] m_brickSerials = new ulong[SdfBrickPoolLayout.MaxBricks];
    private readonly int[] m_brickTotalVoxels = new int[SdfBrickPoolLayout.MaxBricks];
    private readonly int[] m_brickVoxelCursor = new int[SdfBrickPoolLayout.MaxBricks];
    private readonly Vector4[] m_brickRequestScratch = new Vector4[(BrickBakeRequestHeaderFloat4Count + MaxBrickCarvesPerBake)];

    // The baker's frame block and its block, each the same for every brick slot, written once at construction.
    private readonly IGpuStorageBuffer? m_brickBakeFrameBlock;
    private readonly IGpuStorageBuffer? m_brickBakeBlock;
    private readonly nint m_brickBakeFrameSet;

    // The slice ordinal a bake dispatch pushes.
    private readonly byte[] m_brickBakeIndex = new byte[GpuPipelineLayoutDescription.PushIndexBytes];
    private SdfProgram m_liveProgram = null!;

    internal uint[] CopyLiveProgramWords() => m_liveProgram.Words.ToArray();

    /// <summary>Gets or sets the debug-group label wrapping each upload the tables record — the outer scope a GPU capture
    /// (RenderDoc / PIX / Nsight) shows around its copies, brick upload and bake slices. Presentation-only; defaults to
    /// <c>world</c> and never affects rendered output.</summary>
    public string DebugLabel { get; set; } = "world";

    /// <summary>Initializes a new instance of the <see cref="SdfWorldTables"/> class: builds every table, the brick pool
    /// and the samplers against pipelines already built, and uploads the scene program. Creates no pipeline, so it never waits on the driver's pipeline compiler. A
    /// construction that throws partway has released every object it created before the exception leaves the
    /// constructor.</summary>
    /// <param name="device">The GPU device the tables live on; they record through its services, unwrapped.</param>
    /// <param name="pipelines">The pipelines the views' passes record with, leased for <paramref name="device"/>
    /// (<see cref="SdfWorldPipelines.Acquire"/>) and ready (<see cref="SdfWorldPipelines.Poll"/>). The caller keeps
    /// ownership and disposes them after the tables; one set may outlive several tables built from it, but serves one
    /// live residency at a time, since a kernel reload swaps its leases.</param>
    /// <param name="regionCopy">The device's region-copy pipeline, created from <see cref="GpuRegion.CopyPipeline"/> on
    /// <paramref name="device"/>, which the uploads record with. The caller keeps ownership and disposes it after the
    /// tables.</param>
    /// <param name="meshRaster">The device's mesh pass pipeline (<see cref="SdfMeshRasterPass"/>), built on
    /// <paramref name="device"/> with the render pass it draws in, which a view's mesh pass records with. The caller keeps
    /// ownership and disposes it after the tables.</param>
    /// <param name="impostorRaster">The device's impostor card pipeline, built on <paramref name="device"/> with the mesh pass's
    /// layout and render pass, which a view's mesh pass draws impostor cards with after its meshes. The caller keeps
    /// ownership and disposes it after the tables.</param>
    /// <param name="options">The construction options (scene program, capacities, brick pool, work ledger).</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The options enable a brick pool the pipelines were built without, or the mesh
    /// pass pipeline is no graphics pipeline with a render pass.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="pipelines"/> has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The device's descriptor heap cannot admit the tables' pools
    /// (<see cref="CheckAdmission"/>, checked before anything is allocated).</exception>
    public SdfWorldTables(IGpuDeviceContext device, SdfWorldPipelines pipelines, IGpuComputePipeline regionCopy, GpuPassPipeline meshRaster, GpuPassPipeline impostorRaster, SdfWorldTablesOptions options) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(pipelines);
        ArgumentNullException.ThrowIfNull(regionCopy);
        ArgumentNullException.ThrowIfNull(meshRaster);
        ArgumentNullException.ThrowIfNull(impostorRaster);
        ObjectDisposedException.ThrowIf(
            condition: pipelines.IsDisposed,
            instance: pipelines
        );
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Program);

        if ((options.BrickPoolVoxelCapacity > 0) && !pipelines.IncludesBrickPipelines) {
            throw new ArgumentException(message: "The options enable a brick pool, but the pipelines were built without the brick bake and upload pipelines.");
        }

        // Before anything is allocated, so tables the device's descriptor heap cannot hold are refused with nothing to
        // release, and a holder building through SdfWorldPipelineSource.TryBuild records it as a named refusal.
        CheckAdmission(
            device: device,
            options: options
        );

        m_work = (options.WorkLedger ?? new GpuWorkLedger(
            framesInFlight: FrameRingSize,
            name: "gpu.sdf-tables"
        ));

        var gpu = GpuWorkCounting.Wrap(
            ledger: m_work,
            services: device.Services
        );

        m_bindings = gpu.Bindings;
        m_deviceContext = device;
        m_dynamicTransformCapacity = Math.Max(
            val1: Math.Max(
                val1: 1,
                val2: options.DynamicTransformCapacity
            ),
            val2: options.Program.RequiredDynamicTransformCapacity
        );
        m_gpu = gpu;
        m_instanceCapacity = Math.Max(
            val1: options.Program.Instances.Count,
            val2: options.InstanceCapacity
        );
        m_instanceGridInputScratch = new SdfInstanceGridInput[m_instanceCapacity];
        m_instanceGridWorkspace = new SdfInstanceGrid.Workspace(maxInstances: m_instanceCapacity);
        m_instanceGridWordCapacity = SdfInstanceGrid.WordCapacity(maxInstances: m_instanceCapacity);
        m_brickPoolVoxelCapacity = Math.Max(
            val1: 0,
            val2: options.BrickPoolVoxelCapacity
        );
        m_brickPoolEnabled = (m_brickPoolVoxelCapacity > 0);

        // Every object the construction creates joins this scope, so a creation or the program upload that throws releases exactly what was created before it, newest first. Nothing is in flight to drain
        // first: every submission construction makes waits for its completion.
        using var scope = new GpuCreationScope();

        m_pipelines = pipelines;
        m_meshPipeline = (meshRaster.Graphics ?? throw new ArgumentException(message: "The mesh pass pipeline is not a graphics pipeline.", paramName: nameof(meshRaster)));
        m_meshRenderPass = (meshRaster.RenderPass ?? throw new ArgumentException(message: "The mesh pass pipeline names no render pass.", paramName: nameof(meshRaster)));
        m_impostorPipeline = (impostorRaster.Graphics ?? throw new ArgumentException(message: "The impostor pass pipeline is not a graphics pipeline.", paramName: nameof(impostorRaster)));
        m_regionCopyPipeline = regionCopy;
        m_regionCopies = new GpuRegionCopyRecording(
            begin: BeginUpload,
            readers: GpuStage.ComputeShader | GpuStage.VertexShader | GpuStage.FragmentShader,
            recorder: gpu.Recorder
        );

        // What a pass binds at a member it does not use: a sampled image must be shader-readable and a storage image
        // General, which no view's own image can stand in for; both are transitioned once, by the first upload, and never
        // written again.
        m_sampledFiller = scope.Own(created: gpu.ImageFactory.Create(
            name: NameOf(part: "sampled-filler"),
            format: Format,
            height: 1,
            usage: GpuImageUsage.Sampled | GpuImageUsage.Storage,
            width: 1
        ));
        m_storageFiller = scope.Own(created: gpu.ImageFactory.Create(
            name: NameOf(part: "storage-filler"),
            format: Format,
            height: 1,
            usage: GpuImageUsage.Sampled | GpuImageUsage.Storage,
            width: 1
        ));
        m_dummyBuffer = scope.Own(created: gpu.BufferFactory.CreateDeviceLocal(
            name: NameOf(part: "unused-member"),
            sizeBytes: DummyBufferBytes,
            usage: GpuBufferUsage.Storage
        ));

        // The program region holds the live program, not the options' reserve: a probed worst case can run to hundreds
        // of megabytes, which a ring holds once per slot. A larger program grows the region by half again.
        m_programWordReserve = options.ProgramWordCapacity;
        m_programWordCapacity = options.Program.Words.Length;
        // Every region's copy sets, before any region writes them: the copy pool the admission states.
        m_regionCopyPool = ReserveRegionCopyPool(scope: scope);
        // The host-written tables, each a region the kernels bind through its slot's buffer (SdfWorldTables.Regions.cs).
        // The screen-surface table is always MaxScreenSurfaces entries, indexed directly by screen index, so Stage 1's
        // binding stays valid for a program with none: an all-zero undeclared entry is never addressed.
        m_programRegion = scope.Own(created: CreateRegion(
            byteCount: checked((m_programWordCapacity * sizeof(uint))),
            region: ProgramRegionIndex
        ));
        m_dynamicTransformRegion = scope.Own(created: CreateRegion(
            byteCount: checked((m_dynamicTransformCapacity * DynamicTransformByteLength)),
            region: DynamicTransformRegionIndex
        ));
        m_instanceGridRegion = scope.Own(created: CreateRegion(
            byteCount: checked((m_instanceGridWordCapacity * sizeof(uint))),
            region: InstanceGridRegionIndex
        ));
        m_screenSurfaceRegion = scope.Own(created: CreateRegion(
            byteCount: (MaxScreenSurfaces * ScreenSurfaceByteLength),
            region: ScreenSurfaceRegionIndex
        ));
        m_screenMappingRegion = scope.Own(created: CreateRegion(
            byteCount: (MaxScreenSurfaces * ScreenMappingByteLength),
            region: ScreenMappingRegionIndex
        ));
        m_screenLightRegion = scope.Own(created: CreateRegion(
            byteCount: m_screenLightScratch.Length,
            region: ScreenLightRegionIndex
        ));
        m_volumeRegion = scope.Own(created: CreateRegion(
            byteCount: m_volumeScratch.Length,
            region: VolumeRegionIndex
        ));
        m_decalRegion = scope.Own(created: CreateRegion(
            byteCount: ((DecalBufferCells * DecalWordsPerCell) * sizeof(uint)),
            region: DecalRegionIndex
        ));
        m_meshRegion = scope.Own(created: CreateRegion(
            byteCount: SdfMeshRegion.DrawBytes,
            region: MeshRegionIndex
        ));
        m_lightRegion = scope.Own(created: CreateRegion(byteCount: RecordBytes(records: m_lightRecords), region: LightRegionIndex));
        m_skyRegion = scope.Own(created: CreateRegion(byteCount: RecordBytes(records: m_skyRecord), region: SkyRegionIndex));
        m_skyStopRegion = scope.Own(created: CreateRegion(byteCount: RecordBytes(records: m_skyStopRecords), region: SkyStopRegionIndex));
        m_softboxRegion = scope.Own(created: CreateRegion(byteCount: RecordBytes(records: m_softboxRecords), region: SoftboxRegionIndex));
        m_meshRegionBytes = SdfMeshRegion.DrawBytes;
        m_previousDynamicTransforms = scope.Own(created: gpu.BufferFactory.CreateDeviceLocal(
            name: NameOf(part: "previous-dynamic-transforms"), sizeBytes: ((ulong)m_dynamicTransformRegion.ByteCount), usage: GpuBufferUsage.Storage));
        m_previousMeshTransforms = scope.Own(created: gpu.BufferFactory.CreateDeviceLocal(
            name: NameOf(part: "previous-mesh-transforms"), sizeBytes: MeshMatrixBytes, usage: GpuBufferUsage.Storage));

        // The carve-bake brick pool: one persistent DEVICE-LOCAL f32 buffer — device-local so the bake kernel can write it
        // as a UAV (an upload heap forbids UAVs on Direct3D 12) and the beam/views sample it as an SRV. Frozen at the
        // constructed capacity. When the pool is disabled (capacity 0) a single-float filler keeps the always-present
        // sdfBrickPool binding valid — the kernels compile the binding unconditionally, and sdfSampledRegion detects the
        // filler by its element count and renders SampledRegion programs via the conservative uncarved-hull fallback
        // (only RequestBrickBake stays rejected without a pool).
        m_brickPoolBuffer = scope.Own(created: gpu.BufferFactory.CreateDeviceLocal(
            name: NameOf(part: "brick-pool"),
            sizeBytes: checked((((ulong)Math.Max(
                val1: 1,
                val2: m_brickPoolVoxelCapacity
            )) * sizeof(float))),
            usage: GpuBufferUsage.Storage
        ));
        // The carve-bake baker and the host-baked brick staging run only when the pool is enabled (nothing bakes into a
        // filler). The staging region holds one brick, the most one upload carries, and lands it at the brick's slot.
        m_brickBakePipeline = (m_brickPoolEnabled
            ? pipelines.OptionalPipeline(kernel: SdfKernel.BrickBake)
            : null
        );
        m_brickRegion = (m_brickPoolEnabled
            ? scope.Own(created: new GpuRegion(
                bindings: gpu.Bindings,
                buffers: gpu.BufferFactory,
                byteCount: (Math.Min(
                    val1: SdfBrickPoolLayout.VoxelsPerBrick,
                    val2: m_brickPoolVoxelCapacity
                ) * sizeof(float)),
                copyPipeline: m_regionCopyPipeline,
                copySets: m_regionCopyPool.Region(index: BrickStagingRegionIndex),
                destination: m_brickPoolBuffer,
                name: RegionName(region: BrickStagingRegionIndex),
                recorder: gpu.Recorder,
                slotCount: FrameRingSize
            ))
            : null
        );

        // The tables' pool: the World set per ring slot, and with a brick pool the frame set the baker shares and one bake
        // set per brick slot. The sets allocated from the pool are released with it.
        m_pool = m_bindings.CreatePool(
            name: NameOf(part: "descriptors"),
            sizes: DescriptorPoolSizes(brickPool: m_brickPoolEnabled)
        );
        _ = scope.Own(
            handle: m_pool,
            release: m_bindings.DestroyPool
        );

        var worldGroups = pipelines.Pipeline(kernel: SdfKernel.Beam).GroupLayoutHandles;

        // The World set per ring slot, which every view's compute passes bind, written the first time one is bound
        // (WorldSet).
        for (var slot = 0; (slot < FrameRingSize); slot++) {
            m_worldSets[slot] = m_bindings.AllocateSet(
                name: NameOf(
                    detail: "world group",
                    index: slot,
                    part: "tables"
                ),
                descriptorSetLayoutHandle: worldGroups[((int)WorldGroup)],
                poolHandle: m_pool
            );
        }

        // One sampler per filter; a screen samples its source through the one its row chooses and the glyph atlas through
        // the nearest one. The World sets bind them (WriteWorldSet).
        foreach (var filter in Enum.GetValues<GpuSamplerFilter>()) {
            m_samplers[((int)filter)] = scope.Own(
                handle: m_bindings.CreateSampler(filter: filter),
                release: m_bindings.DestroySampler
            );
        }

        for (var slot = 0; (slot < FrameRingSize); slot++) {
            m_commandPools[slot] = scope.Own(created: gpu.CommandPoolFactory.Create(name: NameOf(
                part: "commands",
                index: slot
            )));
            m_frameFences[slot] = scope.Own(created: gpu.QueueSubmitter.CreateSubmissionFence());
        }

        // The carve-bake baker's per-slot request buffers + static descriptor sets (only when the pool is enabled). Each
        // slot owns a host-visible request buffer (header + up to MaxBrickCarvesPerBake carves) and a set binding that
        // buffer and the shared pool, beside the blocks every bake set shares. These are NOT per ring slot: a bake spans
        // uploads and RequestBrickBake drains the ring before rewriting a request buffer, and only uploads read them.
        if (m_brickPoolEnabled) {
            var frameBlock = scope.Own(created: gpu.BufferFactory.CreateHostVisible(
                name: NameOf(detail: "frame block", part: "brick-bake"),
                sizeBytes: ((ulong)UniformBytes(blockBytes: SdfWorldInterfaces.BrickBakeParameters.FrameBlockSizeBytes)),
                usage: GpuBufferUsage.Uniform
            ));
            var frameBlockBytes = new byte[SdfWorldInterfaces.BrickBakeParameters.FrameBlockSizeBytes];

            SdfWorldInterfaces.BrickBakeParameters.WriteFrame(
                block: frameBlockBytes,
                extent: default,
                frame: 0UL,
                values: default
            );
            frameBlock.Write<byte>(data: frameBlockBytes);
            m_brickBakeFrameBlock = frameBlock;
            m_brickBakeFrameSet = m_bindings.AllocateSet(
                name: NameOf(detail: "frame group", part: "brick-bake"),
                descriptorSetLayoutHandle: m_brickBakePipeline!.GroupLayoutHandles[((int)FrameGroup)],
                poolHandle: m_pool
            );
            m_bindings.WriteConstantBuffer(
                arrayElement: 0,
                binding: 0,
                bufferHandle: frameBlock.BufferHandle,
                bufferSize: frameBlock.SizeBytes,
                descriptorSetHandle: m_brickBakeFrameSet
            );

            var bakeBlockBytes = new byte[SdfWorldInterfaces.BrickBakeParameters.SizeBytes];
            var bakeBlock = scope.Own(created: gpu.BufferFactory.CreateHostVisible(
                name: NameOf(detail: "block", part: "brick-bake"),
                sizeBytes: ((ulong)UniformBytes(blockBytes: SdfWorldInterfaces.BrickBakeParameters.SizeBytes)),
                usage: GpuBufferUsage.Uniform
            ));

            // The block's extent is one slice as one row: the voxels one bake dispatch writes at most.
            SdfWorldInterfaces.BrickBakeParameters.WriteExtent(
                block: bakeBlockBytes,
                height: 1u,
                width: ((uint)MaxBrickBakeVoxelsPerSlice)
            );
            bakeBlock.Write<byte>(data: bakeBlockBytes);
            m_brickBakeBlock = bakeBlock;

            for (var brick = 0; (brick < SdfBrickPoolLayout.MaxBricks); brick++) {
                var requestBuffer = scope.Own(created: gpu.BufferFactory.CreateHostVisible(
                    name: NameOf(detail: "request", index: brick, part: "brick-bake"),
                    sizeBytes: (((ulong)m_brickRequestScratch.Length) * (sizeof(float) * 4)),
                    usage: GpuBufferUsage.Storage
                ));

                m_brickRequestBuffers[brick] = requestBuffer;

                var bakeSet = m_bindings.AllocateSet(
                    name: NameOf(part: "brick-bake", index: brick),
                    descriptorSetLayoutHandle: m_brickBakePipeline!.GroupLayoutHandles[((int)PassGroup)],
                    poolHandle: m_pool
                );

                m_brickBakeSets[brick] = bakeSet;
                m_bindings.WriteConstantBuffer(
                    arrayElement: 0,
                    binding: 0,
                    bufferHandle: bakeBlock.BufferHandle,
                    bufferSize: bakeBlock.SizeBytes,
                    descriptorSetHandle: bakeSet
                );
                WriteInterfaceBuffer(buffer: requestBuffer, layout: SdfWorldInterfaces.BrickBakeLayout, member: SdfKernelInterfaces.BakeRequest, set: bakeSet);
                WriteInterfaceBuffer(buffer: m_brickPoolBuffer, layout: SdfWorldInterfaces.BrickBakeLayout, member: SdfKernelInterfaces.BakePool, set: bakeSet);
            }
        }

        // The "uploaded once" seam: the program (and its screen-surface table) is uploaded here and normally never
        // again — frames move entities by rewriting only the small dynamic-transform buffer. UploadProgram is the
        // single owner of per-program derived state (its capacity checks trivially pass for the construction program).
        UploadProgram(program: options.Program);
        scope.Complete();
    }

    // Writes a buffer at a resource of an interface whose group layouts the set was allocated against, as the kind its
    // member declares and at its element's stride (a record's or a value type's), the structured view the kernel's
    // generated declaration reads on Direct3D 12.
    internal void WriteInterfaceBuffer(nint set, ShaderInterfaceLayout layout, string member, IGpuBuffer buffer) {
        var resource = SdfKernelInterfaces.ResourceOf(layout: layout, member: member);

        m_bindings.WriteBuffer(
            binding: resource.Binding,
            bufferHandle: buffer.BufferHandle,
            bufferSize: buffer.SizeBytes,
            descriptorSetHandle: set,
            elementStride: (resource.Member.Structure?.SizeBytes ?? resource.Member.Type!.Value.SizeBytes()),
            kind: resource.Kind
        );
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        // Drain the device BEFORE destroying anything (tolerating an already-lost device, where there is nothing left to
        // drain): the views' submissions, which the tables' own fences cannot see, read every table.
        m_deviceContext.TryWaitIdle();

        for (var slot = 0; (slot < FrameRingSize); slot++) {
            m_frameFences[slot].Dispose();
            m_commandPools[slot].Dispose();
        }

        DisposeRegions();
        m_previousDynamicTransforms.Dispose();
        m_previousMeshTransforms.Dispose();
        m_brickBakeFrameBlock?.Dispose();
        m_brickBakeBlock?.Dispose();

        foreach (var requestBuffer in m_brickRequestBuffers) {
            requestBuffer?.Dispose();
        }

        m_brickPoolBuffer.Dispose();

        foreach (var sampler in m_samplers) {
            m_bindings.DestroySampler(samplerHandle: sampler);
        }

        m_bindings.DestroyPool(poolHandle: m_pool);

        m_dummyBuffer.Dispose();
        m_storageFiller.Dispose();
        m_sampledFiller.Dispose();
        m_glyphAtlasUpload?.Dispose();
        DisposeMeshAtlases();
    }
}
