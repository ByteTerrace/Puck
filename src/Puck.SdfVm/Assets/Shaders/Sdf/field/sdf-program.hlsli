// The program word stream and its instance directory, which the field interpreter reads.
#ifndef FIELD_SDF_PROGRAM_HLSLI
#define FIELD_SDF_PROGRAM_HLSLI
static uint sdfWorkShapes = 0u;
static uint sdfWorkGradients = 0u;
uint sdfProgramVectorCount() {
    uint count;
    uint stride;
    sdfWords.GetDimensions(count, stride);
    return count;
}
bool sdfProgramRange(uint first, uint count, uint stride) {
    uint capacity = sdfProgramVectorCount();
    return stride != 0u && first <= capacity && count <= (capacity - first) / stride;
}
uint4 sdfProgramWord(uint index) {
    if (index >= sdfProgramVectorCount()) { return 0u; }
    return sdfWords[index];
}
#ifdef SDF_DYNAMIC_TRANSFORMS
float4 sdfDynamicTransformRow(uint index) {
    uint count;
    uint stride;
    sdfDynamicTransforms.GetDimensions(count, stride);
    if (index >= count) { return 0.0; }
    return sdfDynamicTransforms[index];
}
#endif
// Program word stream (sdfWords, each element one uint4 = 16 bytes), read-only: the program is never written. Layout:
//   words[0]              = (instructionCount, materialCount, dataOffset, materialOffset)
//   words[1 .. 1+N)       = instruction headers (op, shapeType, blendOp, materialId)
//   words[dataOffset ..]  = instruction data, 2 uint4 per instruction (data0, data1 as float bits)
//   words[matOffset ..] = materials, 20 uint4 each; see SdfProgram.Materials.cs and sdfMaterialLoad.
//   words[matOffset + 20*materialCount ..] = HOST-BAKED bounding-sphere table (SdfProgram.PackBounds), 2 uint4 per
//                           instruction: b0 = center/offset.xyz + radius (float bits), b1 = (mode, dynamicSlot,
//                           skipTo, 0) — map()'s exact Union early-out reads it; mode SDF_BOUND_NONE evaluates fully.
//   [.. segment directory ..] then the INSTANCE directory (SdfProgram.PackInstances): one
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
//                           collapses Reset/Translate/Rotate/TransformDynamic/Shape chains into direct local poses; mapCore bypasses the
//                           generic per-op switch for those segments, while the authored instruction range stays intact for every non-rigid
//                           segment and the CPU SdfFieldEvaluator.

// A bake lattice's instance mask is a DERIVED ceil(instanceCount/32) uints (sdfInstanceMaskWordCount), so the instance
// ceiling SDF_MAX_INSTANCES caps it at SDF_MAX_INSTANCES/32 = 2048 words. The sentinel instance-mask BASE means "every
// instance visible": sdfInstanceMaskWord then reads no buffer and returns all-ones words.
#define SDF_INSTANCE_MASK_ALL 0xFFFFFFFFu
#define SDF_INSTANCE_MASK_MAX_WORDS ((SDF_MAX_INSTANCES + 31u) / 32u)

// The instance-mask width in uints for a program: ceil(instanceCount/32), never below 1 (a zero-instance program
// keeps one all-zero word so the mask buffer indexing stays uniform). Used ONLY for the reader's inner word
// iteration — buffer INDEXING (entry width and lattice base) comes from the host-pushed
// passGroup.instanceMaskWordCount (worldInstanceMaskBase in frame/sdf-frame.hlsli). KEEP IN SYNC with
// SdfProgram.InstanceMaskWordCount — the host derives the pushed value and sizes the mask buffer with the
// identical formula.
uint sdfInstanceMaskWordCount(uint instanceCount) {
    return max(1u, ((instanceCount + 31u) >> 5u));
}
// One word of the caller's instance mask, already masked to the bits that can name a REAL instance (the
// all-ones sentinel — and any stale buffer tail — must never enumerate an instance index >= instanceCount): a buffer
// read at instanceMaskBase + wordIndex for a real mask, or all-ones for the SDF_INSTANCE_MASK_ALL sentinel (and for
// every kernel compiled without SDF_INSTANCE_MASKS, where no mask buffer is bound at all). wordIndex <
// sdfInstanceMaskWordCount(instanceCount) by contract, so `remaining` is always >= 1.
uint sdfInstanceMaskWord(uint instanceMaskBase, uint wordIndex, uint instanceCount) {
    if (instanceCount > SDF_MAX_INSTANCES || wordIndex >= sdfInstanceMaskWordCount(instanceCount)) { return 0u; }
    uint word = 0xFFFFFFFFu;

#ifdef SDF_INSTANCE_MASKS
    if (instanceMaskBase != SDF_INSTANCE_MASK_ALL) {
        uint count;
        uint stride;
        sdfInstanceMasks.GetDimensions(count, stride);
        if (instanceMaskBase >= count || wordIndex >= count - instanceMaskBase) { return 0u; }
        word = sdfInstanceMasks[instanceMaskBase + wordIndex];
    }
#endif

    uint remaining = (instanceCount - (wordIndex << 5u));

    return (word & ((remaining >= 32u) ? 0xFFFFFFFFu : ((1u << remaining) - 1u)));
}

// A real lattice mask stores one summary bit per primary word after its primary run. The all-visible sentinel has no
// device summary and retains the short linear walk.
bool sdfInstanceMaskHasSummary(uint instanceMaskBase) {
    bool hasSummary = false;

#ifdef SDF_INSTANCE_MASKS
    hasSummary = (instanceMaskBase != SDF_INSTANCE_MASK_ALL);
#endif

    return hasSummary;
}

// The SEGMENT directory's element offset in sdfWords: header -> materials -> shape-bound table -> segment directory.
// KEEP IN SYNC with SdfProgram's offset math.
uint sdfSegmentDirectoryOffset() {
    uint4 header = sdfProgramWord(0);

    return ((SDF_PROGRAM_MATERIAL_OFFSET(header) + (SDF_MATERIAL_VECTORS_PER_ENTRY * SDF_PROGRAM_MATERIAL_COUNT(header))) + (SDF_BOUND_RECORD_VECTORS * SDF_PROGRAM_INSTRUCTION_COUNT(header)));
}
// The INSTANCE directory's element offset, given a caller that ALREADY resolved the segment directory
// (sdfLoadProgramLayout has both in hand). DXC's SPIR-V backend runs no GVN over StructuredBuffer loads, so
// re-deriving them costs a real reload of sdfProgramWord(0) on Vulkan — the reason sdfLoadProgramLayout exists, so mapCore
// does not re-run this whole chain on every sample.
uint sdfInstanceDirectoryOffsetFrom(uint segmentOffset, uint segmentCount) {
    return (segmentOffset + SDF_DIRECTORY_HEADER_VECTORS + (SDF_BOUND_RECORD_VECTORS * segmentCount));
}
// The INSTANCE directory's element offset in sdfWords — the ONE resolution of the packed offset chain for callers that
// hold nothing yet. Every consumer that touches the directory locates it through this or its `From` sibling.
uint sdfInstanceDirectoryOffset() {
    uint segmentOffset = sdfSegmentDirectoryOffset();

    return sdfInstanceDirectoryOffsetFrom(segmentOffset, SDF_SEGMENT_COUNT(sdfProgramWord(segmentOffset)));
}
// The per-PROGRAM Lipschitz STEP SCALE (1/L in (0, 1]), baked HOST-SIDE into the segment-directory header's otherwise-
// free .y lane (SdfProgram.AnalyzeLipschitz). mapCore multiplies EVERY returned distance by it, so a consumer that
// compares that distance against a WORLD-space quantity (a penumbra ratio, a footprint threshold) rather than taking a
// STEP with it must divide the clamp back out. == 1.0 exactly for an isometric, warp-free program (x * 1.0f == x to the
// bit for every finite x), so those scenes stay byte-identical. The `> 0` guard keeps a pre-writer all-zero stream
// rendering as before.
float sdfStepScale() {
    float stepScale = asfloat(SDF_SEGMENT_STEP_SCALE(sdfProgramWord(sdfSegmentDirectoryOffset())));

    return ((stepScale > 0.0) ? stepScale : 1.0);
}
// The element offset of instance `index`'s directory entry (i0 = bound, i1 = meta at +1) within the directory at
// `instanceOffset` — the ONE statement of the 2-uint4-per-instance entry stride.
uint sdfInstanceEntryOffset(uint instanceOffset, uint index) {
    return (instanceOffset + SDF_DIRECTORY_HEADER_VECTORS + (SDF_BOUND_RECORD_VECTORS * index));
}
// The packed instance count (the directory's header lane).
uint sdfInstanceCount() {
    return SDF_INSTANCE_COUNT(sdfProgramWord(sdfInstanceDirectoryOffset()));
}

// The ceiling-clamped instance count. The mask-buffer indexing contract itself (entry width, lattice base) lives in
// frame/sdf-frame.hlsli's worldInstanceMaskBase: both bake kernels resolve it from the host-pushed
// passGroup.instanceMaskWordCount (the bake mask WRITES lattice `lattice`'s words, the bake hands mapCore the SAME
// entry's base).
uint sdfInstanceCountClamped() {
    return min(sdfInstanceCount(), SDF_MAX_INSTANCES);
}

// Per-frame dynamic entity transforms (the world render path only). Each moving entity (player/enemy/carried screen)
// owns a slot of THREE rows: element 3*slot is its world position (xyz),
// 3*slot+1 its orientation quaternion (xyzw), 3*slot+2 its Lanes carrier (components 0 through 3 —
// Puck.SignedDistance.DynamicTransform.Lanes). The SDF_OP_TRANSFORM_DYNAMIC opcode reads the rigid transform AND the
// lanes from here by slot index, so an entity moves and its anonymous state updates by writing this small
// buffer instead of re-uploading the static program — the same way the camera moves via the per-frame viewport
// table. The kernels read it through sdfDynamicTransforms.

// The single font atlas the SDF_SHAPE_GLYPH primitive samples as a DISTANCE-level field (SetGlyphAtlas uploads it
// once), read through sdfGlyphAtlas and the nearest sampler. Sampled with EXPLICIT LOD only (SampleLevel):
// implicit-derivative filtering is undefined in a compute kernel's non-uniform control flow, and manual bilinear (sdfGlyphSampleField) reads the true single-channel distance from ALPHA, so a nearest
// sampler is all it needs.

// The persistent brick pool the SDF_SHAPE_SAMPLED_REGION primitive samples through sdfBrickPool: one float per voxel
// (f32), a flat device-local buffer the bake kernel writes and every brick instance indexes at its host-baked
// brickWordOffset (data1.z). Only the kernels that DEFINE SDF_SAMPLED_REGIONS sample it (the mesh bake); every other
// kernel compiles the conservative union-hull fallback instead.

// --- instruction lanes ---
// SDF_SHAPE_DETAIL_FLAG (Puck.SignedDistance.SdfInstruction.Detail) and SDF_SHAPE_NO_SECONDARY_FLAG
// (SdfInstruction.Secondary == false) mark a shape the bake samples like any other: a detail shape's material wins its
// footprint on the baked mesh, and a secondary-excluded shape still bakes into the mesh its cascades draw.
// SDF_OP_SYMMETRY_PLANE reproduces the axis-aligned folds with an axis normal.
// Scoped field accumulator (SdfOp.PushField/PopField). PUSH saves the immediate parent and reseeds a fresh field;
// POP composes only into that parent. The walk uses the bounded SDF_MAX_FIELD_SCOPE_DEPTH stack; the program validator
// guarantees balanced, single-owner scopes.
// Gaussian push: Data0=center/push.x, Data1=radii/push.y, header.y=push.z bits.
// Per-shape lane-driven erosion (SdfOp.LaneErode): ordered immediately before the SdfOp.ShapeBlend it targets.
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
// load-bearing shading vector (sunDiffuse, sdfMaterialShade's half-vector) the SAME BITS on both
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
// ellipsoid — the ISA's one ellipsoid spelling — and runs a pow-free fast path (sdfEllipsoidGauge); any other exponent
// takes the general power path.
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
