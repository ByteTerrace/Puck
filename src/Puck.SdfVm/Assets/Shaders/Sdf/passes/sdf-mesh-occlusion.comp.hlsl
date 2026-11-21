// The mesh pass's occlusion: a two-phase test of each draw's world box against a conservative depth pyramid of the
// view's own first phase, so a draw hidden behind what the view drew is skipped and nothing that shows is (SdfMeshOcclusion
// records it). The pushed index names the step from bit SDF_MESH_LEVEL_SHIFT up and, for the pyramid, the level below it:
//
// - early: one invocation a candidate. A candidate the visible set holds, or one with no host box, is drawn by the first
//   phase's counted indirect draw.
// - depth: one invocation a texel of the level. Level one takes each 2x2 block of the first phase's target, the farthest
//   forward depth of its covered pixels, or positive infinity where any is uncovered (the sky shows there, so nothing
//   behind it is hidden); each level above takes the farthest of its 2x2 children. A level's extent is the one below it
//   halved, rounded up, to 1x1. A pass that tests portal sight also builds the nearest chain the same way.
// - late: one invocation a candidate. Its box is projected as the mesh vertex stage projects a vertex, and the smallest
//   2x2 texels of a level that cover every pixel center the box can reach, a pixel past it each way, are read: the box is
//   hidden when its nearest corner lies beyond the farthest of them. A box reaching the near plane is never hidden. The
//   visible set takes the verdict, and a visible candidate the first phase did not draw is drawn by the second phase.
// - count, then compact: one invocation a candidate, after the early step (its phases) or the late step (the second
//   phase), flag one below the shift naming the late one. Each group counts the commands a phase emitted in its slots;
//   then each group copies those commands, in slot order, to the phase's compact list after the commands of the groups
//   before it, and the last group writes the phase's draw count. The counted indirect draw executes the compact list,
//   so a phase costs the commands it draws, not one for each candidate (a counted draw that pushes each command's
//   identity, as Direct3D 12's does, pays for every command it reads, empty or not).
//
// Draws are counted on the GPU, into the pass's drawn counts past the work counter rows (GpuKernelCounters.DrawnWords).
// On a marked frame (flag 8 of the header's fourth word) each draw a phase emits a command for ORs its level plus one
// into its two bits of the counts' mark words from SDF_MESH_MARK_BASE, sixteen draws a word, which the host reads back
// (SdfMeshOcclusion, SdfMeshSelectionMarks).
// The controls' header holds the draw count and pyramid levels, and the compaction row the slots a phase holds and where
// the compact lists and group counts start; each phase writes one GpuDrawIndirectCommand a candidate at the candidate's
// slot, empty where it draws nothing, with the identity first and first instance zero.
// KEEP IN SYNC with SdfMeshOcclusion. Bounds, levels and mesh identity come from the retained mesh region.
#include "../isa/sdf-mesh.interface.hlsli"
#include "../frame/sdf-viewport.hlsli"
#include "../frame/sdf-mesh.hlsli"
#include "../frame/sdf-mesh-selection.hlsli"
#include "../frame/sdf-shadow-optical.hlsli"

static const uint SdfOcclusionUncovered = 0x7F800000u;
static const uint SdfOcclusionCommandWords = 5u;
static const uint SdfOcclusionDrawnWords = 4u;
static const uint SdfOcclusionUnboxed = 1u;

uint2 sdfOcclusionExtent(uint2 dims, uint level) {
    return max(((dims + ((1u << level) - 1u)) >> level), uint2(1u, 1u));
}
// The first word of a level's texel: the levels from one up, each row-major.
uint sdfOcclusionWord(uint2 dims, uint level, uint2 texel) {
    uint base = 0u;

    [loop]
    for (uint below = 1u; (below < level); below++) {
        uint2 extent = sdfOcclusionExtent(dims, below);

        base += (extent.x * extent.y);
    }

    return (base + ((texel.y * sdfOcclusionExtent(dims, level).x) + texel.x));
}
// A pixel's forward depth as the pyramid orders it: positive floats' bits order as the floats do.
uint sdfOcclusionPixel(uint2 pixel) {
    float4 texel = meshVisibility.Load(int3(int2(pixel), 0));
    if ((sdfMeshOcclusionCandidates[0].z & 2u) != 0u) {
        // Orthographic reversed depth: zero is uncovered, otherwise 1-z orders near to far.
        return texel.x > 0.0 ? asuint(1.0 - texel.x) : SdfOcclusionUncovered;
    }
    return ((texel.y >= 1.0) ? asuint(max(texel.w, 0.0)) : SdfOcclusionUncovered);
}
uint sdfOcclusionTexel(uint2 dims, uint level, uint2 texel) {
    return ((level == 0u) ? sdfOcclusionPixel(texel) : sdfMeshOcclusionDepthRW[sdfOcclusionWord(dims, level, texel)]);
}
// Whether the pyramid carries its nearest chain beside the farthest (flag 128 of the header's fourth word, set where the
// pass tests portal sight): the same levels, each texel the nearest covered depth of its block, after every farthest level.
bool sdfOcclusionNearChain() {
    return ((sdfMeshOcclusionCandidates[0].w & 128u) != 0u);
}
// A level's texel in the nearest chain, which starts past the farthest chain's last level.
uint sdfOcclusionNearWord(uint2 dims, uint level, uint2 texel) {
    return (sdfOcclusionWord(dims, (sdfMeshOcclusionCandidates[0].y + 1u), uint2(0u, 0u)) + sdfOcclusionWord(dims, level, texel));
}
uint sdfOcclusionNearTexel(uint2 dims, uint level, uint2 texel) {
    return ((level == 0u) ? sdfOcclusionPixel(texel) : sdfMeshOcclusionDepthRW[sdfOcclusionNearWord(dims, level, texel)]);
}

#include "../frame/sdf-caster-receivers.hlsli"

// Adds a phase's commands, and their vertices, to the pass's drawn counts: the wave sums both, its first lane adds each.
void sdfOcclusionCount(uint draws, uint vertices) {
    uint waveDraws = WaveActiveSum(draws);
    uint waveVertices = WaveActiveSum(vertices);

    if (WaveIsFirstLane()) {
        uint words;
        uint stride;

        workCounters.GetDimensions(words, stride);

        uint rows = (words / (PuckWorkRowWords + SdfOcclusionDrawnWords));
        uint word = ((rows * PuckWorkRowWords) + (passGroup.workCounterRow * SdfOcclusionDrawnWords));

        puckAddWork(word, waveDraws);
        puckAddWork((word + 2u), waveVertices);
    }
}
// Writes a candidate's command into a phase's list at the candidate's own record, so each phase draws in the order the
// host staged its draws. Coplanar ties across phases can depend on the prior visible set. A candidate the phase does not draw gets
// an empty command. The identity is the draw with the level the selection kernel chose for it from bit SDF_MESH_LEVEL_SHIFT up, which
// the vertex stage reads its level's first triangle by.
void sdfOcclusionEmit(uint phase, uint candidate, uint identity, uint vertices, bool emit) {
    uint capacity = sdfMeshOcclusionCandidates[SDF_COMPACT_ROW].x;

    if (candidate < capacity) {
        uint word = (((phase * capacity) + candidate) * SdfOcclusionCommandWords);

        sdfMeshOcclusionDrawsRW[word] = identity;
        sdfMeshOcclusionDrawsRW[(word + 1u)] = (emit ? vertices : 0u);
        sdfMeshOcclusionDrawsRW[(word + 2u)] = (emit ? 1u : 0u);
        sdfMeshOcclusionDrawsRW[(word + 3u)] = 0u;
        sdfMeshOcclusionDrawsRW[(word + 4u)] = 0u;
    }
}

groupshared uint sdfCompactFlags[SDF_MESH_SELECTION_GROUP_SIZE];
groupshared uint sdfCompactBase;

// Counts (scatter false) or compacts (scatter true) one phase's commands for the group holding a candidate; every thread
// of the group calls it, since it synchronizes the group.
void sdfOcclusionCompactPhase(uint candidate, uint local, uint phase, bool scatter) {
    uint4 row = sdfMeshOcclusionCandidates[SDF_COMPACT_ROW];
    uint count = sdfMeshOcclusionCandidates[0].x;
    uint group = (candidate / SDF_MESH_SELECTION_GROUP_SIZE);
    uint slot = (((phase * row.x) + candidate) * SdfOcclusionCommandWords);
    bool emitted = (candidate < count) && (sdfMeshOcclusionDrawsRW[(slot + 1u)] != 0u) && (sdfMeshOcclusionDrawsRW[(slot + 2u)] != 0u);

    sdfCompactFlags[local] = (emitted ? 1u : 0u);
    if (local == 0u) { sdfCompactBase = 0u; }
    GroupMemoryBarrierWithGroupSync();

    uint rank = 0u;

    [loop]
    for (uint lane = 0u; (lane < local); lane++) {
        rank += sdfCompactFlags[lane];
    }
    if (!scatter) {
        if (local == (SDF_MESH_SELECTION_GROUP_SIZE - 1u)) {
            sdfMeshOcclusionDrawsRW[((row.z + (phase * row.w)) + group)] = (rank + sdfCompactFlags[local]);
        }
        GroupMemoryBarrierWithGroupSync();
        return;
    }

    uint before = 0u;

    [loop]
    for (uint earlier = local; (earlier < group); earlier += SDF_MESH_SELECTION_GROUP_SIZE) {
        before += sdfMeshOcclusionDrawsRW[((row.z + (phase * row.w)) + earlier)];
    }
    InterlockedAdd(sdfCompactBase, before);
    GroupMemoryBarrierWithGroupSync();

    uint place = (sdfCompactBase + rank);

    if (emitted) {
        uint word = (row.y + (((phase * row.x) + place) * SdfOcclusionCommandWords));

        [unroll]
        for (uint part = 0u; (part < SdfOcclusionCommandWords); part++) {
            sdfMeshOcclusionDrawsRW[(word + part)] = sdfMeshOcclusionDrawsRW[(slot + part)];
        }
    }
    if ((local == (SDF_MESH_SELECTION_GROUP_SIZE - 1u)) && (group == ((count - 1u) / SDF_MESH_SELECTION_GROUP_SIZE))) {
        sdfMeshOcclusionCountsRW[phase] = (place + sdfCompactFlags[local]);
    }
    GroupMemoryBarrierWithGroupSync();
}
// Counts or compacts every phase the step before it emitted: the second entry phase after the late step, with a
// cascade's late back-face-culled entry; the first after the early step, with a cascade's solid exit, thin exit, optical
// (with glass extinction) and early back-face-culled entry phases.
void sdfOcclusionCompact(uint candidate, uint local, bool scatter, bool late) {
    uint4 header = sdfMeshOcclusionCandidates[0];
    bool cascade = ((header.z & 2u) != 0u);
    uint entry = (late ? 1u : 0u);

    sdfOcclusionCompactPhase(candidate, local, entry, scatter);
    if (cascade) {
        sdfOcclusionCompactPhase(candidate, local, (SDF_SHADOW_CULLED_ENTRY_PHASE + entry), scatter);
        if (!late) {
            sdfOcclusionCompactPhase(candidate, local, 2u, scatter);
            sdfOcclusionCompactPhase(candidate, local, 3u, scatter);
            if ((header.w & 64u) != 0u) { sdfOcclusionCompactPhase(candidate, local, 4u, scatter); }
        }
    }
}

// Marks a draw a phase selected for submission: its level plus one in its two bits of the counts' mark words. Every
// phase of a draw draws the level the early step chose, so two phases' marks OR to that level's.
void sdfOcclusionMark(uint draw, uint level) {
    uint words;
    uint stride;

    sdfMeshOcclusionCountsRW.GetDimensions(words, stride);

    uint word = (SDF_MESH_MARK_BASE + (draw >> 4u));

    if (word < words) {
        InterlockedOr(sdfMeshOcclusionCountsRW[word], (min((level + 1u), 3u) << ((draw & 15u) * 2u)));
    }
}

// Whether a box may show past the first phase's depth.
bool sdfOcclusionMaySee(float3 center, float3 halfX, float3 halfY, float3 halfZ, uint levels) {
    ViewportData view = worldView();
    uint2 dims = worldViewDims(view);
    float near = worldSurfaceNearDistance(view);
    float scaleX = (1.0 / (view.up.w * view.right.w));
    float scaleY = (1.0 / view.right.w);
    float2 low = float2(1.0e30, 1.0e30);
    float2 high = float2(-1.0e30, -1.0e30);
    float nearest = 1.0e30;
    bool crossesNear = false;
    bool cascade = (sdfMeshOcclusionCandidates[0].z & 2u) != 0u;

    [unroll]
    for (uint corner = 0u; (corner < 8u); corner++) {
        float3 p = (((center + (((corner & 1u) != 0u) ? halfX : -halfX)) + (((corner & 2u) != 0u) ? halfY : -halfY)) + (((corner & 4u) != 0u) ? halfZ : -halfZ));
        float3 relative = (p - view.position.xyz);
        float w = dot(relative, view.forward.xyz);
        float2 ndc = (float2((scaleX * (dot(relative, view.right.xyz) - (view.lens.y * w))), (scaleY * (dot(relative, view.up.xyz) - (view.lens.z * w)))) / (cascade ? 1.0 : max(w, 1.0e-6)));
        float2 at = float2((((ndc.x * 0.5) + 0.5) * (float)dims.x), ((0.5 - (ndc.y * 0.5)) * (float)dims.y));

        crossesNear = (crossesNear || (w <= (cascade ? worldNearDistance(view) : near)));
        nearest = min(nearest, cascade ? (w - worldNearDistance(view)) / (worldFarDistance(view) - worldNearDistance(view)) : w);
        low = min(low, at);
        high = max(high, at);
    }
    if (crossesNear) {
        return true;
    }

    // The pixels whose centers the box can cover, one more each way.
    float2 limit = (float2(dims) + 2.0);
    int2 first = (int2(ceil(clamp((low - 0.5), -2.0, limit))) - 1);
    int2 last = (int2(floor(clamp((high - 0.5), -2.0, limit))) + 1);

    if (any(last < 0) || any(first >= int2(dims))) {
        return false;
    }

    uint2 x0y0 = uint2(max(first, int2(0, 0)));
    uint2 x1y1 = uint2(min(last, (int2(dims) - 1)));
    uint level = 1u;

    [loop]
    while ((level < levels) && ((((x1y1.x >> level) - (x0y0.x >> level)) > 1u) || (((x1y1.y >> level) - (x0y0.y >> level)) > 1u))) {
        level++;
    }

    uint2 a = (x0y0 >> level);
    uint2 b = (x1y1 >> level);
    uint farthest = max(
        max(sdfOcclusionTexel(dims, level, a), sdfOcclusionTexel(dims, level, uint2(b.x, a.y))),
        max(sdfOcclusionTexel(dims, level, uint2(a.x, b.y)), sdfOcclusionTexel(dims, level, b))
    );

    return (farthest == SdfOcclusionUncovered) || ((((asfloat(farthest) * 1.0001) + 1.0e-3)) >= nearest);
}

// The pyramid's farthest and nearest depths over every pixel a perspective view's box, grown by a cube of half side `grow`
// along the view's axes, can reach, a pixel past it each way; false where that reaches the near plane or past the image,
// whose depths the pyramid does not hold.
bool sdfOcclusionRegion(float3 center, float3 halfX, float3 halfY, float3 halfZ, float grow, uint levels, out uint farthest, out uint nearest) {
    ViewportData view = worldView();
    uint2 dims = worldViewDims(view);
    float near = worldSurfaceNearDistance(view);
    float scaleX = (1.0 / (view.up.w * view.right.w));
    float scaleY = (1.0 / view.right.w);
    float2 low = float2(1.0e30, 1.0e30);
    float2 high = float2(-1.0e30, -1.0e30);

    farthest = SdfOcclusionUncovered;
    nearest = 0u;
    [loop]
    for (uint corner = 0u; (corner < 64u); corner++) {
        float3 p = (((center + (((corner & 1u) != 0u) ? halfX : -halfX)) + (((corner & 2u) != 0u) ? halfY : -halfY)) + (((corner & 4u) != 0u) ? halfZ : -halfZ));
        p += (grow * (((((corner & 8u) != 0u) ? view.right.xyz : -view.right.xyz) + (((corner & 16u) != 0u) ? view.up.xyz : -view.up.xyz)) + (((corner & 32u) != 0u) ? view.forward.xyz : -view.forward.xyz)));
        float3 relative = (p - view.position.xyz);
        float w = dot(relative, view.forward.xyz);

        if (w <= near) {
            return false;
        }
        float2 ndc = (float2((scaleX * (dot(relative, view.right.xyz) - (view.lens.y * w))), (scaleY * (dot(relative, view.up.xyz) - (view.lens.z * w)))) / w);
        float2 at = float2((((ndc.x * 0.5) + 0.5) * (float)dims.x), ((0.5 - (ndc.y * 0.5)) * (float)dims.y));

        low = min(low, at);
        high = max(high, at);
    }

    float2 limit = (float2(dims) + 2.0);
    int2 first = (int2(ceil(clamp((low - 0.5), -2.0, limit))) - 1);
    int2 last = (int2(floor(clamp((high - 0.5), -2.0, limit))) + 1);

    if (any(first < 0) || any(last >= int2(dims))) {
        return false;
    }

    uint2 x0y0 = uint2(first);
    uint2 x1y1 = uint2(last);
    uint level = 1u;

    [loop]
    while ((level < levels) && ((((x1y1.x >> level) - (x0y0.x >> level)) > 1u) || (((x1y1.y >> level) - (x0y0.y >> level)) > 1u))) {
        level++;
    }

    uint2 a = (x0y0 >> level);
    uint2 b = (x1y1 >> level);

    farthest = max(
        max(sdfOcclusionTexel(dims, level, a), sdfOcclusionTexel(dims, level, uint2(b.x, a.y))),
        max(sdfOcclusionTexel(dims, level, uint2(a.x, b.y)), sdfOcclusionTexel(dims, level, b))
    );
    nearest = min(
        min(sdfOcclusionNearTexel(dims, level, a), sdfOcclusionNearTexel(dims, level, uint2(b.x, a.y))),
        min(sdfOcclusionNearTexel(dims, level, uint2(a.x, b.y)), sdfOcclusionNearTexel(dims, level, b))
    );
    return true;
}

// Whether a box stays hidden from every eye within `reach` of this view's, whatever way it faces (a turn alone hides
// nothing new). The pyramid holds the occluders this eye saw; an eye moved by at most the reach sees the box's points
// along rays that, at distance r from this eye, lie within asin(reach / r) of this eye's ray to the same point. So where
// every pixel of the box's directions, widened by that angle at the nearest occluder depth d, holds an occluder nearer
// than the box (farthest below the box's nearest depth), and d exceeds the reach, every such ray crosses the occluders'
// depth surface before the box. The widening is taken as a cube of half side reach x (the box's farthest distance) / d
// about each point, and repeated until the region's nearest depth holds the d it was widened by. The surface is taken as
// closed across its depth steps: a hole in a far occluder that a nearer one hides from this eye is assumed absent, which
// is where the guarantee stops.
bool sdfOcclusionHiddenWithin(float3 center, float3 halfX, float3 halfY, float3 halfZ, float reach, uint levels) {
    ViewportData view = worldView();
    float nearest = 1.0e30;
    float distance = 0.0;

    [unroll]
    for (uint corner = 0u; (corner < 8u); corner++) {
        float3 p = (((center + (((corner & 1u) != 0u) ? halfX : -halfX)) + (((corner & 2u) != 0u) ? halfY : -halfY)) + (((corner & 4u) != 0u) ? halfZ : -halfZ));
        float3 relative = (p - view.position.xyz);

        nearest = min(nearest, dot(relative, view.forward.xyz));
        distance = max(distance, length(relative));
    }

    float covered = 0.0;
    float grow = 0.0;

    [loop]
    for (uint attempt = 0u; (attempt < 3u); attempt++) {
        uint farthest;
        uint closest;

        if (!sdfOcclusionRegion(center, halfX, halfY, halfZ, grow, levels, farthest, closest) || (farthest == SdfOcclusionUncovered) ||
            (((asfloat(farthest) * 1.0001) + 1.0e-3) >= nearest)) {
            return false;
        }

        float least = asfloat(closest);

        if (least <= reach) {
            return false;
        }
        if ((attempt > 0u) && (least >= covered)) {
            return true;
        }
        covered = least;
        grow = ((distance * reach) / least);
    }
    return false;
}

groupshared uint sdfOpticalDirtyTiles;

// The raster marks the bitmap before touching any sums. Between rasterizations each bitmap word has one
// workgroup owner, which consumes its bits and clears every texel covered by those bits.
void sdfClearOpticalTiles(uint side, uint across, uint group, uint lane) {
    uint bitmap = sdfShadowOpticalBitmap(side) + group;
    if (lane == 0u) {
        sdfOpticalDirtyTiles = sdfGlassSumsRW[bitmap];
        if (sdfOpticalDirtyTiles != 0u) {
            sdfGlassSumsRW[bitmap] = 0u;
            puckCountDetail(2u, countbits(sdfOpticalDirtyTiles), 0u, 0u, 0u, 0u);
        }
    }
    GroupMemoryBarrierWithGroupSync();
    uint dirty = sdfOpticalDirtyTiles;
    while (dirty != 0u) {
        uint tile = group * 32u + firstbitlow(dirty);
        dirty &= dirty - 1u;
        uint2 origin = uint2(tile % across, tile / across) * SDF_SHADOW_OPTICAL_TILE_EDGE;
        for (uint local = lane; local < SDF_SHADOW_OPTICAL_TILE_EDGE * SDF_SHADOW_OPTICAL_TILE_EDGE;
            local += SDF_MESH_SELECTION_GROUP_SIZE) {
            uint2 pixel = origin + uint2(local % SDF_SHADOW_OPTICAL_TILE_EDGE, local / SDF_SHADOW_OPTICAL_TILE_EDGE);
            if (all(pixel < side)) {
                uint word = sdfShadowOpticalWord(side, pixel);
                [unroll] for (uint part = 0u; part < SDF_SHADOW_OPTICAL_TEXEL_WORDS; part++) {
                    sdfGlassSumsRW[word + part] = 0u;
                }
            }
        }
    }
}

[numthreads(SDF_MESH_SELECTION_GROUP_SIZE, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID, uint lane : SV_GroupIndex, uint3 group : SV_GroupID) {
    id.x += id.y * (SDF_MESH_SELECTION_GROUP_SIZE * SDF_MESH_SELECTION_GROUPS_PER_ROW);
    uint step = (pushedIndex.index >> SDF_MESH_LEVEL_SHIFT);

    if (step == SDF_SHADOW_OPTICAL_CLEAR_STEP) {
        uint side = pushedIndex.index & SdfMeshDrawMask;
        uint across = sdfShadowOpticalTilesAcross(side);
        uint groupIndex = group.x + SDF_MESH_SELECTION_GROUPS_PER_ROW * group.y;
        if (groupIndex >= (across * across + 31u) / 32u) { return; }
        sdfClearOpticalTiles(side, across, groupIndex, lane);
        return;
    }
    if ((step == SDF_OCCLUSION_COUNT_STEP) || (step == SDF_OCCLUSION_COMPACT_STEP)) {
        sdfOcclusionCompact(id.x, lane, (step == SDF_OCCLUSION_COMPACT_STEP), ((pushedIndex.index & SdfMeshDrawMask) != 0u));
        return;
    }

    // One workgroup tests the consumer's portal slabs with exactly the draw test and the same pyramid.
    // Four rows per screen follow the selection controls. Sight counts follow the draw phase counts.
    if (step == SDF_OCCLUSION_SIGHT_STEP) {
        uint rows;
        uint stride;
        sdfMeshOcclusionCandidates.GetDimensions(rows, stride);
        uint screens = (rows - SDF_SIGHT_ROW) / 4u;
        if (id.x < screens) {
            uint row = SDF_SIGHT_ROW + id.x * 4u;
            float4 center = asfloat(sdfMeshOcclusionCandidates[row]);
            float4 halfX = asfloat(sdfMeshOcclusionCandidates[row + 1u]);
            float3 halfY = asfloat(sdfMeshOcclusionCandidates[row + 2u].xyz);
            float3 halfZ = asfloat(sdfMeshOcclusionCandidates[row + 3u].xyz);
            uint levels = sdfMeshOcclusionCandidates[0].y;
            // Bit one: a slab this eye may see. Bit two: a slab hidden from every eye within the reach the host asked for
            // in the half axis's fourth lane (SdfPortalSight).
            bool visible = center.w == 0.0 || sdfOcclusionMaySee(center.xyz, halfX.xyz, halfY, halfZ, levels);
            bool within = !visible && (halfX.w > 0.0) && sdfOcclusionNearChain() && sdfOcclusionHiddenWithin(center.xyz, halfX.xyz, halfY, halfZ, halfX.w, levels);
            sdfMeshOcclusionCountsRW[SDF_SIGHT_COUNT_BASE + id.x] = (visible ? 1u : 0u) | (within ? 2u : 0u);
        }
        return;
    }

    if (step == SDF_RECEIVER_STEP) { sdfReceiverMark(id.x); return; }
    if (step == SDF_RECEIVER_REDUCE_STEP) { sdfReceiverReduce(id.x, pushedIndex.index & SdfMeshDrawMask); return; }

    if (step == SDF_OCCLUSION_DEPTH_STEP) {
        ViewportData view = worldView();
        uint2 dims = worldViewDims(view);
        uint level = (pushedIndex.index & SdfMeshDrawMask);
        uint2 extent = sdfOcclusionExtent(dims, level);
        uint2 below = ((level == 1u) ? dims : sdfOcclusionExtent(dims, (level - 1u)));
        uint2 texel = uint2((id.x % extent.x), (id.x / extent.x));

        if (id.x < (extent.x * extent.y)) {
            uint2 child = (texel << 1u);
            uint2 other = min((child + 1u), (below - 1u));
            uint farthest = max(
                max(sdfOcclusionTexel(dims, (level - 1u), child), sdfOcclusionTexel(dims, (level - 1u), uint2(other.x, child.y))),
                max(sdfOcclusionTexel(dims, (level - 1u), uint2(child.x, other.y)), sdfOcclusionTexel(dims, (level - 1u), other))
            );

            sdfMeshOcclusionDepthRW[sdfOcclusionWord(dims, level, texel)] = farthest;
            if (sdfOcclusionNearChain()) {
                uint nearest = min(
                    min(sdfOcclusionNearTexel(dims, (level - 1u), child), sdfOcclusionNearTexel(dims, (level - 1u), uint2(other.x, child.y))),
                    min(sdfOcclusionNearTexel(dims, (level - 1u), uint2(child.x, other.y)), sdfOcclusionNearTexel(dims, (level - 1u), other))
                );

                sdfMeshOcclusionDepthRW[sdfOcclusionNearWord(dims, level, texel)] = nearest;
            }
        }

        // The pyramid counts nothing: the pass's texels stay the fragments its two phases write, which the box cull
        // alone writes too, so the levels compare.
        return;
    }

    uint4 header = sdfMeshOcclusionCandidates[0];
    bool splitExit = (header.z & 2u) != 0u;
    bool optical = (header.w & 64u) != 0u;
    uint draws = 0u;
    uint vertices = 0u;
    uint exitDraws = 0u;
    uint exitVertices = 0u;
    uint receiverRejected = 0u;
    uint entryRejected = 0u;
    bool culledEntry = false;
    uint entryIdentity = 0u;
    uint entryPhase = (step == SDF_OCCLUSION_LATE_STEP) ? 1u : 0u;
    bool marking = (header.w & 8u) != 0u;
    // A pass that draws static casters alone (the distant shadow map, a far cascade's static layer, SdfMeshOcclusion.Stage)
    // keeps no dynamic draw, boxed or not: what it holds stands until a static draw changes. One that draws dynamic casters
    // alone (the far cascade over a copy of its static layer) keeps no static opaque draw, but keeps every glass draw,
    // whose extinction it draws whole. A draw a filter leaves out keeps the visibility history it had: it writes no state
    // and emits only empty commands, so the other filter's staging of the same draws keeps its two-phase history.
    bool staticOnly = (header.w & 16u) != 0u;
    bool dynamicOnly = (header.w & 32u) != 0u;
    // A share of a draw list split over several frames (the distant map's back map, SdfMeshOcclusion.Stage) keeps the
    // draws whose index leaves its remainder, so the shares draw each draw exactly once.
    uint slices = ((header.w >> 8u) & 0xFFu) + 1u;
    uint slice = ((header.w >> 16u) & 0xFFu);

    if (id.x < header.x) {
        uint draw = id.x;
        uint record = sdfMeshRecord(draw);
        uint4 a = sdfSelectionRow(record, 0u);
        bool absent = (a.w & SDF_MESH_ABSENT_FLAG) != 0u;
        bool dynamicDraw = (a.w & SDF_MESH_DYNAMIC_FLAG) != 0u;
        bool filtered = (staticOnly && dynamicDraw) || (dynamicOnly && !dynamicDraw && ((a.w & 2u) == 0u));
        if (absent || filtered) {
            if (absent) { sdfMeshOcclusionVisibleRW[draw] = uint4(0u, 0u, 0u, 0u); }
            sdfOcclusionEmit(entryPhase, draw, draw, 0u, false);
            if (splitExit) { sdfOcclusionEmit((SDF_SHADOW_CULLED_ENTRY_PHASE + entryPhase), draw, draw, 0u, false); }
            if (splitExit && step == SDF_OCCLUSION_EARLY_STEP) {
                sdfOcclusionEmit(2u, draw, draw, 0u, false);
                sdfOcclusionEmit(3u, draw, draw, 0u, false);
                if (optical) { sdfOcclusionEmit(4u, draw, draw, 0u, false); }
            }
        } else {
        uint4 b = sdfSelectionRow(record, 1u);
        uint4 c = sdfSelectionRow(record, 2u);
        uint4 d = sdfSelectionRow(record, 3u);
        bool unboxed = (a.w & 1u) != 0u;
        float3 boxCenter, boxX, boxY, boxZ;
        sdfSelectionBox(record, a, b, c, d, boxCenter, boxX, boxY, boxZ);
        uint4 state = sdfMeshOcclusionVisibleRW[draw];
        // Visibility and detail belong to this observed surface incarnation, including a rejected occupant.
        if (state.z != c.w) { state = uint4(0u, 0u, c.w, 0u); }
        bool occlude = (header.z & 1u) != 0u;
        bool kept = state.w != 0u;
        // A culled draw can retain another mesh's level in history; its current table may have fewer levels.
        uint level = min(state.y, d.w);
        if (step == SDF_OCCLUSION_EARLY_STEP) {
            kept = ((draw % slices) == slice) &&
                (splitExit || (((a.w >> 1u) & 1u) == (header.w & 1u))) && (unboxed || sdfSelectionKeeps(boxCenter, boxX, boxY, boxZ));
            if (kept && splitExit && occlude && !unboxed) {
                kept = sdfReceiverMayShadow(boxCenter, boxX, boxY, boxZ);
                receiverRejected = !kept && (a.w & 2u) == 0u ? 1u : 0u;
            }
            if (kept) {
                level = sdfSelectionLevel(record, state, unboxed, boxCenter, boxX, boxY, boxZ, asfloat(b.w), c.w, d.w);
                state.y = level;
            }
            state.w = kept ? 1u : 0u;
        }
        bool was = unboxed || state.x != 0u;
        bool emit = kept && (!occlude || was);
        if (step == SDF_OCCLUSION_LATE_STEP) {
            bool visible = kept && (unboxed || sdfOcclusionMaySee(boxCenter, boxX, boxY, boxZ, header.y));
            entryRejected = splitExit && kept && !was && !visible && (a.w & 2u) == 0u ? 1u : 0u;
            emit = visible && !was;
            state.x = visible ? 1u : 0u;
        }
        sdfMeshOcclusionVisibleRW[draw] = state;
        uint count = sdfSelectionVertices(record, level);
        bool glass = (a.w & 2u) != 0u;
        emit = emit && !(splitExit && glass && !optical);
        bool opaqueEmit = emit && !(splitExit && glass);
        // A cascade drawing one layer of a split refresh (SdfMeshOcclusion.Stage) emits and counts that layer's commands
        // alone: the depth's entry and glass phases, or the exit phases.
        bool depthLayer = (header.w & 2u) == 0u;
        bool exitLayer = (header.w & 4u) == 0u;
        bool encloses = (a.w & 4u) != 0u;
        entryIdentity = draw | (level << SDF_MESH_LEVEL_SHIFT);
        if (splitExit && opaqueEmit && depthLayer && encloses && !unboxed &&
            (sdfMeshRecordWord(record + SDF_MESH_FLAGS_WORD) & SDF_MESH_SKIN_FLAG) == 0u &&
            sdfMeshDisplacementEntry(record) == SDF_FRAME_SAMPLED_NONE &&
            asfloat(sdfMeshRecordWord(record + SDF_MESH_CARRY_SCALE_WORD)) > 0.0) {
            uint pose = sdfMeshRecordWord(record + SDF_MESH_FRAME_SLOT_WORD);
            if (sdfViewPoseSlot(pose) == pose) {
                ViewportData light = worldView();
                float3 forward = light.forward.xyz;
                float3 relative = boxCenter - light.position.xyz;
                float radius = sdfSelectionRadius(forward, boxX, boxY, boxZ);
                float depth = dot(relative, forward);
                // A near-plane cut can expose a solid's back faces. Leave those draws in the two-sided list.
                float margin = 1.0e-5 * (length(relative) + radius + abs(worldNearDistance(light)) + 1.0);
                culledEntry = (depth - radius > worldNearDistance(light) + margin);
            }
        }
        sdfOcclusionEmit(entryPhase, id.x, entryIdentity, count, opaqueEmit && depthLayer && !culledEntry);
        // A closed solid wholly past the light's near plane draws in the back-face-culled entry list instead, at the same
        // slot, so its phase compacts in the same staged order.
        if (splitExit) { sdfOcclusionEmit((SDF_SHADOW_CULLED_ENTRY_PHASE + entryPhase), id.x, entryIdentity, count, culledEntry); }
        if (splitExit && step == SDF_OCCLUSION_EARLY_STEP) {
            // The final exit/peel and glass lists depend ONLY on receiver/box selection.
            // Entry visibility (was/visible) must never remove a potential second surface.
            bool exitEmit = kept && !glass && exitLayer;
            bool glassEmit = optical && kept && glass && depthLayer;
            sdfOcclusionEmit(2u, id.x, entryIdentity, count, exitEmit && encloses);
            sdfOcclusionEmit(3u, id.x, entryIdentity, count, exitEmit && !encloses);
            if (optical) { sdfOcclusionEmit(4u, id.x, entryIdentity, count, glassEmit); }
            exitDraws = exitEmit ? 1u : 0u;
            exitVertices = exitEmit ? count : 0u;
            // Optical draws execute once, independently of the two entry phases.
            draws += glassEmit ? 1u : 0u;
            vertices += glassEmit ? count : 0u;
            if (marking && (exitEmit || glassEmit)) { sdfOcclusionMark(draw, level); }
        }
        emit = opaqueEmit && depthLayer;
        draws += emit ? 1u : 0u;
        vertices += emit ? count : 0u;
        if (marking && emit) { sdfOcclusionMark(draw, level); }
        }
    }

    if (splitExit) {
        uint selected = WaveActiveCountBits(culledEntry);

        if (WaveIsFirstLane() && (selected != 0u)) { puckCountDetail(3u, selected, 0u, 0u, 0u, 0u); }
    }
    sdfOcclusionCount(draws, vertices);
    uint receiverSum = WaveActiveSum(receiverRejected);
    uint entrySum = WaveActiveSum(entryRejected);
    if (WaveIsFirstLane()) {
        puckCountDetail(0u, receiverSum, 0u, 0u, 0u, 0u);
        puckCountDetail(1u, entrySum, 0u, 0u, 0u, 0u);
    }
    if (splitExit && step == SDF_OCCLUSION_EARLY_STEP) {
        // Count entry and exit separately: 64 * (3 * MaxTriangles) fits a uint wave sum, twice that does not.
        // Each kept opaque draw emits exactly one exit command; glass emits once. puckAddWork carries into 64 bits.
        sdfOcclusionCount(exitDraws, exitVertices);
    }
}
