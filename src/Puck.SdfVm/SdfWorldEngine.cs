using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>
/// The device-explicit core of the compute SDF world pipeline — the one truth for its buffer/push/binding layouts.
/// One instance owns a scene program (uploaded to the GPU once, at construction) plus every pipeline/buffer/image the
/// ten kernels need, and runs the full chain per frame: <c>region-copy.comp</c> (copies each staged region's owed
/// words to its device-local buffer) → <c>sdf-sky.comp</c> (fills every source pixel with the
/// authored sky, direct — a beam-culled tile's pixel is otherwise never touched by any later pass) →
/// <c>sdf-instance-cull.comp</c> (per-tile instance mask) → <c>sdf-beam.comp</c> (tile-cull cone-march prepass) →
/// <c>sdf-cull-args.comp</c> (GPU-written indirect dispatch args: the surviving-tile bbox) →
/// <c>sdf-world-primary.comp</c> (visibility records) → <c>sdf-world-surface.comp</c> (normals and curvature) →
/// <c>sdf-world-ambient.comp</c> (AO) → <c>sdf-world-views.comp</c> (shading and diagnostics),
/// all four dispatched indirectly from those args. Every view renders that chain from the sky on through its own
/// dispatch set into its own output image (<see cref="TryAcquireViewOutput"/>), which a render graph places into the
/// view's rect. Fully backend-neutral through its device's <see cref="IGpuDeviceContext.Services"/>.
/// <para>
/// Two submission models, and they must never blur: <see cref="RenderFrame"/> is the deterministic harness path — one
/// submit-and-wait plus a readback (validation, headless render). <see cref="SubmitFrame"/> is the live node path —
/// fire-and-forget behind the engine's own <see cref="FrameRingSize"/>-deep frame ring (each slot's fence orders that
/// slot's rewrites against its previous submission, so a pipelining host needs no per-frame device drain), plus the
/// export-mode queue drain when the output crosses a backend seam. Adding a wait to <see cref="SubmitFrame"/> is a
/// frame-rate regression; removing the wait from <see cref="RenderFrame"/> is a nondeterminism bug.
/// </para>
/// </summary>
public sealed partial class SdfWorldEngine : IDisposable, ISdfBrickBakeService {
    private const int BrickBakeRequestHeaderFloat4Count = 3; // (boxMin+cellSize), (dims+carveCount), (destWordOffset+invLambda) — KEEP IN SYNC with sdf-brick-bake.comp
    private const uint BrickBakeWorkgroupSize = 64; // sdf-brick-bake.comp's [numthreads(64, 1, 1)]
    private const ulong CullBoundsByteLength = (sizeof(uint) * 4); // the dispatch box sdf-cull-args.comp writes: the group origin, then the exclusive end
    private const int DecalBufferCells = (DecalDescriptorCount + (MaxScreenSurfaces * MaxScreenDecalCells));
    // The decal buffer's leading DESCRIPTOR band (one uint4 per screen slot) precedes the shared cell region; a screen's
    // cell run starts at DecalDescriptorCount + screenIndex * MaxScreenDecalCells (KEEP IN SYNC with sdfSampleGlyphDecal).
    private const int DecalDescriptorCount = MaxScreenSurfaces;
    private const int DecalWordsPerCell = 4; // one uint4 per cell/descriptor (KEEP IN SYNC with sdf-world.hlsli's sdfDecalCells)
    private const int DynamicTransformByteLength = ((sizeof(float) * 4) * 3); // 48-byte rigid transform: float4 position (xyz + .w = soft-shadow participation: 0 casts / 1 shadow-suppressed) + float4 orientation quaternion + float4 anonymous Lanes (DynamicTransform.Lanes) (KEEP IN SYNC with sdf-vm.hlsli sdfDynamicTransforms: position.w is read by sdfShadowParticipationActive's per-instance skip in sdf-world.hlsli, the third row by SDF_OP_LANE_ERODE's currentLanes and shade-volumes.hlsli's selected intensity lane)
    private const GpuPixelFormat Format = GpuPixelFormat.R8G8B8A8Unorm;
    private const int MaxBrickBakeVoxelsPerSlice = (256 * 1024); // <= 256K voxels per brick per produced frame: ~1-2 ms background-budget
    private const int MaxBrickCarvesPerBake = 4096; // request-buffer carve capacity per slot (the debug pool's MaxCarves ceiling)
    private const int ScreenLightByteLength = ((sizeof(float) * 4) * ((MaxScreenSurfaces + 8) + SdfEnvironment.RowCount)); // float4 rgb+intensity per screen (0..MaxScreenSurfaces-1) + env (MaxScreenSurfaces) + FOUR grid-lock rows (+1..+4) + the engine-bench params row (+5) + the shadow-policy row (+6) + the far-field row (+7) + the environment block (+8 onward: SdfEnvironment's row layout) — KEEP IN SYNC with sdf-world.hlsli SdfGridWorld..SdfEnvBase
    private const float ScreenLightIntensity = 2.5f; // room-glow gain applied to each screen's average color
    private const int ScreenSurfaceByteLength = ((sizeof(float) * 4) * 3); // 48-byte ScreenSurfaceData: right.xyz+halfWidth, up.xyz+halfHeight, origin.xyz+pad (KEEP IN SYNC with sdf-world.hlsli)
    // The tile cull buffer carries FOUR planes per (viewport, tile), each of stride
    // (tileGrid.x * tileGrid.y * viewportCount): plane 0 = the march-start lower bound (the classic beam
    // output; the ONLY plane cull-args reads, so its indexing is unchanged), plane 1 =
    // firstExit, plane 2 = secondEntry — the four-bound teleport's proven-empty gap [firstExit, secondEntry]
    // (Larsson "The Gunk") — and plane 3 = the F1 far bound (the depth past which the tile's cone cannot produce
    // any footprint-accepted hit through the frame's far distance). The extra planes are written by sdf-beam and
    // read by sdf-world-views only; a tile with no proven gap/far bound packs the far distance (teleport/far-exit
    // disabled), so every plane is a total function.
    // KEEP IN SYNC with WorldTilePlaneCount + worldTilePlaneStride in sdf-world.hlsli / sdf-tile.hlsli.
    private const uint TilePlaneCount = 4;
    // Primary and AO cache bands, each holding two float3 corners per (viewport, live instance), after the tile planes.
    // KEEP IN SYNC with SdfPartBoundFloatCount and sdfPartBoundIndex in sdf-part-bounds.hlsli.
    private const uint PartBoundFloatCount = 12;

    /// <summary>The primary (camera) march's per-pixel step budget. KEEP IN SYNC with <c>MaxSteps</c> in
    /// sdf-world.hlsli. Exposed so a host's cost sheet can quote an authored <see cref="SdfFrame.FarDistance"/>
    /// against the budget that has to reach it (a ray skimming open ground at height <c>h</c> takes roughly one step
    /// per <c>h</c> units of depth, so the far distance is that ray's step count per unit of height).</summary>
    public const int PrimaryMarchSteps = 128;
    /// <summary>The distance at which every camera cone begins, and so the near plane a rasterized view shares with
    /// the SDF march (<see cref="Puck.Abstractions.Cameras.ViewProjection.Create"/>'s <c>near</c>). KEEP IN SYNC with
    /// <c>ConeNear</c> in sdf-viewport.hlsli.</summary>
    public const float ConeNear = 0.02f;
    /// <summary>The edge of one screen tile in pixels, the unit the beam, the instance masks and the cull buffer
    /// count in. KEEP IN SYNC with <c>WorldTileSize</c> in sdf-world.hlsli.</summary>
    public const uint TileSize = 16;

    private const int ViewportByteLength = ((sizeof(float) * 4) * 6); // 96-byte ViewportData incl. the renderScale row (KEEP IN SYNC with sdf-world.hlsli)
    private const ulong ViewsArgsByteLength = (sizeof(uint) * 3); // the three indirect group counts sdf-cull-args.comp writes
    private const int PrimaryHitByteLength = (15 * sizeof(uint)); // the visibility record's fifteen words in its V, C, L, N and S rows; paired with sdf-visibility.hlsli's SdfVisibilityWords
    // Packed flow/cloud volume stride; paired with shade-volumes.hlsli.
    private const int VolumeByteLength = ((sizeof(float) * 4) * SdfVolume.VectorsPerEntry);
    private const uint WorkgroupEdge = 8;

    /// <summary>The default carve-bake brick pool capacity in voxels (f32 words) — <see cref="SdfBrickPoolLayout.TotalVoxels"/>
    /// = 16.7M voxels = 64 MB, i.e. <see cref="SdfBrickPoolLayout.MaxBricks"/> slots at full resolution.</summary>
    public const int DefaultBrickPoolVoxelCapacity = SdfBrickPoolLayout.TotalVoxels;
    /// <summary>The frame-ring depth: how many produced frames may be in flight on the GPU at once. Every per-frame
    /// mutable resource — the command buffer, each region's host-visible buffers (program words, viewports, dynamic
    /// transforms, the frame instance grid, screen surfaces, screen lights, volumes, decals, mesh draws), the
    /// descriptor sets that bind them, and the per-submit fence — is duplicated per slot, so re-recording/rewriting
    /// slot <c>k</c> only requires frame <c>k − FrameRingSize</c> to have retired (the slot fence wait in
    /// <c>PrepareFrame</c>), never a whole-device drain. Each of those tables is a <see cref="GpuRegion"/> under the
    /// policy <see cref="GpuResidency.Select"/> chooses for it, and a slot's buffer receives only the words that slot
    /// owes. The GPU-written
    /// device-local scratch (tile / instance-mask / indirect-args / cull-bounds buffers, the per-view output
    /// images) stays shared: the top-of-frame barrier in <c>Record</c> orders each frame's GPU work after the
    /// previous frame's, which is the natural serialization anyway — the ring overlaps CPU production with GPU
    /// execution, not GPU frames with each other. Slot advance is keyed to the produced-frame count (deterministic;
    /// never wall clock).</summary>
    public const int FrameRingSize = 2;
    /// <summary>The per-screen glyph decal cell budget: the most glyph cells one screen slot's decal grid may carry
    /// (columns × rows). The authoritative ceiling lives in <see cref="SdfScreenDecalLayout"/>; the decal buffer
    /// partitions its cell region into <see cref="MaxScreenSurfaces"/> equal per-screen runs of this size, so a decal
    /// on one screen never collides with another's cells.</summary>
    public const int MaxScreenDecalCells = SdfScreenDecalLayout.MaxScreenDecalCells;
    /// <summary>The kernels' screen-source count — the most screen surfaces one program may declare (the same
    /// ceiling as <see cref="Puck.SignedDistance.SdfProgramBuilder.MaxScreenSurfaces"/>, which this reads rather than
    /// hand-syncing). Each screen is one sampled-image member of <see cref="SdfWorldInterfaces.World"/>, all sampled
    /// through its one nearest <see cref="SdfWorldInterfaces.ScreenSampler"/>. Capped at 32 because the world block's
    /// <see cref="SdfWorldInterfaces.ScreenMask"/> (the per-frame bound-slot bitmask) is a single <c>uint</c> — raising
    /// past 32 needs a second mask word on both sides.</summary>
    public const int MaxScreenSurfaces = Puck.SignedDistance.SdfProgramBuilder.MaxScreenSurfaces;
    /// <summary>The most views one engine renders in a frame: its viewport capacity's ceiling, which sizes the per-view
    /// descriptor sets and output images the engine can hold.</summary>
    public const int MaxViewports = 5;
    /// <summary>The most bounded emissive volumes (<see cref="Puck.SignedDistance.SdfVolume"/>) one rendered frame
    /// carries — the same ceiling as <see cref="Puck.SignedDistance.SdfProgramBuilder.MaxVolumes"/>, which this
    /// reads rather than hand-syncing a second literal.</summary>
    public const int MaxVolumes = Puck.SignedDistance.SdfProgramBuilder.MaxVolumes;

    private readonly IGpuComputePipeline m_beamPipeline;
    // The bake pipeline + per-slot request buffers/sets — created ONLY when the pool is enabled (nothing bakes into a
    // filler). Each slot owns a host-visible request buffer (header + carve list) and a static descriptor set binding
    // that buffer + the shared pool (as a UAV). The per-slot state advances one slice per produced frame (RecordBrickBakeSlices).
    private readonly IGpuComputePipeline? m_brickBakePipeline;
    // The host-baked brick path: a region staging one brick at a time into the brick pool, its external destination,
    // and a queue of pending uploads drained one per produced frame (RecordBrickUpload). Null without a brick pool.
    private readonly GpuRegion? m_brickRegion;

    private readonly Queue<(int Slot, int Count, float[] Voxels)> m_brickUploads = new();

    // The device's region-copy pipeline, leased from the pass-pipeline cache's GpuRegionCopyPass, which every staged region
    // records its copy with; the engine never owns it.
    private readonly IGpuComputePipeline m_regionCopyPipeline;
    // The carve-bake brick pool: one persistent device-local f32 buffer the sliced bake writes and
    // the beam + views kernels sample. Always allocated (a 1-float filler when the pool is disabled), always bound to
    // the beam/views sets, since both kernels compile the sdfBrickPool binding unconditionally (SDF_SAMPLED_REGIONS).
    private readonly IGpuBuffer m_brickPoolBuffer;
    private readonly bool m_brickPoolEnabled;
    private readonly int m_brickPoolVoxelCapacity;
    private readonly IGpuComputePipeline m_cullArgsPipeline;
    private readonly IGpuBuffer m_cullBoundsBuffer;
    private readonly IGpuBindings m_bindings;
    private readonly IGpuDeviceContext m_deviceContext;
    private readonly int m_dynamicTransformCapacity;
    private readonly bool m_exportMode;
    private readonly IGpuExportableImage? m_exportableImage;
    private readonly GpuDeviceServices m_gpu;
    private readonly uint m_height;

    private int m_instanceCapacity;

    private readonly IGpuComputePipeline m_instanceCullPipeline;

    private SdfInstanceGridInput[] m_instanceGridInputScratch;
    private int m_instanceGridWordCapacity;
    private SdfInstanceGrid.Workspace m_instanceGridWorkspace;
    private IGpuBuffer m_instanceMaskBuffer;
    private int m_instanceMaskWordCount;

    private readonly nint m_pool;

    // The program region's words, and the words the options provisioned for, which ProgramWordCapacity reports when
    // larger.
    private int m_programWordCapacity;

    private readonly int m_programWordReserve;
    private readonly nint m_screenSampler;
    private readonly IGpuImage m_screenSourceFiller;
    // Like every per-view pipeline it binds the world interface's groups, so the frame sets and the per-slot, per-view
    // m_viewsSets bind against it and it needs no descriptor sets of its own.
    private readonly IGpuComputePipeline m_skyPipeline;

    private IGpuBuffer m_tileBuffer;

    private readonly uint m_tileGridX;
    private readonly uint m_tileGridY;
    private readonly uint m_viewportCapacity;
    private readonly byte[] m_viewportScratch;
    private readonly IGpuBuffer m_viewsArgsBuffer;
    // The core-ops and fold Stage 1 variants (see SdfViewsKernelVariant): the same world interface as m_viewsPipeline, so
    // the per-slot m_viewsSets bind against WHICHEVER pipeline UploadProgram selected, and no second set or
    // descriptor-write path exists.
    private readonly IGpuComputePipeline m_viewsCorePipeline;
    private readonly IGpuComputePipeline m_viewsFoldsPipeline;
    private readonly IGpuComputePipeline m_viewsPipeline;
    private readonly IGpuComputePipeline m_primaryPipeline;
    private readonly IGpuComputePipeline m_surfacePipeline;
    private readonly IGpuComputePipeline m_ambientPipeline;
    private readonly IGpuBuffer m_primaryHitBuffer;
    private readonly uint m_width;

    private int m_currentSlot;
    private ulong m_decalRevision;
    private bool m_disposed;
    // The SDF_SHAPE_GLYPH font atlas: a STATIC texture uploaded once via SetGlyphAtlas (a re-set re-uploads). Held as an
    // IGpuSurfaceUpload (owns the image + staging + the returned view), the current sampleable view, and the last-bound
    // view for a change-detected (re)bind — sound here because the atlas is ENGINE-owned, and SetGlyphAtlas clears this
    // cache itself when a re-upload retires the previous view. Null/0 until set — the
    // glyph binding then samples the neutral 1×1 filler (m_screenSourceFiller) and every SDF_SHAPE_GLYPH reads the
    // saturated band, so a glyph-free program with no atlas is safe.
    private IGpuSurfaceUpload? m_glyphAtlasUpload;
    private nint m_glyphAtlasView;
    private bool m_hasPreviousFrameSignature;
    private bool m_fillerInitialized;
    private int m_liveInstanceMaskWordCount;
    // The previous RENDERED frame's change signature (a 64-bit hash of every packed span + revision the skipped passes
    // consume — see ComputeFrameSignature) and whether one exists yet. Reset whenever the gate is off, so re-enabling it
    // always renders the first frame before it can skip.
    private ulong m_previousFrameSignature;
    // CADENCE GATE: whether the LIVE uploaded program declares any ScreenSlab shape (bound or not) — computed once at
    // UploadProgram (the single owner of per-program state), never per frame. A declared-but-unbound slab's face is the
    // animated test-card (screenContent, sdf-world.hlsli), which reads presentation TIME every frame; the signature
    // excludes that lane (ComputeFrameSignature), so this fact is what makes DecideCadenceSkip force a render instead.
    private bool m_programDeclaresScreenSlab;
    // Monotonic revisions folded into the signature so a change to a resource NOT re-hashed each frame still invalidates
    // it: m_programRevision bumps on every UploadProgram (program words, live mask width, kernel variant, screen-surface
    // reseed), m_decalRevision on every SetScreenDecal/ClearScreenDecal call that ACTUALLY changes the stored bytes (the
    // 820 KB decal buffer is revision-tracked, not re-hashed per frame — both setters change-detect first, since a
    // provider polled every produced frame, e.g. the diegetic terminal mirror, commonly re-supplies unchanged content).
    private ulong m_programRevision;
    private IGpuSurfaceReadback? m_readback;
    private bool m_rebuildInstanceGridPerFrame;
    private int m_requiredDynamicTransformCapacity;
    private ulong m_ringFrame;
    private uint m_screenSourceMask;
    // Cadence gate, latched by PrepareFrame and read by Record: when true, Record skips every view's dispatch set and
    // each view's retained output stands — pixel-identical because the change signature below proved every input those
    // passes consume is unchanged.
    private bool m_skipThisFrame;
    private SdfViewsKernelVariant m_viewsVariant;

    // The render passes, in submission order — the GPU work ledger's column labels (see SdfWorldEngine.Work.cs, whose
    // public PassLabels exposes this list). Adding a pass is naming it here and adding its EnterPass/LeavePass bracket
    // where it submits (SdfWorldEngine.Record.cs); every reader (PassLabels, CadenceSkippedPassLabels, the ledger's
    // per-pass counts) then picks it up automatically.
    private static readonly string[] PassLabelTable = ["upload", "sky", "mask", "beam", "cull-args", "mesh", "primary", "surface", "ambient", "views"];

    // The change-detected descriptor caches are PER RING SLOT: each slot's sets are only rewritten once that slot's
    // fence proves its previous frame retired, so a descriptor update can never race an in-flight command buffer.
    // They cover ENGINE-OWNED views only — a host-owned view (a screen source) is rebound
    // unconditionally, since its handle value is not a durable identity (BindScreenSources' handle-identity rule).
    // Per ring slot and view slot: the screen-source views that views set binds.
    private readonly nint[][][] m_boundScreenSourceViews;
    // Per ring slot and view slot, flattened ([slot * capacity + view]): the glyph atlas view that views set binds.
    private readonly nint[] m_boundGlyphAtlasViews;

    private readonly IGpuCommandPool[] m_commandPools = new IGpuCommandPool[FrameRingSize];
    // One per-submit fence per ring slot: PrepareFrame waits slot k's fence (frame k − FrameRingSize) before
    // rewriting slot k's resources; the fenced submit re-arms it.
    private readonly IGpuSubmissionFence[] m_frameFences = new IGpuSubmissionFence[FrameRingSize];
    private readonly nint[] m_screenSourceViews = new nint[MaxScreenSurfaces];
    // The screen-light table (screen glow colors, environment, grid-overlay and lever rows) and the bounded-volume table
    // (views and sky), each packed here every frame and written into its region.
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

    // One views set per ring slot and view slot ([slot][view]): the pass group every dispatch of that view's dispatch set
    // binds, its block the view's world values and its bindings every table, buffer and image the dispatches read.
    private readonly nint[][] m_viewsSets;

    private SdfProgram m_liveProgram = null!;

    internal uint[] CopyLiveProgramWords() => m_liveProgram.Words.ToArray();

    /// <summary>Gets or sets the debug-group label wrapping this engine's whole recorded frame — the outer scope a GPU
    /// capture (RenderDoc / PIX / Nsight) shows around this engine's per-pass groups (so a nested view engine reads as
    /// <c>view:&lt;name&gt;</c> containing its own mask/beam/cull-args/views). Presentation-only; defaults to
    /// <c>world</c> and never affects rendered output.</summary>
    public string DebugLabel { get; set; } = "world";

    /// <summary>Initializes a new instance of the <see cref="SdfWorldEngine"/> class: builds every buffer, image and
    /// descriptor set at the provisioned viewport capacity against pipelines already built, verifies the kernels' ISA
    /// version once per device and kernel set, and uploads the scene program once. Creates no pipeline, so it never
    /// waits on the driver's pipeline compiler. A construction that throws partway has released every object it created
    /// before the exception leaves the constructor.</summary>
    /// <param name="device">The GPU device the engine renders on; the engine records through its services, unwrapped.</param>
    /// <param name="pipelines">The pipelines to render with, built on <paramref name="device"/>
    /// (<see cref="SdfWorldPipelines.Build"/>). The caller keeps ownership and disposes them after the engine; one set
    /// may outlive several engines built from it, but serves one live engine at a time, since a kernel reload swaps
    /// them in place.</param>
    /// <param name="regionCopy">The device's region-copy pipeline, created from
    /// <see cref="GpuRegion.CopyPipeline"/> on <paramref name="device"/>, which the table upload and the mesh region
    /// record with. The caller keeps ownership and disposes it after the engine.</param>
    /// <param name="meshRaster">The device's mesh pass pipeline (<see cref="SdfMeshRasterPass"/>), built on
    /// <paramref name="device"/> with the render pass it draws in. The caller keeps ownership and disposes it after the
    /// engine.</param>
    /// <param name="width">The engine's extent width in pixels: the widest any view renders.</param>
    /// <param name="height">The engine's extent height in pixels: the tallest any view renders.</param>
    /// <param name="options">The construction options (scene program, capacities, child mask, export seam).</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A dimension is zero, the viewport capacity is 0 or above
    /// <see cref="MaxViewports"/>, or the options enable a brick pool the pipelines were built without.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="pipelines"/> has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The device's descriptor heap cannot admit the engine's pool
    /// (<see cref="CheckAdmission"/>, checked before anything is allocated), or the loaded shader bytecode does not report
    /// the host's <see cref="Puck.SignedDistance.SdfIsa.Version"/>.</exception>
    public SdfWorldEngine(IGpuDeviceContext device, SdfWorldPipelines pipelines, IGpuComputePipeline regionCopy, GpuPassPipeline meshRaster, uint width, uint height, SdfWorldEngineOptions options) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(pipelines);
        ArgumentNullException.ThrowIfNull(regionCopy);
        ArgumentNullException.ThrowIfNull(meshRaster);
        ObjectDisposedException.ThrowIf(
            condition: pipelines.IsDisposed,
            instance: pipelines
        );
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

        if ((options.BrickPoolVoxelCapacity > 0) && !pipelines.IncludesBrickPipelines) {
            throw new ArgumentException(message: "The options enable a brick pool, but the pipelines were built without the brick bake and upload pipelines.");
        }

        // Before anything is allocated, so an engine the device's descriptor heap cannot hold is refused with nothing
        // to release, and a holder building through SdfWorldPipelineSource.TryBuild records it as a named refusal.
        CheckAdmission(
            device: device,
            options: options
        );

        m_work = (options.WorkLedger ?? new GpuWorkLedger(
            framesInFlight: FrameRingSize,
            name: "gpu.sdf-engine"
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
        m_height = height;
        m_instanceCapacity = Math.Max(
            val1: options.Program.Instances.Count,
            val2: options.InstanceCapacity
        );
        m_instanceGridInputScratch = new SdfInstanceGridInput[m_instanceCapacity];
        m_instanceGridWorkspace = new SdfInstanceGrid.Workspace(maxInstances: m_instanceCapacity);
        m_instanceGridWordCapacity = SdfInstanceGrid.WordCapacity(maxInstances: m_instanceCapacity);
        m_viewportCapacity = options.ViewportCapacity;
        m_viewportScratch = new byte[(((int)m_viewportCapacity) * ViewportByteLength)];
        m_width = width;
        m_brickPoolVoxelCapacity = Math.Max(
            val1: 0,
            val2: options.BrickPoolVoxelCapacity
        );
        m_brickPoolEnabled = (m_brickPoolVoxelCapacity > 0);

        var capacity = FrameCapacity;

        m_tileGridX = capacity.TileGridX;
        m_tileGridY = capacity.TileGridY;

        // Every object the construction creates joins this scope, so a creation, the ISA verification or the program
        // upload that throws releases exactly what was created before it, newest first. Nothing is in flight to drain
        // first: every submission construction makes waits for its completion.
        using var scope = new GpuCreationScope();

        m_pipelines = pipelines;
        m_beamPipeline = pipelines.Pipeline(index: BeamPipelineIndex);
        m_instanceCullPipeline = pipelines.Pipeline(index: InstanceCullPipelineIndex);
        m_cullArgsPipeline = pipelines.Pipeline(index: CullArgsPipelineIndex);
        m_primaryPipeline = pipelines.Pipeline(index: PrimaryPipelineIndex);
        m_surfacePipeline = pipelines.Pipeline(index: SurfacePipelineIndex);
        m_ambientPipeline = pipelines.Pipeline(index: AmbientPipelineIndex);
        m_viewsPipeline = pipelines.Pipeline(index: ViewsPipelineIndex);
        m_viewsCorePipeline = pipelines.Pipeline(index: ViewsCorePipelineIndex);
        m_viewsFoldsPipeline = pipelines.Pipeline(index: ViewsFoldsPipelineIndex);
        m_skyPipeline = pipelines.Pipeline(index: SkyPipelineIndex);
        m_meshPipeline = (meshRaster.Graphics ?? throw new ArgumentException(message: "The mesh pass pipeline is not a graphics pipeline.", paramName: nameof(meshRaster)));
        m_meshRenderPass = (meshRaster.RenderPass ?? throw new ArgumentException(message: "The mesh pass pipeline names no render pass.", paramName: nameof(meshRaster)));
        m_regionCopyPipeline = regionCopy;
        m_regionCopies = new GpuRegionCopyRecording(
            begin: BeginUpload,
            readers: GpuStage.ComputeShader | GpuStage.VertexShader | GpuStage.FragmentShader,
            recorder: gpu.Recorder
        );

        m_viewsSets = new nint[FrameRingSize][];
        m_viewOutputs = new ViewOutput?[((int)m_viewportCapacity)];
        m_requestedViewExtents = new (uint Width, uint Height)[((int)m_viewportCapacity)];
        m_boundOutputViews = BuildRingViewCache(width: ((int)m_viewportCapacity));
        m_boundGlyphAtlasViews = new nint[(FrameRingSize * ((int)m_viewportCapacity))];
        m_boundScreenSourceViews = new nint[FrameRingSize][][];

        for (var slot = 0; (slot < FrameRingSize); slot++) {
            m_boundScreenSourceViews[slot] = BuildRingViewCache(rows: ((int)m_viewportCapacity), width: MaxScreenSurfaces);
        }

        // A dedicated 1x1 ShaderReadOnly filler for an unbound screen-source slot: a combined-image-sampler binding
        // requires ShaderReadOnly, which no view output (General while its set writes it) can stand in for. This image is
        // transitioned ONCE, by the first recorded frame, and never written again.
        m_screenSourceFiller = scope.Own(created: gpu.ImageFactory.Create(
            name: NameOf(part: "screen-source-filler"),
            format: Format,
            height: 1,
            usage: GpuImageUsage.Sampled | GpuImageUsage.Storage,
            width: 1
        ));

        // In export mode the host supplies view 0's output, an exportable image at the engine's extent (cross-backend
        // present); every other view output is created at its view's extent by the frame that first renders it.
        if (options.CreateOutputImage is not null) {
            var exported = scope.Own(created: options.CreateOutputImage(device));

            m_exportableImage = (exported as IGpuExportableImage);
            m_viewOutputs[0] = new ViewOutput(
                height: height,
                identity: NextOutputIdentity(),
                image: exported,
                width: width
            );
        }

        m_exportMode = (m_exportableImage is not null);

        // The program region holds the live program, not the options' reserve: a probed worst case can run to hundreds
        // of megabytes, which a ring holds once per slot. A larger program grows the region by half again.
        m_programWordReserve = options.ProgramWordCapacity;
        m_programWordCapacity = options.Program.Words.Length;
        // Every region's copy sets, before any region writes them: the copy pool the admission states.
        m_regionCopyPool = ReserveRegionCopyPool(scope: scope);
        // The host-written tables, each a region the kernels bind through its slot's buffer (SdfWorldEngine.Regions.cs).
        // The screen-surface table is always MaxScreenSurfaces entries, indexed directly by screen index, so Stage 1's
        // binding stays valid for a program with none: an all-zero undeclared entry is never addressed.
        m_programRegion = scope.Own(created: CreateRegion(
            byteCount: checked((m_programWordCapacity * sizeof(uint))),
            region: ProgramRegionIndex
        ));
        m_viewportRegion = scope.Own(created: CreateRegion(
            byteCount: m_viewportScratch.Length,
            region: ViewportRegionIndex
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
        m_meshRegionBytes = SdfMeshRegion.DrawBytes;
        (m_meshTarget, m_meshDepth, m_meshFramebuffer) = CreateMeshAttachments(
            renderPass: m_meshRenderPass,
            scope: scope
        );
        // The cull buffer is GPU-written by the beam prepass (a UAV), so it is device-local (a Direct3D 12 default heap).
        // Four tile planes followed by two world-space bound corners per instance per viewport. The beam refits
        // those bounds from this frame's poses and camera; primary reads them after the existing compute barrier.
        // Reserve the construction envelope, but index with live viewport/instance counts, as the masks do.
        m_tileBuffer = scope.Own(created: gpu.BufferFactory.CreateDeviceLocal(
            name: NameOf(part: "tiles"),
            sizeBytes: FrameBufferBytes(
                buffer: SdfFrameBuffer.Tiles,
                capacity: capacity
            ),
            usage: GpuBufferUsage.Storage
        ));
        // One full-extent slice per viewport, like the source textures: changing regions must never overrun a
        // buffer sized for a previous layout. Shared across frame slots; Record orders primary writes before
        // shading reads and this frame's writes after the preceding frame's reads.
        m_primaryHitBuffer = scope.Own(created: gpu.BufferFactory.CreateDeviceLocal(
            name: NameOf(part: "primary-hits"),
            sizeBytes: FrameBufferBytes(
                buffer: SdfFrameBuffer.PrimaryHits,
                capacity: capacity
            ),
            usage: GpuBufferUsage.Storage
        ));
        // The per-tile instance mask: same (viewport, tile) indexing as the cull buffer, GPU-written by instance
        // cull before the beam (a UAV, so device-local too), read by Stage 1 to gate its masked map() calls. The
        // buffer is sized for the CONSTRUCTION program's width (ceil(instanceCount/32) uints, at least 1 —
        // SdfProgram.InstanceMaskWordCount); the kernels index with the LIVE uploaded program's width, pushed per
        // frame (m_liveInstanceMaskWordCount). UploadProgram grows this reserve when necessary.
        m_instanceMaskWordCount = SdfProgram.InstanceMaskWordCountFor(instanceCount: m_instanceCapacity);
        m_instanceMaskBuffer = scope.Own(created: gpu.BufferFactory.CreateDeviceLocal(
            name: NameOf(part: "instance-masks"),
            sizeBytes: FrameBufferBytes(
                buffer: SdfFrameBuffer.InstanceMasks,
                capacity: capacity
            ),
            usage: GpuBufferUsage.Storage
        ));

        // The carve-bake brick pool: one persistent DEVICE-LOCAL f32 buffer — device-local so the
        // bake kernel can write it as a UAV (an upload heap forbids UAVs on Direct3D 12) and the beam/views sample it as
        // an SRV. Frozen at the constructed capacity. When the pool is disabled (capacity 0) a single-float filler keeps
        // the always-present sdfBrickPool binding valid — the kernels compile the binding unconditionally, and
        // sdfSampledRegion detects the filler by its element count and renders SampledRegion programs via the
        // conservative uncarved-hull fallback (only RequestBrickBake stays rejected on a pool-less engine).
        m_brickPoolBuffer = scope.Own(created: gpu.BufferFactory.CreateDeviceLocal(
            name: NameOf(part: "brick-pool"),
            sizeBytes: FrameBufferBytes(
                buffer: SdfFrameBuffer.BrickPool,
                capacity: capacity
            ),
            usage: GpuBufferUsage.Storage
        ));
        // The carve-bake baker and the host-baked brick staging run only when the pool is enabled (nothing bakes into a
        // filler). The staging region holds one brick, the most one upload carries, and lands it at the brick's slot.
        m_brickBakePipeline = (m_brickPoolEnabled
            ? pipelines.OptionalPipeline(index: BrickBakePipelineIndex)
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

        // GPU-driven cull: the cull-args pass reduces the cull buffer to the Stage-1 INDIRECT dispatch args (the
        // surviving-tile bbox, 3 group counts) and the dispatch box (4 uints: the group origin, then the exclusive
        // end). Both are device-local — the GPU writes them as UAVs, then a barrier orders the indirect read; the
        // views dispatch reads the args (the dispatch grid) and the box (its pixel offset, and where a visibility
        // record is current). The all-empty margins are never dispatched.
        m_viewsArgsBuffer = scope.Own(created: gpu.BufferFactory.CreateDeviceLocal(
            name: NameOf(part: "views-args"),
            sizeBytes: FrameBufferBytes(
                buffer: SdfFrameBuffer.ViewsArgs,
                capacity: capacity
            ),
            usage: GpuBufferUsage.Storage | GpuBufferUsage.Indirect
        ));
        m_cullBoundsBuffer = scope.Own(created: gpu.BufferFactory.CreateDeviceLocal(
            name: NameOf(part: "cull-bounds"),
            sizeBytes: FrameBufferBytes(
                buffer: SdfFrameBuffer.CullBounds,
                capacity: capacity
            ),
            usage: GpuBufferUsage.Storage
        ));

        // The frame block, written once a frame, and one world block per view slot, each a uniform region with one
        // constant buffer per ring slot.
        m_frameRegion = scope.Own(created: CreateBlockRegion(
            blockBytes: SdfWorldInterfaces.WorldParameters.FrameBlockSizeBytes,
            name: NameOf(part: "frame-block")
        ));
        m_viewBlocks = new GpuRegion[((int)m_viewportCapacity)];

        for (var view = 0; (view < m_viewBlocks.Length); view++) {
            m_viewBlocks[view] = scope.Own(created: CreateBlockRegion(
                blockBytes: SdfWorldInterfaces.WorldParameters.SizeBytes,
                name: NameOf(detail: ViewDetails[view], part: "world-block")
            ));
        }

        // One pool: per ring slot a frame set, a mesh set and one views set per view slot, and with a brick pool one bake
        // set per brick slot. The Direct3D 12 allocator bump-allocates a non-overlapping heap region per set (like a Vulkan pool),
        // so they never clobber, and the capacity is derived from the interfaces' groups.
        var poolSizes = DescriptorPoolSizes(
            brickPool: m_brickPoolEnabled,
            viewportCapacity: m_viewportCapacity
        );

        m_pool = m_bindings.CreatePool(
            name: NameOf(part: "descriptors"),
            sizes: poolSizes
        );
        // The sets allocated from the pool are released with it.
        _ = scope.Own(
            handle: m_pool,
            release: m_bindings.DestroyPool
        );

        // The screen sources and the glyph atlas are sampled through one nearest sampler, written into every views set
        // once; the images themselves are (re)bound per frame by BindScreenSources.
        m_screenSampler = scope.Own(
            handle: m_bindings.CreateSampler(filter: GpuSamplerFilter.Nearest),
            release: m_bindings.DestroySampler
        );

        var groupLayouts = m_viewsPipeline.GroupLayoutHandles;

        for (var slot = 0; (slot < FrameRingSize); slot++) {
            m_frameSets[slot] = m_bindings.AllocateSet(
                name: NameOf(detail: "frame group", index: slot, part: "world"),
                descriptorSetLayoutHandle: groupLayouts[((int)FrameGroup)],
                poolHandle: m_pool
            );
            m_bindings.WriteConstantBuffer(
                arrayElement: 0,
                binding: 0,
                bufferHandle: m_frameRegion.Buffer(slot: slot).BufferHandle,
                bufferSize: ((ulong)m_frameRegion.ByteCount),
                descriptorSetHandle: m_frameSets[slot]
            );
            m_viewsSets[slot] = new nint[((int)m_viewportCapacity)];

            for (var view = 0; (view < ((int)m_viewportCapacity)); view++) {
                var viewsSet = m_bindings.AllocateSet(
                    name: NameOf(detail: ViewDetails[view], index: slot, part: "views"),
                    descriptorSetLayoutHandle: groupLayouts[((int)PassGroup)],
                    poolHandle: m_pool
                );

                m_viewsSets[slot][view] = viewsSet;
                m_bindings.WriteConstantBuffer(
                    arrayElement: 0,
                    binding: 0,
                    bufferHandle: m_viewBlocks[view].Buffer(slot: slot).BufferHandle,
                    bufferSize: ((ulong)m_viewBlocks[view].ByteCount),
                    descriptorSetHandle: viewsSet
                );
                m_bindings.WriteSampler(
                    arrayElement: 0,
                    binding: ScreenSamplerBinding,
                    descriptorSetHandle: viewsSet,
                    samplerHandle: m_screenSampler
                );
                // The shared device-local scratch: each buffer one pass writes binds twice, read-write for its writer and
                // read-only for its readers.
                WriteWorldBuffer(buffer: m_viewsArgsBuffer, member: SdfWorldInterfaces.ViewsArgsWritten, set: viewsSet);
                WriteWorldBuffer(buffer: m_cullBoundsBuffer, member: SdfWorldInterfaces.CullBoundsWritten, set: viewsSet);
                WriteWorldBuffer(buffer: m_cullBoundsBuffer, member: SdfWorldInterfaces.CullBounds, set: viewsSet);
                WriteWorldBuffer(buffer: m_brickPoolBuffer, member: SdfWorldInterfaces.BrickPool, set: viewsSet);
                WriteWorldBuffer(buffer: m_primaryHitBuffer, member: SdfWorldInterfaces.VisibilityRecordsWritten, set: viewsSet);
                WriteWorldBuffer(buffer: m_primaryHitBuffer, member: SdfWorldInterfaces.VisibilityRecords, set: viewsSet);
                m_bindings.WriteSampledImage(
                    arrayElement: 0,
                    binding: MeshVisibilityBinding,
                    descriptorSetHandle: viewsSet,
                    imageViewHandle: m_meshTarget.ImageViewHandle
                );
            }

            m_meshSets[slot] = AllocateMeshSet(slot: slot);

            BindProgramBuffers(slot: slot);
            BindRegions(slot: slot);

            // Each view's output image is (re)bound per frame by BindViewOutputs, once the frame that renders the view
            // has sized it.
            m_commandPools[slot] = scope.Own(created: gpu.CommandPoolFactory.Create(name: NameOf(
                part: "commands",
                index: slot
            )));
            m_frameFences[slot] = scope.Own(created: gpu.QueueSubmitter.CreateSubmissionFence());
        }

        // The carve-bake baker's per-slot request buffers + static descriptor sets (only when the pool is enabled). Each
        // slot owns a host-visible request buffer (header + up to MaxBrickCarvesPerBake carves) and a set binding that
        // buffer and the shared pool, beside the one block every bake set shares. These are NOT per-ring-slot: a bake
        // spans frames and RequestBrickBake drains the ring (WaitForFrameRing) before rewriting a request buffer, so one
        // buffer per brick slot is race-free.
        if (m_brickPoolEnabled) {
            var bakeBlockBytes = SdfWorldInterfaces.BrickBakeLayout.Groups.Single(predicate: static group => (group.Group == ShaderInterfaceGroup.Pass)).BlockSizeBytes;
            var bakeBlock = scope.Own(created: gpu.BufferFactory.CreateHostVisible(
                name: NameOf(detail: "block", part: "brick-bake"),
                sizeBytes: ((ulong)UniformBytes(blockBytes: bakeBlockBytes)),
                usage: GpuBufferUsage.Uniform
            ));

            bakeBlock.Write<uint>(data: [((uint)MaxBrickBakeVoxelsPerSlice)]);
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
                WriteBuffer(buffer: requestBuffer, layout: SdfWorldInterfaces.BrickBakeLayout, member: SdfWorldInterfaces.BakeRequest, set: bakeSet);
                WriteBuffer(buffer: m_brickPoolBuffer, layout: SdfWorldInterfaces.BrickBakeLayout, member: SdfWorldInterfaces.BakePool, set: bakeSet);
            }
        }

        SdfShaderSetVerification.VerifyShaderSet(
            device: device,
            kernels: pipelines.Kernels,
            verify: VerifyIsaVersion
        );

        // The "uploaded once" seam: the program (and its screen-surface table) is uploaded here and normally never
        // again — frames move entities by rewriting only the small dynamic-transform buffer. UploadProgram is the
        // single owner of per-program derived state (its capacity checks trivially pass for the construction program).
        UploadProgram(program: options.Program);
        scope.Complete();
    }

    // A change-detected view cache (one row per ring slot, or per view slot, initialized 0 = nothing bound yet).
    private static nint[][] BuildRingViewCache(int width, int rows = FrameRingSize) {
        var cache = new nint[rows][];

        for (var slot = 0; (slot < rows); slot++) {
            cache[slot] = new nint[width];
        }

        return cache;
    }
    // Writes a buffer at an interface member's binding, as the kind its member declares and at its element's stride, the
    // structured view the kernel's generated declaration reads on Direct3D 12.
    private void WriteBuffer(nint set, ShaderInterfaceLayout layout, string member, IGpuBuffer buffer) {
        var resource = layout.Groups.Single(predicate: static group => (group.Group == ShaderInterfaceGroup.Pass)).Resources.Single(predicate: candidate => string.Equals(
            a: candidate.Member.Name,
            b: member,
            comparisonType: StringComparison.Ordinal
        ));

        m_bindings.WriteBuffer(
            binding: resource.Binding,
            bufferHandle: buffer.BufferHandle,
            bufferSize: buffer.SizeBytes,
            descriptorSetHandle: set,
            elementStride: resource.Member.Type!.Value.SizeBytes(),
            kind: resource.Kind
        );
    }
    // Writes a buffer at a member of the world interface in a views set.
    private void WriteWorldBuffer(nint set, string member, IGpuBuffer buffer) =>
        WriteBuffer(
            buffer: buffer,
            layout: SdfWorldInterfaces.WorldLayout,
            member: member,
            set: set
        );

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        // Drain the device BEFORE destroying anything (tolerating an already-lost device, where there is nothing
        // left to drain): the host permits frames in flight, and this engine's resources can
        // be referenced by OTHER in-flight work this engine's own fences cannot see — a view engine released mid-run
        // (the reveal transition) is destroyed while the MAIN engine's in-flight frame still samples its output as a
        // screen source.
        m_deviceContext.TryWaitIdle();

        m_readback?.Dispose();

        for (var slot = 0; (slot < FrameRingSize); slot++) {
            m_frameFences[slot].Dispose();
            m_commandPools[slot].Dispose();
        }

        DisposeRegions();
        m_frameRegion.Dispose();

        foreach (var block in m_viewBlocks) {
            block.Dispose();
        }

        m_brickBakeBlock?.Dispose();
        m_cullBoundsBuffer.Dispose();
        m_viewsArgsBuffer.Dispose();
        m_tileBuffer.Dispose();
        m_primaryHitBuffer.Dispose();
        m_instanceMaskBuffer.Dispose();

        foreach (var requestBuffer in m_brickRequestBuffers) {
            requestBuffer?.Dispose();
        }

        m_brickPoolBuffer.Dispose();
        m_bindings.DestroySampler(
            samplerHandle: m_screenSampler
        );
        m_bindings.DestroyPool(
            poolHandle: m_pool
        );

        DisposeViewOutputs();
        m_screenSourceFiller.Dispose();
        m_meshFramebuffer.Dispose();
        m_meshDepth.Dispose();
        m_meshTarget.Dispose();
        m_glyphAtlasUpload?.Dispose();
    }
}
