// The mesh pass's occlusion: a two-phase test of each draw's world box against a conservative depth pyramid of the
// view's own first phase, so a draw hidden behind what the view drew is skipped and nothing that shows is (SdfMeshOcclusion
// records it). The pushed index names the step from bit SDF_MESH_LEVEL_SHIFT up and, for the pyramid, the level below it:
//
// - early: one invocation a candidate. A candidate the visible set holds, or one with no host box, is drawn by the first
//   phase's counted indirect draw.
// - depth: one invocation a texel of the level. Level one takes each 2x2 block of the first phase's target, the farthest
//   forward depth of its covered pixels, or positive infinity where any is uncovered (the sky shows there, so nothing
//   behind it is hidden); each level above takes the farthest of its 2x2 children. A level's extent is the one below it
//   halved, rounded up, to 1x1.
// - late: one invocation a candidate. Its box is projected as the mesh vertex stage projects a vertex, and the smallest
//   2x2 texels of a level that cover every pixel center the box can reach, a pixel past it each way, are read: the box is
//   hidden when its nearest corner lies beyond the farthest of them. A box reaching the near plane is never hidden. The
//   visible set takes the verdict, and a visible candidate the first phase did not draw is drawn by the second phase.
//
// Draws are counted on the GPU, into the pass's drawn counts past the work counter rows (GpuKernelCounters.DrawnWords).
// The candidates' header holds their count and the pyramid's levels; each phase writes one GpuDrawIndirectCommand a
// candidate at the candidate's place, empty where it draws nothing, the identity first and the first instance the draw.
// KEEP IN SYNC with SdfMeshOcclusion.
#include "../isa/sdf-mesh.interface.hlsli"
#include "../frame/sdf-viewport.hlsli"
#include "../frame/sdf-mesh.hlsli"

static const uint SdfOcclusionEarly = 0u;
static const uint SdfOcclusionDepth = 1u;
static const uint SdfOcclusionLate = 2u;
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

    return ((texel.y >= 1.0) ? asuint(max(texel.w, 0.0)) : SdfOcclusionUncovered);
}
uint sdfOcclusionTexel(uint2 dims, uint level, uint2 texel) {
    return ((level == 0u) ? sdfOcclusionPixel(texel) : sdfMeshOcclusionDepthRW[sdfOcclusionWord(dims, level, texel)]);
}

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
// host staged its draws, as the box cull alone does: depth ties resolve alike. A candidate the phase does not draw gets
// an empty command. The identity is the draw with the level the host chose for it from bit SDF_MESH_LEVEL_SHIFT up, which
// the vertex stage reads its level's first triangle by.
void sdfOcclusionEmit(uint phase, uint candidate, uint identity, uint vertices, bool emit) {
    uint words;
    uint stride;

    sdfMeshOcclusionDrawsRW.GetDimensions(words, stride);

    uint capacity = (words / (2u * SdfOcclusionCommandWords));

    if (candidate < capacity) {
        uint word = (((phase * capacity) + candidate) * SdfOcclusionCommandWords);

        sdfMeshOcclusionDrawsRW[word] = identity;
        sdfMeshOcclusionDrawsRW[(word + 1u)] = (emit ? vertices : 0u);
        sdfMeshOcclusionDrawsRW[(word + 2u)] = (emit ? 1u : 0u);
        sdfMeshOcclusionDrawsRW[(word + 3u)] = 0u;
        sdfMeshOcclusionDrawsRW[(word + 4u)] = identity;
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

    [unroll]
    for (uint corner = 0u; (corner < 8u); corner++) {
        float3 p = (((center + (((corner & 1u) != 0u) ? halfX : -halfX)) + (((corner & 2u) != 0u) ? halfY : -halfY)) + (((corner & 4u) != 0u) ? halfZ : -halfZ));
        float3 relative = (p - view.position.xyz);
        float w = dot(relative, view.forward.xyz);
        float2 ndc = (float2((scaleX * (dot(relative, view.right.xyz) - (view.lens.y * w))), (scaleY * (dot(relative, view.up.xyz) - (view.lens.z * w)))) / max(w, 1.0e-6));
        float2 at = float2((((ndc.x * 0.5) + 0.5) * (float)dims.x), ((0.5 - (ndc.y * 0.5)) * (float)dims.y));

        crossesNear = (crossesNear || (w <= near));
        nearest = min(nearest, w);
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

[numthreads(64, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint step = (pushedIndex.index >> SDF_MESH_LEVEL_SHIFT);

    if (step == SdfOcclusionDepth) {
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
        }

        // The pyramid counts nothing: the pass's texels stay the fragments its two phases write, which the box cull
        // alone writes too, so the levels compare.
        return;
    }

    uint4 header = sdfMeshOcclusionCandidates[0];
    uint draws = 0u;
    uint vertices = 0u;

    // Each phase draws one command a candidate, empty or not.
    if (id.x == 0u) {
        sdfMeshOcclusionCountsRW[((step == SdfOcclusionLate) ? 1u : 0u)] = header.x;
    }
    if (id.x < header.x) {
        uint4 a = sdfMeshOcclusionCandidates[(1u + (4u * id.x))];
        uint draw = a.x;
        uint4 d = sdfMeshOcclusionCandidates[(4u + (4u * id.x))];
        // A draw with no host box (its flag in the candidate's fifteenth word) is visible, and drawn by the first phase;
        // the sixteenth word is the level the host chose for it.
        bool unboxed = ((d.z & SdfOcclusionUnboxed) != 0u);
        bool was = (unboxed || (sdfMeshOcclusionVisibleRW[draw] != 0u));
        bool emit = was;

        if (step == SdfOcclusionLate) {
            uint4 b = sdfMeshOcclusionCandidates[(2u + (4u * id.x))];
            uint4 c = sdfMeshOcclusionCandidates[(3u + (4u * id.x))];
            bool visible = unboxed || sdfOcclusionMaySee(
                asfloat(uint3(a.zw, b.x)),
                asfloat(b.yzw),
                asfloat(c.xyz),
                asfloat(uint3(c.w, d.xy)),
                header.y
            );

            emit = (visible && !was);
            sdfMeshOcclusionVisibleRW[draw] = (visible ? 1u : 0u);
        }
        sdfOcclusionEmit(((step == SdfOcclusionLate) ? 1u : 0u), id.x, (draw | (d.w << SDF_MESH_LEVEL_SHIFT)), a.y, emit);
        draws = (emit ? 1u : 0u);
        vertices = (emit ? a.y : 0u);
    }

    sdfOcclusionCount(draws, vertices);
}
