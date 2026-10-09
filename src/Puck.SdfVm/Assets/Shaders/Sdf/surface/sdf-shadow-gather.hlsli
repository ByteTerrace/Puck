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
#ifdef SDF_SHADOW_PASS
groupshared uint sdfShadowGatherMoved;
static bool sdfShadowMotionActive = false;

bool sdfShadowTransformMoved(uint slot) {
    uint row = (3u * slot);
    return (any(asuint(sdfDynamicTransforms[row]) != asuint(sdfPreviousDynamicTransforms[row])) ||
        any(asuint(sdfDynamicTransforms[row + 1u]) != asuint(sdfPreviousDynamicTransforms[row + 1u])) ||
        any(asuint(sdfDynamicTransforms[row + 2u]) != asuint(sdfPreviousDynamicTransforms[row + 2u])));
}

// Flat and camera-mask fallbacks have no complete shadow gather. Their conservative candidate set is the whole
// table. World segments also run outside every instance mask, so their dynamic rows cannot be excluded by a gather.
void sdfShadowMovedFlat(uint lane) {
    uint rows, stride;
    sdfDynamicTransforms.GetDimensions(rows, stride);
    [loop] for (uint slot = lane; (3u * slot + 2u) < rows; slot += SDF_GROUP_SHADOW_LANES) {
        if (sdfShadowTransformMoved(slot)) { InterlockedOr(sdfShadowGatherMoved, 1u); }
    }
}

// Test both placements of a moved dynamic occluder. A body leaving this group's cone still invalidates the shadow
// it left on the receiver; neither participation suppression nor the current mask can hide its preceding bound.
void sdfShadowMovedCandidate(uint instanceOffset, uint index, float3 origin, float3 direction, float chord,
    float inverseAperture, float inflate) {
    uint entry = sdfInstanceEntryOffset(instanceOffset, index);
    uint4 meta = sdfWords[entry + 1u];
    if ((meta.x == SDF_BOUND_DYNAMIC) && sdfShadowTransformMoved(meta.y)) {
        float4 bound = asfloat(sdfWords[entry]);
        if (bound.w < 0.0) { return; }
        bound.w += inflate;
        float4 previous = bound;
        bound.xyz += sdfDynamicTransforms[3u * meta.y].xyz;
        previous.xyz += sdfPreviousDynamicTransforms[3u * meta.y].xyz;
        if (sdfInstancePassesTileCone(bound, origin, direction, chord, inverseAperture) ||
            sdfInstancePassesTileCone(previous, origin, direction, chord, inverseAperture)) {
            InterlockedOr(sdfShadowGatherMoved, 1u);
        }
    }
}
#endif

// Summarizes the group mask just built (the ambient mask when `ambient`) into sdfGroupMaskSpheres/Scales/Kept, one entry
// per summarized word: the box of its set instances' current bound spheres, then the radius reaching every one of them
// from the box's center. An instance whose radius exceeds `oversized` (the grid's largest binned radius, the split that
// keeps a ground or a building in the grid's always-list) stays out of the sphere and in the word's kept bits, so one
// large neighbour cannot hide a body's whole word. UNIFORM CONTROL FLOW like the gathers: every lane calls it, after the
// barrier that completes the mask; it ends with its own barrier. Only a program under the root-union certificate is
// summarized (each instance then joins as a hard union, which the word rejection relies on), and every instance carries
// its field rescale's inverse in the part table.
void sdfSummarizeGroupMask(bool ambient, float oversized, uint lane) {
    uint table = sdfProgramLayout.partProgramOffset;
    sdfGroupMaskSummarized = ((table != 0u) && sdfCanTracePartsIndependently());
    if (!sdfGroupMaskSummarized) { return; }
    uint count = min(sdfInstanceCount(), SDF_MAX_INSTANCES);
    uint offset = sdfInstanceDirectoryOffset();
    uint words = min(sdfInstanceMaskWordCount(count), SDF_GROUP_MASK_SUMMARY_WORDS);
    for (uint word = lane; word < words; word += SDF_GROUP_SHADOW_LANES) {
        uint bits = (ambient ? sdfAmbientMaskWords[word] : sdfShadowMaskWords[word]);
        float3 low = float3(1.0e30, 1.0e30, 1.0e30);
        float3 high = float3(-1.0e30, -1.0e30, -1.0e30);
        float scale = 1.0;
        uint kept = 0u;
        uint summarized = 0u;
        [loop] for (uint pending = bits; pending != 0u; pending &= (pending - 1u)) {
            uint bit = firstbitlow(pending);
            uint index = ((word << 5u) + bit);
            float4 bound = sdfInstanceBoundAt(offset, index);
            if (bound.w < 0.0) { continue; }
            if (bound.w > oversized) { kept |= (1u << bit); continue; }
            low = min(low, (bound.xyz - bound.w));
            high = max(high, (bound.xyz + bound.w));
            float inverseRescale = asfloat(sdfProgramWord(table + 1u + index).w);
            scale = min(scale, ((inverseRescale > 0.0) ? inverseRescale : 0.0));
            summarized |= (1u << bit);
        }
        float3 center = (0.5 * (low + high));
        float radius = -1.0;
        if ((summarized != 0u) && (scale > 0.0)) {
            radius = 0.0;
            [loop] for (uint pending = summarized; pending != 0u; pending &= (pending - 1u)) {
                float4 bound = sdfInstanceBoundAt(offset, ((word << 5u) + firstbitlow(pending)));
                radius = max(radius, (length(bound.xyz - center) + bound.w));
            }
        }
        sdfGroupMaskSpheres[word] = float4(center, radius);
        sdfGroupMaskScales[word] = scale;
        sdfGroupMaskKept[word] = kept;
    }
    GroupMemoryBarrierWithGroupSync();
}

uint sdfShadowGatherGroup(bool lit, float3 hitPoint, float3 direction, float reach, int lightIndex, uint lane) {
    sdfGroupMaskSummarized = false;
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
#ifdef SDF_SHADOW_PASS
        if (sdfShadowMotionActive) {
            sdfShadowMovedFlat(lane);
            GroupMemoryBarrierWithGroupSync();
        }
#endif
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
#ifdef SDF_SHADOW_PASS
    if (sdfShadowMotionActive) {
        if (SDF_WORLD_SEGMENT_COUNT(sdfWords[sdfProgramLayout.worldSegmentOffset]) != 0u) {
            sdfShadowMovedFlat(lane);
        } else {
            // The live grid indexes current centers. A departing occluder's previous bound can intersect the
            // receiver cone after its current cell leaves the walk. Scan metadata cooperatively once per group;
            // only moved dynamic bounds reach the cone tests, and no instance field is evaluated here.
            [loop]
            for (uint index = lane; index < packedInstanceCount; index += SDF_GROUP_SHADOW_LANES) {
                sdfShadowMovedCandidate(instanceOffset, index, origin, direction, chord, inverseAperture, inflate);
            }
        }
    }
#endif

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
    sdfSummarizeGroupMask(false, grid.footprintPad, lane);

    return 2u;
}
#endif
#endif

#endif
