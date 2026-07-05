// Tile-cull prepass: one invocation per (tile, viewport). It cone-marches the distance field over the tile and
// writes a conservative march-start depth — or TileEmpty when no ray in the tile can hit — that Stage 1
// (sdf-world-views.comp) uses to fast-forward, or skip, the per-pixel march. The SAME invocation also bins the
// program's per-object instances (SdfProgramBuilder.BeginInstance/BeginInstanceDynamic) against the tile's cone into
// a per-tile bitmask (collectInstanceMaskWord, sdf-world.hlsli), one word per 32 instances — Stage 1 marches map()
// for a tile's masked-in instances only, so cost scales with covered pixels and visible instances, not total
// instance count. Generic: it operates on map()/the instance directory, not on any specific scene. Dispatched as
// (tileGrid.x, tileGrid.y, viewportCount) over an 8x8 workgroup, immediately before Stage 1 and separated from it by
// a compute-to-compute memory barrier.
// The cone march runs map(), which must see moving entities so their tiles aren't culled away — so this kernel opts
// into the per-frame dynamic-transform buffer (sdf-vm.hlsli) that Stage 1 also uses (a DYNAMIC instance's bound
// resolves through the same buffer).
#define SDF_DYNAMIC_TRANSFORMS
#include "sdf-world.hlsli"

// The per-tile cull buffer (binding 3), written here and read read-only by the compositor.
[[vk::binding(3, 0)]] RWStructuredBuffer<float> tiles : register(u0);
// The per-tile instance mask (binding 7): a FLAT uint buffer, params.instanceMaskWordCount (the host-pushed live
// program width) elements per (viewport, tile) entry — a plain StructuredBuffer<uint> (4-byte stride) matches the
// existing stride-4 read/readwrite plumbing (WriteStorageBufferReadOnly / WriteStorageBufferReadWrite) other small
// buffers here (the cull buffer, the cull-args bounds) already use, so no new descriptor-write stride was needed.
// Entry `t`'s mask is words [pushedWordCount*t .. pushedWordCount*(t+1)) (word w = instances 32w..32w+31,
// worldInstanceMaskBase), same (viewport, tile) indexing as `tiles` (worldTileIndex). Written here, read read-only
// by Stage 1 (sdf-vm.hlsli's sdfInstanceMasks, the SAME buffer).
[[vk::binding(7, 0)]] RWStructuredBuffer<uint> instanceMasks : register(u1);

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    if (
        (id.z >= params.viewportCount) ||
        (id.x >= params.tileGrid.x) ||
        (id.y >= params.tileGrid.y)
    ) {
        return;
    }

    // A child viewport shows another node's surface — there is no SDF camera to cone-march, and Stage 1 never reads
    // this slot's cull entry, so skip it.
    if (isChildViewport(id.z)) {
        return;
    }

    ViewportData view = viewports[id.z];

    // The symmetry-LOD origin: this viewport's camera (the per-sample wallpaper LOD rule measures from it).
    sdfLodOrigin = view.position.xyz;

    float2 regionSizePx = (view.region.zw * float2(params.imageExtent));
    float2 tileMinPx = (float2(id.xy) * float(WorldTileSize));

    // Tiles past the viewport's pixel extent hold no rays — leave them empty.
    float result = TileEmpty;
    bool insideViewport = (
        (tileMinPx.x < regionSizePx.x) &&
        (tileMinPx.y < regionSizePx.y)
    );
    uint tileIndex = worldTileIndex(id.z, id.xy, params.tileGrid);
    // The host-pushed mask width is the LIVE uploaded program's derived per-tile word count; Stage 1 uses the same base.
    uint maskWordCount = params.instanceMaskWordCount;
    uint maskBase = worldInstanceMaskBase(tileIndex);

    if (insideViewport) {
        float2 tileMaxPx = min((tileMinPx + float(WorldTileSize)), regionSizePx);
        float2 localUvMin = (tileMinPx / regionSizePx);
        float2 localUvMax = (tileMaxPx / regionSizePx);
        TileCone cone = buildTileCone(view, localUvMin, localUvMax);

        result = coneMarchTile(view, cone);

        // The same tile cone drives both the march-start cull and the instance cull. A tile the cone already cleared
        // (TileEmpty) still gets an instance mask, since Stage 1 is skipped for
        // it regardless and an all-zero mask is a safe (if moot) default.
        // The cull loop's loop-invariant instance-directory resolve, hoisted here so each sdfInstanceBoundAt load
        // skips the offset chain.
        uint instanceCount = sdfInstanceCountClamped();
        uint instanceOffset = sdfInstanceDirectoryOffset();

        [loop]
        for (uint word = 0u; (word < maskWordCount); word++) {
            instanceMasks[maskBase + word] = collectInstanceMaskWord(instanceOffset, word, instanceCount, view.position.xyz, cone.centerDirection, cone.chord);
        }
    } else {
        // A tile outside the viewport still owns mask-buffer words; zero-fill them (Stage 1 never reads them — the
        // all-zero mask is the uniform safe default).
        [loop]
        for (uint word = 0u; (word < maskWordCount); word++) {
            instanceMasks[maskBase + word] = 0u;
        }
    }

    tiles[tileIndex] = result;
}
