using Puck.Commands;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>
/// The device-resident half of one SDF frame source (<see cref="SdfWorldResidency"/>): every table the
/// <c>sdf.world</c> kernels read that the frame source writes — the program words, dynamic transforms, screen surfaces,
/// screen mappings, screen lights, bounded volumes, glyph decals and mesh draws, each a <see cref="GpuRegion"/> — with
/// the glyph atlas, the samplers and the pipelines a view's
/// passes record with. A view's scratch and output are its render-graph instance's (<see cref="SdfWorldPasses"/>); the
/// tables are shared by every view of the frame source. Fully backend-neutral through its device's
/// <see cref="IGpuDeviceContext.Services"/>.
/// <para>
/// Each frame the residency packs the frame's rows into the regions host-side (<see cref="Pack"/>), and its staging,
/// before any pass records, submits one upload (<see cref="SubmitUpload"/>): the owed words of every region, fenced, into
/// the next of <see cref="FrameRingSize"/> upload slots, whose
/// buffers every pass of the frame binds. Before an upload rewrites a slot it waits the previous upload's fence, which
/// signals once every submission queued before it has finished, the views that read the slot two uploads earlier among
/// them. A rewrite of what every slot shares — a grown program, a grown mesh region, the glyph atlas, a
/// reloaded kernel — waits the device idle first, since other nodes' submissions read it.
/// </para>
/// </summary>
public sealed partial class SdfWorldTables : IDisposable {
    private const int DecalBufferCells = (DecalDescriptorCount + (MaxScreenSurfaces * MaxScreenDecalCells));
    // The decal buffer's leading DESCRIPTOR band (one uint4 per screen slot) precedes the shared cell region; a screen's
    // cell run starts at DecalDescriptorCount + screenIndex * MaxScreenDecalCells (KEEP IN SYNC with sdfSampleGlyphDecal).
    private const int DecalDescriptorCount = MaxScreenSurfaces;
    private const int DecalWordsPerCell = 4; // one uint4 per cell/descriptor (KEEP IN SYNC with shade/sdf-environment.hlsli's sdfDecalCells)
    private const int DynamicTransformByteLength = ((sizeof(float) * 4) * 3); // 48-byte rigid transform: float4 position (xyz + .w = the slot's SdfUpdateRateCode while a tier view shows, zero otherwise) + float4 orientation quaternion + float4 anonymous Lanes (DynamicTransform.Lanes) (KEEP IN SYNC with isa/sdf-world.interface.hlsli sdfDynamicTransforms: position.w is read by sdfRateCode in debug/sdf-debug-views.hlsli, the third row by SDF_OP_LANE_ERODE's currentLanes and shade-volumes.hlsli's selected intensity lane)
    // The fillers' format: the views' color format, which the storage image they stand in for declares.
    private const GpuPixelFormat Format = RenderGraphPackageCatalog.WorkingFormat;
    // The glyph atlas's format: RGBA8 coverage and color, as the host rasterizes it.
    private const GpuPixelFormat GlyphAtlasFormat = GpuPixelFormat.R8G8B8A8Unorm;
    private const int ScreenMappingByteLength = ((sizeof(float) * 4) * 9);
    // A screen's record in the mapping region: its ScreenMappingByteLength mapping, then its ripple record, which the
    // emission and sky-environment change checks leave out because it moves every frame and lights nothing. KEEP IN SYNC
    // with WorldScreenMappingRows in frame/sdf-screen-mapping.hlsli.
    private const int ScreenMappingStride = (ScreenMappingByteLength + ScreenRippleByteLength);
    private const int ScreenRippleOffset = ScreenMappingByteLength;
    private const int ScreenRippleByteLength = ((sizeof(float) * 4) * SdfScreenRipple.Rows);
    // The ripple record's first row's third float: the screen's SdfUpdateRateCode while a tier view shows. KEEP IN SYNC
    // with sdfRateCode in debug/sdf-debug-views.hlsli.
    private const int ScreenRateOffset = (ScreenRippleOffset + (sizeof(float) * 2));
    // The seventh ScreenMappingData row: bound, sampler and emission flags in its first three floats, the face in its
    // fourth.
    private const int ScreenBoundOffset = ((sizeof(float) * 4) * 6);
    private const int ScreenSamplerOffset = (ScreenBoundOffset + sizeof(float));
    // The seventh row's fourth float: the screen's SdfScreenFace. KEEP IN SYNC with ScreenMappingData.state.w in
    // frame/sdf-screen-mapping.hlsli.
    private const int ScreenFaceOffset = (ScreenBoundOffset + (sizeof(float) * 3));
    private const int ScreenStateFloats = 4;
    // The eighth and ninth ScreenMappingData rows: the display treatment (SdfScreenTreatment). KEEP IN SYNC with
    // ScreenMappingData.treatment and .treatmentShape in frame/sdf-screen-mapping.hlsli.
    private const int ScreenTreatmentOffset = ((sizeof(float) * 4) * 7);
    private const int ScreenTreatmentFloats = 8;
    private const int ScreenSurfaceByteLength = ((sizeof(float) * 4) * 3); // 48-byte ScreenSurfaceData: right.xyz+halfWidth, up.xyz+halfHeight, origin.xyz+pad (KEEP IN SYNC with frame/sdf-environment.hlsli)
    // Packed flow/cloud volume stride; paired with shade-volumes.hlsli.
    private const int VolumeByteLength = ((sizeof(float) * 4) * SdfVolume.VectorsPerEntry);
    // The bytes of the dummy buffer a pass binds at a member whose storage it does not touch: one element of the widest
    // member.
    private const ulong DummyBufferBytes = (sizeof(uint) * 4);

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

    // The device's region-copy pipeline, leased from the pass-pipeline cache's GpuRegionCopyPass, which every staged
    // region records its copy with; the tables never own it.
    private readonly IGpuComputePipeline m_regionCopyPipeline;

    private readonly IGpuBindings m_bindings;
    private readonly IGpuDeviceContext m_deviceContext;
    // What a pass binds at a member whose storage it does not touch: a tiny buffer, a 1x1 storage image resting General,
    // and a 1x1 sampled image resting shader-readable, which also stands for an unset glyph atlas.
    private readonly IGpuBuffer m_dummyBuffer;
    private readonly IGpuImage m_storageFiller;
    private readonly IGpuImage m_sampledFiller;

    private int m_dynamicTransformCapacity;

    private readonly GpuDeviceServices m_gpu;
    private readonly SdfGpuWork m_gpuWork;

    /// <summary>Gets the tables' background GPU work: work that is not a frame's, submitted on completion of the last
    /// rather than once a frame, which binds the latest upload's World set (the mesh bake's sampling among it).</summary>
    public SdfGpuWork GpuWork => m_gpuWork;
    // The device's own services, which the background GPU work's clients create their objects through.
    internal GpuDeviceServices Gpu => m_deviceContext.Services;

    private int m_instanceCapacity;

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
    // it: program revision covers every changed packed program; geometry revision excludes only palette values.
    // Decal revision covers SetScreenDecal/ClearScreenDecal calls that actually change the stored bytes.
    private ulong m_programRevision;
    private ulong m_programGeometryRevision;
    private int m_requiredDynamicTransformCapacity;
    // The uploads submitted, which selects each upload's ring slot.
    private ulong m_uploads;

    private readonly IGpuCommandPool[] m_commandPools = new IGpuCommandPool[FrameRingSize];
    // One per-upload fence per ring slot: an upload waits the previous upload's fence before it rewrites its slot.
    private readonly IGpuSubmissionFence[] m_frameFences = new IGpuSubmissionFence[FrameRingSize];
    // The mapping each screen was last drawn from, compared by reference: a published mapping is republished unchanged
    // while its handle and extent hold, so an unchanged screen packs nothing.
    private readonly SourceMapping?[] m_screenMappings = new SourceMapping?[MaxScreenSurfaces];
    private readonly SdfScreenFace[] m_screenFaces = new SdfScreenFace[MaxScreenSurfaces];
    private readonly SdfScreenTreatment[] m_screenTreatments = new SdfScreenTreatment[MaxScreenSurfaces];
    private readonly bool[] m_screenBound = new bool[MaxScreenSurfaces];
    // Each screen's SdfUpdateRateCode as SetScreenRate wrote it, which SetScreenRipple's rewrite of the record keeps.
    private readonly float[] m_screenRates = new float[MaxScreenSurfaces];
    // The bounded-volume table (views and sky), packed here every frame and written into its region.
    private readonly byte[] m_volumeScratch = new byte[(MaxVolumes * VolumeByteLength)];
    private SdfProgram m_liveProgram = null!;

    internal uint[] CopyLiveProgramWords() => m_liveProgram.Words.ToArray();

    /// <summary>Gets or sets the debug-group label wrapping each upload the tables record — the outer scope a GPU capture
    /// (RenderDoc / PIX / Nsight) shows around its copies. Presentation-only; defaults to
    /// <c>world</c> and never affects rendered output.</summary>
    public string DebugLabel { get; set; } = "world";

    /// <summary>Initializes a new instance of the <see cref="SdfWorldTables"/> class: builds every table and the samplers against pipelines already built, and uploads the scene program. Creates no pipeline, so it never waits on the driver's pipeline compiler. A
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
    /// <param name="shadowRaster">The device's shadow cascade pipeline, built on <paramref name="device"/> with the mesh
    /// pass's layout and the depth-only render pass a view's cascades draw in. The caller keeps ownership and disposes it
    /// after the tables.</param>
    /// <param name="shadowExitRaster">The device's shadow cascade exit-layer pipeline
    /// (<see cref="SdfMeshRasterPass.ShadowExitDescription"/>), built on <paramref name="device"/> with the shadow
    /// pipeline's layout and render pass. The caller keeps ownership and disposes it after the tables.</param>
    /// <param name="glassRaster">The device's glass raster pipeline, built on <paramref name="device"/> with the mesh
    /// pass's layout and render pass, which a view's glass pass draws the glass draws with. The caller keeps ownership
    /// and disposes it after the tables.</param>
    /// <param name="occlusion">The device's mesh pass occlusion kernel (<see cref="SdfMeshRasterPass.OcclusionKey"/>),
    /// built on <paramref name="device"/> with the mesh pass's layout. The caller keeps ownership and disposes it after
    /// the tables.</param>
    /// <param name="options">The construction options (scene program, capacities, work ledger).</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The mesh pass pipeline is no graphics pipeline with a render pass, or the occlusion kernel is no compute pipeline.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="pipelines"/> has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The device's descriptor heap cannot admit the tables' pools
    /// (<see cref="CheckAdmission"/>, checked before anything is allocated).</exception>
    /// <param name="shadowGlassRaster">The cascade glass extinction pipeline.</param>
    public SdfWorldTables(IGpuDeviceContext device, SdfWorldPipelines pipelines, IGpuComputePipeline regionCopy, GpuPassPipeline meshRaster, GpuPassPipeline shadowRaster, GpuPassPipeline shadowExitRaster, GpuPassPipeline shadowGlassRaster, GpuPassPipeline glassRaster, GpuPassPipeline occlusion, SdfWorldTablesOptions options) {
        m_meshesOwed = new OwedMeshes(named: m_meshArena.Named);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(pipelines);
        ArgumentNullException.ThrowIfNull(regionCopy);
        ArgumentNullException.ThrowIfNull(meshRaster);
        ArgumentNullException.ThrowIfNull(shadowRaster);
        ArgumentNullException.ThrowIfNull(shadowExitRaster);
        ArgumentNullException.ThrowIfNull(glassRaster);
        ArgumentNullException.ThrowIfNull(occlusion);
        ObjectDisposedException.ThrowIf(
            condition: pipelines.IsDisposed,
            instance: pipelines
        );
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Program);

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
        m_skyDetails = (options.SkyDetails ?? new SdfSkyDetails());

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

        // Every object the construction creates joins this scope, so a creation or the program upload that throws releases exactly what was created before it, newest first. Nothing is in flight to drain
        // first: every submission construction makes waits for its completion.
        using var scope = new GpuCreationScope();

        m_pipelines = pipelines;
        m_meshPipeline = (meshRaster.Graphics ?? throw new ArgumentException(message: "The mesh pass pipeline is not a graphics pipeline.", paramName: nameof(meshRaster)));
        m_meshRenderPass = (meshRaster.RenderPass ?? throw new ArgumentException(message: "The mesh pass pipeline names no render pass.", paramName: nameof(meshRaster)));
        m_shadowPipeline = (shadowRaster.Graphics ?? throw new ArgumentException(message: "The shadow pass pipeline is not a graphics pipeline.", paramName: nameof(shadowRaster)));
        m_shadowRenderPass = (shadowRaster.RenderPass ?? throw new ArgumentException(message: "The shadow pass pipeline names no render pass.", paramName: nameof(shadowRaster)));
        m_shadowExitPipeline = (shadowExitRaster.Graphics ?? throw new ArgumentException(message: "The shadow exit pipeline is not a graphics pipeline.", paramName: nameof(shadowExitRaster)));
        m_shadowGlassPipeline = shadowGlassRaster.Graphics ?? throw new ArgumentException("The shadow glass pipeline is not a graphics pipeline.", nameof(shadowGlassRaster));
        m_glassPipeline = (glassRaster.Graphics ?? throw new ArgumentException(message: "The glass pass pipeline is not a graphics pipeline.", paramName: nameof(glassRaster)));
        m_occlusionPipeline = (occlusion.Compute ?? throw new ArgumentException(message: "The mesh pass occlusion kernel is not a compute pipeline.", paramName: nameof(occlusion)));
        m_regionCopyPipeline = regionCopy;
        m_regionCopies = new GpuRegionCopyRecording(
            begin: BeginUpload,
            readers: RegionReaders,
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
        m_screenSurfaceRegion = scope.Own(created: CreateRegion(
            byteCount: (MaxScreenSurfaces * ScreenSurfaceByteLength),
            region: ScreenSurfaceRegionIndex
        ));
        m_screenMappingRegion = scope.Own(created: CreateRegion(
            byteCount: (MaxScreenSurfaces * ScreenMappingStride),
            region: ScreenMappingRegionIndex
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
        m_skyLayerRegion = scope.Own(created: CreateRegion(byteCount: RecordBytes(records: m_skyLayerRecords), region: SkyLayerRegionIndex));
        // The frame's sampled table starts a few words long and grows to the first program with a height field.
        m_frameSampledRegion = scope.Own(created: CreateRegion(byteCount: 256, region: FrameSampledRegionIndex));
        m_shadowHandoffBuffer = scope.Own(created: gpu.BufferFactory.CreateDeviceLocal(
            name: RegionName(region: ShadowHandoffRegionIndex), sizeBytes: ((ulong)RecordBytes(records: m_shadowHandoffs)), usage: GpuBufferUsage.Storage));
        m_shadowHandoffRegion = scope.Own(created: new GpuRegion(
            destination: m_shadowHandoffBuffer, byteCount: RecordBytes(records: m_shadowHandoffs), slotCount: FrameRingSize,
            buffers: gpu.BufferFactory, bindings: gpu.Bindings, recorder: gpu.Recorder, copyPipeline: m_regionCopyPipeline,
            name: RegionName(region: ShadowHandoffRegionIndex), copySets: m_regionCopyPool.Region(index: ShadowHandoffRegionIndex)));
        m_meshRegionBytes = SdfMeshRegion.DrawBytes;
        ShadowMeshRecords();
        m_previousDynamicTransforms = scope.Own(created: gpu.BufferFactory.CreateDeviceLocal(
            name: NameOf(part: "previous-dynamic-transforms"), sizeBytes: ((ulong)m_dynamicTransformRegion.ByteCount), usage: GpuBufferUsage.Storage));
        m_previousMeshTransforms = scope.Own(created: gpu.BufferFactory.CreateDeviceLocal(
            name: NameOf(part: "previous-mesh-transforms"), sizeBytes: MeshMatrixBytes, usage: GpuBufferUsage.Storage));
        m_meshHistoryCopies = scope.Own(created: new GpuBufferStridedCopy(buffers: gpu.BufferFactory, bindings: gpu.Bindings,
            recorder: gpu.Recorder, pipeline: m_regionCopyPipeline, frameSlotCount: FrameRingSize, recordingCount: 2,
            name: NameOf(part: "mesh-history-copy")));

        // The tables' pool: the World set per ring slot. The sets allocated from the pool are released with it.
        m_pool = m_bindings.CreatePool(
            name: NameOf(part: "descriptors"),
            sizes: DescriptorPoolSizes
        );
        _ = scope.Own(
            handle: m_pool,
            release: m_bindings.DestroyPool
        );

        var worldGroups = pipelines.Pipeline(kernel: SdfKernel.Surface).GroupLayoutHandles;

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
        // The background GPU work records and submits through the device's own services: its submissions are no upload
        // of the tables' ledger.
        m_gpuWork = scope.Own(created: new SdfGpuWork(device: device, name: ObjectOwner));
        // The mesh region's owed words land through it (SdfWorldTables.MeshLanding.cs).
        m_gpuWork.Add(client: new MeshLanding(tables: this));

        // The sky's environment, which the World sets bind and the graph's shared producer renders after upload.
        // Its constructor joins each object it creates to the scope, which releases them should a later step throw.
        m_skyEnvironment = new SkyEnvironmentPass(
            gpu: gpu,
            scope: scope,
            tables: this
        );
        m_screenEmission = new ScreenEmissionPass(gpu: gpu, scope: scope, tables: this);

        // The "uploaded once" seam: the program (and its screen-surface table) is uploaded here and normally never
        // again — frames move entities by rewriting only the small dynamic-transform buffer. UploadProgram is the
        // single owner of per-program derived state (its capacity checks trivially pass for the construction program).
        UploadProgram(program: options.Program);
        scope.Complete();
    }

    // Writes a buffer at a resource of an interface whose group layouts the set was allocated against, as the kind its
    // member declares and at its element's stride (a record's or a value type's), the structured view the kernel's
    // generated declaration reads on Direct3D 12.
    internal void WriteInterfaceBuffer(nint set, ShaderInterfaceLayout layout, string member, IGpuBuffer buffer, IGpuBindings? bindings = null) {
        var resource = SdfKernelInterfaces.ResourceOf(layout: layout, member: member);

        (bindings ?? m_bindings).WriteBuffer(
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
        if (m_meshTrimListening) { Puck.Hosting.Jobs.MemoryAccount.Shared.TrimRequested -= RequestMeshTrim; m_meshTrimListening = false; }
        if (Interlocked.Exchange(location1: ref m_meshTrimRequested, value: 0) != 0) { Puck.Hosting.Jobs.MemoryAccount.Shared.CompleteDeferredTrim(); }

        // Drain the device BEFORE destroying anything (tolerating an already-lost device, where there is nothing left to
        // drain): the views' submissions, which the tables' own fences cannot see, read every table.
        m_deviceContext.TryWaitIdle();
        m_gpuWork.Dispose();
        m_meshClaims?.Dispose(); m_meshClaims = null;
        m_meshesOwed.Clear();
        ReleaseUploadNeeds();
        m_meshArena.Dispose();
        DisposeRetired(completed: ulong.MaxValue);

        for (var slot = 0; (slot < FrameRingSize); slot++) {
            m_frameFences[slot].Dispose();
            m_commandPools[slot].Dispose();
        }

        DisposeRegions();
        m_previousDynamicTransforms.Dispose();
        m_previousMeshTransforms.Dispose();
        m_meshHistoryCopies.Dispose();
        m_skyEnvironment.Dispose();
        m_screenEmission.Dispose();

        foreach (var sampler in m_samplers) {
            m_bindings.DestroySampler(samplerHandle: sampler);
        }

        m_bindings.DestroyPool(poolHandle: m_pool);

        m_dummyBuffer.Dispose();
        m_storageFiller.Dispose();
        m_sampledFiller.Dispose();
        m_glyphAtlasUpload?.Dispose();
    }
}
