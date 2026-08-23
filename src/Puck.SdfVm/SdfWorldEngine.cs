using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>
/// The device-explicit core of the compute SDF world pipeline — the one truth for its buffer/push/binding layouts.
/// One instance owns a scene program (uploaded to the GPU once, at construction) plus every pipeline/buffer/image the
/// six kernels need, and runs the full chain per frame: <c>sdf-sky.comp</c> (fills every source pixel with the
/// authored sky, direct — a beam-culled tile's pixel is otherwise never touched by any later pass) →
/// <c>sdf-instance-cull.comp</c> (per-tile instance mask) → <c>sdf-beam.comp</c> (tile-cull cone-march prepass) →
/// <c>sdf-cull-args.comp</c> (GPU-written indirect dispatch args: the surviving-tile bbox) →
/// <c>sdf-world-views.comp</c> (per-view render, dispatched indirectly from those args) →
/// <c>sdf-world-composite.comp</c> (source-agnostic region composite, also dispatched indirectly). Fully
/// backend-neutral through the <see cref="IGpuComputeServices"/> seam.
/// <para>
/// Three submission models, and they must never blur — nor run against one engine instance at overlapping times, since
/// all three re-record the shared per-slot command buffers: <see cref="RenderFrame"/> is the deterministic harness path —
/// one submit-and-wait plus a readback (validation, headless render). <see cref="SubmitFrame"/> is the live node path —
/// fire-and-forget behind the engine's own <see cref="FrameRingSize"/>-deep frame ring (each slot's fence orders that
/// slot's rewrites against its previous submission, so a pipelining host needs no per-frame device drain), plus the
/// export-mode queue drain when the output crosses a backend seam.
/// <see cref="SubmitFramePipelined"/> is the demo-preview path — a non-blocking fenced readback (submit fire-and-forget,
/// poll <see cref="IsFramePixelsReady"/> on a later produced frame, then <see cref="AcquireFramePixels"/> maps it), so
/// the live in-editor bake preview never idles the shared present queue mid-sculpt. It stays frame-count driven
/// (determinism is a feature even here), and a single-in-flight guard forbids interleaving it with the other two on one
/// engine — <see cref="RenderFrame"/>, <see cref="SubmitFrame"/>, and <see cref="SubmitFramePipelined"/> each throw while
/// a pipelined frame is outstanding. Adding a wait to <see cref="SubmitFrame"/> is a frame-rate regression; removing the
/// wait from <see cref="RenderFrame"/> is a nondeterminism bug.
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
    private const int CompositePushByteLength = ((16 + ((sizeof(float) * 4) * MaxViewports)) + (sizeof(uint) * 4)); // CompositeParams2: uint2 extent + uint count + 4 bytes padding (16) + float4 rects[5] + uint2 scaleQPacked + uint2 sharpnessQPacked
    private const uint CompositeSourceBindingIndex = 1; // sdf-world-composite.comp: sources[] at binding 1
    private const uint CullArgsBindingIndex = 5; // sdf-cull-args.comp: views indirect dispatch args (register u0)
    private const uint CullBoundsBindingIndex = 6; // sdf-cull-args.comp: bbox group origin (register u1); read by sdf-world-views.comp at binding 8
    private const int DecalBufferCells = (DecalDescriptorCount + (MaxScreenSurfaces * MaxScreenDecalCells));
    // The decal buffer's leading DESCRIPTOR band (one uint4 per screen slot) precedes the shared cell region; a screen's
    // cell run starts at DecalDescriptorCount + screenIndex * MaxScreenDecalCells (KEEP IN SYNC with sdfSampleGlyphDecal).
    private const int DecalDescriptorCount = MaxScreenSurfaces;
    private const int DecalWordsPerCell = 4; // one uint4 per cell/descriptor (KEEP IN SYNC with sdf-world.hlsli's sdfDecalCells)
    private const uint DynamicTransformBindingIndex = 9; // sdf-vm.hlsli's [[vk::binding(9, 0)]] / register(t2) (world path)
    private const int DynamicTransformByteLength = ((sizeof(float) * 4) * 2); // 32-byte rigid transform: float4 position (xyz + .w = soft-shadow participation: 0 casts / 1 shadow-suppressed) + float4 orientation quaternion (KEEP IN SYNC with sdf-vm.hlsli sdfDynamicTransforms: position.w is read by sdfShadowParticipationActive's per-instance skip in sdf-world.hlsli)
    private const GpuPixelFormat Format = GpuPixelFormat.R8G8B8A8Unorm;
    // Ring-local frame instance grid (binding 47): rebuilt after dynamic transforms only when moving, maskable
    // instances exist; invariant programs seed every slot once at UploadProgram. Instance-cull reads it at t3; views
    // at t42.
    private const uint FrameInstanceGridBindingIndex = 47;
    private const uint InstanceMaskBindingIndex = 7; // sdf-beam.comp (u1) / sdf-world-views.comp (t13): per-tile instance mask, written by the beam prepass, read by Stage 1; the per-tile word count is the LIVE uploaded program's InstanceMaskWordCount (pushed per frame, capped at the construction width the buffer was sized for)
    private const int MaxBrickBakeVoxelsPerSlice = (256 * 1024); // <= 256K voxels per brick per produced frame: ~1-2 ms background-budget
    private const int MaxBrickCarvesPerBake = 4096; // request-buffer carve capacity per slot (the debug pool's MaxCarves ceiling)
    private const uint ProgramBindingIndex = 1; // matches sdf-vm.hlsli's [[vk::binding(1, 0)]] / register(t0)
    private const int PushConstantByteLength = (((sizeof(uint) * 4) * 2) + (sizeof(uint) * 2)); // 40-byte CompositeParams; word 6 = screenMask, word 7 = instanceMaskWordCount, word 8 = sampleIndex (the shadow estimator's deterministic net index), word 9 = the shadow accumulator's epoch + enable bit. Vulkan guarantees 128 bytes of push range, so this stays well inside the floor.
    // The area-light shadow estimator's host-baked sampler table (SdfShadowSamplerTables): the digital net's direction
    // numbers plus the sun disc's quantized polar map. Stage 1 ONLY — it is the only kernel that shades. Binding 48;
    // its Direct3D 12 register is POSITIONAL (views append it LAST, after the frame instance grid t42, so it resolves
    // to t43). KEEP IN SYNC with sdf-world.hlsli's sdfSamplerTable.
    private const uint SamplerTableBindingIndex = 48;
    private const uint ScreenLightBindingIndex = 11; // sdf-world-views.comp (Stage 1 ONLY): sdfScreenLights, register t38 (per-frame screen glow colors + environment; KEEP IN SYNC with sdf-world.hlsli)
    private const int ScreenLightByteLength = ((sizeof(float) * 4) * (MaxScreenSurfaces + 22)); // float4 rgb+intensity per screen (0..MaxScreenSurfaces-1) + env (MaxScreenSurfaces) + FOUR grid-lock rows (+1..+4: world grid, object origin+pitchX, object frame quat, object pitchZ+patchRadius) + ONE engine-bench params row (+5: soft-shadow/AO/shadow-distance/screen-light levers) + ONE shadow-policy row (+6: carve proxy/camera-tile mask/fast march) + ONE F1 far-field row (+7: far-bound disable / F2 shadow-exit disable) + FIVE lighting rows (+8..+12: sun direction+weight, sun tangent+ambient base, sun bitangent+ambient hemisphere, sun color, ambient color) + NINE procedural-sky rows (+13..+21: zenith+fogDensity, horizon+skyEnabled, ground+sunDiscIntensity, sunDiscExponent+starDensity+starBrightness+starSeed, twinkleShare+twinkleDepth+twinklePeriodTicks, cloudColor+cloudCoverage, cloudSoftness+cloudScale+cloudSeed, cloudOffset+shearOffset, cloudSpinAngle+cloudCurl) — KEEP IN SYNC with sdf-world.hlsli SdfGridWorld..SdfSkyCloudsD
    private const float ScreenLightIntensity = 2.5f; // room-glow gain applied to each screen's average color
    // The FIRST screen-source binding index; screenSource{i} binds at ScreenSourceBindingBase + i (sdf-world.hlsli's
    // vk::binding). The glyph atlas follows the whole run, so ScreenSourceBindingBase + MaxScreenSurfaces is its binding.
    private const uint ScreenSourceBindingBase = 12;
    private const uint ScreenSurfaceBindingIndex = 10; // sdf-world-views.comp (Stage 1 ONLY): screenSurfaces, register t4
    private const int ScreenSurfaceByteLength = ((sizeof(float) * 4) * 3); // 48-byte ScreenSurfaceData: right.xyz+halfWidth, up.xyz+halfHeight, origin.xyz+pad (KEEP IN SYNC with sdf-world.hlsli)
    // The shadow accumulator's history lives in the per-view source texture's alpha lane, which is undefined until
    // Stage 1 has written the pixel at least once. These force the recurrence to seed from the raw estimate for the
    // first frames after construction — the textures are allocated once and never reallocated, so that is the only
    // window in which the lane holds whatever the allocator left behind.
    private const int ShadowAccumulationResetFrames = 2;
    // The sun disc's angular radius in radians — the half-aperture the shadow estimator samples. tan(0.11) = 0.11045
    // reproduces the retired parabola's 1/9 = 0.1111 penumbra half-slope to within 0.7%, so the shipped look is the
    // same shadows sampled correctly rather than a jolt in penumbra width. KEEP IN SYNC with sdf-world.hlsli's
    // SunAngularRadius, which is documentation on the GPU side: only the host evaluates tan().
    private const double SunAngularRadius = 0.11d;
    private const uint TileBindingIndex = 3; // matches sdf-world.hlsli's [[vk::binding(3, 0)]]
    // The tile cull buffer carries FOUR planes per (viewport, tile), each of stride
    // (tileGrid.x * tileGrid.y * viewportCount): plane 0 = the march-start lower bound (the classic beam
    // output; the ONLY plane cull-args + the compositor read, so their indexing is unchanged), plane 1 =
    // firstExit, plane 2 = secondEntry — the four-bound teleport's proven-empty gap [firstExit, secondEntry]
    // (Larsson "The Gunk") — and plane 3 = the F1 far bound (the depth past which the tile's cone cannot produce
    // any footprint-accepted hit through MaxDistance). The extra planes are written by sdf-beam and read by
    // sdf-world-views only; a tile with no proven gap/far bound packs MaxDistance (teleport/far-exit disabled),
    // so every plane is a total function.
    // KEEP IN SYNC with WorldTilePlaneCount + worldTilePlaneStride in sdf-world.hlsli / sdf-tile.hlsli.
    private const uint TilePlaneCount = 4;
    private const uint TileSize = 16; // KEEP IN SYNC with WorldTileSize in sdf-world.hlsli
    private const uint TimingCapacity = 8; // timestamp slots per pool (headroom over the marks; must stay >= TimingMarkCount)
    // One timing pool more than the ring depth, so the pool read back by TryReadPassTimings (frame N−2's — the
    // newest frame the slot fence PROVES complete) is never the one the current frame is about to reset.
    private const int TimingPoolCount = (FrameRingSize + 1);
    private const uint ViewSourceBindingIndex = 4; // sdf-world-views.comp: sources[] LAST (after the fixed 1/2/3)
    private const uint ViewportBindingIndex = 2; // matches sdf-world.hlsli's [[vk::binding(2, 0)]]
    private const int ViewportByteLength = ((sizeof(float) * 4) * 6); // 96-byte ViewportData incl. the renderScale row (KEEP IN SYNC with sdf-world.hlsli)
    private const uint ViewsCullBoundsBindingIndex = 8; // sdf-world-views.comp: the bbox origin (register t3); the source array is ONE binding number (4) whose 5 elements pack into derived heap slots, so 8 never collides
    private const uint WorkgroupEdge = 8;

    /// <summary>The default carve-bake brick pool capacity in voxels (f32 words) — <see cref="SdfBrickPoolLayout.TotalVoxels"/>
    /// = 16.7M voxels = 64 MB, i.e. <see cref="SdfBrickPoolLayout.MaxBricks"/> slots at full resolution.</summary>
    public const int DefaultBrickPoolVoxelCapacity = SdfBrickPoolLayout.TotalVoxels;
    /// <summary>The frame-ring depth: how many produced frames may be in flight on the GPU at once. Every per-frame
    /// mutable resource — the command buffer, the host-visible per-frame buffers (viewport / dynamic-transform /
    /// screen-surface / screen-light / decal), the descriptor sets that bind them, and the per-submit fence — is
    /// duplicated per slot, so re-recording/rewriting slot <c>k</c> only requires frame <c>k − FrameRingSize</c> to
    /// have retired (the slot fence wait in <c>PrepareFrame</c>), never a whole-device drain. The GPU-written
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
    /// Capped at 32 because <c>screenMask</c> (the per-frame bound-slot bitmask, CompositeParams word 6) is a single
    /// <c>uint</c> — raising past 32 needs a second mask word on both sides.</summary>
    public const int MaxScreenSurfaces = Puck.SignedDistance.SdfProgramBuilder.MaxScreenSurfaces;
    /// <summary>The kernels' source array length (<c>sources[5]</c>) — the most viewports one engine composites.</summary>
    public const int MaxViewports = 5;

    private readonly IGpuComputePipeline m_beamPipeline;
    private readonly IGpuShaderModule m_beamShaderModule;
    // The bake pipeline + per-slot request buffers/sets — created ONLY when the pool is enabled (nothing bakes into a
    // filler). Each slot owns a host-visible request buffer (header + carve list) and a static descriptor set binding
    // that buffer + the shared pool (as a UAV). The per-slot state advances one slice per produced frame (RecordBrickBakeSlices).
    private readonly IGpuComputePipeline? m_brickBakePipeline;
    private readonly IGpuShaderModule? m_brickBakeShaderModule;
    // The host-baked brick path: one staging buffer + descriptor set per ring slot (a frame's copy reads the staging
    // its own slot wrote), and a queue of pending uploads drained one per produced frame (RecordBrickUpload).
    private readonly IGpuComputePipeline? m_brickUploadPipeline;
    private readonly IGpuShaderModule? m_brickUploadShaderModule;
    private readonly IGpuStorageBuffer?[] m_brickUploadStaging = new IGpuStorageBuffer?[FrameRingSize];
    private readonly nint[] m_brickUploadSets = new nint[FrameRingSize];
    private readonly Queue<(int Slot, int Count, float[] Voxels)> m_brickUploads = new();
    private readonly byte[] m_brickUploadPush = new byte[BrickBakePushByteLength];
    // The carve-bake brick pool: one persistent device-local f32 buffer the sliced bake writes and
    // the beam + views kernels sample. Always allocated (a 1-float filler when the pool is disabled), always bound to
    // the beam/views sets, since both kernels compile the sdfBrickPool binding unconditionally (SDF_SAMPLED_REGIONS).
    private readonly IGpuBuffer m_brickPoolBuffer;
    private readonly bool m_brickPoolEnabled;
    private readonly int m_brickPoolVoxelCapacity;
    private readonly uint m_childMask;
    private readonly IGpuStorageBuffer m_compositeArgsBuffer;
    private readonly IGpuComputePipeline m_compositePipeline;
    private readonly IGpuShaderModule m_compositeShaderModule;
    private readonly IGpuComputePipeline m_cullArgsPipeline;
    private readonly nint m_cullArgsSet;
    private readonly IGpuShaderModule m_cullArgsShaderModule;
    private readonly IGpuBuffer m_cullBoundsBuffer;
    private readonly IGpuDescriptorAllocator m_descriptorAllocator;
    private readonly IGpuDeviceContext m_deviceContext;
    private readonly nint m_deviceHandle;
    private readonly int m_dynamicTransformCapacity;
    private readonly byte[] m_dynamicTransformScratch;
    private readonly bool m_exportMode;
    private readonly IGpuExportableStorageImage? m_exportableImage;
    private readonly IGpuComputeServices m_gpu;
    private readonly uint m_height;
    private readonly int m_instanceCapacity;
    private readonly IGpuComputePipeline m_instanceCullPipeline;
    private readonly IGpuShaderModule m_instanceCullShaderModule;
    private readonly SdfInstanceGridInput[] m_instanceGridInputScratch;
    private readonly int m_instanceGridWordCapacity;
    private readonly SdfInstanceGrid.Workspace m_instanceGridWorkspace;
    private readonly IGpuBuffer m_instanceMaskBuffer;
    private readonly int m_instanceMaskWordCount;
    private readonly bool m_liveArmedTiming;
    private readonly nint m_pool;
    private readonly IGpuStorageBuffer m_programBuffer;
    private readonly int m_programWordCapacity;
    private readonly IGpuStorageBuffer m_samplerTableBuffer;
    private readonly nint m_screenSampler;
    private readonly IGpuStorageImage m_screenSourceFiller;
    // Shares Stage 1's exact bindings array (viewsBindings) and push/sampler shape, so its descriptor-set layout is
    // identically defined and the shared per-slot m_viewsSets bind against it too — the same reuse m_viewsCorePipeline
    // already established, so this pipeline needs no descriptor sets of its own.
    private readonly IGpuComputePipeline m_skyPipeline;
    private readonly IGpuShaderModule m_skyShaderModule;
    private readonly IGpuStorageImage?[] m_sourceTextures;
    private readonly IGpuStorageImage m_storageImage;
    private readonly IGpuBuffer m_tileBuffer;
    private readonly uint m_tileGridX;
    private readonly uint m_tileGridY;
    // Timing is AVAILABLE when a supported factory + recorder were supplied; whether a given frame is timed is a
    // separate per-frame decision (m_frameTimingActive). In the default (eager) mode m_frameTimingActive == available
    // every frame; in live-armed mode it also requires GpuTimingControl.Shared.Armed, and the pools below are created
    // lazily on the first armed frame (so a disarmed live node pays nothing). The factory + device are retained for
    // that lazy creation.
    private readonly bool m_timingAvailable;
    private readonly GpuTimestampCapabilities m_timingCapabilities;
    private readonly IGpuTimingPoolFactory? m_timingFactory;
    private readonly IGpuTimingRecorder? m_timingRecorder;
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
    private readonly IGpuShaderModule m_viewsCoreShaderModule;
    private readonly IGpuComputePipeline m_viewsPipeline;
    private readonly IGpuShaderModule m_viewsShaderModule;
    private readonly uint m_width;

    private SdfCadenceDiagnostics m_cadenceDiagnostics;
    private ulong m_cadenceRenderedFrameCount;
    private ulong m_cadenceSkippedFrameCount;
    private int m_currentSlot;
    private ulong m_decalRevision;
    private bool m_disposed;
    // Latched at the top of each Record: whether THIS frame writes timing marks (available, and in live-armed mode
    // also GpuTimingControl.Shared.Armed). Read again by the submit paths so the readback + m_timingFrame advance
    // agree with what Record recorded, even if the shared arming flips mid-frame.
    private bool m_frameTimingActive;
    // The SDF_SHAPE_GLYPH font atlas: a STATIC texture uploaded once via SetGlyphAtlas (a re-set re-uploads). Held as an
    // IGpuSurfaceUpload (owns the image + staging + the returned view), the current sampleable view, and the last-bound
    // view for a change-detected (re)bind — sound here because the atlas is ENGINE-owned, and SetGlyphAtlas clears this
    // cache itself when a re-upload retires the previous view. Null/0 until set — the
    // glyph binding then samples the neutral 1×1 filler (m_screenSourceFiller) and every SDF_SHAPE_GLYPH reads the
    // saturated band, so a glyph-free program with no atlas is safe.
    private IGpuSurfaceUpload? m_glyphAtlasUpload;
    private nint m_glyphAtlasView;
    private bool m_hasPreviousCadenceSpanHashes;
    private bool m_hasPreviousFrameSignature;
    private bool m_imageInitialized;
    private double? m_lastFrameGpuMilliseconds;
    // The CPU cost of the MOST RECENT produced frame's per-frame instance-grid rebuild (BuildFrameInstanceGrid + the
    // ring slot's buffer write) — null whenever that frame's live program is grid-invariant (built once at
    // UploadProgram) and so skipped the rebuild. A plain wall-clock Stopwatch span, not a GPU query: this is CPU-side
    // work (host-built CSR bins), the counterpart to the GPU pass timings for measuring a per-frame-moving dynamic
    // instance set's cost (a bench/ceiling-measurement seam — see SdfBenchScene.DynamicMatrix).
    private double? m_lastInstanceGridRebuildMilliseconds;
    private int m_liveInstanceMaskWordCount;
    private SdfViewsKernelVariant? m_loggedViewsVariant;
    private bool m_pipelinedFrameInFlight;
    // Diagnostics only: the previous decided frame's independent per-span hashes (each starting fresh from the FNV
    // basis — unlike m_previousFrameSignature's chained fold, so one span's hash never smears into another's), the
    // cumulative skip/render counts since the gate last armed, and the latest published SdfCadenceDiagnostics. None
    // of this feeds DecideCadenceSkip's skip decision.
    private CadenceSpanHashes m_previousCadenceSpanHashes;
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
    // passes and re-composites from the retained (single, ring-shared) views output + tile buffer — pixel-identical
    // because the change signature below proved every input those passes consume is unchanged.
    private bool m_skipThisFrame;
    private ulong m_timingFrame;
    private IGpuTimingPool[]? m_timingPools;
    private bool m_useCoreViews;

    // sdf-world-views.comp (Stage 1 ONLY): screenSource0..MaxScreenSurfaces-1, registers t5.. — one binding per screen
    // index (KEEP IN SYNC with sdf-world.hlsli's screenSource declarations). DERIVED from the base + count so the list
    // can never drift from MaxScreenSurfaces (the D3D12 heap-packing discipline: never hand-count a binding run).
    private static readonly uint[] ScreenSourceBindingIndices = BuildScreenSourceBindingIndices();
    // sdf-world-views.comp (Stage 1 ONLY): the SDF_SHAPE_GLYPH font atlas, register t39 (SRV, after screenLights t38) +
    // static sampler s32 (after the 32 screen samplers s0..s31) — APPENDED LAST in viewsBindings so the D3D12 registers
    // land there; DERIVED as the first binding past the 32 screen sources (12..43). KEEP IN SYNC with sdf-vm.hlsli's
    // sdfGlyphAtlas.
    private static readonly uint GlyphAtlasBindingIndex = (ScreenSourceBindingBase + ((uint)MaxScreenSurfaces));
    // sdf-world-views.comp (Stage 1 ONLY): the GLYPH DECAL buffer, register t40 — appended AFTER the glyph atlas
    // (t39), DERIVED so it can never drift when the screen-source run grows. KEEP IN SYNC with sdf-world.hlsli's
    // sdfDecalCells (Vulkan binding 45).
    private static readonly uint DecalCellsBindingIndex = (GlyphAtlasBindingIndex + 1u);
    // The GPU timing marks: one frame-start mark (query 0, top of pipe), then one BOTTOM-OF-PIPE close per render pass,
    // in submission order. The PASS between mark i and i+1 is named PassLabels[i]; the whole frame is mark 0 .. mark
    // last. Adding a pass is TWO edits — append its label here AND its WriteTimingMark close in Record — after which the
    // sdf.info verb, the [world-timing] line, and the bench's per-pass feed all surface it with no further change (each
    // reads PassTimingLabels / TryReadPassTimings, never a hardcoded tuple). TimingCapacity (8) is the pool ceiling, so
    // at most 7 passes fit before the pools must be resized.
    private static readonly string[] PassLabels = ["sky", "mask", "beam", "cull-args", "views", "composite"];
    private static readonly uint TimingMarkCount = ((uint)(PassLabels.Length + 1));
    private readonly nint[] m_beamSets = new nint[FrameRingSize];
    // The change-detected descriptor caches are PER RING SLOT: each slot's sets are only rewritten once that slot's
    // fence proves its previous frame retired, so a descriptor update can never race an in-flight command buffer.
    // They cover ENGINE-OWNED views only — a host-owned view (a screen source, a child's storage image) is rebound
    // unconditionally, since its handle value is not a durable identity (BindScreenSources' handle-identity rule).
    private readonly nint[][] m_boundScreenSourceViews = BuildRingViewCache(width: MaxScreenSurfaces);
    private readonly nint[][] m_boundSourceViews = BuildRingViewCache(width: MaxViewports);
    private readonly nint[] m_boundGlyphAtlasViews = new nint[FrameRingSize];
    private readonly nint[] m_childSourceViews = new nint[MaxViewports];
    private readonly IGpuComputeCommandPool[] m_commandPools = new IGpuComputeCommandPool[FrameRingSize];
    private readonly byte[] m_compositePush = new byte[CompositePushByteLength];
    private readonly nint[] m_compositeSets = new nint[FrameRingSize];
    private readonly IGpuStorageBuffer[] m_dynamicTransformBuffers = new IGpuStorageBuffer[FrameRingSize];
    // One per-submit fence per ring slot: PrepareFrame waits slot k's fence (frame k − FrameRingSize) before
    // rewriting slot k's resources; the fenced submit re-arms it.
    private readonly IGpuSubmissionFence[] m_frameFences = new IGpuSubmissionFence[FrameRingSize];
    private readonly nint[] m_instanceCullSets = new nint[FrameRingSize];
    private readonly IGpuStorageBuffer[] m_instanceGridBuffers = new IGpuStorageBuffer[FrameRingSize];
    private readonly byte[] m_pushConstant = new byte[PushConstantByteLength];
    private readonly nint[] m_screenSourceViews = new nint[MaxScreenSurfaces];
    private readonly IGpuStorageBuffer[] m_screenSurfaceBuffers = new IGpuStorageBuffer[FrameRingSize];
    // The host-side mirror of the screen-surface table: UploadProgram seeds it from the program's declared surfaces;
    // SetScreenSurface patches one entry's slice for a per-frame transform (a screen riding a dynamic entity, e.g. a
    // slab riding a moving rig). PrepareFrame uploads it only when m_screenSurfaceDirty[slot] is set — Write<T> always
    // copies from the buffer's start, so there is no partial-range GPU write to ride instead.
    private readonly byte[] m_screenSurfaceScratch = new byte[(MaxScreenSurfaces * ScreenSurfaceByteLength)];
    // Per-ring-slot dirty flags, same pattern as m_decalDirty: UploadProgram and a value-changing SetScreenSurface call
    // both dirty EVERY slot (each slot must catch up when its turn comes); PrepareFrame clears only the current slot's
    // flag after the upload rides. Starts all-true (BuildRingDirtyFlags) so each slot's first frame uploads at least
    // once — a freshly allocated GPU buffer is not guaranteed zeroed.
    private readonly bool[] m_screenSurfaceDirty = BuildRingDirtyFlags();
    private readonly IGpuStorageBuffer[] m_screenLightBuffers = new IGpuStorageBuffer[FrameRingSize];
    private readonly byte[] m_screenLightScratch = new byte[ScreenLightByteLength];
    private readonly Vector3[] m_screenLightColors = new Vector3[MaxScreenSurfaces];
    // The per-frame GLYPH DECAL buffer (Stage 1 only): the leading per-screen descriptor band + the shared cell region,
    // uploaded each frame like the screen-light buffer. All-zero (every descriptor's gridCols 0) => inert, so a program
    // that declares no decal renders byte-identically. uint[] so the descriptor/cell packing is direct word writes.
    private readonly IGpuStorageBuffer[] m_decalBuffers = new IGpuStorageBuffer[FrameRingSize];
    private readonly uint[] m_decalScratch = new uint[(DecalBufferCells * DecalWordsPerCell)];
    // Per ring slot, starting all-true so each slot's first frame uploads the (all-zero) mirror at least once — a
    // freshly allocated GPU buffer is not guaranteed zeroed. SetScreenDecal/ClearScreenDecal dirty EVERY slot (each
    // slot's buffer must catch up when its turn comes); PrepareFrame clears only the current slot's flag after the
    // upload rides. The bare room never touches decals, so this keeps the 820 KB decal buffers off the per-frame
    // upload path.
    private readonly bool[] m_decalDirty = BuildRingDirtyFlags();
    private readonly byte[] m_brickBakePush = new byte[BrickBakePushByteLength];
    private readonly IGpuStorageBuffer[] m_brickRequestBuffers = new IGpuStorageBuffer[SdfBrickPoolLayout.MaxBricks];
    private readonly nint[] m_brickBakeSets = new nint[SdfBrickPoolLayout.MaxBricks];
    private readonly BrickBakeState[] m_brickStates = new BrickBakeState[SdfBrickPoolLayout.MaxBricks];
    private readonly ulong[] m_brickSerials = new ulong[SdfBrickPoolLayout.MaxBricks];
    private readonly int[] m_brickTotalVoxels = new int[SdfBrickPoolLayout.MaxBricks];
    private readonly int[] m_brickVoxelCursor = new int[SdfBrickPoolLayout.MaxBricks];
    private readonly Vector4[] m_brickRequestScratch = new Vector4[(BrickBakeRequestHeaderFloat4Count + MaxBrickCarvesPerBake)];
    private readonly IGpuStorageBuffer[] m_viewportBuffers = new IGpuStorageBuffer[FrameRingSize];
    private readonly nint[] m_viewsSets = new nint[FrameRingSize];
    private SdfProgram m_liveProgram = null!;
    private int m_shadowAccumulationResetFrames = ShadowAccumulationResetFrames;

    /// <summary>Gets or sets the debug-group label wrapping this engine's whole recorded frame — the outer scope a GPU
    /// capture (RenderDoc / PIX / Nsight) shows around this engine's per-pass groups (so a nested view engine reads as
    /// <c>view:&lt;name&gt;</c> containing its own mask/beam/cull-args/views/composite). Presentation-only; defaults to
    /// <c>world</c> and never affects rendered output.</summary>
    public string DebugLabel { get; set; } = "world";

    /// <summary>Initializes a new instance of the <see cref="SdfWorldEngine"/> class: builds the world pipelines
    /// (the five chain passes plus the Stage 1 core-ops variant), every buffer and image at the provisioned viewport
    /// capacity, and uploads the scene program once.</summary>
    /// <param name="gpu">The neutral GPU compute services.</param>
    /// <param name="device">The GPU device the engine renders on.</param>
    /// <param name="kernels">The compiled world kernel set for the same backend as <paramref name="device"/>.</param>
    /// <param name="width">The composited output width in pixels.</param>
    /// <param name="height">The composited output height in pixels.</param>
    /// <param name="options">The construction options (scene program, capacities, child mask, export/timing seams).</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A dimension is zero, or the viewport capacity is 0 or above <see cref="MaxViewports"/>.</exception>
    /// <exception cref="InvalidOperationException">The loaded shader bytecode does not report the host's
    /// <see cref="Puck.SignedDistance.SdfIsa.Version"/>.</exception>
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
        m_dynamicTransformCapacity = Math.Max(
            val1: Math.Max(
                val1: 1,
                val2: options.DynamicTransformCapacity
            ),
            val2: options.Program.RequiredDynamicTransformCapacity
        );
        m_dynamicTransformScratch = new byte[(m_dynamicTransformCapacity * DynamicTransformByteLength)];
        m_gpu = gpu;
        m_height = height;
        m_instanceCapacity = Math.Max(
            val1: options.Program.Instances.Count,
            val2: options.InstanceCapacity
        );
        m_instanceGridInputScratch = new SdfInstanceGridInput[m_instanceCapacity];
        m_instanceGridWorkspace = new SdfInstanceGrid.Workspace(maxInstances: m_instanceCapacity);
        m_instanceGridWordCapacity = SdfInstanceGrid.WordCapacity(maxInstances: m_instanceCapacity);
        m_tileGridX = ((width + (TileSize - 1)) / TileSize);
        m_tileGridY = ((height + (TileSize - 1)) / TileSize);
        m_viewportCapacity = options.ViewportCapacity;
        m_viewportScratch = new byte[(((int)m_viewportCapacity) * ViewportByteLength)];
        m_width = width;

        m_beamShaderModule = gpu.ShaderModuleFactory.Create(
            deviceContext: device,
            stage: GpuShaderStage.Compute,
            bytecode: kernels.Beam
        );
        m_instanceCullShaderModule = gpu.ShaderModuleFactory.Create(
            deviceContext: device,
            stage: GpuShaderStage.Compute,
            bytecode: kernels.InstanceCull
        );
        m_cullArgsShaderModule = gpu.ShaderModuleFactory.Create(
            deviceContext: device,
            stage: GpuShaderStage.Compute,
            bytecode: kernels.CullArgs
        );
        m_viewsShaderModule = gpu.ShaderModuleFactory.Create(
            deviceContext: device,
            stage: GpuShaderStage.Compute,
            bytecode: kernels.Views
        );
        m_viewsCoreShaderModule = gpu.ShaderModuleFactory.Create(
            deviceContext: device,
            stage: GpuShaderStage.Compute,
            bytecode: kernels.ViewsCore
        );
        m_skyShaderModule = gpu.ShaderModuleFactory.Create(
            deviceContext: device,
            stage: GpuShaderStage.Compute,
            bytecode: kernels.Sky
        );
        m_compositeShaderModule = gpu.ShaderModuleFactory.Create(
            deviceContext: device,
            stage: GpuShaderStage.Compute,
            bytecode: kernels.Composite
        );

        // One FULL-SIZE source texture per NON-child viewport slot — Stage 1 renders the viewport's region-extent into
        // it, Stage 2 copies that into the screen region. Sized to the FULL frame extent (the largest any region can
        // reach), NOT any one frame's region: the regions animate every frame, so a frozen region-sized texture (e.g. a
        // half-width split) under-allocated the pane and blanked it when the layout grew. Writes/reads stay within the
        // live region (≤ full), so full-size is always in-bounds. Child slots stay null: their source is the hosted
        // child's storage image (bound per frame via SetChildSource), and the child owns that image's layout, so the
        // engine never creates or transitions one.
        m_sourceTextures = new IGpuStorageImage?[((int)m_viewportCapacity)];

        for (var index = 0; (index < ((int)m_viewportCapacity)); index++) {
            if (IsChildSlot(slot: index)) {
                continue;
            }

            m_sourceTextures[index] = gpu.StorageImageFactory.Create(
                deviceContext: device,
                format: Format,
                height: height,
                width: width
            );
        }

        // A dedicated 1x1 ShaderReadOnly filler for an unbound screen-source slot: the per-viewport sources[] filler
        // (SourceViewForSlot(0)) is wrong here — it lives in the General (UAV) layout Stage 1/2 read/write it in,
        // while a combined-image-sampler binding requires ShaderReadOnly, so aliasing it trips Vulkan validation the
        // moment any viewport-source dispatch runs. This image is transitioned ONCE, below, and never written again.
        m_screenSourceFiller = gpu.StorageImageFactory.Create(
            deviceContext: device,
            format: Format,
            height: 1,
            width: 1
        );

        // The output image is either a plain same-device storage image (resolved from the neutral factory) or an
        // exportable one supplied by the host (cross-backend present). Only the FINAL output crosses the seam; the
        // per-view sources are always internal.
        m_storageImage = ((options.CreateOutputImage is null)
            ? gpu.StorageImageFactory.Create(
                deviceContext: device,
                format: Format,
                height: height,
                width: width
            )
            : options.CreateOutputImage(device)
        );
        m_exportableImage = (m_storageImage as IGpuExportableStorageImage);
        m_exportMode = (m_exportableImage is not null);

        m_programWordCapacity = Math.Max(
            val1: options.Program.Words.Length,
            val2: options.ProgramWordCapacity
        );
        m_programBuffer = gpu.StorageBufferFactory.Create(
            deviceContext: device,
            sizeBytes: (((ulong)m_programWordCapacity) * sizeof(uint))
        );
        // The shadow sampler table is built ONCE here and uploaded ONCE — it is a pure function of the sun's angular
        // radius, not of the frame or the program, so it never enters the per-frame ring. Rebuilding it per frame
        // would be 65.8 KB of pointless traffic; that it can be built once is the whole reason the transcendentals
        // live on the host.
        m_samplerTableBuffer = gpu.StorageBufferFactory.Create(
            deviceContext: device,
            sizeBytes: (((ulong)SdfShadowSamplerTables.WordCount) * sizeof(uint))
        );

        var samplerTable = new uint[SdfShadowSamplerTables.WordCount];

        SdfShadowSamplerTables.Build(
            destination: samplerTable,
            sunAngularRadius: SunAngularRadius
        );
        m_samplerTableBuffer.Write<uint>(data: samplerTable);
        // The HOST-VISIBLE per-frame buffers are duplicated per ring slot (see FrameRingSize): slot k's copies are
        // only rewritten after slot k's fence proves frame k − FrameRingSize retired, so a frame's in-place upload
        // can never race the previous frame's in-flight reads.
        for (var slot = 0; (slot < FrameRingSize); slot++) {
            m_viewportBuffers[slot] = gpu.StorageBufferFactory.Create(
                deviceContext: device,
                sizeBytes: ((ulong)m_viewportScratch.Length)
            );
            m_dynamicTransformBuffers[slot] = gpu.StorageBufferFactory.Create(
                deviceContext: device,
                sizeBytes: ((ulong)m_dynamicTransformScratch.Length)
            );
            m_instanceGridBuffers[slot] = gpu.StorageBufferFactory.Create(
                deviceContext: device,
                sizeBytes: (((ulong)m_instanceGridWordCapacity) * sizeof(uint))
            );
            // The screen-surface table: always allocated at MaxScreenSurfaces capacity, indexed directly by screen index
            // (like the always-bound dynamic-transform slot), so Stage 1's binding stays valid for a program with none —
            // an all-zero undeclared slot is never addressed (no material id in a consistent program points at it).
            m_screenSurfaceBuffers[slot] = gpu.StorageBufferFactory.Create(
                deviceContext: device,
                sizeBytes: (MaxScreenSurfaces * ((ulong)ScreenSurfaceByteLength))
            );
            // The per-frame screen-light buffer: the screen colors + environment float4s, uploaded each frame like the
            // dynamic-transform buffer. Bound to the views set only (Stage 1 shades; the beam prepass does not).
            m_screenLightBuffers[slot] = gpu.StorageBufferFactory.Create(
                deviceContext: device,
                sizeBytes: ((ulong)m_screenLightScratch.Length)
            );
            // The glyph-decal buffer: descriptor band + cell region, uploaded per frame like the screen-light buffer.
            m_decalBuffers[slot] = gpu.StorageBufferFactory.Create(
                deviceContext: device,
                sizeBytes: (((ulong)m_decalScratch.Length) * sizeof(uint))
            );
        }
        // The cull buffer is GPU-written by the beam prepass (a UAV), so it is device-local (a Direct3D 12 default heap).
        // Sized for TilePlaneCount planes (marchStart + firstExit + secondEntry — the four-bound teleport — plus the F1
        // far bound); cull-args and the compositor read only plane 0, so their (viewport, tile) indexing is unaffected by
        // the extra capacity.
        m_tileBuffer = gpu.StorageBufferFactory.CreateDeviceLocal(
            deviceContext: device,
            sizeBytes: ((((((ulong)TilePlaneCount) * m_viewportCapacity) * m_tileGridX) * m_tileGridY) * sizeof(float))
        );
        // The per-tile instance mask: same (viewport, tile) indexing as the cull buffer, GPU-written by the beam
        // prepass alongside it (a UAV, so device-local too), read by Stage 1 to gate its masked map() calls. The
        // buffer is sized for the CONSTRUCTION program's width (ceil(instanceCount/32) uints, at least 1 —
        // SdfProgram.InstanceMaskWordCount); the kernels index with the LIVE uploaded program's width, pushed per
        // frame (m_liveInstanceMaskWordCount), which UploadProgram caps at this construction width.
        m_instanceMaskWordCount = Math.Max(
            val1: options.Program.InstanceMaskWordCount,
            val2: SdfProgram.InstanceMaskWordCountFor(instanceCount: options.InstanceCapacity)
        );
        var instanceMaskStorageWordCount = (m_instanceMaskWordCount + ((m_instanceMaskWordCount + 31) / 32));

        m_instanceMaskBuffer = gpu.StorageBufferFactory.CreateDeviceLocal(
            deviceContext: device,
            sizeBytes: ((((((ulong)m_viewportCapacity) * m_tileGridX) * m_tileGridY) * ((uint)instanceMaskStorageWordCount)) * sizeof(uint))
        );

        // The carve-bake brick pool: one persistent DEVICE-LOCAL f32 buffer — device-local so the
        // bake kernel can write it as a UAV (an upload heap forbids UAVs on Direct3D 12) and the beam/views sample it as
        // an SRV. Frozen at the constructed capacity. When the pool is disabled (capacity 0) a single-float filler keeps
        // the always-present sdfBrickPool binding valid — the kernels compile the binding unconditionally, and
        // sdfSampledRegion detects the filler by its element count and renders SampledRegion programs via the
        // conservative uncarved-hull fallback (only RequestBrickBake stays rejected on a pool-less engine).
        m_brickPoolVoxelCapacity = Math.Max(
            val1: 0,
            val2: options.BrickPoolVoxelCapacity
        );
        m_brickPoolEnabled = (m_brickPoolVoxelCapacity > 0);
        m_brickPoolBuffer = gpu.StorageBufferFactory.CreateDeviceLocal(
            deviceContext: device,
            sizeBytes: (((ulong)(m_brickPoolEnabled
            ? m_brickPoolVoxelCapacity
            : 1)) * sizeof(float))
        );
        // The baker's shader module — created only when the pool is enabled (nothing bakes into a filler).
        m_brickBakeShaderModule = ((m_brickPoolEnabled && !kernels.BrickBake.IsEmpty)
            ? gpu.ShaderModuleFactory.Create(
                deviceContext: device,
                stage: GpuShaderStage.Compute,
                bytecode: kernels.BrickBake
            )
            : null
        );
        m_brickUploadShaderModule = ((m_brickPoolEnabled && !kernels.BrickUpload.IsEmpty)
            ? gpu.ShaderModuleFactory.Create(
                deviceContext: device,
                stage: GpuShaderStage.Compute,
                bytecode: kernels.BrickUpload
            )
            : null
        );

        // GPU-driven cull: the cull-args pass reduces the cull buffer to the Stage-1 INDIRECT dispatch args (the
        // surviving-tile bbox, 3 group counts) and the bbox group origin (2 uints). Both are device-local — the GPU
        // writes them as UAVs, then a barrier orders the indirect read; the views dispatch reads the args (the
        // dispatch grid) and the bounds (its pixel offset). The all-empty margins are never dispatched.
        m_viewsArgsBuffer = gpu.StorageBufferFactory.CreateDeviceLocalIndirectArgs(
            deviceContext: device,
            sizeBytes: (sizeof(uint) * 3)
        );
        m_cullBoundsBuffer = gpu.StorageBufferFactory.CreateDeviceLocal(
            deviceContext: device,
            sizeBytes: (sizeof(uint) * 2)
        );

        // Stage 2's full-frame composite grid is constant for the run, so its dispatch is driven INDIRECTLY: the GPU
        // reads the (x, y, z) group counts from this host-written args buffer (vkCmdDispatchIndirect / ExecuteIndirect)
        // instead of the CPU supplying them. The counts equal the equivalent direct dispatch, so it is pixel-neutral
        // (the `world` parity gate is the guard). Host-written once + host-coherent, so the queue-submit host-write
        // visibility covers it with no indirect-read barrier.
        m_compositeArgsBuffer = gpu.StorageBufferFactory.CreateIndirectArgs(
            deviceContext: device,
            sizeBytes: (sizeof(uint) * 3)
        );
        m_compositeArgsBuffer.Write<uint>(data: [
            ((width + (WorkgroupEdge - 1)) / WorkgroupEdge),
            ((height + (WorkgroupEdge - 1)) / WorkgroupEdge),
            1u,
        ]);

        var pushConstantBinding = new GpuPushConstantBinding(
            data: m_pushConstant,
            offset: 0,
            stageFlags: GpuShaderStage.Compute
        );
        var compositePushBinding = new GpuPushConstantBinding(
            data: m_compositePush,
            offset: 0,
            stageFlags: GpuShaderStage.Compute
        );

        // Beam prepass: program (1) + viewports (2) + dynamic entity transforms (9) + cull buffer written (3) + the
        // per-tile instance mask READ (7 — the MASK-FIRST order: the cone march evaluates the tile-masked field the
        // instance-cull pass wrote, so a march sample costs O(instances near the tile), not O(all instances)). No
        // output image. Direct3D 12 assigns registers from THIS order: t0 program, t1 viewports, t2 dynamicTransforms,
        // u0 tiles, t3 instanceMasks — the kernel's SDF_INSTANCE_MASKS_REGISTER override mirrors it.
        GpuComputeBinding[] beamBindings = [
            new GpuComputeBinding(
                Binding: ProgramBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            new GpuComputeBinding(
                Binding: ViewportBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            new GpuComputeBinding(
                Binding: DynamicTransformBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            new GpuComputeBinding(
                Binding: TileBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferReadWrite
            ),
            new GpuComputeBinding(
                Binding: InstanceMaskBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            // The brick pool (sdfBrickPool), APPENDED LAST so its SRV resolves to register t4 (after instanceMasks t3) —
            // the cone march samples baked SampledRegion carves. Always present (a filler when the pool is disabled).
            new GpuComputeBinding(
                Binding: BrickPoolBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
        ];

        // Instance-cull pass (sdf-instance-cull.comp — the frame's FIRST pass, and its OWN kernel so the cell walk's
        // register footprint never taxes the cone march's occupancy): program (1) + viewports (2) + dynamic entity
        // transforms (9, a DYNAMIC instance's bound resolves through it) + the per-tile instance mask written (7).
        // Direct3D 12 assigns registers from THIS order: t0 program, t1 viewports, t2 dynamicTransforms, u0
        // instanceMasks — the kernel's register() annotations mirror it exactly.
        GpuComputeBinding[] instanceCullBindings = [
            new GpuComputeBinding(
                Binding: ProgramBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            new GpuComputeBinding(
                Binding: ViewportBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            new GpuComputeBinding(
                Binding: DynamicTransformBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            new GpuComputeBinding(
                Binding: InstanceMaskBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferReadWrite
            ),
            new GpuComputeBinding(
                Binding: FrameInstanceGridBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
        ];

        // Cull-args reduction: cull buffer read (3) + the views indirect args written (5) + the bbox origin written (6).
        GpuComputeBinding[] cullArgsBindings = [
            new GpuComputeBinding(
                Binding: TileBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            new GpuComputeBinding(
                Binding: CullArgsBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferReadWrite
            ),
            new GpuComputeBinding(
                Binding: CullBoundsBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferReadWrite
            ),
        ];

        // Stage 1 (per-view SDF): program (1) + viewports (2) + dynamic entity transforms (9) + cull buffer read (3) +
        // the source array (4) + the GPU-computed bbox origin (8) + the per-tile instance mask read (7) + the
        // screen-surface table (10) + THIRTY-TWO separate screen-source SampledImage bindings LAST (12..43 — DXC cannot
        // fuse an ARRAY texture into one Vulkan combined-image-sampler, so each screen index gets its own binding;
        // the pipeline factory bakes in ONE static nearest sampler PER SampledImage binding on Direct3D 12, all
        // sharing that one filter). dynamicTransforms is listed BEFORE cullBounds so the SRV registers resolve program
        // t0, viewport t1, dynamicTransforms t2, cullBounds t3, screenSurfaces t4, screenSources t5..t36, then
        // instanceMasks t37, screenLights t38 (matching the HLSL) — Direct3D 12 assigns t#/s# registers from THIS
        // array's order (DirectXGpuComputePipelineFactory), so the HLSL's explicit register(tN) annotations must
        // mirror this exact sequence; a reorder here without the matching HLSL edit desyncs the root signature. The 32
        // screen-source bindings are SPREAD from a MaxScreenSurfaces-derived list — never a hand-listed run.
        GpuComputeBinding[] viewsBindings = [
            new GpuComputeBinding(
                Binding: ProgramBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            new GpuComputeBinding(
                Binding: ViewportBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            new GpuComputeBinding(
                Binding: DynamicTransformBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            new GpuComputeBinding(
                Binding: TileBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferReadWrite
            ),
            new GpuComputeBinding(
                Binding: ViewSourceBindingIndex,
                Count: MaxViewports,
                Kind: GpuComputeBindingKind.StorageImage
            ),
            new GpuComputeBinding(
                Binding: ViewsCullBoundsBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            new GpuComputeBinding(
                Binding: ScreenSurfaceBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            .. BuildScreenSourceBindings(),
            new GpuComputeBinding(
                Binding: InstanceMaskBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            // The per-frame screen-light buffer — its SRV resolves to register t38 (after instanceMasks t37).
            new GpuComputeBinding(
                Binding: ScreenLightBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            // The SDF_SHAPE_GLYPH font atlas: its SRV resolves to register t39 (after screenLights t38) and its
            // static sampler to s32 (after the 32 screen samplers). One more SampledImage on this set, (re)bound per
            // frame by BindScreenSources to the atlas view or the neutral 1×1 filler when none is set.
            new GpuComputeBinding(
                Binding: GlyphAtlasBindingIndex,
                Kind: GpuComputeBindingKind.SampledImage
            ),
            // The GLYPH DECAL buffer, so its SRV resolves to register t40 (after the glyph atlas t39) — the
            // material-level text tier the decal-mode screens sample (see sdf-world.hlsli's sdfDecalCells).
            new GpuComputeBinding(
                Binding: DecalCellsBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            // The brick pool (sdfBrickPool), APPENDED LAST so its SRV resolves to register t41 (after sdfDecalCells t40) —
            // Stage 1 samples baked SampledRegion carves O(1). Always present (a filler when the pool is disabled); the
            // core-ops variant shares this bindings array, so both Stage 1 pipelines bind the pool identically.
            new GpuComputeBinding(
                Binding: BrickPoolBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            // The frame-local instance grid resolves to t42, after the brick pool's t41.
            new GpuComputeBinding(
                Binding: FrameInstanceGridBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            // The shadow sampler table, APPENDED LAST so its SRV resolves to register t43 (after the frame instance
            // grid t42). Immutable and shared across ring slots — bound once per slot at construction, never rewritten.
            new GpuComputeBinding(
                Binding: SamplerTableBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
        ];

        // Stage 2 (source-agnostic composite): output image (0) + the source array (1) + the cull buffer read (3),
        // which the compositor uses to flatten every empty (culled) tile to a constant.
        GpuComputeBinding[] compositeBindings = [
            new GpuComputeBinding(
                Binding: CompositeOutputBindingIndex,
                Kind: GpuComputeBindingKind.StorageImage
            ),
            new GpuComputeBinding(
                Binding: CompositeSourceBindingIndex,
                Count: MaxViewports,
                Kind: GpuComputeBindingKind.StorageImage
            ),
            new GpuComputeBinding(
                Binding: TileBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
        ];

        // The carve-bake baker's set: the per-slot request buffer (a float4 SRV at t0) + the shared pool WRITTEN as a UAV
        // (u0). One set per brick slot, each binding that slot's request buffer + the pool; only used when the pool is
        // enabled. Direct3D 12 assigns registers from THIS order: t0 bakeRequest, u0 brickPool.
        GpuComputeBinding[] brickBakeBindings = [
            new GpuComputeBinding(
                Binding: BrickBakeRequestBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferRead
            ),
            new GpuComputeBinding(
                Binding: BrickBakePoolBindingIndex,
                Kind: GpuComputeBindingKind.StorageBufferReadWrite
            ),
        ];

        m_beamPipeline = gpu.ComputePipelineFactory.Create(
            computeShaderModule: m_beamShaderModule,
            description: new GpuComputePipelineDescription(
                Name: "sdf-beam",
                Bindings: beamBindings,
                PushConstantBinding: pushConstantBinding
            ),
            deviceContext: device
        );
        m_instanceCullPipeline = gpu.ComputePipelineFactory.Create(
            computeShaderModule: m_instanceCullShaderModule,
            description: new GpuComputePipelineDescription(
                Name: "sdf-instance-cull",
                Bindings: instanceCullBindings,
                PushConstantBinding: pushConstantBinding
            ),
            deviceContext: device
        );
        m_cullArgsPipeline = gpu.ComputePipelineFactory.Create(
            computeShaderModule: m_cullArgsShaderModule,
            description: new GpuComputePipelineDescription(
                Name: "sdf-cull-args",
                Bindings: cullArgsBindings,
                PushConstantBinding: pushConstantBinding
            ),
            deviceContext: device
        );
        // Nearest filtering end to end: a bound screen source (an emulator/child's native pixels) magnifies as crisp
        // cells, never bilinear smears — the whole point of sampling instead of the flat material.
        m_viewsPipeline = gpu.ComputePipelineFactory.Create(
            computeShaderModule: m_viewsShaderModule,
            description: new GpuComputePipelineDescription(
                Bindings: viewsBindings,
                Name: "sdf-world-views",
                PushConstantBinding: pushConstantBinding,
                SamplerFilter: GpuSamplerFilter.Nearest
            ),
            deviceContext: device
        );
        // The core-ops Stage 1 variant shares the SAME viewsBindings array (and push/sampler shape), so its layout is
        // identically defined and the per-slot views sets bind against either pipeline — UploadProgram just flips
        // which handle the views dispatch records (m_useCoreViews). One extra pipeline object; zero extra sets.
        m_viewsCorePipeline = gpu.ComputePipelineFactory.Create(
            computeShaderModule: m_viewsCoreShaderModule,
            description: new GpuComputePipelineDescription(
                Bindings: viewsBindings,
                Name: "sdf-world-views-core",
                PushConstantBinding: pushConstantBinding,
                SamplerFilter: GpuSamplerFilter.Nearest
            ),
            deviceContext: device
        );
        // The sky pre-pass shares the SAME viewsBindings array too (see m_skyPipeline's field comment): it reads only
        // viewports + sdfScreenLights out of that layout and writes the source array, so it needs no bindings, sets,
        // or pool capacity of its own.
        m_skyPipeline = gpu.ComputePipelineFactory.Create(
            computeShaderModule: m_skyShaderModule,
            description: new GpuComputePipelineDescription(
                Bindings: viewsBindings,
                Name: "sdf-sky",
                PushConstantBinding: pushConstantBinding,
                SamplerFilter: GpuSamplerFilter.Nearest
            ),
            deviceContext: device
        );
        m_compositePipeline = gpu.ComputePipelineFactory.Create(
            computeShaderModule: m_compositeShaderModule,
            description: new GpuComputePipelineDescription(
                Name: "sdf-world-composite",
                Bindings: compositeBindings,
                PushConstantBinding: compositePushBinding
            ),
            deviceContext: device
        );

        // The carve-bake baker pipeline (only when the pool is enabled). Its own push block carries the per-dispatch
        // voxel-slice window (start + count).
        var brickBakePushBinding = new GpuPushConstantBinding(
            data: m_brickBakePush,
            offset: 0,
            stageFlags: GpuShaderStage.Compute
        );

        m_brickBakePipeline = ((m_brickBakeShaderModule is not null)
            ? gpu.ComputePipelineFactory.Create(
                computeShaderModule: m_brickBakeShaderModule,
                description: new GpuComputePipelineDescription(
                    Name: "sdf-brick-bake",
                    Bindings: brickBakeBindings,
                    PushConstantBinding: brickBakePushBinding
                ),
                deviceContext: device
            )
            : null
        );
        m_brickUploadPipeline = ((m_brickUploadShaderModule is not null)
            ? gpu.ComputePipelineFactory.Create(
                computeShaderModule: m_brickUploadShaderModule,
                description: new GpuComputePipelineDescription(
                    Name: "sdf-brick-upload",
                    Bindings: brickBakeBindings,
                    PushConstantBinding: new GpuPushConstantBinding(
                        data: m_brickUploadPush,
                        offset: 0,
                        stageFlags: GpuShaderStage.Compute
                    )
                ),
                deviceContext: device
            )
            : null
        );

        // One pool, one CULL-ARGS set (its bindings are all shared device-local buffers, never rewritten after
        // construction) plus FrameRingSize copies of the other four sets (they bind the per-slot host-visible buffers,
        // and the views/composite copies take per-frame descriptor rewrites) — the Direct3D 12 allocator bump-allocates
        // a non-overlapping heap region per set (like a Vulkan pool), so they never clobber. The capacity is DERIVED
        // from the binding lists (an array binding contributes its full Count), so it can never drift out of sync when
        // a binding is added or MaxViewports/FrameRingSize changes.
        var poolSetBindings = new List<IReadOnlyList<GpuComputeBinding>> { cullArgsBindings };

        for (var slot = 0; (slot < FrameRingSize); slot++) {
            poolSetBindings.Add(item: beamBindings);
            poolSetBindings.Add(item: instanceCullBindings);
            poolSetBindings.Add(item: viewsBindings);
            poolSetBindings.Add(item: compositeBindings);
        }

        // One bake set per brick slot (all static — bound once below), when the pool is enabled.
        if (m_brickPoolEnabled) {
            for (var brick = 0; (brick < SdfBrickPoolLayout.MaxBricks); brick++) {
                poolSetBindings.Add(item: brickBakeBindings);
            }

            if (m_brickUploadPipeline is not null) {
                for (var slot = 0; (slot < FrameRingSize); slot++) {
                    poolSetBindings.Add(item: brickBakeBindings);
                }
            }
        }

        var poolSizes = GpuDescriptorPoolSizes.ForSets([.. poolSetBindings]);

        m_pool = m_descriptorAllocator.CreatePool(
            deviceHandle: m_deviceHandle,
            sizes: poolSizes
        );

        // The cull buffer is read-only here (a stride-4 SRV on Direct3D 12); the args + bounds are written (UAVs).
        m_cullArgsSet = m_descriptorAllocator.AllocateSet(
            descriptorSetLayoutHandle: m_cullArgsPipeline.DescriptorSetLayoutHandle,
            deviceHandle: m_deviceHandle,
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
        m_screenSampler = m_descriptorAllocator.CreateSampler(
            deviceHandle: m_deviceHandle,
            filter: GpuSamplerFilter.Nearest
        );

        for (var slot = 0; (slot < FrameRingSize); slot++) {
            var beamSet = m_descriptorAllocator.AllocateSet(
                descriptorSetLayoutHandle: m_beamPipeline.DescriptorSetLayoutHandle,
                deviceHandle: m_deviceHandle,
                poolHandle: m_pool
            );

            m_beamSets[slot] = beamSet;
            WriteStorageBuffer(
                binding: ProgramBindingIndex,
                buffer: m_programBuffer,
                set: beamSet
            );
            WriteStorageBuffer(
                set: beamSet,
                binding: ViewportBindingIndex,
                buffer: m_viewportBuffers[slot]
            );
            WriteStorageBuffer(
                set: beamSet,
                binding: DynamicTransformBindingIndex,
                buffer: m_dynamicTransformBuffers[slot]
            );
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
            var instanceCullSet = m_descriptorAllocator.AllocateSet(
                descriptorSetLayoutHandle: m_instanceCullPipeline.DescriptorSetLayoutHandle,
                deviceHandle: m_deviceHandle,
                poolHandle: m_pool
            );

            m_instanceCullSets[slot] = instanceCullSet;
            WriteStorageBuffer(
                binding: ProgramBindingIndex,
                buffer: m_programBuffer,
                set: instanceCullSet
            );
            WriteStorageBuffer(
                set: instanceCullSet,
                binding: ViewportBindingIndex,
                buffer: m_viewportBuffers[slot]
            );
            WriteStorageBuffer(
                set: instanceCullSet,
                binding: DynamicTransformBindingIndex,
                buffer: m_dynamicTransformBuffers[slot]
            );
            WriteStorageBufferReadWrite(
                binding: InstanceMaskBindingIndex,
                buffer: m_instanceMaskBuffer,
                set: instanceCullSet
            );
            WriteStorageBufferReadOnly(
                set: instanceCullSet,
                binding: FrameInstanceGridBindingIndex,
                buffer: m_instanceGridBuffers[slot]
            );

            var viewsSet = m_descriptorAllocator.AllocateSet(
                descriptorSetLayoutHandle: m_viewsPipeline.DescriptorSetLayoutHandle,
                deviceHandle: m_deviceHandle,
                poolHandle: m_pool
            );

            m_viewsSets[slot] = viewsSet;
            WriteStorageBuffer(
                binding: ProgramBindingIndex,
                buffer: m_programBuffer,
                set: viewsSet
            );
            WriteStorageBuffer(
                set: viewsSet,
                binding: ViewportBindingIndex,
                buffer: m_viewportBuffers[slot]
            );
            WriteStorageBuffer(
                set: viewsSet,
                binding: DynamicTransformBindingIndex,
                buffer: m_dynamicTransformBuffers[slot]
            );
            WriteStorageBufferReadWrite(
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
            // The screen-surface table (48-byte ScreenSurfaceData, same stride-16-multiple SRV pattern as ViewportData).
            WriteStorageBuffer(
                set: viewsSet,
                binding: ScreenSurfaceBindingIndex,
                buffer: m_screenSurfaceBuffers[slot]
            );
            // The per-frame screen-light buffer (float4 stride — the plain 16-byte WriteStorageBuffer is correct).
            WriteStorageBuffer(
                set: viewsSet,
                binding: ScreenLightBindingIndex,
                buffer: m_screenLightBuffers[slot]
            );
            // The per-frame glyph-decal buffer (uint4 stride, same 16-byte pattern).
            WriteStorageBuffer(
                set: viewsSet,
                binding: DecalCellsBindingIndex,
                buffer: m_decalBuffers[slot]
            );
            // The brick pool (a stride-4 float SRV — the shared read side; the bake set below binds the same buffer as a UAV).
            WriteStorageBufferReadOnly(
                binding: BrickPoolBindingIndex,
                buffer: m_brickPoolBuffer,
                set: viewsSet
            );
            WriteStorageBufferReadOnly(
                set: viewsSet,
                binding: FrameInstanceGridBindingIndex,
                buffer: m_instanceGridBuffers[slot]
            );
            WriteStorageBufferReadOnly(
                binding: SamplerTableBindingIndex,
                buffer: m_samplerTableBuffer,
                set: viewsSet
            );

            var compositeSet = m_descriptorAllocator.AllocateSet(
                descriptorSetLayoutHandle: m_compositePipeline.DescriptorSetLayoutHandle,
                deviceHandle: m_deviceHandle,
                poolHandle: m_pool
            );

            m_compositeSets[slot] = compositeSet;
            m_descriptorAllocator.WriteStorageImage(
                arrayElement: 0,
                binding: CompositeOutputBindingIndex,
                descriptorSetHandle: compositeSet,
                deviceHandle: m_deviceHandle,
                imageViewHandle: m_storageImage.ImageViewHandle
            );
            WriteStorageBufferReadOnly(
                binding: TileBindingIndex,
                buffer: m_tileBuffer,
                set: compositeSet
            );

            // The source array (binding the SDF view textures and any hosted child surfaces) is (re)bound per frame by
            // BindSources — child image-views aren't known until their nodes have produced.
            m_commandPools[slot] = gpu.CommandPoolFactory.Create(deviceContext: device);
            m_frameFences[slot] = gpu.QueueSubmitter.CreateSubmissionFence(deviceContext: device);
        }

        // The carve-bake baker's per-slot request buffers + static descriptor sets (only when the pool is enabled). Each
        // slot owns a host-visible request buffer (header + up to MaxBrickCarvesPerBake carves) and a set binding that
        // buffer (t0 SRV) + the shared pool (u0 UAV). These are NOT per-ring-slot: a bake spans frames and RequestBrickBake
        // drains the ring (WaitForFrameRing) before rewriting a request buffer, so one buffer per brick slot is race-free.
        if (m_brickPoolEnabled) {
            for (var brick = 0; (brick < SdfBrickPoolLayout.MaxBricks); brick++) {
                var requestBuffer = gpu.StorageBufferFactory.Create(
                    deviceContext: device,
                    sizeBytes: (((ulong)m_brickRequestScratch.Length) * (sizeof(float) * 4))
                );

                m_brickRequestBuffers[brick] = requestBuffer;

                var bakeSet = m_descriptorAllocator.AllocateSet(
                    descriptorSetLayoutHandle: m_brickBakePipeline!.DescriptorSetLayoutHandle,
                    deviceHandle: m_deviceHandle,
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

            if (m_brickUploadPipeline is not null) {
                for (var slot = 0; (slot < FrameRingSize); slot++) {
                    var staging = gpu.StorageBufferFactory.Create(
                        deviceContext: device,
                        sizeBytes: (((ulong)SdfBrickPoolLayout.VoxelsPerBrick) * sizeof(float))
                    );

                    m_brickUploadStaging[slot] = staging;

                    var uploadSet = m_descriptorAllocator.AllocateSet(
                        descriptorSetLayoutHandle: m_brickUploadPipeline.DescriptorSetLayoutHandle,
                        deviceHandle: m_deviceHandle,
                        poolHandle: m_pool
                    );

                    m_brickUploadSets[slot] = uploadSet;
                    WriteStorageBuffer(
                        binding: BrickBakeRequestBindingIndex,
                        buffer: staging,
                        set: uploadSet
                    );
                    WriteStorageBufferReadWrite(
                        binding: BrickBakePoolBindingIndex,
                        buffer: m_brickPoolBuffer,
                        set: uploadSet
                    );
                }
            }
        }

        SdfShaderSetVerification.VerifyShaderSet(
            device: device,
            kernels: kernels,
            verify: VerifyIsaVersion
        );

        // The "uploaded once" seam: the program (and its screen-surface table) is uploaded here and normally never
        // again — frames move entities by rewriting only the small dynamic-transform buffer. UploadProgram is the
        // single owner of per-program derived state (its capacity checks trivially pass for the construction program).
        UploadProgram(program: options.Program);

        // Opt-in GPU timing: when a timing factory + recorder are supplied AND the device supports timestamps, each
        // timed frame writes the per-pass marks (frame-start, then one close per PassLabels entry). TimingPoolCount
        // (FrameRingSize + 1) pools are used so a fire-and-forget host can read frame N−FrameRingSize's results — the
        // newest frame the ring's slot fence PROVES retired — with no device-idle stall; the waited path reads the
        // just-submitted pool directly. In EAGER mode (LiveArmedTiming false — the waited harness/measure path) the
        // pools are created here and every frame is timed. In LIVE-ARMED mode (the live node) they are created lazily
        // on the first armed frame (EnsureTimingPools), so a session that never arms timing allocates none.
        if (
            (options.TimingFactory is not null) &&
            (options.TimingRecorder is not null)
        ) {
            m_timingCapabilities = options.TimingFactory.GetCapabilities(deviceContext: device);

            if (m_timingCapabilities.IsSupported) {
                m_timingAvailable = true;
                m_timingFactory = options.TimingFactory;
                m_timingRecorder = options.TimingRecorder;
                m_liveArmedTiming = options.LiveArmedTiming;

                if (!m_liveArmedTiming) {
                    EnsureTimingPools();
                }
            }
        }
    }

    // Per-ring-slot dirty flags starting TRUE (each slot's buffer needs its first upload — see m_decalDirty).
    private static bool[] BuildRingDirtyFlags() {
        var flags = new bool[FrameRingSize];

        Array.Fill(
            array: flags,
            value: true
        );

        return flags;
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
    // The MaxScreenSurfaces screen-source SampledImage bindings, spread into viewsBindings in screen-index order so the
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
    private void WriteStorageBuffer(nint set, uint binding, IGpuBuffer buffer) {
        m_descriptorAllocator.WriteStorageBuffer(
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
    private void WriteStorageBufferReadOnly(nint set, uint binding, IGpuBuffer buffer) {
        m_descriptorAllocator.WriteStorageBufferReadOnly(
            binding: binding,
            bufferHandle: buffer.BufferHandle,
            bufferSize: buffer.SizeBytes,
            descriptorSetHandle: set,
            deviceHandle: m_deviceHandle
        );
    }
    private void WriteStorageBufferReadWrite(nint set, uint binding, IGpuBuffer buffer) {
        m_descriptorAllocator.WriteStorageBufferReadWrite(
            binding: binding,
            bufferHandle: buffer.BufferHandle,
            bufferSize: buffer.SizeBytes,
            descriptorSetHandle: set,
            deviceHandle: m_deviceHandle
        );
    }

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

        if (m_timingPools is not null) {
            foreach (var pool in m_timingPools) {
                pool.Dispose();
            }
        }

        m_readback?.Dispose();

        for (var slot = 0; (slot < FrameRingSize); slot++) {
            m_frameFences[slot].Dispose();
            m_commandPools[slot].Dispose();
            m_dynamicTransformBuffers[slot].Dispose();
            m_instanceGridBuffers[slot].Dispose();
            m_viewportBuffers[slot].Dispose();
            m_screenSurfaceBuffers[slot].Dispose();
            m_screenLightBuffers[slot].Dispose();
            m_decalBuffers[slot].Dispose();
        }

        m_compositeArgsBuffer.Dispose();
        m_cullBoundsBuffer.Dispose();
        m_viewsArgsBuffer.Dispose();
        m_tileBuffer.Dispose();
        m_instanceMaskBuffer.Dispose();
        m_programBuffer.Dispose();
        m_samplerTableBuffer.Dispose();

        foreach (var requestBuffer in m_brickRequestBuffers) {
            requestBuffer?.Dispose();
        }

        foreach (var staging in m_brickUploadStaging) {
            staging?.Dispose();
        }

        m_brickPoolBuffer.Dispose();
        m_brickBakePipeline?.Dispose();
        m_brickBakeShaderModule?.Dispose();
        m_brickUploadPipeline?.Dispose();
        m_brickUploadShaderModule?.Dispose();
        m_beamPipeline.Dispose();
        m_instanceCullPipeline.Dispose();
        m_cullArgsPipeline.Dispose();
        m_viewsPipeline.Dispose();
        m_viewsCorePipeline.Dispose();
        m_skyPipeline.Dispose();
        m_compositePipeline.Dispose();
        m_descriptorAllocator.DestroySampler(
            deviceHandle: m_deviceHandle,
            samplerHandle: m_screenSampler
        );
        m_descriptorAllocator.DestroyPool(
            deviceHandle: m_deviceHandle,
            poolHandle: m_pool
        );

        foreach (var source in m_sourceTextures) {
            source?.Dispose();
        }

        m_screenSourceFiller.Dispose();
        m_glyphAtlasUpload?.Dispose();
        m_storageImage.Dispose();
        m_beamShaderModule.Dispose();
        m_instanceCullShaderModule.Dispose();
        m_cullArgsShaderModule.Dispose();
        m_viewsShaderModule.Dispose();
        m_viewsCoreShaderModule.Dispose();
        m_skyShaderModule.Dispose();
        m_compositeShaderModule.Dispose();
    }
}
