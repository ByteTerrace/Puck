// The per-workgroup shadow and ambient gather that builds one candidate mask for a group's hits.
#ifndef SURFACE_SDF_SHADOW_GATHER_HLSLI
#define SURFACE_SDF_SHADOW_GATHER_HLSLI
#ifdef SDF_SCREEN_SOURCES
#ifdef SDF_GROUP_SHADOW_GATHER
// The group's enclosing sphere widens its light cone; the shared grid walker preserves every candidate.
// World segments need no bit because mapCore always evaluates them.
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

uint sdfShadowGatherGroup(bool lit, float3 hitPoint, float3 direction, float reach, int lightIndex, uint lane) {
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

    float chord = worldShadowPenumbraChord(lightIndex); // this slot's soft penumbra cone
    float inverseAperture = rsqrt(max((1.0 - (chord * chord)), 1.0e-6));
    float groupReach = (reach + inflate);

    // PATH B — the shadow proxy (sdf.shadow-proxy): a SHADOW-TRANSPARENT instance (a host-flagged pure Subtraction-family
    // carve) is NOT added to the mask, so the soft-shadow march evaluates the pre-carve union hull. SOUNDNESS: the mask
    // IS the candidate set the march reads, so a skipped carve is simply never composed, and a Subtraction only ever
    // removes material, so the shadow is conservatively darker, never light-leaked. Default OFF.
    bool shadowProxy = worldShadowProxyEnabled();

    SdfGridQuery query = sdfGridCone(origin, direction, chord, inverseAperture, inflate, groupReach);
    SdfGridWalk walk = sdfGridWalkBegin(grid, query, lane, SDF_GROUP_SHADOW_LANES);
    uint index;
    [loop]
    while (sdfGridWalkNext(grid, query, walk, index)) {
        if (sdfGridQueryContains(query, sdfInstanceBoundAt(instanceOffset, index)) &&
            !(shadowProxy && sdfInstanceShadowTransparent(instanceOffset, index)) &&
            !sdfInstanceShadowSuppressed(instanceOffset, index)) {
            InterlockedOr(sdfShadowMaskWords[index >> 5u], (1u << (index & 31u)));
        }
    }
    // Every lane's bits are visible to every lane's march after this.
    GroupMemoryBarrierWithGroupSync();

    return 2u;
}
#endif
#endif

#endif
