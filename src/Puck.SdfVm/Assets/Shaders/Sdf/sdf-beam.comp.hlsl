// Tile-cull prepass: one invocation per (tile, viewport). It cone-marches the distance field over the tile and
// writes a conservative march-start depth — or TileEmpty when no ray in the tile can hit — that Stage 1
// (sdf-world-views.comp) uses to fast-forward, or skip, the per-pixel march.
// Programs admitted to independent part tracing use only a short entry search; others also search gap/tail bounds.
//
// MASK-FIRST: this kernel runs AFTER the instance-cull pass (sdf-instance-cull.comp) and cone-marches the
// TILE-MASKED field (mapMasked at this tile's mask base) — each march sample walks only the instances whose bounds
// overlap the tile's cone, so a march step costs O(instances near this tile) instead of O(all instances). That
// per-sample enumeration WAS the measured O(n) beam wall (measured ~187 ms at 4096 instances, while
// the per-tile binning itself costs ~0.1 ms). Bit-exactness is coneMarchTileBounds' argument (sdf-world.hlsli): a
// masked-out instance's bound excludes every point of the tile's cone, and the bound-sizing contract
// (SdfProgram.PackInstances) makes its compose return the accumulator bit-exactly at any such point — the SAME
// contract Stage 1's masked march already rides, so plane 0/the gap planes are unchanged for contract-honoring
// scenes. The instance cull stays a SEPARATE pass (not fused here) because its cell walk's register footprint taxed
// this kernel's cone-march occupancy by a measured +12% at 4096 instances, grid path or flat.
// Dispatched once per view as (tileGrid.x, tileGrid.y, 1) SINGLE-THREAD workgroups — one warp per tile — after the
// instance-cull pass and before cull-args/Stage 1, each hop separated by a compute-to-compute memory barrier.
// Each (1,1,1) workgroup performs a long serial cone march whose step count and masked
// segment walk differ tile to tile, so a 32-lane warp of 32 DIFFERENT tiles serializes on that divergence (the march
// loop runs to the slowest lane and the per-step op switch replays per divergent path). One tile per warp wastes 31
// lanes the near-idle dispatch wasn't using anyway and removes the divergence entirely — measured on the revealed
// room (RTX 4070, 1280x800): beam 4.45 -> 4.20 ms native, 4.18 -> 3.71 ms at the quarter tier, two paired runs.
// BIT-IDENTICAL: each tile's bounds are still computed by exactly one invocation with unchanged arithmetic — only
// the thread mapping moved, so the tile buffer (and every downstream pass) is byte-for-byte the same.
// The cone march must see moving entities so their tiles aren't culled away — so this kernel opts into the per-frame
// dynamic-transform buffer (sdf-vm.hlsli) that Stage 1 also uses.
#define SDF_DYNAMIC_TRANSFORMS
// The per-tile instance mask, READ here by the cone march (the instance-cull pass wrote it).
#define SDF_INSTANCE_MASKS
// The serial tile-beam walk measured faster with its two payload vectors fetched together before the opcode switch;
// Stage 1's wider per-pixel interpreter benefits from the default case-local loads instead.
#define SDF_VM_EAGER_PAYLOADS
// The brick pool (sdfBrickPool): the cone march must evaluate baked SampledRegion carves so a brick-carved cavity isn't
// masked into a tile that then holes.
#define SDF_SAMPLED_REGIONS
#define SDF_PART_RAY_BOUNDS
// The tile planes and appended per-view part bounds share one device-local buffer. The beam is its only writer, so it
// reads and writes it through tilesRW (worldTiles); cull-args and the hit passes read it through tiles.
#define SDF_TILES_READ_WRITE
#include "sdf-world.hlsli"

[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    if (passGroup.sampleIndex == SDF_ISA_REPORT_REQUEST) {
        tilesRW[0] = asfloat(SDF_ISA_VERSION);
        return;
    }

    uint viewIndex = worldViewOf(id.z);

    if (
        (viewIndex >= passGroup.viewportCount) ||
        (id.x >= passGroup.tileGrid.x) ||
        (id.y >= passGroup.tileGrid.y)
    ) {
        return;
    }

    uint tileIndex = worldTileIndex(viewIndex, id.xy, passGroup.tileGrid);

    ViewportData view = worldViewport(viewIndex);

    // The symmetry-LOD origin: this viewport's camera (the per-sample wallpaper LOD rule measures from it).
    sdfLodOrigin = view.position.xyz;
    // The per-invocation program-layout cache (sdf-vm.hlsli) — this kernel's cone march calls mapMasked once per
    // step, so the decode must happen exactly once here, before the first call below.
    sdfProgramLayout = sdfLoadProgramLayout();

    // The view's render extent (worldViewDims — the same integers Stage 1 and the instance cull read), so tile coverage
    // tracks the pixels the view renders: tiles past it hold no rendered rays and stay TileEmpty. The last tile's
    // localUvMax then reaches the last pixel's uv exactly, which the "provably absent" exactness mapMasked's contract
    // rests on.
    float2 regionSizePx = float2(worldViewDims(view));
    float2 tileMinPx = (float2(id.xy) * float(WorldTileSize));

    // Tiles past the viewport's pixel extent hold no rays — leave them empty. `bounds` carries the classic march-start
    // (bounds.entry, plane 0) plus the four-bound teleport's proven-empty gap (firstExit/secondEntry, planes 1/2);
    // far-distance defaults (the view's authored far plane, sdf-world.hlsli's worldFarDistance) mean "no gap —
    // teleport disabled" for the outside-viewport tiles (whose planes Stage 1 never reads anyway, since it skips a
    // tile with marchStart < 0).
    bool insideViewport = (
        (tileMinPx.x < regionSizePx.x) &&
        (tileMinPx.y < regionSizePx.y)
    );
    float farDistance = worldFarDistance(view);
    TileBounds bounds;
    bounds.entry = TileEmpty;
    bounds.firstExit = farDistance;
    bounds.secondEntry = farDistance;
    bounds.farBound = farDistance;

    if (insideViewport) {
        float2 tileMaxPx = min((tileMinPx + float(WorldTileSize)), regionSizePx);
        TileCone cone = buildTileCone(view, (tileMinPx / regionSizePx), (tileMaxPx / regionSizePx));

        // The per-pixel world footprint (footprint * t = pixel DIAMETER at depth t): the SAME quantity Stage 1 derives
        // as (2 * right.w) / rectDims.y (sdf-world-views.comp), computed here from the identical regionSizePx (rectDims).
        // The F1 far-bound tail proves its clearance against this footprint-inflated threshold — the load-bearing
        // correctness fact (a bare-ConeEpsilon far bound would be anti-conservative wrt the fine march's footprint hits).
        float footprint = ((2.0 * view.right.w) / max(regionSizePx.y, 1.0));

        // March the TILE-MASKED field: the instance-cull pass already wrote this tile's mask (the pass order), so the
        // march enumerates only the instances overlapping this tile's cone — bit-exact per the function's contract note.
        bounds = coneMarchTileBounds(view, cone, worldInstanceMaskBase(tileIndex), footprint);

        // FULL-FIELD SLICE OVERRIDE (debug view mode 7 — see the termination/slice split note in renderView): the
        // slice view must color EVERY pixel of the viewport with the ideal field, so no in-viewport tile may stay
        // TileEmpty in that mode — an empty tile would be dropped by the cull-args bbox, truncating the isolines into
        // 16-px tile staircases around the shape. Forcing a 0.0 march-start keeps the downstream passes on their
        // normal "live tile" path; renderView skips the march
        // for slice anyway, so the forced tiles never pay a wasted march. Every OTHER mode leaves this kernel
        // byte-identical (the override keys exactly on the viewport row's forward.w mode lane).
        if (((int)round(view.forward.w) == DebugViewModeSlice) && (bounds.entry == TileEmpty)) {
            bounds.entry = 0.0;
        }
    }

    tilesRW[worldTileMarchStartIndex(tileIndex)] = bounds.entry;
    // The four-bound teleport's extra planes + the F1 far bound (Stage 1 reads them; cull-args ignores them). Always written so the device-local buffer holds a defined, total-function gap AND far bound for every
    // (viewport, tile) this frame.
    tilesRW[worldTileFirstExitIndex(tileIndex)] = bounds.firstExit;
    tilesRW[worldTileSecondEntryIndex(tileIndex)] = bounds.secondEntry;
    tilesRW[worldTileFarBoundIndex(tileIndex)] = bounds.farBound;

    // Each part is refitted once per viewport, even when it has more instances than screen tiles. This work
    // reads only program/pose/camera data, so it needs no synchronization with other beam invocations.
    if (sdfCanTracePartsIndependently()) {
        uint tileCount = passGroup.tileGrid.x * passGroup.tileGrid.y;
        [loop]
        for (uint instance = id.y * passGroup.tileGrid.x + id.x;
            instance < sdfProgramLayout.instanceCount; instance += tileCount) {
            sdfWritePartBound(viewIndex, instance, view.position.xyz, farDistance,
                (2.0 * view.right.w) / max(regionSizePx.y, 1.0));
        }
    }
}
