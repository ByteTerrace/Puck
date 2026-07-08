// Stage 2 of the two-stage SDF world compositor: the SOURCE-AGNOSTIC compositor. It places each viewport's source
// texture — an SDF view rendered by Stage 1 OR a child node's output, bound uniformly into the same array slot —
// into its screen region by a 1:1 copy (the source is rect-sized, so no scaling is needed). VIEWPORTS as data: the
// regions drive the layout; the compositor neither knows nor cares what produced each source. One invocation per
// output pixel over an 8x8 workgroup.
#include "sdf-tile.hlsli"

struct CompositeParams2 {
    uint2 imageExtent;     // output image size in pixels
    uint viewportCount;
    uint tileGridPacked;   // (tileGrid.y << 16) | tileGrid.x — the cull buffer's per-viewport stride, for flattening
    float4 rects[5];       // per viewport: xy = normalized origin, zw = normalized size (of the output image)
};
[[vk::push_constant]] ConstantBuffer<CompositeParams2> params;

[[vk::binding(0, 0)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> Output : register(u0);
// The per-view source textures (binding 1, an array): SDF views from Stage 1, or child surfaces, indexed by viewport.
// The format is declared so the read is a formatted OpImageRead (no shaderStorageImageReadWithoutFormat dependency).
[[vk::binding(1, 0)]] [[vk::image_format("rgba8")]] RWTexture2D<float4> sources[5] : register(u1);
// The beam cull buffer (binding 3, read-only): for an EMPTY SDF tile (the GPU cull skipped its Stage-1 dispatch, so
// its source pixel is stale) the compositor writes a flat constant instead of the source. WorldTileSize / TileEmpty /
// worldTileIndex come from sdf-tile.hlsli — this kernel owns its own push-constant layout, so it cannot include
// sdf-world.hlsli, but the tile vocabulary is shared rather than hand-copied.
[[vk::binding(3, 0)]] StructuredBuffer<float> tiles : register(t0);

static const float3 EmptyTileColor = float3(0.07, 0.09, 0.135); // the GPU-cull flat background (replaces per-ray sky)

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID) {
    if ((id.x >= params.imageExtent.x) || (id.y >= params.imageExtent.y)) {
        return;
    }

    float2 uv = ((float2(id.xy) + 0.5) / float2(params.imageExtent));
    float3 color = float3(0.015, 0.016, 0.02); // letterbox outside every viewport region
    uint2 tileGrid = uint2((params.tileGridPacked & 0xFFFFu), (params.tileGridPacked >> 16));

    // First region that contains the pixel wins (split-screen regions are disjoint, so order is irrelevant).
    for (uint v = 0u; (v < params.viewportCount); v++) {
        float4 r = params.rects[v];

        if (
            (uv.x >= r.x) && (uv.y >= r.y) &&
            (uv.x < (r.x + r.z)) && (uv.y < (r.y + r.w))
        ) {
            uint2 originPx = (uint2)(r.xy * float2(params.imageExtent));
            uint2 localPixel = (id.xy - originPx);
            uint2 tileCoord = (localPixel / WorldTileSize);

            // EXACTLY TileEmpty => this SDF tile was culled (its source is stale/undispatched) => flat constant. A
            // surviving SDF tile (>= 0) OR a child viewport's slot (which sdf-beam.comp writes 0.0 into, precisely so
            // this test never reads undefined device memory) copies the source — so child surfaces pass through
            // untouched without needing the child mask here.
            color = (tiles[worldTileIndex(v, tileCoord, tileGrid)] == TileEmpty)
                ? EmptyTileColor
                : sources[v][localPixel].rgb;
            break;
        }
    }

    Output[id.xy] = float4(color, 1.0);
}
