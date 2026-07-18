// Tile-cull prepass: one invocation per (tile, viewport). It cone-marches the distance field over the tile and
// writes a conservative march-start depth — or TileEmpty when no ray in the tile can hit — that Stage 1
// (sdf-world-views.comp) uses to fast-forward, or skip, the per-pixel march.
//
// MASK-FIRST: this kernel runs AFTER the instance-cull pass (sdf-instance-cull.comp) and cone-marches the
// TILE-MASKED field (mapMasked at this tile's mask base) — each march sample walks only the instances whose bounds
// overlap the tile's cone, so a march step costs O(instances near this tile) instead of O(all instances). That
// per-sample enumeration WAS the measured O(n) beam wall (docs/sdf-bench-notes.md — ~187 ms at 4096 instances, while
// the per-tile binning itself costs ~0.1 ms). Bit-exactness is coneMarchTileBounds' argument (sdf-world.hlsli): a
// masked-out instance's bound excludes every point of the tile's cone, and the bound-sizing contract
// (SdfProgram.PackInstances) makes its compose return the accumulator bit-exactly at any such point — the SAME
// contract Stage 1's masked march already rides, so plane 0/the gap planes are unchanged for contract-honoring
// scenes. The instance cull stays a SEPARATE pass (not fused here) because its cell walk's register footprint taxed
// this kernel's cone-march occupancy by a measured +12% at 4096 instances, grid path or flat.
// Dispatched as (tileGrid.x, tileGrid.y, viewportCount) SINGLE-THREAD workgroups — one warp per tile — after the
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
// The per-tile instance mask, READ here by the cone march (the instance-cull pass wrote it). register(t3): the
// Direct3D 12 SRV order follows the engine's beam binding list (program t0, viewports t1, dynamicTransforms t2,
// instanceMasks t3) — Stage 1 binds the SAME buffer at its own t13 (sdf-vm.hlsli's default register).
#define SDF_INSTANCE_MASKS
// The serial tile-beam walk measured faster with its two payload vectors fetched together before the opcode switch;
// Stage 1's wider per-pixel interpreter benefits from the default case-local loads instead.
#define SDF_VM_EAGER_PAYLOADS
#define SDF_INSTANCE_MASKS_REGISTER t3
// The brick pool (sdf-vm.hlsli's sdfBrickPool at binding 46): the cone march must evaluate baked SampledRegion carves
// so a brick-carved cavity isn't masked into a tile that then holes. register(t4): the Direct3D 12 SRV order follows
// the engine's beam binding list (program t0, viewports t1, dynamicTransforms t2, instanceMasks t3, brickPool t4).
#define SDF_SAMPLED_REGIONS
#define SDF_BRICK_POOL_REGISTER t4
#include "sdf-world.hlsli"

// The per-tile cull buffer (binding 3), written here and read read-only by the compositor.
[[vk::binding(3, 0)]] RWStructuredBuffer<float> tiles : register(u0);

[numthreads(1, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    if (
        (id.z >= params.viewportCount) ||
        (id.x >= params.tileGrid.x) ||
        (id.y >= params.tileGrid.y)
    ) {
        return;
    }

    uint tileIndex = worldTileIndex(id.z, id.xy, params.tileGrid);

    // A child viewport shows another node's surface — there is no SDF camera to cone-march, and Stage 1 never reads
    // this slot's cull entry. Stage 2 DOES read it (its `== TileEmpty` test chooses source-copy vs flat fill), and the
    // tile buffer is device-local and never cleared, so the slot must be DEFINED here: any word that happened to hold
    // TileEmpty's bit pattern would paint the flat background over a live child surface. sdf-cull-args skips child
    // viewports, so this 0.0 is invisible to its surviving-tile bbox scan.
    if (isChildViewport(id.z)) {
        tiles[tileIndex] = 0.0;

        return;
    }

    ViewportData view = viewports[id.z];

    // The symmetry-LOD origin: this viewport's camera (the per-sample wallpaper LOD rule measures from it).
    sdfLodOrigin = view.position.xyz;
    // The per-invocation program-layout cache (sdf-vm.hlsli) — this kernel's cone march calls mapMasked once per
    // step, so the decode must happen exactly once here, before the first call below.
    sdfProgramLayout = sdfLoadProgramLayout();

    // TRUNCATED, matching Stage 1's `rectDims = (uint2)(view.region.zw * imageExtent)` exactly. Dividing by the float
    // product instead puts the last tile's localUvMax BELOW the last pixel's uv whenever the product has a fractional
    // part (an odd window width or height does it immediately): that pixel's ray then lies outside both the cone this
    // kernel cleared and the cone the instance-cull pass culled against, silently breaking the "provably absent"
    // exactness mapMasked's contract rests on. Then reduced by the view's render scale (worldRenderDims — the same
    // integer derivation Stage 1 and the instance cull use), so tile coverage tracks the RENDER extent: tiles past it
    // hold no rendered rays and stay TileEmpty.
    float2 regionSizePx = float2(worldRenderDims((uint2)(view.region.zw * float2(params.imageExtent)), view.renderScale.x));
    float2 tileMinPx = (float2(id.xy) * float(WorldTileSize));

    // Tiles past the viewport's pixel extent hold no rays — leave them empty. `bounds` carries the classic march-start
    // (bounds.entry, plane 0) plus the four-bound teleport's proven-empty gap (firstExit/secondEntry, planes 1/2);
    // MaxDistance defaults mean "no gap — teleport disabled" for the outside-viewport tiles (whose planes Stage 1
    // never reads anyway, since it skips a tile with marchStart < 0).
    bool insideViewport = (
        (tileMinPx.x < regionSizePx.x) &&
        (tileMinPx.y < regionSizePx.y)
    );
    TileBounds bounds;
    bounds.entry = TileEmpty;
    bounds.firstExit = MaxDistance;
    bounds.secondEntry = MaxDistance;
    bounds.farBound = MaxDistance;

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
        // TileEmpty in that mode — an empty tile would be dropped by the cull-args bbox AND flattened by Stage 2's
        // empty-tile test, truncating the isolines into 16-px tile staircases around the shape. Forcing a 0.0
        // march-start keeps both downstream consumers on their normal "live tile" path; renderView skips the march
        // for slice anyway, so the forced tiles never pay a wasted march. Every OTHER mode leaves this kernel
        // byte-identical (the override keys exactly on the viewport row's forward.w mode lane).
        if (((int)round(view.forward.w) == DebugViewModeSlice) && (bounds.entry == TileEmpty)) {
            bounds.entry = 0.0;
        }
    }

    tiles[worldTileMarchStartIndex(tileIndex)] = bounds.entry;
    // The four-bound teleport's extra planes + the F1 far bound (Stage 1 reads them; cull-args + the compositor ignore
    // them). Always written so the device-local buffer holds a defined, total-function gap AND far bound for every
    // (viewport, tile) this frame.
    tiles[worldTileFirstExitIndex(tileIndex)] = bounds.firstExit;
    tiles[worldTileSecondEntryIndex(tileIndex)] = bounds.secondEntry;
    tiles[worldTileFarBoundIndex(tileIndex)] = bounds.farBound;
}
