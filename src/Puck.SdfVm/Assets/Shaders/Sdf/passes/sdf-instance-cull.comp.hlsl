// Per-tile INSTANCE-CULL pass: one invocation per (tile, viewport), dispatched with the same grid as sdf-beam,
// FIRST in the frame — the MASK-FIRST order: the beam's cone march consumes the mask this pass writes (mapMasked), so
// each march sample walks only the instances overlapping the tile's cone instead of all of them (the measured O(n)
// beam wall — see the beam kernel's header). A compute-to-compute barrier orders this pass before the beam, and the
// beam before cull-args/Stage 1. It bins the program's per-object instances (SdfProgramBuilder.BeginInstance/
// BeginInstanceDynamic) against the tile's cone into the per-tile bitmask — via the packed uniform grid
// (collectInstanceGridMask) when the program carries one, else the flat per-instance loop (collectInstanceMaskWord,
// march/sdf-cone.hlsli).
//
// Running FIRST means no TileEmpty skip (the beam has not marched yet), so EVERY in-viewport tile bins — cheap: a
// sky/lateral-miss tile hits the grid walk's ray∩grid early-out and walks zero slabs, and what the mask saves the
// beam's march dwarfs what sky tiles spend binning.
//
// A SEPARATE kernel, deliberately: fusing the cull into sdf-beam raised that kernel's register high-water mark and
// the occupancy loss taxed the co-resident cone march — the beam's dominant cost — by a measured +12% at 4096
// instances ON BOTH PATHS (grid enabled or not; +22 ms on the sweep's 4096 rung). Splitting keeps the cone-march
// kernel at its lean footprint and gives the cull's divergent cell walk its own occupancy budget; the extra dispatch
// + barrier cost is noise against that. Timing: this pass is the graph pass sdf.world$mask.
//
// The tile cone is built from the same inputs the beam uses (a pure function of the view + tile coords), so
// both kernels derive the IDENTICAL cone and no inter-pass cone buffer is needed.
//
// A DYNAMIC instance's bound resolves through the per-frame transform buffer, so this kernel opts into it like the
// beam does.
#define SDF_DYNAMIC_TRANSFORMS
#define SDF_FRAME_INSTANCE_GRID
#include "sdf-world.hlsli"

// The per-tile instance mask, written through sdfInstanceMasksRW: a FLAT uint buffer, passGroup.instanceMaskWordCount
// (the host-written live program width) elements per (viewport, tile) entry. Entry `t`'s mask is words
// [pushedWordCount*t .. pushedWordCount*(t+1)) (word w = instances 32w..32w+31, worldInstanceMaskBase), same
// (viewport, tile) indexing as the cull buffer (worldTileIndex). Written here EXCLUSIVELY (one invocation owns one
// tile's words), read by the beam's cone march and Stage 1 through sdfInstanceMasks — the SAME buffer.

// Writes one bit per non-empty primary mask word directly after the primary run. mapCore consumes this hierarchy to
// jump over sparse 32-instance blocks; the primary bits remain the exact, authoritative candidate set.
void writeInstanceMaskSummary(uint maskBase, uint maskWordCount) {
    uint summaryBase = (maskBase + maskWordCount);
    uint summaryCount = ((maskWordCount + 31u) >> 5u);

    [loop]
    for (uint summary = 0u; (summary < summaryCount); summary++) {
        uint bits = 0u;
        uint firstWord = (summary << 5u);
        uint endWord = min((firstWord + 32u), maskWordCount);

        [loop]
        for (uint word = firstWord; (word < endWord); word++) {
            bits |= ((sdfInstanceMasksRW[maskBase + word] != 0u) ? (1u << (word - firstWord)) : 0u);
        }

        sdfInstanceMasksRW[summaryBase + summary] = bits;
    }
}

// Accumulates one tile's per-instance mask via the UNIFORM GRID (the default when the program packs one), setting bits
// DIRECTLY in the mask buffer at `maskBase` (the caller pre-zeroes the tile's words). Each tile's words are owned by
// exactly ONE invocation, so the |= read-modify-write is race-free — and it deliberately replaces a per-thread
// accumulation array: a dynamically indexed uint[SDF_MAX_INSTANCES/32] local allocates 512 B of thread scratch for
// EVERY invocation, and the measured occupancy loss dwarfed the few dozen buffer RMWs a tile's passing instances
// cost. Two sources, each setting a bit by the SAME sdfInstancePassesTileCone test the flat loop uses:
//   (1) the ALWAYS-tested list — unmaskable and oversized bounds, plus dynamic instances in the frozen program grid.
//       The frame grid bins ordinary dynamic bounds. An unmaskable bound passes every tile; parked bounds are rejected.
//   (2) the grid cells the tile's cone FOOTPRINT overlaps — a conservative swept-cone rasterization CLAMPED to the
//       ray∩grid interval. The march parameter is first clipped against the grid AABB inflated by the largest query
//       pad (chord*tFar + footprintPad) via the robust slabs method — a cone that misses the grid laterally walks ZERO
//       slabs — then marched in SDF_GRID_SLAB_CELLS-cell slabs. Each slab [t0, t1] bounds its cone frustum by a world
//       AABB (the two disk centres ± (chord*t1 + footprintPad)) rasterized to a cell range; every entry in those cells
//       is tested. Instances are binned BY CENTER (one cell each), so footprintPad — the max binned bound radius, host
//       side — is LOAD-BEARING: it is what pulls a neighboring-cell center into the query (see SdfInstanceGrid).
// CONSERVATIVENESS: an instance that passes the flat test touches the bare cone at some point q within its own bound,
// at ray depth t(q) <= tFar (t(1 - chord) <= proj(center) + pad, see tFar below); its center is within footprintPad of
// q, hence inside the slab AABB covering t(q) (and inside grid⊕inflate, so t(q) survives the clip); floor is monotone,
// so the center's HOME cell lies in that slab's cell range and the instance is found. An instance reached from several
// slabs sets its bit more than once — idempotent (OR). So every flat-set bit is set here; and since only cell/always
// members are tested by the identical rule, no extra bit is set: the grid mask equals the flat mask.
void collectInstanceGridMask(SdfInstanceGridHeader grid, uint instanceOffset, uint maskBase, float3 rayOrigin, float3 centerDirection, float chord, float inverseAperture, float radius) {
    SdfGridQuery query = sdfGridCone(rayOrigin, centerDirection, chord, inverseAperture, radius, 1.0e20);
    SdfGridWalk walk = sdfGridWalkBegin(grid, query, 0u, 1u);
    uint index;
    [loop]
    while (sdfGridWalkNext(grid, query, walk, index)) {
        if (sdfGridQueryContains(query, sdfInstanceBoundAt(instanceOffset, index)) &&
            (sdfLightCamera() || !sdfInstanceCameraHidden(instanceOffset, index))) {
            sdfInstanceMasksRW[maskBase + (index >> 5u)] |= (1u << (index & 31u));
        }
    }
}
[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    uint viewIndex = worldViewOf(id.z);

    if (
        (viewIndex >= passGroup.viewportCount) ||
        (id.x >= passGroup.tileGrid.x) ||
        (id.y >= passGroup.tileGrid.y)
    ) {
        return;
    }

    uint tileIndex = worldTileIndex(viewIndex, id.xy, passGroup.tileGrid);
    uint maskWordCount = passGroup.instanceMaskWordCount;
    uint maskBase = worldInstanceMaskBase(tileIndex);
    ViewportData view = worldView();
    // The tile cone, built from the same inputs the beam uses — bitwise the same cone (a pure function of the
    // view + tile coords; regionSizePx is the view's render extent, worldViewDims, as in the beam and Stage 1).
    float2 regionSizePx = float2(worldViewDims(view));
    float2 tileMinPx = (float2(id.xy) * float(WorldTileSize));

    // Tiles past the viewport's pixel extent hold no rays — the beam leaves them TileEmpty and Stage 1 never reads
    // their mask, so just zero the words (deterministic, total-function content for the never-cleared device buffer).
    if ((tileMinPx.x >= regionSizePx.x) || (tileMinPx.y >= regionSizePx.y)) {
        [loop]
        for (uint word = 0u; (word < maskWordCount); word++) {
            sdfInstanceMasksRW[maskBase + word] = 0u;
        }

        writeInstanceMaskSummary(maskBase, maskWordCount);

        return;
    }

    float2 tileMaxPx = min((tileMinPx + float(WorldTileSize)), regionSizePx);
    TileCone cone = buildTileCone(view, (tileMinPx / regionSizePx), (tileMaxPx / regionSizePx));

    // The cull loop's loop-invariant instance-directory resolves, hoisted so each per-instance load skips the offset
    // chain. The grid header sits after the world-segment list, located from the UNCLAMPED packed instance count (the
    // offset chain's own stride); the mask enumeration uses the CLAMPED count.
    uint packedInstanceCount = sdfInstanceCount();
    uint instanceCount = sdfInstanceCountClamped();
    uint instanceOffset = sdfInstanceDirectoryOffset();
    SdfInstanceGridHeader grid = sdfLoadInstanceGridHeader(instanceOffset, packedInstanceCount);

    if (grid.enabled) {
        // GRID path: walk only the cells the tile's cone footprint overlaps plus the always-tested list, so cost
        // tracks instances near the cone, not the total. The tile's words are zeroed, then the walk ORs bits in
        // place — this thread exclusively owns them (see collectInstanceGridMask's no-scratch-array rationale).
        [loop]
        for (uint word = 0u; (word < maskWordCount); word++) {
            sdfInstanceMasksRW[maskBase + word] = 0u;
        }

        collectInstanceGridMask(grid, instanceOffset, maskBase, cone.origin, cone.centerDirection, cone.chord, cone.inverseAperture, cone.radius + passGroup.lightSweepRadius);
    } else {
        // FLAT fallback (a degenerate grid — zero binnable or a single cell — or a grid-suppressed program): the
        // pre-grid path, testing every instance per mask word. Byte-identical to the grid path's mask by construction.
        [loop]
        for (uint word = 0u; (word < maskWordCount); word++) {
            sdfInstanceMasksRW[maskBase + word] = collectInstanceMaskWord(instanceOffset, word, instanceCount, cone.origin, cone.centerDirection, cone.chord, cone.inverseAperture, cone.radius + passGroup.lightSweepRadius);
        }
    }

    writeInstanceMaskSummary(maskBase, maskWordCount);

    // The masks are buffers and the cull walks no field, so its row stays zero.
    puckCountWork(sdfWorkSteps, sdfWorkTexels);
    puckCountShapes(sdfWorkShapes, sdfWorkGradients);
}
