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
// ceil(instanceCount/32) uints (sdfInstanceMaskWordCount), so the ceiling caps it at SDF_MAX_INSTANCES/32 = 32
// words. KEEP IN SYNC with SdfProgramBuilder.MaxInstances.
#define SDF_MAX_INSTANCES 1024u
// Sentinel instance-mask BASE meaning "every instance visible" (sdfInstanceMaskWord then reads no buffer and
// returns all-ones words). Every map() CONSUMER that cannot reach the beam-computed per-tile mask (the debug frag
// view, the ray-query debug kernel, the beam prepass's own cone march) passes this, so an instanced program still
// renders its complete picture through them; only sdf-world-views.comp narrows it to a real per-tile mask base.
#define SDF_INSTANCE_MASK_ALL 0xFFFFFFFFu

#ifdef SDF_INSTANCE_MASKS
// The per-tile instance mask sdf-beam.comp wrote (world render path, Stage 1 ONLY): a flat uint buffer,
// params.instanceMaskWordCount (the host-pushed live program width) elements per (viewport, tile) entry, same
// (viewport, tile) indexing as the cull buffer.
// register(t9): the first SRV slot free of Stage 1's program/viewport/dynamicTransforms/cullBounds/screenSurfaces/
// screenSources run (t0..t8). KEEP IN SYNC with SdfWorldEngine's InstanceMaskBindingIndex.
[[vk::binding(7, 0)]] StructuredBuffer<uint> sdfInstanceMasks : register(t9);
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

// The INSTANCE directory's element offset in sdfWords — the ONE resolution of the packed offset chain (header →
// materials → shape-bound table → segment directory → instance directory; KEEP IN SYNC with SdfProgram's offset
// math). Every consumer that touches the directory locates it through this.
uint sdfInstanceDirectoryOffset() {
    uint4 header = sdfWords[0];
    uint boundsOffset = (header.w + (2u * header.y));
    uint segmentOffset = (boundsOffset + (2u * header.x));
    uint segmentCount = sdfWords[segmentOffset].x;

    return (segmentOffset + 1u + (2u * segmentCount));
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

#ifdef SDF_DYNAMIC_TRANSFORMS
// Per-frame dynamic entity transforms (the world render path only). Each moving entity (player/enemy/carried screen)
// owns a slot: element 2*slot is its world position (xyz), 2*slot+1 its orientation quaternion (xyzw). The
// SDF_OP_TRANSFORM_DYNAMIC opcode reads its rigid transform from here by slot index, so an entity moves by updating
// this small buffer instead of re-uploading the static program — the same way the camera moves via the per-frame
// viewport table. register(t2) follows the program (t0) and the world viewport table (t1, in sdf-world.hlsli).
[[vk::binding(9, 0)]] StructuredBuffer<float4> sdfDynamicTransforms : register(t2);
#endif

// --- opcodes (mirror Puck.SdfVm.SdfOp / legacy AvatarSdfInstructionOp numbering) ---
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
#define SDF_OP_SYMMETRY_X      13u
#define SDF_OP_SYMMETRY_Y      14u
#define SDF_OP_SYMMETRY_Z      15u
#define SDF_OP_ONION           16u
#define SDF_OP_DILATE          17u
#define SDF_OP_WALLPAPER_FOLD  18u
#define SDF_OP_TWIST_Y         20u

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

#define SDF_SQRT3 1.7320508

// --- primitives ---
#define SDF_SHAPE_BOX          0u
#define SDF_SHAPE_CAPSULE      1u
#define SDF_SHAPE_SPHERE       2u
#define SDF_SHAPE_TORUS        3u
#define SDF_SHAPE_CYLINDER     4u
#define SDF_SHAPE_PLANE        5u
#define SDF_SHAPE_ELLIPSOID    6u
#define SDF_SHAPE_ROUND_CONE   11u
#define SDF_SHAPE_SCREEN_SLAB  14u

// --- bounding-sphere entry modes (mirror Puck.SdfVm.SdfProgram's PackBounds) ---
#define SDF_BOUND_NONE    0u
#define SDF_BOUND_STATIC  1u
#define SDF_BOUND_DYNAMIC 2u

// --- blend operators ---
#define SDF_BLEND_UNION               0u
#define SDF_BLEND_SMOOTH_UNION        1u
#define SDF_BLEND_SUBTRACTION         2u
#define SDF_BLEND_INTERSECTION        3u
#define SDF_BLEND_XOR                 4u
#define SDF_BLEND_SMOOTH_INTERSECTION 5u
#define SDF_BLEND_SMOOTH_SUBTRACTION  6u

// Material sentinel range: a SCREEN_SLAB shades as a "screen" rather than a table albedo. The plain sentinel
// (SdfProgramBuilder.ScreenSlab with no screen index) shades the procedural test-card. SDF_SCREEN_MATERIAL + 1 +
// screenIndex (SdfProgramBuilder's screen-surface overload) additionally identifies WHICH declared screen surface —
// and so which screen source slot (0..3) — the hit belongs to, decoded as (material - SDF_SCREEN_MATERIAL - 1).
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

    if ((group == SDF_WPG_P3) || (group == SDF_WPG_P6)) {
        // Turn count = the hex lattice 3-coloring (every corner touches one hex of each color), satisfying the
        // corner-rotation cocycle: the pattern gains 3-fold centers at the corners without any in-cell rotation seam.
        float turns = sdfFloorMod((roundedA - roundedB), 3.0);

        // A 120-degree rotation, applied once or twice (GLSL mat2(-0.5, s, -s, -0.5) * r, written out).
        if (turns >= 1.0) {
            r = float2(((-0.5 * r.x) - ((SDF_SQRT3 * 0.5) * r.y)), (((SDF_SQRT3 * 0.5) * r.x) - (0.5 * r.y)));
        }

        if (turns >= 2.0) {
            r = float2(((-0.5 * r.x) - ((SDF_SQRT3 * 0.5) * r.y)), (((SDF_SQRT3 * 0.5) * r.x) - (0.5 * r.y)));
        }

        if ((group == SDF_WPG_P6) && (r.y < 0.0)) {
            // The in-cell half-turn composes with the corner 3-folds into p6's 6-fold centers; its seam is the
            // single horizontal diameter of each hex.
            r = -r;
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
            // Mirror across the quadrant anti-diagonal (through the edge-midpoint 2-fold centers, off the 4-fold
            // centers): p4g.
            float signU = ((r.x >= 0.0) ? 1.0 : -1.0);
            float signV = ((r.y >= 0.0) ? 1.0 : -1.0);
            float crossSign = (signU * signV);

            r = float2(
                ((signU * 0.5 * cell.x) - (crossSign * r.y)),
                ((signV * 0.5 * cell.x) - (crossSign * r.x)));
        }

        return r;
    }

    if ((group == SDF_WPG_CMM) && (r.y < 0.0)) {
        // Half-turn fold about the cell center keeps the 2-fold centers off the boundary mirrors (orbifold 2*22);
        // the cmm sign pair below adds the mirrors.
        r = -r;
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

    return (r * foldSign);
}

// The cell key the parity-material stride multiplies: the hex lattice's 3-coloring for the hex groups (matching the
// P3/P6 turn-count cocycle, so colors and rotations stay in sync), the checkerboard parity for the square-lattice
// groups. Survives the symmetry LOD (the lattice is what the LOD keeps), so distant cells hold their colors.
int sdfWallpaperCellKey(uint group, float2 cellIndex) {
    return ((group >= SDF_WPG_P3)
        ? (int)(sdfFloorMod((cellIndex.x - cellIndex.y), 3.0) + 0.5)
        : (int)(sdfFloorMod((cellIndex.x + cellIndex.y), 2.0) + 0.5));
}

float sdfSphere(float3 p, float radius) {
    return (length(p) - radius);
}
float sdfBox(float3 p, float3 halfExtents, float round) {
    float3 q = (abs(p) - (halfExtents - round));
    return ((length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0)) - round);
}
float sdfTorus(float3 p, float major, float minor) {
    float2 q = float2((length(p.xz) - major), p.y);
    return (length(q) - minor);
}
float sdfPlane(float3 p, float3 normal, float offset) {
    return (dot(p, normal) + offset);
}
// inverseLengthSquared = 1/dot(endpoint, endpoint), baked HOST-SIDE by SdfProgramBuilder.Capsule (data1.y): shapes
// evaluate millions of times per frame while programs build once, and the shared multiply keeps the two backends'
// codegen on the identical operation (a varying-numerator divide contracted differently was a cross-backend
// fuzz-signature risk).
float sdfCapsule(float3 p, float3 endpoint, float radius, float inverseLengthSquared) {
    float h = clamp((dot(p, endpoint) * inverseLengthSquared), 0.0, 1.0);
    return (length(p - (h * endpoint)) - radius);
}
float sdfCylinder(float3 p, float radius, float halfHeight) {
    float2 d = (float2(length(p.xz), abs(p.y)) - float2(radius, halfHeight));
    return (min(max(d.x, d.y), 0.0) + length(max(d, 0.0)));
}
// Not an exact distance (a first-order approximation): accurate near the surface, degrades with high
// eccentricity — keep the radii within a moderate aspect ratio. inverseRadii = 1/max(abs(radii), ε), baked
// HOST-SIDE by SdfProgramBuilder.Ellipsoid (data1.yzw) — two vector divides saved per evaluation.
float sdfEllipsoid(float3 p, float3 inverseRadii) {
    float3 q = (p * inverseRadii);
    float k0 = length(q);
    float k1 = length(q * inverseRadii);
    return ((k0 * (k0 - 1.0)) / max(k1, 0.0001));
}
// slope (b) and its complement (a = sqrt(1 - b²)) are baked HOST-SIDE by SdfProgramBuilder.RoundCone
// (data0.w / data1.y) — a divide and a sqrt saved per evaluation.
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

// Single-return (a result variable rather than returning inside the switch) so the compiler's flow analysis
// sees the value is always initialized — no "potentially uninitialized" warning. data1's .x lane is the ISA-wide
// smooth-blend radius; lanes .yzw carry HOST-BAKED derived constants per shape (see SdfProgramBuilder).
float evaluateShape(uint shapeType, float3 p, float4 data0, float4 data1) {
    float result = 1.0e9;

    switch (shapeType) {
        case SDF_SHAPE_SPHERE:      result = sdfSphere(p, data0.x); break;
        case SDF_SHAPE_BOX:         result = sdfBox(p, data0.xyz, data0.w); break;
        case SDF_SHAPE_SCREEN_SLAB: result = sdfBox(p, data0.xyz, data0.w); break;
        case SDF_SHAPE_TORUS:       result = sdfTorus(p, data0.x, data0.y); break;
        // The plane normal is normalized HOST-SIDE (SdfProgramBuilder.Plane) — the biggest per-eval saving of all:
        // the plane is in nearly every scene, and this normalize used to run for EVERY map() sample.
        case SDF_SHAPE_PLANE:       result = sdfPlane(p, data0.xyz, data0.w); break;
        case SDF_SHAPE_ROUND_CONE:  result = sdfRoundCone(p, data0.x, data0.y, data0.z, data0.w, data1.y); break;
        case SDF_SHAPE_CAPSULE:     result = sdfCapsule(p, data0.xyz, data0.w, data1.y); break;
        case SDF_SHAPE_CYLINDER:    result = sdfCylinder(p, data0.x, data0.y); break;
        case SDF_SHAPE_ELLIPSOID:   result = sdfEllipsoid(p, data1.yzw); break;
    }

    return result;
}
// The polynomial smooth minimum every smooth blend derives from (smoothIntersection/smoothSubtraction are its
// negations, so all three share one seam).
float blendSmoothUnion(float a, float b, float k) {
    float h = clamp((0.5 + ((0.5 * (b - a)) / k)), 0.0, 1.0);

    return (lerp(b, a, h) - ((k * h) * (1.0 - h)));
}
float blendShape(float current, float candidate, uint blendOp, float smoothRadius) {
    float result = min(current, candidate); // SDF_BLEND_UNION (the default)

    switch (blendOp) {
        case SDF_BLEND_SMOOTH_UNION: {
            result = blendSmoothUnion(current, candidate, max(smoothRadius, 0.0001));
            break;
        }
        case SDF_BLEND_SUBTRACTION:  result = max(current, -candidate); break;
        case SDF_BLEND_INTERSECTION: result = max(current, candidate); break;
        case SDF_BLEND_XOR:          result = max(min(current, candidate), -max(current, candidate)); break;
        case SDF_BLEND_SMOOTH_INTERSECTION: {
            result = -blendSmoothUnion(-current, -candidate, max(smoothRadius, 0.0001));
            break;
        }
        case SDF_BLEND_SMOOTH_SUBTRACTION: {
            result = -blendSmoothUnion(candidate, -current, max(smoothRadius, 0.0001));
            break;
        }
    }

    return result;
}

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

        uint4 instanceMeta = sdfWords[sdfInstanceEntryOffset(instanceOffset, instanceIndex) + 1u];

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
    uint segmentCount = sdfWords[segmentOffset].x;
    uint instanceOffset = sdfInstanceDirectoryOffset();
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
    float distanceScale = 1.0;
    // The texturing half of an active wallpaper fold: the cell key times the fold's material stride, added to the
    // material id of later shape wins in the chain (never to the screen sentinel). Reset with the chain.
    int parityMaterialDelta = 0;
    SdfHit result;

    result.distance = 1.0e9;
    result.material = 0;

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
                case SDF_OP_SCALE: {
                    float3 scale = max(abs(data0.xyz), float3(0.0001, 0.0001, 0.0001));
                    localPosition /= scale;
                    distanceScale *= min(scale.x, min(scale.y, scale.z));
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
                    float3 spacing = max(data0.xyz, float3(0.001, 0.001, 0.001));
                    localPosition -= (spacing * clamp(round(localPosition / spacing), -data1.xyz, data1.xyz));
                    break;
                }
                case SDF_OP_SYMMETRY_X: { localPosition.x = abs(localPosition.x); break; }
                case SDF_OP_SYMMETRY_Y: { localPosition.y = abs(localPosition.y); break; }
                case SDF_OP_SYMMETRY_Z: { localPosition.z = abs(localPosition.z); break; }
                // The warps rotate a plane pair by an angle keyed on one coordinate (GLSL mat2(c,-s,s,c) * v, written as
                // explicit components: x' = c*x + s*y, y' = -s*x + c*y). NOT isometries — space stretches tangentially —
                // so authored rates stay moderate (the validator bounds them).
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
                    // The legacy quirk, ported as-is: the angle keys on Y (like BEND_Y), the rotation acts on YZ.
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
                case SDF_OP_WALLPAPER_FOLD: {
                    // header lanes: y = the wallpaper group, z = the fold plane (0 = XZ, 1 = XY, 2 = YZ), w = the
                    // parity-material stride. The fold is an isometry, so distanceScale is untouched.
                    uint group = instructionHeader.y;
                    uint plane = instructionHeader.z;
                    int axisA = ((plane == 2u) ? 1 : 0);
                    int axisB = ((plane == 1u) ? 1 : 2);
                    // The symmetry LOD is PER SAMPLE (not per ray): every map() consumer — beam cone-march, pixel march,
                    // the 6-tap normal, RT shadows — samples the identical field, so cull and march can never disagree.
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
                    uint blendOp = instructionHeader.z;
                    int material = (int)instructionHeader.w;
                    float candidate = (evaluateShape(shapeType, localPosition, data0, data1) * distanceScale);

                    // An active wallpaper stride recolors this shape by its cell key — never a screen sentinel (the whole
                    // >= SDF_SCREEN_MATERIAL range, not just the exact value: a screen-instance id must survive intact).
                    if ((material < SDF_SCREEN_MATERIAL) && (parityMaterialDelta != 0)) {
                        material += parityMaterialDelta;
                    }

                    // Per-op material OWNERSHIP (the legacy avatar rules): union-like ops win when nearer, the
                    // intersection-like when farther (the surviving surface is the farther field's), and the
                    // subtraction-like when the CARVED surface shows (-candidate > current) — so a bowl carved from a
                    // box wears the carving shape's interior material.
                    bool shapeWins = false;

                    switch (blendOp) {
                        case SDF_BLEND_INTERSECTION:
                        case SDF_BLEND_SMOOTH_INTERSECTION: { shapeWins = (candidate > result.distance); break; }
                        case SDF_BLEND_SUBTRACTION:
                        case SDF_BLEND_SMOOTH_SUBTRACTION:  { shapeWins = (-candidate > result.distance); break; }
                        default:                            { shapeWins = (candidate < result.distance); break; }
                    }

                    if (shapeWins) {
                        result.material = material;
                    }

                    result.distance = blendShape(result.distance, candidate, blendOp, data1.x);
                    break;
                }
            }
        }
    }

    return result;
}

// The universal entry point every EXISTING consumer calls (sdf-view.frag.hlsl, sdf-world-rt-debug.rq.comp.hlsl,
// calculateNormal, the shadow/AO marches): every instance visible, so an instanced program still renders its
// complete picture — only the world render path's Stage 1 narrows the mask (see mapMasked).
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
    float specular;  // Blinn-Phong strength in [0, 1]; 0 = matte (the v1 look)
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
// The shared lit-surface shading of the material model: the v1 lambert expression UNCHANGED (all-zero new fields
// reproduce it exactly), plus a Blinn-Phong highlight and an emissive lift. `diffuse` is the caller's accumulated
// radiance (ambient + the sun + any colored screen lights — a float3 so colored lights tint the surface);
// `lightScale` scales the highlight by the caller's shadow/light attenuation.
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
