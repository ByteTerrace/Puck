using System.Numerics;
using Puck.Abstractions.Gpu;
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
/// all four dispatched indirectly from those args →
/// <c>sdf-world-composite.comp</c> (source-agnostic region composite, also dispatched indirectly). Fully
/// backend-neutral through its device's <see cref="IGpuDeviceContext.Services"/>.
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
    private const uint BrickBakePoolBindingIndex = 1;    // sdf-brick-bake.comp: brickPool RW (register u0)
    private const int BrickBakePushByteLength = (sizeof(uint) * 4); // BrickBakePush { uint sliceVoxelStart, sliceVoxelCount, 2x pad }
    private const uint BrickBakeRequestBindingIndex = 0; // sdf-brick-bake.comp: bakeRequest (register t0)
    private const int BrickBakeRequestHeaderFloat4Count = 3; // (boxMin+cellSize), (dims+carveCount), (destWordOffset+invLambda) — KEEP IN SYNC with sdf-brick-bake.comp
    private const uint BrickBakeWorkgroupSize = 64; // sdf-brick-bake.comp's [numthreads(64, 1, 1)]
    // The sdfBrickPool binding number (sdf-vm.hlsli's [[vk::binding(46, 0)]]); the per-consumer Direct3D 12 register is
    // POSITIONAL (views append it LAST -> t41, the beam after its instance mask -> t4). KEEP IN SYNC with sdf-vm.hlsli.
    private const uint BrickPoolBindingIndex = 46;
    private const uint CompositeOutputBindingIndex = 0; // sdf-world-composite.comp: Output at binding 0
    private const int CompositePushByteLength = ((16 + ((sizeof(float) * 4) * MaxViewports)) + (sizeof(uint) * 4)); // CompositeParams2: uint2 extent + uint count + uint padding (16) + float4 rects[5] + uint2 scaleQPacked + uint2 sharpnessQPacked
    private const uint CompositeSourceBindingIndex = 1; // sdf-world-composite.comp: sources[] at binding 1
    private const uint CullArgsBindingIndex = 5; // sdf-cull-args.comp: views indirect dispatch args (register u0)
    private const uint CullBoundsBindingIndex = 6; // sdf-cull-args.comp: bbox group origin and exclusive end (register u1); read by sdf-world-views.comp at binding 8
    private const ulong CullBoundsByteLength = (sizeof(uint) * 4); // the dispatch box sdf-cull-args.comp writes: the group origin, then the exclusive end
    private const int DecalBufferCells = (DecalDescriptorCount + (MaxScreenSurfaces * MaxScreenDecalCells));
    // The decal buffer's leading DESCRIPTOR band (one uint4 per screen slot) precedes the shared cell region; a screen's
    // cell run starts at DecalDescriptorCount + screenIndex * MaxScreenDecalCells (KEEP IN SYNC with sdfSampleGlyphDecal).
    private const int DecalDescriptorCount = MaxScreenSurfaces;
    private const int DecalWordsPerCell = 4; // one uint4 per cell/descriptor (KEEP IN SYNC with sdf-world.hlsli's sdfDecalCells)
    private const uint DynamicTransformBindingIndex = 9; // sdf-vm.hlsli's [[vk::binding(9, 0)]] / register(t2) (world path)
    private const int DynamicTransformByteLength = ((sizeof(float) * 4) * 3); // 48-byte rigid transform: float4 position (xyz + .w = soft-shadow participation: 0 casts / 1 shadow-suppressed) + float4 orientation quaternion + float4 anonymous Lanes (DynamicTransform.Lanes) (KEEP IN SYNC with sdf-vm.hlsli sdfDynamicTransforms: position.w is read by sdfShadowParticipationActive's per-instance skip in sdf-world.hlsli, the third row by SDF_OP_LANE_ERODE's currentLanes and shade-volumes.hlsli's selected intensity lane)
    private const GpuPixelFormat Format = GpuPixelFormat.R8G8B8A8Unorm;
    // Ring-local frame instance grid (binding 47): rebuilt after dynamic transforms only when moving, maskable
    // instances exist; invariant programs seed every slot once at UploadProgram. Instance-cull reads it at t3; views
    // at t42.
    private const uint FrameInstanceGridBindingIndex = 47;
    private const uint InstanceMaskBindingIndex = 7; // sdf-instance-cull.comp (u0) writes the per-tile instance mask; sdf-beam.comp (t3) and the views layout (t37) read it; the per-tile word count is the LIVE uploaded program's InstanceMaskWordCount (pushed per frame, capped at the construction width the buffer was sized for)
    private const int MaxBrickBakeVoxelsPerSlice = (256 * 1024); // <= 256K voxels per brick per produced frame: ~1-2 ms background-budget
    private const int MaxBrickCarvesPerBake = 4096; // request-buffer carve capacity per slot (the debug pool's MaxCarves ceiling)
    private const uint ProgramBindingIndex = 1; // matches sdf-vm.hlsli's [[vk::binding(1, 0)]] / register(t0)
    private const int PushConstantByteLength = ((sizeof(uint) * 4) * 2); // 32-byte CompositeParams; word 5 = screenMask, word 6 = instanceMaskWordCount, word 7 = sampleIndex (the deterministic tick clock the sky reads). KEEP IN SYNC with sdf-world.hlsli's CompositeParams.
    private const uint ScreenLightBindingIndex = 11; // shared hit-pass layout: sdfScreenLights, register t38 (per-frame screen glow colors + environment; KEEP IN SYNC with sdf-world.hlsli)
    private const int ScreenLightByteLength = ((sizeof(float) * 4) * ((MaxScreenSurfaces + 8) + SdfEnvironment.RowCount)); // float4 rgb+intensity per screen (0..MaxScreenSurfaces-1) + env (MaxScreenSurfaces) + FOUR grid-lock rows (+1..+4) + the engine-bench params row (+5) + the shadow-policy row (+6) + the far-field row (+7) + the environment block (+8 onward: SdfEnvironment's row layout) — KEEP IN SYNC with sdf-world.hlsli SdfGridWorld..SdfEnvBase
    private const float ScreenLightIntensity = 2.5f; // room-glow gain applied to each screen's average color
    // The FIRST screen-source binding index; screenSource{i} binds at ScreenSourceBindingBase + i (sdf-world.hlsli's
    // vk::binding). The glyph atlas follows the whole run, so ScreenSourceBindingBase + MaxScreenSurfaces is its binding.
    private const uint ScreenSourceBindingBase = 12;
    private const uint ScreenSurfaceBindingIndex = 10; // shared hit-pass layout: screenSurfaces, register t4
    private const int ScreenSurfaceByteLength = ((sizeof(float) * 4) * 3); // 48-byte ScreenSurfaceData: right.xyz+halfWidth, up.xyz+halfHeight, origin.xyz+pad (KEEP IN SYNC with sdf-world.hlsli)
    private const uint TileBindingIndex = 3; // the cull buffer: u0 in the beam (its writer), t0 in cull-args, t44 in the views layout
    // The tile cull buffer carries FOUR planes per (viewport, tile), each of stride
    // (tileGrid.x * tileGrid.y * viewportCount): plane 0 = the march-start lower bound (the classic beam
    // output; the ONLY plane cull-args + the compositor read, so their indexing is unchanged), plane 1 =
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
    /// <c>ConeNear</c> in sdf-world.hlsli.</summary>
    public const float ConeNear = 0.02f;
    /// <summary>The edge of one screen tile in pixels, the unit the beam, the instance masks and the cull buffer
    /// count in. KEEP IN SYNC with <c>WorldTileSize</c> in sdf-world.hlsli.</summary>
    public const uint TileSize = 16;

    private const uint ViewSourceBindingIndex = 4; // sdf-world-views.comp and sdf-sky.comp: sources[] at u0..u4
    private const uint ViewportBindingIndex = 2; // matches sdf-world.hlsli's [[vk::binding(2, 0)]]
    private const int ViewportByteLength = ((sizeof(float) * 4) * 6); // 96-byte ViewportData incl. the renderScale row (KEEP IN SYNC with sdf-world.hlsli)
    private const ulong ViewsArgsByteLength = (sizeof(uint) * 3); // the three indirect group counts sdf-cull-args.comp writes
    private const uint ViewsCullBoundsBindingIndex = 8; // sdf-world-views.comp: the dispatch box, its group origin and exclusive end (register t3); the source array is ONE binding number (4) whose 5 elements pack into derived heap slots, so 8 never collides
    // Bounded flow/cloud volumes (sdfVolumes), shared by the views and sky passes: appended LAST in the views binding
    // list, so its SRV resolves to register t43 (after sdfFrameInstanceGrid t42). KEEP IN SYNC with sdf-world.hlsli.
    private const uint VolumeBindingIndex = 48;
    private const uint PrimaryHitBindingIndex = 49; // sdfVisibilityRecords written by primary, surface and ambient: u5, after the five source images
    private const uint PrimaryHitReadBindingIndex = 50; // the same buffer read-only for views: t45, after the cull buffer (sdf-visibility.hlsli)
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
    /// device-local scratch (tile / instance-mask / indirect-args / cull-bounds buffers, the per-view source
    /// textures) stays shared: the top-of-frame barrier in <c>Record</c> orders each frame's GPU work after the
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
    /// hand-syncing). 32 separate combined-image-sampler bindings (not one array binding): DXC's
    /// <c>vk::combinedImageSampler</c> only fuses a scalar Texture2D+SamplerState pair, so a true single Vulkan
    /// combined-image-sampler array isn't expressible in the shared HLSL — see <see cref="ScreenSourceBindingIndices"/>.
    /// Capped at 32 because <c>screenMask</c> (the per-frame bound-slot bitmask, CompositeParams word 5) is a single
    /// <c>uint</c> — raising past 32 needs a second mask word on both sides.</summary>
    public const int MaxScreenSurfaces = Puck.SignedDistance.SdfProgramBuilder.MaxScreenSurfaces;
    /// <summary>The kernels' source array length (<c>sources[5]</c>) — the most viewports one engine composites.</summary>
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

    // The device's region-copy pipeline (GpuRegionCopyPipelineCache), which every staged region records its copy with;
    // the engine never owns it.
    private readonly IGpuComputePipeline m_regionCopyPipeline;
    // The carve-bake brick pool: one persistent device-local f32 buffer the sliced bake writes and
    // the beam + views kernels sample. Always allocated (a 1-float filler when the pool is disabled), always bound to
    // the beam/views sets, since both kernels compile the sdfBrickPool binding unconditionally (SDF_SAMPLED_REGIONS).
    private readonly IGpuBuffer m_brickPoolBuffer;
    private readonly bool m_brickPoolEnabled;
    private readonly int m_brickPoolVoxelCapacity;
    private readonly IGpuStorageBuffer m_compositeArgsBuffer;
    private readonly IGpuComputePipeline m_compositePipeline;
    private readonly IGpuComputePipeline m_cullArgsPipeline;
    private readonly nint m_cullArgsSet;
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
    // Shares Stage 1's exact bindings array (PipelineLayouts.Views) and push/sampler shape, so its descriptor-set layout is
    // identically defined and the shared per-slot m_viewsSets bind against it too — the same reuse m_viewsCorePipeline
    // already established, so this pipeline needs no descriptor sets of its own.
    private readonly IGpuComputePipeline m_skyPipeline;
    private readonly IGpuImage?[] m_sourceTextures;
    private readonly IGpuImage m_storageImage;

    private IGpuBuffer m_tileBuffer;

    private readonly uint m_tileGridX;
    private readonly uint m_tileGridY;
    private readonly uint m_viewportCapacity;
    private readonly byte[] m_viewportScratch;
    // The RETAINED copy of the last produced frame's packed viewport rows, and the ring of buffers Stage 1 reprojects
    // through. Held explicitly rather than by reading the ring's other slot: the ring slot's contents are only defined
    // relative to the produced-frame count, and an engine whose caller ever produces an odd number of frames between
    // two renders would silently reproject through the wrong camera.
    private readonly IGpuBuffer m_viewsArgsBuffer;
    // The core-ops Stage 1 variant (see SdfViewsKernelVariant): same bindings array as m_viewsPipeline, so its
    // descriptor-set layout is identically defined — the per-slot m_viewsSets bind against WHICHEVER pipeline
    // UploadProgram selected (compatible layouts on Vulkan; the same slot packing + root-signature shape on
    // Direct3D 12), and no second set/descriptor-write path exists.
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
    private bool m_imageInitialized;
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
    // Cadence gate, latched by PrepareFrame and read by Record: when true, Record skips the mask/beam/cull-args/views
    // passes and re-composites from the retained (single, ring-shared) views output — pixel-identical
    // because the change signature below proved every input those passes consume is unchanged.
    private bool m_skipThisFrame;
    private SdfViewsKernelVariant m_viewsVariant;

    // shared hit-pass layout: screenSource0..MaxScreenSurfaces-1, registers t5.. — one binding per screen
    // index (KEEP IN SYNC with sdf-world.hlsli's screenSource declarations). DERIVED from the base + count so the list
    // can never drift from MaxScreenSurfaces (the D3D12 heap-packing discipline: never hand-count a binding run).
    private static readonly uint[] ScreenSourceBindingIndices = BuildScreenSourceBindingIndices();
    // shared hit-pass layout: the SDF_SHAPE_GLYPH font atlas, register t39 (SRV, after screenLights t38) +
    // static sampler s32 (after the 32 screen samplers s0..s31) — APPENDED LAST in PipelineLayouts.Views so the D3D12 registers
    // land there; DERIVED as the first binding past the 32 screen sources (12..43). KEEP IN SYNC with sdf-vm.hlsli's
    // sdfGlyphAtlas.
    private static readonly uint GlyphAtlasBindingIndex = (ScreenSourceBindingBase + ((uint)MaxScreenSurfaces));
    // shared hit-pass layout: the GLYPH DECAL buffer, register t40 — appended AFTER the glyph atlas
    // (t39), DERIVED so it can never drift when the screen-source run grows. KEEP IN SYNC with sdf-world.hlsli's
    // sdfDecalCells (Vulkan binding 45).
    private static readonly uint DecalCellsBindingIndex = (GlyphAtlasBindingIndex + 1u);
    // The render passes, in submission order — the GPU work ledger's column labels (see SdfWorldEngine.Work.cs, whose
    // public PassLabels exposes this list). Adding a pass is naming it here and adding its EnterPass/LeavePass bracket
    // where it submits (SdfWorldEngine.Record.cs); every reader (PassLabels, CadenceSkippedPassLabels, the ledger's
    // per-pass counts) then picks it up automatically.
    private static readonly string[] PassLabelTable = ["upload", "sky", "mask", "beam", "cull-args", "primary", "surface", "ambient", "views", "composite"];
    private readonly nint[] m_beamSets = new nint[FrameRingSize];
    // The change-detected descriptor caches are PER RING SLOT: each slot's sets are only rewritten once that slot's
    // fence proves its previous frame retired, so a descriptor update can never race an in-flight command buffer.
    // They cover ENGINE-OWNED views only — a host-owned view (a screen source) is rebound
    // unconditionally, since its handle value is not a durable identity (BindScreenSources' handle-identity rule).
    private readonly nint[][] m_boundScreenSourceViews = BuildRingViewCache(width: MaxScreenSurfaces);
    private readonly nint[][] m_boundSourceViews = BuildRingViewCache(width: MaxViewports);
    private readonly nint[] m_boundGlyphAtlasViews = new nint[FrameRingSize];
    private readonly IGpuCommandPool[] m_commandPools = new IGpuCommandPool[FrameRingSize];
    private readonly byte[] m_compositePush = new byte[CompositePushByteLength];
    private readonly nint[] m_compositeSets = new nint[FrameRingSize];
    // One per-submit fence per ring slot: PrepareFrame waits slot k's fence (frame k − FrameRingSize) before
    // rewriting slot k's resources; the fenced submit re-arms it.
    private readonly IGpuSubmissionFence[] m_frameFences = new IGpuSubmissionFence[FrameRingSize];
    private readonly nint[] m_instanceCullSets = new nint[FrameRingSize];
    private readonly byte[] m_pushConstant = new byte[PushConstantByteLength];
    private readonly nint[] m_screenSourceViews = new nint[MaxScreenSurfaces];
    // The screen-light table (screen glow colors, environment, grid-overlay and lever rows) and the bounded-volume table
    // (views and sky), each packed here every frame and written into its region.
    private readonly byte[] m_screenLightScratch = new byte[ScreenLightByteLength];
    private readonly Vector3[] m_screenLightColors = new Vector3[MaxScreenSurfaces];
    private readonly byte[] m_volumeScratch = new byte[(MaxVolumes * VolumeByteLength)];
    private readonly byte[] m_brickBakePush = new byte[BrickBakePushByteLength];
    private readonly IGpuStorageBuffer[] m_brickRequestBuffers = new IGpuStorageBuffer[SdfBrickPoolLayout.MaxBricks];
    private readonly nint[] m_brickBakeSets = new nint[SdfBrickPoolLayout.MaxBricks];
    private readonly BrickBakeState[] m_brickStates = new BrickBakeState[SdfBrickPoolLayout.MaxBricks];
    private readonly ulong[] m_brickSerials = new ulong[SdfBrickPoolLayout.MaxBricks];
    private readonly int[] m_brickTotalVoxels = new int[SdfBrickPoolLayout.MaxBricks];
    private readonly int[] m_brickVoxelCursor = new int[SdfBrickPoolLayout.MaxBricks];
    private readonly Vector4[] m_brickRequestScratch = new Vector4[(BrickBakeRequestHeaderFloat4Count + MaxBrickCarvesPerBake)];
    private readonly nint[] m_viewsSets = new nint[FrameRingSize];
    private SdfProgram m_liveProgram = null!;

    internal uint[] CopyLiveProgramWords() => m_liveProgram.Words.ToArray();

    /// <summary>Gets or sets the debug-group label wrapping this engine's whole recorded frame — the outer scope a GPU
    /// capture (RenderDoc / PIX / Nsight) shows around this engine's per-pass groups (so a nested view engine reads as
    /// <c>view:&lt;name&gt;</c> containing its own mask/beam/cull-args/views/composite). Presentation-only; defaults to
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
    /// <param name="width">The composited output width in pixels.</param>
    /// <param name="height">The composited output height in pixels.</param>
    /// <param name="options">The construction options (scene program, capacities, child mask, export seam).</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A dimension is zero, the viewport capacity is 0 or above
    /// <see cref="MaxViewports"/>, or the options enable a brick pool the pipelines were built without.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="pipelines"/> has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The device's descriptor heap cannot admit the engine's pool
    /// (<see cref="CheckAdmission"/>, checked before anything is allocated), or the loaded shader bytecode does not report
    /// the host's <see cref="Puck.SignedDistance.SdfIsa.Version"/>.</exception>
    public SdfWorldEngine(IGpuDeviceContext device, SdfWorldPipelines pipelines, IGpuComputePipeline regionCopy, uint width, uint height, SdfWorldEngineOptions options) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(pipelines);
        ArgumentNullException.ThrowIfNull(regionCopy);
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
        m_compositePipeline = pipelines.Pipeline(index: CompositePipelineIndex);
        m_regionCopyPipeline = regionCopy;

        // One FULL-SIZE source texture per viewport slot — Stage 1 renders the viewport's region-extent into it,
        // Stage 2 copies that into the screen region. Sized to the FULL frame extent (the largest any region can
        // reach), NOT any one frame's region: the regions animate every frame, so a frozen region-sized texture (e.g. a
        // half-width split) under-allocated the pane and blanked it when the layout grew. Writes/reads stay within the
        // live region (≤ full), so full-size is always in-bounds.
        m_sourceTextures = new IGpuImage?[((int)m_viewportCapacity)];

        for (var index = 0; (index < ((int)m_viewportCapacity)); index++) {
            m_sourceTextures[index] = scope.Own(created: gpu.ImageFactory.Create(
                name: NameOf(part: "sources", index: index),
                format: Format,
                height: height,
                usage: GpuImageUsage.Sampled | GpuImageUsage.Storage,
                width: width
            ));
        }

        // A dedicated 1x1 ShaderReadOnly filler for an unbound screen-source slot: the per-viewport sources[] filler
        // (slot 0's source texture) is wrong here — it lives in the General (UAV) layout Stage 1/2 read/write it in,
        // while a combined-image-sampler binding requires ShaderReadOnly, so aliasing it trips Vulkan validation the
        // moment any viewport-source dispatch runs. This image is transitioned ONCE, below, and never written again.
        m_screenSourceFiller = scope.Own(created: gpu.ImageFactory.Create(
            name: NameOf(part: "screen-source-filler"),
            format: Format,
            height: 1,
            usage: GpuImageUsage.Sampled | GpuImageUsage.Storage,
            width: 1
        ));

        // The output image is either a plain same-device storage image (resolved from the neutral factory) or an
        // exportable one supplied by the host (cross-backend present). Only the FINAL output crosses the seam; the
        // per-view sources are always internal.
        m_storageImage = scope.Own(created: ((options.CreateOutputImage is null)
            ? gpu.ImageFactory.Create(
                name: NameOf(part: "output"),
                format: Format,
                height: height,
                usage: GpuImageUsage.Sampled | GpuImageUsage.Storage,
                width: width
            )
            : options.CreateOutputImage(device)
        ));
        m_exportableImage = (m_storageImage as IGpuExportableImage);
        m_exportMode = (m_exportableImage is not null);

        // The program region holds the live program, not the options' reserve: a probed worst case can run to hundreds
        // of megabytes, which a ring holds once per slot. A larger program grows the region by half again.
        m_programWordReserve = options.ProgramWordCapacity;
        m_programWordCapacity = options.Program.Words.Length;
        // Every region's copy sets, before any region writes them: the copy pools the admission states.
        m_regionCopySets = ReserveRegionCopySets(scope: scope);
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
                copySets: m_regionCopySets[BrickStagingRegionIndex],
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

        // Stage 2's full-frame composite grid is constant for the run, so its dispatch is driven INDIRECTLY: the GPU
        // reads the (x, y, z) group counts from this host-written args buffer (vkCmdDispatchIndirect / ExecuteIndirect)
        // instead of the CPU supplying them. The counts equal the equivalent direct dispatch, so it is pixel-neutral
        // (the `world` parity gate is the guard). Host-written once + host-coherent, so the queue-submit host-write
        // visibility covers it with no indirect-read barrier.
        m_compositeArgsBuffer = scope.Own(created: gpu.BufferFactory.CreateHostVisible(
            name: NameOf(part: "composite-args"),
            sizeBytes: (sizeof(uint) * 3),
            usage: GpuBufferUsage.Storage | GpuBufferUsage.Indirect
        ));
        m_compositeArgsBuffer.Write<uint>(data: [
            ((width + (WorkgroupEdge - 1)) / WorkgroupEdge),
            ((height + (WorkgroupEdge - 1)) / WorkgroupEdge),
            1u,
        ]);


        // One pool, one CULL-ARGS set (its bindings are all shared device-local buffers, never rewritten after
        // construction) plus FrameRingSize copies of the other four sets (they bind the per-slot host-visible buffers,
        // and the views/composite copies take per-frame descriptor rewrites) — the Direct3D 12 allocator bump-allocates
        // a non-overlapping heap region per set (like a Vulkan pool), so they never clobber. The capacity is DERIVED
        // from the binding lists (an array binding contributes its full Count), so it can never drift out of sync when
        // a binding is added or MaxViewports/FrameRingSize changes.
        var poolSizes = DescriptorPoolSizes(brickPool: m_brickPoolEnabled);

        m_pool = m_bindings.CreatePool(
            name: NameOf(part: "descriptors"),
            sizes: poolSizes
        );
        // The sets allocated from the pool are released with it.
        _ = scope.Own(
            handle: m_pool,
            release: m_bindings.DestroyPool
        );

        // The cull buffer is read-only here (a stride-4 SRV on Direct3D 12); the args + bounds are written (UAVs).
        m_cullArgsSet = m_bindings.AllocateSet(
            name: NameOf(part: "cull-args"),
            descriptorSetLayoutHandle: m_cullArgsPipeline.DescriptorSetLayoutHandle,
            poolHandle: m_pool
        );
        WriteStorageBufferReadOnly(
            binding: TileBindingIndex,
            buffer: m_tileBuffer,
            set: m_cullArgsSet
        );
        WriteStorageBufferReadWrite(
            binding: CullArgsBindingIndex,
            buffer: m_viewsArgsBuffer,
            set: m_cullArgsSet
        );
        WriteStorageBufferReadWrite(
            binding: CullBoundsBindingIndex,
            buffer: m_cullBoundsBuffer,
            set: m_cullArgsSet
        );

        // The screen sources (bindings 12..43) are (re)bound per frame by BindScreenSources, mirroring the source array —
        // a filler view isn't known until the first frame's SDF source texture (or child surface) exists.
        m_screenSampler = scope.Own(
            handle: m_bindings.CreateSampler(filter: GpuSamplerFilter.Nearest),
            release: m_bindings.DestroySampler
        );

        for (var slot = 0; (slot < FrameRingSize); slot++) {
            var beamSet = m_bindings.AllocateSet(
                name: NameOf(part: "beam", index: slot),
                descriptorSetLayoutHandle: m_beamPipeline.DescriptorSetLayoutHandle,
                poolHandle: m_pool
            );

            m_beamSets[slot] = beamSet;
            WriteStorageBufferReadWrite(
                binding: TileBindingIndex,
                buffer: m_tileBuffer,
                set: beamSet
            );
            WriteStorageBufferReadOnly(
                binding: InstanceMaskBindingIndex,
                buffer: m_instanceMaskBuffer,
                set: beamSet
            );
            // The brick pool (a stride-4 float SRV, like the instance mask above — never the stride-16 program SRV).
            WriteStorageBufferReadOnly(
                binding: BrickPoolBindingIndex,
                buffer: m_brickPoolBuffer,
                set: beamSet
            );

            // The instance-cull set: the mask buffer written (the frame's first pass — the beam then reads it).
            var instanceCullSet = m_bindings.AllocateSet(
                name: NameOf(part: "instance-cull", index: slot),
                descriptorSetLayoutHandle: m_instanceCullPipeline.DescriptorSetLayoutHandle,
                poolHandle: m_pool
            );

            m_instanceCullSets[slot] = instanceCullSet;
            WriteStorageBufferReadWrite(
                binding: InstanceMaskBindingIndex,
                buffer: m_instanceMaskBuffer,
                set: instanceCullSet
            );

            var viewsSet = m_bindings.AllocateSet(
                name: NameOf(part: "views", index: slot),
                descriptorSetLayoutHandle: m_viewsPipeline.DescriptorSetLayoutHandle,
                poolHandle: m_pool
            );

            m_viewsSets[slot] = viewsSet;
            WriteStorageBufferReadOnly(
                binding: TileBindingIndex,
                buffer: m_tileBuffer,
                set: viewsSet
            );
            WriteStorageBufferReadOnly(
                binding: ViewsCullBoundsBindingIndex,
                buffer: m_cullBoundsBuffer,
                set: viewsSet
            );
            WriteStorageBufferReadOnly(
                binding: InstanceMaskBindingIndex,
                buffer: m_instanceMaskBuffer,
                set: viewsSet
            );
            // The brick pool (a stride-4 float SRV — the shared read side; the bake set below binds the same buffer as a UAV).
            WriteStorageBufferReadOnly(
                binding: BrickPoolBindingIndex,
                buffer: m_brickPoolBuffer,
                set: viewsSet
            );
            WriteStorageBufferReadWrite(
                binding: PrimaryHitBindingIndex,
                buffer: m_primaryHitBuffer,
                set: viewsSet
            );
            WriteStorageBufferReadOnly(
                binding: PrimaryHitReadBindingIndex,
                buffer: m_primaryHitBuffer,
                set: viewsSet
            );

            var compositeSet = m_bindings.AllocateSet(
                name: NameOf(part: "composite", index: slot),
                descriptorSetLayoutHandle: m_compositePipeline.DescriptorSetLayoutHandle,
                poolHandle: m_pool
            );

            m_compositeSets[slot] = compositeSet;
            m_bindings.WriteStorageImage(
                arrayElement: 0,
                binding: CompositeOutputBindingIndex,
                descriptorSetHandle: compositeSet,
                imageViewHandle: m_storageImage.ImageViewHandle
            );
            BindRegions(slot: slot);

            // The source array (binding the SDF view textures and any hosted child surfaces) is (re)bound per frame by
            // BindSources — child image-views aren't known until their nodes have produced.
            m_commandPools[slot] = scope.Own(created: gpu.CommandPoolFactory.Create(name: NameOf(
                part: "commands",
                index: slot
            )));
            m_frameFences[slot] = scope.Own(created: gpu.QueueSubmitter.CreateSubmissionFence());
        }

        // The carve-bake baker's per-slot request buffers + static descriptor sets (only when the pool is enabled). Each
        // slot owns a host-visible request buffer (header + up to MaxBrickCarvesPerBake carves) and a set binding that
        // buffer (t0 SRV) + the shared pool (u0 UAV). These are NOT per-ring-slot: a bake spans frames and RequestBrickBake
        // drains the ring (WaitForFrameRing) before rewriting a request buffer, so one buffer per brick slot is race-free.
        if (m_brickPoolEnabled) {
            for (var brick = 0; (brick < SdfBrickPoolLayout.MaxBricks); brick++) {
                var requestBuffer = scope.Own(created: gpu.BufferFactory.CreateHostVisible(
                    name: NameOf(detail: "request", index: brick, part: "brick-bake"),
                    sizeBytes: (((ulong)m_brickRequestScratch.Length) * (sizeof(float) * 4)),
                    usage: GpuBufferUsage.Storage
                ));

                m_brickRequestBuffers[brick] = requestBuffer;

                var bakeSet = m_bindings.AllocateSet(
                    name: NameOf(part: "brick-bake", index: brick),
                    descriptorSetLayoutHandle: m_brickBakePipeline!.DescriptorSetLayoutHandle,
                    poolHandle: m_pool
                );

                m_brickBakeSets[brick] = bakeSet;
                // The request buffer is a float4 (stride-16) SRV; the pool is the stride-4 UAV the baker writes.
                WriteStorageBuffer(
                    binding: BrickBakeRequestBindingIndex,
                    buffer: requestBuffer,
                    set: bakeSet
                );
                WriteStorageBufferReadWrite(
                    binding: BrickBakePoolBindingIndex,
                    buffer: m_brickPoolBuffer,
                    set: bakeSet
                );
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

    // A per-ring-slot change-detected view cache (one row per slot, initialized 0 = nothing bound yet).
    private static nint[][] BuildRingViewCache(int width) {
        var cache = new nint[FrameRingSize][];

        for (var slot = 0; (slot < FrameRingSize); slot++) {
            cache[slot] = new nint[width];
        }

        return cache;
    }
    // The screen-source binding indices — screenSource{i} at ScreenSourceBindingBase + i — derived from
    // MaxScreenSurfaces so the run can never drift from the cap (never hand-listed).
    private static uint[] BuildScreenSourceBindingIndices() {
        var indices = new uint[MaxScreenSurfaces];

        for (var index = 0; (index < MaxScreenSurfaces); index++) {
            indices[index] = (ScreenSourceBindingBase + ((uint)index));
        }

        return indices;
    }
    // The MaxScreenSurfaces screen-source SampledImage bindings, spread into PipelineLayouts.Views in screen-index order so the
    // D3D12 registers land contiguously (t5..t36). Derived from the same index list the per-frame (re)binds use, so the
    // descriptor pool (GpuDescriptorPoolSizes.ForSets, which counts these) and the writes can never disagree.
    private static GpuComputeBinding[] BuildScreenSourceBindings() {
        var bindings = new GpuComputeBinding[MaxScreenSurfaces];

        for (var index = 0; (index < MaxScreenSurfaces); index++) {
            bindings[index] = new GpuComputeBinding(
                Binding: ScreenSourceBindingIndices[index],
                Kind: GpuComputeBindingKind.SampledImage
            );
        }

        return bindings;
    }
    // The program words: a read-only StructuredBuffer<uint4>.
    private void WriteStorageBuffer(nint set, uint binding, IGpuBuffer buffer) =>
        m_bindings.WriteBuffer(
            binding: binding,
            bufferHandle: buffer.BufferHandle,
            bufferSize: buffer.SizeBytes,
            descriptorSetHandle: set,
            elementStride: (4 * sizeof(uint)),
            kind: GpuBindingKind.ReadOnlyBuffer
        );
    // A read-only StructuredBuffer of 4-byte elements (the float cull buffer, the uint cull bounds). The program words'
    // 16-byte stride over the 8-byte bounds buffer is a zero-element view on Direct3D 12.
    private void WriteStorageBufferReadOnly(nint set, uint binding, IGpuBuffer buffer) =>
        m_bindings.WriteBuffer(
            binding: binding,
            bufferHandle: buffer.BufferHandle,
            bufferSize: buffer.SizeBytes,
            descriptorSetHandle: set,
            elementStride: sizeof(uint),
            kind: GpuBindingKind.ReadOnlyBuffer
        );
    // A RWStructuredBuffer of 4-byte elements.
    private void WriteStorageBufferReadWrite(nint set, uint binding, IGpuBuffer buffer) =>
        m_bindings.WriteBuffer(
            binding: binding,
            bufferHandle: buffer.BufferHandle,
            bufferSize: buffer.SizeBytes,
            descriptorSetHandle: set,
            elementStride: sizeof(uint),
            kind: GpuBindingKind.ReadWriteBuffer
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

        m_compositeArgsBuffer.Dispose();
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

        foreach (var source in m_sourceTextures) {
            source?.Dispose();
        }

        m_screenSourceFiller.Dispose();
        m_glyphAtlasUpload?.Dispose();
        m_storageImage.Dispose();
    }
}
