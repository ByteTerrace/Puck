// The SDF virtual machine — single-source HLSL, compiled by DXC to both SPIR-V (Vulkan) and DXIL (Direct3D 12).
// A program is a flat uint4 word stream that map() interprets per sample point: transform opcodes mutate the
// evaluation point, SHAPE opcodes evaluate a primitive and blend it into the running nearest-surface result.
// KEEP IN SYNC with Puck.SdfVm (SdfOp/SdfShapeType/SdfBlendOp and the SdfProgram word layout).
#ifndef SDF_VM_HLSLI
#define SDF_VM_HLSLI

// Program word stream (each element one uint4 = 16 bytes). A read-only StructuredBuffer. On Vulkan it is the
// storage buffer at set 0, binding 1 (what the backend's descriptor layout expects); on DirectX it is an SRV at
// t0 (DirectXGpuPipelineFactory's storage-buffer SRV slot — the program is never written, the buffer is on an
// upload heap where UAVs are invalid, and an SRV avoids the pixel-shader u0/render-target clash). Layout:
//   words[0]              = (instructionCount, materialCount, dataOffset, materialOffset)
//   words[1 .. 1+N)       = instruction headers (op, shapeType, blendOp, materialId)
//   words[dataOffset ..]  = instruction data, 2 uint4 per instruction (data0, data1 as float bits)
//   words[matOffset ..]   = materials, 2 uint4 each (m0 = albedo.rgb + emissive, m1 = specular + shininess + 2
//                           reserved, all as float bits)
//   words[matOffset + 2*materialCount ..] = HOST-BAKED bounding-sphere table (SdfProgram.PackBounds), 2 uint4 per
//                           instruction: b0 = center/offset.xyz + radius (float bits), b1 = (mode, dynamicSlot,
//                           skipTo, 0) — map()'s exact Union early-out reads it; mode SDF_BOUND_NONE evaluates fully.
//   [.. segment directory ..] then the INSTANCE directory (SdfProgram.PackInstances, world render path only): one
//                           (instanceCount, 0, 0, 0) header uint4, then 2 uint4 per instance — i0 = bound
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
[[vk::binding(1, 0)]] StructuredBuffer<uint4> sdfWords : register(t0);

// The instance CEILING — the most instances one program may declare. The per-tile mask is a DERIVED
// ceil(instanceCount/32) uints (sdfInstanceMaskWordCount), so the ceiling caps it at SDF_MAX_INSTANCES/32 = 512
// words. KEEP IN SYNC with SdfProgramBuilder.MaxInstances.
#define SDF_MAX_INSTANCES 16384u
// Sentinel instance-mask BASE meaning "every instance visible" (sdfInstanceMaskWord then reads no buffer and
// returns all-ones words). Every map() CONSUMER that cannot reach the beam-computed per-tile mask (the debug frag
// view, the ray-query debug kernel, the beam prepass's own cone march) passes this, so an instanced program still
// renders its complete picture through them; only sdf-world-views.comp narrows it to a real per-tile mask base.
#define SDF_INSTANCE_MASK_ALL 0xFFFFFFFFu

#ifdef SDF_INSTANCE_MASKS
// The per-tile instance mask sdf-instance-cull.comp wrote (world render path): a flat uint buffer,
// params.instanceMaskWordCount (the host-pushed live program width) elements per (viewport, tile) entry, same
// (viewport, tile) indexing as the cull buffer. TWO readers, with different Direct3D 12 SRV registers (the register
// follows each kernel's engine binding-list order): Stage 1 at the default t13 (the first slot free of its
// program/viewport/dynamicTransforms/cullBounds/screenSurfaces/screenSources run, t0..t12) and the beam cone march at
// t3 (its list is program/viewports/dynamicTransforms + this) — the consumer overrides SDF_INSTANCE_MASKS_REGISTER
// before including. KEEP IN SYNC with SdfWorldEngine's binding lists.
#ifndef SDF_INSTANCE_MASKS_REGISTER
#define SDF_INSTANCE_MASKS_REGISTER t13
#endif
[[vk::binding(7, 0)]] StructuredBuffer<uint> sdfInstanceMasks : register(SDF_INSTANCE_MASKS_REGISTER);
#endif

// The per-tile mask width in uints for a program: ceil(instanceCount/32), never below 1 (a zero-instance program
// keeps one all-zero word so the mask buffer indexing stays uniform). Used ONLY for the reader's inner word
// iteration — buffer INDEXING (entry width and tile base) comes from the host-pushed
// params.instanceMaskWordCount (worldInstanceMaskBase in sdf-world.hlsli). KEEP IN SYNC with
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

#ifdef SDF_INSTANCE_MASKS
    if (instanceMaskBase != SDF_INSTANCE_MASK_ALL) {
        word = sdfInstanceMasks[instanceMaskBase + wordIndex];
    }
#endif

    uint remaining = (instanceCount - (wordIndex << 5u));

    return (word & ((remaining >= 32u) ? 0xFFFFFFFFu : ((1u << remaining) - 1u)));
}

// The SEGMENT directory's element offset in sdfWords: header -> materials -> shape-bound table -> segment directory.
// KEEP IN SYNC with SdfProgram's offset math.
uint sdfSegmentDirectoryOffset() {
    uint4 header = sdfWords[0];

    return ((header.w + (2u * header.y)) + (2u * header.x));
}
// The INSTANCE directory's element offset, given a caller that ALREADY resolved the segment directory (mapCore has
// both in hand). DXC's SPIR-V backend runs no GVN over StructuredBuffer loads, so re-deriving them costs a real
// reload of sdfWords[0] on Vulkan.
uint sdfInstanceDirectoryOffsetFrom(uint segmentOffset, uint segmentCount) {
    return (segmentOffset + 1u + (2u * segmentCount));
}
// The INSTANCE directory's element offset in sdfWords — the ONE resolution of the packed offset chain for callers that
// hold nothing yet. Every consumer that touches the directory locates it through this or its `From` sibling.
uint sdfInstanceDirectoryOffset() {
    uint segmentOffset = sdfSegmentDirectoryOffset();

    return sdfInstanceDirectoryOffsetFrom(segmentOffset, sdfWords[segmentOffset].x);
}
// The per-PROGRAM Lipschitz STEP SCALE (1/L in (0, 1]), baked HOST-SIDE into the segment-directory header's otherwise-
// free .y lane (SdfProgram.AnalyzeLipschitz). mapCore multiplies EVERY returned distance by it, so a consumer that
// compares that distance against a WORLD-space quantity (a penumbra ratio, a footprint threshold) rather than taking a
// STEP with it must divide the clamp back out. == 1.0 exactly for an isometric, warp-free program (x * 1.0f == x to the
// bit for every finite x), so those scenes stay byte-identical. The `> 0` guard keeps a pre-writer all-zero stream
// rendering as before.
float sdfStepScale() {
    float stepScale = asfloat(sdfWords[sdfSegmentDirectoryOffset()].y);

    return ((stepScale > 0.0) ? stepScale : 1.0);
}
// The element offset of instance `index`'s directory entry (i0 = bound, i1 = meta at +1) within the directory at
// `instanceOffset` — the ONE statement of the 2-uint4-per-instance entry stride.
uint sdfInstanceEntryOffset(uint instanceOffset, uint index) {
    return (instanceOffset + 1u + (2u * index));
}
// The packed instance count (the directory's header lane).
uint sdfInstanceCount() {
    return sdfWords[sdfInstanceDirectoryOffset()].x;
}

// The ceiling-clamped instance count. The mask-buffer indexing contract itself (entry width, tile base) lives in
// sdf-world.hlsli's worldInstanceMaskBase: both world kernels resolve it from the host-pushed
// params.instanceMaskWordCount (the beam prepass WRITES entry `tileIndex`'s words, Stage 1 hands mapCore the SAME
// entry's base).
uint sdfInstanceCountClamped() {
    return min(sdfInstanceCount(), SDF_MAX_INSTANCES);
}

// The world-space UNIFORM-GRID instance cull (world render path, the beam prepass ONLY): a uint-granular block appended
// after the world-segment list, so mapCore — which stops at that list — never reads it and every rendered pixel is
// unchanged by its presence. The beam walks it instead of testing every instance in every tile, so its cost tracks the
// instances NEAR a tile's cone. KEEP IN SYNC with Puck.SdfVm.SdfInstanceGrid (the host packer) — the header layout,
// SDF_GRID_HEADER_WORDS, and SDF_GRID_MAX_DIM.
#define SDF_GRID_HEADER_WORDS 16u
#define SDF_GRID_MAX_DIM 64u     // per-axis cell-count cap (KEEP IN SYNC with SdfInstanceGrid.MaxDimension)
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
// The grid block's base WORD offset (uint-granular), given the instance directory offset and the UNCLAMPED packed
// instance count the caller already holds (mapCore and the beam both resolve them). The block sits one uint4 (the
// world-segment header) plus the world-segment entries past the instance directory's own span.
uint sdfGridBaseWord(uint instanceOffset, uint instanceCount) {
    uint worldSegmentOffset = (instanceOffset + 1u + (2u * instanceCount)); // uint4 index of the world-segment header
    uint worldSegmentCount = sdfWords[worldSegmentOffset].x;
    uint gridBaseVector = (worldSegmentOffset + 1u + worldSegmentCount);    // uint4 index of the grid block

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
    uint base = sdfGridBaseWord(instanceOffset, instanceCount);

    SdfInstanceGridHeader grid;
    grid.baseWord = base;
    grid.enabled = (sdfWordAt(base + 0u) != 0u);
    grid.dims = uint3(sdfWordAt(base + 1u), sdfWordAt(base + 2u), sdfWordAt(base + 3u));
    grid.origin = float3(asfloat(sdfWordAt(base + 4u)), asfloat(sdfWordAt(base + 5u)), asfloat(sdfWordAt(base + 6u)));
    grid.invCellSize = asfloat(sdfWordAt(base + 7u));
    grid.cellSize = asfloat(sdfWordAt(base + 8u));
    grid.footprintPad = asfloat(sdfWordAt(base + 9u));
    grid.cellStartWord = sdfWordAt(base + 10u);
    grid.entryWord = sdfWordAt(base + 11u);
    grid.alwaysWord = sdfWordAt(base + 12u);
    grid.alwaysCount = sdfWordAt(base + 13u);
    grid.cellCount = sdfWordAt(base + 14u);

    return grid;
}

#ifdef SDF_DYNAMIC_TRANSFORMS
// Per-frame dynamic entity transforms (the world render path only). Each moving entity (player/enemy/carried screen)
// owns a slot: element 2*slot is its world position (xyz), 2*slot+1 its orientation quaternion (xyzw). The
// SDF_OP_TRANSFORM_DYNAMIC opcode reads its rigid transform from here by slot index, so an entity moves by updating
// this small buffer instead of re-uploading the static program — the same way the camera moves via the per-frame
// viewport table. register(t2) follows the program (t0) and the world viewport table (t1, in sdf-world.hlsli).
[[vk::binding(9, 0)]] StructuredBuffer<float4> sdfDynamicTransforms : register(t2);
#endif

// --- opcodes (mirror Puck.SdfVm.SdfOp) ---
#define SDF_OP_RESET             0u
#define SDF_OP_TRANSLATE         1u
#define SDF_OP_ROTATE            2u
#define SDF_OP_SCALE             3u
#define SDF_OP_TRANSFORM_DYNAMIC 4u
#define SDF_OP_BEND_X            5u
#define SDF_OP_BEND_Y            6u
#define SDF_OP_BEND_Z            7u
#define SDF_OP_ELONGATE          8u
#define SDF_OP_SHAPE             9u
#define SDF_OP_REPEAT          11u
#define SDF_OP_REPEAT_LIMITED  12u
// 13–15 (SymmetryX/Y/Z) retired — collapsed into SDF_OP_SYMMETRY_PLANE (26), which reproduces each with an axis normal.
#define SDF_OP_ONION           16u
#define SDF_OP_DILATE          17u
#define SDF_OP_WALLPAPER_FOLD  18u
#define SDF_OP_TWIST_Y         20u
#define SDF_OP_LOG_SPHERE      21u
#define SDF_OP_CELL_JITTER     22u
#define SDF_OP_REPEAT_POLAR    23u
#define SDF_OP_DISPLACE        24u
#define SDF_OP_DOMAIN_WARP     25u
#define SDF_OP_SYMMETRY_PLANE  26u
// Scoped field accumulator (KEEP IN SYNC with Puck.SdfVm.SdfOp.PushField/PopField). PUSH saves the running accumulator
// into a one-deep slot and reseeds a fresh scope; POP composes the scope's field back into the saved parent as a
// candidate (reusing SHAPE's blend tail). SDF_MAX_FIELD_SCOPE_DEPTH is DOCUMENTATION ONLY — no shader expression reads
// it; the real capacity is the single non-indexed (savedFieldDistance, savedFieldMaterial) scalar pair in mapCore, which
// holds exactly ONE parent. Raising the depth means making that pair an indexed array with push/pop-by-depth stack
// semantics HERE, not just bumping this #define. KEEP IN SYNC with SdfProgramBuilder.MaxFieldScopeDepth.
#define SDF_OP_PUSH_FIELD      27u
#define SDF_OP_POP_FIELD       28u
#define SDF_MAX_FIELD_SCOPE_DEPTH 1u
// SDF_OP_CELL_JITTER Blend-lane (instructionHeader.z) noise flavor: how the per-cell POSITION offset is distributed
// (KEEP IN SYNC with Puck.SdfVm.SdfNoiseFlavor). Reshapes ONLY r0 — tumble and material variant are unaffected.
#define SDF_NOISE_WHITE          0u
#define SDF_NOISE_BLUE           1u
#define SDF_NOISE_GAUSSIAN       2u
// SDF_OP_REPEAT_POLAR Shape-lane (instructionHeader.y) rotation axis (KEEP IN SYNC with Puck.SdfVm.SdfPolarAxis): the
// angular fold acts in the plane PERPENDICULAR to it (the axial coordinate is untouched).
#define SDF_POLAR_AXIS_X         0u
#define SDF_POLAR_AXIS_Y         1u
#define SDF_POLAR_AXIS_Z         2u

// --- wallpaper symmetry groups, IUC order (mirror Puck.SdfVm.SdfWallpaperGroup) ---
#define SDF_WPG_P1    0u
#define SDF_WPG_P2    1u
#define SDF_WPG_PM    2u
#define SDF_WPG_PG    3u
#define SDF_WPG_CM    4u
#define SDF_WPG_PMM   5u
#define SDF_WPG_PMG   6u
#define SDF_WPG_PGG   7u
#define SDF_WPG_CMM   8u
#define SDF_WPG_P4    9u
#define SDF_WPG_P4M  10u
#define SDF_WPG_P4G  11u
#define SDF_WPG_P3   12u
#define SDF_WPG_P3M1 13u
#define SDF_WPG_P31M 14u
#define SDF_WPG_P6   15u
#define SDF_WPG_P6M  16u

// === Shared numeric constants ========================================================================================
// Written at full double precision: each rounds to the SAME float32 the shorter literal did, so naming them is
// bytecode-identical while the digits document the exact quantity.
#define SDF_SQRT3     1.7320508075688772   // sqrt(3)
#define SDF_SQRT_HALF 0.7071067811865476   // sqrt(1/2) — the 45-degree chamfer bevel plane's normalization
#define SDF_PI        3.141592653589793
#define SDF_TAU       6.283185307179586    // 2*pi
// 2^-32, exact. Maps a full-range uint hash to a float in [0, 1] — NOTE the CLOSED upper end: (float)0xFFFFFFFFu
// rounds UP to 2^32, so the product can be exactly 1.0. Every consumer below is written to tolerate that.
#define SDF_INV_2POW32 (1.0 / 4294967296.0)

// The "nothing nearer yet" sentinel every accumulator and every unknown shape id starts at. It is deliberately far
// beyond MaxDistance (60) so it always loses a min() against real geometry, yet small enough that `a + (b - a)`
// still resolves; see blendSmoothUnion, which must NOT be handed this value through a saturating lerp.
#define SDF_FAR_DISTANCE 1.0e9

// Degenerate-input floors. The Scale / Repeat / RepeatLimited floors are HOST-BAKED (SdfProgramBuilder) — these
// two are the ones the shader still applies itself.
#define SDF_SMOOTH_RADIUS_MIN  0.0001   // the smooth blends' radius floor (the CHAMFER blends clamp against 0.0)
#define SDF_ELLIPSOID_MIN_DENOM 0.0001  // keeps the approximate ellipsoid's k1 divide finite at its center

// Clamps length(p) away from 0 in the log-spherical fold so log() never sees -inf at the Droste center (the origin is
// a measure-zero singularity, kept finite). A host-contracted literal — identical across DXC targets.
#define SDF_LOGSPHERE_MIN_RADIUS 1.0e-4

// The scene's directional sun, PRE-NORMALIZED to the exact float32 triple that DXC's DXIL backend constant-folds
// normalize(float3(0.55, 0.85, 0.35)) into (bits 0x3F03708B / 0x3F4B224B / 0x3EA7496B). DXC's SPIR-V backend does NOT
// fold it — it emits a runtime OpExtInst Normalize — so spelling the folded value here keeps the single most
// load-bearing shading vector (sunDiffuse, the shadow ray, sdfMaterialShade's half-vector) the SAME BITS on both
// backends instead of "one compile-time constant, one driver rsqrt". Every kernel that lights a surface uses it.
static const float3 SdfSunDirection = float3(0.51343602, 0.79349202, 0.32673201);

// --- primitives ---
#define SDF_SHAPE_BOX          0u
#define SDF_SHAPE_CAPSULE      1u
#define SDF_SHAPE_SPHERE       2u
#define SDF_SHAPE_TORUS        3u
#define SDF_SHAPE_CYLINDER     4u
#define SDF_SHAPE_PLANE        5u
#define SDF_SHAPE_ELLIPSOID    6u
#define SDF_SHAPE_VESICA       7u
// The 2D-primitive family: an exact IQ 2D SDF lifted to 3D. KEEP IN SYNC with SdfShapeType. Shared lane layout —
// data0.xyz = 2D params, data0.w = lift amount (revolve offset o OR extrude half-height h), data1.x = smooth,
// data1.y = lift mode (see SDF_LIFT_*), data1.zw = per-shape host-baked constants.
#define SDF_SHAPE_ROUNDED_RECT     8u
#define SDF_SHAPE_REGULAR_POLYGON  9u
#define SDF_SHAPE_STAR            10u
#define SDF_SHAPE_ROUND_CONE      11u
#define SDF_SHAPE_TRAPEZOID       12u
#define SDF_SHAPE_ELLIPSE         13u
#define SDF_SHAPE_SCREEN_SLAB     14u

// Lift mode for the 2D-primitive family (data1.y). Decoded as `> 0.5` so a float lane carries it cleanly on both
// backends. KEEP IN SYNC with Puck.SdfVm.SdfLift.
#define SDF_LIFT_REVOLVE 0u
#define SDF_LIFT_EXTRUDE 1u

// --- bounding-sphere entry modes (mirror Puck.SdfVm.SdfProgram's PackBounds) ---
#define SDF_BOUND_NONE    0u
#define SDF_BOUND_STATIC  1u
#define SDF_BOUND_DYNAMIC 2u

// --- blend operators ---
// THE ACCUMULATOR RULE (KEEP IN SYNC with Puck.SdfVm.SdfBlendOp's summary). mapCore carries ONE running nearest-surface
// distance across the WHOLE program; SDF_OP_RESET resets the evaluation POINT, never result.distance. So a blend never
// sees a subtree - it sees every shape emitted before it. Union (a min) and subtraction (a max against the NEGATED
// candidate, which only bites inside the subtrahend) are therefore LOCAL and may appear anywhere. The INTERSECTION
// family is not: max(accumulator, candidate) returns the candidate wherever the candidate is farther, i.e. everywhere
// outside its own shape, so it annihilates every earlier shape it does not overlap. Author an intersection pair FIRST.
// That unbounded influence region is also why an INSTANCE carrying one cannot be culled (SdfProgram.UnmaskableBoundRadius).
#define SDF_BLEND_UNION               0u
#define SDF_BLEND_SMOOTH_UNION        1u
#define SDF_BLEND_SUBTRACTION         2u
#define SDF_BLEND_INTERSECTION        3u
#define SDF_BLEND_XOR                 4u
#define SDF_BLEND_SMOOTH_INTERSECTION 5u
#define SDF_BLEND_SMOOTH_SUBTRACTION  6u
// Chamfered (45° beveled) seams — the mechanical/CAD counterpart to the smooth (round) blends; bevel size = Data1.x.
// For unit outward gradients meeting at angle φ, |∇((a + b - r)·√½)| = √2·cos(φ/2): the bevel plane's gradient reaches
// √2 at a FLAT / near-parallel seam (two tangent surfaces, φ → 0), is exactly 1 at a perpendicular seam, and falls to 0
// at an acute knife edge. The √2 ceiling is real and attained, so a chamfer blend carries a conservative √2 step clamp
// via SdfProgram.AnalyzeLipschitz — the one blend family that is not 1-Lipschitz (KEEP IN SYNC with Puck.SdfVm.SdfBlendOp).
#define SDF_BLEND_CHAMFER_UNION        7u
#define SDF_BLEND_CHAMFER_INTERSECTION 8u
#define SDF_BLEND_CHAMFER_SUBTRACTION  9u

// Material sentinel range: a SCREEN_SLAB shades as a "screen" rather than a table albedo. The plain sentinel
// (SdfProgramBuilder.ScreenSlab with no screen index) shades the procedural test-card. SDF_SCREEN_MATERIAL + 1 +
// screenIndex (SdfProgramBuilder's screen-surface overload) additionally identifies WHICH declared screen surface —
// and so which screen source slot (0..7) — the hit belongs to, decoded as (material - SDF_SCREEN_MATERIAL - 1).
// Every material id in this range is screen shading; test with >= SDF_SCREEN_MATERIAL, never ==.
#define SDF_SCREEN_MATERIAL 65535

struct SdfHit {
    float distance;
    int material;
};

float3 rotatePointByInverseQuaternion(float3 p, float4 q) {
    float3 u = -q.xyz;
    return (p + (2.0 * cross(u, ((q.w * p) + cross(u, p)))));
}

// The symmetry-LOD origin (the marching camera's world position), set by each kernel's entry point before it
// marches. A wallpaper fold whose data1.z threshold is exceeded by distance(worldPosition, sdfLodOrigin) keeps its
// lattice but skips the in-cell folds. The 0 default (with threshold 0 = off) keeps any kernel that never sets it
// correct.
static float3 sdfLodOrigin = float3(0.0, 0.0, 0.0);

// GLSL-style FLOOR modulo. HLSL's fmod truncates toward zero, so it disagrees with GLSL's mod for negative
// operands — and the wallpaper parity keys take mod of (possibly negative) cell indices, where a trunc-mod would
// silently mis-color/mis-rotate every negative-index cell. Keep every wallpaper mod on this helper.
float sdfFloorMod(float x, float y) {
    return (x - (y * floor(x / y)));
}

// Hex-lattice wallpaper groups (P3 and up) on the equilateral triangular lattice of pitch cell.x (the host requires
// square cells; the hex groups are only exact on the equilateral lattice, so the lattice shape is not a free
// parameter). Cells are cube-rounded axial hexes; limits clamp the axial indices with RepeatLimited semantics. P3
// keys a 120-degree turn count on the 3-coloring of the hex lattice (seams only at hex boundaries); P6 adds the
// in-cell half-turn; P3m1/P31m/P6m are in-cell dihedral kaleidoscopes (pure conditional mirror folds — continuous),
// with mirrors along the corner directions (P3m1), the edge directions (P31m), or both (P6m). All rotations/mirrors
// are written as EXPLICIT component expressions (no float2x2) so no row/column convention can flip a fold.
// inversePitch = (1/pitch, 2/(√3·pitch)), baked HOST-SIDE by SdfProgramBuilder.WallpaperFold (data0.zw).
float2 sdfWallpaperFoldHexCell(float2 q, uint group, float pitch, float2 inversePitch, float2 limit, bool lodSimplify, out float2 cellIndex) {
    float axialB = (q.y * inversePitch.y);
    float axialA = ((q.x * inversePitch.x) - (0.5 * axialB));
    float axialC = -(axialA + axialB);
    float roundedA = round(axialA);
    float roundedB = round(axialB);
    float roundedC = round(axialC);
    float errorA = abs(roundedA - axialA);
    float errorB = abs(roundedB - axialB);
    float errorC = abs(roundedC - axialC);

    if ((errorA > errorB) && (errorA > errorC)) {
        roundedA = -(roundedB + roundedC);
    }
    else if (errorB > errorC) {
        roundedB = -(roundedA + roundedC);
    }

    roundedA = clamp(roundedA, -limit.x, limit.x);
    roundedB = clamp(roundedB, -limit.y, limit.y);
    cellIndex = float2(roundedA, roundedB);

    float2 r = (q - (float2((roundedA + (0.5 * roundedB)), (roundedB * (SDF_SQRT3 * 0.5))) * pitch));

    if (lodSimplify) {
        // Symmetry LOD: keep the lattice (copies stay planted on the hex centers) but skip every in-cell fold —
        // upright copies, cheaper and shimmer-free at range.
        return r;
    }

    if (group == SDF_WPG_P6) {
        // p6 is the C6 fold about the hex CENTRE — six 60-degree sectors, folded onto one. 6-fold centres at the hex
        // centres, 3-folds at the corners, 2-folds at the edge midpoints; translation lattice = the hex lattice.
        //
        // It canNOT be built from P3's 3-coloring turn plus an in-cell half-turn: the turn count k(h) = (a - b) mod 3
        // satisfies k(-h) = -k(h), so the central inversion is not a symmetry and the pattern collapses to p3 (verified
        // by direct point-group measurement: max rotation 3, identical signature to P3). Unlike P3, whose seams sit only
        // on hex boundaries, this fold has IN-CELL seams — the six sector walls — so content must clear them, exactly as
        // for P3M1/P31M/P6M.
        float sector = floor(atan2(r.y, r.x) * (3.0 / SDF_PI));   // 3/pi = 1/(pi/3), one sector per 60 degrees
        float spin = -(sector * (SDF_PI / 3.0));
        float spinCos = cos(spin);
        float spinSin = sin(spin);

        return float2(((spinCos * r.x) - (spinSin * r.y)), ((spinSin * r.x) + (spinCos * r.y)));
    }

    if (group == SDF_WPG_P3) {
        // Turn count = the hex lattice 3-coloring (every corner touches one hex of each color), satisfying the
        // corner-rotation cocycle: the pattern gains 3-fold centers at the corners without any in-cell rotation seam.
        // Its translation lattice is the sqrt3 x sqrt3 supercell, not the hex cell (see the authoring note above).
        float turns = sdfFloorMod((roundedA - roundedB), 3.0);

        // A +120-degree rotation (GLSL mat2(-0.5, s, -s, -0.5) * r with s = sqrt(3)/2, written out), applied once for
        // turns == 1 and twice for turns == 2.
        if (turns >= 1.0) {
            r = float2(((-0.5 * r.x) - ((SDF_SQRT3 * 0.5) * r.y)), (((SDF_SQRT3 * 0.5) * r.x) - (0.5 * r.y)));
        }

        if (turns >= 2.0) {
            r = float2(((-0.5 * r.x) - ((SDF_SQRT3 * 0.5) * r.y)), (((SDF_SQRT3 * 0.5) * r.x) - (0.5 * r.y)));
        }

        return r;
    }

    // Dihedral kaleidoscopes: conditional reflections only, so the fold map is continuous. P3m1 mirrors run through
    // the corners (vertical + the 30/150 pair), P31m through the edge midpoints (horizontal + the 60/120 pair), P6m
    // through both.
    bool cornerMirrors = ((group == SDF_WPG_P3M1) || (group == SDF_WPG_P6M));
    bool edgeMirrors = ((group == SDF_WPG_P31M) || (group == SDF_WPG_P6M));

    if (edgeMirrors && (r.y < 0.0)) {
        r.y = -r.y;
    }

    if (cornerMirrors && (r.x < 0.0)) {
        r.x = -r.x;
    }

    if (edgeMirrors && (dot(r, float2(-(SDF_SQRT3 * 0.5), 0.5)) > 0.0)) {
        // Reflect across the 60-degree edge mirror (GLSL mat2(-0.5, s, s, 0.5) * r, written out).
        r = float2(((-0.5 * r.x) + ((SDF_SQRT3 * 0.5) * r.y)), (((SDF_SQRT3 * 0.5) * r.x) + (0.5 * r.y)));
    }

    if (edgeMirrors && (r.y < 0.0)) {
        r.y = -r.y;
    }

    if (cornerMirrors && (dot(r, float2(-0.5, (SDF_SQRT3 * 0.5))) < 0.0)) {
        // Reflect across the 30-degree corner mirror (GLSL mat2(0.5, s, s, -0.5) * r, written out).
        r = float2(((0.5 * r.x) + ((SDF_SQRT3 * 0.5) * r.y)), (((SDF_SQRT3 * 0.5) * r.x) - (0.5 * r.y)));
    }

    if (cornerMirrors && (r.x < 0.0)) {
        r.x = -r.x;
    }

    return r;
}

// Folds the in-plane coordinate q onto the fundamental cell of a wallpaper group. The lattice reduction is
// RepeatLimited restricted to two axes (P1 is bit-identical to it); the per-cell stage composes mirrors/rotations
// keyed on the lattice parity. Every branch is an isometry, so distances are preserved and callers never touch
// distanceScale. Like plain repetition, content must stay clear of cell boundaries (and of the rotation seams of
// P2/CMM/P4*) unless a mirror of the group protects that edge. lodSimplify (driven by the instruction's data1.z
// distance threshold) keeps the lattice but skips the in-cell folds — same copy positions, upright copies.
// inverseCell = 1/cell (square lattices) or the hex (1/pitch, 2/(√3·pitch)) pair, baked HOST-SIDE (data0.zw).
//
// AUTHORING NOTE: `cell` is the fold cell, NOT the pattern's translation period, for every group whose in-cell
// transform is keyed on the lattice PARITY (P2/PG/CM/PMG/PGG/CMM/P4/P4M/P4G) or on the hex 3-coloring (P3/P6). Those
// realize their point group over a sublattice: the parity groups repeat over the CENTERED/doubled cell (period
// 2*cell, plus the (1,1) centering vector), the P3/P6 turn cocycle over the √3×√3 hex supercell. Only P1/PM/PMM and
// the pure dihedral hex kaleidoscopes (P3M1/P31M/P6M) have period == cell. Verified by direct translation-invariance
// test over all 17 groups.
float2 sdfWallpaperFoldCell(float2 q, uint group, float2 cell, float2 inverseCell, float2 limit, bool lodSimplify, out float2 cellIndex) {
    if (group >= SDF_WPG_P3) {
        return sdfWallpaperFoldHexCell(q, group, cell.x, inverseCell, limit, lodSimplify, cellIndex);
    }

    cellIndex = clamp(round(q * inverseCell), -limit, limit);

    float2 r = (q - (cell * cellIndex));
    float2 parity = float2(sdfFloorMod(cellIndex.x, 2.0), sdfFloorMod(cellIndex.y, 2.0));

    if (lodSimplify) {
        return r;
    }

    if ((group == SDF_WPG_P4) || (group == SDF_WPG_P4M) || (group == SDF_WPG_P4G)) {
        // Quarter-turns about the cell corners (cells are square; the host validates). The per-cell turn count k
        // satisfies the corner-rotation cocycle, so the pattern is p4 with 4-fold centers on corners and cell centers.
        float k = sdfFloorMod(((parity.y - parity.x) - (2.0 * (parity.x * parity.y))), 4.0);

        if (k >= 2.0) {
            r = -r;
            k -= 2.0;
        }

        if (k >= 1.0) {
            r = float2(-r.y, r.x);
        }

        if ((group == SDF_WPG_P4M) && (r.y > r.x)) {
            // Mirror across the cell diagonal (through the 4-fold centers): p4m.
            r = r.yx;
        }

        if ((group == SDF_WPG_P4G) && ((abs(r.x) + abs(r.y)) > (0.5 * cell.x))) {
            // KNOWN DEFECT: this diamond fold does NOT produce p4g. The fold commutes with the quarter-turn R90, and
            // for a diagonal mirror m the per-cell turn count satisfies k(mq) = -k(q) mod 4, so no reflection survives:
            // a direct point-group measurement finds 4-fold rotation and ZERO mirror classes — the same signature as
            // SDF_WPG_P4. p4g needs its mirrors to sit at the half-cell offset the parity key destroys, so recovering it
            // is a redesign of the turn cocycle, not a tweak here. Until then this group renders as p4.
            float signU = ((r.x >= 0.0) ? 1.0 : -1.0);
            float signV = ((r.y >= 0.0) ? 1.0 : -1.0);
            float crossSign = (signU * signV);

            r = float2(
                ((signU * 0.5 * cell.x) - (crossSign * r.y)),
                ((signV * 0.5 * cell.x) - (crossSign * r.x)));
        }

        return r;
    }

    if (group == SDF_WPG_PM) {
        r.x = abs(r.x);

        return r;
    }

    if (group == SDF_WPG_PMM) {
        return abs(r);
    }

    // Sign-pair groups: each axis flips by (-1)^dot(coef, parity). Own-axis parity makes a boundary mirror, the
    // orthogonal parity a glide, the summed parities a half-turn; the pairs below are what distinguish the groups.
    float2 coefU = float2(0.0, 0.0);
    float2 coefV = float2(0.0, 0.0);

    switch (group) {
        case SDF_WPG_P2:  { coefU = float2(1.0, 1.0); coefV = float2(1.0, 1.0); break; }
        case SDF_WPG_PG:  { coefU = float2(0.0, 1.0); break; }
        case SDF_WPG_CM:  { coefU = float2(1.0, 1.0); break; }
        case SDF_WPG_PMG: { coefU = float2(1.0, 1.0); coefV = float2(0.0, 1.0); break; }
        case SDF_WPG_PGG: { coefU = float2(0.0, 1.0); coefV = float2(1.0, 0.0); break; }
        case SDF_WPG_CMM: { coefU = float2(1.0, 0.0); coefV = float2(0.0, 1.0); break; }
        default: { break; }
    }

    float2 foldSign = (1.0 - (2.0 * float2(sdfFloorMod(dot(coefU, parity), 2.0), sdfFloorMod(dot(coefV, parity), 2.0))));
    float2 folded = (r * foldSign);

    if ((group == SDF_WPG_CMM) && (folded.y < 0.0)) {
        // The half-turn about the cell centre must come AFTER the sign pair, not before it. Its image is centrally
        // symmetric, so cells (1,0) and (0,1) coincide: the lattice becomes CENTERED and BOTH boundary mirrors survive
        // (orbifold 2*22 = cmm). Applied BEFORE the sign pair, the diag(1,-1) flip swaps the half-planes this fold
        // selects and one mirror class degenerates into a glide — the pattern is then pmg, a duplicate of SDF_WPG_PMG.
        // Verified by direct point-group measurement: after = 2-fold + two mirror directions; before = 2-fold + one.
        folded = -folded;
    }

    return folded;
}

// The cell key the parity-material stride multiplies: the hex lattice's 3-coloring for the hex groups (matching the
// P3/P6 turn-count cocycle, so colors and rotations stay in sync), the checkerboard parity for the square-lattice
// groups. Survives the symmetry LOD (the lattice is what the LOD keeps), so distant cells hold their colors.
int sdfWallpaperCellKey(uint group, float2 cellIndex) {
    return ((group >= SDF_WPG_P3)
        ? (int)(sdfFloorMod((cellIndex.x - cellIndex.y), 3.0) + 0.5)
        : (int)(sdfFloorMod((cellIndex.x + cellIndex.y), 2.0) + 0.5));
}

// --- integer hashes (the cross-backend-exact randomness substrate) ---------------------------------------------------
// Every hashed DECISION in the ISA is integer-only on purpose: DXC lowers multiply/add/xor/shift bit-identically to
// both SPIR-V and DXIL, while float codegen drifts +-1 LSB between the two. A cell index, a noise lattice, and a
// dither pattern therefore come out the SAME on Vulkan and Direct3D.

// Knuth's LCG step, the mixing core of PCG3D.
#define SDF_PCG_MULTIPLIER 1664525u
#define SDF_PCG_INCREMENT  1013904223u
// Decorrelation multipliers for deriving independent hash streams from one seed (the golden-ratio and Murmur3 finalizer
// constants). SDF_HASH_TUMBLE keys the CellJitter tumble stream apart from the position and material streams.
#define SDF_HASH_STREAM_A 0x9E3779B9u
#define SDF_HASH_STREAM_B 0x85EBCA6Bu
#define SDF_HASH_TUMBLE   0x27D4EB2Fu

// Canonical PCG3D integer hash (Jarzynski & Olano, "Hash Functions for GPU Rendering"): three uints in, three
// well-mixed uints out. SDF_OP_CELL_JITTER keys this on the two's-complement cell index.
uint3 sdfPcg3d(uint3 v) {
    v = ((v * SDF_PCG_MULTIPLIER) + SDF_PCG_INCREMENT);
    v.x += (v.y * v.z); v.y += (v.z * v.x); v.z += (v.x * v.y);
    v ^= (v >> 16u);
    v.x += (v.y * v.z); v.y += (v.z * v.x); v.z += (v.x * v.y);
    return v;
}

// Roberts' R2 low-discrepancy lattice: alpha_i = round(2^32 / phi2^i) for the plastic number
// phi2 = 1.32471795724474602596 (the real root of x^3 = x + 1). The uint multiply wraps mod 2^32, which IS the
// fractional part of the additive recurrence — so the lattice is exact in fixed point.
#define SDF_R2_ALPHA1 3242174889u
#define SDF_R2_ALPHA2 2447445414u
// The R3 siblings: alpha_i = round(2^32 / phi3^i) for phi3 = 1.2207440846057596 (the real root of x^4 = x + 1).
// SDF_OP_CELL_JITTER's Blue flavor rotates these three across its axes so the offset components decorrelate.
#define SDF_R3_ALPHA1 3518319155u
#define SDF_R3_ALPHA2 2882110345u
#define SDF_R3_ALPHA3 2360945575u

// One R2 dither sample in [0, 1] from integer pixel coordinates. The spatial pattern is "blue-ish" low-discrepancy, so
// adding ~1 LSB of it before an 8-bit write turns gradient BANDING (sky, distance fog) into high-frequency noise the
// eye barely sees. Fixed-point, so both backends add the IDENTICAL pattern (a float frac(x/phi2 + y/phi2^2) would
// +-1-LSB diverge and break cross-backend parity).
float sdfR2Dither(uint2 pixel) {
    uint h = ((pixel.x * SDF_R2_ALPHA1) + (pixel.y * SDF_R2_ALPHA2));

    return ((float)h * SDF_INV_2POW32);
}

// === 3D primitives ===================================================================================================
// Exact signed-distance fields unless a comment says otherwise. Derived per-shape constants (a reciprocal, a sqrt, a
// normalization) are HOST-BAKED into the spare Data0/Data1 lanes by SdfProgramBuilder: shapes evaluate millions of
// times per frame while programs build once, and a shared multiply keeps both DXC targets on the identical operation
// (a varying-numerator divide contracted differently is a cross-backend fuzz-signature risk).

float sdfSphere(float3 p, float radius) {
    return (length(p) - radius);
}
// `cornerRadius` deliberately does NOT shadow the HLSL round() intrinsic (which this file's fold ops rely on).
float sdfBox(float3 p, float3 halfExtents, float cornerRadius) {
    float3 q = (abs(p) - (halfExtents - cornerRadius));
    return ((length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0)) - cornerRadius);
}
float sdfTorus(float3 p, float major, float minor) {
    float2 q = float2((length(p.xz) - major), p.y);
    return (length(q) - minor);
}
float sdfPlane(float3 p, float3 normal, float offset) {
    return (dot(p, normal) + offset);
}
// inverseLengthSquared = 1/dot(endpoint, endpoint), HOST-BAKED (data1.y) by SdfProgramBuilder.Capsule.
float sdfCapsule(float3 p, float3 endpoint, float radius, float inverseLengthSquared) {
    float h = clamp((dot(p, endpoint) * inverseLengthSquared), 0.0, 1.0);
    return (length(p - (h * endpoint)) - radius);
}
float sdfCylinder(float3 p, float radius, float halfHeight) {
    float2 d = (float2(length(p.xz), abs(p.y)) - float2(radius, halfHeight));
    return (min(max(d.x, d.y), 0.0) + length(max(d, 0.0)));
}
// NOT an exact distance (a first-order approximation): accurate near the surface, degrading with eccentricity — which
// is why it is the one primitive that earns no cull bound and instead feeds its eccentricity into the program's
// Lipschitz step clamp (SdfProgram.AnalyzeLipschitz). Prefer the exact revolved Ellipse (2D family) when a real
// bound matters. inverseRadii = 1/max(abs(radii), eps), HOST-BAKED (data1.yzw).
float sdfEllipsoid(float3 p, float3 inverseRadii) {
    float3 q = (p * inverseRadii);
    float k0 = length(q);
    float k1 = length(q * inverseRadii);
    return ((k0 * (k0 - 1.0)) / max(k1, SDF_ELLIPSOID_MIN_DENOM));
}
// slope b = (lowerRadius - upperRadius)/height and its complement a = sqrt(1 - b*b) are HOST-BAKED (data0.w / data1.y).
float sdfRoundCone(float3 p, float lowerRadius, float upperRadius, float height, float b, float a) {
    float2 q = float2(length(p.xz), p.y);
    float k = dot(q, float2(-b, a));

    if (k < 0.0) {
        return (length(q) - lowerRadius);
    }

    if (k > (a * height)) {
        return (length(q - float2(0.0, height)) - upperRadius);
    }

    return (dot(q, float2(a, b)) - lowerRadius);
}
// The vesica (lens): iq's 2D vesica revolved around Y. r = the two circles' radius, d = their half-separation (d < r),
// b = sqrt(r*r - d*d) = the tip half-height, HOST-BAKED by SdfProgramBuilder.Vesica to skip the per-eval sqrt. Exact
// and convex, so revolving the exact 2D field yields a true 3D distance (earns a cull bound, factor-1 Lipschitz). The
// lens is pointed along +/-Y and is a disc of radius (r - d) in the XZ plane.
float sdfVesica(float3 p, float r, float d, float b) {
    float2 q = float2(length(p.xz), abs(p.y));

    return (((q.y - b) * d) > (q.x * b))
        ? (length(q - float2(0.0, b)))
        : (length(q - float2(-d, 0.0)) - r);
}

// === The 2D-primitive family: exact IQ 2D SDFs lifted to 3D solids ==================================================
// Each primitive is an exact 2D signed-distance field (translated verbatim from iquilezles.org/articles/distfunctions2d)
// then LIFTED to 3D one of two ways (data1.y): EXTRUDE along Z (a prism/slab; opExtrusion) or REVOLVE around Y (a lathe;
// opRevolution). Extrusion of an exact 2D field is exact; revolution is exact when the profile clears the axis and a
// harmless conservative underestimate near it. Both are 1-Lipschitz, so no AnalyzeLipschitz step clamp is needed and
// each earns a real cull bound. The lifted wrappers below are what evaluateShape calls; the 2D cores are reusable.

// --- lift operators (iq opExtrusion / opRevolution) ---
// Extrude an exact 2D distance d (evaluated on the XY plane) to half-height h along Z: exact for any exact d.
float sdfExtrude2D(float d, float pz, float h) {
    float2 w = float2(d, (abs(pz) - h));
    return (min(max(w.x, w.y), 0.0) + length(max(w, 0.0)));
}
// The meridian point for revolving around Y at radial offset o: the 2D core is evaluated at (length(p.xz) - o, p.y).
float2 sdfRevolve2D(float3 p, float o) {
    return float2((length(p.xz) - o), p.y);
}

// --- exact 2D cores ---
// iq sdRoundedBox (single corner radius r): the box half-extents are b, corners rounded by r (staying within b).
float sdfRoundBox2D(float2 p, float2 b, float r) {
    float2 q = ((abs(p) - b) + r);
    return ((min(max(q.x, q.y), 0.0) + length(max(q, 0.0))) - r);
}
// iq sdTrapezoid: isosceles trapezoid, r1 = bottom half-width, r2 = top half-width, he = half-height.
float sdfTrapezoid2D(float2 p, float r1, float r2, float he) {
    float2 k1 = float2(r2, he);
    float2 k2 = float2((r2 - r1), (2.0 * he));
    p.x = abs(p.x);
    float2 ca = float2((p.x - min(p.x, ((p.y < 0.0) ? r1 : r2))), (abs(p.y) - he));
    float2 cb = ((p - k1) + (k2 * clamp((dot((k1 - p), k2) / dot(k2, k2)), 0.0, 1.0)));
    float s = (((cb.x < 0.0) && (ca.y < 0.0)) ? -1.0 : 1.0);
    return (s * sqrt(min(dot(ca, ca), dot(cb, cb))));
}
// iq sdStar (n points, inner-radius control m): r = outer radius, an = pi/n (baked), ecs = (cos(pi/m), sin(pi/m))
// (baked). ecs = (0, 1) collapses this to the exact regular n-gon (m = 2). The GLSL `mod` is FLOOR modulo — use the
// house sdfFloorMod so a negative atan2 sector index folds correctly (HLSL fmod truncates and would mis-fold).
float sdfStar2D(float2 p, float r, float an, float2 ecs) {
    float2 acs = float2(cos(an), sin(an));
    float bn = (sdfFloorMod(atan2(p.x, p.y), (2.0 * an)) - an);
    p = (length(p) * float2(cos(bn), abs(sin(bn))));
    p = (p - (r * acs));
    p = (p + (ecs * clamp((-dot(p, ecs)), 0.0, ((r * acs.y) / ecs.y))));
    return (length(p) * sign(p.x));
}
// iq sdEllipse: exact distance to an ellipse with semi-axes ab (the analytic depressed-cubic solve; branches on the
// discriminant). The host guards ab.x != ab.y (l = ab.y^2 - ab.x^2 divides), so a perfect circle never reaches here.
// The final sqrt is SATURATED: at extreme eccentricity (measured: aspect > ~100:1) rounding pushes `co` past 1, and an
// unguarded sqrt(1 - co*co) returns NaN — which then poisons the blend min() and diverges between backends, whose
// min(NaN, x) differ. saturate() only ever clamps the negative side (co*co >= 0 bounds the argument above by 1), so it
// is bit-identical wherever the old code was finite. At the exact centre p == (0,0) this returns 0 rather than
// -min(ab): sign(p.y - r.y) is 0 there. That is iq's own behaviour, a measure-zero point, and only observable through
// a Subtraction/Onion of an ellipse exactly at its centre.
float sdfEllipse2D(float2 p, float2 ab) {
    p = abs(p);

    if (p.x > p.y) {
        p = p.yx;
        ab = ab.yx;
    }

    float l = ((ab.y * ab.y) - (ab.x * ab.x));
    float m = ((ab.x * p.x) / l);
    float m2 = (m * m);
    float n = ((ab.y * p.y) / l);
    float n2 = (n * n);
    float c = (((m2 + n2) - 1.0) / 3.0);
    float c3 = ((c * c) * c);
    float q = (c3 + ((m2 * n2) * 2.0));
    float d = (c3 + (m2 * n2));
    float g = (m + (m * n2));
    float co;

    if (d < 0.0) {
        float h = (acos(q / c3) / 3.0);
        float s = cos(h);
        float t = (sin(h) * SDF_SQRT3);
        float rx = sqrt((-c * ((s + t) + 2.0)) + m2);
        float ry = sqrt((-c * ((s - t) + 2.0)) + m2);
        co = ((((ry + (sign(l) * rx)) + (abs(g) / (rx * ry))) - m) / 2.0);
    } else {
        float h = ((2.0 * m) * (n * sqrt(d)));
        float s = (sign(q + h) * pow(abs(q + h), (1.0 / 3.0)));
        float u = (sign(q - h) * pow(abs(q - h), (1.0 / 3.0)));
        float rx = ((((-s - u) - (c * 4.0)) + (2.0 * m2)));
        float ry = ((s - u) * SDF_SQRT3);
        float rm = sqrt(((rx * rx) + (ry * ry)));
        co = ((((ry / sqrt(rm - rx)) + ((2.0 * g) / rm)) - m) / 2.0);
    }

    float2 r = (ab * float2(co, sqrt(saturate(1.0 - (co * co)))));
    return (length(r - p) * sign(p.y - r.y));
}

// --- lifted wrappers (data1.y > 0.5 selects EXTRUDE; else REVOLVE) — what evaluateShape dispatches to ---
float sdfRoundedRect(float3 p, float4 data0, float4 data1) {
    return ((data1.y > 0.5)
        ? sdfExtrude2D(sdfRoundBox2D(p.xy, data0.xy, data0.z), p.z, data0.w)
        : sdfRoundBox2D(sdfRevolve2D(p, data0.w), data0.xy, data0.z));
}
// Regular polygon AND star share this: data0 = (r, an, ecs.x, lift), data1.z = ecs.y (the polygon bakes ecs = (0, 1)).
float sdfPolyStar(float3 p, float4 data0, float4 data1) {
    float2 ecs = float2(data0.z, data1.z);

    return ((data1.y > 0.5)
        ? sdfExtrude2D(sdfStar2D(p.xy, data0.x, data0.y, ecs), p.z, data0.w)
        : sdfStar2D(sdfRevolve2D(p, data0.w), data0.x, data0.y, ecs));
}
float sdfTrapezoidSolid(float3 p, float4 data0, float4 data1) {
    return ((data1.y > 0.5)
        ? sdfExtrude2D(sdfTrapezoid2D(p.xy, data0.x, data0.y, data0.z), p.z, data0.w)
        : sdfTrapezoid2D(sdfRevolve2D(p, data0.w), data0.x, data0.y, data0.z));
}
float sdfEllipseSolid(float3 p, float4 data0, float4 data1) {
    return ((data1.y > 0.5)
        ? sdfExtrude2D(sdfEllipse2D(p.xy, data0.xy), p.z, data0.w)
        : sdfEllipse2D(sdfRevolve2D(p, data0.w), data0.xy));
}
// === end 2D-primitive family =======================================================================================

// The ONE shape dispatch. Single-return (a result variable rather than returning inside the switch) so the compiler's
// flow analysis sees the value is always initialized. data1's .x lane is the ISA-wide smooth-blend radius; lanes .yzw
// carry HOST-BAKED derived constants per shape (see SdfProgramBuilder). Ids that share a body FALL THROUGH: DXC inlines
// each `case` separately, so a duplicated arm duplicates the whole primitive (the Box/ScreenSlab and Polygon/Star pairs
// cost ~10% of this kernel's instructions when written twice).
float evaluateShape(uint shapeType, float3 p, float4 data0, float4 data1) {
    float result = SDF_FAR_DISTANCE;

    switch (shapeType) {
        case SDF_SHAPE_SPHERE:      result = sdfSphere(p, data0.x); break;
        // A ScreenSlab IS a box; only its material sentinel distinguishes it (see SDF_SCREEN_MATERIAL).
        case SDF_SHAPE_BOX:
        case SDF_SHAPE_SCREEN_SLAB: result = sdfBox(p, data0.xyz, data0.w); break;
        case SDF_SHAPE_TORUS:       result = sdfTorus(p, data0.x, data0.y); break;
        // The plane normal is normalized HOST-SIDE (SdfProgramBuilder.Plane) — the biggest per-eval saving of all:
        // the plane is in nearly every scene, and a per-sample normalize would run for EVERY map() sample.
        case SDF_SHAPE_PLANE:       result = sdfPlane(p, data0.xyz, data0.w); break;
        case SDF_SHAPE_ROUND_CONE:  result = sdfRoundCone(p, data0.x, data0.y, data0.z, data0.w, data1.y); break;
        case SDF_SHAPE_CAPSULE:     result = sdfCapsule(p, data0.xyz, data0.w, data1.y); break;
        case SDF_SHAPE_CYLINDER:    result = sdfCylinder(p, data0.x, data0.y); break;
        case SDF_SHAPE_ELLIPSOID:   result = sdfEllipsoid(p, data1.yzw); break;
        case SDF_SHAPE_VESICA:      result = sdfVesica(p, data0.x, data0.y, data0.z); break;
        // The 2D-primitive family: each lifted wrapper reads its lift mode (data1.y) and lift amount (data0.w) itself.
        // A regular polygon is sdfStar2D's m = 2 case, so it shares the star's body verbatim.
        case SDF_SHAPE_ROUNDED_RECT:    result = sdfRoundedRect(p, data0, data1); break;
        case SDF_SHAPE_REGULAR_POLYGON:
        case SDF_SHAPE_STAR:            result = sdfPolyStar(p, data0, data1); break;
        case SDF_SHAPE_TRAPEZOID:       result = sdfTrapezoidSolid(p, data0, data1); break;
        case SDF_SHAPE_ELLIPSE:         result = sdfEllipseSolid(p, data0, data1); break;
    }

    return result;
}
// The polynomial smooth minimum every smooth blend derives from (smoothIntersection/smoothSubtraction are its
// negations, so all three share one seam). BOTH saturated endpoints return their input TO THE BIT:
//
//  * FAR (b at least k beyond a): h clamps to exactly 1, lerp(a, b, 0) == a and k*h*(1-h) == 0. This is what lets a
//    masked-out smooth-blended instance (dropped past its cull bound) return the accumulator identically to evaluating
//    it, so a smooth instance can carry a FINITE, k-inflated bound instead of an unmaskable one (SdfProgram.PackInstances).
//    The usual lerp(b, a, h) form leaves candidate + (current - candidate) here, ~1 LSB off.
//  * NEAR (a at least k beyond b): h clamps to exactly 0, and the `h <= 0` select returns b. Without that select the
//    expression is a + (b - a), which is NOT b when |a| >> |b| — and `a` is SDF_FAR_DISTANCE (1e9) for the first shape
//    evaluated in a mapCore call. blendSmoothUnion(1e9, 5, 0.2) then returns 0 rather than 5, collapsing the whole field
//    to a surface at the march origin. Reachable whenever a segment's first shape carries SmoothUnion and every earlier
//    segment was bound-skipped. The select costs one cndsel and makes "smooth-union with an empty accumulator" mean
//    what it must: the shape itself.
float blendSmoothUnion(float a, float b, float k) {
    float h = clamp((0.5 + ((0.5 * (b - a)) / k)), 0.0, 1.0);
    float blended = ((h <= 0.0) ? b : lerp(a, b, (1.0 - h)));

    return (blended - ((k * h) * (1.0 - h)));
}
float blendShape(float current, float candidate, uint blendOp, float smoothRadius) {
    float result = min(current, candidate);                     // SDF_BLEND_UNION (the default)
    float smoothK = max(smoothRadius, SDF_SMOOTH_RADIUS_MIN);   // the SMOOTH arms' shared radius floor
    // The CHAMFER arms clamp against 0.0, not SDF_SMOOTH_RADIUS_MIN: a zero bevel must be exactly a hard seam, and
    // folding the smooth floor in would shift the bevel plane. Do not merge the two clamps.
    float chamfer = max(smoothRadius, 0.0);

    switch (blendOp) {
        case SDF_BLEND_SMOOTH_UNION:        result = blendSmoothUnion(current, candidate, smoothK); break;
        case SDF_BLEND_SUBTRACTION:         result = max(current, -candidate); break;
        case SDF_BLEND_INTERSECTION:        result = max(current, candidate); break;
        case SDF_BLEND_XOR:                 result = max(min(current, candidate), -max(current, candidate)); break;
        case SDF_BLEND_SMOOTH_INTERSECTION: result = -blendSmoothUnion(-current, -candidate, smoothK); break;
        case SDF_BLEND_SMOOTH_SUBTRACTION:  result = -blendSmoothUnion(candidate, -current, smoothK); break;
        // Chamfered (45-degree bevel) seams (hg_sdf fOp*Chamfer): the bevel plane is (a +- r + b) * sqrt(1/2). Union
        // bevels the near corner, intersection/subtraction the far one; SUBTRACTION is the intersection of `current`
        // with -candidate. The bevel plane's gradient reaches sqrt(2) at a FLAT/near-parallel seam (1 at a perpendicular
        // one, 0 at an acute one), hence the chamfer step clamp in SdfProgram.AnalyzeLipschitz.
        case SDF_BLEND_CHAMFER_UNION:        result = min(min(current, candidate), ((current + candidate - chamfer) * SDF_SQRT_HALF)); break;
        case SDF_BLEND_CHAMFER_INTERSECTION: result = max(max(current, candidate), ((current + candidate + chamfer) * SDF_SQRT_HALF)); break;
        case SDF_BLEND_CHAMFER_SUBTRACTION:  result = max(max(current, -candidate), ((current - candidate + chamfer) * SDF_SQRT_HALF)); break;
    }

    return result;
}

// === Forward-mode gradient dual (analytic normals) ===================================================================
// mapGradCore below is the HIT-ONLY dual twin of mapCore: it walks the SAME instruction stream and, alongside the
// scalar distance, carries the WORLD-space gradient of the accumulated field so the surface normal is analytic —
// forward-mode chain rule through the runtime transforms, NOT the baked Lipschitz scalars (those bound the STEP; these
// propagate the DERIVATIVE). It runs once per lit hit pixel, replacing the 4-tap finite-difference calculateNormal;
// the march stays scalar (mapCore). The dual eval costs ~2x a scalar one, paid once per hit — never in the march loop.
//
// TRANSPORT STATE. The gradient is created only at a SHAPE (a primitive's local gradient), so between shapes there is
// no gradient to move; what the point ops build up instead is the JACOBIAN of the transform chain, carried as its three
// COLUMNS jx = d(localPosition)/d(worldPosition.x), jy = .../.y, jz = .../.z (column j of the matrix J with
// J[i][j] = d(lp_i)/d(w_j)). A point op with local point-Jacobian A (lp_new = A*lp_old + b) updates each column by
// A*column (sdfApplyJacobian applies A given as its three ROWS, or the op's own orthogonal map is applied to the
// vector directly). RESET restores jx/jy/jz to identity exactly as it restores localPosition. At a SHAPE the local
// gradient g maps to world by worldGrad_j = dot(g, column_j) = (dot(jx,g), dot(jy,g), dot(jz,g)), times the distanceScale
// (Scale/LogSphere's metric factor multiplies the gradient exactly as it multiplies the distance). stepScale is NOT
// applied — it is a uniform positive scale on the whole returned field, and normalize() at the consumer cancels it, so
// the dual ignores it entirely for normal purposes.

#define SDF_SHAPE_GRAD_EPSILON 0.0006  // shape-LOCAL FD offset for the exotic-primitive gradient fallback (one primitive, no chain)

float3 sdfSafeNormalize(float3 v) {
    return (v * rsqrt(max(dot(v, v), 1.0e-24)));
}

// Applies a 3x3 point-Jacobian A (given as its three ROWS ax/ay/az) to each carried Jacobian column vector. Each new
// vector is a full float3 built from dot products of the OLD vector, so the in/out aliasing is safe.
void sdfApplyJacobian(float3 ax, float3 ay, float3 az, inout float3 jx, inout float3 jy, inout float3 jz) {
    jx = float3(dot(ax, jx), dot(ay, jx), dot(az, jx));
    jy = float3(dot(ax, jy), dot(ay, jy), dot(az, jy));
    jz = float3(dot(ax, jz), dot(ay, jz), dot(az, jz));
}

// --- analytic LEAF gradients (the cheap majority; exact for a metric SDF) ---
// The box: outside, the gradient is the outward direction of the exterior offset, signed per octant; inside, it points
// along the nearest face's axis. cornerRadius rounds the surface but not the gradient direction.
float3 sdfBoxGradient(float3 p, float3 halfExtents, float cornerRadius) {
    float3 s = float3((p.x < 0.0) ? -1.0 : 1.0, (p.y < 0.0) ? -1.0 : 1.0, (p.z < 0.0) ? -1.0 : 1.0);
    float3 q = (abs(p) - (halfExtents - cornerRadius));
    float m = max(q.x, max(q.y, q.z));

    if (m > 0.0) {
        return (s * sdfSafeNormalize(max(q, 0.0)));
    }

    float3 axis = float3((q.x >= m) ? 1.0 : 0.0, (q.y >= m) ? 1.0 : 0.0, (q.z >= m) ? 1.0 : 0.0);

    return (s * sdfSafeNormalize(axis));
}
float3 sdfTorusGradient(float3 p, float major, float minor) {
    float lxz = length(p.xz);
    float2 q = float2((lxz - major), p.y);
    float lq = max(length(q), 1.0e-12);
    float2 radial = ((lxz > 1.0e-12) ? (p.xz / lxz) : float2(0.0, 0.0));

    return float3(((q.x / lq) * radial.x), (q.y / lq), ((q.x / lq) * radial.y));
}
float3 sdfCapsuleGradient(float3 p, float3 endpoint, float inverseLengthSquared) {
    float h = clamp((dot(p, endpoint) * inverseLengthSquared), 0.0, 1.0);

    return sdfSafeNormalize(p - (h * endpoint));
}
float3 sdfCylinderGradient(float3 p, float radius, float halfHeight) {
    float lxz = length(p.xz);
    float2 radial = ((lxz > 1.0e-12) ? (p.xz / lxz) : float2(0.0, 0.0));
    float ySign = ((p.y < 0.0) ? -1.0 : 1.0);
    float2 d = (float2(lxz, abs(p.y)) - float2(radius, halfHeight));

    if (max(d.x, d.y) <= 0.0) {
        return ((d.x > d.y) ? float3(radial.x, 0.0, radial.y) : float3(0.0, ySign, 0.0));
    }

    float2 e = max(d, 0.0);
    float le = max(length(e), 1.0e-12);
    float2 n = (e / le);

    return float3((n.x * radial.x), (n.y * ySign), (n.x * radial.y));
}
// Shape-LOCAL 4-tap tetrahedron FD for the exotic primitives (ellipsoid/vesica/roundcone/the 2D-lifted family): a tight
// difference of just that one primitive's SDF in folded space, no transform chain — so it fixes the op-CHAIN
// propagation (the real win) with a cheap, cancellation-light leaf. Same isotropic tetrahedron the world normal uses.
float3 sdfShapeGradientFd(uint shapeType, float3 p, float4 data0, float4 data1) {
    const float2 k = float2(1.0, -1.0);
    const float e = SDF_SHAPE_GRAD_EPSILON;

    return sdfSafeNormalize(
        (k.xyy * evaluateShape(shapeType, (p + (k.xyy * e)), data0, data1)) +
        (k.yyx * evaluateShape(shapeType, (p + (k.yyx * e)), data0, data1)) +
        (k.yxy * evaluateShape(shapeType, (p + (k.yxy * e)), data0, data1)) +
        (k.xxx * evaluateShape(shapeType, (p + (k.xxx * e)), data0, data1)));
}
// The gradient companion to evaluateShape (same dispatch): analytic for the cheap majority, shape-local FD for the rest.
float3 evaluateShapeGradient(uint shapeType, float3 p, float4 data0, float4 data1) {
    switch (shapeType) {
        case SDF_SHAPE_SPHERE:      return sdfSafeNormalize(p);
        // The plane's gradient is its (host-normalized) normal, exactly.
        case SDF_SHAPE_PLANE:       return data0.xyz;
        case SDF_SHAPE_BOX:
        case SDF_SHAPE_SCREEN_SLAB: return sdfBoxGradient(p, data0.xyz, data0.w);
        case SDF_SHAPE_TORUS:       return sdfTorusGradient(p, data0.x, data0.y);
        case SDF_SHAPE_CAPSULE:     return sdfCapsuleGradient(p, data0.xyz, data1.y);
        case SDF_SHAPE_CYLINDER:    return sdfCylinderGradient(p, data0.x, data0.y);
        default:                    return sdfShapeGradientFd(shapeType, p, data0, data1);
    }
}
// The gradient-carrying twin of blendShape: reproduces its distance branch-for-branch AND propagates the world-space
// surface gradient. HARD blends SELECT the winning branch's gradient (negated where the distance formula negates the
// candidate — the subtraction sign bug lives here). SMOOTH blends LERP the two gradients by the SAME h weight the
// distance lerp uses; the exact smin gradient carries an extra tangential term, but the normal normalizes and the SOTA
// survey accepts the standard lerp approximation, so it is used here. CHAMFER blends select the winning one of their
// three terms (the bevel term's gradient is the summed/differenced unit gradients times sqrt(1/2)).
void blendShapeDual(float current, float3 currentGrad, float candidate, float3 candidateGrad, uint blendOp, float smoothRadius, out float outDist, out float3 outGrad) {
    float smoothK = max(smoothRadius, SDF_SMOOTH_RADIUS_MIN);
    float chamfer = max(smoothRadius, 0.0);

    outDist = min(current, candidate);                                  // SDF_BLEND_UNION (the default)
    outGrad = ((candidate < current) ? candidateGrad : currentGrad);

    switch (blendOp) {
        case SDF_BLEND_SMOOTH_UNION: {
            float h = clamp((0.5 + ((0.5 * (candidate - current)) / smoothK)), 0.0, 1.0);
            outDist = blendSmoothUnion(current, candidate, smoothK);
            outGrad = lerp(currentGrad, candidateGrad, (1.0 - h));
            break;
        }
        case SDF_BLEND_SUBTRACTION: {
            outDist = max(current, -candidate);
            outGrad = (((-candidate) > current) ? (-candidateGrad) : currentGrad);
            break;
        }
        case SDF_BLEND_INTERSECTION: {
            outDist = max(current, candidate);
            outGrad = ((candidate > current) ? candidateGrad : currentGrad);
            break;
        }
        case SDF_BLEND_XOR: {
            float mn = min(current, candidate);
            float mx = max(current, candidate);
            outDist = max(mn, -mx);

            if ((-mx) > mn) {
                outGrad = ((current > candidate) ? (-currentGrad) : (-candidateGrad));
            }
            else {
                outGrad = ((current < candidate) ? currentGrad : candidateGrad);
            }

            break;
        }
        case SDF_BLEND_SMOOTH_INTERSECTION: {
            float h = clamp((0.5 + ((0.5 * (current - candidate)) / smoothK)), 0.0, 1.0);
            outDist = -blendSmoothUnion(-current, -candidate, smoothK);
            outGrad = lerp(currentGrad, candidateGrad, (1.0 - h));
            break;
        }
        case SDF_BLEND_SMOOTH_SUBTRACTION: {
            float h = clamp((0.5 + ((0.5 * ((-current) - candidate)) / smoothK)), 0.0, 1.0);
            outDist = -blendSmoothUnion(candidate, -current, smoothK);
            outGrad = lerp((-candidateGrad), currentGrad, (1.0 - h));
            break;
        }
        case SDF_BLEND_CHAMFER_UNION: {
            float bevel = ((current + candidate - chamfer) * SDF_SQRT_HALF);
            outDist = min(min(current, candidate), bevel);
            outGrad = currentGrad;
            if (candidate < current) { outGrad = candidateGrad; }
            if (bevel <= min(current, candidate)) { outGrad = ((currentGrad + candidateGrad) * SDF_SQRT_HALF); }
            break;
        }
        case SDF_BLEND_CHAMFER_INTERSECTION: {
            float bevel = ((current + candidate + chamfer) * SDF_SQRT_HALF);
            outDist = max(max(current, candidate), bevel);
            outGrad = currentGrad;
            if (candidate > current) { outGrad = candidateGrad; }
            if (bevel >= max(current, candidate)) { outGrad = ((currentGrad + candidateGrad) * SDF_SQRT_HALF); }
            break;
        }
        case SDF_BLEND_CHAMFER_SUBTRACTION: {
            float bevel = ((current - candidate + chamfer) * SDF_SQRT_HALF);
            outDist = max(max(current, -candidate), bevel);
            outGrad = currentGrad;
            if ((-candidate) > current) { outGrad = -candidateGrad; }
            if (bevel >= max(current, -candidate)) { outGrad = ((currentGrad - candidateGrad) * SDF_SQRT_HALF); }
            break;
        }
    }
}
// The one-deep scoped-accumulator SAVE SLOT for the dual walk. A STRUCT carrying {distance, material, gradient} even at
// depth 1 (SDF_MAX_FIELD_SCOPE_DEPTH): raising the depth and adding this gradient lane must be ONE migration to an
// indexed stack, not two — so the struct exists now (mirrors mapCore's savedFieldDistance/savedFieldMaterial pair plus
// the new gradient lane the dual needs).
struct SdfFieldSave {
    float distance;
    int material;
    float3 gradient;
};

// The merge cursors' "exhausted" sentinel: no segment remains on that side (an impossible segment-directory index).
// Numerically equal to SDF_INSTANCE_MASK_ALL by coincidence only — the two name unrelated contracts.
#define SDF_SEGMENT_NONE 0xFFFFFFFFu

// Advances the visible-instance cursor to the next SET BIT of the caller's per-tile mask (ascending instance index,
// so ascending segment index — instances' segment ranges are disjoint and ascend with declaration order) and loads
// that instance's [segmentFirst, segmentEnd) directory range; both outputs are SDF_SEGMENT_NONE when no visible
// instance remains. `maskWordIndex`/`maskWordBits` carry the enumeration across calls: the current word index and
// its remaining (unconsumed) bits — the caller initializes them to (0xFFFFFFFFu, 0u), so the word-fetch loop below
// wraps onto word 0 on the first call. An empty range (an instance declared around zero instructions) is skipped,
// never surfaced.
void sdfNextVisibleInstanceRange(uint instanceMaskBase, uint instanceOffset, uint instanceCount, inout uint maskWordIndex, inout uint maskWordBits, out uint segmentFirst, out uint segmentEnd) {
    uint wordCount = sdfInstanceMaskWordCount(instanceCount);

    [loop]
    for (;;) {
        [loop]
        while ((maskWordBits == 0u) && ((maskWordIndex + 1u) < wordCount)) {
            maskWordIndex++;
            maskWordBits = sdfInstanceMaskWord(instanceMaskBase, maskWordIndex, instanceCount);
        }

        if (maskWordBits == 0u) {
            segmentFirst = SDF_SEGMENT_NONE;
            segmentEnd = SDF_SEGMENT_NONE;
            return;
        }

        uint instanceIndex = ((maskWordIndex << 5u) + firstbitlow(maskWordBits));

        maskWordBits &= (maskWordBits - 1u);

        uint entryBase = sdfInstanceEntryOffset(instanceOffset, instanceIndex);
        // A PARKED instance (a reserved-pool slot with no live content this rebuild) packs a negative-radius sentinel in
        // its bound (SdfProgram.ParkedBoundRadius): it contributes nothing to any ray, so skip its whole segment range —
        // this is what makes the beam prepass's own cone march (which evaluates under the SDF_INSTANCE_MASK_ALL sentinel,
        // so it would otherwise walk every parked pool slot's segments) cost track LIVE content, not reserved capacity.
        // Stage 1 never reaches here for a parked instance (its per-tile mask bit is always 0), so this only fires on the
        // all-visible cone-march / full-eval paths — exactly the ones that lacked the beam's per-tile skip.
        float parkedRadius = asfloat(sdfWords[entryBase]).w;

        if (parkedRadius < 0.0) {
            continue;
        }

        uint4 instanceMeta = sdfWords[entryBase + 1u];

        if (instanceMeta.z < instanceMeta.w) {
            segmentFirst = instanceMeta.z;
            segmentEnd = instanceMeta.w;
            return;
        }
    }
}

// The generic evaluator both map() (every existing consumer; a zero-instance program's ONLY path) and mapMasked()
// (the world render path's Stage 1) share. A ZERO-INSTANCE program takes the linear fast path — every segment in
// directory order, the pre-instancing interpreter verbatim (`hasInstances` is read once, per-program-uniform, so the
// branch never diverges within one program's invocations). An INSTANCED program merges the WORLD-SEGMENT list
// (always evaluated) with the VISIBLE instances' segment ranges by ascending segment index — the flat stream's
// order, so order-dependent blend ops (SmoothSubtraction, Xor, ...) compose exactly as an unmasked walk would — and
// a call costs O(world segments + visible instances' segments): a masked-out instance's segments are never loaded,
// never owner-tested, never bound-tested. With the SDF_INSTANCE_MASK_ALL sentinel every instance enumerates as
// visible (validity-masked to the real instance count), so the merge degenerates to the full ascending walk.
SdfHit mapCore(float3 worldPosition, uint instanceMaskBase) {
    uint4 header = sdfWords[0];
    uint instructionCount = header.x;
    uint dataOffset = header.z;
    // The host-baked bounding-sphere tables sit right after the materials (2 uint4 per material, header.y of them):
    // the per-shape table (2 uint4 per instruction), then the segment directory (a count vector, then 2 uint4 per
    // segment), then the instance directory, then the world-segment list. KEEP IN SYNC with SdfProgram.PackBounds /
    // PackInstances / PackWorldSegments.
    uint boundsOffset = (header.w + (2u * header.y));
    uint segmentOffset = (boundsOffset + (2u * header.x));
    uint4 segmentHeader = sdfWords[segmentOffset];
    uint segmentCount = segmentHeader.x;
    // The per-PROGRAM Lipschitz STEP SCALE (1/L; see sdfStepScale). Applied as ONE multiply on the FINAL returned
    // distance below, it clamps sphere-tracing steps to the field's true rate of change so a non-1-Lipschitz warp
    // (twist/bend/chamfer/displace/domain-warp) or an eccentric ellipsoid cannot overstep and hole. DISTINCT from the
    // per-sample distanceScale below (the true DOMAIN corrections: Scale's min-axis factor and LogSphere's r/density
    // factor) — that one is applied per candidate mid-walk; this is the field-preserving step clamp on the final min.
    // Never merge the two.
    float stepScale = asfloat(segmentHeader.y);
    stepScale = ((stepScale > 0.0) ? stepScale : 1.0);
    uint instanceOffset = sdfInstanceDirectoryOffsetFrom(segmentOffset, segmentCount);
    uint instanceCount = sdfWords[instanceOffset].x;
    uint worldSegmentOffset = (instanceOffset + 1u + (2u * instanceCount));
    bool hasInstances = (instanceCount != 0u);

    // The merge cursors (instanced programs only): the next always-evaluated WORLD segment, and the visible-instance
    // enumeration (current mask word + remaining bits, the in-progress instance's [instanceSegment, instanceSegmentEnd)
    // range). SDF_SEGMENT_NONE = exhausted on either side. The mask cursor starts "before word 0" (0xFFFFFFFFu, 0u),
    // so sdfNextVisibleInstanceRange's own word-fetch loop wraps onto word 0 on its first call.
    uint linearCursor = 0u;
    uint worldCursor = 0u;
    uint worldCount = 0u;
    uint worldNext = SDF_SEGMENT_NONE;
    uint maskWordIndex = 0xFFFFFFFFu;
    uint maskWordBits = 0u;
    uint instanceSegment = SDF_SEGMENT_NONE;
    uint instanceSegmentEnd = SDF_SEGMENT_NONE;

    if (hasInstances) {
        worldCount = sdfWords[worldSegmentOffset].x;
        worldNext = ((0u < worldCount) ? sdfWords[worldSegmentOffset + 1u].x : SDF_SEGMENT_NONE);
        sdfNextVisibleInstanceRange(instanceMaskBase, instanceOffset, instanceCount, maskWordIndex, maskWordBits, instanceSegment, instanceSegmentEnd);
    }

    float3 localPosition = worldPosition;
    // distanceScale is the PER-SAMPLE, PER-CANDIDATE domain correction: SDF_OP_SCALE folds its min-axis factor in here
    // and SDF_OP_LOG_SPHERE its r/density shell factor, composing multiplicatively. It multiplies EACH shape candidate
    // before blending, so it participates in the min, the material winner, and the field ops. This is a SEPARATE channel
    // from the per-program stepScale above — that one clamps only the FINAL returned distance to keep the marcher's
    // steps 1-Lipschitz-safe and never touches a candidate mid-walk. Keep the two distinct.
    float distanceScale = 1.0;
    // The texturing half of an active wallpaper fold: the cell key times the fold's material stride, added to the
    // material id of later shape wins in the chain (never to the screen sentinel). Reset with the chain.
    int parityMaterialDelta = 0;
    SdfHit result;

    result.distance = SDF_FAR_DISTANCE;
    result.material = 0;

    // The one-deep SCOPED-ACCUMULATOR slot (SDF_OP_PUSH_FIELD/POP_FIELD): PUSH saves the parent accumulator here and
    // reseeds `result`; POP composes the scope's `result` back into this saved value. This is a single NON-INDEXED pair,
    // so it holds exactly ONE parent — the builder's validator rejects nesting past SDF_MAX_FIELD_SCOPE_DEPTH == 1, and
    // raising that depth would require turning this pair into an indexed array with push/pop-by-depth stack semantics
    // (SDF_MAX_FIELD_SCOPE_DEPTH is documentation only — nothing here reads it). Untouched by a scope-free program, so
    // its codegen and render stay byte-identical.
    float savedFieldDistance = SDF_FAR_DISTANCE;
    int savedFieldMaterial = 0;

    // The OUTER loop walks chain segments (the stream split at ResetPoints), the inner loop interprets a segment's
    // instructions exactly as before — the zero-instance linear walk visits every segment in directory order, and
    // the instanced merge visits the world segments plus the visible instances' ranges in the SAME ascending order,
    // so with no skips the execution is the reference interpreter verbatim. The EXACT Union early-out: when a
    // segment's combined bounding sphere cannot beat the running union minimum, the whole chain is skipped —
    // transforms included (the state they would have produced is provably dead: every later segment begins with a
    // ResetPoint). The skipped candidates' true distances are >= the sphere's lower bound >= the running minimum, so
    // the min, the material winner, and every pixel are bit-identical to full evaluation (even a backend-divergent
    // skip DECISION cannot diverge a pixel). A DYNAMIC sphere's center is offset + the entity slot's per-frame
    // position — NO quaternion rotate (orientation is folded into the host-baked radius).
    [loop]
    for (;;) {
        // Select the next segment. Zero-instance: the plain linear counter (independent of any loaded value, so the
        // per-segment sphere loads pipeline across iterations). Instanced: the two-pointer merge — world segments
        // never fall inside an instance's range, so comparing the next world segment against the next owned segment
        // yields the globally ascending order the blend ops require.
        uint segment;

        if (!hasInstances) {
            if (linearCursor >= segmentCount) {
                break;
            }

            segment = linearCursor++;
        } else if (worldNext < instanceSegment) {
            segment = worldNext;
            worldCursor++;
            worldNext = ((worldCursor < worldCount) ? sdfWords[worldSegmentOffset + 1u + worldCursor].x : SDF_SEGMENT_NONE);
        } else if (instanceSegment < instanceSegmentEnd) {
            segment = instanceSegment++;

            if (instanceSegment == instanceSegmentEnd) {
                sdfNextVisibleInstanceRange(instanceMaskBase, instanceOffset, instanceCount, maskWordIndex, maskWordBits, instanceSegment, instanceSegmentEnd);
            }
        } else {
            break;
        }

        uint4 segmentMeta = sdfWords[segmentOffset + 1u + (2u * segment) + 1u];

        [branch]
        if (segmentMeta.x != SDF_BOUND_NONE) {
            float4 segmentBound = asfloat(sdfWords[segmentOffset + 1u + (2u * segment)]);
            float3 boundCenter = segmentBound.xyz;
            bool boundReady = (segmentMeta.x == SDF_BOUND_STATIC);

#ifdef SDF_DYNAMIC_TRANSFORMS
            if (segmentMeta.x == SDF_BOUND_DYNAMIC) {
                boundCenter += sdfDynamicTransforms[2u * segmentMeta.y].xyz;
                boundReady = true;
            }
#endif

            // A dynamic sphere without the dynamic-transform buffer (non-world paths) stays unready: evaluate fully.
            // Squared-distance form of length(p - c) - radius >= runningMin (the max keeps a negative running
            // minimum, deep inside geometry, from flipping the comparison's sign).
            if (boundReady) {
                float3 toCenter = (worldPosition - boundCenter);
                float clearance = max((result.distance + segmentBound.w), 0.0);

                if (dot(toCenter, toCenter) >= (clearance * clearance)) {
                    continue;
                }
            }
        }

        [loop]
        for (uint index = segmentMeta.z; (index < segmentMeta.w); index++) {
            uint4 instructionHeader = sdfWords[1u + index];
            uint op = instructionHeader.x;
            float4 data0 = asfloat(sdfWords[dataOffset + (2u * index)]);
            float4 data1 = asfloat(sdfWords[dataOffset + (2u * index) + 1u]);

            // The SHARED BLEND TAIL's inputs. SHAPE and POP_FIELD both feed it a candidate (already in world units) plus
            // a blend/material/smooth, then it runs the ONE material-winner + blendShape below — so a POP reuses SHAPE's
            // tail instead of a second copy of the ten-way blend switch (the whole cost saving; accumulator plan). Every
            // other op leaves composePending false, so a scope-free program's SHAPE path computes the identical floats it
            // did before (byte-identical render) — the tail simply moved a few lines down.
            bool composePending = false;
            float composeCandidate = SDF_FAR_DISTANCE;
            uint composeBlend = SDF_BLEND_UNION;
            int composeMaterial = 0;
            float composeSmooth = 0.0;

            switch (op) {
                case SDF_OP_RESET: {
                    localPosition = worldPosition;
                    distanceScale = 1.0;
                    parityMaterialDelta = 0;
                    break;
                }
                case SDF_OP_TRANSLATE: {
                    localPosition -= data0.xyz;
                    break;
                }
                case SDF_OP_ROTATE: {
                    localPosition = rotatePointByInverseQuaternion(localPosition, data0);
                    break;
                }
                // data0.xyz = |scale|, pre-clamped away from 0; data0.w = its min axis. BOTH HOST-BAKED by
                // SdfProgramBuilder.Scale (the abs/max/min were 8 dx.op per evaluation). The min-axis factor is the
                // conservative correction for a NON-uniform scale: f(S^-1 p) * min(s) is 1-Lipschitz because
                // |S^-1| <= 1/min(s), so it can only UNDERESTIMATE true distance — never overstep.
                case SDF_OP_SCALE: {
                    localPosition /= data0.xyz;
                    distanceScale *= data0.w;
                    break;
                }
                // Log-spherical DOMAIN WARP: data0.x = w (= ln shellRatio, HOST-BAKED), data0.y = twist (radians/shell),
                // data0.z = 1/w (HOST-BAKED). Fold the RADIAL log-coordinate to the NEAREST shell (round, exactly like
                // SDF_OP_REPEAT): a translation along log(radius) becomes a uniform Cartesian SCALING, tiling space into
                // self-similar "Droste" shells. The half-cell bound the nearest-shell round gives is what makes the
                // exp(w/2) Lipschitz factor (SdfProgram.AnalyzeLipschitz) a sound step clamp for the over-relaxed march.
                case SDF_OP_LOG_SPHERE: {
                    float logRadius = log(max(length(localPosition), SDF_LOGSPHERE_MIN_RADIUS));
                    float shell = round(logRadius * data0.z);   // nearest shell index k
                    float shellScale = exp(shell * data0.x);    // exp(k*w) = the shell's Cartesian scale = r / rFolded

                    localPosition /= shellScale;                // fold every shell onto the prototype

                    // Optional Droste spiral: rotate each shell about Z by k*twist. UNCONDITIONAL (twist == 0 -> cos 0 = 1,
                    // sin 0 = 0, an exact identity on both backends) so no divergent branch re-rolls codegen. A rotation is
                    // an ISOMETRY: it neither touches distanceScale nor contributes to the Lipschitz factor. No atan2, so
                    // the Z axis is a plain rotation fixed line, NOT a polar-pinch singularity.
                    float spinAngle = (shell * data0.y);
                    float spinCos = cos(spinAngle);
                    float spinSin = sin(spinAngle);

                    localPosition.xy = float2(
                        ((spinCos * localPosition.x) - (spinSin * localPosition.y)),
                        ((spinSin * localPosition.x) + (spinCos * localPosition.y)));

                    // The r/density metric correction: within a shell the fold is a similarity that shrank space by
                    // 1/shellScale, so evaluateShape(localPosition) UNDERESTIMATES true distance by 1/shellScale — multiply
                    // the candidate back by shellScale. This rides the SAME per-candidate distanceScale channel SDF_OP_SCALE
                    // writes (applied per candidate below), composing multiplicatively when a Scale is also on the chain.
                    // NEVER stepScale (that clamps the FINAL distance once, and cannot per-candidate-correct a mixed chain).
                    distanceScale *= shellScale;
                    break;
                }
#ifdef SDF_DYNAMIC_TRANSFORMS
                case SDF_OP_TRANSFORM_DYNAMIC: {
                    // A rigid transform sourced from a per-frame buffer slot (data0.x): place the shape at the slot's world
                    // position + orientation by moving the sample point into the shape's local frame (translate then
                    // inverse-rotate), exactly like an immediate Translate + Rotate would.
                    uint dynamicSlot = (uint)data0.x;
                    float4 dynamicPosition = sdfDynamicTransforms[(2u * dynamicSlot)];
                    float4 dynamicOrientation = sdfDynamicTransforms[((2u * dynamicSlot) + 1u)];
                    localPosition = rotatePointByInverseQuaternion((localPosition - dynamicPosition.xyz), dynamicOrientation);
                    break;
                }
#endif
                case SDF_OP_REPEAT: {
                    // data0 arrives pre-clamped and data1 = 1/spacing, both HOST-BAKED by SdfProgramBuilder.Repeat.
                    localPosition -= (data0.xyz * round(localPosition * data1.xyz));
                    break;
                }
                case SDF_OP_REPEAT_LIMITED: {
                    // data0.xyz = spacing, pre-clamped away from 0 exactly as SDF_OP_REPEAT's is (HOST-BAKED by
                    // SdfProgramBuilder.RepeatLimited); data1.xyz = the per-axis cell limit. Unlike Repeat there is no
                    // free lane for 1/spacing, so the divide stays — a per-instruction-uniform divisor lowers to one
                    // reciprocal anyway.
                    localPosition -= (data0.xyz * clamp(round(localPosition / data0.xyz), -data1.xyz, data1.xyz));
                    break;
                }
                // Stochastic domain-repeat fold (KEEP IN SYNC with SdfProgramBuilder.CellJitter): tile space like
                // SDF_OP_REPEAT, then per cell displace by a hashed offset, optionally tumble (a hashed rotation), and
                // optionally recolor by a hashed material variant. data0.xyz = spacing (pre-clamped), data0.w = jitter
                // (peak-to-peak displacement), data1.xyz = 1/spacing (HOST-BAKED), data1.w = tumble in [0,1].
                // header.y = seed, header.z = SDF_NOISE_* flavor (how the POSITION offset r0 is distributed; White = 0 is
                // the byte-identical default), header.w = materialVariants (0 = geometric only). The hash is INTEGER-ONLY (sdfPcg3d
                // on the two's-complement cell index xored with the seed), so cell decisions are bit-identical across
                // both DXC targets. Displacement and tumble are BOTH isometries — distanceScale is untouched, and the
                // only Lipschitz contribution is the jitter half-amplitude (SdfProgram.AnalyzeLipschitz).
                case SDF_OP_CELL_JITTER: {
                    float3 cell = round(localPosition * data1.xyz);   // data1.xyz = 1/spacing (host-baked)
                    // asuint(int3(cell)) is well-defined two's-complement for a negative rounded index on both backends.
                    uint3 seed = uint3(instructionHeader.y, (instructionHeader.y * SDF_HASH_STREAM_A), (instructionHeader.y * SDF_HASH_STREAM_B));
                    uint3 key = (asuint(int3(cell)) ^ seed);
                    uint3 h0 = sdfPcg3d(key);   // still used by the material-variant apply below (every flavor)

                    // The POSITION offset r0, shaped by the Blend-lane noise flavor (KEEP IN SYNC with
                    // Puck.SdfVm.SdfNoiseFlavor). Only r0 changes. Every flavor stays in [0, 1] per axis — CLOSED at 1,
                    // because (float)0xFFFFFFFFu rounds up to 2^32 — so (r0 - 0.5) * jitter holds within +-jitter/2 per
                    // axis (White's bound) and the AnalyzeLipschitz reach term is unchanged for all three.
                    uint noiseFlavor = instructionHeader.z;
                    float3 r0;
                    if (noiseFlavor == SDF_NOISE_BLUE) {
                        // Roberts' R3 low-discrepancy lattice: alpha_i = round(2^32 / phi3^i) for phi3 = 1.2207440846057596
                        // (the real root of x^4 = x + 1), as a fixed-point rank-1 lattice on the integer cell index. The uint
                        // mul-add wraps mod 2^32 = the fractional part, so it is BIT-IDENTICAL across DXC's SPIR-V and DXIL
                        // (a float frac would diverge). The three alphas are ROTATED across the axes so the offset components
                        // decorrelate. "Blue-ish" low-discrepancy (de-clumped), NOT true isotropic blue noise. The seed folds
                        // in ADDITIVELY (a lattice translation preserves low-discrepancy, unlike an xor), so the field varies
                        // with seed AND a non-zero seed breaks the circulant's (1,1,1) main-diagonal degeneracy.
                        uint3 uc = (asuint(int3(cell)) + seed);
                        uint bx = ((uc.x * SDF_R3_ALPHA1) + (uc.y * SDF_R3_ALPHA2) + (uc.z * SDF_R3_ALPHA3));
                        uint by = ((uc.x * SDF_R3_ALPHA2) + (uc.y * SDF_R3_ALPHA3) + (uc.z * SDF_R3_ALPHA1));
                        uint bz = ((uc.x * SDF_R3_ALPHA3) + (uc.y * SDF_R3_ALPHA1) + (uc.z * SDF_R3_ALPHA2));
                        r0 = (float3(bx, by, bz) * SDF_INV_2POW32);
                    } else if (noiseFlavor == SDF_NOISE_GAUSSIAN) {
                        // Central-limit: the mean of 3 decorrelated hashed uniforms per axis — a Bates(3) distribution, i.e.
                        // bell-SHAPED and clustered toward the cell centre, not a true Gaussian (it has compact support, which
                        // is exactly what keeps the +-jitter/2 bound). FLOAT-averaged, not uint-summed, to avoid 32-bit overflow.
                        uint3 g1 = sdfPcg3d(key ^ SDF_HASH_STREAM_A);
                        uint3 g2 = sdfPcg3d(key ^ SDF_HASH_STREAM_B);
                        r0 = (((float3)h0 + (float3)g1 + (float3)g2) * (SDF_INV_2POW32 / 3.0));
                    } else {
                        // SDF_NOISE_WHITE (0, the default): the plain independent PCG3D uniform.
                        r0 = ((float3)h0 * SDF_INV_2POW32);
                    }

                    localPosition -= (data0.xyz * cell);               // the SDF_OP_REPEAT fold
                    localPosition -= ((r0 - 0.5) * data0.w);           // per-cell position jitter (data0.w peak-to-peak)

                    // Tumble (an isometry): guarded by amplitude so a zeroed op stays an EXACT identity (no rotate, no
                    // divergent codegen). The hashed axis is uniform on the sphere; the angle is |r1.z| * tumble * pi.
                    if (data1.w > 0.0) {
                        uint3 h1 = sdfPcg3d(key ^ SDF_HASH_TUMBLE);
                        float3 r1 = ((float3)h1 * SDF_INV_2POW32);
                        float zz = ((2.0 * r1.x) - 1.0);
                        float rr = sqrt(max(0.0, (1.0 - (zz * zz))));
                        float phi = (SDF_TAU * r1.y);
                        float3 axis = float3((rr * cos(phi)), (rr * sin(phi)), zz);   // uniform on the unit sphere
                        float angle = ((r1.z * data1.w) * SDF_PI);
                        float ha = (0.5 * angle);
                        float sa = sin(ha);
                        float4 q = float4((axis * sa), cos(ha));       // (x,y,z,w) matches SDF_OP_ROTATE's layout
                        localPosition = rotatePointByInverseQuaternion(localPosition, q);
                    }

                    // Per-cell material variant (same channel WallpaperFold recolors through): a hashed palette row in
                    // 0..variants-1, added to a later shape's material by the SDF_OP_SHAPE parityMaterialDelta apply.
                    if (instructionHeader.w != 0u) {
                        parityMaterialDelta = (int)(h0.z % instructionHeader.w);
                    }

                    break;
                }
                // Angular domain-repeat fold (KEEP IN SYNC with SdfProgramBuilder.RepeatPolar): fold the plane
                // perpendicular to the axis into `count` equal sectors so the prototype repeats ROTATIONALLY around it.
                // data0.x = sector angle 2*pi/count (HOST-BAKED), data0.y = count/(2*pi) = 1/angle (HOST-BAKED),
                // data0.z = count, data0.w = 1/count (HOST-BAKED). header.y = axis (SDF_POLAR_AXIS_*), header.z = mirror
                // flag (reflect each sector across its bisector — the kaleidoscope fold), header.w = materialStride
                // (per-sector palette stride; 0 = geometric only). A rotation into the base sector (and, when mirrored, a
                // reflection) about the axis — BOTH isometries, so distances are preserved (factor 1, no step clamp; like
                // SDF_OP_REPEAT the prototype must stay clear of the sector walls). atan2/floor are floats, so a
                // sector-seam pixel carries the usual +-1 LSB warp noise (geometry only; the per-sector material can flip
                // at a seam exactly as SDF_OP_WALLPAPER_FOLD's can). At the axis (r == 0) atan2(0,0) = 0 keeps it a no-op.
                case SDF_OP_REPEAT_POLAR: {
                    uint polarAxis = instructionHeader.y;
                    // The fold plane (u,v) perpendicular to the axis; the axial coordinate is untouched.
                    float2 pv;
                    if (polarAxis == SDF_POLAR_AXIS_X) { pv = localPosition.yz; }
                    else if (polarAxis == SDF_POLAR_AXIS_Z) { pv = localPosition.xy; }
                    else { pv = localPosition.xz; }   // SDF_POLAR_AXIS_Y (default): the XZ ground plane

                    float sectorAngle = data0.x;
                    float a = (atan2(pv.y, pv.x) + (0.5 * sectorAngle));
                    float r = length(pv);
                    float sector = floor(a * data0.y);                         // data0.y = 1/angle
                    a = ((a - (sectorAngle * sector)) - (0.5 * sectorAngle));  // a in [-angle/2, angle/2)
                    if (instructionHeader.z != 0u) { a = abs(a); }             // mirror: reflect across the sector bisector
                    pv = (float2(cos(a), sin(a)) * r);

                    if (polarAxis == SDF_POLAR_AXIS_X) { localPosition.yz = pv; }
                    else if (polarAxis == SDF_POLAR_AXIS_Z) { localPosition.xy = pv; }
                    else { localPosition.xz = pv; }

                    // Per-sector material variant (the same channel SDF_OP_WALLPAPER_FOLD / SDF_OP_CELL_JITTER recolor
                    // through): wrap the raw sector index into [0, count) and stride the palette.
                    if (instructionHeader.w != 0u) {
                        float wrapped = (sector - (data0.z * floor(sector * data0.w)));   // data0.z = count, data0.w = 1/count
                        parityMaterialDelta = ((int)wrapped * (int)instructionHeader.w);
                    }

                    break;
                }
                // POINT op (KEEP IN SYNC with SdfProgramBuilder.DomainWarp): perturb the sample point by a bounded,
                // cross-coupled sinusoidal field before the shapes evaluate — organic bulge/wobble/terrain. data0.xyz =
                // per-axis frequency, data0.w = amplitude; each axis is driven by the NEXT axis's coordinate so the warp
                // is non-separable. Deterministic float trig (±1 LSB). The Jacobian is I plus a perturbation of spectral
                // norm <= amp*max|freq_i| (J - I is a scaled cyclic permutation, so its norm is its largest entry), so the metric
                // stretches by up to (1 + amp*max|freq_i|); SdfProgram.AnalyzeLipschitz bakes
                // that step clamp and folds the point's max travel (amp*sqrt(3)) into a downstream twist/bend's reach.
                case SDF_OP_DOMAIN_WARP: {
                    localPosition += (data0.w * float3(
                        sin(data0.x * localPosition.y),
                        sin(data0.y * localPosition.z),
                        sin(data0.z * localPosition.x)));
                    break;
                }
                // Reflection fold across an ARBITRARY plane (KEEP IN SYNC with SdfProgramBuilder.SymmetryPlane): the
                // general-normal superset of SDF_OP_SYMMETRY_X/Y/Z. data0.xyz = unit plane normal, data0.w = offset;
                // points on the negative side (dot(p,n)+offset < 0) mirror onto the positive side. For n = (1,0,0),
                // offset 0 this is abs(localPosition.x) to the bit. A reflection is an isometry, so distanceScale is
                // untouched and the field stays 1-Lipschitz.
                case SDF_OP_SYMMETRY_PLANE: {
                    float spT = (dot(localPosition, data0.xyz) + data0.w);
                    localPosition -= ((2.0 * min(spT, 0.0)) * data0.xyz);
                    break;
                }
                // The warps rotate a plane pair by an angle keyed on ONE coordinate (GLSL mat2(c,-s,s,c) * v, written as
                // explicit components: x' = c*x + s*y, y' = -s*x + c*y). NOT isometries — space stretches tangentially —
                // so authored rates stay moderate (the validator bounds them) and SdfProgram.AnalyzeLipschitz folds the
                // exact Jacobian operator norm over the chain's reach into the program's step clamp.
                //
                // The three members are DISTINCT ops, not a symmetric family — each names the plane it rotates:
                //   BEND_X: angle keyed on x, rotates the XY plane
                //   BEND_Y: angle keyed on y, rotates the XY plane
                //   BEND_Z: angle keyed on y, rotates the YZ plane
                // TWIST_Y keys on y and rotates XZ (the axis-orthogonal plane) — the only one that twists about its axis.
                case SDF_OP_TWIST_Y: {
                    float twistCos = cos(data0.x * localPosition.y);
                    float twistSin = sin(data0.x * localPosition.y);

                    localPosition.xz = float2(
                        ((twistCos * localPosition.x) + (twistSin * localPosition.z)),
                        ((-twistSin * localPosition.x) + (twistCos * localPosition.z)));
                    break;
                }
                case SDF_OP_BEND_X: {
                    float bendCos = cos(data0.x * localPosition.x);
                    float bendSin = sin(data0.x * localPosition.x);

                    localPosition.xy = float2(
                        ((bendCos * localPosition.x) + (bendSin * localPosition.y)),
                        ((-bendSin * localPosition.x) + (bendCos * localPosition.y)));
                    break;
                }
                case SDF_OP_BEND_Y: {
                    float bendCos = cos(data0.x * localPosition.y);
                    float bendSin = sin(data0.x * localPosition.y);

                    localPosition.xy = float2(
                        ((bendCos * localPosition.x) + (bendSin * localPosition.y)),
                        ((-bendSin * localPosition.x) + (bendCos * localPosition.y)));
                    break;
                }
                case SDF_OP_BEND_Z: {
                    float bendCos = cos(data0.x * localPosition.y);
                    float bendSin = sin(data0.x * localPosition.y);

                    localPosition.yz = float2(
                        ((bendCos * localPosition.y) + (bendSin * localPosition.z)),
                        ((-bendSin * localPosition.y) + (bendCos * localPosition.z)));
                    break;
                }
                case SDF_OP_ELONGATE: {
                    localPosition -= clamp(localPosition, -data0.xyz, data0.xyz);
                    break;
                }
                // The FIELD ops act on the running result, not the point: Onion shells everything accumulated so far
                // into a hollow skin; Dilate inflates it. Both operate in world units (candidates were distance-scaled
                // before blending).
                case SDF_OP_ONION: {
                    result.distance = (abs(result.distance) - data0.x);
                    break;
                }
                case SDF_OP_DILATE: {
                    result.distance -= data0.x;
                    break;
                }
                // FIELD op (KEEP IN SYNC with SdfProgramBuilder.Displace): add a bounded sinusoidal RELIEF to the
                // accumulated field, evaluated at the current folded point — the SDF-native height/parallax map (the
                // relief is real geometry). data0.xyz = per-axis frequency, data0.w = amplitude. The separable
                // sin-product basis is deterministic (float trig, ±1 LSB like the twist/bend warps), so it stays
                // cross-backend parity-safe without a hashed noise table. Its squared gradient norm is MULTILINEAR in the three
                // squared sines, so it maximizes at a cube vertex and reaches exactly amp*max|freq_i| (the infinity norm,
                // not the Euclidean length): the field can overestimate by (1 + amp*max|freq_i|), which
                // SdfProgram.AnalyzeLipschitz bakes as the step clamp.
                case SDF_OP_DISPLACE: {
                    float3 df = (data0.xyz * localPosition);
                    result.distance += (data0.w * ((sin(df.x) * sin(df.y)) * sin(df.z)));
                    break;
                }
                case SDF_OP_WALLPAPER_FOLD: {
                    // header lanes: y = the wallpaper group, z = the fold plane (0 = XZ, 1 = XY, 2 = YZ), w = the
                    // parity-material stride. The fold is an isometry, so distanceScale is untouched.
                    uint group = instructionHeader.y;
                    uint plane = instructionHeader.z;
                    int axisA = ((plane == 2u) ? 1 : 0);
                    int axisB = ((plane == 1u) ? 1 : 2);
                    // The symmetry LOD is PER SAMPLE (not per ray): every map() consumer — beam cone-march, pixel march,
                    // the normal probe, the shadow marches — samples the identical field, so cull and march can never disagree.
                    bool lodSimplify = ((data1.z > 0.0) && (distance(worldPosition, sdfLodOrigin) > data1.z));
                    float2 cellIndex;
                    float2 folded = sdfWallpaperFoldCell(float2(localPosition[axisA], localPosition[axisB]), group, data0.xy, data0.zw, data1.xy, lodSimplify, cellIndex);

                    localPosition[axisA] = folded.x;
                    localPosition[axisB] = folded.y;

                    // The stride recolors the shapes the fold repeats: the cell key strides their declared material, so
                    // each lattice cell selects its own row of the palette. Distances never depend on it.
                    if (instructionHeader.w != 0u) {
                        parityMaterialDelta = (sdfWallpaperCellKey(group, cellIndex) * (int)instructionHeader.w);
                    }

                    break;
                }
                case SDF_OP_SHAPE: {
                    // The per-shape flavour of the segment early-out above (same exactness argument): inside an
                    // EVALUATED segment, a Union shape whose own (tighter) sphere cannot beat the running minimum
                    // skips just its evaluation — always sound, because shape ops never mutate chain state.
                    uint4 shapeBoundMeta = sdfWords[boundsOffset + (2u * index) + 1u];

                    [branch]
                    if (shapeBoundMeta.x != SDF_BOUND_NONE) {
                        float4 shapeBound = asfloat(sdfWords[boundsOffset + (2u * index)]);
                        float3 shapeBoundCenter = shapeBound.xyz;
                        bool shapeBoundReady = (shapeBoundMeta.x == SDF_BOUND_STATIC);

#ifdef SDF_DYNAMIC_TRANSFORMS
                        if (shapeBoundMeta.x == SDF_BOUND_DYNAMIC) {
                            shapeBoundCenter += sdfDynamicTransforms[2u * shapeBoundMeta.y].xyz;
                            shapeBoundReady = true;
                        }
#endif

                        if (shapeBoundReady) {
                            float3 toShapeCenter = (worldPosition - shapeBoundCenter);
                            float shapeClearance = max((result.distance + shapeBound.w), 0.0);

                            if (dot(toShapeCenter, toShapeCenter) >= (shapeClearance * shapeClearance)) {
                                break;
                            }
                        }
                    }

                    uint shapeType = instructionHeader.y;
                    int material = (int)instructionHeader.w;
                    float candidate = (evaluateShape(shapeType, localPosition, data0, data1) * distanceScale);

                    // An active wallpaper stride recolors this shape by its cell key — never a screen sentinel (the whole
                    // >= SDF_SCREEN_MATERIAL range, not just the exact value: a screen-instance id must survive intact).
                    if ((material < SDF_SCREEN_MATERIAL) && (parityMaterialDelta != 0)) {
                        material += parityMaterialDelta;
                    }

                    // Hand the DISTANCE-SCALED candidate to the shared blend tail below.
                    composeCandidate = candidate;
                    composeMaterial = material;
                    composeBlend = instructionHeader.z;
                    composeSmooth = data1.x;
                    composePending = true;
                    break;
                }
                // Scoped field accumulator (KEEP IN SYNC with Puck.SdfVm.SdfOp.PushField/PopField). A scope touches ONLY
                // the FIELD — never localPosition / distanceScale / parityMaterialDelta — so the point chain is untouched
                // and ResetPoint semantics are unchanged.
                case SDF_OP_PUSH_FIELD: {
                    // Save the parent accumulator into the one-deep slot and reseed a fresh scope. Every accumulator-
                    // reading op until the matching POP now composes against SDF_FAR_DISTANCE (this scope), not the scene.
                    savedFieldDistance = result.distance;
                    savedFieldMaterial = result.material;
                    result.distance = SDF_FAR_DISTANCE;
                    result.material = 0;
                    break;
                }
                case SDF_OP_POP_FIELD: {
                    // The scope's accumulated field IS the candidate — ALREADY in world units (its shapes were
                    // distance-scaled as they blended in), so it is NOT re-multiplied by distanceScale, and the point
                    // parityMaterialDelta must NOT touch it (the fusion trap). Restore the parent accumulator as the
                    // blend LHS, then fall into the SAME material-winner + blendShape tail a SHAPE uses. The compose blend
                    // + smooth ride the POP instruction (header.z / data1.x, baked by SdfProgramBuilder.PushField).
                    composeCandidate = result.distance;
                    composeMaterial = result.material;
                    composeBlend = instructionHeader.z;
                    composeSmooth = data1.x;
                    result.distance = savedFieldDistance;
                    result.material = savedFieldMaterial;
                    composePending = true;
                    break;
                }
            }

            // The SHARED BLEND TAIL (SHAPE + POP_FIELD). The material winner uses the SAME strict compares a shape does —
            // union-like wins when nearer, intersection-like when farther (the surviving surface is the farther field's),
            // subtraction-like when the CARVED surface shows — so a bowl carved from a box wears the carving shape's
            // interior material, and the incumbent keeps its material on a TIE (a scoped shape resting on the ground plane
            // is a contact locus of ties). Then blend the candidate into result.distance. composePending is false for
            // every point/field op AND for a bound-skipped SHAPE, so those paths are byte-for-byte the pre-scope walk.
            if (composePending) {
                bool candidateWins;

                switch (composeBlend) {
                    case SDF_BLEND_INTERSECTION:
                    case SDF_BLEND_SMOOTH_INTERSECTION:
                    case SDF_BLEND_CHAMFER_INTERSECTION: { candidateWins = (composeCandidate > result.distance); break; }
                    case SDF_BLEND_SUBTRACTION:
                    case SDF_BLEND_SMOOTH_SUBTRACTION:
                    case SDF_BLEND_CHAMFER_SUBTRACTION:  { candidateWins = (-composeCandidate > result.distance); break; }
                    default:                             { candidateWins = (composeCandidate < result.distance); break; }
                }

                if (candidateWins) {
                    result.material = composeMaterial;
                }

                result.distance = blendShape(result.distance, composeCandidate, composeBlend, composeSmooth);
            }
        }
    }

    // The Lipschitz clamp: scale the FINAL nearest-surface distance by the per-program 1/L so every marcher that funnels
    // through mapCore (the beam cone-march, the pixel march, the RT debug marcher, the shadow marches, the normal probes)
    // takes field-rate-safe steps. Applied ONCE here, AFTER the walk — the per-candidate blends, the material-winner
    // compares, the Onion/Dilate/Displace field ops, and the exact-cull skip tests all ran on UNCORRECTED candidates, so
    // the zero set, the seams, the materials, and the cull contract are all preserved; only the step LENGTH shrinks. A
    // uniform positive scale never moves the zero crossing, and a normal probe's common factor cancels under normalize
    // (true of both the 4-tap tetrahedron and the 6-tap central difference), so shading is unchanged. stepScale == 1.0
    // leaves the result bit-identical.
    //
    // CAVEAT for consumers: the returned distance is scaled. Take a STEP with it freely, but a consumer that COMPARES it
    // against a world-space quantity (a penumbra ratio, a footprint threshold) must divide the clamp back out — see
    // sdfStepScale() and softShadow in sdf-world.hlsli.
    result.distance *= stepScale;

    return result;
}

// The universal entry point every consumer outside the world path's Stage 1 calls (the beam cone-march,
// sdf-world-rt-debug's marcher and its normal probe and shadow march): every instance visible, so an instanced program
// still renders its complete picture — only Stage 1 narrows the mask (see mapMasked).
SdfHit map(float3 worldPosition) {
    return mapCore(worldPosition, SDF_INSTANCE_MASK_ALL);
}
// The per-tile MASKED entry point (world render path only, sdf-world-views.comp): evaluates the WORLD set (segments
// owned by no instance) plus only the instances the per-tile mask at `instanceMaskBase` (an element offset into
// sdfInstanceMasks, written by the tile-cull beam prepass) marks visible — culling the shapes AND transform chains
// of instances the tile's rays cannot reach, without ever touching their segments. Exact, not approximate: a
// masked-out instance's true contribution to the tile's rays is provably absent (the beam prepass tested every
// ray's cone against the instance's own bounding sphere), so the result is bit-identical to a full
// mapCore(worldPosition, SDF_INSTANCE_MASK_ALL) call for every ray the mask keeps correct.
SdfHit mapMasked(float3 worldPosition, uint instanceMaskBase) {
    return mapCore(worldPosition, instanceMaskBase);
}

// Applies a RepeatPolar sector fold's local orthogonal map (a rotation by -angle*sector in the fold plane, then an
// optional reflection across the sector bisector — both isometries, piecewise-constant per sector) to a Jacobian
// column vector `b`, identity on the axial coordinate. `rc`/`rs` = cos/sin(angle*sector); `flip` = -1 when the point's
// rotated v-coordinate was negative and the op mirrors, else 1.
float3 sdfApplyPolarJacobian(float3 b, uint axis, float rc, float rs, float flip) {
    float2 uv;

    if (axis == SDF_POLAR_AXIS_X) { uv = b.yz; }
    else if (axis == SDF_POLAR_AXIS_Z) { uv = b.xy; }
    else { uv = b.xz; }

    float2 nuv = float2(((rc * uv.x) + (rs * uv.y)), (((-rs * uv.x) + (rc * uv.y)) * flip));

    if (axis == SDF_POLAR_AXIS_X) { b.yz = nuv; }
    else if (axis == SDF_POLAR_AXIS_Z) { b.xy = nuv; }
    else { b.xz = nuv; }

    return b;
}
// Applies a wallpaper fold's local 2x2 in-plane Jacobian (its two columns, recovered by a shape-local finite difference
// of the fold map — the fold is an isometry but composes many conditional reflections/rotations no single closed form
// spells) to a Jacobian column vector `b`, identity on the third axis.
float3 sdfApplyPlaneJacobian(float3 b, int axisA, int axisB, float2 col0, float2 col1) {
    float bA = b[axisA];
    float bB = b[axisB];

    b[axisA] = ((col0.x * bA) + (col1.x * bB));
    b[axisB] = ((col0.y * bA) + (col1.y * bB));

    return b;
}

// The forward-mode dual twin of mapCore (see the "Forward-mode gradient dual" banner above blendShapeDual). Walks the
// SAME segment/instance merge and the SAME op stream, tracking — beside the scalar accumulator — the transform-chain
// Jacobian columns jx/jy/jz (= d(localPosition)/d(worldPosition.{x,y,z}); the point ops build them, RESET restores
// identity) and the world-space accumulator gradient `resultGradient`. `gradient` (out) is the UN-normalized surface
// gradient at the hit; the consumer normalizes (which cancels the stepScale the scalar distance still carries). HIT-
// ONLY: never called from the march, so its ~2x cost is paid once per lit pixel. KEEP the walk skeleton IN SYNC with
// mapCore — this is a parallel evaluation path, not a rewrite: only the gradient lane is added.
SdfHit mapGradCore(float3 worldPosition, uint instanceMaskBase, out float3 gradient) {
    uint4 header = sdfWords[0];
    uint instructionCount = header.x;
    uint dataOffset = header.z;
    uint boundsOffset = (header.w + (2u * header.y));
    uint segmentOffset = (boundsOffset + (2u * header.x));
    uint4 segmentHeader = sdfWords[segmentOffset];
    uint segmentCount = segmentHeader.x;
    float stepScale = asfloat(segmentHeader.y);
    stepScale = ((stepScale > 0.0) ? stepScale : 1.0);
    uint instanceOffset = sdfInstanceDirectoryOffsetFrom(segmentOffset, segmentCount);
    uint instanceCount = sdfWords[instanceOffset].x;
    uint worldSegmentOffset = (instanceOffset + 1u + (2u * instanceCount));
    bool hasInstances = (instanceCount != 0u);

    uint linearCursor = 0u;
    uint worldCursor = 0u;
    uint worldCount = 0u;
    uint worldNext = SDF_SEGMENT_NONE;
    uint maskWordIndex = 0xFFFFFFFFu;
    uint maskWordBits = 0u;
    uint instanceSegment = SDF_SEGMENT_NONE;
    uint instanceSegmentEnd = SDF_SEGMENT_NONE;

    if (hasInstances) {
        worldCount = sdfWords[worldSegmentOffset].x;
        worldNext = ((0u < worldCount) ? sdfWords[worldSegmentOffset + 1u].x : SDF_SEGMENT_NONE);
        sdfNextVisibleInstanceRange(instanceMaskBase, instanceOffset, instanceCount, maskWordIndex, maskWordBits, instanceSegment, instanceSegmentEnd);
    }

    float3 localPosition = worldPosition;
    float distanceScale = 1.0;
    // The transform-chain Jacobian columns: jx = d(localPosition)/d(worldPosition.x), etc. Identity at the start of
    // every chain segment (RESET restores them, exactly as it restores localPosition). worldGrad_j = dot(localGrad, j_).
    float3 jx = float3(1.0, 0.0, 0.0);
    float3 jy = float3(0.0, 1.0, 0.0);
    float3 jz = float3(0.0, 0.0, 1.0);
    SdfHit result;

    result.distance = SDF_FAR_DISTANCE;
    result.material = 0;
    float3 resultGradient = float3(0.0, 0.0, 0.0);

    // The one-deep scoped-accumulator save slot — a {distance, material, gradient} struct so a future depth raise and
    // this gradient lane are ONE migration (see SdfFieldSave).
    SdfFieldSave saved;
    saved.distance = SDF_FAR_DISTANCE;
    saved.material = 0;
    saved.gradient = float3(0.0, 0.0, 0.0);

    [loop]
    for (;;) {
        uint segment;

        if (!hasInstances) {
            if (linearCursor >= segmentCount) {
                break;
            }

            segment = linearCursor++;
        } else if (worldNext < instanceSegment) {
            segment = worldNext;
            worldCursor++;
            worldNext = ((worldCursor < worldCount) ? sdfWords[worldSegmentOffset + 1u + worldCursor].x : SDF_SEGMENT_NONE);
        } else if (instanceSegment < instanceSegmentEnd) {
            segment = instanceSegment++;

            if (instanceSegment == instanceSegmentEnd) {
                sdfNextVisibleInstanceRange(instanceMaskBase, instanceOffset, instanceCount, maskWordIndex, maskWordBits, instanceSegment, instanceSegmentEnd);
            }
        } else {
            break;
        }

        uint4 segmentMeta = sdfWords[segmentOffset + 1u + (2u * segment) + 1u];

        [branch]
        if (segmentMeta.x != SDF_BOUND_NONE) {
            float4 segmentBound = asfloat(sdfWords[segmentOffset + 1u + (2u * segment)]);
            float3 boundCenter = segmentBound.xyz;
            bool boundReady = (segmentMeta.x == SDF_BOUND_STATIC);

#ifdef SDF_DYNAMIC_TRANSFORMS
            if (segmentMeta.x == SDF_BOUND_DYNAMIC) {
                boundCenter += sdfDynamicTransforms[2u * segmentMeta.y].xyz;
                boundReady = true;
            }
#endif

            if (boundReady) {
                float3 toCenter = (worldPosition - boundCenter);
                float clearance = max((result.distance + segmentBound.w), 0.0);

                if (dot(toCenter, toCenter) >= (clearance * clearance)) {
                    continue;
                }
            }
        }

        [loop]
        for (uint index = segmentMeta.z; (index < segmentMeta.w); index++) {
            uint4 instructionHeader = sdfWords[1u + index];
            uint op = instructionHeader.x;
            float4 data0 = asfloat(sdfWords[dataOffset + (2u * index)]);
            float4 data1 = asfloat(sdfWords[dataOffset + (2u * index) + 1u]);

            bool composePending = false;
            float composeCandidate = SDF_FAR_DISTANCE;
            float3 composeGradient = float3(0.0, 0.0, 0.0);
            uint composeBlend = SDF_BLEND_UNION;
            int composeMaterial = 0;
            float composeSmooth = 0.0;

            switch (op) {
                case SDF_OP_RESET: {
                    localPosition = worldPosition;
                    distanceScale = 1.0;
                    jx = float3(1.0, 0.0, 0.0);
                    jy = float3(0.0, 1.0, 0.0);
                    jz = float3(0.0, 0.0, 1.0);
                    break;
                }
                case SDF_OP_TRANSLATE: {
                    localPosition -= data0.xyz;   // A = I (a constant offset has zero Jacobian)
                    break;
                }
                case SDF_OP_ROTATE: {
                    // A = R^T (the same inverse rotation the point takes), applied to each Jacobian column.
                    localPosition = rotatePointByInverseQuaternion(localPosition, data0);
                    jx = rotatePointByInverseQuaternion(jx, data0);
                    jy = rotatePointByInverseQuaternion(jy, data0);
                    jz = rotatePointByInverseQuaternion(jz, data0);
                    break;
                }
                case SDF_OP_SCALE: {
                    localPosition /= data0.xyz;   // A = diag(1/scale)
                    jx /= data0.xyz;
                    jy /= data0.xyz;
                    jz /= data0.xyz;
                    distanceScale *= data0.w;
                    break;
                }
                case SDF_OP_LOG_SPHERE: {
                    float logRadius = log(max(length(localPosition), SDF_LOGSPHERE_MIN_RADIUS));
                    float shell = round(logRadius * data0.z);
                    float shellScale = exp(shell * data0.x);
                    float spinAngle = (shell * data0.y);
                    float spinCos = cos(spinAngle);
                    float spinSin = sin(spinAngle);
                    float invShell = (1.0 / shellScale);

                    // A = (1/shellScale) * Rz(spin) — the shell is locally constant (round), so this is the exact linear
                    // part; the shellScale factor rides distanceScale below and cancels the 1/shellScale here under norm.
                    jx = float3(((spinCos * jx.x) - (spinSin * jx.y)), ((spinSin * jx.x) + (spinCos * jx.y)), jx.z) * invShell;
                    jy = float3(((spinCos * jy.x) - (spinSin * jy.y)), ((spinSin * jy.x) + (spinCos * jy.y)), jy.z) * invShell;
                    jz = float3(((spinCos * jz.x) - (spinSin * jz.y)), ((spinSin * jz.x) + (spinCos * jz.y)), jz.z) * invShell;

                    localPosition /= shellScale;
                    localPosition.xy = float2(
                        ((spinCos * localPosition.x) - (spinSin * localPosition.y)),
                        ((spinSin * localPosition.x) + (spinCos * localPosition.y)));
                    distanceScale *= shellScale;
                    break;
                }
#ifdef SDF_DYNAMIC_TRANSFORMS
                case SDF_OP_TRANSFORM_DYNAMIC: {
                    uint dynamicSlot = (uint)data0.x;
                    float4 dynamicPosition = sdfDynamicTransforms[(2u * dynamicSlot)];
                    float4 dynamicOrientation = sdfDynamicTransforms[((2u * dynamicSlot) + 1u)];
                    localPosition = rotatePointByInverseQuaternion((localPosition - dynamicPosition.xyz), dynamicOrientation);
                    jx = rotatePointByInverseQuaternion(jx, dynamicOrientation);
                    jy = rotatePointByInverseQuaternion(jy, dynamicOrientation);
                    jz = rotatePointByInverseQuaternion(jz, dynamicOrientation);
                    break;
                }
#endif
                case SDF_OP_REPEAT: {
                    localPosition -= (data0.xyz * round(localPosition * data1.xyz));   // A = I (round is locally constant)
                    break;
                }
                case SDF_OP_REPEAT_LIMITED: {
                    localPosition -= (data0.xyz * clamp(round(localPosition / data0.xyz), -data1.xyz, data1.xyz));
                    break;
                }
                case SDF_OP_CELL_JITTER: {
                    float3 cell = round(localPosition * data1.xyz);
                    uint3 seed = uint3(instructionHeader.y, (instructionHeader.y * SDF_HASH_STREAM_A), (instructionHeader.y * SDF_HASH_STREAM_B));
                    uint3 key = (asuint(int3(cell)) ^ seed);
                    uint3 h0 = sdfPcg3d(key);

                    uint noiseFlavor = instructionHeader.z;
                    float3 r0;
                    if (noiseFlavor == SDF_NOISE_BLUE) {
                        uint3 uc = (asuint(int3(cell)) + seed);
                        uint bx = ((uc.x * SDF_R3_ALPHA1) + (uc.y * SDF_R3_ALPHA2) + (uc.z * SDF_R3_ALPHA3));
                        uint by = ((uc.x * SDF_R3_ALPHA2) + (uc.y * SDF_R3_ALPHA3) + (uc.z * SDF_R3_ALPHA1));
                        uint bz = ((uc.x * SDF_R3_ALPHA3) + (uc.y * SDF_R3_ALPHA1) + (uc.z * SDF_R3_ALPHA2));
                        r0 = (float3(bx, by, bz) * SDF_INV_2POW32);
                    } else if (noiseFlavor == SDF_NOISE_GAUSSIAN) {
                        uint3 g1 = sdfPcg3d(key ^ SDF_HASH_STREAM_A);
                        uint3 g2 = sdfPcg3d(key ^ SDF_HASH_STREAM_B);
                        r0 = (((float3)h0 + (float3)g1 + (float3)g2) * (SDF_INV_2POW32 / 3.0));
                    } else {
                        r0 = ((float3)h0 * SDF_INV_2POW32);
                    }

                    localPosition -= (data0.xyz * cell);          // A = I (fold + constant jitter)
                    localPosition -= ((r0 - 0.5) * data0.w);

                    // Tumble is a per-cell isometry: apply the same rotation to the Jacobian columns.
                    if (data1.w > 0.0) {
                        uint3 h1 = sdfPcg3d(key ^ SDF_HASH_TUMBLE);
                        float3 r1 = ((float3)h1 * SDF_INV_2POW32);
                        float zz = ((2.0 * r1.x) - 1.0);
                        float rr = sqrt(max(0.0, (1.0 - (zz * zz))));
                        float phi = (SDF_TAU * r1.y);
                        float3 axis = float3((rr * cos(phi)), (rr * sin(phi)), zz);
                        float angle = ((r1.z * data1.w) * SDF_PI);
                        float ha = (0.5 * angle);
                        float sa = sin(ha);
                        float4 q = float4((axis * sa), cos(ha));
                        localPosition = rotatePointByInverseQuaternion(localPosition, q);
                        jx = rotatePointByInverseQuaternion(jx, q);
                        jy = rotatePointByInverseQuaternion(jy, q);
                        jz = rotatePointByInverseQuaternion(jz, q);
                    }

                    break;
                }
                case SDF_OP_REPEAT_POLAR: {
                    uint polarAxis = instructionHeader.y;
                    float2 pv;
                    if (polarAxis == SDF_POLAR_AXIS_X) { pv = localPosition.yz; }
                    else if (polarAxis == SDF_POLAR_AXIS_Z) { pv = localPosition.xy; }
                    else { pv = localPosition.xz; }

                    float sectorAngle = data0.x;
                    float a = (atan2(pv.y, pv.x) + (0.5 * sectorAngle));
                    float r = length(pv);
                    float sector = floor(a * data0.y);
                    a = ((a - (sectorAngle * sector)) - (0.5 * sectorAngle));

                    // The local linear map = rotation by -sectorAngle*sector, then an optional v-flip (the mirror). rc/rs
                    // = cos/sin(sectorAngle*sector); the flip fires where the rotated v (== r*sin(a)) is negative.
                    float rc = cos(sectorAngle * sector);
                    float rs = sin(sectorAngle * sector);
                    float flip = (((instructionHeader.z != 0u) && (a < 0.0)) ? -1.0 : 1.0);
                    jx = sdfApplyPolarJacobian(jx, polarAxis, rc, rs, flip);
                    jy = sdfApplyPolarJacobian(jy, polarAxis, rc, rs, flip);
                    jz = sdfApplyPolarJacobian(jz, polarAxis, rc, rs, flip);

                    if (instructionHeader.z != 0u) { a = abs(a); }
                    pv = (float2(cos(a), sin(a)) * r);

                    if (polarAxis == SDF_POLAR_AXIS_X) { localPosition.yz = pv; }
                    else if (polarAxis == SDF_POLAR_AXIS_Z) { localPosition.xy = pv; }
                    else { localPosition.xz = pv; }

                    break;
                }
                case SDF_OP_DOMAIN_WARP: {
                    // A = I + amp * M, M a scaled cyclic permutation (each axis driven by the next); rows below.
                    float cx = cos(data0.x * localPosition.y);
                    float cy = cos(data0.y * localPosition.z);
                    float cz = cos(data0.z * localPosition.x);
                    float3 ax = float3(1.0, (data0.w * data0.x * cx), 0.0);
                    float3 ay = float3(0.0, 1.0, (data0.w * data0.y * cy));
                    float3 az = float3((data0.w * data0.z * cz), 0.0, 1.0);
                    sdfApplyJacobian(ax, ay, az, jx, jy, jz);

                    localPosition += (data0.w * float3(
                        sin(data0.x * localPosition.y),
                        sin(data0.y * localPosition.z),
                        sin(data0.z * localPosition.x)));
                    break;
                }
                case SDF_OP_SYMMETRY_PLANE: {
                    float spT = (dot(localPosition, data0.xyz) + data0.w);
                    // Reflection A = I - 2 n n^T, applied only on the negative side (spT < 0) — the same half-space fold.
                    float reflect = ((spT < 0.0) ? 2.0 : 0.0);
                    jx -= ((reflect * dot(jx, data0.xyz)) * data0.xyz);
                    jy -= ((reflect * dot(jy, data0.xyz)) * data0.xyz);
                    jz -= ((reflect * dot(jz, data0.xyz)) * data0.xyz);
                    localPosition -= ((2.0 * min(spT, 0.0)) * data0.xyz);
                    break;
                }
                case SDF_OP_TWIST_Y: {
                    float twistCos = cos(data0.x * localPosition.y);
                    float twistSin = sin(data0.x * localPosition.y);
                    float nx = ((twistCos * localPosition.x) + (twistSin * localPosition.z));
                    float nz = ((-twistSin * localPosition.x) + (twistCos * localPosition.z));
                    float k = data0.x;
                    float3 ax = float3(twistCos, (k * nz), twistSin);
                    float3 ay = float3(0.0, 1.0, 0.0);
                    float3 az = float3(-twistSin, (-k * nx), twistCos);
                    sdfApplyJacobian(ax, ay, az, jx, jy, jz);
                    localPosition.xz = float2(nx, nz);
                    break;
                }
                case SDF_OP_BEND_X: {
                    float bendCos = cos(data0.x * localPosition.x);
                    float bendSin = sin(data0.x * localPosition.x);
                    float nx = ((bendCos * localPosition.x) + (bendSin * localPosition.y));
                    float ny = ((-bendSin * localPosition.x) + (bendCos * localPosition.y));
                    float k = data0.x;
                    float3 ax = float3((bendCos + (k * ny)), bendSin, 0.0);
                    float3 ay = float3((-bendSin - (k * nx)), bendCos, 0.0);
                    float3 az = float3(0.0, 0.0, 1.0);
                    sdfApplyJacobian(ax, ay, az, jx, jy, jz);
                    localPosition.xy = float2(nx, ny);
                    break;
                }
                case SDF_OP_BEND_Y: {
                    float bendCos = cos(data0.x * localPosition.y);
                    float bendSin = sin(data0.x * localPosition.y);
                    float nx = ((bendCos * localPosition.x) + (bendSin * localPosition.y));
                    float ny = ((-bendSin * localPosition.x) + (bendCos * localPosition.y));
                    float k = data0.x;
                    float3 ax = float3(bendCos, (bendSin + (k * ny)), 0.0);
                    float3 ay = float3(-bendSin, (bendCos - (k * nx)), 0.0);
                    float3 az = float3(0.0, 0.0, 1.0);
                    sdfApplyJacobian(ax, ay, az, jx, jy, jz);
                    localPosition.xy = float2(nx, ny);
                    break;
                }
                case SDF_OP_BEND_Z: {
                    float bendCos = cos(data0.x * localPosition.y);
                    float bendSin = sin(data0.x * localPosition.y);
                    float ny = ((bendCos * localPosition.y) + (bendSin * localPosition.z));
                    float nz = ((-bendSin * localPosition.y) + (bendCos * localPosition.z));
                    float k = data0.x;
                    float3 ax = float3(1.0, 0.0, 0.0);
                    float3 ay = float3(0.0, (bendCos + (k * nz)), bendSin);
                    float3 az = float3(0.0, (-bendSin - (k * ny)), bendCos);
                    sdfApplyJacobian(ax, ay, az, jx, jy, jz);
                    localPosition.yz = float2(ny, nz);
                    break;
                }
                case SDF_OP_ELONGATE: {
                    // A = diag(indicator(|p_i| > h_i)): the interior of the swept region collapses onto the core (zero
                    // derivative there), the outside translates rigidly (unit derivative).
                    float3 a = step(data0.xyz, abs(localPosition));
                    jx *= a;
                    jy *= a;
                    jz *= a;
                    localPosition -= clamp(localPosition, -data0.xyz, data0.xyz);
                    break;
                }
                case SDF_OP_ONION: {
                    float onionSign = ((result.distance < 0.0) ? -1.0 : 1.0);   // d -> |d| - t flips the gradient by sign(d)
                    result.distance = (abs(result.distance) - data0.x);
                    resultGradient *= onionSign;
                    break;
                }
                case SDF_OP_DILATE: {
                    result.distance -= data0.x;   // gradient unchanged (a uniform outward offset)
                    break;
                }
                case SDF_OP_DISPLACE: {
                    // gradient += amp * grad(sin*sin*sin) — analytic and exact (the FD-cancellation win), mapped to world.
                    float3 df = (data0.xyz * localPosition);
                    float sx = sin(df.x);
                    float sy = sin(df.y);
                    float sz = sin(df.z);
                    result.distance += (data0.w * ((sx * sy) * sz));
                    float3 localGradProduct = (data0.w * float3(
                        ((data0.x * cos(df.x)) * (sy * sz)),
                        ((sx * data0.y * cos(df.y)) * sz),
                        ((sx * sy) * (data0.z * cos(df.z)))));
                    resultGradient += float3(dot(localGradProduct, jx), dot(localGradProduct, jy), dot(localGradProduct, jz));
                    break;
                }
                case SDF_OP_WALLPAPER_FOLD: {
                    uint group = instructionHeader.y;
                    uint plane = instructionHeader.z;
                    int axisA = ((plane == 2u) ? 1 : 0);
                    int axisB = ((plane == 1u) ? 1 : 2);
                    bool lodSimplify = ((data1.z > 0.0) && (distance(worldPosition, sdfLodOrigin) > data1.z));
                    float2 cellIndex;
                    float2 in2 = float2(localPosition[axisA], localPosition[axisB]);
                    float2 folded = sdfWallpaperFoldCell(in2, group, data0.xy, data0.zw, data1.xy, lodSimplify, cellIndex);

                    // The fold is a composed isometry with no single closed-form linear part; recover its in-plane 2x2 by
                    // a shape-local finite difference (both output columns), then transport the Jacobian columns through it.
                    float2 idxScratch;
                    float2 foldedA = sdfWallpaperFoldCell(in2 + float2(SDF_SHAPE_GRAD_EPSILON, 0.0), group, data0.xy, data0.zw, data1.xy, lodSimplify, idxScratch);
                    float2 foldedB = sdfWallpaperFoldCell(in2 + float2(0.0, SDF_SHAPE_GRAD_EPSILON), group, data0.xy, data0.zw, data1.xy, lodSimplify, idxScratch);
                    float2 col0 = ((foldedA - folded) * (1.0 / SDF_SHAPE_GRAD_EPSILON));
                    float2 col1 = ((foldedB - folded) * (1.0 / SDF_SHAPE_GRAD_EPSILON));
                    jx = sdfApplyPlaneJacobian(jx, axisA, axisB, col0, col1);
                    jy = sdfApplyPlaneJacobian(jy, axisA, axisB, col0, col1);
                    jz = sdfApplyPlaneJacobian(jz, axisA, axisB, col0, col1);

                    localPosition[axisA] = folded.x;
                    localPosition[axisB] = folded.y;
                    break;
                }
                case SDF_OP_SHAPE: {
                    uint4 shapeBoundMeta = sdfWords[boundsOffset + (2u * index) + 1u];

                    [branch]
                    if (shapeBoundMeta.x != SDF_BOUND_NONE) {
                        float4 shapeBound = asfloat(sdfWords[boundsOffset + (2u * index)]);
                        float3 shapeBoundCenter = shapeBound.xyz;
                        bool shapeBoundReady = (shapeBoundMeta.x == SDF_BOUND_STATIC);

#ifdef SDF_DYNAMIC_TRANSFORMS
                        if (shapeBoundMeta.x == SDF_BOUND_DYNAMIC) {
                            shapeBoundCenter += sdfDynamicTransforms[2u * shapeBoundMeta.y].xyz;
                            shapeBoundReady = true;
                        }
#endif

                        if (shapeBoundReady) {
                            float3 toShapeCenter = (worldPosition - shapeBoundCenter);
                            float shapeClearance = max((result.distance + shapeBound.w), 0.0);

                            if (dot(toShapeCenter, toShapeCenter) >= (shapeClearance * shapeClearance)) {
                                break;
                            }
                        }
                    }

                    uint shapeType = instructionHeader.y;
                    int material = (int)instructionHeader.w;
                    float candidate = (evaluateShape(shapeType, localPosition, data0, data1) * distanceScale);
                    // The primitive's LOCAL gradient, mapped to world through the transform-chain Jacobian columns and
                    // scaled by the same distanceScale the candidate distance took (Scale/LogSphere's metric factor).
                    float3 localGrad = evaluateShapeGradient(shapeType, localPosition, data0, data1);
                    float3 worldGrad = (float3(dot(localGrad, jx), dot(localGrad, jy), dot(localGrad, jz)) * distanceScale);

                    composeCandidate = candidate;
                    composeGradient = worldGrad;
                    composeMaterial = material;
                    composeBlend = instructionHeader.z;
                    composeSmooth = data1.x;
                    composePending = true;
                    break;
                }
                case SDF_OP_PUSH_FIELD: {
                    saved.distance = result.distance;
                    saved.material = result.material;
                    saved.gradient = resultGradient;
                    result.distance = SDF_FAR_DISTANCE;
                    result.material = 0;
                    resultGradient = float3(0.0, 0.0, 0.0);
                    break;
                }
                case SDF_OP_POP_FIELD: {
                    composeCandidate = result.distance;
                    composeGradient = resultGradient;
                    composeMaterial = result.material;
                    composeBlend = instructionHeader.z;
                    composeSmooth = data1.x;
                    result.distance = saved.distance;
                    result.material = saved.material;
                    resultGradient = saved.gradient;
                    composePending = true;
                    break;
                }
            }

            if (composePending) {
                bool candidateWins;

                switch (composeBlend) {
                    case SDF_BLEND_INTERSECTION:
                    case SDF_BLEND_SMOOTH_INTERSECTION:
                    case SDF_BLEND_CHAMFER_INTERSECTION: { candidateWins = (composeCandidate > result.distance); break; }
                    case SDF_BLEND_SUBTRACTION:
                    case SDF_BLEND_SMOOTH_SUBTRACTION:
                    case SDF_BLEND_CHAMFER_SUBTRACTION:  { candidateWins = (-composeCandidate > result.distance); break; }
                    default:                             { candidateWins = (composeCandidate < result.distance); break; }
                }

                if (candidateWins) {
                    result.material = composeMaterial;
                }

                float blendedDistance;
                float3 blendedGradient;
                blendShapeDual(result.distance, resultGradient, composeCandidate, composeGradient, composeBlend, composeSmooth, blendedDistance, blendedGradient);
                result.distance = blendedDistance;
                resultGradient = blendedGradient;
            }
        }
    }

    result.distance *= stepScale;
    // The gradient is NOT scaled by stepScale: it is a uniform positive factor on the whole field, and the consumer
    // normalizes the gradient (which cancels it) — so applying it here would be undone anyway.
    gradient = resultGradient;

    return result;
}
// The per-tile MASKED dual entry (world render path, hit-only). Analytic surface gradient at `worldPosition` under the
// same tile instance mask the primary march used (self-consistent with the hit). Consumers normalize `gradient`.
SdfHit mapGradMasked(float3 worldPosition, uint instanceMaskBase, out float3 gradient) {
    return mapGradCore(worldPosition, instanceMaskBase, gradient);
}

// Instance `index`'s bound within the directory at `instanceOffset` (the caller resolves
// sdfInstanceDirectoryOffset() ONCE and passes it down — the beam prepass hoists it out of its per-instance cull
// loop): center (STATIC) or pre-dynamic offset (DYNAMIC, resolved against sdfDynamicTransforms — requires
// SDF_DYNAMIC_TRANSFORMS) and radius. Mirrors the per-segment/per-shape bound resolve in mapCore.
float4 sdfInstanceBoundAt(uint instanceOffset, uint index) {
    uint entryBase = sdfInstanceEntryOffset(instanceOffset, index);
    float4 bound = asfloat(sdfWords[entryBase]);
    uint4 meta = sdfWords[entryBase + 1u];

#ifdef SDF_DYNAMIC_TRANSFORMS
    if (meta.x == SDF_BOUND_DYNAMIC) {
        bound.xyz += sdfDynamicTransforms[2u * meta.y].xyz;
    }
#endif

    return bound;
}

struct SdfMaterialData {
    float3 albedo;
    float emissive;  // self-illumination strength: albedo * emissive adds to the shaded color
    float specular;  // Blinn-Phong strength in [0, 1]; 0 = matte
    float shininess; // Blinn-Phong exponent (highlight tightness)
};

// The ONE material decode point (KEEP IN SYNC with the 2-uint4 layout above and SdfProgram.cs).
SdfMaterialData sdfMaterialLoad(int material) {
    uint4 header = sdfWords[0];
    uint materialBase = (header.w + (2u * (uint)material));
    float4 m0 = asfloat(sdfWords[materialBase]);
    float4 m1 = asfloat(sdfWords[materialBase + 1u]);
    SdfMaterialData data;

    data.albedo = m0.rgb;
    data.emissive = m0.a;
    data.specular = m1.x;
    data.shininess = m1.y;

    return data;
}
// Thin wrapper for callers that only shade the base color (debug views, palette ramps).
float3 sdfMaterialAlbedo(int material) {
    return sdfMaterialLoad(material).albedo;
}
// The ONE lit-surface shade funnel: a lambert term, a Blinn-Phong highlight, and an emissive lift. An all-zero
// specular/emissive material reduces to pure lambert exactly. `diffuse` is the caller's accumulated radiance (ambient +
// the sun + any colored screen lights — a float3 so colored lights tint the surface); `lightScale` scales the highlight
// by the caller's shadow/light attenuation. KEEP IN SYNC across every caller (sdf-world.hlsli, sdf-world-rt-debug).
float3 sdfMaterialShade(SdfMaterialData material, float3 diffuse, float3 normal, float3 rayDirection, float3 lightDirection, float lightScale) {
    float3 color = (material.albedo * diffuse);

    if (material.specular > 0.0) {
        float3 halfVector = normalize(lightDirection - rayDirection);
        color += ((material.specular * pow(saturate(dot(normal, halfVector)), material.shininess)) * lightScale);
    }

    if (material.emissive > 0.0) {
        color += (material.albedo * material.emissive);
    }

    return color;
}

#endif
