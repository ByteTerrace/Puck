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
/// <param name="LiveArmedTiming">When <see langword="true"/> (the live node path), the timing pools are created LAZILY
/// on the first armed frame and each frame consults <see cref="GpuTimingControl.Shared"/> — a disarmed frame skips the
/// timestamp writes/reads at near-zero cost, so timing arms and disarms mid-session with no rebuild. When
/// <see langword="false"/> (the default, the waited harness/measure path), timing runs EAGERLY the moment a supported
/// factory + recorder are supplied — the pools are created at construction and every frame is timed, never consulting
/// the shared arming control.</param>
/// <param name="ProgramWordCapacity">An optional FLOOR on the program buffer's packed-word capacity (the engine
/// always provisions at least <paramref name="Program"/>'s length). A host that hot-swaps programs via
/// <see cref="SdfWorldEngine.UploadProgram"/> declares its envelope here instead of relying on every future program
/// staying within the first one's size.</param>
/// <param name="InstanceCapacity">An optional FLOOR on the instance count the per-tile mask buffer is sized for (the
/// engine always provisions at least <paramref name="Program"/>'s <see cref="SdfProgram.InstanceMaskWordCount"/>).
/// The hot-swap counterpart of <paramref name="ProgramWordCapacity"/> for instanced programs.</param>
/// <param name="BrickPoolVoxelCapacity">The carve-bake brick pool's voxel (f32 word) capacity, FROZEN at construction
/// (carve-bake plan §3). Defaults to <see cref="SdfWorldEngine.DefaultBrickPoolVoxelCapacity"/> (16.7M voxels = 64 MB —
/// <see cref="SdfBrickPoolLayout.MaxBricks"/> slots at full <see cref="SdfBrickPoolLayout.BrickDim"/><sup>3</sup>
/// resolution). <c>0</c> provisions NO pool (a 4-byte filler keeps the always-present shader binding valid). A pool-less
/// engine still ACCEPTS a program declaring a <see cref="SdfShapeType.SampledRegion"/> — baking and rendering are split:
/// the shader detects the filler (by its element count) and renders the region via the conservative uncarved-hull
/// fallback (the Subtraction never bites), so a filming view (<c>SdfCameraView</c>/<c>NestedWorldView</c>) shows a
/// SampledRegion world uncarved rather than a box-shaped hole. Only <see cref="SdfWorldEngine.RequestBrickBake"/> stays a
/// loud rejection on a pool-less engine (nothing to bake into). The pool is a persistent device-local buffer the sliced
/// background bake (<see cref="SdfWorldEngine.RequestBrickBake"/>) writes and the beam + views kernels sample.</param>
public sealed record SdfWorldEngineOptions(
    SdfProgram Program,
    uint ViewportCapacity = SdfWorldEngine.MaxViewports,
    uint ChildMask = 0,
    int DynamicTransformCapacity = 1,
    Func<IGpuDeviceContext, IGpuStorageImage>? CreateOutputImage = null,
    IGpuTimingPoolFactory? TimingFactory = null,
    IGpuTimingRecorder? TimingRecorder = null,
    int ProgramWordCapacity = 0,
    int InstanceCapacity = 0,
    bool LiveArmedTiming = false,
    int BrickPoolVoxelCapacity = SdfWorldEngine.DefaultBrickPoolVoxelCapacity
);

/// <summary>Names one of the cadence gate's hashed change-signature spans (see <see cref="SdfWorldEngine.DecideCadenceSkip"/>) —
/// a flag set in <see cref="SdfCadenceDiagnostics.ChangedSpans"/> means that span's bytes differed from the previous
/// decided frame's, i.e. it is a candidate driver of a never-skipping gate.</summary>
[Flags]
public enum SdfCadenceSpan {
    /// <summary>No span changed.</summary>
    None = 0,
    /// <summary>The program/decal revisions + live viewport count.</summary>
    Revisions = (1 << 0),
    /// <summary>The Stage 0/1 push constant (extent, tile grid, viewport/child/screen masks, instance-mask width).</summary>
    Push = (1 << 1),
    /// <summary>The per-view camera/region/render-scale table, excluding each row's presentation-time lane.</summary>
    Viewports = (1 << 2),
    /// <summary>The per-entity dynamic-transform table.</summary>
    Dynamics = (1 << 3),
    /// <summary>The screen-surface sampling-frame table.</summary>
    ScreenSurfaces = (1 << 4),
    /// <summary>The screen-light/environment/grid/bench-lever table.</summary>
    ScreenLights = (1 << 5),
}

/// <summary>The cadence gate's diagnostics for the most recently decided frame (STEP 1 instrumentation, perf plan Phase
/// 6.1 follow-up) — read-only, never fed back into the skip decision. Exposed via <see cref="SdfWorldEngine.CadenceDiagnostics"/>
/// and surfaced by the <c>sdf.info</c> verb's cadence section, so a live session names exactly which span keeps a
/// static scene from skipping instead of guessing.</summary>
/// <param name="GateEnabled">Whether the gate was armed (<see cref="SdfFrame.EnableCadenceGate"/>) for this frame.</param>
/// <param name="Skipped">Whether this frame skipped the mask/beam/cull-args/views passes.</param>
/// <param name="SkippedFrameCount">The cumulative skipped-frame count since the gate last armed (reset whenever the gate turns off).</param>
/// <param name="RenderedFrameCount">The cumulative fully-rendered-frame count since the gate last armed (reset alongside <paramref name="SkippedFrameCount"/>).</param>
/// <param name="RevisionsHash">This frame's independent FNV-1a hash of the revisions span (see <see cref="SdfCadenceSpan.Revisions"/>).</param>
/// <param name="PushHash">This frame's independent hash of the push-constant span.</param>
/// <param name="ViewportsHash">This frame's independent hash of the viewport span (time lane excluded).</param>
/// <param name="DynamicsHash">This frame's independent hash of the dynamic-transform span.</param>
/// <param name="ScreenSurfacesHash">This frame's independent hash of the screen-surface span.</param>
/// <param name="ScreenLightsHash">This frame's independent hash of the screen-light span.</param>
/// <param name="ChangedSpans">Which spans' hashes differ from the previous decided frame's — the payload a human reads
/// to find the never-skipping driver.</param>
/// <param name="ScreenSourceBound">Whether ANY screen source slot is bound this frame (<c>m_screenSourceMask != 0</c>)
/// — informational only (it does NOT gate the skip decision, and never has by rights: a live console booted anywhere in
/// the engine binds this per-ENGINE mask independent of which program is uploaded, and sampleScreenSurface is
/// unreachable without a ScreenSlab material — see <see cref="SdfWorldEngine.DecideCadenceSkip"/>'s coverage rationale
/// for the false-block this used to cause before the fix).</param>
/// <param name="ProgramDeclaresScreenSlab">Whether the LIVE uploaded program declares any ScreenSlab shape — the first
/// of the two conditions NOT covered by any hashed span (see <see cref="SdfWorldEngine.DecideCadenceSkip"/>).</param>
/// <param name="BrickBaking">Whether a carve-bake is in progress (the second uncovered condition).</param>
public readonly record struct SdfCadenceDiagnostics(
    bool GateEnabled,
    bool Skipped,
    ulong SkippedFrameCount,
    ulong RenderedFrameCount,
    ulong RevisionsHash,
    ulong PushHash,
    ulong ViewportsHash,
    ulong DynamicsHash,
    ulong ScreenSurfacesHash,
    ulong ScreenLightsHash,
    SdfCadenceSpan ChangedSpans,
    bool ScreenSourceBound,
    bool ProgramDeclaresScreenSlab,
    bool BrickBaking
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
/// all three re-record the shared per-slot command buffers: <see cref="RenderFrame"/> is the deterministic harness path —
/// one submit-and-wait plus a readback (validation, headless render). <see cref="SubmitFrame"/> is the live node path —
/// fire-and-forget behind the engine's own <see cref="FrameRingSize"/>-deep frame ring (each slot's fence orders that
/// slot's rewrites against its previous submission, so a pipelining host needs NO per-frame device drain), plus the
/// export-mode queue drain when the output crosses a backend seam.
/// <see cref="SubmitFramePipelined"/> is the DEMO-PREVIEW path — a non-blocking FENCED readback (submit fire-and-forget,
/// poll <see cref="IsFramePixelsReady"/> on a LATER produced frame, then <see cref="AcquireFramePixels"/> maps it), so
/// the live in-editor bake preview never idles the shared present queue mid-sculpt. It stays frame-count driven
/// (determinism is a feature even here), and a single-in-flight guard forbids interleaving it with the other two on one
/// engine — <see cref="RenderFrame"/>, <see cref="SubmitFrame"/>, and <see cref="SubmitFramePipelined"/> each throw while
/// a pipelined frame is outstanding. Adding a wait to <see cref="SubmitFrame"/> is a frame-rate regression; removing the
/// wait from <see cref="RenderFrame"/> is a nondeterminism bug.
/// </para>
/// </summary>
public sealed class SdfWorldEngine : IDisposable, ISdfBrickBakeService {
    private const uint CompositeOutputBindingIndex = 0; // sdf-world-composite.comp: Output at binding 0
    private const int CompositePushByteLength = ((16 + ((sizeof(float) * 4) * MaxViewports)) + (sizeof(uint) * 4)); // CompositeParams2: uint2 extent + uint count + uint tileGridPacked + float4 rects[5] + uint2 scaleQPacked + uint2 sharpnessQPacked
    private const uint CompositeSourceBindingIndex = 1; // sdf-world-composite.comp: sources[] at binding 1
    private const uint CullArgsBindingIndex = 5; // sdf-cull-args.comp: views indirect dispatch args (register u0)
    private const uint CullBoundsBindingIndex = 6; // sdf-cull-args.comp: bbox group origin (register u1); read by sdf-world-views.comp at binding 8
    private const uint DynamicTransformBindingIndex = 9; // sdf-vm.hlsli's [[vk::binding(9, 0)]] / register(t2) (world path)
    private const int DynamicTransformByteLength = ((sizeof(float) * 4) * 2); // 32-byte rigid transform: float4 position (xyz + .w = soft-shadow participation: 0 casts / 1 shadow-suppressed) + float4 orientation quaternion (KEEP IN SYNC with sdf-vm.hlsli sdfDynamicTransforms: position.w is read by sdfShadowParticipationActive's per-instance skip in sdf-world.hlsli)
    private const GpuPixelFormat Format = GpuPixelFormat.R8G8B8A8Unorm;
    private const uint InstanceMaskBindingIndex = 7; // sdf-beam.comp (u1) / sdf-world-views.comp (t13): per-tile instance mask, written by the beam prepass, read by Stage 1; the per-tile word count is the LIVE uploaded program's InstanceMaskWordCount (pushed per frame, capped at the construction width the buffer was sized for)
    /// <summary>The kernels' source array length (<c>sources[5]</c>) — the most viewports one engine composites.</summary>
    public const int MaxViewports = 5;

    private const uint ProgramBindingIndex = 1; // matches sdf-vm.hlsli's [[vk::binding(1, 0)]] / register(t0)
    private const int PushConstantByteLength = ((sizeof(uint) * 4) * 2); // 32-byte CompositeParams (16-byte rounded); word 6 = screenMask, word 7 = instanceMaskWordCount
    /// <summary>The kernels' screen-source count — the most screen surfaces one program may declare (matches
    /// <see cref="SdfProgramBuilder.MaxScreenSurfaces"/>). THIRTY-TWO SEPARATE combined-image-sampler bindings (not one
    /// array binding): DXC's <c>vk::combinedImageSampler</c> only fuses a SCALAR Texture2D+SamplerState pair, so a
    /// true single Vulkan combined-image-sampler array isn't expressible in the shared HLSL — see
    /// <see cref="ScreenSourceBindingIndices"/>. Capped at 32 because <c>screenMask</c> (the per-frame bound-slot
    /// bitmask, CompositeParams word 6) is a single <c>uint</c> — raising past 32 needs a second mask word on both
    /// sides.</summary>
    public const int MaxScreenSurfaces = 32;
    // The FIRST screen-source binding index; screenSource{i} binds at ScreenSourceBindingBase + i (sdf-world.hlsli's
    // vk::binding). The glyph atlas follows the whole run, so ScreenSourceBindingBase + MaxScreenSurfaces is its binding.
    private const uint ScreenSourceBindingBase = 12;
    // sdf-world-views.comp (Stage 1 ONLY): screenSource0..MaxScreenSurfaces-1, registers t5.. — one binding per screen
    // index (KEEP IN SYNC with sdf-world.hlsli's screenSource declarations). DERIVED from the base + count so the list
    // can never drift from MaxScreenSurfaces (the D3D12 heap-packing discipline: never hand-count a binding run).
    private static readonly uint[] ScreenSourceBindingIndices = BuildScreenSourceBindingIndices();

    private const int ScreenSurfaceByteLength = ((sizeof(float) * 4) * 3); // 48-byte ScreenSurfaceData: right.xyz+halfWidth, up.xyz+halfHeight, origin.xyz+pad (KEEP IN SYNC with sdf-world.hlsli)
    private const uint ScreenSurfaceBindingIndex = 10; // sdf-world-views.comp (Stage 1 ONLY): screenSurfaces, register t4
    private const uint ScreenLightBindingIndex = 11; // sdf-world-views.comp (Stage 1 ONLY): sdfScreenLights, register t38 (per-frame screen glow colors + environment; KEEP IN SYNC with sdf-world.hlsli)
    // sdf-world-views.comp (Stage 1 ONLY): the SDF_SHAPE_GLYPH font atlas, register t39 (SRV, after screenLights t38) +
    // static sampler s32 (after the 32 screen samplers s0..s31) — APPENDED LAST in viewsBindings so the D3D12 registers
    // land there; DERIVED as the first binding past the 32 screen sources (12..43). KEEP IN SYNC with sdf-vm.hlsli's
    // sdfGlyphAtlas.
    private static readonly uint GlyphAtlasBindingIndex = (ScreenSourceBindingBase + (uint)MaxScreenSurfaces);
    // sdf-world-views.comp (Stage 1 ONLY): the GLYPH DECAL buffer, register t40 — appended AFTER the glyph atlas
    // (t39), DERIVED so it can never drift when the screen-source run grows. KEEP IN SYNC with sdf-world.hlsli's
    // sdfDecalCells (Vulkan binding 45).
    private static readonly uint DecalCellsBindingIndex = (GlyphAtlasBindingIndex + 1u);
    /// <summary>The per-screen GLYPH DECAL cell budget: the most glyph cells one screen slot's decal grid may carry
    /// (columns × rows). The decal buffer partitions its cell region into <see cref="MaxScreenSurfaces"/> equal
    /// per-screen runs of this size, so a decal on one screen never collides with another's cells.</summary>
    public const int MaxScreenDecalCells = 1600; // e.g. up to a 48×32 or 53×30 terminal grid

    private const int DecalWordsPerCell = 4; // one uint4 per cell/descriptor (KEEP IN SYNC with sdf-world.hlsli's sdfDecalCells)
    // The decal buffer's leading DESCRIPTOR band (one uint4 per screen slot) precedes the shared cell region; a screen's
    // cell run starts at DecalDescriptorCount + screenIndex * MaxScreenDecalCells (KEEP IN SYNC with sdfSampleGlyphDecal).
    private const int DecalDescriptorCount = MaxScreenSurfaces;
    private const int DecalBufferCells = (DecalDescriptorCount + (MaxScreenSurfaces * MaxScreenDecalCells));
    private const int ScreenLightByteLength = ((sizeof(float) * 4) * (MaxScreenSurfaces + 8)); // float4 rgb+intensity per screen (0..MaxScreenSurfaces-1) + env (MaxScreenSurfaces) + FOUR grid-lock rows (+1..+4: world grid, object origin+pitchX, object frame quat, object pitchZ+patchRadius) + ONE engine-bench params row (+5: soft-shadow/AO/shadow-distance/screen-light levers) + ONE shadow-policy row (+6: carve proxy/camera-tile mask/fast march) + ONE F1 far-field row (+7: far-bound disable / F2 shadow-exit disable) — KEEP IN SYNC with sdf-world.hlsli SdfGridWorld..SdfFarFieldParams
    private const float ScreenLightIntensity = 2.5f; // room-glow gain applied to each screen's average color
    /// <summary>The frame-ring depth: how many produced frames may be in flight on the GPU at once. Every per-frame
    /// MUTABLE resource — the command buffer, the host-visible per-frame buffers (viewport / dynamic-transform /
    /// screen-surface / screen-light / decal), the descriptor sets that bind them, and the per-submit fence — is
    /// duplicated per slot, so re-recording/rewriting slot <c>k</c> only requires frame <c>k − FrameRingSize</c> to
    /// have retired (the slot fence wait in <c>PrepareFrame</c>), never a whole-device drain. The GPU-written
    /// device-local scratch (tile / instance-mask / indirect-args / cull-bounds buffers, the per-view source
    /// textures) stays SHARED: the top-of-frame barrier in <c>Record</c> orders each frame's GPU work after the
    /// previous frame's, which is the natural serialization anyway — the ring overlaps CPU production with GPU
    /// execution, not GPU frames with each other. Slot advance is keyed to the produced-frame count (deterministic;
    /// never wall clock).</summary>
    public const int FrameRingSize = 2;
    // One timing pool more than the ring depth, so the pool read back by TryReadPassTimings (frame N−2's — the
    // newest frame the slot fence PROVES complete) is never the one the current frame is about to reset.
    private const int TimingPoolCount = (FrameRingSize + 1);
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
    // The GPU timing marks: one frame-start mark (query 0, top of pipe), then one BOTTOM-OF-PIPE close per render pass,
    // in submission order. The PASS between mark i and i+1 is named PassLabels[i]; the whole frame is mark 0 .. mark
    // last. Adding a pass is TWO edits — append its label here AND its WriteTimingMark close in Record — after which the
    // sdf.info verb, the [world-timing] line, and the bench's per-pass feed all surface it with no further change (each
    // reads PassTimingLabels / TryReadPassTimings, never a hardcoded tuple). TimingCapacity (8) is the pool ceiling, so
    // at most 7 passes fit before the pools must be resized.
    private static readonly string[] PassLabels = ["mask", "beam", "cull-args", "views", "composite"];
    private static readonly uint TimingMarkCount = (uint)(PassLabels.Length + 1);

    private const int ViewportByteLength = ((sizeof(float) * 4) * 6); // 96-byte ViewportData incl. the renderScale row (KEEP IN SYNC with sdf-world.hlsli)
    private const uint ViewportBindingIndex = 2; // matches sdf-world.hlsli's [[vk::binding(2, 0)]]
    private const uint ViewsCullBoundsBindingIndex = 8; // sdf-world-views.comp: the bbox origin (register t3); the source array is ONE binding number (4) whose 5 elements pack into derived heap slots, so 8 never collides
    private const uint ViewSourceBindingIndex = 4; // sdf-world-views.comp: sources[] LAST (after the fixed 1/2/3)
    private const uint WorkgroupEdge = 8;
    /// <summary>The default carve-bake brick pool capacity in voxels (f32 words) — <see cref="SdfBrickPoolLayout.TotalVoxels"/>
    /// = 16.7M voxels = 64 MB, i.e. <see cref="SdfBrickPoolLayout.MaxBricks"/> slots at full resolution (carve-bake plan §3).</summary>
    public const int DefaultBrickPoolVoxelCapacity = SdfBrickPoolLayout.TotalVoxels;
    // The sdfBrickPool binding number (sdf-vm.hlsli's [[vk::binding(46, 0)]]); the per-consumer Direct3D 12 register is
    // POSITIONAL (views append it LAST -> t41, the beam after its instance mask -> t4). KEEP IN SYNC with sdf-vm.hlsli.
    private const uint BrickPoolBindingIndex = 46;
    // Ring-local frame instance grid (binding 47): rebuilt after dynamic transforms only when moving, maskable
    // instances exist; invariant programs seed every slot once at UploadProgram. Instance-cull reads it at t3; views
    // at t42.
    private const uint FrameInstanceGridBindingIndex = 47;
    private const uint BrickBakeRequestBindingIndex = 0; // sdf-brick-bake.comp: bakeRequest (register t0)
    private const uint BrickBakePoolBindingIndex = 1;    // sdf-brick-bake.comp: brickPool RW (register u0)
    private const int BrickBakePushByteLength = (sizeof(uint) * 4); // BrickBakePush { uint sliceVoxelStart, sliceVoxelCount, 2x pad }
    private const int BrickBakeRequestHeaderFloat4Count = 3; // (boxMin+cellSize), (dims+carveCount), (destWordOffset+invLambda) — KEEP IN SYNC with sdf-brick-bake.comp
    private const int MaxBrickCarvesPerBake = 4096; // request-buffer carve capacity per slot (the debug pool's MaxCarves ceiling)
    private const int MaxBrickBakeVoxelsPerSlice = (256 * 1024); // <= 256K voxels per brick per produced frame (carve-bake plan §3): ~1-2 ms background-budget
    private const uint BrickBakeWorkgroupSize = 64; // sdf-brick-bake.comp's [numthreads(64, 1, 1)]

    private readonly IGpuComputePipeline m_beamPipeline;
    private readonly nint[] m_beamSets = new nint[FrameRingSize];
    private readonly IGpuShaderModule m_beamShaderModule;
    // The change-detected descriptor caches are PER RING SLOT: each slot's sets are only rewritten once that slot's
    // fence proves its previous frame retired, so a descriptor update can never race an in-flight command buffer.
    private readonly nint[][] m_boundScreenSourceViews = BuildRingViewCache(width: MaxScreenSurfaces);
    private readonly nint[][] m_boundSourceViews = BuildRingViewCache(width: MaxViewports);
    // The SDF_SHAPE_GLYPH font atlas: a STATIC texture uploaded once via SetGlyphAtlas (a re-set re-uploads). Held as an
    // IGpuSurfaceUpload (owns the image + staging + the returned view), the current sampleable view, and the last-bound
    // view for the same change-detected (re)bind BindScreenSources does for the screen sources. Null/0 until set — the
    // glyph binding then samples the neutral 1×1 filler (m_screenSourceFiller) and every SDF_SHAPE_GLYPH reads the
    // saturated band, so a glyph-free program with no atlas is safe.
    private IGpuSurfaceUpload? m_glyphAtlasUpload;
    private nint m_glyphAtlasView;
    private readonly nint[] m_boundGlyphAtlasViews = new nint[FrameRingSize];
    private readonly uint m_childMask;
    private readonly nint[] m_childSourceViews = new nint[MaxViewports];
    private readonly IGpuComputeCommandPool[] m_commandPools = new IGpuComputeCommandPool[FrameRingSize];
    private readonly IGpuStorageBuffer m_compositeArgsBuffer;
    private readonly IGpuComputePipeline m_compositePipeline;
    private readonly byte[] m_compositePush = new byte[CompositePushByteLength];
    private readonly nint[] m_compositeSets = new nint[FrameRingSize];
    private readonly IGpuShaderModule m_compositeShaderModule;
    private readonly IGpuComputePipeline m_cullArgsPipeline;
    private readonly nint m_cullArgsSet;
    private readonly IGpuShaderModule m_cullArgsShaderModule;
    private readonly IGpuStorageBuffer m_cullBoundsBuffer;
    private readonly IGpuDescriptorAllocator m_descriptorAllocator;
    private readonly IGpuDeviceContext m_deviceContext;
    private readonly nint m_deviceHandle;
    private readonly IGpuStorageBuffer[] m_dynamicTransformBuffers = new IGpuStorageBuffer[FrameRingSize];
    private readonly int m_dynamicTransformCapacity;
    private readonly byte[] m_dynamicTransformScratch;
    private readonly IGpuExportableStorageImage? m_exportableImage;
    private readonly bool m_exportMode;
    // One per-submit fence per ring slot: PrepareFrame waits slot k's fence (frame k − FrameRingSize) before
    // rewriting slot k's resources; the fenced submit re-arms it.
    private readonly IGpuSubmissionFence[] m_frameFences = new IGpuSubmissionFence[FrameRingSize];
    private readonly IGpuComputeServices m_gpu;
    private readonly uint m_height;
    private readonly IGpuComputePipeline m_instanceCullPipeline;
    private readonly int m_instanceCapacity;
    private readonly nint[] m_instanceCullSets = new nint[FrameRingSize];
    private readonly IGpuStorageBuffer[] m_instanceGridBuffers = new IGpuStorageBuffer[FrameRingSize];
    private readonly SdfInstanceGridInput[] m_instanceGridInputScratch;
    private readonly SdfInstanceGrid.Workspace m_instanceGridWorkspace;
    private readonly int m_instanceGridWordCapacity;
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
    private readonly IGpuStorageImage?[] m_sourceTextures;
    private readonly IGpuStorageImage m_storageImage;
    private readonly IGpuStorageBuffer m_tileBuffer;
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
    private readonly bool m_liveArmedTiming;
    private IGpuTimingPool[]? m_timingPools;
    private readonly IGpuTimingRecorder? m_timingRecorder;
    // The carve-bake brick pool (carve-bake plan §3): one persistent device-local f32 buffer the sliced bake writes and
    // the beam + views kernels sample. Always allocated (a 1-float filler when the pool is disabled), always bound to
    // the beam/views sets, since both kernels compile the sdfBrickPool binding unconditionally (SDF_SAMPLED_REGIONS).
    private readonly IGpuStorageBuffer m_brickPoolBuffer;
    private readonly int m_brickPoolVoxelCapacity;
    private readonly bool m_brickPoolEnabled;
    // The bake pipeline + per-slot request buffers/sets — created ONLY when the pool is enabled (nothing bakes into a
    // filler). Each slot owns a host-visible request buffer (header + carve list) and a static descriptor set binding
    // that buffer + the shared pool (as a UAV). The per-slot state advances one slice per produced frame (RecordBrickBakeSlices).
    private readonly IGpuComputePipeline? m_brickBakePipeline;
    private readonly IGpuShaderModule? m_brickBakeShaderModule;
    private readonly byte[] m_brickBakePush = new byte[BrickBakePushByteLength];
    private readonly IGpuStorageBuffer[] m_brickRequestBuffers = new IGpuStorageBuffer[SdfBrickPoolLayout.MaxBricks];
    private readonly nint[] m_brickBakeSets = new nint[SdfBrickPoolLayout.MaxBricks];
    private readonly BrickBakeState[] m_brickStates = new BrickBakeState[SdfBrickPoolLayout.MaxBricks];
    private readonly ulong[] m_brickSerials = new ulong[SdfBrickPoolLayout.MaxBricks];
    private readonly int[] m_brickTotalVoxels = new int[SdfBrickPoolLayout.MaxBricks];
    private readonly int[] m_brickVoxelCursor = new int[SdfBrickPoolLayout.MaxBricks];
    private readonly Vector4[] m_brickRequestScratch = new Vector4[(BrickBakeRequestHeaderFloat4Count + MaxBrickCarvesPerBake)];
    private readonly uint m_viewportCapacity;
    private readonly IGpuStorageBuffer[] m_viewportBuffers = new IGpuStorageBuffer[FrameRingSize];
    private readonly byte[] m_viewportScratch;
    private readonly IGpuStorageBuffer m_viewsArgsBuffer;
    // The core-ops Stage 1 variant (see SdfViewsKernelVariant): same bindings array as m_viewsPipeline, so its
    // descriptor-set layout is identically defined — the per-slot m_viewsSets bind against WHICHEVER pipeline
    // UploadProgram selected (compatible layouts on Vulkan; the same slot packing + root-signature shape on
    // Direct3D 12), and no second set/descriptor-write path exists.
    private readonly IGpuComputePipeline m_viewsCorePipeline;
    private readonly IGpuShaderModule m_viewsCoreShaderModule;
    private readonly IGpuComputePipeline m_viewsPipeline;
    private readonly nint[] m_viewsSets = new nint[FrameRingSize];
    private readonly IGpuShaderModule m_viewsShaderModule;
    private readonly uint m_width;
    private int m_currentSlot;
    private bool m_disposed;
    private bool m_imageInitialized;
    private double? m_lastFrameGpuMilliseconds;
    private SdfViewsKernelVariant? m_loggedViewsVariant;
    private int m_liveInstanceMaskWordCount;
    private SdfProgram m_liveProgram = null!;
    private bool m_rebuildInstanceGridPerFrame;
    // CADENCE GATE: whether the LIVE uploaded program declares any ScreenSlab shape (bound or not) — computed once at
    // UploadProgram (the single owner of per-program state), never per frame. A declared-but-unbound slab's face is the
    // animated test-card (screenContent, sdf-world.hlsli), which reads presentation TIME every frame; the signature
    // excludes that lane (ComputeFrameSignature), so this fact is what makes DecideCadenceSkip force a render instead.
    private bool m_programDeclaresScreenSlab;
    private bool m_pipelinedFrameInFlight;
    private IGpuSurfaceReadback? m_readback;
    private int m_requiredDynamicTransformCapacity;
    private ulong m_ringFrame;
    private uint m_screenSourceMask;
    // Latched at the top of each Record: whether THIS frame writes timing marks (available, and in live-armed mode
    // also GpuTimingControl.Shared.Armed). Read again by the submit paths so the readback + m_timingFrame advance
    // agree with what Record recorded, even if the shared arming flips mid-frame.
    private bool m_frameTimingActive;
    private ulong m_timingFrame;
    private bool m_useCoreViews;
    // CADENCE GATE (perf plan Phase 6.1). Latched by PrepareFrame, read by Record: when true, Record skips the
    // mask/beam/cull-args/views passes and re-composites from the retained (single, ring-shared) views output + tile
    // buffer — pixel-identical because the change SIGNATURE below proved every input those passes consume is unchanged.
    private bool m_skipThisFrame;
    // The previous RENDERED frame's change signature (a 64-bit hash of every packed span + revision the skipped passes
    // consume — see ComputeFrameSignature) and whether one exists yet. Reset whenever the gate is off, so re-enabling it
    // always renders the first frame before it can skip.
    private ulong m_previousFrameSignature;
    private bool m_hasPreviousFrameSignature;
    // Monotonic revisions folded into the signature so a change to a resource NOT re-hashed each frame still invalidates
    // it: m_programRevision bumps on every UploadProgram (program words, live mask width, kernel variant, screen-surface
    // reseed), m_decalRevision on every SetScreenDecal/ClearScreenDecal call that ACTUALLY changes the stored bytes (the
    // 820 KB decal buffer is revision-tracked, not re-hashed per frame — both setters change-detect first, since a
    // provider polled every produced frame, e.g. the diegetic terminal mirror, commonly re-supplies unchanged content).
    private ulong m_programRevision;
    private ulong m_decalRevision;
    // STEP 1 instrumentation (perf plan Phase 6.1 follow-up): the previous decided frame's INDEPENDENT per-span hashes
    // (each starting fresh from the FNV basis — unlike m_previousFrameSignature's chained fold, so one span's hash never
    // smears into another's), the cumulative skip/render counts since the gate last armed, and the latest published
    // SdfCadenceDiagnostics. None of this feeds DecideCadenceSkip's skip decision.
    private CadenceSpanHashes m_previousCadenceSpanHashes;
    private bool m_hasPreviousCadenceSpanHashes;
    private ulong m_cadenceSkippedFrameCount;
    private ulong m_cadenceRenderedFrameCount;
    private SdfCadenceDiagnostics m_cadenceDiagnostics;

    /// <summary>Initializes a new instance of the <see cref="SdfWorldEngine"/> class: builds the world pipelines
    /// (the five chain passes plus the Stage 1 core-ops variant), every buffer and image at the provisioned viewport
    /// capacity, and uploads the scene program ONCE.</summary>
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
        m_dynamicTransformCapacity = Math.Max(val1: Math.Max(val1: 1, val2: options.DynamicTransformCapacity), val2: options.Program.RequiredDynamicTransformCapacity);
        m_dynamicTransformScratch = new byte[(m_dynamicTransformCapacity * DynamicTransformByteLength)];
        m_gpu = gpu;
        m_height = height;
        m_instanceCapacity = Math.Max(val1: options.Program.Instances.Count, val2: options.InstanceCapacity);
        m_instanceGridInputScratch = new SdfInstanceGridInput[m_instanceCapacity];
        m_instanceGridWorkspace = new SdfInstanceGrid.Workspace(maxInstances: m_instanceCapacity);
        m_instanceGridWordCapacity = SdfInstanceGrid.WordCapacity(maxInstances: m_instanceCapacity);
        m_tileGridX = ((width + (TileSize - 1)) / TileSize);
        m_tileGridY = ((height + (TileSize - 1)) / TileSize);
        m_viewportCapacity = options.ViewportCapacity;
        m_viewportScratch = new byte[((int)m_viewportCapacity * ViewportByteLength)];
        m_width = width;

        m_beamShaderModule = gpu.ShaderModuleFactory.Create(deviceContext: device, stage: GpuShaderStage.Compute, bytecode: kernels.Beam);
        m_instanceCullShaderModule = gpu.ShaderModuleFactory.Create(deviceContext: device, stage: GpuShaderStage.Compute, bytecode: kernels.InstanceCull);
        m_cullArgsShaderModule = gpu.ShaderModuleFactory.Create(deviceContext: device, stage: GpuShaderStage.Compute, bytecode: kernels.CullArgs);
        m_viewsShaderModule = gpu.ShaderModuleFactory.Create(deviceContext: device, stage: GpuShaderStage.Compute, bytecode: kernels.Views);
        m_viewsCoreShaderModule = gpu.ShaderModuleFactory.Create(deviceContext: device, stage: GpuShaderStage.Compute, bytecode: kernels.ViewsCore);
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
        m_storageImage = ((options.CreateOutputImage is null)
            ? gpu.StorageImageFactory.Create(deviceContext: device, format: Format, height: height, width: width)
            : options.CreateOutputImage(device));
        m_exportableImage = (m_storageImage as IGpuExportableStorageImage);
        m_exportMode = (m_exportableImage is not null);

        m_programWordCapacity = Math.Max(val1: options.Program.Words.Length, val2: options.ProgramWordCapacity);
        m_programBuffer = gpu.StorageBufferFactory.Create(deviceContext: device, sizeBytes: ((ulong)m_programWordCapacity * sizeof(uint)));

        // The HOST-VISIBLE per-frame buffers are duplicated per ring slot (see FrameRingSize): slot k's copies are
        // only rewritten after slot k's fence proves frame k − FrameRingSize retired, so a frame's in-place upload
        // can never race the previous frame's in-flight reads.
        for (var slot = 0; (slot < FrameRingSize); slot++) {
            m_viewportBuffers[slot] = gpu.StorageBufferFactory.Create(deviceContext: device, sizeBytes: (ulong)m_viewportScratch.Length);
            m_dynamicTransformBuffers[slot] = gpu.StorageBufferFactory.Create(deviceContext: device, sizeBytes: (ulong)m_dynamicTransformScratch.Length);
            m_instanceGridBuffers[slot] = gpu.StorageBufferFactory.Create(deviceContext: device, sizeBytes: ((ulong)m_instanceGridWordCapacity * sizeof(uint)));
            // The screen-surface table: always allocated at MaxScreenSurfaces capacity, indexed directly by screen index
            // (like the always-bound dynamic-transform slot), so Stage 1's binding stays valid for a program with none —
            // an all-zero undeclared slot is never addressed (no material id in a consistent program points at it).
            m_screenSurfaceBuffers[slot] = gpu.StorageBufferFactory.Create(deviceContext: device, sizeBytes: (MaxScreenSurfaces * (ulong)ScreenSurfaceByteLength));
            // The per-frame screen-light buffer: the screen colors + environment float4s, uploaded each frame like the
            // dynamic-transform buffer. Bound to the views set only (Stage 1 shades; the beam prepass does not).
            m_screenLightBuffers[slot] = gpu.StorageBufferFactory.Create(deviceContext: device, sizeBytes: (ulong)m_screenLightScratch.Length);
            // The glyph-decal buffer: descriptor band + cell region, uploaded per frame like the screen-light buffer.
            m_decalBuffers[slot] = gpu.StorageBufferFactory.Create(deviceContext: device, sizeBytes: ((ulong)m_decalScratch.Length * sizeof(uint)));
        }
        // The cull buffer is GPU-written by the beam prepass (a UAV), so it is device-local (a Direct3D 12 default heap).
        // Sized for TilePlaneCount planes (marchStart + firstExit + secondEntry — the four-bound teleport — plus the F1
        // far bound); cull-args and the compositor read only plane 0, so their (viewport, tile) indexing is unaffected by
        // the extra capacity.
        m_tileBuffer = gpu.StorageBufferFactory.CreateDeviceLocal(deviceContext: device, sizeBytes: (((((ulong)TilePlaneCount * m_viewportCapacity) * m_tileGridX) * m_tileGridY) * sizeof(float)));
        // The per-tile instance mask: same (viewport, tile) indexing as the cull buffer, GPU-written by the beam
        // prepass alongside it (a UAV, so device-local too), read by Stage 1 to gate its masked map() calls. The
        // buffer is sized for the CONSTRUCTION program's width (ceil(instanceCount/32) uints, at least 1 —
        // SdfProgram.InstanceMaskWordCount); the kernels index with the LIVE uploaded program's width, pushed per
        // frame (m_liveInstanceMaskWordCount), which UploadProgram caps at this construction width.
        m_instanceMaskWordCount = Math.Max(val1: options.Program.InstanceMaskWordCount, val2: SdfProgram.InstanceMaskWordCountFor(instanceCount: options.InstanceCapacity));
        var instanceMaskStorageWordCount = (m_instanceMaskWordCount + ((m_instanceMaskWordCount + 31) / 32));
        m_instanceMaskBuffer = gpu.StorageBufferFactory.CreateDeviceLocal(deviceContext: device, sizeBytes: (((((ulong)m_viewportCapacity * m_tileGridX) * m_tileGridY) * (uint)instanceMaskStorageWordCount) * sizeof(uint)));

        // The carve-bake brick pool (carve-bake plan §3): one persistent DEVICE-LOCAL f32 buffer — device-local so the
        // bake kernel can write it as a UAV (an upload heap forbids UAVs on Direct3D 12) and the beam/views sample it as
        // an SRV. Frozen at the constructed capacity. When the pool is disabled (capacity 0) a single-float filler keeps
        // the always-present sdfBrickPool binding valid — the kernels compile the binding unconditionally, and
        // sdfSampledRegion detects the filler by its element count and renders SampledRegion programs via the
        // conservative uncarved-hull fallback (only RequestBrickBake stays rejected on a pool-less engine).
        m_brickPoolVoxelCapacity = Math.Max(val1: 0, val2: options.BrickPoolVoxelCapacity);
        m_brickPoolEnabled = (m_brickPoolVoxelCapacity > 0);
        m_brickPoolBuffer = gpu.StorageBufferFactory.CreateDeviceLocal(deviceContext: device, sizeBytes: ((ulong)(m_brickPoolEnabled ? m_brickPoolVoxelCapacity : 1) * sizeof(float)));
        // The baker's shader module — created only when the pool is enabled (nothing bakes into a filler).
        m_brickBakeShaderModule = ((m_brickPoolEnabled && !kernels.BrickBake.IsEmpty)
            ? gpu.ShaderModuleFactory.Create(deviceContext: device, stage: GpuShaderStage.Compute, bytecode: kernels.BrickBake)
            : null);

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
            // The brick pool (sdfBrickPool), APPENDED LAST so its SRV resolves to register t4 (after instanceMasks t3) —
            // the cone march samples baked SampledRegion carves. Always present (a filler when the pool is disabled).
            new GpuComputeBinding(Binding: BrickPoolBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
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
            new GpuComputeBinding(Binding: FrameInstanceGridBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
        ];

        // Cull-args reduction: cull buffer read (3) + the views indirect args written (5) + the bbox origin written (6).
        GpuComputeBinding[] cullArgsBindings = [
            new GpuComputeBinding(Binding: TileBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            new GpuComputeBinding(Binding: CullArgsBindingIndex, Kind: GpuComputeBindingKind.StorageBufferReadWrite),
            new GpuComputeBinding(Binding: CullBoundsBindingIndex, Kind: GpuComputeBindingKind.StorageBufferReadWrite),
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
            new GpuComputeBinding(Binding: ProgramBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            new GpuComputeBinding(Binding: ViewportBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            new GpuComputeBinding(Binding: DynamicTransformBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            new GpuComputeBinding(Binding: TileBindingIndex, Kind: GpuComputeBindingKind.StorageBufferReadWrite),
            new GpuComputeBinding(Binding: ViewSourceBindingIndex, Kind: GpuComputeBindingKind.StorageImage, Count: MaxViewports),
            new GpuComputeBinding(Binding: ViewsCullBoundsBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            new GpuComputeBinding(Binding: ScreenSurfaceBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            .. BuildScreenSourceBindings(),
            new GpuComputeBinding(Binding: InstanceMaskBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            // The per-frame screen-light buffer — its SRV resolves to register t38 (after instanceMasks t37).
            new GpuComputeBinding(Binding: ScreenLightBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            // The SDF_SHAPE_GLYPH font atlas: its SRV resolves to register t39 (after screenLights t38) and its
            // static sampler to s32 (after the 32 screen samplers). One more SampledImage on this set, (re)bound per
            // frame by BindScreenSources to the atlas view or the neutral 1×1 filler when none is set.
            new GpuComputeBinding(Binding: GlyphAtlasBindingIndex, Kind: GpuComputeBindingKind.SampledImage),
            // The GLYPH DECAL buffer, so its SRV resolves to register t40 (after the glyph atlas t39) — the
            // material-level text tier the decal-mode screens sample (see sdf-world.hlsli's sdfDecalCells).
            new GpuComputeBinding(Binding: DecalCellsBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            // The brick pool (sdfBrickPool), APPENDED LAST so its SRV resolves to register t41 (after sdfDecalCells t40) —
            // Stage 1 samples baked SampledRegion carves O(1). Always present (a filler when the pool is disabled); the
            // core-ops variant shares this bindings array, so both Stage 1 pipelines bind the pool identically.
            new GpuComputeBinding(Binding: BrickPoolBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            // The frame-local instance grid resolves to t42, after the brick pool's t41.
            new GpuComputeBinding(Binding: FrameInstanceGridBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
        ];

        // Stage 2 (source-agnostic composite): output image (0) + the source array (1) + the cull buffer read (3),
        // which the compositor uses to flatten every empty (culled) tile to a constant.
        GpuComputeBinding[] compositeBindings = [
            new GpuComputeBinding(Binding: CompositeOutputBindingIndex, Kind: GpuComputeBindingKind.StorageImage),
            new GpuComputeBinding(Binding: CompositeSourceBindingIndex, Kind: GpuComputeBindingKind.StorageImage, Count: MaxViewports),
            new GpuComputeBinding(Binding: TileBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
        ];

        // The carve-bake baker's set: the per-slot request buffer (a float4 SRV at t0) + the shared pool WRITTEN as a UAV
        // (u0). One set per brick slot, each binding that slot's request buffer + the pool; only used when the pool is
        // enabled. Direct3D 12 assigns registers from THIS order: t0 bakeRequest, u0 brickPool.
        GpuComputeBinding[] brickBakeBindings = [
            new GpuComputeBinding(Binding: BrickBakeRequestBindingIndex, Kind: GpuComputeBindingKind.StorageBufferRead),
            new GpuComputeBinding(Binding: BrickBakePoolBindingIndex, Kind: GpuComputeBindingKind.StorageBufferReadWrite),
        ];

        m_beamPipeline = gpu.ComputePipelineFactory.Create(bindings: beamBindings, computeShaderModule: m_beamShaderModule, deviceContext: device, pushConstantBinding: pushConstantBinding);
        m_instanceCullPipeline = gpu.ComputePipelineFactory.Create(bindings: instanceCullBindings, computeShaderModule: m_instanceCullShaderModule, deviceContext: device, pushConstantBinding: pushConstantBinding);
        m_cullArgsPipeline = gpu.ComputePipelineFactory.Create(bindings: cullArgsBindings, computeShaderModule: m_cullArgsShaderModule, deviceContext: device, pushConstantBinding: pushConstantBinding);
        // Nearest filtering end to end: a bound screen source (an emulator/child's native pixels) magnifies as crisp
        // cells, never bilinear smears — the whole point of sampling instead of the flat material.
        m_viewsPipeline = gpu.ComputePipelineFactory.Create(bindings: viewsBindings, computeShaderModule: m_viewsShaderModule, deviceContext: device, pushConstantBinding: pushConstantBinding, samplerFilter: GpuSamplerFilter.Nearest);
        // The core-ops Stage 1 variant shares the SAME viewsBindings array (and push/sampler shape), so its layout is
        // identically defined and the per-slot views sets bind against either pipeline — UploadProgram just flips
        // which handle the views dispatch records (m_useCoreViews). One extra pipeline object; zero extra sets.
        m_viewsCorePipeline = gpu.ComputePipelineFactory.Create(bindings: viewsBindings, computeShaderModule: m_viewsCoreShaderModule, deviceContext: device, pushConstantBinding: pushConstantBinding, samplerFilter: GpuSamplerFilter.Nearest);
        m_compositePipeline = gpu.ComputePipelineFactory.Create(bindings: compositeBindings, computeShaderModule: m_compositeShaderModule, deviceContext: device, pushConstantBinding: compositePushBinding);

        // The carve-bake baker pipeline (only when the pool is enabled). Its own push block carries the per-dispatch
        // voxel-slice window (start + count).
        var brickBakePushBinding = new GpuPushConstantBinding(data: m_brickBakePush, offset: 0, stageFlags: GpuShaderStage.Compute);

        m_brickBakePipeline = ((m_brickBakeShaderModule is not null)
            ? gpu.ComputePipelineFactory.Create(bindings: brickBakeBindings, computeShaderModule: m_brickBakeShaderModule, deviceContext: device, pushConstantBinding: brickBakePushBinding)
            : null);

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
        }

        var poolSizes = GpuDescriptorPoolSizes.ForSets([.. poolSetBindings]);

        m_pool = m_descriptorAllocator.CreatePool(deviceHandle: m_deviceHandle, sizes: poolSizes);

        // The cull buffer is read-only here (a stride-4 SRV on Direct3D 12); the args + bounds are written (UAVs).
        m_cullArgsSet = m_descriptorAllocator.AllocateSet(descriptorSetLayoutHandle: m_cullArgsPipeline.DescriptorSetLayoutHandle, deviceHandle: m_deviceHandle, poolHandle: m_pool);
        WriteStorageBufferReadOnly(set: m_cullArgsSet, binding: TileBindingIndex, buffer: m_tileBuffer);
        WriteStorageBufferReadWrite(set: m_cullArgsSet, binding: CullArgsBindingIndex, buffer: m_viewsArgsBuffer);
        WriteStorageBufferReadWrite(set: m_cullArgsSet, binding: CullBoundsBindingIndex, buffer: m_cullBoundsBuffer);

        // The screen sources (bindings 12..43) are (re)bound per frame by BindScreenSources, mirroring the source array —
        // a filler view isn't known until the first frame's SDF source texture (or child surface) exists.
        m_screenSampler = m_descriptorAllocator.CreateSampler(deviceHandle: m_deviceHandle, filter: GpuSamplerFilter.Nearest);

        for (var slot = 0; (slot < FrameRingSize); slot++) {
            var beamSet = m_descriptorAllocator.AllocateSet(descriptorSetLayoutHandle: m_beamPipeline.DescriptorSetLayoutHandle, deviceHandle: m_deviceHandle, poolHandle: m_pool);

            m_beamSets[slot] = beamSet;
            WriteStorageBuffer(set: beamSet, binding: ProgramBindingIndex, buffer: m_programBuffer);
            WriteStorageBuffer(set: beamSet, binding: ViewportBindingIndex, buffer: m_viewportBuffers[slot]);
            WriteStorageBuffer(set: beamSet, binding: DynamicTransformBindingIndex, buffer: m_dynamicTransformBuffers[slot]);
            WriteStorageBufferReadWrite(set: beamSet, binding: TileBindingIndex, buffer: m_tileBuffer);
            WriteStorageBufferReadOnly(set: beamSet, binding: InstanceMaskBindingIndex, buffer: m_instanceMaskBuffer);
            // The brick pool (a stride-4 float SRV, like the instance mask above — never the stride-16 program SRV).
            WriteStorageBufferReadOnly(set: beamSet, binding: BrickPoolBindingIndex, buffer: m_brickPoolBuffer);

            // The instance-cull set: the mask buffer written (the frame's first pass — the beam then reads it).
            var instanceCullSet = m_descriptorAllocator.AllocateSet(descriptorSetLayoutHandle: m_instanceCullPipeline.DescriptorSetLayoutHandle, deviceHandle: m_deviceHandle, poolHandle: m_pool);

            m_instanceCullSets[slot] = instanceCullSet;
            WriteStorageBuffer(set: instanceCullSet, binding: ProgramBindingIndex, buffer: m_programBuffer);
            WriteStorageBuffer(set: instanceCullSet, binding: ViewportBindingIndex, buffer: m_viewportBuffers[slot]);
            WriteStorageBuffer(set: instanceCullSet, binding: DynamicTransformBindingIndex, buffer: m_dynamicTransformBuffers[slot]);
            WriteStorageBufferReadWrite(set: instanceCullSet, binding: InstanceMaskBindingIndex, buffer: m_instanceMaskBuffer);
            WriteStorageBufferReadOnly(set: instanceCullSet, binding: FrameInstanceGridBindingIndex, buffer: m_instanceGridBuffers[slot]);

            var viewsSet = m_descriptorAllocator.AllocateSet(descriptorSetLayoutHandle: m_viewsPipeline.DescriptorSetLayoutHandle, deviceHandle: m_deviceHandle, poolHandle: m_pool);

            m_viewsSets[slot] = viewsSet;
            WriteStorageBuffer(set: viewsSet, binding: ProgramBindingIndex, buffer: m_programBuffer);
            WriteStorageBuffer(set: viewsSet, binding: ViewportBindingIndex, buffer: m_viewportBuffers[slot]);
            WriteStorageBuffer(set: viewsSet, binding: DynamicTransformBindingIndex, buffer: m_dynamicTransformBuffers[slot]);
            WriteStorageBufferReadWrite(set: viewsSet, binding: TileBindingIndex, buffer: m_tileBuffer);
            WriteStorageBufferReadOnly(set: viewsSet, binding: ViewsCullBoundsBindingIndex, buffer: m_cullBoundsBuffer);
            WriteStorageBufferReadOnly(set: viewsSet, binding: InstanceMaskBindingIndex, buffer: m_instanceMaskBuffer);
            // The screen-surface table (48-byte ScreenSurfaceData, same stride-16-multiple SRV pattern as ViewportData).
            WriteStorageBuffer(set: viewsSet, binding: ScreenSurfaceBindingIndex, buffer: m_screenSurfaceBuffers[slot]);
            // The per-frame screen-light buffer (float4 stride — the plain 16-byte WriteStorageBuffer is correct).
            WriteStorageBuffer(set: viewsSet, binding: ScreenLightBindingIndex, buffer: m_screenLightBuffers[slot]);
            // The per-frame glyph-decal buffer (uint4 stride, same 16-byte pattern).
            WriteStorageBuffer(set: viewsSet, binding: DecalCellsBindingIndex, buffer: m_decalBuffers[slot]);
            // The brick pool (a stride-4 float SRV — the shared read side; the bake set below binds the same buffer as a UAV).
            WriteStorageBufferReadOnly(set: viewsSet, binding: BrickPoolBindingIndex, buffer: m_brickPoolBuffer);
            WriteStorageBufferReadOnly(set: viewsSet, binding: FrameInstanceGridBindingIndex, buffer: m_instanceGridBuffers[slot]);

            var compositeSet = m_descriptorAllocator.AllocateSet(descriptorSetLayoutHandle: m_compositePipeline.DescriptorSetLayoutHandle, deviceHandle: m_deviceHandle, poolHandle: m_pool);

            m_compositeSets[slot] = compositeSet;
            m_descriptorAllocator.WriteStorageImage(arrayElement: 0, binding: CompositeOutputBindingIndex, descriptorSetHandle: compositeSet, deviceHandle: m_deviceHandle, imageViewHandle: m_storageImage.ImageViewHandle);
            WriteStorageBufferReadOnly(set: compositeSet, binding: TileBindingIndex, buffer: m_tileBuffer);

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
                var requestBuffer = gpu.StorageBufferFactory.Create(deviceContext: device, sizeBytes: ((ulong)m_brickRequestScratch.Length * (sizeof(float) * 4)));

                m_brickRequestBuffers[brick] = requestBuffer;

                var bakeSet = m_descriptorAllocator.AllocateSet(descriptorSetLayoutHandle: m_brickBakePipeline!.DescriptorSetLayoutHandle, deviceHandle: m_deviceHandle, poolHandle: m_pool);

                m_brickBakeSets[brick] = bakeSet;
                // The request buffer is a float4 (stride-16) SRV; the pool is the stride-4 UAV the baker writes.
                WriteStorageBuffer(set: bakeSet, binding: BrickBakeRequestBindingIndex, buffer: requestBuffer);
                WriteStorageBufferReadWrite(set: bakeSet, binding: BrickBakePoolBindingIndex, buffer: m_brickPoolBuffer);
            }
        }

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
        if ((options.TimingFactory is not null) && (options.TimingRecorder is not null)) {
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

    // Creates the TimingPoolCount rotating timestamp pools once. Called at construction in eager mode, or on the first
    // armed frame in live-armed mode. A no-op after the pools exist.
    private void EnsureTimingPools() {
        if (m_timingPools is not null) {
            return;
        }

        var timingPools = new IGpuTimingPool[TimingPoolCount];

        for (var pool = 0; (pool < TimingPoolCount); pool++) {
            timingPools[pool] = m_timingFactory!.CreateTimestampPool(deviceContext: m_deviceContext, queryCapacity: TimingCapacity);
        }

        m_timingPools = timingPools;
    }

    // A per-ring-slot change-detected view cache (one row per slot, initialized 0 = nothing bound yet).
    private static nint[][] BuildRingViewCache(int width) {
        var cache = new nint[FrameRingSize][];

        for (var slot = 0; (slot < FrameRingSize); slot++) {
            cache[slot] = new nint[width];
        }

        return cache;
    }
    // Per-ring-slot dirty flags starting TRUE (each slot's buffer needs its first upload — see m_decalDirty).
    private static bool[] BuildRingDirtyFlags() {
        var flags = new bool[FrameRingSize];

        Array.Fill(array: flags, value: true);

        return flags;
    }

    // The screen-source binding indices — screenSource{i} at ScreenSourceBindingBase + i — derived from
    // MaxScreenSurfaces so the run can never drift from the cap (never hand-listed).
    private static uint[] BuildScreenSourceBindingIndices() {
        var indices = new uint[MaxScreenSurfaces];

        for (var index = 0; (index < MaxScreenSurfaces); index++) {
            indices[index] = (ScreenSourceBindingBase + (uint)index);
        }

        return indices;
    }
    // The MaxScreenSurfaces screen-source SampledImage bindings, spread into viewsBindings in screen-index order so the
    // D3D12 registers land contiguously (t5..t36). Derived from the same index list the per-frame (re)binds use, so the
    // descriptor pool (GpuDescriptorPoolSizes.ForSets, which counts these) and the writes can never disagree.
    private static GpuComputeBinding[] BuildScreenSourceBindings() {
        var bindings = new GpuComputeBinding[MaxScreenSurfaces];

        for (var index = 0; (index < MaxScreenSurfaces); index++) {
            bindings[index] = new GpuComputeBinding(Binding: ScreenSourceBindingIndices[index], Kind: GpuComputeBindingKind.SampledImage);
        }

        return bindings;
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
    /// <summary>Gets or sets the debug-group label wrapping this engine's whole recorded frame — the outer scope a GPU
    /// capture (RenderDoc / PIX / Nsight) shows around this engine's per-pass groups (so a nested view engine reads as
    /// <c>view:&lt;name&gt;</c> containing its own mask/beam/cull-args/views/composite). Presentation-only; defaults to
    /// <c>world</c> and never affects rendered output.</summary>
    public string DebugLabel { get; set; } = "world";
    /// <summary>Gets the GPU timestamp capabilities when opt-in timing was enabled (period/valid-bits for digests).</summary>
    public GpuTimestampCapabilities TimingCapabilities => m_timingCapabilities;
    /// <summary>Gets whether opt-in GPU timing is AVAILABLE (a supported factory + recorder were supplied). In eager
    /// mode every frame is timed; in live-armed mode a frame is timed only while <see cref="GpuTimingControl.Shared"/>
    /// is armed — see <see cref="SdfWorldEngineOptions.LiveArmedTiming"/>.</summary>
    public bool TimingEnabled => m_timingAvailable;
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

        if (program.Instances.Count > m_instanceCapacity) {
            throw new ArgumentException(message: $"The uploaded program has {program.Instances.Count} instances; the engine was constructed for {m_instanceCapacity} frame-grid entries (increase InstanceCapacity or construct the engine with the larger program).", paramName: nameof(program));
        }

        if (program.RequiredDynamicTransformCapacity > m_dynamicTransformCapacity) {
            throw new ArgumentException(message: $"The uploaded program requires {program.RequiredDynamicTransformCapacity} dynamic-transform slots; the engine was constructed for {m_dynamicTransformCapacity} (increase DynamicTransformCapacity or construct the engine with the larger program).", paramName: nameof(program));
        }

        // Baking and rendering are SPLIT: a pool-less engine (BrickPoolVoxelCapacity 0) still accepts a SampledRegion
        // program. It cannot BAKE (RequestBrickBake stays a loud rejection — nothing to write into), but it RENDERS the
        // region via the shader's conservative uncarved-hull fallback (sdfSampledRegion detects the single-float filler
        // by element count and returns SDF_FAR_DISTANCE, so the Subtraction never bites). Only the pool's own capacity
        // (checked in RequestBrickBake) is the frozen envelope now — not the program's shape declaration.

        // The program buffer is SHARED across the frame ring (a program swap is a rare host event, not per-frame
        // state), so rewriting it must first drain every in-flight frame still reading the current words. A no-op when
        // nothing is outstanding (construction, or a waited harness).
        WaitForFrameRing();

        // A program whose grid contains no active maskable dynamic instance has one invariant ring-local table. Build
        // it against the engine's actual capacity envelope and seed every now-idle slot once. Programs with moving
        // binnable instances retain the per-frame build after the matching transform upload.
        var rebuildInstanceGridPerFrame = program.RequiresFrameInstanceGridRebuild;
        ReadOnlySpan<uint> invariantInstanceGrid = default;

        if (!rebuildInstanceGridPerFrame) {
            invariantInstanceGrid = program.BuildInvariantFrameInstanceGrid(
                inputScratch: m_instanceGridInputScratch,
                workspace: m_instanceGridWorkspace
            );
            ValidateInstanceGridCapacity(words: invariantInstanceGrid);
        }

        m_programBuffer.Write<uint>(data: program.Words);
        // Seed the host-side mirror from the program's declared surfaces (the "program uploaded once" baseline); any
        // SetScreenSurface call made before the next produced frame patches this same mirror before it goes out — a
        // re-upload never resurrects the program's original frame over a live SetScreenSurface write made in between.
        MemoryMarshal.Cast<uint, byte>(span: program.ScreenSurfaceWords).CopyTo(destination: m_screenSurfaceScratch);
        // Every ring slot's copy is now stale relative to the freshly seeded mirror (all idle after the drain above);
        // PrepareFrame's dirty gate catches each one up on its next turn — mirrors m_decalDirty's pattern.
        Array.Fill(array: m_screenSurfaceDirty, value: true);

        if (!rebuildInstanceGridPerFrame) {
            foreach (var instanceGridBuffer in m_instanceGridBuffers) {
                instanceGridBuffer.Write<uint>(data: invariantInstanceGrid);
            }
        }

        m_liveInstanceMaskWordCount = program.InstanceMaskWordCount;
        m_liveProgram = program;
        m_rebuildInstanceGridPerFrame = rebuildInstanceGridPerFrame;
        m_requiredDynamicTransformCapacity = program.RequiredDynamicTransformCapacity;
        // CADENCE GATE: whether ANY declared screen forces every frame to render — see m_programDeclaresScreenSlab.
        m_programDeclaresScreenSlab = ProgramDeclaresShape(program: program, shapeType: SdfShapeType.ScreenSlab);
        // CADENCE GATE: a new program (words, live mask width, kernel variant, reseeded screen-surface table, invariant
        // instance grid) invalidates any prior frame's signature — bump the revision the signature folds in.
        m_programRevision++;

        // Stage 1 kernel-variant selection — a pure function of the uploaded program's instruction stream (see
        // SdfViewsKernelVariant): a program touching any exotic op/shape runs the full-ISA reference kernel; a
        // core-only program runs the exotic-stripped variant, bit-identical by construction (the stripped cases are
        // unreachable) but with far less live register state in the interpreter. Logged (when GPU timing is armed) only
        // when the selection CHANGES, so a per-interaction overworld rebuild doesn't spam the digest stream.
        var exoticTouch = SdfViewsKernelVariants.FirstExoticTouch(program: program);
        var viewsVariant = ((exoticTouch is null) ? SdfViewsKernelVariant.CoreOps : SdfViewsKernelVariant.Full);

        m_useCoreViews = (SdfViewsKernelVariant.CoreOps == viewsVariant);

        if (Views.ViewTiming.Enabled && (m_loggedViewsVariant != viewsVariant)) {
            m_loggedViewsVariant = viewsVariant;
            Console.Error.WriteLine(value: ((exoticTouch is null)
                ? $"[world-timing] {DebugLabel} views variant: core-ops (no exotic op in the program)"
                : $"[world-timing] {DebugLabel} views variant: full (program touches {exoticTouch})"));
        }
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
    /// <param name="screenIndex">The screen source slot (0..31, matching a program's declared
    /// <see cref="SdfScreenSurface.ScreenIndex"/>).</param>
    /// <param name="imageViewHandle">The source's same-device storage-image view, or 0 to unbind.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="screenIndex"/> is outside <c>0..31</c>.</exception>
    public void SetScreenSource(int screenIndex, nint imageViewHandle) {
        if (
            (screenIndex < 0) ||
            (screenIndex >= MaxScreenSurfaces)
        ) {
            throw new ArgumentOutOfRangeException(paramName: nameof(screenIndex), message: $"A screen index must be 0..{(MaxScreenSurfaces - 1)}.");
        }

        m_screenSourceViews[screenIndex] = imageViewHandle;
        m_screenSourceMask = ((0 != imageViewHandle)
            ? m_screenSourceMask | (1u << screenIndex)
            : m_screenSourceMask & ~(1u << screenIndex));
    }
    /// <summary>Uploads the single font atlas the <see cref="SdfShapeType.Glyph"/> primitive samples as a
    /// distance-level field, replacing any previously set atlas. STATIC: unlike a screen source (an external per-frame
    /// image-view handle), this copies the CPU pixels into a device image ONCE and holds the sampleable view for the
    /// engine's lifetime; the next produced frame binds it. The atlas MUST carry the true single-channel signed
    /// distance in the ALPHA channel (this engine's runtime generator, <c>Puck.Text.SdfCoverageAtlas</c>, replicates
    /// its single channel into alpha; an atlas from the font-atlas bake pipeline (<c>tools/font-atlas</c>) already
    /// does). Passing an empty
    /// <paramref name="rgbaPixels"/> clears the atlas back to the neutral 1×1 filler.</summary>
    /// <param name="rgbaPixels">The tightly packed, row-major, top-down RGBA atlas pixels
    /// (<paramref name="width"/> × <paramref name="height"/> × 4 bytes), or empty to clear.</param>
    /// <param name="width">The atlas width in texels.</param>
    /// <param name="height">The atlas height in texels.</param>
    /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
    /// <exception cref="ArgumentException">The dimensions are zero, or the pixel buffer length is not
    /// <paramref name="width"/> × <paramref name="height"/> × 4.</exception>
    public void SetGlyphAtlas(ReadOnlyMemory<byte> rgbaPixels, uint width, uint height) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        if (rgbaPixels.IsEmpty) {
            m_glyphAtlasView = 0;

            return;
        }

        if ((0 == width) || (0 == height)) {
            throw new ArgumentException(message: "A glyph atlas must have non-zero dimensions.");
        }

        if (rgbaPixels.Length != checked((int)((width * height) * 4))) {
            throw new ArgumentException(message: $"A glyph atlas of {width}x{height} needs {((width * height) * 4)} RGBA bytes; got {rgbaPixels.Length}.", paramName: nameof(rgbaPixels));
        }

        // The upload object owns the image + staging + the returned view. One instance held for the lifetime (a re-set
        // re-uploads through it — Vulkan reuses the view, Direct3D 12 replaces it, so re-read the handle every time).
        // The atlas is shared across the frame ring like the program buffer, so a RE-upload (rewriting an image an
        // in-flight frame may still sample) first drains the ring — a rare host event, typically once per engine.
        WaitForFrameRing();

        m_glyphAtlasUpload ??= m_gpu.SurfaceTransferFactory.CreateUpload(deviceContext: m_deviceContext);
        m_glyphAtlasView = m_glyphAtlasUpload.Upload(deviceContext: m_deviceContext, pixels: rgbaPixels, format: Format, width: width, height: height);
    }
    /// <summary>Overwrites screen <paramref name="screenIndex"/>'s world-space sampling frame for the NEXT produced
    /// frame — the per-frame counterpart of the screen-surface table <see cref="UploadProgram"/> otherwise writes only
    /// once, at program upload. A slab riding a moving rig must call
    /// this every frame its geometry moves, or its sampling frame goes stale relative to the geometry the dynamic
    /// transform already moved (a mismatched frame sizes/rotates/positions the sampled image wrong without affecting
    /// the geometry at all — see <see cref="SdfProgramBuilder.ScreenSlab(Vector3, float, Vector3, Vector3, Vector3, int, SdfBlendOp, float)"/>'s
    /// frame contract). Pure host-side buffer state: the shader's <c>screenSurfaces[screenIndex]</c> read
    /// (<c>sdf-world.hlsli</c>) already resolves at shading time with no HLSL change required for this seam — only the
    /// host-side table this call patches needed to become writable per frame. A call that reproduces the entry's
    /// current values (a static screen, or a rig sampled at an unchanged pose) is a no-op — it does not dirty the
    /// upload; the GPU table only re-uploads on an actual change.</summary>
    /// <param name="screenIndex">The screen slot (0..31, matching a program's declared <see cref="SdfScreenSurface.ScreenIndex"/>).</param>
    /// <param name="origin">The front face's world-space center this frame.</param>
    /// <param name="right">The unit world-space axis the UV's U increases along this frame (need not be pre-normalized —
    /// normalized here, matching <see cref="SdfProgramBuilder.ScreenSlab(Vector3, float, Vector3, Vector3, Vector3, int, SdfBlendOp, float)"/>'s contract).</param>
    /// <param name="up">The unit world-space axis the UV's V increases against this frame (V = 0 at the top; normalized here).</param>
    /// <param name="halfWidth">The half-extent along <paramref name="right"/> this frame.</param>
    /// <param name="halfHeight">The half-extent along <paramref name="up"/> this frame.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="screenIndex"/> is outside <c>0..31</c>.</exception>
    public void SetScreenSurface(int screenIndex, Vector3 origin, Vector3 right, Vector3 up, float halfWidth, float halfHeight) {
        if (
            (screenIndex < 0) ||
            (screenIndex >= MaxScreenSurfaces)
        ) {
            throw new ArgumentOutOfRangeException(paramName: nameof(screenIndex), message: $"A screen index must be 0..{(MaxScreenSurfaces - 1)}.");
        }

        var unitRight = Vector3.Normalize(value: right);
        var unitUp = Vector3.Normalize(value: up);
        var floats = MemoryMarshal.Cast<byte, float>(span: m_screenSurfaceScratch.AsSpan());
        // 3 float4 per entry (right.xyz+halfWidth, up.xyz+halfHeight, origin.xyz+pad) — KEEP IN SYNC with SdfProgram's
        // ScreenSurfaceWords packing and sdf-world.hlsli's ScreenSurfaceData.
        var b = (screenIndex * 12);
        // SdfEngineNode polls this every frame via transform providers, often with an unchanged value (a static screen,
        // or a rig sampled at the same pose) — only an actual change needs to dirty the ring (C5 perf plan Phase 1.2).
        var changed =
            (floats[(b + 0)] != unitRight.X) || (floats[(b + 1)] != unitRight.Y) || (floats[(b + 2)] != unitRight.Z) || (floats[(b + 3)] != halfWidth) ||
            (floats[(b + 4)] != unitUp.X) || (floats[(b + 5)] != unitUp.Y) || (floats[(b + 6)] != unitUp.Z) || (floats[(b + 7)] != halfHeight) ||
            (floats[(b + 8)] != origin.X) || (floats[(b + 9)] != origin.Y) || (floats[(b + 10)] != origin.Z);

        if (!changed) {
            return;
        }

        floats[(b + 0)] = unitRight.X; floats[(b + 1)] = unitRight.Y; floats[(b + 2)] = unitRight.Z; floats[(b + 3)] = halfWidth;
        floats[(b + 4)] = unitUp.X; floats[(b + 5)] = unitUp.Y; floats[(b + 6)] = unitUp.Z; floats[(b + 7)] = halfHeight;
        floats[(b + 8)] = origin.X; floats[(b + 9)] = origin.Y; floats[(b + 10)] = origin.Z; floats[(b + 11)] = 0f;
        // Every ring slot's buffer must catch up with the patched mirror when its turn comes.
        Array.Fill(array: m_screenSurfaceDirty, value: true);
    }
    /// <summary>Supplies the colored light a declared screen surface at <paramref name="screenIndex"/> emits into the
    /// room this frame — typically the average color of its framebuffer, so the room glows the game's dominant hue. The
    /// light's position/orientation/extent come from the program's screen-surface table (a screen is an area emitter);
    /// only its color is per-frame. Contributes nothing while the screen is unbound (the shader gates on the same
    /// screen mask <see cref="SetScreenSource"/> maintains) or while the color is zero (a dark screen).</summary>
    /// <param name="screenIndex">The screen slot (0..31, matching a program's declared <see cref="SdfScreenSurface.ScreenIndex"/>).</param>
    /// <param name="color">The emitted light color (linear RGB, typically 0..1).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="screenIndex"/> is outside <c>0..31</c>.</exception>
    public void SetScreenLight(int screenIndex, Vector3 color) {
        if (
            (screenIndex < 0) ||
            (screenIndex >= MaxScreenSurfaces)
        ) {
            throw new ArgumentOutOfRangeException(paramName: nameof(screenIndex), message: $"A screen index must be 0..{(MaxScreenSurfaces - 1)}.");
        }

        m_screenLightColors[screenIndex] = color;
    }
    /// <summary>Binds a GLYPH DECAL (the material-level text tier) to screen slot <paramref name="screenIndex"/> for the
    /// NEXT produced frame: the screen's ScreenSlab face then samples this grid of glyph cells + colours at the hit
    /// instead of a bound image (dense reading text, resolution-independent at walk-up distance — see
    /// <c>sdfSampleGlyphDecal</c>). The carrier geometry is the same screen-surface frame the image path uses (declared
    /// by <see cref="SdfProgramBuilder.ScreenSlab(Vector3, float, Vector3, Vector3, Vector3, int, SdfBlendOp, float)"/>);
    /// a glyph atlas must be uploaded (<see cref="SetGlyphAtlas"/>) for the letters to resolve. Re-set every frame the
    /// text changes; <see cref="ClearScreenDecal"/> reverts the slot to the image/procedural path.</summary>
    /// <param name="screenIndex">The screen slot (0..<see cref="MaxScreenSurfaces"/>-1).</param>
    /// <param name="columns">The grid column count (&gt; 0).</param>
    /// <param name="rows">The grid row count (&gt; 0).</param>
    /// <param name="distanceRange">The atlas's SDF distance range in texels (the AA source; 0 = a raw coverage atlas).</param>
    /// <param name="cellWords">The packed cells, row-major (rows × columns), <see cref="DecalWordsPerCell"/> uints each:
    /// (packedUvTopLeft, packedUvBottomRight [unorm2x16], fgRgba8, bgRgba8); a blank cell packs equal UV corners.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="screenIndex"/>/<paramref name="columns"/>/<paramref name="rows"/> out of range, or the grid exceeds <see cref="MaxScreenDecalCells"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="cellWords"/> is not <c>rows × columns × 4</c> uints.</exception>
    public void SetScreenDecal(int screenIndex, int columns, int rows, float distanceRange, ReadOnlySpan<uint> cellWords) {
        if ((screenIndex < 0) || (screenIndex >= MaxScreenSurfaces)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(screenIndex), message: $"A screen index must be 0..{(MaxScreenSurfaces - 1)}.");
        }

        if ((columns <= 0) || (rows <= 0)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(columns), message: "A decal grid must have positive columns and rows.");
        }

        var cellCount = (columns * rows);

        if (cellCount > MaxScreenDecalCells) {
            throw new ArgumentOutOfRangeException(paramName: nameof(columns), message: $"A decal grid of {columns}×{rows} = {cellCount} cells exceeds the per-screen budget {MaxScreenDecalCells}.");
        }

        if (cellWords.Length != (cellCount * DecalWordsPerCell)) {
            throw new ArgumentException(message: $"A {columns}×{rows} decal needs {(cellCount * DecalWordsPerCell)} cell words; got {cellWords.Length}.", paramName: nameof(cellWords));
        }

        var cellBase = (uint)(DecalDescriptorCount + (screenIndex * MaxScreenDecalCells));
        var descriptorBase = (screenIndex * DecalWordsPerCell);
        var distanceRangeBits = BitConverter.SingleToUInt32Bits(value: distanceRange);
        var cellDestination = m_decalScratch.AsSpan(start: ((int)cellBase * DecalWordsPerCell), length: cellWords.Length);

        // CADENCE GATE ROOT CAUSE (found live via sdf.info's cadence diagnostics — the "revisions" span differed every
        // frame with a fully static debug scene and an idle console): a provider that re-supplies the SAME decal every
        // produced frame (e.g. the diegetic terminal mirroring an untouched console — DiegeticUiDirector.ComposeTerminalDecal
        // returns a fresh SdfScreenDecalFrame wrapper every call even when its cell bytes are unchanged) must not look
        // like new content. Change-detect BEFORE writing, exactly as an ease snaps to its target: a call that reproduces
        // the bytes already stored is a no-op, not a revision bump.
        if (
            (m_decalScratch[(descriptorBase + 0)] == (uint)columns) &&
            (m_decalScratch[(descriptorBase + 1)] == (uint)rows) &&
            (m_decalScratch[(descriptorBase + 3)] == distanceRangeBits) &&
            cellWords.SequenceEqual(other: cellDestination)
        ) {
            return;
        }

        m_decalScratch[(descriptorBase + 0)] = (uint)columns;
        m_decalScratch[(descriptorBase + 1)] = (uint)rows;
        m_decalScratch[(descriptorBase + 2)] = cellBase;
        m_decalScratch[(descriptorBase + 3)] = distanceRangeBits;
        cellWords.CopyTo(destination: cellDestination);
        // Every ring slot's buffer must catch up with the patched mirror when its turn comes.
        Array.Fill(array: m_decalDirty, value: true);
        // CADENCE GATE: the decal buffer is revision-tracked (not re-hashed each frame — it is 820 KB), so a REAL decal
        // change invalidates the signature.
        m_decalRevision++;
    }
    /// <summary>Reverts screen slot <paramref name="screenIndex"/> to the image/procedural path (clears its decal
    /// descriptor's gridCols to 0 — the shader's "no decal" gate). A no-op if the slot carried no decal.</summary>
    /// <param name="screenIndex">The screen slot (0..<see cref="MaxScreenSurfaces"/>-1).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="screenIndex"/> is out of range.</exception>
    public void ClearScreenDecal(int screenIndex) {
        if ((screenIndex < 0) || (screenIndex >= MaxScreenSurfaces)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(screenIndex), message: $"A screen index must be 0..{(MaxScreenSurfaces - 1)}.");
        }

        var descriptorBase = (screenIndex * DecalWordsPerCell);

        // CADENCE GATE: same producer-level fix as SetScreenDecal — a slot that is ALREADY clear (gridCols already 0)
        // must not look like a change; a caller that clears every frame (mirroring SetScreenDecal's every-frame poll)
        // would otherwise defeat the gate exactly as the unconditional bump did.
        if (m_decalScratch[(descriptorBase + 0)] == 0u) {
            return;
        }

        m_decalScratch[(descriptorBase + 0)] = 0u; // gridCols 0 => inert (the image/procedural path applies)
        m_decalScratch[(descriptorBase + 1)] = 0u;
        m_decalScratch[(descriptorBase + 2)] = 0u;
        m_decalScratch[(descriptorBase + 3)] = 0u;
        Array.Fill(array: m_decalDirty, value: true);
        // CADENCE GATE: revision-track the REAL decal change (see SetScreenDecal).
        m_decalRevision++;
    }

    /// <summary>Requests a SLICED background bake of a settled-carve bin's UNION distance field into brick pool slot
    /// <paramref name="slot"/> (carve-bake plan §3). The carve list is copied into the slot's request buffer and the
    /// bake begins slicing across subsequent produced frames (≤ 256K voxels each); it does NOT wait. Re-requesting a
    /// slot cancels its in-flight bake and restarts it, bumping the slot's monotonic bake serial. The slot's word range
    /// is the STATIC <see cref="SdfBrickPoolLayout.SlotWordOffset(int)"/> region, so a SampledRegion instruction the
    /// caller emits with that same offset samples exactly this brick once it reaches <see cref="BrickBakeState.Ready"/>
    /// (poll <see cref="GetBrickState"/>).</summary>
    /// <param name="slot">The brick pool slot, in <c>[0, <see cref="SdfBrickPoolLayout.MaxBricks"/>)</c>.</param>
    /// <param name="request">The bake request (box, cell size, dims, 1/λ, and the sphere carves).</param>
    /// <exception cref="InvalidOperationException">The engine has no brick pool (<c>BrickPoolVoxelCapacity</c> was 0), or is disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is out of range, a dimension is outside
    /// <c>[1, <see cref="SdfBrickPoolLayout.BrickDim"/>]</c>, the cell size or 1/λ is not finite and positive, or the
    /// carve list exceeds the per-bake capacity.</exception>
    public void RequestBrickBake(int slot, BrickBakeRequest request) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        if (!m_brickPoolEnabled) {
            throw new InvalidOperationException(message: "This engine has no brick pool (it was constructed with BrickPoolVoxelCapacity 0); RequestBrickBake is unavailable.");
        }

        if ((slot < 0) || (slot >= SdfBrickPoolLayout.MaxBricks)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(slot), message: $"A brick slot must be in [0, {SdfBrickPoolLayout.MaxBricks}).");
        }

        ValidateBrickDimension(value: request.DimX, name: nameof(request.DimX));
        ValidateBrickDimension(value: request.DimY, name: nameof(request.DimY));
        ValidateBrickDimension(value: request.DimZ, name: nameof(request.DimZ));

        if (!float.IsFinite(f: request.CellSize) || (request.CellSize <= 0f)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(request), message: "A brick bake cell size must be finite and greater than zero.");
        }

        if (!float.IsFinite(f: request.InverseLambda) || (request.InverseLambda <= 0f)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(request), message: "A brick bake inverse-lambda must be finite and greater than zero.");
        }

        var carves = request.Carves.Span;

        if (carves.Length > MaxBrickCarvesPerBake) {
            throw new ArgumentOutOfRangeException(paramName: nameof(request), message: $"A brick bake carries {carves.Length} carves; the per-bake capacity is {MaxBrickCarvesPerBake}.");
        }

        var totalVoxels = ((request.DimX * request.DimY) * request.DimZ);
        var destWordOffset = SdfBrickPoolLayout.SlotWordOffset(slot: slot);

        if ((destWordOffset + totalVoxels) > m_brickPoolVoxelCapacity) {
            throw new ArgumentOutOfRangeException(paramName: nameof(request), message: $"Brick slot {slot}'s {request.DimX}x{request.DimY}x{request.DimZ} = {totalVoxels} voxels at word {destWordOffset} exceed the pool capacity {m_brickPoolVoxelCapacity}.");
        }

        // The request buffer is read across this slot's slices as they dispatch over the next frames; a re-request that
        // rewrites it must first drain every in-flight ring frame, exactly like the shared program/glyph re-uploads.
        WaitForFrameRing();

        // Pack the header + carve list. Header: (boxMin, cellSize), asfloat(dims + carveCount), (asfloat(destWordOffset),
        // 1/λ, 0, 0). KEEP IN SYNC with sdf-brick-bake.comp's request layout.
        m_brickRequestScratch[0] = new Vector4(x: request.BoxMin.X, y: request.BoxMin.Y, z: request.BoxMin.Z, w: request.CellSize);
        m_brickRequestScratch[1] = new Vector4(
            x: BitConverter.UInt32BitsToSingle(value: (uint)request.DimX),
            y: BitConverter.UInt32BitsToSingle(value: (uint)request.DimY),
            z: BitConverter.UInt32BitsToSingle(value: (uint)request.DimZ),
            w: BitConverter.UInt32BitsToSingle(value: (uint)carves.Length)
        );
        m_brickRequestScratch[2] = new Vector4(x: BitConverter.UInt32BitsToSingle(value: (uint)destWordOffset), y: request.InverseLambda, z: 0f, w: 0f);
        carves.CopyTo(destination: m_brickRequestScratch.AsSpan(start: BrickBakeRequestHeaderFloat4Count));

        m_brickRequestBuffers[slot].Write<Vector4>(data: m_brickRequestScratch.AsSpan(start: 0, length: (BrickBakeRequestHeaderFloat4Count + carves.Length)));

        m_brickStates[slot] = BrickBakeState.Baking;
        m_brickTotalVoxels[slot] = totalVoxels;
        m_brickVoxelCursor[slot] = 0;
        m_brickSerials[slot]++;
    }

    /// <summary>Polls brick pool slot <paramref name="slot"/>'s current bake state and serial (carve-bake plan §3) —
    /// the frame source reads this each produced frame to know when a bake has finished and it may swap the bin's
    /// analytic carves for the one SampledRegion instance sampling this slot. <see cref="BrickBakeState.Ready"/> means
    /// every slice has been recorded; the engine's cross-frame barrier orders those writes before any later frame's
    /// render read, so a program that references the slot only after seeing Ready never samples an incomplete brick.</summary>
    /// <param name="slot">The brick pool slot, in <c>[0, <see cref="SdfBrickPoolLayout.MaxBricks"/>)</c>.</param>
    /// <returns>The slot's state and monotonic bake serial.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is out of range.</exception>
    public BrickBakeStatus GetBrickState(int slot) {
        if ((slot < 0) || (slot >= SdfBrickPoolLayout.MaxBricks)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(slot), message: $"A brick slot must be in [0, {SdfBrickPoolLayout.MaxBricks}).");
        }

        return new BrickBakeStatus(State: m_brickStates[slot], Serial: m_brickSerials[slot]);
    }

    /// <summary>Whether this engine provisions a brick pool (its <c>BrickPoolVoxelCapacity</c> was non-zero) — the
    /// <see cref="ISdfBrickBakeService"/> predicate the carve-bake planner checks before ever proposing a bake, so a
    /// pool-less engine keeps every carve analytic instead of throwing at <see cref="RequestBrickBake"/>.</summary>
    public bool BrickBakeAvailable => m_brickPoolEnabled;

    private static void ValidateBrickDimension(int value, string name) {
        if ((value < 1) || (value > SdfBrickPoolLayout.BrickDim)) {
            throw new ArgumentOutOfRangeException(paramName: name, message: $"A brick dimension must be in [1, {SdfBrickPoolLayout.BrickDim}] (a slot reserves one {SdfBrickPoolLayout.BrickDim}³ cube).");
        }
    }

    // Whether the program's instruction stream declares any shape of the given type — a one-time UploadProgram walk
    // backing per-program facts (the SampledRegion frozen-envelope guard; the cadence gate's ScreenSlab force-render).
    private static bool ProgramDeclaresShape(SdfProgram program, SdfShapeType shapeType) {
        foreach (var instruction in program.Instructions) {
            if ((instruction.Op == SdfOp.ShapeBlend) && ((SdfShapeType)instruction.Shape == shapeType)) {
                return true;
            }
        }

        return false;
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
        m_gpu.QueueSubmitter.SubmitAndWait(commandBufferHandles: [m_commandPools[m_currentSlot].CommandBufferHandle], deviceContext: m_deviceContext);

        // The wait above completed this frame's pool, so its marks are readable immediately. m_frameTimingActive was
        // latched by Record; the ring index only advances on timed frames so the pool selection stays consistent.
        if (m_frameTimingActive) {
            Span<ulong> ticks = stackalloc ulong[(int)TimingMarkCount];
            var pool = m_timingPools![(int)(m_timingFrame % (ulong)TimingPoolCount)];

            m_lastFrameGpuMilliseconds = ((m_timingRecorder!.ReadTimestamps(deviceHandle: m_deviceHandle, firstQuery: 0, poolHandle: pool.PoolHandle, queryCount: TimingMarkCount, rawTicks: ticks) < TimingMarkCount)
                ? null
                : m_timingCapabilities.TicksToMilliseconds(startTicks: ticks[0], endTicks: ticks[((int)TimingMarkCount - 1)]));

            m_timingFrame++;
        }

        return ReadPixels().ToArray();
    }

    /// <summary>Records and submits one frame FIRE-AND-FORGET — the live node path. The submit arms the current ring
    /// slot's fence: nothing waits here, and the ONLY wait a later frame pays is that slot fence in
    /// <c>PrepareFrame</c>, <see cref="FrameRingSize"/> frames later — so a pipelining host overlaps this frame's GPU
    /// execution with the next frame's CPU production. In export mode the consumer lives on another backend with no
    /// shared timeline, so this DOES drain the producer queue (<see cref="IGpuExportableStorageImage.FinalizeForExport"/>)
    /// before the shared handle is handed off.</summary>
    /// <param name="frame">The per-frame data: views (cameras + regions), time, and the dynamic entity transforms.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The frame has zero views or more than the provisioned capacity.</exception>
    /// <exception cref="InvalidOperationException">A pipelined preview frame is still in flight on this engine.</exception>
    public void SubmitFrame(SdfFrame frame) {
        ThrowIfPipelinedFrameInFlight();

        var viewportCount = PrepareFrame(frame: frame);

        Record(viewportCount: viewportCount);
        m_gpu.QueueSubmitter.Submit(commandBufferHandles: [m_commandPools[m_currentSlot].CommandBufferHandle], deviceContext: m_deviceContext, fence: m_frameFences[m_currentSlot]);
        m_exportableImage?.FinalizeForExport();

        // The ring index advances only on TIMED frames (Record latched m_frameTimingActive), so disarmed frames leave
        // the last timed frame's pool readable and the N−FrameRingSize readback contract intact across arm/disarm gaps.
        if (m_frameTimingActive) {
            m_timingFrame++;
        }
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
        // Fire-and-forget compute submit (the SAME fenced call SubmitFrame uses), then the fenced-but-unwaited
        // readback copy. The readback lives on this engine and tracks its own single outstanding fence; the timing
        // path is not driven here (this path is preview-only and never constructed with a timing pool).
        m_gpu.QueueSubmitter.Submit(commandBufferHandles: [m_commandPools[m_currentSlot].CommandBufferHandle], deviceContext: m_deviceContext, fence: m_frameFences[m_currentSlot]);
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

    /// <summary>Reads frame N−<see cref="FrameRingSize"/>'s per-pass GPU times — the newest frame the ring's slot
    /// fence PROVES retired, so a pipelining fire-and-forget host reads them complete with no added stall (the
    /// TimingPoolCount rotating pools). Fills <paramref name="passMilliseconds"/> with one entry per
    /// <see cref="PassTimingLabels"/> (same order) and reports the whole-frame span separately.</summary>
    /// <param name="passMilliseconds">Receives each pass's milliseconds, in <see cref="PassTimingLabels"/> order; must be
    /// at least <see cref="PassTimingCount"/> long.</param>
    /// <param name="passCount">The number of pass entries written (equals <see cref="PassTimingCount"/> on success, 0 otherwise).</param>
    /// <param name="frame">The whole-frame (frame-start → last-close) milliseconds.</param>
    /// <returns>Whether timing is live and the previous frame's marks were readable.</returns>
    public bool TryReadPassTimings(Span<double> passMilliseconds, out int passCount, out double frame) {
        passCount = 0;
        frame = 0.0;

        // The pools may not exist yet (live-armed mode before the first armed frame), and m_timingFrame counts only
        // timed frames, so the warmup guard holds across arm/disarm gaps — a disarmed frame simply leaves the last
        // timed frame's pool readable rather than advancing past it.
        if (
            (m_timingPools is null) ||
            (m_timingFrame < (ulong)TimingPoolCount)
        ) {
            return false;
        }

        // After frame k's submit m_timingFrame is k+1; frame k−FrameRingSize recorded into pool
        // (k − FrameRingSize) % TimingPoolCount == (k + 1) % TimingPoolCount (the pool counts differ by one), and
        // that pool is not reset again until frame k+1 — so this read targets a complete, stable pool.
        var previousPool = m_timingPools![(int)(m_timingFrame % (ulong)TimingPoolCount)];
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
            if (string.Equals(a: PassLabels[index], b: label, comparisonType: StringComparison.Ordinal)) {
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

        // Drain the device BEFORE destroying anything (tolerating an already-lost device, where there is nothing
        // left to drain): the host permits frames in flight, and this engine's resources can
        // be referenced by OTHER in-flight work this engine's own fences cannot see — a view engine released mid-run
        // (the reveal transition) is destroyed while the MAIN engine's in-flight frame still samples its output as a
        // screen source. Pre-ring this was the host's job ("wait-free by contract"); now disposal is the one seam
        // that knows a resource is about to vanish, so the drain lives here.
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

        foreach (var requestBuffer in m_brickRequestBuffers) {
            requestBuffer?.Dispose();
        }

        m_brickPoolBuffer.Dispose();
        m_brickBakePipeline?.Dispose();
        m_brickBakeShaderModule?.Dispose();
        m_beamPipeline.Dispose();
        m_instanceCullPipeline.Dispose();
        m_cullArgsPipeline.Dispose();
        m_viewsPipeline.Dispose();
        m_viewsCorePipeline.Dispose();
        m_compositePipeline.Dispose();
        m_descriptorAllocator.DestroySampler(deviceHandle: m_deviceHandle, samplerHandle: m_screenSampler);
        m_descriptorAllocator.DestroyPool(deviceHandle: m_deviceHandle, poolHandle: m_pool);

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

        // FRAME RING: advance to this produced frame's slot (keyed to the produced-frame count — deterministic, never
        // wall clock), then wait that slot's fence: it was armed by frame N − FrameRingSize's submit, so once it
        // signals, every resource about to be rewritten below (command buffer, host-visible buffers, descriptor
        // sets) is provably idle. A never-armed or already-waited fence is a no-op, so waited/first frames pass free.
        var slot = (int)(m_ringFrame % FrameRingSize);

        m_currentSlot = slot;
        m_ringFrame++;
        m_frameFences[slot].Wait();

        BindSources(viewportCount: viewportCount);
        BindScreenSources();
        PackViewports(frame: frame, viewportCount: viewportCount);
        m_viewportBuffers[slot].Write<byte>(data: m_viewportScratch);
        PackDynamicTransforms(frame: frame);
        m_dynamicTransformBuffers[slot].Write<byte>(data: m_dynamicTransformScratch);
        // Re-bin only when an active maskable dynamic instance can move a grid entry. Invariant programs had every
        // ring slot seeded by UploadProgram; rewriting the same CSR words every frame would be pure CPU/upload work.
        if (m_rebuildInstanceGridPerFrame) {
            var frameGrid = m_liveProgram.BuildFrameInstanceGrid(
                transforms: frame.DynamicTransforms,
                inputScratch: m_instanceGridInputScratch,
                workspace: m_instanceGridWorkspace
            );

            ValidateInstanceGridCapacity(words: frameGrid);
            m_instanceGridBuffers[slot].Write<uint>(data: frameGrid);
        }
        // The screen-surface table: UploadProgram seeds the host mirror once; any SetScreenSurface call since patches
        // it in place. Unlike the buffers above, this one is only re-uploaded when a value-changing SetScreenSurface
        // (or UploadProgram) actually dirtied this slot's copy — a static program's screens never rewrite this table
        // after their first upload, and a screen riding a dynamic transform still renders fresh every produced frame,
        // because SetScreenSurface dirties every ring slot the instant a value actually changes (C5, screen-surface
        // upload; the screen-light buffer below stays unconditional — it also carries per-frame env/grid/bench rows
        // that genuinely dirty most frames).
        if (m_screenSurfaceDirty[slot]) {
            m_screenSurfaceBuffers[slot].Write<byte>(data: m_screenSurfaceScratch);
            m_screenSurfaceDirty[slot] = false;
        }

        PackScreenLights(frame: frame);
        m_screenLightBuffers[slot].Write<byte>(data: m_screenLightScratch);
        // The glyph-decal buffer: SetScreenDecal/ClearScreenDecal patch the host mirror; unlike the buffers above this
        // one is only re-uploaded when a decal call actually dirtied this slot's copy — it is 820 KB, and a program
        // that never touches decals (e.g. the bare revealed room) must not pay that upload every frame. An all-zero
        // mirror (no decal declared) uploads inert descriptors once per slot's first frame so the GPU buffers'
        // initial contents are known-zero — byte-identical shading either way.
        if (m_decalDirty[slot]) {
            m_decalBuffers[slot].Write<uint>(data: m_decalScratch);
            m_decalDirty[slot] = false;
        }

        // CompositeParams { uint2 imageExtent; uint2 tileGrid; uint viewportCount; uint childMask; uint screenMask; uint instanceMaskWordCount; } — Stage 0/1 push.
        var pushWords = MemoryMarshal.Cast<byte, uint>(span: m_pushConstant.AsSpan());

        pushWords[0] = m_width; pushWords[1] = m_height; pushWords[2] = m_tileGridX; pushWords[3] = m_tileGridY; pushWords[4] = viewportCount; pushWords[5] = m_childMask; pushWords[6] = m_screenSourceMask; pushWords[7] = (uint)m_liveInstanceMaskWordCount;

        BuildCompositePush(frame: frame);
        DecideCadenceSkip(frame: frame, viewportCount: viewportCount);

        return viewportCount;
    }

    // CADENCE GATE (perf plan Phase 6.1) — latch whether Record may skip the mask/beam/cull-args/views passes and
    // re-composite from the retained (ring-SHARED, single) views output + tile buffer. A skip is permitted ONLY when the
    // gate is enabled AND this frame's change signature exactly matches the last RENDERED frame's AND the live program
    // declares no ScreenSlab AND no carve bake is in progress.
    //
    // SIGNATURE COVERAGE — the signature (ComputeFrameSignature) folds in EVERYTHING the four skipped passes consume:
    //   - m_programRevision  : the uploaded program (words, live instance-mask width, kernel variant, reseeded
    //                          screen-surface table, invariant instance grid) — bumped by UploadProgram.
    //   - m_pushConstant     : Stage 0/1 push — width/height/tileGrid (constant), viewportCount, childMask,
    //                          screenSourceMask (bound-slot bitmask), liveInstanceMaskWordCount.
    //   - m_viewportScratch  : per-view camera basis + fov/aspect, region, debug view mode, and the quantized
    //                          render-scale numerator — EXCLUDING each row's presentation-TIME lane (PackViewports'
    //                          position.w; byte offset 12 of each 96-byte ViewportData row). Time free-runs every
    //                          frame (it feeds the animated test-card in screenContent, sdf-world.hlsli), so hashing it
    //                          would make the signature never repeat and the gate permanently inert — the defect this
    //                          exclusion fixes (verified live: the static debug scene never skipped with the gate on).
    //                          Any camera ease still counts (it changes the surrounding lanes in the same row).
    //   - m_dynamicTransformScratch : every moving entity's position/orientation + soft-shadow participation. Also
    //                          covers the frame instance grid (a pure function of these transforms + the program).
    //   - m_screenSurfaceScratch : the screen-surface sampling table (a slab riding a dynamic rig re-poses here).
    //   - m_screenLightScratch : per-screen glow colors + the environment row (ambient/sun/slice) + the grid-overlay
    //                          rows + the engine-bench lever rows (soft-shadow/AO/shadow-distance/screen-lights) + the
    //                          shadow-proxy rows + the analytic-normal and shadow-cull toggles — every shading lever.
    //   - m_decalRevision    : the glyph-decal buffer — revision-tracked (it is 820 KB, not re-hashed each frame).
    // DELIBERATELY EXCLUDED (composite-only inputs — composite runs EVERY frame, so a change to them is applied by this
    // frame's composite and can never produce a stale pixel): the composite push's UpscaleSharpness lane and WarpAmount.
    // The region layout IS covered (it rides m_viewportScratch, because Stage 1 renders into the region extent).
    // NOT COVERED BY ANY PACKED SPAN — handled conservatively by forcing a render:
    //   - m_programDeclaresScreenSlab (computed once at UploadProgram — see there): covers BOTH the declared-but-UNBOUND
    //     case (the excluded time lane is the sole per-frame driver of screenContent's test-card — verified the sole
    //     consumer of ViewportData.position.w — sdf-world.hlsli's renderView reads it into `time` at exactly one call
    //     site, gated on `material >= SDF_SCREEN_MATERIAL`) AND the BOUND case (a live CRT's image content updates in
    //     place each frame with the same view handle, unseen by any packed span) — force-renders on ANY declared
    //     ScreenSlab regardless of binding, cheaper to reason about than tracking per-slab bound state here.
    //     CADENCE GATE ROOT CAUSE (found live via sdf.info's cadence diagnostics — a fully static, zero-ScreenSlab
    //     debug scene never skipped even after the signature repeated exactly): a raw `m_screenSourceMask != 0` check
    //     used to ALSO force every render, regardless of whether the LIVE PROGRAM declares any ScreenSlab at all —
    //     m_screenSourceMask is per-ENGINE state (which slots ANY program, including the room's booted consoles, has
    //     ever bound), not per-program, so a live console booted underneath an unrelated fullscreen takeover kept
    //     forcing that takeover to fully re-render every frame even though sampleScreenSurface is PROVABLY unreachable
    //     without a ScreenSlab material (verified above). Removed: whenever !m_programDeclaresScreenSlab holds, no
    //     shader invocation this frame can read ANY screen source, so the mask's value cannot affect the pixels — the
    //     check was pure dead weight that only ever produced a false block, never a real one (m_screenSourceMask IS
    //     still folded into m_pushConstant/the Push span, so a bind/unbind transition is still caught there).
    //   - AnyBrickBaking() : an in-progress carve bake writing brick voxels each frame.
    // Refinement path: a per-source content revision the provider supplies (then a static bound source could skip),
    // and a "settled" flag once every bake completes.
    private void DecideCadenceSkip(SdfFrame frame, uint viewportCount) {
        if (!frame.EnableCadenceGate) {
            // OFF: never skip (byte-identical to a build without the gate), and forget any prior signature so the first
            // frame after the gate is re-enabled always renders before it can skip. Diagnostics reset alongside it (the
            // "since gate-arm" counters restart the instant the gate re-arms).
            m_skipThisFrame = false;
            m_hasPreviousFrameSignature = false;
            m_hasPreviousCadenceSpanHashes = false;
            m_cadenceSkippedFrameCount = 0;
            m_cadenceRenderedFrameCount = 0;
            m_cadenceDiagnostics = default;

            return;
        }

        var signature = ComputeFrameSignature(viewportCount: viewportCount);
        var brickBaking = AnyBrickBaking();

        m_skipThisFrame =
            m_hasPreviousFrameSignature &&
            (signature == m_previousFrameSignature) &&
            !m_programDeclaresScreenSlab &&
            !brickBaking;
        m_previousFrameSignature = signature;
        m_hasPreviousFrameSignature = true;

        if (m_skipThisFrame) {
            m_cadenceSkippedFrameCount++;
        } else {
            m_cadenceRenderedFrameCount++;
        }

        var spanHashes = ComputeCadenceSpanHashes(viewportCount: viewportCount);
        var changedSpans = SdfCadenceSpan.None;

        if (m_hasPreviousCadenceSpanHashes) {
            if (spanHashes.Revisions != m_previousCadenceSpanHashes.Revisions) { changedSpans |= SdfCadenceSpan.Revisions; }
            if (spanHashes.Push != m_previousCadenceSpanHashes.Push) { changedSpans |= SdfCadenceSpan.Push; }
            if (spanHashes.Viewports != m_previousCadenceSpanHashes.Viewports) { changedSpans |= SdfCadenceSpan.Viewports; }
            if (spanHashes.Dynamics != m_previousCadenceSpanHashes.Dynamics) { changedSpans |= SdfCadenceSpan.Dynamics; }
            if (spanHashes.ScreenSurfaces != m_previousCadenceSpanHashes.ScreenSurfaces) { changedSpans |= SdfCadenceSpan.ScreenSurfaces; }
            if (spanHashes.ScreenLights != m_previousCadenceSpanHashes.ScreenLights) { changedSpans |= SdfCadenceSpan.ScreenLights; }
        }

        m_cadenceDiagnostics = new SdfCadenceDiagnostics(
            GateEnabled: true,
            Skipped: m_skipThisFrame,
            SkippedFrameCount: m_cadenceSkippedFrameCount,
            RenderedFrameCount: m_cadenceRenderedFrameCount,
            RevisionsHash: spanHashes.Revisions,
            PushHash: spanHashes.Push,
            ViewportsHash: spanHashes.Viewports,
            DynamicsHash: spanHashes.Dynamics,
            ScreenSurfacesHash: spanHashes.ScreenSurfaces,
            ScreenLightsHash: spanHashes.ScreenLights,
            ChangedSpans: changedSpans,
            ScreenSourceBound: (0u != m_screenSourceMask),
            ProgramDeclaresScreenSlab: m_programDeclaresScreenSlab,
            BrickBaking: brickBaking
        );
        m_previousCadenceSpanHashes = spanHashes;
        m_hasPreviousCadenceSpanHashes = true;
    }

    /// <summary>The cadence gate's per-span diagnostics for the most recently decided frame (see <see cref="SdfCadenceDiagnostics"/>).
    /// Default (all-zero, <see cref="SdfCadenceDiagnostics.GateEnabled"/> false) until the gate first arms.</summary>
    public SdfCadenceDiagnostics CadenceDiagnostics => m_cadenceDiagnostics;

    // The 64-bit FNV-1a change signature over every packed span + revision the skipped passes consume (see
    // DecideCadenceSkip for the coverage rationale). Hashing the WHOLE scratch buffers (including any rows past the live
    // count) is deliberately conservative: extra stale bytes can only make two frames look DIFFERENT (a redundant
    // render), never make a changed frame look the SAME (a stale skip). A collision would require a 64-bit hash clash
    // across two genuinely different input sets — negligible, and still only presentation, never simulation.
    private ulong ComputeFrameSignature(uint viewportCount) {
        var hash = FnvOffsetBasis;

        Span<byte> revisions = stackalloc byte[(sizeof(ulong) * 3)];

        MemoryMarshal.Write(destination: revisions[..sizeof(ulong)], value: in m_programRevision);
        MemoryMarshal.Write(destination: revisions.Slice(start: sizeof(ulong), length: sizeof(ulong)), value: in m_decalRevision);
        var viewportCountWide = (ulong)viewportCount;
        MemoryMarshal.Write(destination: revisions.Slice(start: (sizeof(ulong) * 2), length: sizeof(ulong)), value: in viewportCountWide);

        hash = Fold(hash: hash, bytes: revisions);
        hash = Fold(hash: hash, bytes: m_pushConstant);
        hash = FoldViewportsExcludingTime(hash: hash, viewportScratch: m_viewportScratch);
        hash = Fold(hash: hash, bytes: m_dynamicTransformScratch);
        hash = Fold(hash: hash, bytes: m_screenSurfaceScratch);
        hash = Fold(hash: hash, bytes: m_screenLightScratch);

        return hash;
    }

    private const ulong FnvOffsetBasis = 1469598103934665603UL;

    // STEP 1 instrumentation: the same six spans ComputeFrameSignature chains, but each hashed INDEPENDENTLY (fresh
    // from the FNV basis) so DecideCadenceSkip can name exactly which span changed frame-to-frame. Never used for the
    // skip decision — a diagnostics-only read of state ComputeFrameSignature already touched.
    private readonly record struct CadenceSpanHashes(ulong Revisions, ulong Push, ulong Viewports, ulong Dynamics, ulong ScreenSurfaces, ulong ScreenLights);

    private CadenceSpanHashes ComputeCadenceSpanHashes(uint viewportCount) {
        Span<byte> revisions = stackalloc byte[(sizeof(ulong) * 3)];

        MemoryMarshal.Write(destination: revisions[..sizeof(ulong)], value: in m_programRevision);
        MemoryMarshal.Write(destination: revisions.Slice(start: sizeof(ulong), length: sizeof(ulong)), value: in m_decalRevision);
        var viewportCountWide = (ulong)viewportCount;
        MemoryMarshal.Write(destination: revisions.Slice(start: (sizeof(ulong) * 2), length: sizeof(ulong)), value: in viewportCountWide);

        return new CadenceSpanHashes(
            Revisions: Fold(hash: FnvOffsetBasis, bytes: revisions),
            Push: Fold(hash: FnvOffsetBasis, bytes: m_pushConstant),
            Viewports: FoldViewportsExcludingTime(hash: FnvOffsetBasis, viewportScratch: m_viewportScratch),
            Dynamics: Fold(hash: FnvOffsetBasis, bytes: m_dynamicTransformScratch),
            ScreenSurfaces: Fold(hash: FnvOffsetBasis, bytes: m_screenSurfaceScratch),
            ScreenLights: Fold(hash: FnvOffsetBasis, bytes: m_screenLightScratch)
        );
    }

    private static ulong Fold(ulong hash, ReadOnlySpan<byte> bytes) {
        foreach (var b in bytes) {
            hash = ((hash ^ b) * 1099511628211UL); // FNV-1a prime
        }

        return hash;
    }

    // Folds m_viewportScratch a row at a time, skipping each 96-byte ViewportData row's presentation-TIME lane (byte
    // offset 12, one float — PackViewports' position.w) so the signature is invariant to time's per-frame advance.
    // Time-driven content is instead covered by DecideCadenceSkip's m_programDeclaresScreenSlab force-render — see the
    // SIGNATURE COVERAGE comment there. Allocation-free: no copy, just two folded spans per row instead of one.
    private static ulong FoldViewportsExcludingTime(ulong hash, ReadOnlySpan<byte> viewportScratch) {
        const int TimeLaneOffset = (sizeof(float) * 3); // position.xyz precede time in each row (PackViewports)
        const int TimeLaneLength = sizeof(float);

        for (var rowStart = 0; (rowStart < viewportScratch.Length); rowStart += ViewportByteLength) {
            var row = viewportScratch.Slice(start: rowStart, length: ViewportByteLength);

            hash = Fold(hash: hash, bytes: row[..TimeLaneOffset]);
            hash = Fold(hash: hash, bytes: row[(TimeLaneOffset + TimeLaneLength)..]);
        }

        return hash;
    }

    // Whether any brick slot is mid-bake: a Baking slot has RecordBrickBakeSlices writing new voxels every frame, so the
    // sampled field the beam/views marches changes with no packed-span change — the cadence gate must render through it.
    private bool AnyBrickBaking() {
        if (m_brickBakePipeline is null) {
            return false;
        }

        for (var slot = 0; (slot < SdfBrickPoolLayout.MaxBricks); slot++) {
            if (m_brickStates[slot] == BrickBakeState.Baking) {
                return true;
            }
        }

        return false;
    }

    private void ValidateInstanceGridCapacity(ReadOnlySpan<uint> words) {
        if (words.Length > m_instanceGridWordCapacity) {
            throw new InvalidOperationException(message: $"The frame instance grid packed {words.Length} words into a {m_instanceGridWordCapacity}-word construction envelope.");
        }
    }

    // Waits until every in-flight ring frame has retired — the drain the rare SHARED-resource rewrites (program
    // upload, glyph-atlas re-upload) pay so they never race a pipelined frame. A no-op when nothing is outstanding.
    private void WaitForFrameRing() {
        foreach (var fence in m_frameFences) {
            fence.Wait();
        }
    }

    // Bind (or rebind when a child's image-view changed) the source array in both the CURRENT ring slot's Stage 1
    // (views) and Stage 2 (composite) sets: an SDF source texture for a normal slot, the hosted child's storage image
    // for a child slot. Array elements past the live viewport count duplicate slot 0 (Vulkan requires every bound
    // array element to be a valid descriptor); the kernels never read them. The change-detected cache is per ring
    // slot (a slot's set is only rewritten after its fence proved the slot idle).
    private void BindSources(uint viewportCount) {
        var fillerView = SourceViewForSlot(slot: 0);
        var boundViews = m_boundSourceViews[m_currentSlot];

        for (var element = 0u; (element < MaxViewports); element++) {
            var view = ((element < viewportCount) ? SourceViewForSlot(slot: (int)element) : fillerView);

            if (view == boundViews[element]) {
                continue;
            }

            m_descriptorAllocator.WriteStorageImage(arrayElement: element, binding: ViewSourceBindingIndex, descriptorSetHandle: m_viewsSets[m_currentSlot], deviceHandle: m_deviceHandle, imageViewHandle: view);
            m_descriptorAllocator.WriteStorageImage(arrayElement: element, binding: CompositeSourceBindingIndex, descriptorSetHandle: m_compositeSets[m_currentSlot], deviceHandle: m_deviceHandle, imageViewHandle: view);
            boundViews[element] = view;
        }
    }
    // (Re)bind the MaxScreenSurfaces screen-source bindings (Stage 1 only) — a slot with no host-supplied source this
    // frame duplicates the DEDICATED ShaderReadOnly filler (m_screenSourceFiller; NOT the sources[] filler BindSources
    // uses, which lives in the General/UAV layout Stage 1/2 read/write it in — aliasing that here would violate the
    // combined-image-sampler binding's required layout the instant any viewport-source dispatch ran). The shader
    // never samples an unbound slot (params.screenMask gates it), so the filler's content never reaches a pixel. Each
    // is a SCALAR binding, not one array (see ScreenSourceBindingIndices), so each is written at arrayElement 0; the
    // change-detected rebind means an idle scene (few sources bound) only writes descriptors that actually changed.
    private void BindScreenSources() {
        var fillerView = m_screenSourceFiller.ImageViewHandle;
        var boundViews = m_boundScreenSourceViews[m_currentSlot];
        var viewsSet = m_viewsSets[m_currentSlot];

        for (var element = 0u; (element < MaxScreenSurfaces); element++) {
            var view = ((0 != m_screenSourceViews[element]) ? m_screenSourceViews[element] : fillerView);

            if (view == boundViews[element]) {
                continue;
            }

            m_descriptorAllocator.WriteCombinedImageSampler(arrayElement: 0, binding: ScreenSourceBindingIndices[(int)element], descriptorSetHandle: viewsSet, deviceHandle: m_deviceHandle, imageViewHandle: view, samplerHandle: m_screenSampler);
            boundViews[element] = view;
        }

        // The glyph atlas rides the same ShaderReadOnly filler when unset, and the same change-detected rebind. It is
        // static, so this normally writes once per ring slot (the atlas view, or the filler) and then no-ops every
        // later frame.
        var glyphView = ((0 != m_glyphAtlasView) ? m_glyphAtlasView : fillerView);

        if (glyphView != m_boundGlyphAtlasViews[m_currentSlot]) {
            m_descriptorAllocator.WriteCombinedImageSampler(arrayElement: 0, binding: GlyphAtlasBindingIndex, descriptorSetHandle: viewsSet, deviceHandle: m_deviceHandle, imageViewHandle: glyphView, samplerHandle: m_screenSampler);
            m_boundGlyphAtlasViews[m_currentSlot] = glyphView;
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

    // Pack each frame's views (camera snapshot + region + render scale) into the 96-byte ViewportData rows the kernels
    // read — member-for-member from SdfFrame, no camera math (the snapshot already holds the basis + tan(fov/2) +
    // aspect). The render scale packs as its QUANTIZED numerator q (RenderScaleQ) so Stage 1, the tile passes, and
    // Stage 2 all derive the identical integer render extent.
    private void PackViewports(SdfFrame frame, uint viewportCount) {
        var floats = MemoryMarshal.Cast<byte, float>(span: m_viewportScratch.AsSpan());

        for (var index = 0; (index < (int)viewportCount); index++) {
            var view = frame.Views[index];
            var camera = view.Camera;
            var region = view.Region;
            var b = (index * 24);

            floats[(b + 0)] = camera.Position.X; floats[(b + 1)] = camera.Position.Y; floats[(b + 2)] = camera.Position.Z; floats[(b + 3)] = frame.Time;          // position.xyz, time
            floats[(b + 4)] = camera.Right.X; floats[(b + 5)] = camera.Right.Y; floats[(b + 6)] = camera.Right.Z; floats[(b + 7)] = camera.TanHalfFieldOfView;     // right.xyz, tan(fov/2)
            floats[(b + 8)] = camera.Up.X; floats[(b + 9)] = camera.Up.Y; floats[(b + 10)] = camera.Up.Z; floats[(b + 11)] = camera.AspectRatio;                   // up.xyz, aspect
            floats[(b + 12)] = camera.Forward.X; floats[(b + 13)] = camera.Forward.Y; floats[(b + 14)] = camera.Forward.Z; floats[(b + 15)] = DebugMode;           // forward.xyz, debug view mode
            floats[(b + 16)] = region.X; floats[(b + 17)] = region.Y; floats[(b + 18)] = region.Width; floats[(b + 19)] = region.Height;                           // region origin.xy, size.xy
            floats[(b + 20)] = RenderScaleQ(view: view, slot: index); floats[(b + 21)] = 0f; floats[(b + 22)] = 0f; floats[(b + 23)] = 0f;                         // renderScale q, spares
        }
    }

    // The quantized render-scale numerator q (1..255; 255 = native): one quantization, shared by the viewport row and
    // the composite push, so every kernel derives the same integer render extent. A child slot always renders native
    // (its source is another node's full-rect surface — Stage 1 never renders it, and Stage 2 must copy it 1:1).
    private byte RenderScaleQ(SdfViewSnapshot view, int slot) {
        if (IsChildSlot(slot: slot)) {
            return 255;
        }

        var scale = view.RenderScale;

        if (!(scale > 0f) || (scale >= 1f)) {
            return 255;
        }

        return (byte)Math.Clamp(value: (int)MathF.Round(x: (scale * 255f)), min: 1, max: 255);
    }

    // The per-view reconstruction blend quantized to one byte. Invalid/negative input degrades to the existing
    // bilinear path; values above one saturate at full clamped Catmull-Rom.
    private static byte UpscaleSharpnessQ(SdfViewSnapshot view) {
        var sharpness = view.UpscaleSharpness;

        if (!float.IsFinite(f: sharpness) || (sharpness <= 0f)) {
            return 0;
        }

        if (sharpness >= 1f) {
            return 255;
        }

        return (byte)Math.Clamp(value: (int)MathF.Round(x: (sharpness * 255f)), min: 0, max: 255);
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
        var count = Math.Min(val1: transforms.Count, val2: capacity);

        if (count == 0) {
            floats[0] = 0f; floats[1] = 0f; floats[2] = 0f; floats[3] = 0f;   // position.xyz, pad
            floats[4] = 0f; floats[5] = 0f; floats[6] = 0f; floats[7] = 1f;   // identity quaternion

            return;
        }

        for (var index = 0; (index < count); index++) {
            var transform = transforms[index];
            var b = (index * 8);

            // position.w encodes per-instance soft-shadow participation: 0 = casts (the default pad every prior frame
            // uploaded → byte-identical), 1 = shadow-suppressed (skipped by the soft-shadow march only). Read by
            // sdf-world.hlsli's sdfShadowParticipationActive skip; camera/AO marches ignore it.
            floats[(b + 0)] = transform.Position.X; floats[(b + 1)] = transform.Position.Y; floats[(b + 2)] = transform.Position.Z; floats[(b + 3)] = (transform.CastsSoftShadow ? 0f : 1f);
            floats[(b + 4)] = transform.Orientation.X; floats[(b + 5)] = transform.Orientation.Y; floats[(b + 6)] = transform.Orientation.Z; floats[(b + 7)] = transform.Orientation.W;
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

            floats[(b + 0)] = color.X; floats[(b + 1)] = color.Y; floats[(b + 2)] = color.Z; floats[(b + 3)] = ScreenLightIntensity;
        }

        var envBase = (MaxScreenSurfaces * 4);

        // The env entry's zw lanes carry the SLICE debug view's plane selector (axis + offset — see
        // SdfFrame.DebugSliceAxis); they were spare pads before, so a frame that never sets them uploads the same zeros.
        floats[(envBase + 0)] = frame.AmbientScale; floats[(envBase + 1)] = frame.SunScale; floats[(envBase + 2)] = frame.DebugSliceAxis; floats[(envBase + 3)] = frame.DebugSliceOffset;

        // The grid-lock overlay rows (grid-locking §4a): four float4 rows AFTER the env entry (env stays at
        // MaxScreenSurfaces, load-bearing as the shader's screen-count loop bound). Default 0 = no overlay, so a frame
        // that never sets the Grid* fields uploads the same zeros. KEEP IN SYNC with sdf-world.hlsli's SdfGridWorld..
        var gridWorldBase = ((MaxScreenSurfaces + 1) * 4);

        floats[(gridWorldBase + 0)] = frame.GridFlags; floats[(gridWorldBase + 1)] = frame.GridFloorY; floats[(gridWorldBase + 2)] = frame.GridWorldPitch.X; floats[(gridWorldBase + 3)] = frame.GridWorldPitch.Y;

        var gridObjOriginBase = ((MaxScreenSurfaces + 2) * 4);

        floats[(gridObjOriginBase + 0)] = frame.GridObjectOrigin.X; floats[(gridObjOriginBase + 1)] = frame.GridObjectOrigin.Y; floats[(gridObjOriginBase + 2)] = frame.GridObjectOrigin.Z; floats[(gridObjOriginBase + 3)] = frame.GridObjectPitch.X;

        var gridObjFrameBase = ((MaxScreenSurfaces + 3) * 4);

        floats[(gridObjFrameBase + 0)] = frame.GridObjectFrame.X; floats[(gridObjFrameBase + 1)] = frame.GridObjectFrame.Y; floats[(gridObjFrameBase + 2)] = frame.GridObjectFrame.Z; floats[(gridObjFrameBase + 3)] = frame.GridObjectFrame.W;

        // The .z lane is the analytic-normal A/B toggle (0 = the forward-mode dual normal, the default; 1 = the legacy
        // 4-tap finite-difference probe), read by sdf-world.hlsli's worldUseTapNormals. The .w lane is the soft-shadow
        // GRID-CULL toggle (0 = ON, the default grid-gathered shadow march; 1 = OFF, the flat all-instances reference),
        // read by worldShadowCullEnabled. Both were reserved before, so an unset frame uploads 0 = analytic normals +
        // cull ON. KEEP IN SYNC with SdfFrame.UseFiniteDifferenceNormals / SdfFrame.DisableShadowCull.
        var gridObjParamsBase = ((MaxScreenSurfaces + 4) * 4);

        floats[(gridObjParamsBase + 0)] = frame.GridObjectPitch.Y; floats[(gridObjParamsBase + 1)] = frame.GridObjectPatchRadius; floats[(gridObjParamsBase + 2)] = (frame.UseFiniteDifferenceNormals ? 1f : 0f); floats[(gridObjParamsBase + 3)] = (frame.DisableShadowCull ? 1f : 0f);

        // Engine-bench shader-feature levers: one reserved row after the grid rows. x = disable soft
        // shadows, y = disable AO, z = shadow-distance scale (0 = the full 1.0 reach — an unset frame uploads 0), w =
        // disable screen lights. All default 0, so a frame that never sets the Disable*/ShadowDistanceScale fields
        // uploads the same zeros = every feature ON at full reach. KEEP IN SYNC with sdf-world.hlsli's SdfBenchParams
        // decode (worldSoftShadowsDisabled/worldAoDisabled/worldShadowDistanceScale/worldScreenLightsDisabled).
        var benchParamsBase = ((MaxScreenSurfaces + 5) * 4);

        floats[(benchParamsBase + 0)] = (frame.DisableSoftShadows ? 1f : 0f); floats[(benchParamsBase + 1)] = (frame.DisableAmbientOcclusion ? 1f : 0f); floats[(benchParamsBase + 2)] = frame.ShadowDistanceScale; floats[(benchParamsBase + 3)] = (frame.DisableScreenLights ? 1f : 0f);

        // The shadow-proxy lever (PATH B): one reserved row AFTER the bench-params row (whose four lanes are full). x =
        // enable the shadow proxy (shadow rays skip Subtraction-family carve instances and march the pre-carve union
        // hull); y = use the camera-tile shadow mask instead of the per-pixel shadow-grid gather; z = use the bounded-cost
        // fast soft-shadow marcher; w = use the one-sample contact-AO approximation.
        // Both default 0, so a frame that never sets either lever uploads the same zeros = the full gathered occluder
        // set. KEEP IN SYNC with sdf-world.hlsli's SdfShadowProxyParams / worldShadowProxyEnabled /
        // worldUseCameraTileShadowMask / worldUseFastSoftShadowMarch / worldUseFastAmbientOcclusion.
        var shadowProxyBase = ((MaxScreenSurfaces + 6) * 4);

        floats[(shadowProxyBase + 0)] = (frame.EnableShadowProxy ? 1f : 0f); floats[(shadowProxyBase + 1)] = (frame.UseCameraTileShadowMask ? 1f : 0f); floats[(shadowProxyBase + 2)] = (frame.UseFastSoftShadowMarch ? 1f : 0f); floats[(shadowProxyBase + 3)] = (frame.UseFastAmbientOcclusion ? 1f : 0f);

        // The F1/F2 far-field lever row (perf plan Phase 5.1): one reserved row AFTER the shadow-proxy row. x = disable the
        // beam-published per-tile far bound (F1 A/B "off" side — the fine march ignores plane 3 and runs to MaxDistance
        // exactly as pre-F1); y = disable the F2 shadow light-side early exit (softShadow runs its full budget/reach); zw
        // reserved. Both levers default 0, so a frame that sets neither uploads zeros = both features ON (the shipped
        // behavior). KEEP IN SYNC with sdf-world.hlsli's SdfFarFieldParams / worldFarBoundDisabled / worldShadowFarExitDisabled.
        var farFieldBase = ((MaxScreenSurfaces + 7) * 4);

        floats[(farFieldBase + 0)] = (frame.DisableFarBound ? 1f : 0f); floats[(farFieldBase + 1)] = (frame.DisableShadowFarExit ? 1f : 0f); floats[(farFieldBase + 2)] = 0f; floats[(farFieldBase + 3)] = 0f;
    }

    // Stage 2's CompositeParams2 { uint2 imageExtent; uint viewportCount; uint tileGridPacked; float4 rects[5];
    // uint2 scaleQPacked; uint2 sharpnessQPacked; }: the LIVE regions drive the layout every frame. word[3] packs the
    // tile grid ((y << 16) | x); the final four words carry the byte-packed per-view controls.
    private void BuildCompositePush(SdfFrame frame) {
        var words = MemoryMarshal.Cast<byte, uint>(span: m_compositePush.AsSpan());

        words[0] = m_width; words[1] = m_height; words[2] = (uint)frame.Views.Count; words[3] = (m_tileGridY << 16) | m_tileGridX;

        var floats = MemoryMarshal.Cast<byte, float>(span: m_compositePush.AsSpan());

        for (var index = 0; (index < frame.Views.Count); index++) {
            var region = frame.Views[index].Region;
            var b = (4 + (index * 4));

            floats[(b + 0)] = region.X; floats[(b + 1)] = region.Y; floats[(b + 2)] = region.Width; floats[(b + 3)] = region.Height;
        }

        // scaleQPacked (after rects): view v's quantized render-scale numerator in byte lane (v % 4) of word (v / 4) —
        // the SAME RenderScaleQ the viewport row carries, so Stage 2's upsample derivation matches Stage 1's render.
        // Unpacked slots stay q = 255 (native) so a stale lane can never scale a live view.
        var qBase = (4 + (MaxViewports * 4));

        words[(qBase + 0)] = 0xFFFFFFFFu; words[(qBase + 1)] = 0xFFFFFFFFu;

        for (var index = 0; (index < frame.Views.Count); index++) {
            var word = (qBase + (index / 4));
            var shift = ((index % 4) * 8);

            words[word] = (words[word] & ~(0xFFu << shift)) | ((uint)RenderScaleQ(view: frame.Views[index], slot: index) << shift);
        }

        // sharpnessQPacked follows scaleQPacked with the same five-view byte-lane layout. Zero is bilinear and retains
        // the existing four-tap path; nonzero blends toward clamped Catmull-Rom. Unused lanes stay zero.
        var sharpnessBase = (qBase + 2);

        words[(sharpnessBase + 0)] = 0u; words[(sharpnessBase + 1)] = 0u;

        for (var index = 0; (index < frame.Views.Count); index++) {
            var word = (sharpnessBase + (index / 4));
            var shift = ((index % 4) * 8);

            words[word] |= ((uint)UpscaleSharpnessQ(view: frame.Views[index]) << shift);
        }
    }

    // beam → barrier → cull-args → barrier + indirect-args transition → views (INDIRECT) → barrier → composite
    // (INDIRECT), with the output handed off in its consumer layout.
    private void Record(uint viewportCount) {
        var recorder = m_gpu.ComputeRecorder;
        var commandBuffer = m_commandPools[m_currentSlot].CommandBufferHandle;
        // After the first frame the OUTPUT rests in its handoff layout: shader-readable when a same-device consumer
        // sampled it, or the cross-backend External layout when it was exported. The first frame starts undefined.
        // The non-export consumer set spans TWO stages — the presenter's fragment blit AND another engine's COMPUTE
        // sampler (a view engine's output bound as a screen source) — so the resting-stage scope names both; under
        // the frame ring the begin-of-frame re-transition below must order after whichever consumer read it last.
        var restingLayout = (m_exportMode ? GpuImageLayout.External : GpuImageLayout.ShaderReadOnly);
        var restingStage = (m_exportMode ? GpuComputeStage.ComputeShader : GpuComputeStage.FragmentShader | GpuComputeStage.ComputeShader);
        var outputOldLayout = (m_imageInitialized ? restingLayout : GpuImageLayout.Undefined);
        var outputSourceAccess = (m_imageInitialized ? GpuComputeAccess.ShaderRead : GpuComputeAccess.None);
        var outputSourceStage = (m_imageInitialized ? restingStage : GpuComputeStage.TopOfPipe);

        recorder.BeginCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);
        // The outer debug-marker group scoping this engine's whole recorded frame (see DebugLabel) — a GPU capture
        // shows the per-pass groups below nested inside it. No-op on a backend without debug labels; pixel-neutral.
        recorder.BeginDebugGroup(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, label: DebugLabel);

        // FRAME-RING cross-frame gate: the GPU-written device-local scratch (tile / instance-mask / indirect-args /
        // cull-bounds buffers, the per-view source textures) is SHARED across ring slots, so with FrameRingSize
        // frames in flight this frame's first write must order after the PREVIOUS frame's last read of that scratch —
        // an execution dependency on all prior compute (and the indirect-args fetch), queue-scoped like every Vulkan
        // barrier. This serializes GPU frames against each other (the natural order anyway — the ring overlaps CPU
        // production with GPU execution, not GPU frames); it replaces the host's per-frame whole-device drain.
        recorder.MemoryBarrier(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuComputeAccess.ShaderRead | GpuComputeAccess.ShaderWrite,
            destinationStageMask: GpuComputeStage.ComputeShader,
            deviceHandle: m_deviceHandle,
            sourceAccessMask: GpuComputeAccess.ShaderWrite | GpuComputeAccess.IndirectCommandRead,
            sourceStageMask: GpuComputeStage.ComputeShader | GpuComputeStage.DrawIndirect
        );

        // CARVE-BAKE: prepend this frame's background bake slices (carve-bake plan §3), BEFORE the frame-timing marks so
        // the render passes' per-pass budget excludes the background bake. Each baking slot advances one ≤ 256K-voxel
        // slice; when a slot's cursor reaches its total, it flips to Ready. A pool-write → pool-read barrier follows so
        // the beam/views marches see the just-written voxels this same frame (and the cross-frame barrier orders any
        // later frame's read after this frame's writes regardless).
        if (RecordBrickBakeSlices(commandBuffer: commandBuffer)) {
            recorder.MemoryBarrier(
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuComputeAccess.ShaderRead,
                destinationStageMask: GpuComputeStage.ComputeShader,
                deviceHandle: m_deviceHandle,
                sourceAccessMask: GpuComputeAccess.ShaderWrite,
                sourceStageMask: GpuComputeStage.ComputeShader
            );
        }

        // GPU timing: decide ONCE whether this frame is timed (available, and in live-armed mode also
        // GpuTimingControl.Shared.Armed), latch it for the submit paths, and lazily create the pools on the first armed
        // frame. Then this frame's rotating pool is reset and marked frame-start (top of pipe). The marks are
        // pixel-neutral, so the determinism/capture-hash parity gates are unaffected.
        m_frameTimingActive = (m_timingAvailable && (!m_liveArmedTiming || GpuTimingControl.Shared.Armed));

        if (m_frameTimingActive) {
            EnsureTimingPools();
        }

        var timingPool = (m_frameTimingActive ? m_timingPools![(int)(m_timingFrame % (ulong)TimingPoolCount)].PoolHandle : 0);

        if (0 != timingPool) {
            m_timingRecorder!.ResetTimestamps(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, firstQuery: 0, poolHandle: timingPool, queryCount: TimingCapacity);
            m_timingRecorder.WriteTimestamp(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, poolHandle: timingPool, queryIndex: 0, stageFlags: GpuTimingStage.TopOfPipe);
        }

        // CADENCE GATE (perf plan Phase 6.1): when this frame's inputs are byte-identical to the last RENDERED frame's
        // (DecideCadenceSkip proved it), SKIP the four render passes and fall straight through to the composite below —
        // which re-reads the RETAINED (single, ring-shared) views source textures + tile buffer the previous frame wrote
        // and re-composites them into the swapchain-bound output. Pixel-identical to a full re-render of these inputs;
        // the top-of-frame cross-frame barrier already orders this read after that previous frame's writes. Honest
        // timing: the skipped passes' closing marks are written back-to-back (queries 1..4), so each reports ~0 ms.
        if (!m_skipThisFrame) {
            // Instance-cull pass FIRST (mask-first): one invocation per (tile, viewport) — bins the program's instances
            // against each tile's cone into the per-tile mask (the uniform-grid walk, or the flat loop when the program
            // packs no grid). Its OWN kernel so its register footprint never taxes the cone march's occupancy.
            recorder.BeginDebugGroup(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, label: "mask");
            recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, pipelineHandle: m_instanceCullPipeline.Handle);
            recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: m_instanceCullSets[m_currentSlot], deviceHandle: m_deviceHandle, pipelineLayoutHandle: m_instanceCullPipeline.LayoutHandle);
            recorder.PushConstants(commandBufferHandle: commandBuffer, data: m_pushConstant, deviceHandle: m_deviceHandle, offset: 0, pipelineLayoutHandle: m_instanceCullPipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
            recorder.Dispatch(
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle,
                groupCountX: ((m_tileGridX + (WorkgroupEdge - 1)) / WorkgroupEdge),
                groupCountY: ((m_tileGridY + (WorkgroupEdge - 1)) / WorkgroupEdge),
                groupCountZ: viewportCount
            );
            recorder.EndDebugGroup(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);

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
            recorder.BeginDebugGroup(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, label: "beam");
            recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, pipelineHandle: m_beamPipeline.Handle);
            recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: m_beamSets[m_currentSlot], deviceHandle: m_deviceHandle, pipelineLayoutHandle: m_beamPipeline.LayoutHandle);
            recorder.PushConstants(commandBufferHandle: commandBuffer, data: m_pushConstant, deviceHandle: m_deviceHandle, offset: 0, pipelineLayoutHandle: m_beamPipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
            recorder.Dispatch(
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle,
                groupCountX: m_tileGridX,
                groupCountY: m_tileGridY,
                groupCountZ: viewportCount
            );
            recorder.EndDebugGroup(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);

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
            recorder.BeginDebugGroup(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, label: "cull-args");
            recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, pipelineHandle: m_cullArgsPipeline.Handle);
            recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: m_cullArgsSet, deviceHandle: m_deviceHandle, pipelineLayoutHandle: m_cullArgsPipeline.LayoutHandle);
            recorder.PushConstants(commandBufferHandle: commandBuffer, data: m_pushConstant, deviceHandle: m_deviceHandle, offset: 0, pipelineLayoutHandle: m_cullArgsPipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
            recorder.Dispatch(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, groupCountX: 1, groupCountY: 1, groupCountZ: 1);
            recorder.EndDebugGroup(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);

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

            WriteTimingMark(commandBuffer: commandBuffer, queryIndex: 3, timingPool: timingPool); // close: cull-args reduction

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
            // invocation by the bbox origin (binding 8). The pipeline is the variant UploadProgram selected for the LIVE
            // program (full ISA vs core-ops — the stripped cases are unreachable under core, so the field is the same;
            // see SdfViewsKernelVariant); the per-slot views set binds against either (identically defined layouts, same
            // bindings array).
            var viewsPipeline = (m_useCoreViews ? m_viewsCorePipeline : m_viewsPipeline);

            recorder.BeginDebugGroup(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, label: "views");
            recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, pipelineHandle: viewsPipeline.Handle);
            recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: m_viewsSets[m_currentSlot], deviceHandle: m_deviceHandle, pipelineLayoutHandle: viewsPipeline.LayoutHandle);
            recorder.PushConstants(commandBufferHandle: commandBuffer, data: m_pushConstant, deviceHandle: m_deviceHandle, offset: 0, pipelineLayoutHandle: viewsPipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
            recorder.DispatchIndirect(
                argumentBufferHandle: m_viewsArgsBuffer.BufferHandle,
                argumentBufferOffset: 0,
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle
            );
            recorder.EndDebugGroup(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);

            WriteTimingMark(commandBuffer: commandBuffer, queryIndex: 4, timingPool: timingPool); // close: Stage 1 views

            // Make Stage 1's source writes visible to Stage 2's reads.
            recorder.MemoryBarrier(
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuComputeAccess.ShaderRead,
                destinationStageMask: GpuComputeStage.ComputeShader,
                deviceHandle: m_deviceHandle,
                sourceAccessMask: GpuComputeAccess.ShaderWrite,
                sourceStageMask: GpuComputeStage.ComputeShader
            );
        } else {
            // SKIPPED FRAME: no render passes ran, so close their four timing marks (queries 1..4) back-to-back — each
            // reports ~0 ms, the honest cost of a skipped pass — and fall through to the composite. The retained tile
            // buffer + source textures (single, ring-shared, left in General by the previous rendered frame) are ordered
            // for this frame's composite reads by the top-of-frame cross-frame barrier, so no extra barrier is needed.
            WriteTimingMark(commandBuffer: commandBuffer, queryIndex: 1, timingPool: timingPool); // close: instance-mask cull (skipped)
            WriteTimingMark(commandBuffer: commandBuffer, queryIndex: 2, timingPool: timingPool); // close: beam prepass (skipped)
            WriteTimingMark(commandBuffer: commandBuffer, queryIndex: 3, timingPool: timingPool); // close: cull-args reduction (skipped)
            WriteTimingMark(commandBuffer: commandBuffer, queryIndex: 4, timingPool: timingPool); // close: Stage 1 views (skipped)
        }

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
        recorder.BeginDebugGroup(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, label: "composite");
        recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, pipelineHandle: m_compositePipeline.Handle);
        recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: m_compositeSets[m_currentSlot], deviceHandle: m_deviceHandle, pipelineLayoutHandle: m_compositePipeline.LayoutHandle);
        recorder.PushConstants(commandBufferHandle: commandBuffer, data: m_compositePush, deviceHandle: m_deviceHandle, offset: 0, pipelineLayoutHandle: m_compositePipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
        recorder.DispatchIndirect(
            argumentBufferHandle: m_compositeArgsBuffer.BufferHandle,
            argumentBufferOffset: 0,
            commandBufferHandle: commandBuffer,
            deviceHandle: m_deviceHandle
        );
        recorder.EndDebugGroup(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);

        WriteTimingMark(commandBuffer: commandBuffer, queryIndex: 5, timingPool: timingPool); // close: Stage 2 composite

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

        recorder.EndDebugGroup(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle); // close the outer per-engine group
        recorder.EndCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);

        m_imageInitialized = true;
    }

    // Records this frame's carve-bake slices: for each Baking brick slot, one voxel slice of ≤ MaxBrickBakeVoxelsPerSlice,
    // advancing the slot's CPU cursor and flipping it to Ready once its whole brick is written. Returns whether ANY
    // slice was recorded (so Record inserts the pool-visibility barrier). A no-op when the pool is disabled or nothing
    // is baking — the bare room never pays it. Each slice is a plain direct dispatch of the standalone baker pipeline;
    // the bake writes are made visible to the render marches by the barrier Record adds after this returns true.
    private bool RecordBrickBakeSlices(nint commandBuffer) {
        if (m_brickBakePipeline is null) {
            return false;
        }

        var recorder = m_gpu.ComputeRecorder;
        var push = MemoryMarshal.Cast<byte, uint>(span: m_brickBakePush.AsSpan());
        var recorded = false;

        for (var slot = 0; (slot < SdfBrickPoolLayout.MaxBricks); slot++) {
            if (m_brickStates[slot] != BrickBakeState.Baking) {
                continue;
            }

            var remaining = (m_brickTotalVoxels[slot] - m_brickVoxelCursor[slot]);

            if (remaining <= 0) {
                m_brickStates[slot] = BrickBakeState.Ready;

                continue;
            }

            var sliceCount = Math.Min(val1: remaining, val2: MaxBrickBakeVoxelsPerSlice);

            if (!recorded) {
                recorder.BeginDebugGroup(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, label: "brick-bake");
            }

            push[0] = (uint)m_brickVoxelCursor[slot]; push[1] = (uint)sliceCount; push[2] = 0u; push[3] = 0u;

            recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, pipelineHandle: m_brickBakePipeline.Handle);
            recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: m_brickBakeSets[slot], deviceHandle: m_deviceHandle, pipelineLayoutHandle: m_brickBakePipeline.LayoutHandle);
            recorder.PushConstants(commandBufferHandle: commandBuffer, data: m_brickBakePush, deviceHandle: m_deviceHandle, offset: 0, pipelineLayoutHandle: m_brickBakePipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
            recorder.Dispatch(
                commandBufferHandle: commandBuffer,
                deviceHandle: m_deviceHandle,
                groupCountX: (((uint)sliceCount + (BrickBakeWorkgroupSize - 1)) / BrickBakeWorkgroupSize),
                groupCountY: 1,
                groupCountZ: 1
            );

            m_brickVoxelCursor[slot] += sliceCount;

            if (m_brickVoxelCursor[slot] >= m_brickTotalVoxels[slot]) {
                m_brickStates[slot] = BrickBakeState.Ready;
            }

            recorded = true;
        }

        if (recorded) {
            recorder.EndDebugGroup(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);
        }

        return recorded;
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
