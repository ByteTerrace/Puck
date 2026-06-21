// Tile-cull prepass: one invocation per (tile, viewport). It cone-marches the distance field over the tile and
// writes a conservative march-start depth — or TileEmpty when no ray in the tile can hit — that Stage 1
// (sdf-world-views.comp) uses to fast-forward, or skip, the per-pixel march. Generic: it operates on map(), not on
// any specific scene. Dispatched as (tileGrid.x, tileGrid.y, viewportCount) over an 8x8 workgroup, immediately
// before Stage 1 and separated from it by a compute-to-compute memory barrier.
// The cone march runs map(), which must see moving entities so their tiles aren't culled away — so this kernel opts
// into the per-frame dynamic-transform buffer (sdf-vm.hlsli) that Stage 1 also uses.
#define SDF_DYNAMIC_TRANSFORMS
#include "sdf-world.hlsli"

// The per-tile cull buffer (binding 3), written here and read read-only by the compositor.
[[vk::binding(3, 0)]] RWStructuredBuffer<float> tiles : register(u0);

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
    float2 regionSizePx = (view.region.zw * float2(params.imageExtent));
    float2 tileMinPx = (float2(id.xy) * float(WorldTileSize));

    // Tiles past the viewport's pixel extent hold no rays — leave them empty.
    float result = TileEmpty;

    if (
        (tileMinPx.x < regionSizePx.x) &&
        (tileMinPx.y < regionSizePx.y)
    ) {
        float2 tileMaxPx = min((tileMinPx + float(WorldTileSize)), regionSizePx);

        result = coneMarchTile(view, (tileMinPx / regionSizePx), (tileMaxPx / regionSizePx));
    }

    tiles[worldTileIndex(id.z, id.xy, params.tileGrid)] = result;
}
