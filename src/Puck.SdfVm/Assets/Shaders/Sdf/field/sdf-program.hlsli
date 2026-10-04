// The program word stream, its instance directory and frame instance grid, the group masks, and the variant strip tiers every tape interpreter reads.
#ifndef FIELD_SDF_PROGRAM_HLSLI
#define FIELD_SDF_PROGRAM_HLSLI
static uint sdfWorkShapes = 0u;
static uint sdfWorkGradients = 0u;
// Program word stream (sdfWords, each element one uint4 = 16 bytes), read-only: the program is never written. Layout:
//   words[0]              = (instructionCount, materialCount, dataOffset, materialOffset)
//   words[1 .. 1+N)       = instruction headers (op, shapeType, blendOp, materialId)
//   words[dataOffset ..]  = instruction data, 2 uint4 per instruction (data0, data1 as float bits)
//   words[matOffset ..] = materials, 20 uint4 each; see SdfProgram.Materials.cs and sdfMaterialLoad.
//   words[matOffset + 20*materialCount ..] = HOST-BAKED bounding-sphere table (SdfProgram.PackBounds), 2 uint4 per
//                           instruction: b0 = center/offset.xyz + radius (float bits), b1 = (mode, dynamicSlot,
//                           skipTo, 0) — map()'s exact Union early-out reads it; mode SDF_BOUND_NONE evaluates fully.
//   [.. segment directory ..] then the INSTANCE directory (SdfProgram.PackInstances, world render path only): one
//                           (instanceCount, partProgramOffset, shadingFlags, tapeTokens) header uint4, then 2 uint4 per instance — i0 = bound
//                           center/offset.xyz + radius (float bits), i1 = (mode, dynamicSlot, segmentFirst,
//                           segmentEnd) — segmentFirst/segmentEnd index the SEGMENT directory (not raw
//                           instructions): every segment in that range is owned by exactly that instance, so
//                           mapCore's merge walks the range whole when the instance's mask bit is set.
//   [.. + 1 + 2*instanceCount ..] then the WORLD-SEGMENT list (SdfProgram.PackWorldSegments, world render path
//                           only): one (worldSegmentCount, 0, 0, 0) header uint4, then one uint4 (only .x used) per
//                           WORLD segment (owned by no instance), value = its segment-directory index, ascending.
//                           mapCore merges this list with the VISIBLE instances' segment ranges — ascending segment
//                           index, so blend-op order is the flat stream's — and a call costs O(world segments +
//                           visible instances' segments), never O(all segments).
//   [.. after the instance grid ..] then the RIGID-LEAF execution plan: one directory uint4 per segment followed by
//                           three uint4 per compiled leaf (pose, quaternion, tight sphere). Segment-header .z is its absolute uint4 offset. The host
//                           collapses Reset/Translate/Rotate/TransformDynamic/Shape chains into direct local poses; BOTH mapCore (scalar) and
//                           mapGradCore (analytic-gradient dual) bypass the generic per-op switch for those segments — parallel rigid walks, KEEP
//                           IN SYNC — while the authored instruction range stays intact for every non-rigid segment and the CPU SdfFieldEvaluator.

// The per-tile instance mask is a DERIVED ceil(instanceCount/32) uints (sdfInstanceMaskWordCount), so the instance
// ceiling SDF_MAX_INSTANCES caps it at SDF_MAX_INSTANCES/32 = 2048 words.
// Sentinel instance-mask BASE meaning "every instance visible" (sdfInstanceMaskWord then reads no buffer and
// returns all-ones words). Every map() CONSUMER that cannot reach the beam-computed per-tile mask (the debug frag
// view, the beam prepass's own cone march) passes this, so an instanced program still
// renders its complete picture through them; only sdf-world-views.comp narrows it to a real per-tile mask base.
#define SDF_INSTANCE_MASK_ALL 0xFFFFFFFFu

// SDF_INSTANCE_SHADOW_TRANSPARENT_BIT, the high bit of the instance meta's segmentEnd lane (i1.w), marks an instance
// whose compose only REMOVES material (a Subtraction-family carve), so omitting it from a shadow march can only make
// the field MORE solid (darker / never light-leak). Read ONLY by surface/sdf-shadow-gather.hlsli's sdfShadowGather under the
// sdf.shadow-proxy lever (sdfInstanceShadowTransparent); mapCore's segment-range enumeration MASKS it off
// (SDF_INSTANCE_SEGMENT_END_MASK) so segmentEnd stays the true directory range and every rendered pixel is
// byte-identical whether the bit is set or not.

// The per-tile instance mask sdf-instance-cull.comp wrote (world render path), read through sdfInstanceMasks: a flat
// uint buffer, passGroup.instanceMaskWordCount (the host-written live program width) elements per (viewport, tile)
// entry, same (viewport, tile) indexing as the cull buffer.

// === Shadow-ray instance cull (the world LIT path only) ==============================================================
// A shadow-ray instance mask over the light ray's grid neighborhood, built by surface/sdf-shadow-gather.hlsli's
// sdfShadowGatherGroup (the grid slab walk along the sun ray) and consumed by mapMasked EXACTLY like the device per-tile
// mask — so a culled soft-shadow march is BIT-IDENTICAL to the flat all-instances march by the same exact-cull contract
// (an omitted instance's bound excludes every on-axis shadow sample, so its compose returns the accumulator to the
// bit). The Stage 1 shared mask addresses the complete instance ceiling, including reserved/parked slots: total
// capacity must never silently select the camera-tile approximation for an exact shadow request.
// sdfShadowMaskActive gates sdfInstanceMaskWord onto this
// array for ONE softShadowVisibility call. Exact AO independently selects the complete live-instance mask below;
// the primary march, normals, and coverage keep their camera masks. Guarded on SDF_SCREEN_SOURCES: only the world-views kernel
// shades (the beam/cull/rt kernels never see it).
#ifdef SDF_SCREEN_SOURCES
// GROUPSHARED under SDF_GROUP_SHADOW_GATHER (the Stage 1 kernels): the per-tile gather (surface/sdf-shadow-gather.hlsli's
// sdfShadowGatherGroup) fills ONE mask per 8x8 workgroup. The full 2048-word mask costs 8 KiB per group, not per lane.
// Other kernels retain the small inactive per-thread array; nothing in them builds a mask.
#ifdef SDF_GROUP_SHADOW_GATHER
#define SDF_SHADOW_MASK_WORDS ((SDF_MAX_INSTANCES + 31u) / 32u)
#define SDF_GROUP_SHADOW_LANES 64u // the Stage 1 workgroup: [numthreads(8, 8, 1)]
groupshared uint sdfShadowMaskWords[SDF_SHADOW_MASK_WORDS];
groupshared uint sdfAmbientMaskWords[SDF_SHADOW_MASK_WORDS];
#else
#define SDF_SHADOW_MASK_WORDS 32u
static uint sdfShadowMaskWords[SDF_SHADOW_MASK_WORDS];
static uint sdfAmbientMaskWords[SDF_SHADOW_MASK_WORDS];
#endif
static bool sdfShadowMaskActive = false;
static bool sdfAmbientMaskActive = false;
#endif

#ifdef SDF_DYNAMIC_TRANSFORMS
// Per-instance soft-shadow participation gate (mirrors sdfShadowMaskActive's static-flag pattern). shade/sdf-light-stage.hlsli
// flips it true for exactly the lifetime of ONE softShadowVisibility call, so sdfNextVisibleInstanceRange SKIPS any dynamic
// instance whose packed position.w > 0.5 (host encoding: 0 = casts, 1 = shadow-suppressed — see PackDynamicTransforms).
// Indirect queries and their conservative light camera use their own instance policy. Ordinary camera/AO/coverage
// enumerations leave it false, so their participation is unchanged.
static bool sdfShadowParticipationActive = false;
#endif

// Indirect queries use their own whole-instance policy; direct shadow suppression does not override it.
static bool sdfIndirectParticipationActive = false;
uint sdfIndirectPolicy(uint policy, bool isDynamic) {
    if (policy != SDF_INDIRECT_PARTICIPATION_DEFAULT) { return policy; }
    if (!isDynamic) { return SDF_INDIRECT_PARTICIPATION_CAST; }
    return passGroup.indirectBodies != SDF_INDIRECT_PARTICIPATION_DEFAULT ? passGroup.indirectBodies :
        passGroup.indirectTier == SDF_INDIRECT_TIER_HIGH ? SDF_INDIRECT_PARTICIPATION_CAST : SDF_INDIRECT_PARTICIPATION_RECEIVE;
}
uint sdfInstanceIndirectPolicy(uint4 meta) {
    return sdfIndirectPolicy((meta.w & SDF_INSTANCE_INDIRECT_MASK) >> SDF_INSTANCE_INDIRECT_SHIFT, meta.x == SDF_BOUND_DYNAMIC);
}

// The per-tile mask width in uints for a program: ceil(instanceCount/32), never below 1 (a zero-instance program
// keeps one all-zero word so the mask buffer indexing stays uniform). Used ONLY for the reader's inner word
// iteration — buffer INDEXING (entry width and tile base) comes from the host-pushed
// passGroup.instanceMaskWordCount (worldInstanceMaskBase in frame/sdf-frame.hlsli). KEEP IN SYNC with
// SdfProgram.InstanceMaskWordCount — the host derives the pushed value and sizes the mask buffer with the
// identical formula.
uint sdfInstanceMaskWordCount(uint instanceCount) {
    return max(1u, ((instanceCount + 31u) >> 5u));
}
// One word of the caller's per-tile instance mask, already masked to the bits that can name a REAL instance (the
// all-ones sentinel — and any stale buffer tail — must never enumerate an instance index >= instanceCount): a buffer
// read at instanceMaskBase + wordIndex for a real mask, or all-ones for the SDF_INSTANCE_MASK_ALL sentinel (and for
// every kernel compiled without SDF_INSTANCE_MASKS, where no mask buffer is bound at all). wordIndex <
// sdfInstanceMaskWordCount(instanceCount) by contract, so `remaining` is always >= 1.
uint sdfInstanceMaskWord(uint instanceMaskBase, uint wordIndex, uint instanceCount) {
    uint word = 0xFFFFFFFFu;

#ifdef SDF_SCREEN_SOURCES
    // Secondary-lighting masks override the device camera mask for their own field walks. Stage 1 addresses the
    // complete instance ceiling; the guard also keeps inactive arrays safe in other kernel configurations.
    if (sdfAmbientMaskActive) {
        word = ((wordIndex < SDF_SHADOW_MASK_WORDS) ? sdfAmbientMaskWords[wordIndex] : 0u);
    } else if (sdfShadowMaskActive) {
        word = ((wordIndex < SDF_SHADOW_MASK_WORDS) ? sdfShadowMaskWords[wordIndex] : 0u);
    } else
#endif
    {
#ifdef SDF_INSTANCE_MASKS
        if (instanceMaskBase != SDF_INSTANCE_MASK_ALL) {
            word = sdfInstanceMasks[instanceMaskBase + wordIndex];
        }
#endif
    }

    uint remaining = (instanceCount - (wordIndex << 5u));

    return (word & ((remaining >= 32u) ? 0xFFFFFFFFu : ((1u << remaining) - 1u)));
}

// A real camera-tile mask stores one summary bit per primary word after its primary run. The all-visible sentinel and
// the workgroup lighting masks have no device summary and retain the short linear walk.
bool sdfInstanceMaskHasSummary(uint instanceMaskBase) {
    bool hasSummary = false;

#ifdef SDF_INSTANCE_MASKS
    hasSummary = (instanceMaskBase != SDF_INSTANCE_MASK_ALL);
#ifdef SDF_SCREEN_SOURCES
    hasSummary = (hasSummary && !sdfShadowMaskActive && !sdfAmbientMaskActive);
#endif
#endif

    return hasSummary;
}

// The SEGMENT directory's element offset in sdfWords: header -> materials -> shape-bound table -> segment directory.
// KEEP IN SYNC with SdfProgram's offset math.
uint sdfSegmentDirectoryOffset() {
    uint4 header = sdfWords[0];

    return ((SDF_PROGRAM_MATERIAL_OFFSET(header) + (SDF_MATERIAL_VECTORS_PER_ENTRY * SDF_PROGRAM_MATERIAL_COUNT(header))) + (SDF_BOUND_RECORD_VECTORS * SDF_PROGRAM_INSTRUCTION_COUNT(header)));
}
// The INSTANCE directory's element offset, given a caller that ALREADY resolved the segment directory
// (sdfLoadProgramLayout has both in hand). DXC's SPIR-V backend runs no GVN over StructuredBuffer loads, so
// re-deriving them costs a real reload of sdfWords[0] on Vulkan — the reason sdfLoadProgramLayout exists: mapCore/
// mapGradCore used to re-run this whole chain on EVERY call, and marchers call them once per march step.
uint sdfInstanceDirectoryOffsetFrom(uint segmentOffset, uint segmentCount) {
    return (segmentOffset + SDF_DIRECTORY_HEADER_VECTORS + (SDF_BOUND_RECORD_VECTORS * segmentCount));
}
// The INSTANCE directory's element offset in sdfWords — the ONE resolution of the packed offset chain for callers that
// hold nothing yet. Every consumer that touches the directory locates it through this or its `From` sibling.
uint sdfInstanceDirectoryOffset() {
    uint segmentOffset = sdfSegmentDirectoryOffset();

    return sdfInstanceDirectoryOffsetFrom(segmentOffset, SDF_SEGMENT_COUNT(sdfWords[segmentOffset]));
}
// The per-PROGRAM Lipschitz STEP SCALE (1/L in (0, 1]), baked HOST-SIDE into the segment-directory header's otherwise-
// free .y lane (SdfProgram.AnalyzeLipschitz). mapCore multiplies EVERY returned distance by it, so a consumer that
// compares that distance against a WORLD-space quantity (a penumbra ratio, a footprint threshold) rather than taking a
// STEP with it must divide the clamp back out. == 1.0 exactly for an isometric, warp-free program (x * 1.0f == x to the
// bit for every finite x), so those scenes stay byte-identical. The `> 0` guard keeps a pre-writer all-zero stream
// rendering as before.
float sdfStepScale() {
    float stepScale = asfloat(SDF_SEGMENT_STEP_SCALE(sdfWords[sdfSegmentDirectoryOffset()]));

    return ((stepScale > 0.0) ? stepScale : 1.0);
}
// The element offset of instance `index`'s directory entry (i0 = bound, i1 = meta at +1) within the directory at
// `instanceOffset` — the ONE statement of the 2-uint4-per-instance entry stride.
uint sdfInstanceEntryOffset(uint instanceOffset, uint index) {
    return (instanceOffset + SDF_DIRECTORY_HEADER_VECTORS + (SDF_BOUND_RECORD_VECTORS * index));
}
// The packed instance count (the directory's header lane).
uint sdfInstanceCount() {
    return SDF_INSTANCE_COUNT(sdfWords[sdfInstanceDirectoryOffset()]);
}

// The ceiling-clamped instance count. The mask-buffer indexing contract itself (entry width, tile base) lives in
// frame/sdf-frame.hlsli's worldInstanceMaskBase: both world kernels resolve it from the host-pushed
// passGroup.instanceMaskWordCount (the beam prepass WRITES entry `tileIndex`'s words, Stage 1 hands mapCore the SAME
// entry's base).
uint sdfInstanceCountClamped() {
    return min(sdfInstanceCount(), SDF_MAX_INSTANCES);
}

// The world-space UNIFORM-GRID instance cull (world render path, the beam prepass ONLY): a uint-granular block appended
// after the world-segment list, so mapCore — which stops at that list — never reads it and every rendered pixel is
// unchanged by its presence. The beam walks it instead of testing every instance in every tile, so its cost tracks the
// instances NEAR a tile's cone. Puck.SignedDistance.SdfInstanceGrid packs it: the header layout,
// SDF_GRID_HEADER_WORDS, and SDF_GRID_MAX_DIM.
#define SDF_GRID_SLAB_CELLS 2.0  // cone-march slab length in cell edges (fewer iterations + fewer slab-boundary re-tests than 1)
// The cone-march slab budget. The walk clamps to the ray∩grid interval (at most sqrt(3)*SDF_GRID_MAX_DIM ≈ 111 cells,
// ~56 slabs at SDF_GRID_SLAB_CELLS = 2) plus the query inflation's few extra slabs, so 128 comfortably covers every
// legal walk — and the LAST budget slab force-covers the remaining interval whole (see collectInstanceGridMask), so
// even a pathological clip can only get more conservative, never truncate.
#define SDF_GRID_MAX_SLABS 128u

// One uint of the program word stream. The stream is a StructuredBuffer<uint4>; every table before the grid is
// uint4-granular, but the grid block is uint-granular, so it reads through this component index.
uint sdfWordAt(uint wordIndex) {
    return sdfWords[wordIndex >> 2u][wordIndex & 3u];
}

// Ring-local instance grid rebuilt from this frame's dynamic bound centers. Only the instance-cull and Stage-1
// kernels opt in: the former builds camera-tile masks and the latter builds soft-shadow masks from the same table.
// Keeping it separate from sdfWords lets animated instances move without rewriting the shared immutable program.
// The grid block's base WORD offset (uint-granular), given the instance directory offset and the UNCLAMPED packed
// instance count the caller already holds (mapCore and the beam both resolve them). The block sits one uint4 (the
// world-segment header) plus the world-segment entries past the instance directory's own span.
uint sdfGridBaseWord(uint instanceOffset, uint instanceCount) {
    uint worldSegmentOffset = (instanceOffset + SDF_DIRECTORY_HEADER_VECTORS + (SDF_BOUND_RECORD_VECTORS * instanceCount)); // uint4 index of the world-segment header
    uint worldSegmentCount = SDF_WORLD_SEGMENT_COUNT(sdfWords[worldSegmentOffset]);
    uint gridBaseVector = (worldSegmentOffset + SDF_DIRECTORY_HEADER_VECTORS + worldSegmentCount);    // uint4 index of the grid block

    return (gridBaseVector << 2u); // the grid block is uint-granular from here
}

// The decoded grid header (see SdfInstanceGrid for the packed layout). All array offsets are grid-block-relative uints
// (add `baseWord`). A DISABLED grid has enabled == false — the beam then flat-loops every instance.
struct SdfInstanceGridHeader {
    uint baseWord;      // absolute uint offset of the block
    bool enabled;
    uint3 dims;
    float3 origin;      // grid-AABB min (world)
    float invCellSize;  // host-baked 1/cellSize (the shader never divides by the cell edge)
    float cellSize;     // world cell edge (the slab-march step unit)
    float footprintPad; // LOAD-BEARING query margin: max binned bound radius + float-safety epsilon. Instances are
                        // binned by CENTER (one cell each), so a query that omits this pad misses any bound whose
                        // center sits in a neighboring cell — a hole-in-the-world bug, not slop.
    uint cellStartWord; // block-relative uint offset of cellStart[]
    uint entryWord;     // block-relative uint offset of the cell entries
    uint alwaysWord;    // block-relative uint offset of the always-tested list
    uint alwaysCount;
    uint cellCount;     // dims.x * dims.y * dims.z
};

SdfInstanceGridHeader sdfLoadInstanceGridHeader(uint instanceOffset, uint instanceCount) {
#ifdef SDF_FRAME_INSTANCE_GRID
    uint base = 0u;
#else
    uint base = sdfGridBaseWord(instanceOffset, instanceCount);
#endif

#ifdef SDF_FRAME_INSTANCE_GRID
#define SDF_GRID_HEADER_WORD(relativeWord) sdfFrameInstanceGrid[(relativeWord)]
#else
#define SDF_GRID_HEADER_WORD(relativeWord) sdfWordAt(base + (relativeWord))
#endif

    SdfInstanceGridHeader grid;
    grid.baseWord = base;
    grid.enabled = (SDF_GRID_HEADER_WORD(0u) != 0u);
    grid.dims = uint3(SDF_GRID_HEADER_WORD(1u), SDF_GRID_HEADER_WORD(2u), SDF_GRID_HEADER_WORD(3u));
    grid.origin = float3(asfloat(SDF_GRID_HEADER_WORD(4u)), asfloat(SDF_GRID_HEADER_WORD(5u)), asfloat(SDF_GRID_HEADER_WORD(6u)));
    grid.invCellSize = asfloat(SDF_GRID_HEADER_WORD(7u));
    grid.cellSize = asfloat(SDF_GRID_HEADER_WORD(8u));
    grid.footprintPad = asfloat(SDF_GRID_HEADER_WORD(9u));
    grid.cellStartWord = SDF_GRID_HEADER_WORD(10u);
    grid.entryWord = SDF_GRID_HEADER_WORD(11u);
    grid.alwaysWord = SDF_GRID_HEADER_WORD(12u);
    grid.alwaysCount = SDF_GRID_HEADER_WORD(13u);
    grid.cellCount = SDF_GRID_HEADER_WORD(14u);

#undef SDF_GRID_HEADER_WORD

    return grid;
}

uint sdfGridWordAt(SdfInstanceGridHeader grid, uint relativeWord) {
#ifdef SDF_FRAME_INSTANCE_GRID
    return sdfFrameInstanceGrid[relativeWord];
#else
    return sdfWordAt(grid.baseWord + relativeWord);
#endif
}

// Per-frame dynamic entity transforms (the world render path only). Each moving entity (player/enemy/carried screen)
// owns a slot of THREE rows: element 3*slot is its world position (xyz) + soft-shadow participation (w),
// 3*slot+1 its orientation quaternion (xyzw), 3*slot+2 its Lanes carrier (components 0 through 3 —
// Puck.SignedDistance.DynamicTransform.Lanes). The SDF_OP_TRANSFORM_DYNAMIC opcode reads the rigid transform AND the
// lanes from here by slot index, so an entity moves and its anonymous state updates by writing this small
// buffer instead of re-uploading the static program — the same way the camera moves via the per-frame viewport
// table. The kernels read it through sdfDynamicTransforms.

// The single font atlas the SDF_SHAPE_GLYPH primitive samples as a DISTANCE-level field (world-render path ONLY;
// SetGlyphAtlas uploads it once), read through sdfGlyphAtlas and the views set's nearest sampler. Sampled with
// EXPLICIT LOD only (SampleLevel): implicit-derivative filtering is undefined inside the march's non-uniform control
// flow, and manual bilinear (sdfGlyphSampleField) reads the true single-channel distance from ALPHA, so a nearest
// sampler is all it needs.

// The persistent brick pool the SDF_SHAPE_SAMPLED_REGION primitive samples through sdfBrickPool: one float per voxel
// (f32), a flat device-local buffer the bake kernel writes and every brick instance indexes at its host-baked
// brickWordOffset (data1.z). Only the kernels that DEFINE SDF_SAMPLED_REGIONS sample it (the world-views kernel and its
// variants, and the beam); every other kernel compiles the conservative union-hull fallback instead.

// --- instruction lanes ---
// SDF_SHAPE_DETAIL_FLAG (Puck.SignedDistance.SdfInstruction.Detail) marks a SHADING-ONLY shape: skipped by
// mapCore/mapGradCore's default (march) mode and included only under sdfDetailShadingActive (the hit-only shade
// re-evaluation in shade/sdf-light-stage.hlsli). SDF_SHAPE_NO_SECONDARY_FLAG (SdfInstruction.Secondary == false) marks
// a SECONDARY-EXCLUDED shape: unlike a Detail shape it marches for the camera/beam/fine march and the hit-only shade
// re-evaluations like any ordinary shape, and drops out ONLY under sdfSecondaryMarchActive, the soft-shadow and
// ambient-occlusion field walks in shade/sdf-light-stage.hlsli and surface/sdf-surface.hlsli (eyelids and other small parts still shade and collide, they just
// cast no shadow and cost no AO tap).
// SDF_OP_SYMMETRY_PLANE reproduces the axis-aligned folds with an axis normal.
// Scoped field accumulator (SdfOp.PushField/PopField). PUSH saves the immediate parent and reseeds a fresh field;
// POP composes only into that parent. Scalar/dual walks, deferred derivatives and tape certificates use the same
// bounded SDF_MAX_FIELD_SCOPE_DEPTH stack; the program validator guarantees balanced, single-owner scopes.
// Gaussian push: Data0=center/push.x, Data1=radii/push.y, header.y=push.z bits.
// Per-shape lane-driven erosion (SdfOp.LaneErode): ordered immediately before the SdfOp.ShapeBlend it targets.
// SDF_CORE_OPS — the CORE-OPS compiled variant of the tape interpreters (defined by sdf-world-views-core.comp.hlsl,
// the second compiled flavor of the Stage 1 views kernel; every other kernel compiles the FULL ISA). Compiles out every
// EXOTIC op case — everything beyond Reset/Translate/Rotate/Scale/TransformDynamic/Shape — and the exotic shape bodies,
// in mapCore, mapGradCore, evaluateShape, and evaluateShapeGradient. The win is REGISTER PRESSURE, not instruction
// count: the full interpreter's live state across the exotic cases holds Stage 1 at ~38% CS-warp occupancy (~72% of the
// register file allocated); stripping the cases lets more warps reside and hides tape-walk latency. The beam
// deliberately stays on the full interpreter because the stripped variant increased cone-march cost. Selection is
// per-program at UploadProgram time —
// a pure function of the instruction stream (SdfWorldTables picks the core pipeline only when no instruction touches a
// stripped op/shape), so a stripped case is provably unreachable whenever this variant runs. KEEP the strip set IN SYNC
// with SdfViewsKernelVariants.Select (Puck.SdfVm/SdfViewsKernelVariant.cs) — a case guarded here must make Select
// answer Full, or the core variant silently no-ops the op.
// The two-tier strip ladder: SDF_CORE_OPS strips EVERYTHING exotic (folds, scopes, warps, exotic shapes);
// SDF_FOLD_OPS (sdf-world-views-folds.comp) keeps the fold/scope/simple-exotic tier and strips only the HEAVY
// warp/noise family — the register-hungry cases whose live state holds the full interpreter at low occupancy.
// KEEP both strip sets IN SYNC with SdfViewsKernelVariants.Select: a case stripped under a macro must send
// Select to a fuller variant.
#ifdef SDF_CORE_OPS
#define SDF_STRIP_ALL_EXOTIC
#define SDF_STRIP_HEAVY
#endif
#ifdef SDF_FOLD_OPS
#define SDF_STRIP_HEAVY
#endif
// SDF_OP_CELL_JITTER's Blend lane (instructionHeader.z) is an SDF_NOISE_* flavor: how the per-cell POSITION offset is
// distributed. It reshapes ONLY r0 — tumble and material variant are unaffected.
// SDF_OP_REPEAT_POLAR's Shape lane (instructionHeader.y) is an SDF_AXIS_* rotation axis: the angular fold acts in
// the plane PERPENDICULAR to it (the axial coordinate is untouched).
// SDF_OP_WALLPAPER_FOLD's group is an SDF_WPG_* wallpaper group in IUC order, and its plane an SDF_PLANE_* pair.

// === Shared numeric constants ========================================================================================
// Written at full double precision: each rounds to the SAME float32 the shorter literal did, so naming them is
// bytecode-identical while the digits document the exact quantity.
#define SDF_SQRT3     1.7320508075688772   // sqrt(3)
#define SDF_SQRT_HALF 0.7071067811865476   // sqrt(1/2) — the 45-degree chamfer bevel plane's normalization
#define SDF_PI        3.141592653589793
#define SDF_TAU       6.283185307179586    // 2*pi

// The "nothing nearer yet" sentinel every accumulator and every unknown shape id starts at. It is deliberately far
// beyond any authored far distance (render.farDistance is capped at 8192 world units by the world validator) so it
// always loses a min() against real geometry, yet small enough that `a + (b - a)` still resolves; see
// blendSmoothUnion, which must NOT be handed this value through a saturating lerp.
#define SDF_FAR_DISTANCE 1.0e9

// Degenerate-input floors. The Scale / Repeat / RepeatLimited floors are HOST-BAKED (SdfProgramBuilder) — this
// is the one the shader still applies itself.
#define SDF_SMOOTH_RADIUS_MIN  0.0001   // the smooth blends' radius floor (the CHAMFER blends clamp against 0.0)

// Clamps length(p) away from 0 in the log-spherical fold so log() never sees -inf at the Droste center (the origin is
// a measure-zero singularity, kept finite). A host-contracted literal — identical across DXC targets.
#define SDF_LOGSPHERE_MIN_RADIUS 1.0e-4
// SDF_FLARE_MIN_SCALE floors SDF_OP_AXIAL_PROFILE's scale profile s(t) so an authored amount/bulge combination that
// drives it non-positive still yields a finite warp rather than a divide-by-zero or a sign flip.
// SDF_LANE_ERODE_RAGGED_AMOUNT is SDF_OP_LANE_ERODE's ragged-front noise weight: how far the noise sample (centered,
// [-0.5, 0.5]) perturbs the saturated lane fraction before it scales the target shape's reach — 0 would erode a
// uniform, noise-free front.
// The hash-stream triple SDF_OP_LANE_ERODE folds into sdfValueNoise3 — the op carries no seed lane of its own
// (data0/data1 are fully spent on lane index/from/to/noiseScale/reach), so every erode instruction shares one fixed,
// still-decorrelated-per-axis stream (reusing the existing hash-stream separators, never a fresh magic constant).
#define SDF_LANE_ERODE_SEED uint3(SDF_HASH_STREAM_A, SDF_HASH_STREAM_B, SDF_HASH_TUMBLE)

// The scene's directional sun, PRE-NORMALIZED to the exact float32 triple that DXC's DXIL backend constant-folds
// normalize(float3(0.55, 0.85, 0.35)) into (bits 0x3F03708B / 0x3F4B224B / 0x3EA7496B). DXC's SPIR-V backend does NOT
// fold it — it emits a runtime OpExtInst Normalize — so spelling the folded value here keeps the single most
// load-bearing shading vector (sunDiffuse, the shadow ray, sdfMaterialShade's half-vector) the SAME BITS on both
// backends instead of "one compile-time constant, one driver rsqrt". Every kernel that lights a surface uses it.
static const float3 SdfSunDirection = float3(0.51343602, 0.79349202, 0.32673201);

// --- primitive lane layouts (Puck.SignedDistance.SdfShapeType) ---
// Every ellipsoid is SDF_SHAPE_SUPERELLIPSOID at exponent 2.
// The 2D-primitive family: an exact 2D SDF lifted to 3D. Shared lane layout — data0.xyz = 2D params, data0.w = lift
// amount (revolve offset o OR extrude half-height h), data1.x = smooth, data1.y = lift mode (an SDF_LIFT_* value,
// decoded as `> 0.5` so a float lane carries it cleanly on both backends), data1.zw = per-shape host-baked constants.
// A glyph SAMPLED FROM A FONT ATLAS as a DISTANCE-level field (SdfShapeType.Glyph). data0 =
// (packedUvMin, packedUvMax [each host-baked unorm2x16 of an atlas UV], distanceScale, extrudeHalfDepth); data1 =
// (smooth [ISA-wide], halfWidth, halfHeight, _). Only the world-views kernel binds the atlas (SDF_GLYPH_ATLAS); every
// other kernel evaluates the conservative extruded-quad fallback (the glyph is strictly inside its cell). See sdfGlyph.
// A SAMPLED distance-field brick (SdfShapeType.SampledRegion). data0 = (boxMin.xyz, cellSize); data1 =
// (smooth [ISA-wide], packedDims [3x10-bit dims, unpacked with SDF_SAMPLED_REGION_DIM_MASK], brickWordOffset [pool base word], boundaryFloor
// [outside-box lower-bound offset = margin/lambda]). Evaluated by manual trilinear ONLY where the pool is bound
// (SDF_SAMPLED_REGIONS); every other kernel returns the conservative union-hull fallback (SDF_FAR_DISTANCE, so a
// Subtraction compose never bites). See sdfSampledRegion.
// A 45-degree-chamfered rectangle (SdfShapeType.ChamferedRectangle) — the family's shared lane
// layout: data0 = (halfX, halfY, chamfer c, lift); data1 = (smooth, lift mode, UNUSED, edge-rounding radius r).
// The extrude lift additionally bevels the cap edges at the same c via sdfExtrudeChamfer2D. c = 0 reduces both the 2D
// core and the extrude join to the plain rectangle/box forms exactly.
// A generalized ellipsoid (SdfShapeType.Superellipsoid). data0 = (radiusX, radiusY, radiusZ,
// exponent e in [2, 8]); data1 = (smooth [ISA-wide], 1/radiusX, 1/radiusY, 1/radiusZ [host-baked]). e = 2 is the
// ellipsoid — the ISA's one ellipsoid spelling — and runs a pow-free fast path (sdfEllipsoidGauge) that stays compiled
// in the fold tier; the general exponent path is SDF_STRIP_HEAVY. KEEP IN SYNC with SdfViewsKernelVariants, which
// sends an e != 2 instance to the full variant.
// A validated convex polygon (SdfShapeType.ConvexPolygon) — the 2D-primitive family's lane layout,
// but its profile is a vertex list too large to pack inline: data0.x = asfloat(packed uint (tableOffset << 4) |
// vertexCount), data0.w = lift amount; data1 = (smooth [ISA-wide], lift mode, cap chamfer, edge-rounding radius). The
// vertices live in sdfWords itself, right after every other table this program packs (see sdfPolygonVertex).
// A quadratic Bezier curve (SdfShapeType.Sweep) swept with a tapering, bulging radius, optionally
// as helical strands. data0 = (asfloat(uint table offset), strands, twist, strandOffset); data1 = (smooth
// [ISA-wide], reserved, reserved, reserved). The control points (A, B, C) and radius endpoints
// (radiusStart, radiusEnd, bulge) live in sdfWords, 3 fixed uvec4 words at the table offset (see sdfSweepCurve).

// --- bound records (Puck.SignedDistance.SdfProgram's PackBounds) ---
// Segment metadata overlays SDF_SEGMENT_RIGID_PLAN on the high bit of its bound mode. The low byte remains
// SDF_BOUND_*; shape and instance bound records never carry this flag.
// A leaf flagged SDF_RIGID_LEAF_FOLDED rides a fold run of at most SDF_RIGID_LEAF_MAX_FOLD_RUN instructions; the slot
// after it holds (pose before the run.xyz, first fold | identity bit), that pose's quaternion, and (run length in
// instructions, 0, 0, 0).

// --- blend operators ---
// THE ACCUMULATOR RULE (Puck.SignedDistance.SdfBlendOp's summary). mapCore carries ONE running nearest-surface
// distance across the WHOLE program; SDF_OP_RESET_POINT resets the evaluation POINT, never result.distance. So a blend never
// sees a subtree - it sees every shape emitted before it. Union (a min) and subtraction (a max against the NEGATED
// candidate, which only bites inside the subtrahend) are therefore LOCAL and may appear anywhere. The INTERSECTION
// family is not: max(accumulator, candidate) returns the candidate wherever the candidate is farther, i.e. everywhere
// outside its own shape, so it annihilates every earlier shape it does not overlap. Author an intersection pair FIRST.
// That unbounded influence region is also why an INSTANCE carrying one cannot be culled (SdfProgram.UnmaskableBoundRadius).
// Chamfered (45° beveled) seams — the mechanical/CAD counterpart to the smooth (round) blends; bevel size = Data1.x.
// For unit outward gradients meeting at angle φ, |∇((a + b - r)·√½)| = √2·cos(φ/2): the bevel plane's gradient reaches
// √2 at a FLAT / near-parallel seam (two tangent surfaces, φ → 0), is exactly 1 at a perpendicular seam, and falls to 0
// at an acute knife edge. The √2 ceiling is real and attained. Generally, for operands bounded by La and Lb the bevel
// arm's gradient is (∇a ± ∇b)/√2, so the composed bound is max(La, Lb, (La + Lb)/√2) — the one blend family that is not
// 1-Lipschitz, and the only arm that can exceed BOTH operands, so SdfProgram.AnalyzeLipschitz folds it once per
// COMPOSITION in this switch's own order rather than once per program. The recurrence's fixed point is 1 + √2, and the
// accumulator starts at the SDF_FAR_DISTANCE constant, which is what makes the first chamfer composition the identity.
// (Puck.SignedDistance.SdfBlendOp.)

// Material sentinel range: a SCREEN_SLAB shades as a "screen" rather than a table albedo. The plain sentinel
// (SdfProgramBuilder.ScreenSlab with no screen index) shades as unbound glass. SDF_SCREEN_MATERIAL + 1 +
// screenIndex (SdfProgramBuilder's screen-surface overload) additionally identifies WHICH declared screen surface —
// and so which screen source slot (0..31) — the hit belongs to, decoded as (material - SDF_SCREEN_MATERIAL - 1).
// Every material id in this range is screen shading; test with >= SDF_SCREEN_MATERIAL, never ==.
#define SDF_ISA_ERROR_MATERIAL (-1) // sdfMaterialLoad decodes this as emissive diagnostic magenta.

#endif
