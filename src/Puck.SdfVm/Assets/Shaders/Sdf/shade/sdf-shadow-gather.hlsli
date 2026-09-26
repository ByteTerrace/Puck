// The per-workgroup shadow and ambient gather that builds one candidate mask for a group's hits.
#ifndef SHADE_SDF_SHADOW_GATHER_HLSLI
#define SHADE_SDF_SHADOW_GATHER_HLSLI
#ifdef SDF_SCREEN_SOURCES
// The soft-shadow GRID-CULL A/B lever (the sdf.shadowcull verb). Rides SdfGridObjParams.w: 0 (the DEFAULT, an unset
// frame uploads 0) = ON — the grid-gathered shadow-ray march; 1 = OFF — the flat all-instances march (the ground-truth
// reference the departed cull gate matched, and the A/B lever's slow reference). KEEP IN SYNC with SdfFrame.DisableShadowCull
// and SdfWorldEngine.PackScreenLights.
bool worldShadowCullEnabled() {
    return (sdfScreenLights[SdfGridObjParams].w < 0.5);
}

#ifdef SDF_GROUP_SHADOW_GATHER
// Build the shadow-ray candidate mask into sdfShadowMaskWords for the soft-shadow marches of ONE 8x8 WORKGROUP — the
// per-tile gather that replaced the former per-lit-pixel gather. Every lane publishes its hit hitPoint (or none),
// lane 0 reduces the group's lit points to a apex and the radius R that encloses them, and the 64 lanes then walk
// the instance grid COOPERATIVELY along the penumbra cone apexed at the apex, testing every bound INFLATED by R
// (+ ShadowBias, the march origin's normal offset). SUPERSET-PRESERVING for every pixel in the group: a pixel's own
// gather cone is the apex cone translated by at most R, so an instance meeting the pixel cone lies within R of
// the apex cone and the inflated test admits it; and an admitted instance the pixel's ray never reaches composes
// as the accumulator to the bit (the bound-sizing contract mapMasked already rides), so the masked march of every
// lane equals the flat map() soft shadow TO THE BIT — the same argument the per-pixel gather made, widened by R. What
// changed is cost: one grid walk per 64 pixels instead of 64 divergent walks, the walk spread across the lanes, and
// the 32-word mask living in groupshared memory instead of 32 per-thread registers. The penumbra cone (chord
// worldShadowPenumbraChord) is unchanged. The walk is collectInstanceGridMask's SAME robust-slabs cone
// rasterization, capped at the march reach + R + footprintPad. KEEP THE WALK IN SYNC with
// sdf-instance-cull.comp.hlsl's collectInstanceGridMask (the device-buffer twin — a hand-maintained near-clone): same
// cell rasterization, same footprintPad contract; only the bit TARGET (the groupshared mask, InterlockedOr), the
// chord, the inflation, the lane striding, and the tExit cap differ. World segments need no bit — mapCore always
// evaluates them.
//
// UNIFORM CONTROL FLOW: every lane calls this with the same direction/reach
// (the lit lanes with their hitPoint, the rest with lit = false) — it carries group barriers, so no caller may skip it
// or call it under a per-lane branch. Returns the fallback DECISION (uniform across the group) so the caller marches
// correctly whether or not the mask was built:
//   2 = mask BUILT into sdfShadowMaskWords — march it (the cull); also the answer when no lane in the group is lit
//       (an empty mask nothing marches);
//   0 = NO grid packed (a grid-suppressed or few-instance program) — march the FLAT all-instances field, which for a
//       few-instance program is cheap AND, for a deliberately grid-suppressed program, MATCHES the grid-present gather
//       so the grid toggle stays render-invariant (the world-grid-cull grid==flat contract).
groupshared float4 sdfShadowGatherPoints[SDF_GROUP_SHADOW_LANES];
groupshared float4 sdfShadowGatherCone; // xyz = the hit points' apex, w = the enclosing radius + ShadowBias
groupshared uint sdfShadowGatherLitCount;
groupshared float3 sdfAmbientGatherLow;
groupshared float3 sdfAmbientGatherHigh;

uint sdfShadowGatherGroup(bool lit, float3 hitPoint, float3 direction, float reach, uint lane) {
    // Phase 0 — clear the group mask and publish this lane's hitPoint.
    for (uint word = lane; word < SDF_SHADOW_MASK_WORDS; word += SDF_GROUP_SHADOW_LANES) {
        sdfShadowMaskWords[word] = 0u;
    }

    sdfShadowGatherPoints[lane] = float4(hitPoint, (lit ? 1.0 : 0.0));
    GroupMemoryBarrierWithGroupSync();

    // Phase 1 — lane 0 reduces the lit points to the cone apex and its enclosing radius.
    if (lane == 0u) {
        float3 sum = float3(0.0, 0.0, 0.0);
        float count = 0.0;

        [loop]
        for (uint i = 0u; (i < SDF_GROUP_SHADOW_LANES); i++) {
            float4 entry = sdfShadowGatherPoints[i];

            if (entry.w > 0.5) {
                sum += entry.xyz;
                count += 1.0;
            }
        }

        float3 apex = ((count > 0.0) ? (sum / count) : float3(0.0, 0.0, 0.0));
        float radius = 0.0;

        [loop]
        for (uint j = 0u; (j < SDF_GROUP_SHADOW_LANES); j++) {
            float4 entry = sdfShadowGatherPoints[j];

            if (entry.w > 0.5) {
                radius = max(radius, length(entry.xyz - apex));
            }
        }

        sdfShadowGatherCone = float4(apex, (radius + ShadowBias));
        sdfShadowGatherLitCount = ((uint)count);
    }

    GroupMemoryBarrierWithGroupSync();

    float4 cone = sdfShadowGatherCone;
    uint litCount = sdfShadowGatherLitCount;
    float3 origin = cone.xyz;
    float inflate = cone.w;

    // The decisions below are uniform (program-level facts and the group's own count), so an early return here leaves
    // no lane behind at a later barrier.
    uint packedInstanceCount = sdfInstanceCount();
    uint instanceOffset = sdfInstanceDirectoryOffset();
    SdfInstanceGridHeader grid = sdfLoadInstanceGridHeader(instanceOffset, packedInstanceCount);

    if (!grid.enabled) {
        return 0u; // no grid — flat fallback (cheap for few instances; matches a would-be gather so the grid toggle is invariant)
    }

    // Stage 1's shared mask covers SDF_MAX_INSTANCES. A large reserved pool is still an exact gather; the caller
    // selects the camera-tile approximation only when that quality mode was requested.

    if (litCount == 0u) {
        return 2u; // nothing in this group marches a shadow; the cleared mask is complete
    }

    float chord = worldShadowPenumbraChord(); // the soft penumbra cone's half-slope, not a bare ray
    float inverseAperture = rsqrt(max((1.0 - (chord * chord)), 1.0e-6));
    float groupReach = (reach + inflate);

    // PATH B — the shadow proxy (sdf.shadow-proxy): a SHADOW-TRANSPARENT instance (a host-flagged pure Subtraction-family
    // carve) is NOT added to the mask, so the soft-shadow march evaluates the pre-carve union hull. SOUNDNESS: the mask
    // IS the candidate set the march reads, so a skipped carve is simply never composed, and a Subtraction only ever
    // removes material, so the shadow is conservatively darker, never light-leaked. Default OFF.
    bool shadowProxy = worldShadowProxyEnabled();

    // Phase 2 — the cooperative walk. (1) The ALWAYS-tested list — dynamic + unmaskable instances the frozen grid cannot
    // bin — strided across the lanes, each bound inflated by R against the penumbra cone.
    [loop]
    for (uint a = lane; (a < grid.alwaysCount); a += SDF_GROUP_SHADOW_LANES) {
        uint index = sdfGridWordAt(grid, grid.alwaysWord + a);
        float4 bound = sdfInstanceBoundAt(instanceOffset, index);

        if (bound.w >= 0.0) {
            bound.w += inflate;
        }

        // Per-instance shadow-participation skip (the cheaper-mask twin of sdfNextVisibleInstanceRange's enumeration
        // skip): a shadow-suppressed dynamic instance never enters the mask, so it costs no mask bit and mode-2's march
        // stays consistent with the enumerate-skip. Gated on the raw condition — the gather is inherently shadow-scoped.
        if (sdfInstancePassesTileCone(bound, origin, direction, chord, inverseAperture) && !(shadowProxy && sdfInstanceShadowTransparent(instanceOffset, index)) && !sdfInstanceShadowSuppressed(instanceOffset, index)) {
            InterlockedOr(sdfShadowMaskWords[index >> 5u], (1u << (index & 31u)));
        }
    }

    // (2) The grid cells the inflated penumbra cone sweeps, far bound capped at the group reach + the query pad — the
    // same robust-slabs clip + cell rasterization the beam cull uses (see collectInstanceGridMask). The slab walk is
    // uniform across the lanes; each slab's cell box is linearized and strided across them.
    float3 gridMin = grid.origin;
    float3 gridMax = (grid.origin + (float3(grid.dims) * grid.cellSize));
    float3 farCorner = float3(
        ((direction.x > 0.0) ? gridMax.x : gridMin.x),
        ((direction.y > 0.0) ? gridMax.y : gridMin.y),
        ((direction.z > 0.0) ? gridMax.z : gridMin.z)
    );
    float projection = max(dot((farCorner - origin), direction), 0.0);
    float tFar = min(((projection + grid.footprintPad + inflate) / max((1.0 - chord), 0.01)), (groupReach + grid.footprintPad));

    float pad = (grid.footprintPad + inflate); // the query pad, widened by the group's enclosing radius
    float inflateBox = ((chord * tFar) + pad);  // the widest query radius any slab uses (chord grows it with t)
    float3 clipMin = (gridMin - inflateBox);
    float3 clipMax = (gridMax + inflateBox);
    float tEnter = 0.0;
    float tExit = tFar;
    bool missesGrid = false;

    [unroll]
    for (int axis = 0; (axis < 3); axis++) {
        float dir = direction[axis];
        float ori = origin[axis];

        if (abs(dir) > 1.0e-8) {
            float tA = ((clipMin[axis] - ori) / dir);
            float tB = ((clipMax[axis] - ori) / dir);

            tEnter = max(tEnter, min(tA, tB));
            tExit = min(tExit, max(tA, tB));
        }
        else if ((ori < clipMin[axis]) || (ori > clipMax[axis])) {
            missesGrid = true; // the cone provably misses the grid on this axis — only the always-list bits matter
        }
    }

    if (!missesGrid && (tEnter <= tExit)) {
        int3 dimensionsMinusOne = (int3(grid.dims) - int3(1, 1, 1));
        float slabStep = (grid.cellSize * SDF_GRID_SLAB_CELLS);
        float t0 = tEnter;

        [loop]
        for (uint slab = 0u; (slab < SDF_GRID_MAX_SLABS); slab++) {
            float t1 = (((slab + 1u) < SDF_GRID_MAX_SLABS) ? min((t0 + slabStep), tExit) : tExit);
            float3 c0 = (origin + (direction * t0));
            float3 c1 = (origin + (direction * t1));
            float radius = ((chord * t1) + pad);
            float3 low = (min(c0, c1) - radius);
            float3 high = (max(c0, c1) + radius);

            if (all(high >= gridMin) && all(low <= gridMax)) {
                int3 cellLow = clamp(int3(floor((low - grid.origin) * grid.invCellSize)), int3(0, 0, 0), dimensionsMinusOne);
                int3 cellHigh = clamp(int3(floor((high - grid.origin) * grid.invCellSize)), int3(0, 0, 0), dimensionsMinusOne);
                uint3 span = uint3(cellHigh - cellLow) + uint3(1u, 1u, 1u);
                uint cellCount = ((span.x * span.y) * span.z);

                [loop]
                for (uint c = lane; (c < cellCount); c += SDF_GROUP_SHADOW_LANES) {
                    uint cx = (c % span.x);
                    uint rest = (c / span.x);
                    uint cy = (rest % span.y);
                    uint cz = (rest / span.y);
                    uint cell = (((((uint)cellLow.z + cz) * grid.dims.y) + ((uint)cellLow.y + cy)) * grid.dims.x) + ((uint)cellLow.x + cx);
                    uint entryStart = sdfGridWordAt(grid, grid.cellStartWord + cell);
                    uint entryEnd = sdfGridWordAt(grid, grid.cellStartWord + cell + 1u);

                    [loop]
                    for (uint k = entryStart; (k < entryEnd); k++) {
                        uint index = sdfGridWordAt(grid, grid.entryWord + k);
                        float4 bound = sdfInstanceBoundAt(instanceOffset, index);

                        if (bound.w >= 0.0) {
                            bound.w += inflate;
                        }

                        // Per-instance shadow-participation skip — same raw-condition test as the always-list loop above.
                        if (sdfInstancePassesTileCone(bound, origin, direction, chord, inverseAperture) && !(shadowProxy && sdfInstanceShadowTransparent(instanceOffset, index)) && !sdfInstanceShadowSuppressed(instanceOffset, index)) {
                            InterlockedOr(sdfShadowMaskWords[index >> 5u], (1u << (index & 31u)));
                        }
                    }
                }
            }

            if (t1 >= tExit) {
                break;
            }

            t0 = t1;
        }
    }

    // Every lane's bits are visible to every lane's march after this.
    GroupMemoryBarrierWithGroupSync();

    return 2u;
}
#endif
#endif

#endif
