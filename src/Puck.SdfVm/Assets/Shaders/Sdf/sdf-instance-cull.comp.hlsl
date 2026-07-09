// Per-tile INSTANCE-CULL pass: one invocation per (tile, viewport), dispatched with the same grid as sdf-beam,
// FIRST in the frame — the MASK-FIRST order: the beam's cone march consumes the mask this pass writes (mapMasked), so
// each march sample walks only the instances overlapping the tile's cone instead of all of them (the measured O(n)
// beam wall — see the beam kernel's header). A compute-to-compute barrier orders this pass before the beam, and the
// beam before cull-args/Stage 1. It bins the program's per-object instances (SdfProgramBuilder.BeginInstance/
// BeginInstanceDynamic) against the tile's cone into the per-tile bitmask — via the packed uniform grid
// (collectInstanceGridMask) when the program carries one, else the flat per-instance loop (collectInstanceMaskWord,
// sdf-world.hlsli).
//
// Running FIRST means no TileEmpty skip (the beam has not marched yet), so EVERY in-viewport tile bins — cheap: a
// sky/lateral-miss tile hits the grid walk's ray∩grid early-out and walks zero slabs, and what the mask saves the
// beam's march dwarfs what sky tiles spend binning.
//
// A SEPARATE kernel, deliberately: fusing the cull into sdf-beam raised that kernel's register high-water mark and
// the occupancy loss taxed the co-resident cone march — the beam's dominant cost — by a measured +12% at 4096
// instances ON BOTH PATHS (grid enabled or not; +22 ms on the sweep's 4096 rung). Splitting keeps the cone-march
// kernel at its lean footprint and gives the cull's divergent cell walk its own occupancy budget; the extra dispatch
// + barrier cost is noise against that. Timing: this pass closes the "mask" mark (SdfWorldEngine.PassLabels).
//
// The tile cone is built from the same inputs the beam uses (a pure function of the viewport row + tile coords), so
// both kernels derive the IDENTICAL cone and no inter-pass cone buffer is needed.
//
// A DYNAMIC instance's bound resolves through the per-frame transform buffer, so this kernel opts into it like the
// beam does.
#define SDF_DYNAMIC_TRANSFORMS
#include "sdf-world.hlsli"

// The per-tile instance mask (binding 7): a FLAT uint buffer, params.instanceMaskWordCount (the host-pushed live
// program width) elements per (viewport, tile) entry. Entry `t`'s mask is words
// [pushedWordCount*t .. pushedWordCount*(t+1)) (word w = instances 32w..32w+31, worldInstanceMaskBase), same
// (viewport, tile) indexing as the cull buffer (worldTileIndex). Written here EXCLUSIVELY (one invocation owns one
// tile's words), read by the beam's cone march (t3) and Stage 1 (t13) — the SAME buffer.
[[vk::binding(7, 0)]] RWStructuredBuffer<uint> instanceMasks : register(u0);

// Accumulates one tile's per-instance mask via the UNIFORM GRID (the default when the program packs one), setting bits
// DIRECTLY in the mask buffer at `maskBase` (the caller pre-zeroes the tile's words). Each tile's words are owned by
// exactly ONE invocation, so the |= read-modify-write is race-free — and it deliberately replaces a per-thread
// accumulation array: a dynamically indexed uint[SDF_MAX_INSTANCES/32] local allocates 512 B of thread scratch for
// EVERY invocation, and the measured occupancy loss dwarfed the few dozen buffer RMWs a tile's passing instances
// cost. Two sources, each setting a bit by the SAME sdfInstancePassesTileCone test the flat loop uses:
//   (1) the ALWAYS-tested list — dynamic and unmaskable instances the frozen grid cannot bin (an unmaskable instance's
//       1e30 bound passes every tile, so it just sets its bit; a parked one is rejected inside the test).
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
void collectInstanceGridMask(SdfInstanceGridHeader grid, uint instanceOffset, uint maskBase, float3 rayOrigin, float3 centerDirection, float chord, float inverseAperture) {
    // (1) The always-tested list.
    [loop]
    for (uint a = 0u; (a < grid.alwaysCount); a++) {
        uint index = sdfWordAt(grid.baseWord + grid.alwaysWord + a);
        float4 bound = sdfInstanceBoundAt(instanceOffset, index);

        if (sdfInstancePassesTileCone(bound, rayOrigin, centerDirection, chord, inverseAperture)) {
            instanceMasks[maskBase + (index >> 5u)] |= (1u << (index & 31u));
        }
    }

    // (2) The cone footprint's grid cells.
    float3 gridMin = grid.origin;
    float3 gridMax = (grid.origin + (float3(grid.dims) * grid.cellSize));
    // The march's far bound: the largest ray depth at which a binned instance can still pass the cull. A hit at t
    // needs the center within (chord*t + pad) of ray(t), so t - proj(center) <= chord*t + pad, and with proj(center)
    // <= proj(farCorner) (the per-axis corner maximizing the projection): t <= (proj + pad) / (1 - chord). The divisor
    // floor only ever GROWS the bound (a degenerate near-1 chord walks farther, never truncates).
    float3 farCorner = float3(
        ((centerDirection.x > 0.0) ? gridMax.x : gridMin.x),
        ((centerDirection.y > 0.0) ? gridMax.y : gridMin.y),
        ((centerDirection.z > 0.0) ? gridMax.z : gridMin.z)
    );
    float projection = max(dot((farCorner - rayOrigin), centerDirection), 0.0);
    float tFar = ((projection + grid.footprintPad) / max((1.0 - chord), 0.01));

    // Clip the march to the ray's overlap with the grid AABB inflated by the LARGEST query pad any slab uses — the
    // robust slabs method (a near-parallel axis constrains nothing unless the origin already lies outside its slab,
    // in which case the whole cone provably misses the grid: the ray never moves on that axis, and the inflation
    // already covers every pad). This is the lateral-miss early-out AND the entry clamp: a sky tile behind the grid,
    // or a corridor tile aimed past it, walks ZERO slabs.
    float inflate = ((chord * tFar) + grid.footprintPad);
    float3 clipMin = (gridMin - inflate);
    float3 clipMax = (gridMax + inflate);
    float tEnter = 0.0;
    float tExit = tFar;

    [unroll]
    for (int axis = 0; (axis < 3); axis++) {
        float direction = centerDirection[axis];
        float origin = rayOrigin[axis];

        if (abs(direction) > 1.0e-8) {
            float tA = ((clipMin[axis] - origin) / direction);
            float tB = ((clipMax[axis] - origin) / direction);

            tEnter = max(tEnter, min(tA, tB));
            tExit = min(tExit, max(tA, tB));
        }
        else if ((origin < clipMin[axis]) || (origin > clipMax[axis])) {
            return;
        }
    }

    if (tEnter > tExit) {
        return; // the cone never reaches the grid
    }

    int3 dimensionsMinusOne = (int3(grid.dims) - int3(1, 1, 1));
    float slabStep = (grid.cellSize * SDF_GRID_SLAB_CELLS);
    float t0 = tEnter;

    [loop]
    for (uint slab = 0u; (slab < SDF_GRID_MAX_SLABS); slab++) {
        // The LAST budget slab force-covers the remaining interval whole: a truncated walk would silently drop the
        // instances past it (a hole-in-the-world bug); one oversized final slab is merely conservative.
        float t1 = (((slab + 1u) < SDF_GRID_MAX_SLABS) ? min((t0 + slabStep), tExit) : tExit);
        float3 c0 = (rayOrigin + (centerDirection * t0));
        float3 c1 = (rayOrigin + (centerDirection * t1));
        float radius = ((chord * t1) + grid.footprintPad);
        float3 low = (min(c0, c1) - radius);
        float3 high = (max(c0, c1) + radius);

        // Skip a slab whose own AABB still misses the raw grid (the clip interval is inflated, so entry/exit slabs can
        // sit outside it). clamp() would otherwise pin an off-grid AABB onto boundary cells and test them spuriously.
        if (all(high >= gridMin) && all(low <= gridMax)) {
            // No integer over-cover ring: the host folded a float-safety epsilon into footprintPad (so `low`/`high`
            // already sit strictly past any floor() rounding disagreement with the host's center binning), and floor is
            // monotone — a ±1-cell ring here multiplied the walked cells by up to 27x for a ~1-ulp boundary event.
            int3 cellLow = clamp(int3(floor((low - grid.origin) * grid.invCellSize)), int3(0, 0, 0), dimensionsMinusOne);
            int3 cellHigh = clamp(int3(floor((high - grid.origin) * grid.invCellSize)), int3(0, 0, 0), dimensionsMinusOne);

            [loop]
            for (int cz = cellLow.z; (cz <= cellHigh.z); cz++) {
                [loop]
                for (int cy = cellLow.y; (cy <= cellHigh.y); cy++) {
                    [loop]
                    for (int cx = cellLow.x; (cx <= cellHigh.x); cx++) {
                        uint cell = ((((uint)cz * grid.dims.y) + (uint)cy) * grid.dims.x) + (uint)cx;
                        uint entryStart = sdfWordAt(grid.baseWord + grid.cellStartWord + cell);
                        uint entryEnd = sdfWordAt(grid.baseWord + grid.cellStartWord + cell + 1u);

                        [loop]
                        for (uint k = entryStart; (k < entryEnd); k++) {
                            uint index = sdfWordAt(grid.baseWord + grid.entryWord + k);
                            float4 bound = sdfInstanceBoundAt(instanceOffset, index);

                            if (sdfInstancePassesTileCone(bound, rayOrigin, centerDirection, chord, inverseAperture)) {
                                instanceMasks[maskBase + (index >> 5u)] |= (1u << (index & 31u));
                            }
                        }
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

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    if (
        (id.z >= params.viewportCount) ||
        (id.x >= params.tileGrid.x) ||
        (id.y >= params.tileGrid.y)
    ) {
        return;
    }

    // A child viewport shows another node's surface — Stage 1 never reads its mask words, so nothing is written
    // (matching the beam's own child early-out, which leaves those words untouched).
    if (isChildViewport(id.z)) {
        return;
    }

    uint tileIndex = worldTileIndex(id.z, id.xy, params.tileGrid);
    uint maskWordCount = params.instanceMaskWordCount;
    uint maskBase = worldInstanceMaskBase(tileIndex);
    ViewportData view = viewports[id.z];
    // The tile cone, built from the same inputs the beam uses — bitwise the same cone (a pure function of the
    // viewport row + tile coords; the TRUNCATED regionSizePx matches Stage 1's rectDims exactly, see the beam).
    float2 regionSizePx = float2((uint2)(view.region.zw * float2(params.imageExtent)));
    float2 tileMinPx = (float2(id.xy) * float(WorldTileSize));

    // Tiles past the viewport's pixel extent hold no rays — the beam leaves them TileEmpty and Stage 1 never reads
    // their mask, so just zero the words (deterministic, total-function content for the never-cleared device buffer).
    if ((tileMinPx.x >= regionSizePx.x) || (tileMinPx.y >= regionSizePx.y)) {
        [loop]
        for (uint word = 0u; (word < maskWordCount); word++) {
            instanceMasks[maskBase + word] = 0u;
        }

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
            instanceMasks[maskBase + word] = 0u;
        }

        collectInstanceGridMask(grid, instanceOffset, maskBase, view.position.xyz, cone.centerDirection, cone.chord, cone.inverseAperture);
    } else {
        // FLAT fallback (a degenerate grid — zero binnable or a single cell — or a grid-suppressed program): the
        // pre-grid path, testing every instance per mask word. Byte-identical to the grid path's mask by construction.
        [loop]
        for (uint word = 0u; (word < maskWordCount); word++) {
            instanceMasks[maskBase + word] = collectInstanceMaskWord(instanceOffset, word, instanceCount, view.position.xyz, cone.centerDirection, cone.chord, cone.inverseAperture);
        }
    }
}
