// The hit record, the point transforms, the per-invocation map channels and the wallpaper folds.
#ifndef FIELD_SDF_POINT_HLSLI
#define FIELD_SDF_POINT_HLSLI
// The winning candidate's anonymous values and dynamic frame travel with its material.
struct SdfHit {
    float distance;
    int material;
    float4 lanes;
    int frameSlot;
};

SdfHit sdfIsaErrorHit() {
    SdfHit result;
    result.distance = 0.0;
    result.material = SDF_ISA_ERROR_MATERIAL;
    result.lanes = float4(0.0, 0.0, 0.0, 0.0);
    result.frameSlot = -1;
    return result;
}

float3 rotatePointByInverseQuaternion(float3 p, float4 q) {
    float3 u = -q.xyz;
    return (p + (2.0 * cross(u, ((q.w * p) + cross(u, p)))));
}
// Forward rotation R(q) p (the inverse's conjugate). The rigid-leaf dual maps a shape-LOCAL gradient to world by the
// leaf's forward rotation, mirroring the inverse rotation the rigid point walk applies (worldGrad = R(q) localGrad).
float3 rotatePointByQuaternion(float3 p, float4 q) {
    float3 u = q.xyz;
    return (p + (2.0 * cross(u, ((q.w * p) + cross(u, p)))));
}

// The symmetry-LOD origin (the marching camera's world position), set by each kernel's entry point before it
// marches. A wallpaper fold whose data1.z threshold is exceeded by distance(worldPosition, sdfLodOrigin) keeps its
// lattice but skips the in-cell folds. The 0 default (with threshold 0 = off) keeps any kernel that never sets it
// correct.
static float3 sdfLodOrigin = float3(0.0, 0.0, 0.0);

// The FOLD-SAFE STEP BOUND: the distance (in the same Lipschitz-clamped units as the returned field) from the last
// map sample to the nearest fold-cell boundary of any radial fold on its chain — or SDF_STEP_BOUND_NONE when the
// program folds nothing. A folded field measures only the NEAREST cell's copy, so its VALUE can OVERESTIMATE true
// distance near a cell boundary (the neighbor cell's geometry may be closer — the containment ≠ nearest-copy class
// the Repeat/CellJitter crease verdict documents); the sound marchable field is min(value, boundary gap). Marchers
// therefore STEP — and build cone-clearance proofs — with min(distance, sdfMapStepBound) while still TERMINATING on
// the raw value: the zero set is exact in the owning cell, so accepts stay honest and no phantom boundary surfaces
// appear. Unbounded, this is exactly the Droste tile-shatter: the beam's cone proof trusted an overestimating value
// and classified tiles straight through shell geometry. Written by mapCore on EVERY call (per-thread mutable static,
// the sdfLodOrigin pattern); a consumer reads it immediately after the map call it pairs with.
#define SDF_STEP_BOUND_NONE 1.0e30
static float sdfMapStepBound = SDF_STEP_BOUND_NONE;

// The MATERIAL BLEND CHANNEL (material-blend-at-seams). A smooth blend blends the two operands' DISTANCE smoothly, but
// result.material is an integer that can only carry ONE winner — so the material snaps as a HARD cut at the geometric
// seam even though the surface eased across it. The continuous material weight rides HERE instead, a separate per-thread
// channel captured at the winning smooth blend the SAME way sdfMapStepBound rides the fold state (and blendShapeDual
// carries the gradient alongside the distance). At every SMOOTH compose mapCore records the OTHER (losing) operand's
// material and the SYMMETRIC seam weight min(h, 1-h) in [0, 0.5] (0 at/beyond the band, 0.5 at the seam centre) — h is
// the SAME clamped smooth-blend factor blendShapeDual uses (KEEP IN SYNC). A HARD compose that flips the winner resets
// the weight to 0 (a hard material cut); a HARD compose the incumbent keeps leaves an earlier smooth seam's weight
// intact (the far shape never touched the incumbent's appearance). The shading epilogue (renderView, passes/sdf-render-view.hlsli)
// captures these at the accept-sample march call and lerps the winner's albedo toward `other` by the weight — HIT-ONLY,
// one lerp per lit pixel, never a per-step map eval. Both ids are POST-parityMaterialDelta (a wallpaper-recoloured seam
// blends its recoloured albedos), so the mixed colour stays inside the same relaxed material-flip parity family the
// hard winner-flip already lived in. Weight 0 (the reset default, every hard/far seam) leaves the albedo the exact table
// lookup — byte-identical shading for any pixel with no smooth material seam within a blend radius of the hit.
static float sdfMaterialBlendWeight = 0.0;
static int sdfMaterialBlendOther = 0;

// mapCore/mapGradCore's march-vs-shade mode: false (every march sample — the beam cone, the fine march, shadow, AO,
// including their rigid-leaf fast paths) skips a
// SDF_SHAPE_DETAIL_FLAG shape entirely, so it never appears in the marched hit, the collider, or the step bound.
// renderView and sdfResolveSurface flip it true for exactly the lifetime of hit-only material/normal re-evaluation
// at the already-found surface point, so a detail shape's local perturbation and material win only there. False
// everywhere else, so an unauthored program renders byte-identical.
static bool sdfDetailShadingActive = false;

// mapCore/mapGradCore's secondary-ray exclusion mode: false (the primary/beam/fine march, the normal probes, and the
// hit-only shade/detail re-evaluations) carries a SDF_SHAPE_NO_SECONDARY_FLAG shape like any ordinary shape. true
// (renderView's shadow calls and sdfResolveAmbient's AO calls, only for their duration) skips
// it — the study's secondaryScene posture: a shape marked secondary=false still shades and collides, it just casts
// no shadow and contributes no AO. False everywhere else, so an unauthored program renders byte-identical.
static bool sdfSecondaryMarchActive = false;

// Shared by scalar/gradient, rigid/generic walks. Flags control participation in the field, not the primitive id.
bool sdfShapeEnabled(uint packedShapeType) {
    return (((packedShapeType & SDF_SHAPE_DETAIL_FLAG) == 0u) || sdfDetailShadingActive)
        && (((packedShapeType & SDF_SHAPE_NO_SECONDARY_FLAG) == 0u) || !sdfSecondaryMarchActive);
}

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
// transform is keyed on the lattice PARITY (P2/PG/CM/PMG/PGG/CMM/P4/P4M) or on the hex 3-coloring (P3/P6). Those
// realize their point group over a sublattice: the parity groups repeat over the CENTERED/doubled cell (period
// 2*cell, plus the (1,1) centering vector), the P3/P6 turn cocycle over the √3×√3 hex supercell. P4G is the
// exception among the square groups: it folds directly to a fundamental wedge (a C4 reduction about the cell center
// plus the offset-diagonal mirror), so its translation period is exactly `cell` — a pair of opposed 4-fold centers
// (cell center and cell corner) compose to the unit translation, unlike P4/P4M's period-2 rotated-block realization.
// P1/PM/PMM, P4G, and the pure dihedral hex kaleidoscopes (P3M1/P31M/P6M) have period == cell. Verified by direct
// translation-invariance test over all 17 groups.
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

    if (group == SDF_WPG_P4G) {
        // p4g (orbifold 4*2). Its point group is 4mm like p4m, but the mirror lines run BETWEEN the 4-fold centers
        // (through the edge-midpoint 2-fold centers), and the axis directions carry GLIDES, not mirrors — the 4-fold
        // centers themselves sit on NO mirror. The parity turn-cocycle P4/P4M ride cannot host it: the offset mirror
        // sigma (x + y = cell/2) maps a point to a cell whose index depends on the SUB-cell SIGN of r, which the
        // parity key (cellIndex mod 2) cannot see — so composing sigma with the cocycle leaves no surviving mirror
        // class and the group collapses to p4. P4G therefore folds directly to a
        // fundamental wedge using only genuine p4g isometries: a C4 rotation about the cell center reduces r to one
        // quadrant, then a single reflection across the offset diagonal x + y = cell/2 (a real p4g mirror, through the
        // 2-fold centers, NOT the 4-fold center) halves that quadrant. The point group at the 4-fold center is then
        // exactly C4 — rotation only, no through-center mirror — the signature that separates p4g from p4m. Like p4,
        // p4g has rotation-only 4-fold centers, so it carries rotation seams (the quadrant walls); content must clear
        // them, exactly as for P4/P2/CMM. Verified against a brute-force p4g orbit oracle: the fold is constant on
        // p4g orbits (4-fold, offset mirror, both unit translations, the axis glide, the corner 4-fold) and its
        // point group at the center is C4 with zero through-center mirrors.
        if (r.y < 0.0) {
            // Quadrant III -> +180 into I, quadrant IV -> +90 into I.
            r = ((r.x >= 0.0) ? float2(-r.y, r.x) : float2(-r.x, -r.y));
        }
        else if (r.x < 0.0) {
            // Quadrant II -> -90 into I.
            r = float2(r.y, -r.x);
        }

        if ((r.x + r.y) > (0.5 * cell.x)) {
            // Reflect across the offset diagonal x + y = cell/2 (through the edge-midpoint 2-fold centers): the mirror
            // required glide-mirror class. This is a pure reflection (swap-and-shift), an isometry.
            r = float2(((0.5 * cell.x) - r.y), ((0.5 * cell.x) - r.x));
        }

        return r;
    }

    if ((group == SDF_WPG_P4) || (group == SDF_WPG_P4M)) {
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

// The R2 low-discrepancy lattice: alpha_i = round(2^32 / phi2^i) for the plastic number
// phi2 = 1.32471795724474602596 (the real root of x^3 = x + 1). The uint multiply wraps mod 2^32, which IS the
// fractional part of the additive recurrence — so the lattice is exact in fixed point.
#define SDF_R2_ALPHA1 3242174889u
#define SDF_R2_ALPHA2 2447445414u
// The R3 siblings: alpha_i = round(2^32 / phi3^i) for phi3 = 1.2207440846057596 (the real root of x^4 = x + 1).
// SDF_OP_CELL_JITTER's Blue flavor rotates these three across its axes so the offset components decorrelate.
#define SDF_R3_ALPHA1 3518319155u
#define SDF_R3_ALPHA2 2882110345u
#define SDF_R3_ALPHA3 2360945575u

#endif
